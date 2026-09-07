using AudioBookReader.App.Services;

namespace AudioBookReader.App.Views;

/// <summary>
/// Explains what alignment is doing, reachable from the book's page and from Settings.
///
/// Modal rather than a tab of its own: it is read once, when a question comes up, and then closed.
/// It holds no state and asks for nothing, so it needs neither a view model nor a route.
/// </summary>
public partial class AlignmentHelpPage : ContentPage
{
    public AlignmentHelpPage() => InitializeComponent();

    private async void OnClose(object? sender, EventArgs e)
    {
        try
        {
            await Navigation.PopModalAsync();
        }
        catch (Exception ex)
        {
            // An exception out of an async void override takes the process with it, and closing a
            // help sheet is not worth that.
            AppLog.Error("closing the alignment help", ex);
        }
    }
}
