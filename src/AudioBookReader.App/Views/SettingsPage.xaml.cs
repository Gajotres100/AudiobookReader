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
}
