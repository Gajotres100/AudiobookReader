using AudioBookReader.App.Services;

namespace AudioBookReader.App.Views;

/// <summary>Where a photograph of a page is to come from.</summary>
public enum PhotoSource
{
    Camera,
    Gallery,
}

/// <summary>
/// Asks which of the two to use.
///
/// Pushed modally and awaited, so the caller reads the way it did when this was a system action
/// sheet. The page decides nothing itself — it reports the answer and lets the caller act on it,
/// the same shape <see cref="DeleteConfirmPage"/> already uses.
/// </summary>
public partial class PhotoSourcePage : ContentPage
{
    private readonly TaskCompletionSource<PhotoSource?> _answer = new();

    /// <summary>The source chosen, or null when the choice was called off.</summary>
    public Task<PhotoSource?> Answer => _answer.Task;

    public PhotoSourcePage() => InitializeComponent();

    private async void OnCamera(object? sender, EventArgs e) => await CloseAsync(PhotoSource.Camera);

    private async void OnGallery(object? sender, EventArgs e) => await CloseAsync(PhotoSource.Gallery);

    private async void OnCancel(object? sender, EventArgs e) => await CloseAsync(null);

    private async Task CloseAsync(PhotoSource? choice)
    {
        // Guarded because the page can also go by the back gesture, which reaches OnDisappearing
        // with nobody having pressed anything.
        if (!_answer.TrySetResult(choice)) return;

        try
        {
            await Navigation.PopModalAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("closing the photo source choice", ex);
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();

        // Backed out of. Nothing was chosen, and the caller must not wait forever.
        _answer.TrySetResult(null);
    }
}
