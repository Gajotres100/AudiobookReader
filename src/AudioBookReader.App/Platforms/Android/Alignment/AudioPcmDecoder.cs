using Android.Media;

namespace AudioBookReader.App.Platforms.Android.Alignment;

/// <summary>
/// Decodes a stretch of an audiobook into the 16 kHz mono float samples the recognizer expects.
///
/// Decoding goes through <see cref="MediaCodec"/> so the device's hardware AAC decoder does the
/// work. A managed decoder would burn a meaningful share of the CPU budget on a step the phone can
/// do essentially for free, and the whole point of that budget is to spend it on recognition.
///
/// Only the requested range is decoded, never the whole file: a ten-hour book at 16 kHz would be
/// gigabytes of samples, and alignment only ever listens to short probes.
/// </summary>
internal static class AudioPcmDecoder
{
    public const int TargetSampleRate = 16_000;

    private const int DequeueTimeoutUs = 10_000;

    /// <summary>
    /// Keeps one file open across probes.
    ///
    /// Opening it is not cheap: the container has to be parsed, a hardware decoder claimed,
    /// configured and started — and an audiobook is aligned in hundreds of short probes, so doing
    /// that once per probe was measured at a second each, a fifth of the whole run spent switching
    /// a decoder on and off. Seeking a decoder that is already running costs nothing by comparison.
    /// </summary>
    internal sealed class Session : IDisposable
    {
        private readonly MediaExtractor _extractor;
        private readonly MediaCodec _codec;

        public string Path { get; }

        private readonly int _sourceRate;
        private readonly int _channels;

        private Session(string path, MediaExtractor extractor, MediaCodec codec, int sourceRate, int channels)
        {
            Path = path;
            _extractor = extractor;
            _codec = codec;
            _sourceRate = sourceRate;
            _channels = channels;
        }

        public static Session Open(string path)
        {
            var extractor = new MediaExtractor();
            SetSource(extractor, path);

            var trackIndex = FindAudioTrack(extractor)
                ?? throw new InvalidOperationException($"No audio track in '{path}'.");

            var format = extractor.GetTrackFormat(trackIndex);
            extractor.SelectTrack(trackIndex);

            var mime = format.GetString(MediaFormat.KeyMime)
                ?? throw new InvalidOperationException("Audio track has no MIME type.");

            var codec = MediaCodec.CreateDecoderByType(mime)
                ?? throw new InvalidOperationException($"No decoder for '{mime}'.");

            codec.Configure(format, surface: null, crypto: null, flags: MediaCodecConfigFlags.None);
            codec.Start();

            return new Session(
                path,
                extractor,
                codec,
                format.GetInteger(MediaFormat.KeySampleRate),
                format.GetInteger(MediaFormat.KeyChannelCount));
        }

        public float[] Decode(long startMs, long durationMs, CancellationToken ct)
        {
            var startUs = startMs * 1_000;
            var endUs = (startMs + durationMs) * 1_000;

            // Whatever the decoder still holds belongs to the previous probe, and seeking without
            // clearing it would prepend someone else's audio to this one.
            //
            // Flush alone: start() after flush() belongs to asynchronous mode. Calling it on a
            // codec being driven synchronously leaves it in a state where no output buffer is ever
            // produced, and the decode loop waits for one forever.
            _codec.Flush();

            // Seeking lands on the nearest sync point at or before the target, so the leading
            // samples are decoded and then dropped rather than assumed to start on time.
            _extractor.SeekTo(startUs, MediaExtractorSeekTo.PreviousSync);

            var mono = DecodeRange(_codec, _extractor, _channels, _sourceRate, startUs, endUs, ct);
            return Resample(mono, _sourceRate);
        }

        public void Dispose()
        {
            try
            {
                _codec.Stop();
            }
            catch (Java.Lang.IllegalStateException)
            {
                // Already stopped; nothing to salvage and nothing worth reporting.
            }

            _codec.Dispose();
            _extractor.Dispose();
        }
    }

    private static void SetSource(MediaExtractor extractor, string path)
    {
        // Alignment normally works on a local copy, but a book can be referenced where the user
        // keeps it, and then the extractor has to be pointed at the provider instead of a path.
        if (path.StartsWith("content://", StringComparison.OrdinalIgnoreCase))
        {
            extractor.SetDataSource(
                global::Android.App.Application.Context,
                global::Android.Net.Uri.Parse(path)!,
                headers: null);
        }
        else
        {
            extractor.SetDataSource(path);
        }
    }

    public static float[] Decode(string path, long startMs, long durationMs, CancellationToken ct)
    {
        using var extractor = new MediaExtractor();

        // Alignment normally works on a local copy, but a book can be referenced where the user
        // keeps it, and then the extractor has to be pointed at the provider instead of a path.
        if (path.StartsWith("content://", StringComparison.OrdinalIgnoreCase))
        {
            extractor.SetDataSource(
                global::Android.App.Application.Context,
                global::Android.Net.Uri.Parse(path)!,
                headers: null);
        }
        else
        {
            extractor.SetDataSource(path);
        }

        var trackIndex = FindAudioTrack(extractor)
            ?? throw new InvalidOperationException($"No audio track in '{path}'.");

        var format = extractor.GetTrackFormat(trackIndex);
        extractor.SelectTrack(trackIndex);

        var mime = format.GetString(MediaFormat.KeyMime)
            ?? throw new InvalidOperationException("Audio track has no MIME type.");

        var sourceRate = format.GetInteger(MediaFormat.KeySampleRate);
        var channels = format.GetInteger(MediaFormat.KeyChannelCount);

        var startUs = startMs * 1_000;
        var endUs = (startMs + durationMs) * 1_000;

        using var codec = MediaCodec.CreateDecoderByType(mime)
            ?? throw new InvalidOperationException($"No decoder for '{mime}'.");

        codec.Configure(format, surface: null, crypto: null, flags: MediaCodecConfigFlags.None);
        codec.Start();

        try
        {
            // Seeking lands on the nearest sync point at or before the target, so the leading
            // samples are decoded and then dropped rather than assumed to start on time.
            extractor.SeekTo(startUs, MediaExtractorSeekTo.PreviousSync);

            var mono = DecodeRange(codec, extractor, channels, sourceRate, startUs, endUs, ct);
            return Resample(mono, sourceRate);
        }
        finally
        {
            codec.Stop();
        }
    }

    private static int? FindAudioTrack(MediaExtractor extractor)
    {
        for (var i = 0; i < extractor.TrackCount; i++)
        {
            var mime = extractor.GetTrackFormat(i).GetString(MediaFormat.KeyMime);
            if (mime?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true) return i;
        }

        return null;
    }

    private static List<float> DecodeRange(
        MediaCodec codec,
        MediaExtractor extractor,
        int channels,
        int sourceRate,
        long startUs,
        long endUs,
        CancellationToken ct)
    {
        var samples = new List<float>();
        var info = new MediaCodec.BufferInfo();
        var inputDone = false;

        // A decoder that stops producing output is a decoder in a state this loop cannot fix, and
        // waiting for it forever turns a bad probe into a hung alignment. Ten seconds of nothing is
        // far past any legitimate stall.
        var idle = 0;
        const int idleLimit = 1_000_000 / DequeueTimeoutUs * 10;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (!inputDone) inputDone = FeedInput(codec, extractor);

            var outputIndex = codec.DequeueOutputBuffer(info, DequeueTimeoutUs);

            if (outputIndex < 0)
            {
                if (++idle < idleLimit) continue;

                throw new TimeoutException("The audio decoder stopped producing output.");
            }

            idle = 0;

            if (info.Size > 0 && codec.GetOutputBuffer(outputIndex) is { } buffer)
                AppendMono(samples, buffer, info, channels, sourceRate, startUs, endUs);

            var reachedEnd = info.PresentationTimeUs >= endUs;
            var endOfStream = (info.Flags & MediaCodecBufferFlags.EndOfStream) != 0;

            codec.ReleaseOutputBuffer(outputIndex, render: false);

            if (reachedEnd || endOfStream) return samples;
        }
    }

    /// <returns>True once the end of the stream has been queued.</returns>
    private static bool FeedInput(MediaCodec codec, MediaExtractor extractor)
    {
        var index = codec.DequeueInputBuffer(DequeueTimeoutUs);
        if (index < 0) return false;

        var buffer = codec.GetInputBuffer(index);
        if (buffer is null) return false;

        var size = extractor.ReadSampleData(buffer, 0);
        if (size < 0)
        {
            codec.QueueInputBuffer(index, 0, 0, 0, MediaCodecBufferFlags.EndOfStream);
            return true;
        }

        codec.QueueInputBuffer(index, 0, size, extractor.SampleTime, 0);
        extractor.Advance();
        return false;
    }

    /// <summary>
    /// Converts one decoded buffer to mono floats, trimming whatever falls outside the requested
    /// range. Channels are averaged rather than one being picked, so a book mastered with the
    /// narration panned to one side does not come back silent.
    /// </summary>
    private static void AppendMono(
        List<float> samples,
        Java.Nio.ByteBuffer buffer,
        MediaCodec.BufferInfo info,
        int channels,
        int sourceRate,
        long startUs,
        long endUs)
    {
        var bytes = new byte[info.Size];
        buffer.Position(info.Offset);
        buffer.Get(bytes, 0, info.Size);
        buffer.Clear();

        var frames = bytes.Length / 2 / Math.Max(channels, 1);
        var usPerFrame = 1_000_000.0 / sourceRate;

        for (var frame = 0; frame < frames; frame++)
        {
            var at = info.PresentationTimeUs + (long)(frame * usPerFrame);
            if (at < startUs) continue;
            if (at >= endUs) return;

            var sum = 0f;
            for (var channel = 0; channel < channels; channel++)
            {
                var offset = (frame * channels + channel) * 2;
                var value = (short)(bytes[offset] | (bytes[offset + 1] << 8));
                sum += value / 32768f;
            }

            samples.Add(sum / channels);
        }
    }

    /// <summary>
    /// Resamples to 16 kHz by averaging each group of source samples that maps to one output
    /// sample. The averaging is a crude low-pass that keeps plain decimation from folding high
    /// frequencies down into the speech band — cheap, and more than the recognizer needs.
    /// </summary>
    private static float[] Resample(List<float> source, int sourceRate)
    {
        if (source.Count == 0) return [];
        if (sourceRate == TargetSampleRate) return [.. source];

        var ratio = sourceRate / (double)TargetSampleRate;
        var length = Math.Max(1, (int)(source.Count / ratio));
        var output = new float[length];

        for (var i = 0; i < length; i++)
        {
            var from = (int)(i * ratio);
            var to = Math.Min(source.Count, (int)((i + 1) * ratio));

            if (to <= from)
            {
                output[i] = source[Math.Min(from, source.Count - 1)];
                continue;
            }

            var sum = 0f;
            for (var j = from; j < to; j++) sum += source[j];
            output[i] = sum / (to - from);
        }

        return output;
    }
}
