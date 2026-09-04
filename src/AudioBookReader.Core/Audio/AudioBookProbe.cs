using ATL;
using AudioBookReader.Core.Models;
using Chapter = AudioBookReader.Core.Models.Chapter;

namespace AudioBookReader.Core.Audio;

/// <summary>What import learned about an audiobook before it was written to the library.</summary>
public record AudioBookInfo(
    string Title,
    string? Author,
    long DurationMs,
    IReadOnlyList<Chapter> Chapters,
    IReadOnlyList<string> Files,
    byte[]? Cover,
    string? CoverMimeType)
{
    /// <summary>True when the book is a folder of per-chapter files rather than one tagged container.</summary>
    public bool IsMultiFile => Files.Count > 1;
}

/// <summary>
/// Reads title, duration, cover and the chapter list from an audiobook, which may be either a
/// single tagged container (typically .m4b) or a folder of per-chapter files.
/// </summary>
public static class AudioBookProbe
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".m4b", ".m4a", ".mp3", ".aac", ".ogg", ".opus", ".flac", ".wav", ".wma",
    };

    public static bool IsSupportedAudioFile(string path) =>
        AudioExtensions.Contains(Path.GetExtension(path));

    public static Task<AudioBookInfo> ProbeAsync(string path, CancellationToken ct = default) =>
        Task.Run(() => Directory.Exists(path) ? ProbeFolder(path) : ProbeFile(path), ct);

    /// <summary>
    /// Reads the same information from an open stream.
    ///
    /// A book the user keeps where it is — referenced rather than copied into app storage — has no
    /// path this code can open, only a stream from the platform. Chapters are the reason this goes
    /// through the tag reader rather than the system's metadata service: chapter marks are what
    /// makes an audiobook navigable, and nothing else reports them.
    /// </summary>
    /// <param name="fileName">Used for the extension, which tells the tag reader what it is holding.</param>
    public static Task<AudioBookInfo> ProbeAsync(
        Stream stream,
        string fileName,
        CancellationToken ct = default) =>
        Task.Run(() => ProbeStream(stream, fileName), ct);

    private static AudioBookInfo ProbeStream(Stream stream, string fileName)
    {
        var track = new Track(stream, Path.GetExtension(fileName));
        return Describe(track, fileName, fileName);
    }

    private static AudioBookInfo ProbeFile(string path) => Describe(new Track(path), path, path);

    private static AudioBookInfo Describe(Track track, string reference, string nameSource)
    {
        var durationMs = (long)Math.Round(track.DurationMs);

        var chapters = BuildChapters(track, durationMs);
        var (cover, mime) = ExtractCover(track);

        return new AudioBookInfo(
            Title: FirstNonEmpty(track.Album, track.Title, Path.GetFileNameWithoutExtension(nameSource)),
            Author: FirstNonEmpty(track.AlbumArtist, track.Artist, track.Composer),
            DurationMs: durationMs,
            Chapters: chapters,
            Files: [reference],
            Cover: cover,
            CoverMimeType: mime);
    }

    /// <summary>
    /// Treats a folder as one book with a chapter per file, in natural filename order.
    /// Chapter times are cumulative across files, so the rest of the app can keep treating a
    /// book as a single timeline regardless of how it is stored on disk.
    /// </summary>
    private static AudioBookInfo ProbeFolder(string directory)
    {
        var files = Directory.EnumerateFiles(directory)
            .Where(IsSupportedAudioFile)
            .OrderBy(f => Path.GetFileName(f), NaturalStringComparer.Instance)
            .ToList();

        if (files.Count == 0)
            throw new InvalidOperationException($"No supported audio files in '{directory}'.");

        var chapters = new List<Chapter>(files.Count);
        byte[]? cover = null;
        string? mime = null;
        string? title = null;
        string? author = null;
        var cursorMs = 0L;

        for (var i = 0; i < files.Count; i++)
        {
            var track = new Track(files[i]);
            var lengthMs = (long)Math.Round(track.DurationMs);

            chapters.Add(new Chapter
            {
                Index = i,
                Title = FirstNonEmpty(track.Title, Path.GetFileNameWithoutExtension(files[i])),
                StartMs = cursorMs,
                EndMs = cursorMs + lengthMs,
            });

            cursorMs += lengthMs;

            title ??= FirstNonEmptyOrNull(track.Album);
            author ??= FirstNonEmptyOrNull(track.AlbumArtist, track.Artist);
            if (cover is null) (cover, mime) = ExtractCover(track);
        }

        return new AudioBookInfo(
            Title: title ?? new DirectoryInfo(directory).Name,
            Author: author,
            DurationMs: cursorMs,
            Chapters: chapters,
            Files: files,
            Cover: cover,
            CoverMimeType: mime);
    }

    /// <summary>
    /// Turns ATL's chapter list into a gapless, ordered list covering the whole file.
    ///
    /// Chapter metadata in the wild is unreliable: several formats record only start times, some
    /// leave <c>EndTime</c> at zero, and entries are not always ordered. Anything downstream —
    /// next/previous chapter, the alignment work queue, "listen to end of chapter" — assumes a
    /// clean timeline, so it is repaired once, here.
    /// </summary>
    private static List<Chapter> BuildChapters(Track track, long durationMs)
    {
        var raw = (track.Chapters ?? [])
            .Where(c => c.StartTime < durationMs)
            .OrderBy(c => c.StartTime)
            .ToList();

        if (raw.Count == 0)
        {
            // Untagged file: the whole book is one chapter, which still gives the player and the
            // aligner a valid unit of work.
            return
            [
                new Chapter
                {
                    Index = 0,
                    Title = FirstNonEmpty(track.Title, "Chapter 1"),
                    StartMs = 0,
                    EndMs = durationMs,
                }
            ];
        }

        var chapters = new List<Chapter>(raw.Count);
        for (var i = 0; i < raw.Count; i++)
        {
            var start = (long)raw[i].StartTime;
            var declaredEnd = (long)raw[i].EndTime;
            var nextStart = i + 1 < raw.Count ? (long)raw[i + 1].StartTime : durationMs;

            // Trust the declared end only when it is sane; otherwise run to the next chapter.
            var end = declaredEnd > start && declaredEnd <= nextStart ? declaredEnd : nextStart;

            chapters.Add(new Chapter
            {
                Index = i,
                Title = FirstNonEmpty(raw[i].Title, $"Chapter {i + 1}"),
                StartMs = start,
                EndMs = end,
            });
        }

        return chapters;
    }

    private static (byte[]? Data, string? MimeType) ExtractCover(Track track)
    {
        var pictures = track.EmbeddedPictures;
        if (pictures is null || pictures.Count == 0) return (null, null);

        var picture = pictures.FirstOrDefault(p => p.PicType == PictureInfo.PIC_TYPE.Front)
                      ?? pictures[0];

        return picture.PictureData is { Length: > 0 } data ? (data, picture.MimeType) : (null, null);
    }

    private static string FirstNonEmpty(params string?[] candidates) =>
        FirstNonEmptyOrNull(candidates) ?? "";

    private static string? FirstNonEmptyOrNull(params string?[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))?.Trim();
}
