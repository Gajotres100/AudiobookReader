namespace AudioBookReader.App.Services;

// No iOS equivalent of Android's ActionProcessText system chooser (a system-wide "act on this
// selected text" registry). No-op for v1; UIReferenceLibraryViewController (the built-in
// dictionary) is a possible future replacement, not a launch blocker.
public partial class WordTranslator
{
    public partial void Translate(string text) { }
}
