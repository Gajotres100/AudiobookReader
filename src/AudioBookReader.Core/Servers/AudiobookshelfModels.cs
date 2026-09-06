using System.Text.Json.Serialization;

namespace AudioBookReader.Core.Servers;

// ---- What the app works with ----

public record ServerLibrary(string Id, string Name, string MediaType)
{
    /// <summary>Podcast libraries exist and are not books; nothing here can do anything with them.</summary>
    public bool IsBooks => MediaType is "book";
}

/// <param name="AudioFileCount">
/// Zero for a text-only title, one for the ordinary tagged container, more for a folder of
/// per-chapter files — which is the commonest shape on a server and the one the app cannot hold yet.
/// </param>
public record ServerBook(
    string Id,
    string Title,
    string? Author,
    int AudioFileCount,
    string? EbookFormat,
    double DurationSeconds)
{
    public bool HasAudio => AudioFileCount > 0;

    public bool HasEbook => !string.IsNullOrEmpty(EbookFormat);
}

/// <param name="Ino">The server's own handle for a file within an item; what a download is addressed by.</param>
public record ServerFile(string Ino, string FileName, double DurationSeconds);

/// <param name="Chapters">
/// The server's chapter marks, in seconds. Worth taking rather than re-reading from the file: the
/// server has already done that work, and for a book split across files these are the only marks
/// that describe the whole book rather than one part of it.
/// </param>
public record ServerBookDetail(
    ServerBook Book,
    IReadOnlyList<ServerFile> AudioFiles,
    ServerFile? Ebook,
    IReadOnlyList<ServerChapter> Chapters);

public record ServerChapter(double StartSeconds, double EndSeconds, string Title);

/// <param name="CurrentTimeSeconds">Where listening had reached, as the server last heard it.</param>
public record ServerProgress(double CurrentTimeSeconds, double DurationSeconds, bool IsFinished);

// ---- What the wire actually carries ----
//
// Kept separate from the records above so the app never handles the server's shapes directly. The
// wire types mirror Audiobookshelf exactly, nullable throughout, because a field being absent is
// ordinary: a text-only title has no audio files, a podcast library has no book metadata, and an
// item that has never been opened has no progress.

internal record LoginResponse([property: JsonPropertyName("user")] LoginUser? User);

internal record LoginUser([property: JsonPropertyName("token")] string? Token);

internal record LibrariesResponse([property: JsonPropertyName("libraries")] List<WireLibrary>? Libraries);

internal record WireLibrary(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("mediaType")] string? MediaType);

internal record ItemsResponse(
    [property: JsonPropertyName("results")] List<WireItem>? Results,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("limit")] int Limit);

internal record WireItem(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("media")] WireMedia? Media);

internal record WireMedia(
    [property: JsonPropertyName("metadata")] WireMetadata? Metadata,
    [property: JsonPropertyName("numAudioFiles")] int? NumAudioFiles,
    [property: JsonPropertyName("ebookFormat")] string? EbookFormat,
    [property: JsonPropertyName("ebookFileFormat")] string? EbookFileFormat,
    [property: JsonPropertyName("duration")] double? Duration,
    [property: JsonPropertyName("audioFiles")] List<WireAudioFile>? AudioFiles,
    [property: JsonPropertyName("ebookFile")] WireLibraryFile? EbookFile,
    [property: JsonPropertyName("chapters")] List<WireChapter>? Chapters);

internal record WireMetadata(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("authorName")] string? AuthorName);

internal record WireAudioFile(
    [property: JsonPropertyName("ino")] string? Ino,
    [property: JsonPropertyName("duration")] double? Duration,
    [property: JsonPropertyName("metadata")] WireFileMetadata? Metadata);

internal record WireLibraryFile(
    [property: JsonPropertyName("ino")] string? Ino,
    [property: JsonPropertyName("metadata")] WireFileMetadata? Metadata);

internal record WireFileMetadata(
    [property: JsonPropertyName("filename")] string? Filename,
    [property: JsonPropertyName("ext")] string? Ext);

internal record WireChapter(
    [property: JsonPropertyName("start")] double Start,
    [property: JsonPropertyName("end")] double End,
    [property: JsonPropertyName("title")] string? Title);

internal record WireProgress(
    [property: JsonPropertyName("currentTime")] double? CurrentTime,
    [property: JsonPropertyName("duration")] double? Duration,
    [property: JsonPropertyName("isFinished")] bool? IsFinished);

internal record ProgressUpdate(
    [property: JsonPropertyName("currentTime")] double CurrentTime,
    [property: JsonPropertyName("duration")] double Duration,
    [property: JsonPropertyName("progress")] double Progress,
    [property: JsonPropertyName("isFinished")] bool IsFinished);

internal record Credentials(
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("password")] string Password);
