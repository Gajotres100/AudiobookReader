namespace AudioBookReader.App.Services;

// iOS-5: MAUI Shell's iOS tab bar needs a different hook than Android's ViewGroup walk to
// NavigationBarView (likely a handler mapper on the iOS tab bar controller). Not launch-blocking —
// a tab tap that re-navigates instead of resetting to the list is a minor UX quirk, not broken
// functionality.
public partial class TabReselect
{
    public partial void Watch() { }
}
