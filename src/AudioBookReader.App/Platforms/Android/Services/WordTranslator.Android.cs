using Android.Content;
using AudioBookReader.App.Resources.Strings;

namespace AudioBookReader.App.Services;

public partial class WordTranslator
{
    public partial void Translate(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
        if (activity is null) return;

        try
        {
            // The same action the system offers when text is selected anywhere on the phone —
            // every translator worth having already answers it, so this asks the one thing every
            // such app already understands instead of picking one on the user's behalf. Nothing
            // about the length of the text is special-cased: Android hands a selected paragraph to
            // this exact intent every time someone selects one in any other app, so a dragged
            // phrase from the reader is not asking these apps to do anything new.
            var request = new Intent(Intent.ActionProcessText)
                .SetType("text/plain")
                .PutExtra(Intent.ExtraProcessText, text)
                .PutExtra(Intent.ExtraProcessTextReadonly, true);

            // A chooser rather than starting the first match: several apps can register for this,
            // and which one someone wants for a book is exactly the kind of thing they should
            // still get to pick, the way they would picking it from a text-selection menu. Which
            // apps actually show up here — a dictionary, a translator, an assistant that can
            // explain a passage — is entirely down to what is installed and what that app chose
            // to register for; this app has no say in the list and no way to add an entry to it.
            activity.StartActivity(Intent.CreateChooser(request, Strings.Reader_TranslateChooser));
        }
        catch (ActivityNotFoundException)
        {
            // No app on the phone answers this. Quietly: a book someone is reading is not the
            // moment to explain what a "process text" handler is.
        }
        catch (Exception ex)
        {
            AppLog.Error($"translating '{text}'", ex);
        }
    }
}
