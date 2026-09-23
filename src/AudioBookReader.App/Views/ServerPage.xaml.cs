using AudioBookReader.App.Services;
using AudioBookReader.App.ViewModels;

namespace AudioBookReader.App.Views;

public partial class ServerPage : ContentPage
{
    private readonly ServerViewModel _viewModel;

    public ServerPage(ServerViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    private void OnShowPasswordPressed(object? sender, EventArgs e) => PasswordEntry.IsPassword = false;

    private void OnShowPasswordReleased(object? sender, EventArgs e) => PasswordEntry.IsPassword = true;

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        try
        {
            await _viewModel.LoadAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("opening the server page", ex);
        }
    }
}
