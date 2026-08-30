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

    private WhisperTranscriber(WhisperFactory factory, WhisperProcessor processor, bool backgroundPriority)
    {
        _factory = factory;
        _processor = processor;
        _backgroundPriority = backgroundPriority;
    }

    /// <param name="language">
    /// A language code, or "auto" to detect. Naming it is worth doing: detection is an extra pass
    /// over the audio for something the book already tells us.
    /// </param>
    public static WhisperTranscriber Create(string modelPath, CpuBudget budget, string language = "auto")
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

        if (budget.Threads > 0) builder = builder.WithThreads(budget.Threads);

        return new WhisperTranscriber(factory, builder.Build(), budget.BackgroundPriority);
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
            var samples = await Task.Run(() => DecodeOnAWorkerThread(audioPath, startMs, durationMs, ct), ct);
            if (samples.Length == 0) return Transcript.Empty;

            return await RecognizeAsync(samples, startMs, ct);
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    private float[] DecodeOnAWorkerThread(string audioPath, long startMs, long durationMs, CancellationToken ct)
    {
        ApplyThreadPriority();
        return AudioPcmDecoder.Decode(audioPath, startMs, durationMs, ct);
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
        ApplyThreadPriority();

        var segments = new List<TranscriptSegment>();

        await foreach (var segment in _processor.ProcessAsync(samples, ct))
            segments.Add(new TranscriptSegment(
                startMs + (long)segment.Start.TotalMilliseconds,
                startMs + (long)segment.End.TotalMilliseconds,
                segment.Text));

        return Transcript.FromSegments(segments);
    }

    /// <summary>
    /// Drops the calling thread to background priority.
    ///
    /// On Android this does more than reorder the scheduler: a background-priority thread is moved
    /// into the background cpuset, which confines it to the efficiency cores. That single call is
    /// most of the difference between alignment the user never notices and alignment that makes
    /// the phone hot and unresponsive.
    ///
    /// whisper.cpp spawns its own worker threads from here, and threads inherit their creator's
    /// cgroup, so the whole recognition run stays on the little cores rather than only this thread.
    /// </summary>
    private void ApplyThreadPriority()
    {
        if (!_backgroundPriority) return;

        try
        {
            AndroidProcess.SetThreadPriority(global::Android.OS.ThreadPriority.Background);
        }
        catch (Java.Lang.IllegalArgumentException)
        {
            // Some devices refuse the change; running at normal priority is worse but not broken.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _processor.DisposeAsync();
        _factory.Dispose();
        _oneAtATime.Dispose();
    }
}
