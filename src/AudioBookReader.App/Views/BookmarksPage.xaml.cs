using AudioBookReader.App.Services;
using AudioBookReader.App.ViewModels;

namespace AudioBookReader.App.Views;

public partial class BookmarksPage : ContentPage
{
    private readonly BookmarksViewModel _viewModel;

    public BookmarksPage(BookmarksViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        // An exception out of an async void override kills the process, and this one is reached
        // before the user can do anything about whatever went wrong.
        try
        {
            await _viewModel.LoadAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("opening the bookmarks", ex);
        }
    }
}
