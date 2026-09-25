namespace AudioBookReader.App.Services;

/// <summary>
/// Audio that stays on the Audiobookshelf server and is played from there.
///
/// A book's audio location is normally a path or a <c>content://</c> reference to a file on the
/// device. A streamed book has neither: its location is <c>abs://item/file</c>, naming the item and
/// the file on the server, and only turned into an address — with a token that is current at that
/// moment — when something actually needs the bytes. Keeping the address out of the stored location
/// is what lets the server move or the token change without every streamed book breaking.
///
/// To the rest of the app it is a reference like any other: owned by someone else, never deleted,
/// and readable as a stream — which is also how a streamed book gets copied in if it is ever aligned.
/// </summary>
public static class StreamedAudio
{
    private const string Scheme = "abs://";

    public static string Location(string itemId, string ino) =>
        $"{Scheme}{Uri.EscapeDataString(itemId)}/{Uri.EscapeDataString(ino)}";

    public static bool Is(string? location) =>
        location?.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase) == true;

    public static bool TryParse(string? location, out string itemId, out string ino)
    {
        itemId = ino = "";
        if (!Is(location)) return false;

        var parts = location![Scheme.Length..].Split('/');
        if (parts.Length != 2) return false;

        itemId = Uri.UnescapeDataString(parts[0]);
        ino = Uri.UnescapeDataString(parts[1]);

        return itemId.Length > 0 && ino.Length > 0;
    }

    /// <summary>
    /// Supplied by the server connection once it exists, so that code with no business knowing about
    /// servers — the file references, the player — can still reach the bytes. A static rather than an
    /// injected service because the connection itself depends on the importer, which depends on the
    /// file references, and injecting it there would close the loop.
    /// </summary>
    internal static IStreamSource? Source { get; set; }
}

/// <summary>What the server connection offers to the parts of the app that play or copy streamed audio.</summary>
internal interface IStreamSource
{
    /// <summary>The address to play from, without credentials.</summary>
    string? UrlFor(string location);

    /// <summary>An access token good for a while yet, or null when signed out.</summary>
    Task<string?> FreshTokenAsync(CancellationToken ct = default);

    /// <summary>The file as a seekable stream.</summary>
    Task<Stream> OpenAsync(string location, CancellationToken ct = default);
}
