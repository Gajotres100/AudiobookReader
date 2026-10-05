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

        _viewModel.PropertyChanged += OnViewModelChanged;
    }

    /// <summary>
    /// Puts the keyboard away once connecting starts. Connecting from the keyboard's own Go key
    /// left it up over the server's books, and the first taps on them landed on its keys instead.
    /// </summary>
    private async void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ServerViewModel.IsConnecting) || !_viewModel.IsConnecting) return;

        try
        {
            await PasswordEntry.HideSoftInputAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            AppLog.Error("hiding the keyboard", ex);
        }
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
