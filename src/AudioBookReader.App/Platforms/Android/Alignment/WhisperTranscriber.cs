using AudioBookReader.App.Services;
using AudioBookReader.Core.Alignment;
using Whisper.net;
using AndroidProcess = Android.OS.Process;

namespace AudioBookReader.App.Platforms.Android.Alignment;

/// <summary>
/// The real <see cref="ITranscriber"/>: hardware-decodes a stretch of the audiobook and runs
/// whisper over it, entirely on the device.
/// </summary>
public sealed class WhisperTranscriber : ITranscriber, IAsyncDisposable
{
    private readonly WhisperFactory _factory;
    private readonly WhisperProcessor _processor;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private readonly bool _backgroundPriority;
    private readonly bool _wordTimestamps;

    private WhisperTranscriber(
        WhisperFactory factory,
        WhisperProcessor processor,
        bool backgroundPriority,
        bool wordTimestamps)
    {
        _factory = factory;
        _processor = processor;
        _backgroundPriority = backgroundPriority;
        _wordTimestamps = wordTimestamps;
    }

    /// <param name="language">
    /// A language code, or "auto" to detect. Naming it is worth doing: detection is an extra pass
    /// over the audio for something the book already tells us.
    /// </param>
    /// <param name="wordTimestamps">
    /// Ask the model when each word was spoken, instead of placing them by spreading the segment
    /// evenly. This is post-processing over attention weights the decoder computes anyway, so it
    /// should cost a few percent rather than a multiple — but it is marked experimental upstream
    /// and has not been timed on every device, which is why it can be turned off.
    /// </param>
    public static WhisperTranscriber Create(
        string modelPath,
        CpuBudget budget,
        string language = "auto",
        bool wordTimestamps = true)
    {
        var factory = WhisperFactory.FromPath(modelPath);

        var builder = factory.CreateBuilder()
            .WithLanguage(language)
            // Each probe stands alone, so carrying decoder context between them would only add
            // work and let a bad transcript poison the next one.
            .WithNoContext()
            // Greedy, no beam search. Beam search buys accuracy in the transcript itself, which is
            // not what is being produced here — the words only have to be close enough to find in
            // a text we already have.
            .WithGreedySamplingStrategy(greedy => greedy.WithBestOf(1));

        if (wordTimestamps) builder = builder.WithTokenTimestamps();

        var threads = budget.ResolveThreads(Environment.ProcessorCount);
        builder = builder.WithThreads(threads);

        AppLog.Info(
            $"whisper: preset '{budget.Id}', {threads} threads of {Environment.ProcessorCount} cores, " +
            $"{(budget.BackgroundPriority ? "little cores" : "all cores")}, duty {budget.DutyCycle:P0}, " +
            $"{(wordTimestamps ? "word times" : "segment times")}, model '{System.IO.Path.GetFileName(modelPath)}'");

        return new WhisperTranscriber(factory, builder.Build(), budget.BackgroundPriority, wordTimestamps);
    }

    public async Task<Transcript> TranscribeAsync(
        string audioPath,
        long startMs,
        long durationMs,
        CancellationToken ct = default)
    {
        // The processor holds decoder state, so probes are serialized rather than run in parallel.
        await _oneAtATime.WaitAsync(ct);

        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();

            var samples = await Task.Run(() => DecodeOnAWorkerThread(audioPath, startMs, durationMs, ct), ct);
            var decodedMs = clock.ElapsedMilliseconds;

            if (samples.Length == 0) return Transcript.Empty;

            var transcript = await RecognizeAsync(samples, startMs, ct);

            // Split, because the two have completely different cures: recognition is the model and
            // the CPU budget, decoding is the container and how often the hardware codec is torn
            // down and built again.
            Report(decodedMs, clock.ElapsedMilliseconds - decodedMs, durationMs);

            return transcript;
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    private long _probes;
    private long _decodeMs;
    private long _recogniseMs;
    private long _audioMs;

    /// <summary>
    /// Reports the running cost every so often, in the terms that decide whether alignment is
    /// worth waiting for: how much faster than real time it is getting through the audio.
    /// </summary>
    private void Report(long decodeMs, long recogniseMs, long durationMs)
    {
        _probes++;
        _decodeMs += decodeMs;
        _recogniseMs += recogniseMs;
        _audioMs += durationMs;

        // The first one, then every tenth. Counting only probes that actually reached the
        // recognizer means a short chapter produces very few — the opening chapter of a book is
        // often a title card, and waiting for a round number there meant the throughput reading
        // never appeared at all.
        if (_probes != 1 && _probes % 10 != 0) return;

        var wall = _decodeMs + _recogniseMs;

        // The thermal state travels with the numbers because it explains them: the same preset on
        // the same phone runs at half the speed once the chip is warm, and a timing without it
        // invites the wrong conclusion about what changed.
        AppLog.Info(
            $"transcribe: {_probes} probes, decode {_decodeMs / _probes} ms + recognise " +
            $"{_recogniseMs / _probes} ms each, {_audioMs / 1000} s of audio in {wall / 1000} s " +
            $"({(wall > 0 ? _audioMs / (double)wall : 0):0.0}x realtime), thermal {ThermalNow()}");
    }

    private static string ThermalNow()
    {
        try
        {
            var power = (global::Android.OS.PowerManager?)global::Android.App.Application.Context
                .GetSystemService(global::Android.Content.Context.PowerService);

            return power?.CurrentThermalStatus.ToString() ?? "unknown";
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    private AudioPcmDecoder.Session? _decoder;

    /// <summary>
    /// Decodes the probe, keeping the file open between probes.
    ///
    /// Safe to hold one session because probes are serialized: the recognizer has decoder state of
    /// its own, so they were already running one at a time.
    /// </summary>
    private float[] DecodeOnAWorkerThread(string audioPath, long startMs, long durationMs, CancellationToken ct)
    {
        using var priority = BorrowAtBackgroundPriority();

        if (_decoder is not null && _decoder.Path != audioPath)
        {
            _decoder.Dispose();
            _decoder = null;
        }

        _decoder ??= AudioPcmDecoder.Session.Open(audioPath);

        return _decoder.Decode(startMs, durationMs, ct);
    }

    /// <summary>
    /// Runs recognition, keeping each segment's timing.
    ///
    /// whisper reports when it heard every segment, in time relative to the buffer it was given.
    /// Shifting that by the probe's own start turns it into book time, which is what an anchor
    /// needs — and it is how alignment learns that the narrator paused for two seconds before
    /// starting the sentence this probe caught.
    /// </summary>
    private async Task<Transcript> RecognizeAsync(float[] samples, long startMs, CancellationToken ct)
    {
        // No priority borrow here, deliberately. It cannot span an await: at the first suspension
        // the thread returns to the pool still in the background cpuset, and everything the app
        // schedules on it meanwhile runs on the efficiency cores. And it would not buy what the
        // comment below claims anyway — whisper runs the native work on threads of its own, not on
        // the one waiting here, so demoting the waiter confines nothing. Recognition is paced by
        // the thread count and the duty cycle instead; those are the controls that reach it.
        var segments = new List<TranscriptSegment>();

        await foreach (var segment in _processor.ProcessAsync(samples, ct))
        {
            var from = startMs + (long)segment.Start.TotalMilliseconds;
            var to = startMs + (long)segment.End.TotalMilliseconds;

            segments.Add(new TranscriptSegment(
                from,
                to,
                segment.Text,
                _wordTimestamps ? WordsOf(segment, startMs, from, to) : null,
                segment.Probability,
                segment.NoSpeechProbability));
        }

        return Transcript.FromSegments(segments);
    }

    /// <summary>
    /// The segment's words with the time each was spoken, or null if the times are not usable.
    ///
    /// Validated rather than trusted. Token times are only meaningful when token timestamps were
    /// asked for, they are reported in a unit the binding does not describe, and a model that
    /// wandered can report times outside the segment it just gave. Checking them against the
    /// segment's own bounds catches all three at once, and failing that check costs nothing: the
    /// caller falls back to spreading the segment evenly, which is what it did before.
    /// </summary>
    private static IReadOnlyList<TimedWord>? WordsOf(SegmentData segment, long startMs, long from, long to)
    {
        if (segment.Tokens is not { Length: > 0 } tokens) return null;

        var spoken = new List<SpokenToken>(tokens.Length);

        foreach (var token in tokens)
        {
            // whisper reports token times in hundredths of a second, the same unit it uses for
            // segments. Relative to the buffer, so the probe's own start goes back on.
            var at = startMs + token.Start * 10;

            // A token outside the segment that produced it is not a timing, it is noise. One is
            // enough to distrust the lot: they are only useful as a monotone run, and a run with a
            // hole in it places words worse than an even spread does.
            if (at < from - Tolerance || at > to + Tolerance) return null;

            spoken.Add(new SpokenToken(token.Text ?? "", Math.Clamp(at, from, to)));
        }

        var words = TokenWords.Merge(spoken);

        // Nothing survived merging: all control tokens, or all punctuation. Not a failure, but
        // there is nothing here to time.
        return words.Count > 0 ? words : null;
    }

    /// <summary>
    /// How far outside its segment a token's time may land before the segment's times are refused.
    /// Rounding between units accounts for a few milliseconds; a second means something is wrong.
    /// </summary>
    private const long Tolerance = 1_000;

    /// <summary>
    /// Drops the calling thread to background priority for the duration of one piece of work.
    ///
    /// On Android this does more than reorder the scheduler: a background-priority thread is moved
    /// into the background cpuset, which confines it to the efficiency cores. That single call is
    /// most of the difference between alignment the user never notices and alignment that makes
    /// the phone hot and unresponsive.
    ///
    /// whisper.cpp spawns its own worker threads from here, and threads inherit their creator's
    /// cgroup, so the whole recognition run stays on the little cores rather than only this thread.
    ///
    /// Two guards, both learned the hard way. It refuses to touch the main thread: an await that
    /// resumed there would otherwise leave the app's own UI thread in the background cpuset for the
    /// life of the process, which is a phone that feels broken. And it restores what it found, so a
    /// pooled thread borrowed for one probe is not handed back to unrelated work still demoted.
    /// </summary>
    private IDisposable? BorrowAtBackgroundPriority()
    {
        if (!_backgroundPriority) return null;

        // A continuation that came back to the UI thread must never be demoted.
        if (global::Android.OS.Looper.MyLooper() == global::Android.OS.Looper.MainLooper) return null;

        try
        {
            return new BackgroundPriority();
        }
        catch (Java.Lang.RuntimeException)
        {
            // Some devices refuse the change; running at normal priority is worse but not broken.
            return null;
        }
    }

    /// <summary>Holds one thread at background priority, and puts back what it found.</summary>
    private sealed class BackgroundPriority : IDisposable
    {
        private readonly int _thread = AndroidProcess.MyTid();
        private readonly int _previous;

        public BackgroundPriority()
        {
            _previous = (int)AndroidProcess.GetThreadPriority(_thread);
            AndroidProcess.SetThreadPriority(_thread, global::Android.OS.ThreadPriority.Background);
        }

        public void Dispose()
        {
            try
            {
                AndroidProcess.SetThreadPriority(_thread, (global::Android.OS.ThreadPriority)_previous);
            }
            catch (Java.Lang.RuntimeException)
            {
                // The thread is going away anyway; nothing left to restore it for.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _decoder?.Dispose();
        _decoder = null;

        await _processor.DisposeAsync();
        _factory.Dispose();
        _oneAtATime.Dispose();
    }
}
