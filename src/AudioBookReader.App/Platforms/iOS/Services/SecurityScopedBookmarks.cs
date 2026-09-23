using System.Collections.Concurrent;
using Foundation;

namespace AudioBookReader.App.Services;

/// <summary>
/// Keeps the one thing Android does not need a place for. On Android a persistable SAF grant is
/// keyed by the URI itself, so a "content://…" string is the whole story. iOS has nothing keyed
/// like that: a security-scoped bookmark is a blob that has to be created while access is fresh,
/// stored somewhere, and resolved again — with access started — before the file can be touched in
/// a later session. This is that somewhere: a (path → bookmark) table in Preferences, keyed by the
/// same path the database stores, the pattern <see cref="DownloadFolder"/> already uses.
///
/// A folder can be bookmarked too (the download folder). Files written inside it carry no bookmark
/// of their own; access to them comes from resolving the folder's.
/// </summary>
internal static class SecurityScopedBookmarks
{
    private const string KeyPrefix = "iosbookmark.";
    private const string FoldersKey = "iosbookmark.folders";

    /// <summary>
    /// Locations whose access has already been started in this process, with the URL to read them
    /// through. Starting access is counted, so each location is started once and kept for the life
    /// of the process rather than started again on every read — alignment reads the same file in
    /// short bursts for hours.
    /// </summary>
    private static readonly ConcurrentDictionary<string, NSUrl> Active = new();

    /// <summary>
    /// Bookmarks a URL a picker has just handed over. False when the system refused.
    ///
    /// Access is started first: a picker's URL for a document left in place can be neither read
    /// nor bookmarked until it is. It is then kept started for this session, because the importer
    /// reads the file straight away — and if the bookmark itself failed, that same session access
    /// is what still lets the file be copied in instead.
    /// </summary>
    public static bool Save(NSUrl url, bool isFolder = false)
    {
        var path = url.Path;
        if (string.IsNullOrEmpty(path)) return false;

        if (url.StartAccessingSecurityScopedResource()) Active[path] = url;

        // WithSecurityScope is a macOS App Sandbox option and does not exist on iOS; there, the
        // scope comes from how the picker granted the URL, and starting access on resolve is enough.
        var data = url.CreateBookmarkData(default, [], null, out var error);

        if (error is not null || data is null)
        {
            AppLog.Error($"bookmarking {path}", new Exception(error?.LocalizedDescription ?? "no data"));
            return false;
        }

        Store(path, data);
        if (isFolder) AddFolder(path);

        return true;
    }

    /// <summary>
    /// Starts access to a location if it is — or lies inside — something bookmarked, and returns the
    /// URL to read it through. Null for an ordinary path the app owns, which needs none of this.
    /// </summary>
    public static NSUrl? Access(string path)
    {
        if (Active.TryGetValue(path, out var known)) return known;

        if (Resolve(path) is { } own) return Active[path] = own;

        foreach (var folder in Folders())
        {
            if (!IsInside(path, folder)) continue;

            if (!Active.ContainsKey(folder))
            {
                if (Resolve(folder) is not { } resolved) continue;
                Active[folder] = resolved;
            }

            return Active[path] = NSUrl.FromFilename(path);
        }

        return null;
    }

    /// <summary>
    /// The URL AVFoundation should open a stored location through, with access started when it
    /// needs it. Every path-based reader goes through this — the player, the decoder, the metadata
    /// fallback — because a referenced file opened by path alone works in the session it was
    /// picked in and fails in every session after, once nothing has started its access.
    /// </summary>
    public static NSUrl UrlFor(string location) =>
        Access(location)
        ?? (location.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
            ? NSUrl.FromString(location)!
            : NSUrl.FromFilename(location));

    /// <summary>True when the location is bookmarked, or inside a bookmarked folder. Starts nothing.</summary>
    public static bool Covers(string path) =>
        Preferences.Default.ContainsKey(KeyPrefix + path) || Folders().Any(f => IsInside(path, f));

    public static void Forget(string path)
    {
        Preferences.Default.Remove(KeyPrefix + path);

        if (Active.TryRemove(path, out var url)) url.StopAccessingSecurityScopedResource();

        var folders = Folders().Where(f => f != path).ToList();
        Preferences.Default.Set(FoldersKey, string.Join('\n', folders));
    }

    private static NSUrl? Resolve(string path)
    {
        var stored = Preferences.Default.Get<string?>(KeyPrefix + path, null);
        if (stored is null) return null;

        var url = NSUrl.FromBookmarkData(
            NSData.FromArray(Convert.FromBase64String(stored)), default, null, out var stale, out var error);

        if (error is not null || url is null)
        {
            AppLog.Error($"resolving the bookmark for {path}",
                new Exception(error?.LocalizedDescription ?? "no url"));
            return null;
        }

        url.StartAccessingSecurityScopedResource();

        // A stale bookmark still resolved this once — the file moved, but the system could still
        // find it. It is refreshed under the path the database knows it by, not the new one, or the
        // next lookup by that path would find nothing.
        if (stale && url.CreateBookmarkData(default, [], null, out var refreshError) is { } fresh
            && refreshError is null)
        {
            Store(path, fresh);
        }

        return url;
    }

    private static void Store(string path, NSData data) =>
        Preferences.Default.Set(KeyPrefix + path, Convert.ToBase64String(data.ToArray()));

    private static IEnumerable<string> Folders() =>
        Preferences.Default.Get(FoldersKey, "").Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static void AddFolder(string path)
    {
        var folders = Folders().Append(path).Distinct().ToList();
        Preferences.Default.Set(FoldersKey, string.Join('\n', folders));
    }

    private static bool IsInside(string path, string folder) =>
        path.StartsWith(folder.TrimEnd('/') + "/", StringComparison.Ordinal);
}
