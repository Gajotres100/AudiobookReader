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
        string? onBack)
    {
        _onBack = onBack;

        Shell.SetNavBarIsVisible(this, false);
        BackgroundColor = Color.FromArgb("#99000000");

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

        Content = new Border
        {
            Style = Find<Style>("Card"),
            Content = new ScrollView { Content = content },
            MaximumWidthRequest = 560,
            Margin = new Thickness(20),
            Padding = new Thickness(20, 18),
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Fill,
        };
    }

    private static T? Find<T>(string key) where T : class =>
        Application.Current?.Resources.TryGetValue(key, out var value) == true ? value as T : null;

    protected override void OnAppearing()
    {
        base.OnAppearing();

        // Where a remote starts: on the answer the dialog is asking for.
        _first?.Focus();
    }

    protected override bool OnBackButtonPressed()
    {
        _ = AnswerAsync(_onBack);
        return true;
    }

    // Deliberately no answer on disappearing: locking the screen makes a page disappear too, and
    // taking that as "cancel" would answer a question nobody had answered, then leave the card on
    // screen with buttons that no longer did anything.

    private async Task AnswerAsync(string? answer)
    {
        if (!_answer.TrySetResult(answer)) return;

        try
        {
            if (Navigation.ModalStack.Contains(this)) await Navigation.PopModalAsync(animated: false);
        }
        catch (Exception ex)
        {
            AppLog.Error("closing a dialog", ex);
        }
    }
}
