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
        await _viewModel.LoadAsync();
    }
}
