using AudioToolbox;
using AVFoundation;
using CoreMedia;
using Foundation;

namespace AudioBookReader.App.Platforms.iOS.Alignment;

/// <summary>
/// Decodes a stretch of an audiobook into the 16 kHz mono float samples the recognizer expects —
/// the iOS counterpart of Android's MediaCodec-based decoder, built on AVAssetReader instead.
///
/// Simpler than the Android version in one respect: AVAssetReaderTrackOutput can be asked to
/// deliver already-resampled, already-downmixed 16 kHz mono PCM directly from the hardware decoder,
/// so there is no manual box-filter resample step here — AVFoundation's own converter does it.
///
/// Harder in another: an AVAssetReader is single-use. Once StartReading() has run to completion or
/// failure it cannot be rewound or reused, unlike a MediaCodec that can be flushed and re-seeked
/// indefinitely. <see cref="Session"/> therefore caches only what genuinely is reusable — the
/// AVUrlAsset and its resolved audio track — and builds a fresh reader per probe. That is not free,
/// but it is the container parse Android's own comment says was the expensive part, and that half
/// of the cost is still avoided by keeping the asset and track alive across probes.
/// </summary>
internal static class AudioPcmDecoder
{
    public const int TargetSampleRate = 16_000;

    internal sealed class Session : IDisposable
    {
        public string Path { get; }

        private readonly AVUrlAsset _asset;
        private readonly AVAssetTrack _track;

        private Session(string path, AVUrlAsset asset, AVAssetTrack track)
        {
            Path = path;
            _asset = asset;
            _track = track;
        }

        public static Session Open(string path)
        {
            var asset = new AVUrlAsset(ResolveUrl(path));
            var track = asset.GetTracks(AVMediaTypes.Audio).FirstOrDefault()
                ?? throw new InvalidOperationException($"No audio track in '{path}'.");

            return new Session(path, asset, track);
        }

        public float[] Decode(long startMs, long durationMs, CancellationToken ct) =>
            DecodeRange(_asset, _track, startMs, durationMs, ct);

        // AVUrlAsset and AVAssetTrack need no explicit teardown — unlike MediaCodec, there is no
        // hardware resource claimed until a reader actually starts reading.
        public void Dispose() { }
    }

    private static NSUrl ResolveUrl(string path) => AudioBookReader.App.Services.SecurityScopedBookmarks.UrlFor(path);

    public static float[] Decode(string path, long startMs, long durationMs, CancellationToken ct)
    {
        var asset = new AVUrlAsset(ResolveUrl(path));
        var track = asset.GetTracks(AVMediaTypes.Audio).FirstOrDefault()
            ?? throw new InvalidOperationException($"No audio track in '{path}'.");

        return DecodeRange(asset, track, startMs, durationMs, ct);
    }

    private static float[] DecodeRange(
        AVUrlAsset asset, AVAssetTrack track, long startMs, long durationMs, CancellationToken ct)
    {
        var settings = new AudioSettings
        {
            Format = AudioFormatType.LinearPCM,
            SampleRate = TargetSampleRate,
            NumberChannels = 1,
            LinearPcmBitDepth = 16,
            LinearPcmFloat = false,
            LinearPcmBigEndian = false,
            LinearPcmNonInterleaved = false,
        };

        var output = new AVAssetReaderTrackOutput(track, settings);

        var reader = new AVAssetReader(asset, out var openError);
        if (openError is not null)
            throw new InvalidOperationException($"Could not open '{asset.Url}': {openError.LocalizedDescription}");

        // Only the requested range is decoded, never the whole file — the same reasoning as
        // Android: a ten-hour book at 16 kHz would be gigabytes of samples, and alignment only
        // ever listens to short probes. Must be set before StartReading().
        reader.TimeRange = new CMTimeRange
        {
            Start = CMTime.FromSeconds(startMs / 1000.0, 1000),
            Duration = CMTime.FromSeconds(durationMs / 1000.0, 1000),
        };

        if (!reader.CanAddOutput(output))
            throw new InvalidOperationException("This audio track cannot be read as 16 kHz mono PCM.");

        reader.AddOutput(output);

        if (!reader.StartReading())
            throw new InvalidOperationException($"Could not start reading: {reader.Error?.LocalizedDescription}");

        var samples = new List<float>();

        while (reader.Status == AVAssetReaderStatus.Reading)
        {
            ct.ThrowIfCancellationRequested();

            using var sample = output.CopyNextSampleBuffer();
            if (sample is null) break;

            using var block = sample.GetDataBuffer();
            if (block is null || block.IsEmpty) continue;

            // Already 16-bit signed little-endian mono at the target rate, per the AudioSettings
            // above — nothing left to convert but the sample encoding itself.
            block.CopyDataBytes(UIntPtr.Zero, block.DataLength, out byte[]? bytes);
            if (bytes is not null) AppendMono(samples, bytes);
        }

        if (reader.Status == AVAssetReaderStatus.Failed)
            throw new InvalidOperationException($"Decoding failed: {reader.Error?.LocalizedDescription}");

        return [.. samples];
    }

    private static void AppendMono(List<float> samples, byte[] bytes)
    {
        for (var i = 0; i + 1 < bytes.Length; i += 2)
        {
            var value = (short)(bytes[i] | (bytes[i + 1] << 8));
            samples.Add(value / 32768f);
        }
    }
}
