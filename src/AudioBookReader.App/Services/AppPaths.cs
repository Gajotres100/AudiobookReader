namespace AudioBookReader.App.Services;

/// <summary>
/// Where the app keeps things.
///
/// Everything lives in app-private storage. Imported books are copied in rather than referenced
/// where the user picked them: a content-URI grant from the file picker does not survive a reboot,
/// and a book that stops playing next week because its permission lapsed would be baffling.
/// Keeping our own copy also means the app never needs broad filesystem permissions, which is what
/// keeps it inside what Play allows without a special declaration.
/// </summary>
public static class AppPaths
{
    private static string Root => FileSystem.AppDataDirectory;

    public static string LibraryDatabase => Path.Combine(Root, "library.db");

    /// <summary>Sync maps, one JSON file per book.</summary>
    public static string SyncMaps => Path.Combine(Root, "sync");

    /// <summary>Speech models, downloaded on first use.</summary>
    public static string Models => Path.Combine(Root, "models");

    /// <summary>Imported audiobooks and ebooks.</summary>
    public static string Books => Path.Combine(Root, "books");

    /// <summary>Extracted cover images.</summary>
    public static string Covers => Path.Combine(Root, "covers");

    public static void EnsureCreated()
    {
        foreach (var directory in new[] { SyncMaps, Models, Books, Covers })
            Directory.CreateDirectory(directory);
    }
}
