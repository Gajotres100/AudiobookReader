using Foundation;

namespace AudioBookReader.App.Services;

/// <summary>
/// Keeps the one thing Android does not need a place for: on Android, a persistable SAF URI
/// permission is a system-wide grant keyed by the URI itself, so a "content://…" string is the
/// whole story. iOS has nothing that keyed — a security-scoped bookmark is a blob that has to be
/// created once, while access is fresh, and stored somewhere the app can find again by the same
/// path it already keeps in the database. This is that somewhere: a small (path -> bookmark)
/// table in Preferences, following the same pattern <see cref="DownloadFolder"/> already uses to
/// remember the chosen download folder.
/// </summary>
internal static class SecurityScopedBookmarks
{
    private const string KeyPrefix = "iosbookmark.";

    /// <summary>Creates and stores a bookmark for a URL the system has just granted access to (from a picker's own callback). False when the system refused.</summary>
    public static bool Save(NSUrl url)
    {
        // WithSecurityScope is a macOS App Sandbox option and does not exist on iOS — there,
        // scoped access comes implicitly from how the document picker grants the URL in the first
        // place, and StartAccessingSecurityScopedResource() (called on resolve, below) is enough.
        var data = url.CreateBookmarkData(default, [], null, out var error);

        if (error is not null || data is null)
        {
            AppLog.Error($"creating a security-scoped bookmark for {url.Path}",
                new Exception(error?.LocalizedDescription ?? "unknown"));
            return false;
        }

        Preferences.Default.Set(KeyPrefix + url.Path, Convert.ToBase64String(data.ToArray()));
        return true;
    }

    /// <summary>
    /// Resolves a previously saved bookmark back into a usable URL, already access-started. Null
    /// when nothing was ever saved for this path or the system can no longer resolve it (the file
    /// was moved, or permission genuinely lapsed).
    /// </summary>
    public static NSUrl? Resolve(string path)
    {
        var stored = Preferences.Default.Get<string?>(KeyPrefix + path, null);
        if (stored is null) return null;

        var data = NSData.FromArray(Convert.FromBase64String(stored));
        var url = NSUrl.FromBookmarkData(data, default, null, out var stale, out var error);

        if (error is not null || url is null)
        {
            AppLog.Error($"resolving the security-scoped bookmark for {path}",
                new Exception(error?.LocalizedDescription ?? "unknown"));
            return null;
        }

        url.StartAccessingSecurityScopedResource();

        // A stale bookmark still resolved this once (the file moved but the system could still
        // find it), so it is re-saved transparently rather than failed — the same tolerance a
        // moved file gets on Android, where the permission simply follows the URI regardless.
        if (stale) Save(url);

        return url;
    }

    public static void Forget(string path) => Preferences.Default.Remove(KeyPrefix + path);

    public static bool Exists(string path) => Preferences.Default.ContainsKey(KeyPrefix + path);
}
