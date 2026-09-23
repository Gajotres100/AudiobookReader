using AVFoundation;
using AudioBookReader.Core.Audio;
using AudioBookReader.Core.Models;
using Foundation;

namespace AudioBookReader.App.Services;

/// <summary>
/// AVAsset/AVURLAsset.CommonMetadata — the iOS fallback for files the tag library (ATL) cannot
/// read, same role as Android's MediaMetadataRetriever here. Cannot read chapter marks any more
/// than the Android fallback can, for the same reason: nothing at this level exposes them.
/// </summary>
public static partial class AudioMetadata
{
    public static partial Task<AudioBookInfo?> ReadAsync(string location, string fileName) =>
        Task.Run(() => Read(location, fileName));

    private static AudioBookInfo? Read(string location, string fileName)
    {
        try
        {
            var asset = new AVUrlAsset(SecurityScopedBookmarks.UrlFor(location));

            var durationMs = asset.Duration is { IsInvalid: false, IsIndefinite: false } duration
                ? (long)(duration.Seconds * 1000)
                : 0;

            if (durationMs <= 0) return null;

            string? Common(string key) =>
                asset.CommonMetadata.FirstOrDefault(m => m.CommonKey == key)?.StringValue is { } value
                && !string.IsNullOrWhiteSpace(value)
                    ? value.Trim()
                    : null;

            var title = Common("albumName") ?? Common("title");
            var author = Common("artist");

            var artwork = asset.CommonMetadata
                .FirstOrDefault(m => m.CommonKey == "artwork")?.DataValue?.ToArray();

            AppLog.Info(
                $"metadata fallback: '{Path.GetFileName(fileName)}' is {durationMs} ms, " +
                "no chapter marks (the platform extractor does not report them)");

            return new AudioBookInfo(
                Title: title ?? Path.GetFileNameWithoutExtension(fileName),
                Author: author,
                DurationMs: durationMs,
                Chapters: [new Chapter { Index = 0, Title = "", StartMs = 0, EndMs = durationMs }],
                Files: [location],
                Cover: artwork,
                CoverMimeType: artwork is null ? null : "image/jpeg");
        }
        catch (Exception ex)
        {
            AppLog.Info(
                $"metadata fallback failed for '{Path.GetFileName(fileName)}': {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
