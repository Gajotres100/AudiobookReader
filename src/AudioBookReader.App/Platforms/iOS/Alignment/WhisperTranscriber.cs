using AudioBookReader.App.Services;
using AudioBookReader.Core.Alignment;
using Whisper.net;

namespace AudioBookReader.App.Platforms.iOS.Alignment;

/// <summary>
/// The real <see cref="ITranscriber"/> on iOS — decodes a stretch of the audiobook via
/// <see cref="AudioPcmDecoder"/> and runs the same Whisper.net API Android uses, since whisper.net
/// itself is cross-platform and ships ios-device/ios-simulator native binaries in the same package.
///
/// The one thing this does not port is Android's thread-priority pinning
/// (Process.SetThreadPriority(Background), which confines whisper's native worker threads to the
/// little cores). iOS has no equivalent lever a managed app can pull — Grand Central Dispatch's QoS
/// classes influence scheduling hints, not cpuset placement, and whisper.cpp spawns its own native
/// threads that this process does not get to tag afterward. Pacing here rests entirely on the
/// thread count and duty cycle, same as it always did for the recognition side of the Android
/// budget; only the extra, nearly-free win from pinning is a difference between the platforms that
/// no amount of C# closes.
/// </summary>
public sealed class WhisperTranscriber : ITranscriber, IAsyncDisposable
{
    private readonly WhisperFactory _factory;
    private readonly WhisperProcessor _processor;
    private readonly bool _wordTimestamps;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    private WhisperTranscriber(WhisperFactory factory, WhisperProcessor processor, bool wordTimestamps)
    {
        _factory = factory;
        _processor = processor;
        _wordTimestamps = wordTimestamps;
    }

    public static WhisperTranscriber Create(
        string modelPath,
        CpuBudget budget,
        string language = "auto",
        bool wordTimestamps = true)
    {
        // On the CPU, not the GPU. The iOS runtime ships whisper's Metal backend and uses it by
        // default, but iOS forbids submitting GPU work from the background — and both kinds of
        // alignment carry on there: reading-along while the book plays with the screen locked, and
        // the whole-book run once iOS lets it continue. The GPU path failed in exactly those moments
        // and alignment only worked with the screen on. Android was always CPU-only, and the tiny
        // model is quick enough on these cores.
        var factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = false });

        var builder = factory.CreateBuilder()
            .WithLanguage(language)
            .WithNoContext()
            .WithGreedySamplingStrategy(greedy => greedy.WithBestOf(1));

        if (wordTimestamps) builder = builder.WithTokenTimestamps();

        var threads = budget.ResolveThreads(Environment.ProcessorCount);
        builder = builder.WithThreads(threads);

        AppLog.Info(
            $"whisper: preset '{budget.Id}', {threads} threads of {Environment.ProcessorCount} cores, " +
            $"duty {budget.DutyCycle:P0}, {(wordTimestamps ? "word times" : "segment times")}, " +
            $"model '{System.IO.Path.GetFileName(modelPath)}'");

        return new WhisperTranscriber(factory, builder.Build(), wordTimestamps);
    }

    public async Task<Transcript> TranscribeAsync(
        string audioPath, long startMs, long durationMs, CancellationToken ct = default)
    {
        await _oneAtATime.WaitAsync(ct);

        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();

            var samples = await Task.Run(() => DecodeOnAWorkerThread(audioPath, startMs, durationMs, ct), ct);
            var decodedMs = clock.ElapsedMilliseconds;

            if (samples.Length == 0) return Transcript.Empty;

            var transcript = await RecognizeAsync(samples, startMs, ct);

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

    private void Report(long decodeMs, long recogniseMs, long durationMs)
    {
        _probes++;
        _decodeMs += decodeMs;
        _recogniseMs += recogniseMs;
        _audioMs += durationMs;

        if (_probes != 1 && _probes % 10 != 0) return;

        var wall = _decodeMs + _recogniseMs;

        AppLog.Info(
            $"transcribe: {_probes} probes, decode {_decodeMs / _probes} ms + recognise " +
            $"{_recogniseMs / _probes} ms each, {_audioMs / 1000} s of audio in {wall / 1000} s " +
            $"({(wall > 0 ? _audioMs / (double)wall : 0):0.0}x realtime)");
    }

    private AudioPcmDecoder.Session? _decoder;

    /// <summary>Decodes the probe, keeping the file's asset and track open between probes — see AudioPcmDecoder's own doc comment for why that is only half of what Android's session caches.</summary>
    private float[] DecodeOnAWorkerThread(string audioPath, long startMs, long durationMs, CancellationToken ct)
    {
        if (_decoder is not null && _decoder.Path != audioPath)
        {
            _decoder.Dispose();
            _decoder = null;
        }

        _decoder ??= AudioPcmDecoder.Session.Open(audioPath);

        return _decoder.Decode(startMs, durationMs, ct);
    }

    private async Task<Transcript> RecognizeAsync(float[] samples, long startMs, CancellationToken ct)
    {
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

    private static IReadOnlyList<TimedWord>? WordsOf(SegmentData segment, long startMs, long from, long to)
    {
        if (segment.Tokens is not { Length: > 0 } tokens) return null;

        var spoken = new List<SpokenToken>(tokens.Length);

        foreach (var token in tokens)
        {
            var at = startMs + token.Start * 10;

            if (at < from - Tolerance || at > to + Tolerance) return null;

            spoken.Add(new SpokenToken(token.Text ?? "", Math.Clamp(at, from, to)));
        }

        var words = TokenWords.Merge(spoken);
        return words.Count > 0 ? words : null;
    }

    private const long Tolerance = 1_000;

    public async ValueTask DisposeAsync()
    {
        _decoder?.Dispose();
        _decoder = null;

        await _processor.DisposeAsync();
        _factory.Dispose();
        _oneAtATime.Dispose();
    }
}
