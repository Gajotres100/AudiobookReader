using AudioBookReader.App.Services;

namespace AudioBookReader.App.Views;

/// <param name="AlsoFile">Whether the file itself should go, not only the library entry.</param>
public readonly record struct DeleteChoice(bool AlsoFile);

/// <summary>
/// Asks whether to delete, and how far the deletion should reach.
///
/// Pushed modally and awaited, so a caller reads the way it did when this was an alert. The page
/// decides nothing itself — it reports the answer and lets the caller act on it.
/// </summary>
public partial class DeleteConfirmPage : ContentPage
{
    private readonly TaskCompletionSource<DeleteChoice?> _answer = new();

    /// <summary>The choice made, or null when the deletion was called off.</summary>
    public Task<DeleteChoice?> Answer => _answer.Task;

    /// <param name="where">
    /// Where the file lives, shown so nobody deletes one they did not mean to. Null when there is
    /// no file to offer — an app-storage copy goes either way, so there is nothing to decide.
    /// </param>
    public DeleteConfirmPage(string heading, string body, string? where)
    {
        InitializeComponent();

        Heading.Text = heading;
        Body.Text = body;

        // Nothing to ask about when the only file is the app's own: it is deleted with the entry
        // and always was, and a switch over a decision that has already been made is noise.
        FileChoice.IsVisible = where is not null;
        Where.Text = where ?? "";
    }

    private async void OnConfirm(object? sender, EventArgs e) =>
        await CloseAsync(new DeleteChoice(FileChoice.IsVisible && AlsoFile.IsToggled));

    private async void OnCancel(object? sender, EventArgs e) => await CloseAsync(null);

    private async Task CloseAsync(DeleteChoice? choice)
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
            AppLog.Error("closing the delete confirmation", ex);
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();

        // Backed out of. Nothing was chosen, and the caller must not wait forever.
        _answer.TrySetResult(null);
    }
}
