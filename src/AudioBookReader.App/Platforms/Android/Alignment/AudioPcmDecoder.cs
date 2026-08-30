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

    public static float[] Decode(string path, long startMs, long durationMs, CancellationToken ct)
    {
        using var extractor = new MediaExtractor();
        extractor.SetDataSource(path);

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

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (!inputDone) inputDone = FeedInput(codec, extractor);

            var outputIndex = codec.DequeueOutputBuffer(info, DequeueTimeoutUs);
            if (outputIndex < 0) continue;

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
