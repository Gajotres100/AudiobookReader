using AudioBookReader.App.Resources.Strings;
using AudioBookReader.App.Services;

namespace AudioBookReader.App.Views;

/// <summary>
/// Asks where a downloaded book should go, and answers with the choice.
///
/// Pushed modally and awaited, so the caller reads like the action sheet it replaces rather than
/// like a navigation. The page owns nothing but the question; picking the folder itself is still
/// the system's business, and this only decides whether to ask for one.
/// </summary>
public partial class DownloadDestinationPage : ContentPage
{
    private readonly DownloadFolder _folder;
    private readonly TaskCompletionSource<bool?> _answer = new();

    /// <summary>True for app storage, false for the chosen folder, null when called off.</summary>
    public Task<bool?> Answer => _answer.Task;

    public DownloadDestinationPage(DownloadFolder folder)
    {
        InitializeComponent();

        _folder = folder;

        // Said only when there is something to say. Before a folder has been granted this line
        // would be a promise about a place the user has not seen yet.
        ChosenFolder.Text = folder.IsChosen
            ? string.Format(Strings.Destination_FolderChosen, folder.Describe())
            : Strings.Destination_FolderWillAsk;
    }

    private async void OnFolderTapped(object? sender, TappedEventArgs e)
    {
        // Already granted, so nothing to ask. Otherwise the picker opens at Audiobooks: the system
        // will not hand over a folder without someone confirming it, but it will start them in the
        // right place, which makes this one tap rather than a hunt.
        if (!_folder.IsChosen && await _folder.ChooseAsync() is null) return;

        await CloseAsync(toAppStorage: false);
    }

    private async void OnAppStorageTapped(object? sender, TappedEventArgs e) =>
        await CloseAsync(toAppStorage: true);

    private async void OnCancelled(object? sender, EventArgs e) => await CloseAsync(null);

    /// <summary>
    /// Answers once and leaves. Guarded because the page can also go by the system back gesture,
    /// which reaches <see cref="OnDisappearing"/> with nobody having chosen anything.
    /// </summary>
    private async Task CloseAsync(bool? toAppStorage)
    {
        if (!_answer.TrySetResult(toAppStorage)) return;

        await Navigation.PopModalAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();

        // Backed out of. Nothing was chosen, and the caller must not be left waiting forever.
        _answer.TrySetResult(null);
    }
}
