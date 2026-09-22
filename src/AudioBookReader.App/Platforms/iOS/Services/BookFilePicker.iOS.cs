using UIKit;
using UniformTypeIdentifiers;

namespace AudioBookReader.App.Services;

/// <summary>
/// UIDocumentPickerViewController asked for every file type, the same reasoning as Android's
/// MainActivity.PickDocumentAsync: filtering by UTType greys out books whose type the system
/// misreports, and .m4b/.epub are exactly the kind of file that happens to. Opened with
/// asCopy:false so the file stays where the user keeps it rather than being copied into the app's
/// own sandbox — the bookmark saved right after is what keeps that reference usable later.
/// </summary>
public partial class BookFilePicker
{
    private partial Task<PickedMedia?> PickAsync(string prompt)
    {
        var tcs = new TaskCompletionSource<PickedMedia?>();

        var picker = new UIDocumentPickerViewController([UTTypes.Item], asCopy: false);

        picker.DidPickDocumentAtUrls += (_, e) =>
        {
            var url = e.Urls.FirstOrDefault();

            if (url is null) { tcs.TrySetResult(null); return; }

            // Saved immediately, while the grant the picker just handed over is still fresh —
            // there is no later moment to come back and ask for it, unlike Android's persistable
            // URI permission.
            SecurityScopedBookmarks.Save(url);

            tcs.TrySetResult(new PickedMedia(url.Path!, url.LastPathComponent ?? url.Path!));
        };

        picker.WasCancelled += (_, _) => tcs.TrySetResult(null);

        var presenter = CurrentViewController.Get();
        if (presenter is null) { tcs.TrySetResult(null); return tcs.Task; }

        presenter.PresentViewController(picker, animated: true, completionHandler: null);
        return tcs.Task;
    }
}
