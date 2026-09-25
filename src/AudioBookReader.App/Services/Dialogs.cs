using AudioBookReader.App.Views;

namespace AudioBookReader.App.Services;

/// <summary>
/// Questions and notices in the app's own style — see <see cref="DialogPage"/> for why not the
/// system's. The same three shapes the system offers, so a call site reads the same as before.
/// </summary>
public static class Dialogs
{
    /// <summary>A yes-or-no question. True when the first answer was chosen.</summary>
    public static async Task<bool> AskAsync(
        string? title,
        string? message,
        string accept,
        string cancel,
        bool destructive = false)
    {
        var answer = await ShowPageAsync(
            title,
            message,
            [(accept, destructive ? DialogButtonKind.Destructive : DialogButtonKind.Primary), (cancel, DialogButtonKind.Secondary)],
            onBack: cancel);

        return answer == accept;
    }

    /// <summary>A notice with a single way out.</summary>
    public static Task ShowAsync(string? title, string message, string close) =>
        ShowPageAsync(title, message, [(close, DialogButtonKind.Primary)], onBack: close);

    /// <summary>
    /// A choice among several. Returns the text of the option chosen, or <paramref name="cancel"/>
    /// when nothing was — the same contract as the system's action sheet.
    /// </summary>
    public static async Task<string> ChooseAsync(
        string? title,
        string cancel,
        string? destruction,
        params string[] options)
    {
        var buttons = options.Select(o => (o, DialogButtonKind.Secondary)).ToList();

        if (destruction is not null) buttons.Add((destruction, DialogButtonKind.Destructive));
        buttons.Add((cancel, DialogButtonKind.Secondary));

        return await ShowPageAsync(title, null, buttons, onBack: cancel) ?? cancel;
    }

    private static async Task<string?> ShowPageAsync(
        string? title,
        string? message,
        IReadOnlyList<(string, DialogButtonKind)> buttons,
        string onBack)
    {
        var page = new DialogPage(title, message, buttons, onBack);

        var navigation = Shell.Current?.Navigation
                         ?? Application.Current?.Windows.FirstOrDefault()?.Page?.Navigation;

        if (navigation is null) return onBack;

        page.Host = navigation;
        await MainThread.InvokeOnMainThreadAsync(() => navigation.PushModalAsync(page, animated: false));
        return await page.Answer;
    }
}
