using AudioBookReader.App.Services;

namespace AudioBookReader.App.Views;

/// <summary>Where the joke $ button in the library lands, once someone says yes to it.</summary>
public partial class PremiumPage : ContentPage
{
    public PremiumPage() => InitializeComponent();

    private async void OnClose(object? sender, EventArgs e)
    {
        try
        {
            await Navigation.PopModalAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("closing the premium screen", ex);
        }
    }
}
