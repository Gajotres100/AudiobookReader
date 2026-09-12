namespace AudioBookReader.App.Services;

/// <summary>
/// Notices when the tab you are already on is tapped again.
///
/// This exists because Shell does not report it. Tapping a different tab is a navigation and
/// arrives through <c>OnNavigated</c>; tapping the current one is not, so nothing above the
/// platform hears about it at all — and that is precisely the gesture that means "take me back to
/// the list", made by somebody looking at a book and wanting the shelf.
/// </summary>
public partial class TabReselect
{
    /// <summary>
    /// The one listener that gets told, not a list of them.
    ///
    /// A plain event would let every AppShell that has ever existed pile onto this singleton: each
    /// language change builds a new AppShell, and each one's constructor subscribed here without
    /// anything ever unsubscribing the last one. The tab bar's own reselect handler stays cleaned up
    /// on the native side (TabReselect.Android.cs), but this cross-platform event had no such
    /// guard — so the next reselect after a language switch called back into every AppShell that
    /// switch had ever discarded, including ones whose Shell was already torn down, which is what
    /// actually crashed with a NullReferenceException in ShellSectionRenderer. Only the current
    /// AppShell should ever hear this, so subscribing replaces the previous listener instead of
    /// joining it.
    /// </summary>
    private EventHandler<string>? _reselected;

    /// <summary>Raised with the route of the tab that was tapped again.</summary>
    public event EventHandler<string>? Reselected
    {
        add => _reselected = value;
        remove
        {
            if (ReferenceEquals(_reselected, value)) _reselected = null;
        }
    }

    private void Raise(string route) => _reselected?.Invoke(this, route);

    /// <summary>
    /// Attaches to whatever bottom bar is on screen now.
    ///
    /// Called every time the library comes forward rather than once at startup, because the bar is
    /// rebuilt whenever the shell is — which happens on every change of language. Attaching twice
    /// to the same bar is harmless: it checks.
    /// </summary>
    public partial void Watch();
}
