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

    protected override async void OnAppearing()
    {
        base.OnAppearing();

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
        _viewModel.Dispose();
    }
}
