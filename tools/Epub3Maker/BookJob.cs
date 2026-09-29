using System.Diagnostics;
using AudioBookReader.Core.Alignment;
using AudioBookReader.Core.Audio;
using AudioBookReader.Core.Books;
using AudioBookReader.Core.Data;
using AudioBookReader.Core.Models;

namespace Epub3Maker;

/// <summary>One book to make: the two halves, where it goes, and how hard to listen.</summary>
/// <param name="AnchorSeconds">Seconds between probes for the Whisper qualities; null for the quality's own.</param>
/// <param name="Model">A Whisper model other than the quality's own.</param>
/// <param name="Map">An alignment made on the phone, used instead of listening.</param>
/// <param name="Threads">Processor threads to use; 0 for all of them.</param>
public sealed record JobSpec(
    string Ebook,
    string Audio,
    string Output,
    Quality Quality,
    string? Model = null,
    int? AnchorSeconds = null,
    string? Map = null,
    string? Language = null,
    int Threads = 0);

public sealed record JobResult(WriteReport Written, TimeSpan Elapsed);

/// <summary>
/// Makes one EPUB 3: reads the ebook, listens to the audiobook, lines the two up and writes the
/// package. The same whether a person typed the command or a nightly scan picked the book.
///
/// Every way of listening can be stopped and started again without losing more than a few minutes:
/// the Whisper qualities keep the app's own library and map in the work folder, the MMS quality
/// keeps what it has heard so far. A stop is an <see cref="OperationCanceledException"/>, and
/// running the same job again carries on.
/// </summary>
public static class BookJob
{
    public static async Task<JobResult> RunAsync(JobSpec spec, IJobOutput output, LogFile log, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        void Log(string line) => log.Write(line);

        Log($"---- {spec.Ebook} + {spec.Audio} ({Qualities.Name(spec.Quality)})");

        output.Line($"E-knjiga:    {Path.GetFileName(spec.Ebook)}");
        output.Line($"Audioknjiga: {Path.GetFileName(spec.Audio)}");

        var extractors = new BookTextExtractors();
        var extracted = await extractors.ExtractAsync(spec.Ebook, ct);
        var plainText = extracted.Text.PlainText;

        if (plainText.Length == 0) throw new InvalidDataException("U e-knjizi nema teksta.");

        var language = spec.Language ?? TextLanguage.Detect(plainText);

        var audio = await AudioBookProbe.ProbeAsync(spec.Audio, ct);
        output.Line($"Audio:       {Duration(audio.DurationMs)}, {audio.Chapters.Count} poglavlja; jezik: {language ?? "?"}");

        if (audio.DurationMs <= 0) throw new InvalidDataException("Audio datoteci se ne može pročitati trajanje.");

        var threads = spec.Threads > 0 ? spec.Threads : Environment.ProcessorCount;
        var audioHash = await ContentHash.ComputeAsync(spec.Audio, ct);

        Dictionary<int, SentenceTime> times;
        string? checkpoint = null;

        if (spec.Map is null && spec.Quality == Quality.Best)
        {
            // ---- Forced alignment, letter by letter ----

            RequireFfmpeg();

            using var model = await MmsModel.LoadAsync(threads, ct);

            checkpoint = Path.Combine(Paths.Work, "mms", audioHash + ".f32");
            output.Line($"Slušam cijelu knjigu (MMS, slovo po slovo, {threads} niti)…");

            var eta = new Eta(audio.DurationMs);
            var emissions = await Task.Run(
                () => model.Emissions(spec.Audio, audio.DurationMs, checkpoint, done => output.Progress(done, eta.Describe(done)), ct),
                ct);

            output.Line($"Preslušano. Poravnavam slovo po slovo…");

            var (ctcTimes, report) = CtcAligner.Align(extracted.Text, emissions, Log);
            times = ctcTimes;

            output.Line($"  {report.Anchors} sidara, {report.Segments} odsječaka poravnato" +
                        (report.SkippedSegments > 0 ? $", {report.SkippedSegments} preskočeno (tekst koji se ne čita ili ne odgovara)" : ""));
        }
        else
        {
            times = await AlignWithWhisperAsync(spec, extracted, extractors, audio, audioHash, language, threads, output, Log, ct);
        }

        if (times.Count == 0)
            throw new InvalidOperationException("Poravnanje nije pronašlo nijedno mjesto u tekstu. Jesu li audio i e-knjiga ista knjiga?");

        // ---- The EPUB 3 ----

        output.Line("Zapisujem EPUB 3…");
        Directory.CreateDirectory(Path.GetDirectoryName(spec.Output)!);

        var written = Epub3Writer.Write(
            spec.Ebook, extracted, times, spec.Audio, audio.DurationMs, spec.Output,
            warning => { output.Line("  upozorenje: " + warning); });

        output.Line($"  {spec.Output}");
        output.Line($"  {written.Bytes / 1_048_576} MB, {written.TimedSentences} od {written.Sentences} rečenica ima vrijeme " +
                    $"({(written.Sentences > 0 ? written.TimedSentences / (double)written.Sentences : 0):P0}), " +
                    $"{written.DocumentsWithOverlay} od {written.Documents} dokumenata sinkronizirano, " +
                    $"gotovo za {Duration((long)clock.Elapsed.TotalMilliseconds)}");

        Log($"done: {written}");

        // What was heard is only worth keeping until the book is written.
        if (checkpoint is not null) File.Delete(checkpoint);

        return new JobResult(written, clock.Elapsed);
    }

    /// <summary>
    /// The app's own aligner — sampled probes, anchors, the text between them estimated — with the
    /// same library and map the phone keeps, so an interrupted run carries on.
    ///
    /// One library per model and spacing: a book aligned lightly once and asked for again at a
    /// better quality is aligned afresh rather than handed the sparse map it already has.
    /// </summary>
    private static async Task<Dictionary<int, SentenceTime>> AlignWithWhisperAsync(
        JobSpec spec, ExtractedBook extracted, BookTextExtractors extractors, AudioBookInfo audio, string audioHash,
        string? language, int threads, IJobOutput output, Action<string> log, CancellationToken ct)
    {
        var settings = Qualities.Settings(spec.Quality, spec.AnchorSeconds);
        var modelName = spec.Model ?? Qualities.DefaultModel(spec.Quality);

        var work = spec.Map is not null
            ? Path.Combine(Paths.Work, "phone-maps")
            : Path.Combine(Paths.Work, $"whisper-{modelName}-{settings.ProbeDurationMs / 1000}of{settings.ProbeIntervalMs / 1000}s");

        Directory.CreateDirectory(work);

        var ebookHash = await ContentHash.ComputeAsync(spec.Ebook, ct);

        var database = new LibraryDatabase(Path.Combine(work, "library.db3"));
        var syncMaps = new SyncMapStore(Path.Combine(work, "maps"));
        var library = new LibraryService(database, syncMaps);

        var book = (await database.GetBooksAsync()).FirstOrDefault(b => b.AudioHash == audioHash && b.EbookHash == ebookHash);

        if (book is null)
        {
            book = await library.CreateFromAudioAsync(new AudioAttachment(
                spec.Audio, audioHash, audio.DurationMs, audio.Chapters, audio.Title, audio.Author));

            book = await library.AttachTextAsync(book.Id, new TextAttachment(
                spec.Ebook, ebookHash, extracted.Text.PlainText.Length, extracted.Chapters,
                extracted.Text.Title, extracted.Text.Author, language));
        }
        else
        {
            // The same files, perhaps moved since the last run.
            book.AudioPath = spec.Audio;
            book.EbookPath = spec.Ebook;
            await database.UpdateBookAsync(book);
        }

        SyncMap? map;

        if (spec.Map is { } mapFile)
        {
            // A map made on the phone: nothing to listen to here.
            Directory.CreateDirectory(Path.Combine(work, "maps"));
            File.Copy(mapFile, Path.Combine(work, "maps", $"book-{book.Id}.sync.json"), overwrite: true);

            map = await syncMaps.LoadAsync(book.Id);
            if (map is null || !map.MatchesPair(audioHash, ebookHash))
                throw new InvalidDataException("Ta karta ne pripada ovim datotekama (drugo izdanje e-knjige ili drugi audio).");

            output.Line("Koristim zadanu kartu poravnanja, bez slušanja.");
        }
        else
        {
            RequireFfmpeg();

            var model = await Models.EnsureAsync(modelName, language, ct);

            output.Line($"Model: {Path.GetFileName(model)}, {threads} niti, " +
                        $"sluša {settings.ProbeDurationMs / 1000} s svakih {settings.ProbeIntervalMs / 1000} s");

            await using var transcriber = DesktopTranscriber.Create(model, language ?? "auto", threads);

            var chapterCount = (await database.GetChaptersAsync(book.Id)).Count;

            var progress = new SynchronousProgress<AlignmentProgress>(p =>
            {
                var done = (p.ChapterIndex + (p.ProbeCount > 0 ? p.ProbesCompleted / (double)p.ProbeCount : 0)) / Math.Max(chapterCount, 1);
                var speed = transcriber.WorkMs > 0 ? transcriber.AudioMs / (double)transcriber.WorkMs : 0;

                output.Progress(done, $"poglavlje {p.ChapterIndex + 1}/{chapterCount}  sidra {p.AnchorsFound}  {speed:0.0}x");
            });

            var aligner = new BookAligner(database, syncMaps, extractors, transcriber, settings, throttle: null, log: log);
            await aligner.AlignAsync(book.Id, progress, ct);

            map = await syncMaps.LoadAsync(book.Id);
        }

        if (map is null || map.Chapters.All(c => c.IsEmpty))
            throw new InvalidOperationException("Poravnanje nije pronašlo nijedno mjesto u tekstu. Jesu li audio i e-knjiga ista knjiga?");

        return Epub3Writer.TimesFromMap(extracted.Text, map);
    }

    public static void RequireFfmpeg()
    {
        if (!FfmpegDecoder.IsAvailable())
            throw new InvalidOperationException(
                $"FFmpeg nije pronađen ('{FfmpegDecoder.Executable}'). Instaliraj ga — Windows: winget install Gyan.FFmpeg, ili " +
                "raspakiraj https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip; Linux: apt install ffmpeg — " +
                "ili zadaj --ffmpeg <putanja do ffmpeg.exe>.");
    }

    public static string Duration(long ms)
    {
        var time = TimeSpan.FromMilliseconds(ms);
        return time.TotalHours >= 1 ? $"{(int)time.TotalHours} h {time.Minutes:00} min" : $"{time.Minutes} min {time.Seconds:00} s";
    }

    /// <summary>
    /// Speed and time left, from this run's own pace — a run that resumed halfway does not count
    /// the half it did not do as done in no time.
    /// </summary>
    private sealed class Eta(long durationMs)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double? _first;

        public string Describe(double done)
        {
            _first ??= done;

            var did = done - _first.Value;
            var seconds = _clock.Elapsed.TotalSeconds;

            if (did <= 0.0005 || seconds <= 0) return "";

            var speed = did * durationMs / 1000.0 / seconds;
            var left = TimeSpan.FromSeconds(seconds / did * (1 - done));

            return $"{speed:0.0}x  još ~{Duration((long)left.TotalMilliseconds)}";
        }
    }
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
