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

    /// <summary>
    /// Where a book coming down from a server lands before the importer takes it.
    ///
    /// Its own directory rather than the books one, so a download interrupted halfway can be told
    /// apart from a book in the library and cleared without touching anything the user owns.
    /// </summary>
    public static string Downloads => Path.Combine(Root, "downloads");

    public static void EnsureCreated()
    {
        foreach (var directory in new[] { SyncMaps, Models, Books, Covers, Downloads })
            Directory.CreateDirectory(directory);

        SweepDownloads();
    }

    /// <summary>
    /// Clears anything left in the staging directory.
    ///
    /// Its contents are only ever mid-import. A download deletes what it wrote whichever way it
    /// ends — but not when the app is killed while it is running, and what is left then is half a
    /// book taking up hundreds of megabytes that nothing will ever look at again.
    /// </summary>
    private static void SweepDownloads()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Downloads)) File.Delete(file);
        }
        catch (IOException)
        {
            // Worth a try, not worth failing startup over.
        }
    }
}
