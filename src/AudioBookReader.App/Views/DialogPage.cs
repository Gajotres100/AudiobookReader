using AudioBookReader.App.Services;

namespace AudioBookReader.App.Views;

/// <summary>How a dialog button looks, which says what it does.</summary>
public enum DialogButtonKind
{
    /// <summary>The answer the dialog is really asking for: filled, in the app's colour.</summary>
    Primary,

    /// <summary>Any other answer: outlined.</summary>
    Secondary,

    /// <summary>An answer that removes something: in the warning colour.</summary>
    Destructive,
}

/// <summary>
/// The app's own alert, in place of the system's.
///
/// The system dialog draws its buttons as bare words in a corner and its text wherever the platform
/// likes, and on a television it was hard to tell which of two words had focus at all. This is a
/// card over the dimmed page, with the text laid out like the rest of the app and real buttons —
/// the one that answers the question filled in the app's colour, the rest outlined — full width and
/// stacked, so they are easy to hit on a phone and easy to see on a television, where focus starts
/// on the main one and wears the same ring as everything else.
/// </summary>
public sealed class DialogPage : ContentPage
{
    private readonly TaskCompletionSource<string?> _answer = new();
    private readonly string? _onBack;
    private Button? _first;

    /// <summary>The text of the button pressed, or the back answer when it was dismissed.</summary>
    public Task<string?> Answer => _answer.Task;

    /// <param name="onBack">What leaving with the back button counts as — the cancel answer.</param>
    public DialogPage(
        string? title,
        string? message,
        IReadOnlyList<(string Text, DialogButtonKind Kind)> buttons,
        string? onBack,
        string? entryPlaceholder = null)
    {
        _onBack = onBack;

        Shell.SetNavBarIsVisible(this, false);
        BackgroundColor = Color.FromArgb("#99000000");

        // Over the page that asked, not instead of it. iOS presents a modal full screen by default
        // and stops drawing what is underneath, so the dimmed backdrop came out solid black there.
        Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific.Page.SetModalPresentationStyle(
            this.On<Microsoft.Maui.Controls.PlatformConfiguration.iOS>(),
            Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific.UIModalPresentationStyle.OverFullScreen);

        var content = new VerticalStackLayout { Spacing = 10 };

        if (!string.IsNullOrWhiteSpace(title))
            content.Add(new Label { Text = title, Style = Find<Style>("Heading") });

        if (!string.IsNullOrWhiteSpace(message))
            content.Add(new Label
            {
                Text = message,
                FontSize = 16,
                LineHeight = 1.25,
                Margin = new Thickness(0, 0, 0, 8),
            });

        // A line to type into, when the question needs words rather than a choice.
        if (entryPlaceholder is not null)
        {
            _entry = new Entry { Placeholder = entryPlaceholder, Keyboard = Keyboard.Url, Margin = new Thickness(0, 0, 0, 8) };
            content.Add(_entry);
        }

        foreach (var (text, kind) in buttons)
        {
            var button = new Button
            {
                Text = text,
                Style = Find<Style>(kind switch
                {
                    DialogButtonKind.Primary => "Primary",
                    DialogButtonKind.Destructive => "Destructive",
                    _ => "Secondary",
                }),
            };

            button.Clicked += async (_, _) => await AnswerAsync(text);
            content.Add(button);

            _first ??= button;
        }

        // Sized to what it holds, and centred. A scroll view left to itself takes all the height it
        // is offered, which stretched a two-button question down the whole screen; in an Auto row it
        // is measured by its content instead, capped so a long list of chapters still scrolls.
        var display = DeviceDisplay.Current.MainDisplayInfo;
        var screenHeight = display.Density > 0 ? display.Height / display.Density : 800;

        var card = new Border
        {
            Style = Find<Style>("Card"),
            Content = new ScrollView { Content = content, MaximumHeightRequest = screenHeight * 0.8 },
            MaximumWidthRequest = 560,
            Padding = new Thickness(20, 18),
            HorizontalOptions = LayoutOptions.Fill,
        };

        var frame = new Grid
        {
            Padding = new Thickness(20),
            RowDefinitions = [new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star)],
        };

        frame.Add(card, 0, 1);
        Content = frame;
    }

    private readonly Entry? _entry;

    /// <summary>What was typed, when the dialog asked for something to be typed.</summary>
    public string? EntryText => _entry?.Text;

    /// <summary>The navigation that opened this, and so the one that closes it.</summary>
    internal INavigation? Host { get; set; }

    private static T? Find<T>(string key) where T : class =>
        Application.Current?.Resources.TryGetValue(key, out var value) == true ? value as T : null;

    protected override void OnAppearing()
    {
        base.OnAppearing();

        // Where a remote starts: on the answer the dialog is asking for. Only on a television —
        // on a touch screen a focused button takes its first tap as focus rather than as a press,
        // so the main answer needed pressing twice.
        if (_entry is not null) _entry.Focus();
        else if (DeviceInfo.Current.Idiom == DeviceIdiom.TV) _first?.Focus();
    }

    protected override bool OnBackButtonPressed()
    {
        _ = AnswerAsync(_onBack);
        return true;
    }

    // Deliberately no answer on disappearing: locking the screen makes a page disappear too, and
    // taking that as "cancel" would answer a question nobody had answered, then leave the card on
    // screen with buttons that no longer did anything.

    private bool _answered;

    /// <summary>
    /// Closes the card first and only then hands over the answer.
    ///
    /// The other way round, whoever was waiting carried on at once — while the card was still on
    /// top — and an answer that opens another page (Premium's "yes") had that page closed by the
    /// card's own closing a moment later, so the button looked as if it had done nothing.
    /// </summary>
    private async Task AnswerAsync(string? answer)
    {
        if (_answered) return;
        _answered = true;

        AppLog.Info($"dialog: answered '{answer}'");

        try
        {
            // Through the navigation that opened it. The page's own sees a different modal stack
            // under Shell, so asking it whether this was on top answered no and nothing was closed.
            var navigation = Host ?? Navigation;

            if (navigation.ModalStack.LastOrDefault() is { } top && !ReferenceEquals(top, this))
                AppLog.Info($"dialog: not on top of the modal stack ({top.GetType().Name} is)");

            await navigation.PopModalAsync(animated: false);
        }
        catch (Exception ex)
        {
            AppLog.Error("closing a dialog", ex);
        }
        finally
        {
            _answer.TrySetResult(answer);
        }
    }
}
