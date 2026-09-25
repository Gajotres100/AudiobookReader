using AudioBookReader.Core.Alignment;
using Whisper.net;

namespace Epub3Maker;

/// <summary>
/// Whisper on a PC, behind the same seam the phone uses.
///
/// Built the way the Android transcriber is — greedy, no context between probes, per-word times —
/// so a map made here is measured exactly as one made on the phone would be, only with every core
/// and a larger model.
/// </summary>
public sealed class DesktopTranscriber : ITranscriber, IAsyncDisposable
{
    private readonly WhisperFactory _factory;
    private readonly WhisperProcessor _processor;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    /// <summary>Seconds of audio heard and seconds spent, for the speed shown while it runs.</summary>
    public long AudioMs { get; private set; }

    public long WorkMs { get; private set; }

    private DesktopTranscriber(WhisperFactory factory, WhisperProcessor processor)
    {
        _factory = factory;
        _processor = processor;
    }

    public static DesktopTranscriber Create(string modelPath, string language, int threads)
    {
        var factory = WhisperFactory.FromPath(modelPath);

        var processor = factory.CreateBuilder()
            .WithLanguage(language)
            .WithNoContext()
            .WithGreedySamplingStrategy(greedy => greedy.WithBestOf(1))
            .WithTokenTimestamps()
            .WithThreads(threads)
            .Build();

        return new DesktopTranscriber(factory, processor);
    }

    public async Task<Transcript> TranscribeAsync(string audioPath, long startMs, long durationMs, CancellationToken ct = default)
    {
        await _oneAtATime.WaitAsync(ct);

        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();

            var samples = await FfmpegDecoder.DecodeAsync(audioPath, startMs, durationMs, ct);
            if (samples.Length == 0) return Transcript.Empty;

            var segments = new List<TranscriptSegment>();

            await foreach (var segment in _processor.ProcessAsync(samples, ct))
            {
                var from = startMs + (long)segment.Start.TotalMilliseconds;
                var to = startMs + (long)segment.End.TotalMilliseconds;

                segments.Add(new TranscriptSegment(
                    from, to, segment.Text, WordsOf(segment, startMs, from, to),
                    segment.Probability, segment.NoSpeechProbability));
            }

            AudioMs += durationMs;
            WorkMs += clock.ElapsedMilliseconds;

            return Transcript.FromSegments(segments);
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    /// <summary>The segment's words with the time each was spoken — the same rules as on the phone.</summary>
    private static List<TimedWord>? WordsOf(SegmentData segment, long startMs, long from, long to)
    {
        if (segment.Tokens is not { Length: > 0 } tokens) return null;

        const long tolerance = 1_000;
        var spoken = new List<SpokenToken>(tokens.Length);

        foreach (var token in tokens)
        {
            // Hundredths of a second, relative to the buffer.
            var at = startMs + token.Start * 10;
            if (at < from - tolerance || at > to + tolerance) return null;

            spoken.Add(new SpokenToken(token.Text ?? "", Math.Clamp(at, from, to)));
        }

        var words = TokenWords.Merge(spoken);
        return words.Count > 0 ? words : null;
    }

    public async ValueTask DisposeAsync()
    {
        await _processor.DisposeAsync();
        _factory.Dispose();
        _oneAtATime.Dispose();
    }
}
