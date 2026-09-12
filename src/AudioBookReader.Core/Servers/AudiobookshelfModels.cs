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
/// <param name="Series">
/// The series this belongs to, without the number — "The Raven's Mark". The server sends name and
/// sequence as one string, and comma-separated when a book is in more than one; only the first is
/// kept, because a shelf can only stand a book in one place.
/// </param>
/// <param name="Sequence">Its place in that series, as written — "1", "2.5", or empty.</param>
/// <param name="AddedAt">When the server first saw it, for the recently-added shelf.</param>
public record ServerBook(
    string Id,
    string Title,
    string? Author,
    int AudioFileCount,
    string? EbookFormat,
    double DurationSeconds,
    string? Series = null,
    string? Sequence = null,
    DateTimeOffset? AddedAt = null)
{
    public bool HasAudio => AudioFileCount > 0;

    public bool HasEbook => !string.IsNullOrEmpty(EbookFormat);

    public bool InSeries => !string.IsNullOrEmpty(Series);

    /// <summary>
    /// Splits what the server sends into a name and a place in it.
    ///
    /// The wire carries "The Raven's Mark #1", and several of those comma-separated when a book
    /// belongs to more than one series. Sorting a shelf needs the two apart, and the number has to
    /// sort as a number: "#10" belongs after "#9", which it does not as text.
    /// </summary>
    public static (string? Series, string? Sequence) SplitSeries(string? seriesName)
    {
        if (string.IsNullOrWhiteSpace(seriesName)) return (null, null);

        var first = seriesName.Split(',')[0].Trim();
        var hash = first.LastIndexOf('#');

        return hash < 0
            ? (first, null)
            : (first[..hash].Trim(), first[(hash + 1)..].Trim());
    }

    /// <summary>Its place in the series as a number, for ordering. Unnumbered books go last.</summary>
    public double SequenceOrder =>
        double.TryParse(Sequence, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : double.MaxValue;
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

internal record LoginUser(
    [property: JsonPropertyName("token")] string? Token,
    [property: JsonPropertyName("accessToken")] string? AccessToken,
    [property: JsonPropertyName("refreshToken")] string? RefreshToken);

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
    [property: JsonPropertyName("addedAt")] long? AddedAt,
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

/// <param name="AuthorName">
/// The authors as one string. Present in the short form the library listing returns.
/// </param>
/// <param name="Authors">
/// The authors one by one. This is what a single item returns when asked for in full, and the
/// short field is then absent — which is why the shelf showed an author and the book's own page
/// did not, and why a downloaded book landed in a folder called "Unknown author". Reading both
/// costs nothing and stops the answer depending on which call it came from.
/// </param>
internal record WireMetadata(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("authorName")] string? AuthorName,
    [property: JsonPropertyName("seriesName")] string? SeriesName,
    [property: JsonPropertyName("authors")] List<WireAuthor>? Authors = null)
{
    /// <summary>The authors, however this particular reply chose to say them.</summary>
    public string? Author => string.IsNullOrWhiteSpace(AuthorName)
        ? Join(Authors?.Select(a => a.Name))
        : AuthorName;

    private static string? Join(IEnumerable<string?>? names)
    {
        var kept = names?.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        return kept is { Count: > 0 } ? string.Join(", ", kept) : null;
    }
}

internal record WireAuthor(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("name")] string? Name);

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
