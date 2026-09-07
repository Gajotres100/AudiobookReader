using System.Globalization;

namespace AudioBookReader.App.Services;

/// <summary>
/// Which language the app speaks.
///
/// Three choices rather than two: the phone's own language, or one of the ones the app has. The
/// first is the default and is what almost everyone wants — a Croatian phone gets Croatian without
/// anybody choosing anything — but somebody reading English books on a Croatian phone is a real
/// person, and this is one line of code to let them.
///
/// A change rebuilds the shell rather than trying to retranslate the screens in place. Every label
/// asks the resource file for its text as it is created, so a page built after the change is simply
/// in the new language; a page built before it would need every binding to know it should ask
/// again. Rebuilding is both simpler and complete.
/// </summary>
public class Language
{
    private const string Key = "app.language";

    /// <summary>What the app can speak, in the order the setting lists them.</summary>
    public static readonly IReadOnlyList<string> Available = ["", "hr", "en"];

    /// <summary>The stored choice: a culture name, or empty for the phone's own.</summary>
    public string Current
    {
        get => Preferences.Default.Get(Key, "");
        private set => Preferences.Default.Set(Key, value);
    }

    /// <summary>
    /// Applies the stored choice. Called once at startup, before any page is built.
    /// </summary>
    /// <remarks>
    /// Both cultures, not only the interface one. The UI culture picks which resource file answers;
    /// the formatting culture decides how a date or a number is written. Setting only the first
    /// gives a screen whose words are Croatian and whose dates are month-first American, which
    /// looks like a bug because it is one.
    /// </remarks>
    public void Apply()
    {
        var culture = Resolve(Current);

        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.CurrentCulture = culture;

        AppLog.Info($"language: {(Current.Length == 0 ? "system" : Current)} → {culture.Name}");
    }

    /// <summary>
    /// Changes the language and rebuilds the screens in it.
    /// </summary>
    /// <returns>True when something actually changed.</returns>
    public bool Set(string language)
    {
        if (language == Current) return false;

        Current = language;
        Apply();

        // The whole shell, not the current page: the tabs, their titles and every page behind them
        // were all built in the old language.
        if (Application.Current?.Windows.FirstOrDefault() is { } window) window.Page = new AppShell();

        return true;
    }

    /// <summary>
    /// The culture to actually use.
    ///
    /// A phone set to a language the app does not have falls back to English rather than to an
    /// empty screen — .NET would otherwise look for, say, Hungarian resources, find none, and use
    /// the neutral file, which is English anyway. Naming it explicitly keeps the behaviour obvious
    /// instead of incidental.
    /// </summary>
    private static CultureInfo Resolve(string language)
    {
        if (language.Length > 0) return new CultureInfo(language);

        var phone = CultureInfo.CurrentUICulture;

        return Available.Contains(phone.TwoLetterISOLanguageName)
            ? new CultureInfo(phone.TwoLetterISOLanguageName)
            : new CultureInfo("en");
    }
}
