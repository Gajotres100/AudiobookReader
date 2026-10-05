namespace AudioBookReader.App.Services;

/// <summary>
/// Audio that stays on an Audiobookshelf server and is played from there.
///
/// A book's audio location is normally a path or a <c>content://</c> reference to a file on the
/// device. A streamed book has neither: its location is <c>abs://server/item/file</c>, naming which
/// of the configured servers, the item and the file on it, and only turned into an address — with a
/// token that is current at that moment — when something actually needs the bytes. Keeping the
/// address out of the stored location is what lets the server move or the token change without
/// every streamed book breaking.
///
/// Books streamed before more than one server could be configured carry <c>abs://item/file</c>,
/// without the server; those belong to the first one, which is where they came from.
///
/// To the rest of the app it is a reference like any other: owned by someone else, never deleted,
/// and readable as a stream — which is also how a streamed book gets copied in if it is ever aligned.
///
/// The narration of an EPUB 3 on the server is a stretch of the package file rather than a file of
/// its own, and carries that stretch after the file: <c>abs://server/item/file#slice=start-length</c>,
/// in bytes. Everything that reads it — the players, hashing, probing — sees just that stretch, as if
/// it were the whole file.
/// </summary>
public static class StreamedAudio
{
    private const string Scheme = "abs://";
    private const string SliceMark = "#slice=";

    /// <summary>What the stretch is called on an address handed to a player, which reads it off again.</summary>
    public const string UrlSliceMark = "#abr-slice=";

    public static string Location(string serverId, string itemId, string ino) =>
        $"{Scheme}{Uri.EscapeDataString(serverId)}/{Uri.EscapeDataString(itemId)}/{Uri.EscapeDataString(ino)}";

    /// <summary>A stretch of a file on the server, played as if it were a file of its own.</summary>
    public static string Location(string serverId, string itemId, string ino, long offset, long length) =>
        $"{Location(serverId, itemId, ino)}{SliceMark}{offset}-{length}";

    /// <summary>The stretch a location names, when it names one.</summary>
    public static bool TrySlice(string? location, out long offset, out long length) =>
        TryReadSlice(location, SliceMark, out offset, out length);

    /// <summary>The same, from an address a player was given.</summary>
    public static bool TrySliceOfUrl(string? url, out long offset, out long length) =>
        TryReadSlice(url, UrlSliceMark, out offset, out length);

    /// <summary>An address with the stretch taken off: what is actually asked of the server.</summary>
    public static string WithoutSlice(string url)
    {
        var mark = url.IndexOf(UrlSliceMark, StringComparison.Ordinal);
        return mark < 0 ? url : url[..mark];
    }

    private static bool TryReadSlice(string? text, string mark, out long offset, out long length)
    {
        offset = length = 0;

        var at = text?.IndexOf(mark, StringComparison.Ordinal) ?? -1;
        if (at < 0) return false;

        var parts = text![(at + mark.Length)..].Split('-');
        return parts.Length == 2
               && long.TryParse(parts[0], out offset) && long.TryParse(parts[1], out length)
               && offset >= 0 && length > 0;
    }

    public static bool Is(string? location) =>
        location?.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase) == true;

    public static bool TryParse(string? location, out string serverId, out string itemId, out string ino)
    {
        serverId = itemId = ino = "";
        if (!Is(location)) return false;

        var body = location![Scheme.Length..];
        var hash = body.IndexOf('#');
        if (hash >= 0) body = body[..hash];

        var parts = body.Split('/');

        switch (parts.Length)
        {
            case 2:
                serverId = ServerAccount.LegacyId;
                itemId = Uri.UnescapeDataString(parts[0]);
                ino = Uri.UnescapeDataString(parts[1]);
                break;

            case 3:
                serverId = Uri.UnescapeDataString(parts[0]);
                itemId = Uri.UnescapeDataString(parts[1]);
                ino = Uri.UnescapeDataString(parts[2]);
                break;

            default:
                return false;
        }

        return serverId.Length > 0 && itemId.Length > 0 && ino.Length > 0;
    }

    /// <summary>
    /// Supplied by the server connections once they exist, so that code with no business knowing
    /// about servers — the file references, the player — can still reach the bytes. A static rather
    /// than an injected service because the connections depend on the importer, which depends on the
    /// file references, and injecting them there would close the loop.
    /// </summary>
    internal static IStreamSource? Source { get; set; }
}

/// <summary>What the server connections offer to the parts of the app that play or copy streamed audio.</summary>
internal interface IStreamSource
{
    /// <summary>The address to play from, without credentials.</summary>
    string? UrlFor(string location);

    /// <summary>
    /// An access token good for a while yet for the server behind <paramref name="address"/> — a
    /// streamed location, or an address worked out from one — or null when signed out.
    /// </summary>
    Task<string?> FreshTokenAsync(string address, CancellationToken ct = default);

    /// <summary>The file as a seekable stream.</summary>
    Task<Stream> OpenAsync(string location, CancellationToken ct = default);
}
