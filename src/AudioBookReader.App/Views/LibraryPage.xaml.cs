using AudioBookReader.App.ViewModels;

namespace AudioBookReader.App.Views;

public partial class LibraryPage : ContentPage
{
    private readonly LibraryViewModel _viewModel;

    public LibraryPage(LibraryViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    /// <summary>
    /// Reloads on every appearance rather than only on first load: the library changes while the
    /// user is on another page — a book gets imported, alignment finishes, a position advances —
    /// and coming back to a stale list would be the obvious bug.
    /// </summary>
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.LoadAsync();
    }
}
