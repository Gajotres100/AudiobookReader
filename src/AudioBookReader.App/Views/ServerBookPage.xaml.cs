using AudioBookReader.App.Services;
using AudioBookReader.App.ViewModels;

namespace AudioBookReader.App.Views;

public partial class ServerBookPage : ContentPage
{
    private readonly ServerBookViewModel _viewModel;

    public ServerBookPage(ServerBookViewModel viewModel)
    {
        InitializeComponent();
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
            AppLog.Error("opening a server book", ex);
        }
    }
}
