using AudioBookReader.App.Services;
using AudioBookReader.App.ViewModels;

namespace AudioBookReader.App.Views;

public partial class LibraryPage : ContentPage
{
    private readonly LibraryViewModel _viewModel;

    private readonly TabReselect _taps;

    public LibraryPage(LibraryViewModel viewModel, TabReselect taps)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
        _taps = taps;
    }

    /// <summary>Lets go of the background queues, which outlive this page.</summary>
    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _viewModel.Detach();
    }

    /// <summary>
    /// Reloads on every appearance rather than only on first load: the library changes while the
    /// user is on another page — a book gets imported, alignment finishes, a position advances —
    /// and coming back to a stale list would be the obvious bug.
    /// </summary>
    protected override async void OnAppearing()
    {
        base.OnAppearing();

        // Here rather than at startup: the bottom bar is rebuilt whenever the shell is, which
        // happens on every change of language, and this is the first page shown afterwards.
        _taps.Watch();

        _viewModel.Attach();

        // An exception out of an async void override kills the process, and this one is reached
        // before the user can do anything about whatever went wrong.
        try
        {
            await _viewModel.LoadAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("opening the library", ex);
        }
    }
}
