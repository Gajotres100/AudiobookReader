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

        // Two different jobs, so two different weights.
        //
        // "Now: Audiobooks" is a note about a settled thing and can sit quietly at the bottom of
        // the card. The other line is a warning that tapping this opens the system's folder picker
        // — and at twelve grey pixels nobody read it, so choosing the Audiobooks folder and then
        // being handed a folder picker looked like the app ignoring the answer just given.
        if (folder.IsChosen)
        {
            ChosenFolder.Text = string.Format(Strings.Destination_FolderChosen, folder.Describe());
        }
        else
        {
            ChosenFolder.Text = Strings.Destination_FolderWillAsk;
            ChosenFolder.FontSize = 14;
            ChosenFolder.FontFamily = "OpenSansSemibold";
        }
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
