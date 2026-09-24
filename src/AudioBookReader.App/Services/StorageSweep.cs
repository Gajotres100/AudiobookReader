using AudioBookReader.Core.Data;

namespace AudioBookReader.App.Services;

/// <summary>
/// Removes files in the app's own storage that no book uses.
///
/// An import makes its file before it makes the book: the audio is downloaded or copied in first,
/// then read for chapters, and only then written to the library. Anything that ends it in between —
/// the app closed, the process killed, a television running out of memory — leaves the file with no
/// book pointing at it, invisible in the app and taking up space that on a 4 GB television is most of
/// what there is. This finds those and deletes them, once, shortly after the app starts.
///
/// Only the app's own folders are touched — never a book the user keeps in a folder of theirs — and
/// nothing while a download or import is in progress or was writing a moment ago.
/// </summary>
public sealed class StorageSweep(LibraryDatabase database, DownloadQueue downloads)
{
    /// <summary>Files touched more recently than this are assumed to belong to something still running.</summary>
    private static readonly TimeSpan RecentlyWritten = TimeSpan.FromMinutes(2);

    public async Task RunAsync()
    {
        try
        {
            if (Busy()) return;

            var books = await database.GetBooksAsync();

            var used = new HashSet<string>(
                books.SelectMany(b => new[] { b.AudioPath, b.OriginalAudioPath, b.EbookPath, b.CoverPath })
                    .Where(p => !string.IsNullOrEmpty(p))
                    .Select(p => Full(p!)),
                StringComparer.Ordinal);

            long freed = 0;
            var removed = 0;

            // A staged download is never used by a book directly: it is moved or copied into the
            // library as the import finishes. One still here outlived its import.
            foreach (var folder in new[] { AppPaths.Downloads, AppPaths.Books, AppPaths.Covers })
            {
                if (!Directory.Exists(folder)) continue;

                foreach (var file in Directory.EnumerateFiles(folder))
                {
                    if (used.Contains(Full(file))) continue;

                    var info = new FileInfo(file);
                    if (DateTime.UtcNow - info.LastWriteTimeUtc < RecentlyWritten) continue;

                    // Asked again for every file: an import may have begun since the sweep did.
                    if (Busy()) return;

                    var size = info.Length;
                    info.Delete();

                    freed += size;
                    removed++;
                }
            }

            if (removed > 0)
                AppLog.Info($"storage sweep: removed {removed} file(s) no book uses, {freed / (1024 * 1024)} MB freed");
        }
        catch (Exception ex)
        {
            AppLog.Error("sweeping app storage", ex);
        }
    }

    private bool Busy() => BookImporter.IsImporting || downloads.Status.IsRunning;

    private static string Full(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception)
        {
            // A content:// reference is not a path at all, and is never in these folders anyway.
            return path;
        }
    }
}
