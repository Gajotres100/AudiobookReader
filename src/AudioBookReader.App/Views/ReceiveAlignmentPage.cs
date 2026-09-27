using AudioBookReader.App.Resources.Strings;
using AudioBookReader.App.Services;
using QRCoder;

namespace AudioBookReader.App.Views;

/// <summary>
/// Shows a QR code for another device to scan, and waits for the alignment it sends.
///
/// The code says where this device is and carries a one-time key, so the sender needs neither to
/// find it on the network — which an iPhone or iPad cannot do on its own — nor to type an address,
/// and what arrives with the key is taken without a question: the person is standing right here.
/// </summary>
public sealed class ReceiveAlignmentPage : ContentPage
{
    private readonly AlignmentShare _share;
    private readonly Label _status;

    public ReceiveAlignmentPage(AlignmentShare share)
    {
        _share = share;

        Shell.SetNavBarIsVisible(this, false);
        if (Resource<Color>("Paper") is { } light && Resource<Color>("PaperDark") is { } dark)
            this.SetAppThemeColor(BackgroundColorProperty, light, dark);

        var code = share.BeginPairing();

        _status = new Label
        {
            HorizontalTextAlignment = TextAlignment.Center,
            Style = Style("Muted"),
            Text = code is null ? Strings.Share_QrNoNetwork : Strings.Share_QrWaiting,
        };

        var close = new Button { Text = "✕", Style = Style("Icon") };
        close.Clicked += async (_, _) => await CloseAsync();

        var layout = new VerticalStackLayout
        {
            Padding = new Thickness(24, 16, 24, 24),
            Spacing = 16,
            Children =
            {
                new Grid
                {
                    ColumnDefinitions = [new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star)],
                    Children =
                    {
                        close,
                        Placed(new Label
                        {
                            Text = Strings.Share_QrTitle,
                            Style = Style("Heading"),
                            VerticalOptions = LayoutOptions.Center,
                        }, 1),
                    },
                },
                new Label { Text = Strings.Share_QrBody, Style = Style("Muted") },
            },
        };

        if (code is not null)
        {
            // White behind the code in either theme: a scanner reads dark modules on light, and a QR
            // code drawn on a dark page is one camera apps routinely fail to see.
            layout.Children.Add(new Border
            {
                BackgroundColor = Colors.White,
                Padding = 16,
                StrokeThickness = 0,
                HorizontalOptions = LayoutOptions.Center,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 16 },
                Content = new Image
                {
                    Source = QrImage(code.ToString()),
                    WidthRequest = 260,
                    HeightRequest = 260,
                },
            });

            layout.Children.Add(new Label
            {
                HorizontalTextAlignment = TextAlignment.Center,
                FontFamily = "OpenSansSemibold",
                Text = $"{code.Name}\n{string.Join(", ", code.Addresses)}",
            });
        }

        layout.Children.Add(_status);

        Content = new ScrollView { Content = layout };
    }

    private static ImageSource QrImage(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(12);

        return ImageSource.FromStream(() => new MemoryStream(png));
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _share.Received += OnReceived;
    }

    protected override void OnDisappearing()
    {
        _share.Received -= OnReceived;
        _share.EndPairing();
        base.OnDisappearing();
    }

    private void OnReceived(object? sender, string title) => MainThread.BeginInvokeOnMainThread(async () =>
    {
        _status.Text = string.Format(Strings.Share_QrReceived, title);

        // Long enough to read, then out of the way; another scan would need a fresh code anyway.
        await Task.Delay(2500);
        await CloseAsync();
    });

    private async Task CloseAsync()
    {
        if (Navigation.ModalStack.Contains(this)) await Navigation.PopModalAsync();
    }

    private static Style? Style(string key) => Resource<Style>(key);

    private static T? Resource<T>(string key) where T : class =>
        Application.Current?.Resources.TryGetValue(key, out var value) == true ? value as T : null;

    private static View Placed(View view, int column)
    {
        Grid.SetColumn(view, column);
        return view;
    }
}
