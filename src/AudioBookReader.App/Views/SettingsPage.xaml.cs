using AudioBookReader.App.Services;
using AudioBookReader.App.ViewModels;

namespace AudioBookReader.App.Views;

public partial class SettingsPage : ContentPage
{
    private readonly SettingsViewModel _viewModel;

    public SettingsPage(SettingsViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _viewModel.Load();
    }

    private void OnBudgetTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Element { BindingContext: BudgetOption option }) _viewModel.Select(option);
    }

    private void OnRecognitionTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Element { BindingContext: RecognitionOption option }) _viewModel.Select(option);
    }

    private void OnProbeSpacingTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Element { BindingContext: ProbeSpacingOption option }) _viewModel.Select(option);
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
