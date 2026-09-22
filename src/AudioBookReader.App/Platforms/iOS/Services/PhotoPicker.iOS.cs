using Foundation;
using PhotosUI;
using UIKit;
using UniformTypeIdentifiers;

namespace AudioBookReader.App.Services;

/// <summary>
/// PHPickerViewController — needs no privacy usage description or permission at all, the system
/// mediates access and hands back only the one image chosen. Same reasoning as Android's native
/// photo picker needing no storage permission.
/// </summary>
public partial class PhotoPicker
{
    public partial Task<byte[]?> PickFromGalleryAsync()
    {
        var tcs = new TaskCompletionSource<byte[]?>();

        var configuration = new PHPickerConfiguration
        {
            Filter = PHPickerFilter.ImagesFilter,
            SelectionLimit = 1,
        };

        var picker = new PHPickerViewController(configuration)
        {
            Delegate = new Delegate(tcs),
        };

        var presenter = CurrentViewController.Get();
        if (presenter is null) { tcs.TrySetResult(null); return tcs.Task; }

        presenter.PresentViewController(picker, animated: true, completionHandler: null);
        return tcs.Task;
    }

    private sealed class Delegate(TaskCompletionSource<byte[]?> tcs) : PHPickerViewControllerDelegate
    {
        public override void DidFinishPicking(PHPickerViewController picker, PHPickerResult[] results)
        {
            picker.DismissViewController(animated: true, completionHandler: null);

            var provider = results.FirstOrDefault()?.ItemProvider;
            if (provider is null) { tcs.TrySetResult(null); return; }

            _ = LoadAsync(provider);
        }

        private async Task LoadAsync(NSItemProvider provider)
        {
            try
            {
                var data = await provider.LoadDataRepresentationAsync(UTTypes.Image.Identifier);
                tcs.TrySetResult(data?.ToArray());
            }
            catch (Exception ex)
            {
                AppLog.Error("reading the chosen picture", ex);
                tcs.TrySetResult(null);
            }
        }
    }
}
