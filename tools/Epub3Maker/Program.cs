using System.Runtime.InteropServices;
using AudioBookReader.Core.Audio;
using Epub3Maker;

// epub3maker <book.epub> <audiobook.m4b> [options]     one book, now
// epub3maker scan <library> --output <folder> [options]  every book in a library, unattended
//
// Aligns ebooks with their audiobooks and writes EPUB 3s whose Media Overlays let any reading
// system that supports them follow the narration sentence by sentence.

Console.OutputEncoding = System.Text.Encoding.UTF8;

using var cancellation = new CancellationTokenSource();

void Stop(string how)
{
    // Every way of listening saves as it goes, so stopping here loses at most the last few
    // minutes; running the same command again carries on.
    if (cancellation.IsCancellationRequested) return;
    Console.WriteLine();
    Console.WriteLine($"Zaustavljam ({how})… napredak je spremljen, isto pokretanje nastavlja gdje je stalo.");
    cancellation.Cancel();
}

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Stop("Ctrl+C");
};

// `docker stop` and systemd send SIGTERM; without this the process dies mid-write.
using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
{
    context.Cancel = true;
    Stop("SIGTERM");
});

try
{
    if (args.Length > 0 && ScanCommand.IsCommand(args[0]))
        return await ScanCommand.RunAsync(args[1..], cancellation.Token);

    return await SingleBook.RunAsync(args, cancellation.Token);
}
catch (OperationCanceledException)
{
    return 2;
}
catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or DirectoryNotFoundException
                               or InvalidDataException or InvalidOperationException or NotSupportedException)
{
    Console.WriteLine();
    Console.Error.WriteLine("Greška: " + ex.Message);
    return 1;
}

/// <summary><c>epub3maker book.epub book.m4b</c>: one book, with a person watching.</summary>
static class SingleBook
{
    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        var positional = new List<string>();
        var settings = Settings.FromEnvironment().With(Settings.FromCommandLine(args, positional));

        if (settings.IsOn("help") || positional.Count != 2)
        {
            Help.Print();
            return settings.IsOn("help") ? 0 : 1;
        }

        if (settings["data"] is { Length: > 0 } data) Paths.Data = Path.GetFullPath(data);
        if (settings["ffmpeg"] is { Length: > 0 } ffmpeg) FfmpegDecoder.Executable = ffmpeg;

        // Either order: whichever the audio probe recognises is the audiobook.
        var (ebook, audio) = AudioBookProbe.IsSupportedAudioFile(positional[0])
            ? (positional[1], positional[0])
            : (positional[0], positional[1]);

        ebook = Path.GetFullPath(ebook);
        audio = Path.GetFullPath(audio);
        var map = settings["map"] is { } m ? Path.GetFullPath(m) : null;

        foreach (var file in new[] { ebook, audio, map }.OfType<string>())
            if (!File.Exists(file)) throw new FileNotFoundException($"Ne postoji: {file}");

        if (!Path.GetExtension(ebook).Equals(".epub", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("E-knjiga mora biti EPUB — EPUB 3 se gradi iz originalnog EPUB-a.");

        var quality = settings["quality"] is { } q
            ? Qualities.Parse(q) ?? throw new ArgumentException($"Nepoznata kvaliteta '{q}'. Moguće: light, better, best.")
            : Quality.Best;

        int? anchors = settings["anchors"] is { } a
            ? Qualities.ParseAnchors(a) ?? throw new ArgumentException($"Sidra: sparse, medium, dense ili broj sekundi 5–600, ne '{a}'.")
            : null;

        if (settings["model"] is { } model && !Models.Names.Contains(model))
            throw new ArgumentException($"Nepoznat model '{model}'. Mogući: {string.Join(", ", Models.Names)}.");

        var threads = settings["threads"] is { } t
            ? int.TryParse(t, out var n) && n > 0 ? n : throw new ArgumentException($"Niti: pozitivan broj, ne '{t}'.")
            : 0;

        var output = Path.GetFullPath(settings["output"] ?? Path.Combine(
            Path.GetDirectoryName(ebook)!,
            Path.GetFileNameWithoutExtension(ebook) + " - EPUB3.epub"));

        using var log = new LogFile(Paths.Log);

        await BookJob.RunAsync(
            new JobSpec(ebook, audio, output, quality, settings["model"], anchors, map, settings["language"], threads),
            new InteractiveOutput(log), log, ct);

        Console.WriteLine();
        Console.WriteLine("Probaj ga u Thorium Readeru (thorium.edrlab.org) ili ga uvezi u Syncbook gumbom EP3.");
        return 0;
    }
}

static class Help
{
    public static void Print()
    {
        Console.WriteLine("""
            epub3maker — makes an EPUB 3 with synchronised narration (Media Overlays)
                         from an ebook and its audiobook

            One book:
              epub3maker <book.epub> <audiobook.m4b|.mp3> [options]

            A whole library, unattended (see: epub3maker scan --help):
              epub3maker scan <library> --output <folder> [options]

            Options:
              -o, --output <file>       where to write (default: "<book> - EPUB3.epub" next to the ebook)
              -q, --quality <q>         light | better | best            (default: best)
                                          light   Whisper tiny, samples the book      ~1–1.5 s
                                          better  Whisper base, samples more densely  ~0.5–1 s
                                          best    MMS, every letter of the whole book  20–80 ms
              --anchors <a>             light/better only: sparse | medium | dense, or seconds between probes
              -m, --model <name>        Whisper model instead of the quality's own: tiny | base | small | medium
              --map <book-N.sync.json>  use an alignment made on the phone, no listening
              --language <hr|en|…>      narration language, if it is not recognised
              --threads <n>             processor threads (default: all)
              --ffmpeg <path>           ffmpeg, if it is not on PATH
              --data <folder>           models and work in progress (or EPUB3MAKER_DATA)

            Hrvatski nazivi rade isto: --izlaz, --kvaliteta lagano|bolje|najbolje, --sidra rijetko|srednje|gusto,
            --jezik, --niti, --karta, --podaci.

            Ctrl+C stops; running the same command again carries on where it stopped.
            """);
    }

    public static void PrintScan()
    {
        Console.WriteLine("""
            epub3maker scan — makes EPUB 3s for every book in a library, one at a time

              epub3maker scan <library> --output <folder> [options]

            Finds every folder holding an EPUB and its audio (one file, a file per chapter, or
            CD1/, CD2/ subfolders), makes what is missing, and remembers what it has done.
            Stopping is always safe: the book in progress carries on next time.

            Options:
              -o, --output <folder>     where the EPUB 3s go, mirroring the library's folders
              -q, --quality <q>         light | better | best            (default: better)
              --anchors <a>             sparse | medium | dense, or seconds between probes
              -m, --model <name>        Whisper model: tiny | base | small | medium
              --language <hr|en|…>      narration language, if it is not recognised
              --threads <n>             processor threads (default: all)
              --window <HH:MM-HH:MM>    work only between these times, e.g. 01:00-07:00 ("off": any time)
              --watch <interval>        keep running, look for new books every 60m, 2h, …
              --retry                   try books that failed again
              --priority <low|normal>   default low, so the server's other work comes first
              --dry-run                 only list what was found and what would be done
              --config <file>           settings file (default: <data>/epub3maker.conf)
              --data <folder>           models, work in progress, state.json, report.txt, log.txt
              --ffmpeg <path>           ffmpeg, if it is not on PATH

            Every option can also be set in the settings file (quality = best) or as an environment
            variable (EPUB3MAKER_QUALITY=best). The command line wins over the environment, the
            environment over the file.

            One book's own settings: a file named .epub3maker in its folder, e.g.
              quality = best
              anchors = dense
              language = hr
              skip                      # leave this book alone
            """);
    }
}
