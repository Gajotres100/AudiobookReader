using AudioBookReader.App.Views;

namespace AudioBookReader.App.Services;

/// <summary>
/// Questions and notices in the app's own style — see <see cref="DialogPage"/> for why not the
/// system's. The same three shapes the system offers, so a call site reads the same as before.
/// </summary>
public static class Dialogs
{
    private static int _showing;

    /// <summary>
    /// Whether one of these dialogs is on screen.
    ///
    /// A dialog is a page pushed over the one that asked, so that page is told it has disappeared
    /// and, when the dialog closes, that it has appeared again — exactly what it is told when it is
    /// left and returned to. Pages that save, tear down or reload on those two events check this to
    /// tell a dialog passing over them from someone actually leaving: the reader reloaded after every
    /// chapter picked from its own list, and put the book straight back where it had been.
    /// </summary>
    public static bool IsShowing => _showing > 0;

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

    /// <summary>A question answered by typing. Null when it was cancelled.</summary>
    public static async Task<string?> PromptAsync(
        string? title,
        string? message,
        string accept,
        string cancel,
        string placeholder)
    {
        var page = new DialogPage(
            title, message, [(accept, DialogButtonKind.Primary), (cancel, DialogButtonKind.Secondary)], cancel, placeholder);

        return await PresentAsync(page, cancel) == accept ? page.EntryText : null;
    }

    private static Task<string?> ShowPageAsync(
        string? title,
        string? message,
        IReadOnlyList<(string, DialogButtonKind)> buttons,
        string onBack) =>
        PresentAsync(new DialogPage(title, message, buttons, onBack), onBack);

    private static async Task<string?> PresentAsync(DialogPage page, string onBack)
    {

        var navigation = Shell.Current?.Navigation
                         ?? Application.Current?.Windows.FirstOrDefault()?.Page?.Navigation;

        if (navigation is null) return onBack;

        page.Host = navigation;
        Interlocked.Increment(ref _showing);

        try
        {
            await MainThread.InvokeOnMainThreadAsync(() => navigation.PushModalAsync(page, animated: false));
            return await page.Answer;
        }
        finally
        {
            Interlocked.Decrement(ref _showing);
        }
    }
}
