namespace AudioBookReader.App.Services;

/// <summary>
/// Looks up a word or a selected phrase, by handing it to whatever the phone already uses for
/// that.
///
/// Deliberately not a translator this app writes itself: picking a provider, a target language
/// and an API key is a whole feature on its own, and Android already carries one — the same
/// "Translate" action that shows up when text is selected anywhere on the phone. A press and hold
/// in the reader, extended by a drag to more than one word, is the same request in different
/// clothes, so it is handed to the same place. Whichever app answers it — a dictionary, a
/// translator, an assistant able to explain a passage — is whatever the phone has installed for
/// this system action, not a list this app curates or a service it calls itself: nothing in the
/// selected text leaves the device except by the app the user themselves picks from that chooser.
/// </summary>
public partial class WordTranslator
{
    /// <param name="text">The word or phrase selected in the reader.</param>
    /// <param name="chooserTitle">
    /// What the picker says while offering it. Android has no separate "translate" and "explain"
    /// actions — both reach this same method and open the same list of apps — so the title is the
    /// only place the reader's two buttons are actually different from each other.
    /// </param>
    public partial void Translate(string text, string chooserTitle);
}
