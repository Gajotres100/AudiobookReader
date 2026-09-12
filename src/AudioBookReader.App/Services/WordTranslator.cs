namespace AudioBookReader.App.Services;

/// <summary>
/// Looks up a single word, by handing it to whatever the phone already uses for that.
///
/// Deliberately not a translator this app writes itself: picking a provider, a target language
/// and an API key is a whole feature on its own, and Android already carries one — the same
/// "Translate" action that shows up when text is selected anywhere on the phone. A press and hold
/// on a word in the reader is the same request in different clothes, so it is handed to the same
/// place. Whichever app answers it, and in whatever language it was last asked for, is that app's
/// decision to remember, not this one's to duplicate.
/// </summary>
public partial class WordTranslator
{
    /// <summary>Offers the word to every app on the phone that can act on selected text.</summary>
    public partial void Translate(string word);
}
