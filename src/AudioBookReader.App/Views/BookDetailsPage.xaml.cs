using AudioBookReader.App.Services;
using AudioBookReader.App.ViewModels;

namespace AudioBookReader.App.Views;

public partial class BookDetailsPage : ContentPage
{
    private readonly BookViewModel _viewModel;

    public BookDetailsPage(BookViewModel viewModel)
    {
        InitializeComponent();

        // Nothing here shows the playhead, so the page does not run the once-a-second timer that
        // drives the player's position. It shares the view model because it acts on the same book —
        // attaching media, alignment, deletion — and splitting that in two would mean two objects
        // writing the same row.
        viewModel.TracksPlayback = false;

        BindingContext = _viewModel = viewModel;
    }

    /// <summary>Set while one of the app's dialogs is over the page; see the reader's.</summary>
    private bool _underDialog;

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        if (_underDialog)
        {
            _underDialog = false;
            return;
        }

        try
        {
            await _viewModel.LoadAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("opening the book details", ex);
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();

        // Asking a question — delete this? send it where? — is not leaving the page.
        if (Dialogs.IsShowing)
        {
            _underDialog = true;
            return;
        }

        _viewModel.Dispose();
    }

    /// <summary>Opens the page that explains what alignment is doing and why it can be slow.</summary>
    private async void OnAlignmentHelp(object? sender, EventArgs e)
    {
        try
        {
            await Navigation.PushModalAsync(new AlignmentHelpPage());
        }
        catch (Exception ex)
        {
            AppLog.Error("opening the alignment help", ex);
        }
    }
}
