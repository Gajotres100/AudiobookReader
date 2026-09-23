using Foundation;
using UIKit;
using UniformTypeIdentifiers;

namespace AudioBookReader.App.Services;

/// <summary>
/// UIDocumentPickerViewController in folder-picking mode, with the chosen tree kept as a
/// security-scoped bookmark — the same mechanism BookFilePicker.iOS.cs uses for a single file,
/// reused here through SecurityScopedBookmarks so both stay in one place.
/// </summary>
public partial class DownloadFolder
{
    public partial string Describe() =>
        Location is { } path ? Path.GetFileName(path) : "";

    private partial Task<string?> ChooseCoreAsync(bool startAtAudiobooks)
    {
        var tcs = new TaskCompletionSource<string?>();

        var picker = new UIDocumentPickerViewController([UTTypes.Folder], asCopy: false);

        picker.DidPickDocumentAtUrls += (_, e) =>
        {
            var url = e.Urls.FirstOrDefault();

            if (url is null) { tcs.TrySetResult(null); return; }

            SecurityScopedBookmarks.Save(url, isFolder: true);
            tcs.TrySetResult(url.Path);
        };

        picker.WasCancelled += (_, _) => tcs.TrySetResult(null);

        var presenter = CurrentViewController.Get();
        if (presenter is null) { tcs.TrySetResult(null); return tcs.Task; }

        presenter.PresentViewController(picker, animated: true, completionHandler: null);
        return tcs.Task;
    }

    /// <summary>Resolves the chosen folder's bookmark, starting access. Null when nothing is chosen or the folder can no longer be reached.</summary>
    private NSUrl? ResolveFolder() => Location is { } path ? SecurityScopedBookmarks.Access(path) : null;

    public partial Task<(Stream Stream, string Location)> CreateAsync(
        IReadOnlyList<string> folders, string fileName)
    {
        var root = ResolveFolder()
            ?? throw new IOException("No download folder is chosen, or it can no longer be reached.");

        var directory = folders.Aggregate(root.Path!, Path.Combine);
        Directory.CreateDirectory(directory);

        var fullPath = Path.Combine(directory, fileName);

        return Task.FromResult<(Stream, string)>((File.Create(fullPath), fullPath));
    }

    public partial void Delete(string location)
    {
        try
        {
            // The folder's own bookmark has to be resolved first — the file's own path alone
            // carries no access grant on iOS, unlike a plain app-storage path.
            ResolveFolder();
            File.Delete(location);
        }
        catch (Exception ex)
        {
            AppLog.Error($"deleting the unfinished download '{location}'", ex);
        }
    }
}
