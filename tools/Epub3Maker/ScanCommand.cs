using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Epub3Maker;

/// <summary>
/// <c>epub3maker scan</c>: goes through a library, finds every ebook with its narration, and makes
/// the EPUB 3s one book at a time — meant to run unattended, on the server the library lives on.
///
/// It is built around being stopped. A book takes hours, a slow server's night may not be enough
/// for one, and a container or a scheduled task is stopped whenever something else decides so;
/// every book therefore carries on from where it was, and what was done is remembered in the data
/// folder. One book at a time, because a small server has no cores to spare for a second, and at
/// low priority, so whatever else it serves — the audiobooks themselves — comes first.
/// </summary>
public static class ScanCommand
{
    public static bool IsCommand(string arg) =>
        arg.Equals("scan", StringComparison.OrdinalIgnoreCase) || arg.Equals("skeniraj", StringComparison.OrdinalIgnoreCase);

    private sealed record Options(
        string Library,
        string Output,
        Quality Quality,
        int? AnchorSeconds,
        string? Model,
        string? Language,
        int Threads,
        TimeWindow? Window,
        TimeSpan? Watch,
        bool Retry,
        bool DryRun,
        bool LowPriority);

    public static async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var positional = new List<string>();
        var command = Settings.FromCommandLine(args, positional);

        if (command.IsOn("help"))
        {
            Help.PrintScan();
            return 0;
        }

        if (positional.Count > 1) throw new ArgumentException("Zadaj jednu mapu s knjigama.");
        if (positional.Count == 1) command.Set("library", positional[0]);

        // The data folder decides where the default settings file is, so it is settled first.
        var environment = Settings.FromEnvironment();
        if ((command["data"] ?? environment["data"]) is { Length: > 0 } data) Paths.Data = Path.GetFullPath(data);

        var configPath = command["config"] ?? environment["config"] ?? Path.Combine(Paths.Data, "epub3maker.conf");
        var fromFile = File.Exists(configPath) ? Settings.ReadFile(configPath) : new Settings();
        if (command["config"] is { } asked && !File.Exists(asked)) throw new FileNotFoundException($"Ne postoji: {asked}");

        var settings = fromFile.With(environment).With(command);

        if (settings["data"] is { Length: > 0 } dataAgain) Paths.Data = Path.GetFullPath(dataAgain);
        if (settings["ffmpeg"] is { Length: > 0 } ffmpeg) FfmpegDecoder.Executable = ffmpeg;

        var options = Parse(settings);

        using var log = new LogFile(Paths.Log);
        var output = new LoggedOutput(log);

        if (options.DryRun) return List(options);

        // One scan at a time: a scheduled task that fires while last night's is still running
        // would otherwise start the same book twice.
        using var instance = TryLock();
        if (instance is null)
        {
            output.Line("Već radi drugi epub3maker s istom mapom podataka; izlazim.");
            return 0;
        }

        if (options.LowPriority) LowerPriority();

        output.Line($"epub3maker scan: {options.Library} → {options.Output}");
        output.Line($"  kvaliteta {Qualities.Name(options.Quality)}" +
                    (options.AnchorSeconds is { } a ? $", sidra svakih {a} s" : "") +
                    $", {(options.Threads > 0 ? options.Threads : Environment.ProcessorCount)} niti" +
                    (options.Window is { } w ? $", radi {w}" : "") +
                    (options.Watch is { } t ? $", provjerava svakih {t.TotalMinutes:0} min" : ", jedan prolaz") +
                    $", podaci u {Paths.Data}");

        while (!ct.IsCancellationRequested)
        {
            if (options.Window is { } window && !window.Contains(DateTime.Now))
            {
                if (options.Watch is null)
                {
                    output.Line($"Izvan prozora {window}; izlazim.");
                    return 0;
                }

                var wait = window.UntilOpen(DateTime.Now);
                output.Line($"Izvan prozora {window}; čekam {BookJob.Duration((long)wait.TotalMilliseconds)}.");
                await Task.Delay(wait, ct);
                continue;
            }

            using var shift = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (options.Window is { } open) shift.CancelAfter(open.Remaining(DateTime.Now));

            var finishedAll = await PassAsync(options, output, log, shift.Token);

            if (ct.IsCancellationRequested) break;

            if (!finishedAll && shift.IsCancellationRequested)
                output.Line("Prozor je zatvoren; knjiga u radu nastavlja se sljedeći put.");

            if (options.Watch is not { } every) break;

            output.Line($"Sljedeća provjera za {every.TotalMinutes:0} min.");
            await Task.Delay(every, ct);
        }

        return 0;
    }

    /// <summary>
    /// One pass over the library. True when it got through everything, false when it was stopped
    /// part way.
    /// </summary>
    private static async Task<bool> PassAsync(Options options, IJobOutput output, LogFile log, CancellationToken ct)
    {
        var state = ScanState.Load();
        var books = BookFinder.Find(options.Library, options.Output, [Paths.Data]);

        var queue = new List<(FoundBook Book, JobSpec Spec)>();

        foreach (var book in books)
        {
            if (Plan(book, options, state) is { } spec) queue.Add((book, spec));
        }

        state.Save();

        if (queue.Count == 0)
        {
            output.Line($"Pregledano {books.Count} knjiga, nema novog posla.");
            return true;
        }

        // A book already started first: its hours are not wasted by starting another.
        queue = [.. queue.OrderByDescending(q => state.Books.TryGetValue(q.Book.Key, out var r) && r.Status == BookStatus.Working)];

        output.Line($"Pregledano {books.Count} knjiga, za napraviti {queue.Count}.");

        foreach (var (book, spec) in queue)
        {
            if (ct.IsCancellationRequested) return false;

            var record = state.Books[book.Key];
            var clock = Stopwatch.StartNew();

            record.Status = BookStatus.Working;
            record.Started ??= DateTime.Now;
            record.Attempts++;
            record.Error = null;
            state.Save();

            output.Line("");
            output.Line($"== {book.Key}  ({Qualities.Name(spec.Quality)})");

            string? joined = null;

            try
            {
                var audio = book.Audio[0];

                if (book.Audio.Count > 1)
                {
                    joined = Path.Combine(Paths.Work, "joined", JoinName(book) + Path.GetExtension(book.Audio[0]).ToLowerInvariant());

                    if (!File.Exists(joined))
                    {
                        output.Line($"Spajam {book.Audio.Count} audio datoteka u jednu…");
                        await FfmpegDecoder.JoinAsync(book.Audio, joined, ct);
                    }

                    audio = joined;
                }

                var result = await BookJob.RunAsync(spec with { Audio = audio }, output, log, ct);

                record.Status = BookStatus.Done;
                record.Finished = DateTime.Now;
                record.Output = spec.Output;
                record.Sentences = result.Written.Sentences;
                record.TimedSentences = result.Written.TimedSentences;

                if (joined is not null) File.Delete(joined);
            }
            catch (OperationCanceledException)
            {
                // Stopped, not failed: carries on next time. The joined audio is kept for then.
                record.Attempts--;
                return false;
            }
            catch (Exception ex)
            {
                record.Status = BookStatus.Failed;
                record.Error = ex.Message;
                output.Line("  GREŠKA: " + ex.Message);
                log.Write(ex.ToString());
            }
            finally
            {
                record.Hours += clock.Elapsed.TotalHours;
                state.Save();
            }
        }

        return true;
    }

    /// <summary>
    /// Whether a book needs making, and how. Records what it decided in the state, so a book that
    /// cannot be made is reported once rather than attempted every night.
    /// </summary>
    private static JobSpec? Plan(FoundBook book, Options options, ScanState state)
    {
        if (book.Settings.IsOn("skip")) return null;

        var quality = book.Settings["quality"] is { } q ? Qualities.Parse(q) ?? options.Quality : options.Quality;
        var anchors = book.Settings["anchors"] is { } a ? Qualities.ParseAnchors(a) ?? options.AnchorSeconds : options.AnchorSeconds;
        var model = book.Settings["model"] ?? options.Model;
        var language = book.Settings["language"] ?? options.Language;

        var recipe = quality == Quality.Best
            ? "best"
            : $"{Qualities.Name(quality)}, {model ?? Qualities.DefaultModel(quality)}, {Qualities.Settings(quality, anchors).ProbeIntervalMs / 1000} s";

        state.Books.TryGetValue(book.Key, out var record);

        if (book.Problem is not null)
        {
            state.Books[book.Key] = new BookRecord { Status = BookStatus.Failed, Fingerprint = book.Fingerprint, Recipe = recipe, Error = book.Problem };
            return null;
        }

        // A different quality remakes a book still in progress, and a finished one only when the
        // book's own settings ask for it: changing the default must not queue up every book already
        // made, at hours each.
        var ownRecipe = book.Settings.Has("quality") || book.Settings.Has("anchors") || book.Settings.Has("model");
        var changed = record is null
                      || record.Fingerprint != book.Fingerprint
                      || (record.Recipe != recipe && (record.Status != BookStatus.Done || ownRecipe));

        if (!changed)
        {
            switch (record!.Status)
            {
                case BookStatus.Done:
                    return null;
                case BookStatus.Failed when !options.Retry:
                    return null;
            }
        }
        else if (record is null && File.Exists(book.Output))
        {
            // Made before the state was kept, or the state was deleted: not worth another night.
            state.Books[book.Key] = new BookRecord
            {
                Status = BookStatus.Done, Fingerprint = book.Fingerprint, Recipe = recipe, Output = book.Output,
                Finished = File.GetLastWriteTime(book.Output), Error = "već postojao",
            };
            return null;
        }

        if (changed)
            state.Books[book.Key] = new BookRecord { Status = BookStatus.Waiting, Fingerprint = book.Fingerprint, Recipe = recipe };

        return new JobSpec(book.Ebook!, book.Audio[0], book.Output, quality, model, anchors, Language: language, Threads: options.Threads);
    }

    /// <summary>
    /// <c>--dry-run</c>: what was found and what would happen to it, decided exactly as a real pass
    /// would decide it, touching nothing.
    /// </summary>
    private static int List(Options options)
    {
        var state = ScanState.Load();
        var books = BookFinder.Find(options.Library, options.Output, [Paths.Data]);

        foreach (var book in books)
        {
            var before = state.Books.GetValueOrDefault(book.Key)?.Status;
            var spec = Plan(book, options, state);
            var record = state.Books.GetValueOrDefault(book.Key);

            var status = (spec, record) switch
            {
                _ when book.Problem is { } problem => "PROBLEM: " + problem,
                _ when book.Settings.IsOn("skip") => "preskače se (.epub3maker)",
                (null, { Status: BookStatus.Done, Error: "već postojao" }) => "gotovo (izlaz već postoji)",
                (null, { Status: BookStatus.Done }) => "gotovo",
                (null, { Status: BookStatus.Failed }) => "greška: " + record!.Error + " (--retry za ponovni pokušaj)",
                (not null, _) when before == BookStatus.Working => $"nastavlja se ({Qualities.Name(spec.Quality)})",
                (not null, _) => $"napravit će se ({Qualities.Name(spec.Quality)})",
                _ => "?",
            };

            Console.WriteLine(book.Key);
            Console.WriteLine($"    {status}");
            Console.WriteLine($"    audio: {book.Audio.Count} datotek{(book.Audio.Count == 1 ? "a" : "e")}" +
                              (book.Audio.Count > 0 ? $" ({Path.GetFileName(book.Audio[0])}{(book.Audio.Count > 1 ? ", …" : "")})" : ""));
            Console.WriteLine($"    → {book.Output}");
        }

        Console.WriteLine();
        Console.WriteLine($"{books.Count} knjiga pronađeno.");
        return 0;
    }

    private static Options Parse(Settings settings)
    {
        // --data is the tool's own folder, and taking it for the library is the likeliest mistake.
        const string Usage =
            "
  epub3maker scan <mapa s audioknjigama> --output <mapa za EPUB3> [--data <mapa za modele i napredak>]" +
            "
  ili 'library = …' i 'output = …' u epub3maker.conf u --data mapi.";

        var library = settings["library"] ?? throw new ArgumentException("Nije zadana mapa s knjigama (prvi argument iza 'scan')." + Usage);
        var output = settings["output"] ?? throw new ArgumentException("Nije zadano kamo idu gotove knjige (--output)." + Usage);

        if (Path.GetFullPath(output).TrimEnd('\', '/').Equals(Paths.Data.TrimEnd('\', '/'), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("--output i --data moraju biti različite mape." + Usage);

        if (!Directory.Exists(library)) throw new DirectoryNotFoundException($"Ne postoji mapa: {library}");

        var quality = settings["quality"] is { } q
            ? Qualities.Parse(q) ?? throw new ArgumentException($"Nepoznata kvaliteta '{q}'. Moguće: light, better, best.")
            : Quality.Better;

        int? anchors = settings["anchors"] is { } a
            ? Qualities.ParseAnchors(a) ?? throw new ArgumentException($"Sidra: sparse, medium, dense ili broj sekundi 5–600, ne '{a}'.")
            : null;

        if (settings["model"] is { } model && !Models.Names.Contains(model))
            throw new ArgumentException($"Nepoznat model '{model}'. Mogući: {string.Join(", ", Models.Names)}.");

        var threads = settings["threads"] is { } t
            ? int.TryParse(t, out var n) && n > 0 ? n : throw new ArgumentException($"Niti: pozitivan broj, ne '{t}'.")
            : 0;

        // "off" lifts a window set in the settings file, for starting a run by hand in the day.
        TimeWindow? window = settings["window"] is { Length: > 0 } w && w.Trim().ToLowerInvariant() is not ("off" or "none" or "always" or "uvijek")
            ? TimeWindow.Parse(w) ?? throw new ArgumentException($"Prozor: npr. 01:00-07:00, ne '{w}'.")
            : null;

        TimeSpan? watch = settings["watch"] is { Length: > 0 } every
            ? Durations.Parse(every) ?? throw new ArgumentException($"Gledaj: npr. 60m ili 2h, ne '{every}'.")
            : null;

        var priority = settings["priority"]?.Trim().ToLowerInvariant();
        if (priority is not (null or "low" or "nisko" or "normal" or "normalno"))
            throw new ArgumentException($"Prioritet: low ili normal, ne '{priority}'.");

        return new Options(
            Path.GetFullPath(library), Path.GetFullPath(output), quality, anchors, settings["model"], settings["language"],
            threads, window, watch, settings.IsOn("retry"), settings.IsOn("dry-run"), priority is null or "low" or "nisko");
    }

    /// <summary>A file held open for as long as the scan runs; a second scan cannot open it.</summary>
    private static FileStream? TryLock()
    {
        Directory.CreateDirectory(Paths.Data);

        try
        {
            return new FileStream(Path.Combine(Paths.Data, "scan.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                1, FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Below everything else on the machine. FFmpeg and the model's threads inherit it, so the
    /// audiobook server keeps its processor whenever it wants it.
    /// </summary>
    private static void LowerPriority()
    {
        try
        {
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or PlatformNotSupportedException or InvalidOperationException)
        {
            // Not allowed here; the work still gets done, only less politely.
        }
    }

    private static string JoinName(FoundBook book) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(book.Key + "|" + book.Fingerprint)))[..16];
}
