namespace AudioBookReader.App.Services;

/// <summary>
/// Makes the app usable with a remote — arrow keys and OK — which is all a television has.
///
/// The app was built for fingers: book rows, chapters, settings choices and the download
/// destination are all layouts with a tap gesture on them, and a tap gesture is invisible to
/// keyboard focus. Rewriting each of those as a button would have touched every page; instead,
/// anything carrying a tap gesture is made focusable, shows a ring when focused, and treats OK as
/// the tap. Buttons already take focus and only get the ring, so it is visible which one is chosen.
///
/// Harmless on a phone: focus only appears once a key is pressed, and touch works as before.
/// </summary>
public static partial class RemoteFocus
{
    /// <summary>Hooks the handlers. Called once, while the app is being built.</summary>
    public static partial void Register();
}
