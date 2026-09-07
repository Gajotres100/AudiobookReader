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
    /// <summary>Raised with the route of the tab that was tapped again.</summary>
    public event EventHandler<string>? Reselected;

    private void Raise(string route) => Reselected?.Invoke(this, route);

    /// <summary>
    /// Attaches to whatever bottom bar is on screen now.
    ///
    /// Called every time the library comes forward rather than once at startup, because the bar is
    /// rebuilt whenever the shell is — which happens on every change of language. Attaching twice
    /// to the same bar is harmless: it checks.
    /// </summary>
    public partial void Watch();
}
