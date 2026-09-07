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

    /// <summary>Lets go of the download queue, which outlives this page by design.</summary>
    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _viewModel.Detach();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        _viewModel.Attach();

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
