using Android.Media;
using AudioBookReader.Core.Audio;
using AudioBookReader.Core.Models;
using AndroidUri = Android.Net.Uri;

namespace AudioBookReader.App.Services;

public static partial class AudioMetadata
{
    public static partial Task<AudioBookInfo?> ReadAsync(string location, string fileName) =>
        Task.Run(() => Read(location, fileName));

    private static AudioBookInfo? Read(string location, string fileName)
    {
        using var retriever = new MediaMetadataRetriever();

        global::Android.OS.ParcelFileDescriptor? descriptor = null;

        try
        {
            // A book is either kept in app storage or referenced where the user has it, so the
            // location is either a path or a content URI.
            //
            // The URI form goes through a file descriptor rather than through the resolver. These
            // books are hundreds of megabytes with their metadata atom at the very end — not
            // fast-started — so reading it means seeking to the end and back, and a descriptor is
            // the one handle that reliably seeks.
            if (location.StartsWith("content://", StringComparison.OrdinalIgnoreCase))
            {
                var context = global::Android.App.Application.Context;

                descriptor = context.ContentResolver?.OpenFileDescriptor(AndroidUri.Parse(location)!, "r")
                             ?? throw new IOException($"Ne mogu otvoriti '{location}'.");

                retriever.SetDataSource(descriptor.FileDescriptor);
            }
            else
            {
                retriever.SetDataSource(location);
            }

            var durationMs = Length(retriever);

            AppLog.Info(
                $"metadata fallback: duration '{retriever.ExtractMetadata(MetadataKey.Duration)}', " +
                $"album '{retriever.ExtractMetadata(MetadataKey.Album)}', " +
                $"title '{retriever.ExtractMetadata(MetadataKey.Title)}', " +
                $"mime '{retriever.ExtractMetadata(MetadataKey.Mimetype)}'");

            if (durationMs <= 0) return null;

            var title = Text(retriever, MetadataKey.Album) ?? Text(retriever, MetadataKey.Title);
            var author = Text(retriever, MetadataKey.Albumartist) ?? Text(retriever, MetadataKey.Artist);

            AppLog.Info(
                $"metadata fallback: '{Path.GetFileName(fileName)}' is {durationMs} ms, " +
                "no chapter marks (the platform extractor does not report them)");

            return new AudioBookInfo(
                Title: title ?? Path.GetFileNameWithoutExtension(fileName),
                Author: author,
                DurationMs: durationMs,
                // One chapter, the whole book. Honest: the platform has no chapter marks to give,
                // and an empty list would leave the book with no way to navigate at all.
                Chapters: [new Chapter { Index = 0, Title = "", StartMs = 0, EndMs = durationMs }],
                Files: [location],
                Cover: retriever.GetEmbeddedPicture(),
                CoverMimeType: "image/jpeg");
        }
        catch (Exception ex)
        {
            // Both the managed reader and the platform have now refused it, so it is not audio this
            // device can play whatever it is called.
            AppLog.Info(
                $"metadata fallback failed for '{Path.GetFileName(fileName)}': " +
                $"{ex.GetType().Name}: {ex.Message}");

            return null;
        }
        finally
        {
            descriptor?.Close();
            descriptor?.Dispose();
        }
    }

    private static long Length(MediaMetadataRetriever retriever) =>
        long.TryParse(retriever.ExtractMetadata(MetadataKey.Duration), out var ms) ? ms : 0;

    private static string? Text(MediaMetadataRetriever retriever, MetadataKey key)
    {
        var value = retriever.ExtractMetadata(key);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
