using System.Diagnostics;
using AudioBookReader.Core.Alignment;
using AudioBookReader.Core.Audio;
using AudioBookReader.Core.Books;
using AudioBookReader.Core.Data;
using AudioBookReader.Core.Models;
using Epub3Maker;

// epub3maker <knjiga.epub> <audioknjiga.m4b> [opcije]
//
// Aligns the book on this PC with the app's own aligner and writes an EPUB 3 whose Media Overlays
// let any reading system that supports them follow the narration sentence by sentence.

Console.OutputEncoding = System.Text.Encoding.UTF8;

var options = Options.Parse(args);
if (options is null)
{
    Options.PrintUsage();
    return 1;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // The map is saved as alignment goes, so stopping here loses at most the last few minutes;
    // running the same command again carries on.
    e.Cancel = true;
    cancellation.Cancel();
    Console.WriteLine();
    Console.WriteLine("Zaustavljam… (napredak je spremljen, isto pokretanje nastavlja gdje je stalo)");
};

var ct = cancellation.Token;

try
{
    return await RunAsync(options, ct);
}
catch (OperationCanceledException)
{
    return 2;
}
catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or InvalidOperationException or NotSupportedException)
{
    Console.WriteLine();
    Console.Error.WriteLine("Greška: " + ex.Message);
    return 1;
}

static async Task<int> RunAsync(Options options, CancellationToken ct)
{
    var work = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Epub3Maker", "work");
    Directory.CreateDirectory(work);

    var logPath = Path.Combine(work, "log.txt");
    await using var log = new StreamWriter(logPath, append: true) { AutoFlush = true };
    void Log(string line) => log.WriteLine($"{DateTime.Now:HH:mm:ss} {line}");

    Log($"---- {options.Ebook} + {options.Audio}");

    // ---- The two halves ----

    Console.WriteLine($"E-knjiga:    {Path.GetFileName(options.Ebook)}");
    Console.WriteLine($"Audioknjiga: {Path.GetFileName(options.Audio)}");

    var extractors = new BookTextExtractors();
    var extracted = await extractors.ExtractAsync(options.Ebook, ct);
    var plainText = extracted.Text.PlainText;

    if (plainText.Length == 0) throw new InvalidDataException("U e-knjizi nema teksta.");

    var language = options.Language ?? TextLanguage.Detect(plainText);

    Console.Write("Čitam audio… ");
    var audio = await AudioBookProbe.ProbeAsync(options.Audio, ct);
    Console.WriteLine($"{Duration(audio.DurationMs)}, {audio.Chapters.Count} poglavlja");

    if (audio.DurationMs <= 0) throw new InvalidDataException("Audio datoteci se ne može pročitati trajanje.");

    Dictionary<int, SentenceTime> times;

    if (options.Map is null && !options.Whisper)
    {
        // ---- Forced alignment, letter by letter ----

        if (!FfmpegDecoder.IsAvailable())
            throw new InvalidOperationException("FFmpeg nije pronađen. Instaliraj ga (winget install Gyan.FFmpeg) ili zadaj --ffmpeg <putanja>.");

        using var model = await MmsModel.LoadAsync(ct);

        Console.WriteLine($"Slušam knjigu (MMS, {Environment.ProcessorCount} niti)…");
        var clock = Stopwatch.StartNew();

        var emissions = await Task.Run(() => model.Emissions(options.Audio, audio.DurationMs, done =>
        {
            var speed = clock.Elapsed.TotalSeconds > 0 ? done * audio.DurationMs / 1000.0 / clock.Elapsed.TotalSeconds : 0;
            var left = done > 0.001 ? TimeSpan.FromSeconds(clock.Elapsed.TotalSeconds / done * (1 - done)) : TimeSpan.Zero;
            Console.Write($"\r  {done:P1}  {speed:0.0}x  još ~{Duration((long)left.TotalMilliseconds)}      ");
        }, ct), ct);

        Console.WriteLine();
        Console.WriteLine($"Preslušano za {Duration((long)clock.Elapsed.TotalMilliseconds)}. Poravnavam slovo po slovo…");

        var (ctcTimes, ctcReport) = CtcAligner.Align(extracted.Text, emissions, Log);
        times = ctcTimes;

        Console.WriteLine($"  {ctcReport.Anchors} sidara, {ctcReport.Segments} odsječaka poravnato" +
                          (ctcReport.SkippedSegments > 0 ? $", {ctcReport.SkippedSegments} preskočeno (tekst koji se ne čita ili ne odgovara)" : ""));

        return Finish(options, extracted, times, audio.DurationMs, Log);
    }

    var audioHash = await ContentHash.ComputeAsync(options.Audio, ct);
    var ebookHash = await ContentHash.ComputeAsync(options.Ebook, ct);

    // ---- The same library the phone keeps, in miniature ----
    //
    // One entry per pair of files, found again by their hashes, so running the command a second
    // time — after Ctrl+C, a crash, a reboot — carries on from the last saved chapter.

    var database = new LibraryDatabase(Path.Combine(work, "library.db3"));
    var syncMaps = new SyncMapStore(Path.Combine(work, "maps"));
    var library = new LibraryService(database, syncMaps);

    var book = (await database.GetBooksAsync()).FirstOrDefault(b => b.AudioHash == audioHash && b.EbookHash == ebookHash);

    if (book is null)
    {
        book = await library.CreateFromAudioAsync(new AudioAttachment(
            options.Audio, audioHash, audio.DurationMs, audio.Chapters, audio.Title, audio.Author));

        book = await library.AttachTextAsync(book.Id, new TextAttachment(
            options.Ebook, ebookHash, plainText.Length, extracted.Chapters,
            extracted.Text.Title, extracted.Text.Author, language));
    }
    else
    {
        // The same files, perhaps moved since the last run.
        book.AudioPath = options.Audio;
        book.EbookPath = options.Ebook;
        await database.UpdateBookAsync(book);
    }

    // ---- The alignment ----

    SyncMap? map;

    if (options.Map is { } mapFile)
    {
        // A map made on the phone: nothing to listen to here.
        Directory.CreateDirectory(Path.Combine(work, "maps"));
        File.Copy(mapFile, Path.Combine(work, "maps", $"book-{book.Id}.sync.json"), overwrite: true);

        map = await syncMaps.LoadAsync(book.Id);
        if (map is null || !map.MatchesPair(audioHash, ebookHash))
            throw new InvalidDataException("Ta karta ne pripada ovim datotekama (drugo izdanje e-knjige ili drugi audio).");

        Console.WriteLine("Koristim zadanu kartu poravnanja, bez slušanja.");
    }
    else
    {
        if (!FfmpegDecoder.IsAvailable())
            throw new InvalidOperationException("FFmpeg nije pronađen. Instaliraj ga (winget install Gyan.FFmpeg) ili zadaj --ffmpeg <putanja>.");

        var model = await Models.EnsureAsync(options.Model, language, ct);
        var threads = Math.Min(Environment.ProcessorCount, 8);

        Console.WriteLine($"Model: {Path.GetFileName(model)}, jezik: {language ?? "auto"}, {threads} niti, " +
                          (options.Fast ? "uzorkovanje kao na mobitelu" : "sluša cijelu knjigu"));

        await using var transcriber = DesktopTranscriber.Create(model, language ?? "auto", threads);

        var settings = options.Fast
            ? new AlignmentSettings()
            : new AlignmentSettings { ProbeDurationMs = 30_000, ProbeIntervalMs = 30_000 };

        var chapterCount = (await database.GetChaptersAsync(book.Id)).Count;
        var clock = Stopwatch.StartNew();
        var anchors = 0;

        var progress = new SynchronousProgress<AlignmentProgress>(p =>
        {
            anchors = p.AnchorsFound;

            var done = (p.ChapterIndex + (p.ProbeCount > 0 ? p.ProbesCompleted / (double)p.ProbeCount : 0)) / Math.Max(chapterCount, 1);
            var speed = transcriber.WorkMs > 0 ? transcriber.AudioMs / (double)transcriber.WorkMs : 0;
            var left = done > 0.001 ? TimeSpan.FromSeconds(clock.Elapsed.TotalSeconds / done * (1 - done)) : (TimeSpan?)null;

            Console.Write($"\r  poglavlje {p.ChapterIndex + 1}/{chapterCount}  {done:P1}  " +
                          $"sidra {p.AnchorsFound}  {speed:0.0}x  " +
                          (left is { } l ? $"još ~{Duration((long)l.TotalMilliseconds)}   " : "   "));
        });

        var aligner = new BookAligner(database, syncMaps, extractors, transcriber, settings, throttle: null, log: Log);
        await aligner.AlignAsync(book.Id, progress, ct);

        Console.WriteLine();
        Console.WriteLine($"Poravnanje gotovo za {Duration((long)clock.Elapsed.TotalMilliseconds)}.");

        map = await syncMaps.LoadAsync(book.Id);
    }

    if (map is null || map.Chapters.All(c => c.IsEmpty))
        throw new InvalidOperationException("Poravnanje nije pronašlo nijedno mjesto u tekstu. Jesu li audio i e-knjiga ista knjiga?");

    times = Epub3Writer.TimesFromMap(extracted.Text, map);
    return Finish(options, extracted, times, audio.DurationMs, Log);
}

static int Finish(Options options, ExtractedBook extracted, Dictionary<int, SentenceTime> times, long durationMs, Action<string> Log)
{
    if (times.Count == 0)
        throw new InvalidOperationException("Poravnanje nije pronašlo nijedno mjesto u tekstu. Jesu li audio i e-knjiga ista knjiga?");

    // ---- The EPUB 3 ----

    Console.Write("Zapisujem EPUB 3… ");

    var report = Epub3Writer.Write(
        options.Ebook, extracted, times, options.Audio, durationMs, options.Output,
        warning => { Console.WriteLine(); Console.WriteLine("  upozorenje: " + warning); Log("warning: " + warning); });

    Console.WriteLine("gotovo.");
    Console.WriteLine();
    Console.WriteLine($"  {options.Output}");
    Console.WriteLine($"  {report.Bytes / 1_048_576} MB, {report.TimedSentences} od {report.Sentences} rečenica ima vrijeme " +
                      $"({(report.Sentences > 0 ? report.TimedSentences / (double)report.Sentences : 0):P0}), " +
                      $"{report.DocumentsWithOverlay} od {report.Documents} dokumenata sinkronizirano");
    Console.WriteLine();
    Console.WriteLine("Probaj ga u Thorium Readeru (thorium.edrlab.org): otvori knjigu i pokreni \"Read aloud\" / \"Media overlay\".");

    Log($"done: {report}");
    return 0;
}

static string Duration(long ms)
{
    var time = TimeSpan.FromMilliseconds(ms);
    return time.TotalHours >= 1 ? $"{(int)time.TotalHours} h {time.Minutes:00} min" : $"{time.Minutes} min {time.Seconds:00} s";
}

/// <summary>Reports on the thread that made the report, so the console line is never written by two at once.</summary>
sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
{
    private readonly object _gate = new();

    public void Report(T value)
    {
        lock (_gate) report(value);
    }
}

sealed record Options(string Ebook, string Audio, string Output, string Model, bool Fast, string? Map, string? Language, bool Whisper)
{
    public static Options? Parse(string[] args)
    {
        var positional = new List<string>();
        string? output = null, map = null, language = null;
        var model = "base";
        var fast = false;
        var whisper = false;

        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} traži vrijednost");

            switch (args[i])
            {
                case "-o" or "--izlaz" or "--output": output = Next(); break;
                case "-m" or "--model": model = Next(); break;
                case "--brzo" or "--fast": fast = true; whisper = true; break;
                case "--whisper": whisper = true; break;
                case "--karta" or "--map": map = Next(); break;
                case "--jezik" or "--language": language = Next(); break;
                case "--ffmpeg": FfmpegDecoder.Executable = Next(); break;
                case "-h" or "--help" or "/?": return null;
                default: positional.Add(args[i]); break;
            }
        }

        if (positional.Count != 2) return null;

        // Either order: whichever the audio probe recognises is the audiobook.
        var (ebook, audio) = AudioBookProbe.IsSupportedAudioFile(positional[0])
            ? (positional[1], positional[0])
            : (positional[0], positional[1]);

        ebook = Path.GetFullPath(ebook);
        audio = Path.GetFullPath(audio);

        foreach (var file in new[] { ebook, audio, map }.OfType<string>())
            if (!File.Exists(file)) throw new FileNotFoundException($"Ne postoji: {file}");

        if (!Path.GetExtension(ebook).Equals(".epub", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("E-knjiga mora biti EPUB — EPUB 3 se gradi iz originalnog EPUB-a.");

        if (!Models.Names.Contains(model))
            throw new NotSupportedException($"Nepoznat model '{model}'. Mogući: {string.Join(", ", Models.Names)}.");

        output ??= Path.Combine(
            Path.GetDirectoryName(ebook)!,
            Path.GetFileNameWithoutExtension(ebook) + " - EPUB3.epub");

        return new Options(ebook, audio, Path.GetFullPath(output), model, fast, map is null ? null : Path.GetFullPath(map), language, whisper);
    }

    public static void PrintUsage()
    {
        Console.WriteLine("""
            epub3maker — od e-knjige i audioknjige radi EPUB 3 sa sinkroniziranim čitanjem (Media Overlays)

            Upotreba:
              epub3maker <knjiga.epub> <audioknjiga.m4b|.mp3> [opcije]

            Zadano poravnava Metinim MMS modelom slovo po slovo (najpreciznije, ~360 MB, preuzima se jednom).

            Opcije:
              -o, --izlaz <datoteka>   gdje zapisati (zadano: "<ime knjige> - EPUB3.epub" pokraj e-knjige)
              --whisper                poravnaj Whisperom kao aplikacija (sidra + interpolacija) umjesto MMS-a
              -m, --model <ime>        Whisper model: tiny | base | small | medium  (zadano: base)
              --brzo                   Whisper, uzorkuje kao mobitel (10 s svake minute)
              --karta <book-N.sync.json>  koristi poravnanje napravljeno na mobitelu, bez slušanja
              --jezik <hr|en|…>        jezik naracije, ako ga ne prepozna sam
              --ffmpeg <putanja>       ffmpeg.exe, ako nije u PATH-u

            Ctrl+C zaustavlja; isto pokretanje poslije nastavlja gdje je stalo.
            """);
    }
}
