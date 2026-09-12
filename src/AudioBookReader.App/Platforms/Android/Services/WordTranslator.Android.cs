using Android.Content;
using AudioBookReader.App.Resources.Strings;

namespace AudioBookReader.App.Services;

public partial class WordTranslator
{
    public partial void Translate(string word)
    {
        if (string.IsNullOrWhiteSpace(word)) return;

        var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
        if (activity is null) return;

        try
        {
            // The same action the system offers when text is selected anywhere on the phone —
            // every translator worth having already answers it, so this asks the one thing every
            // such app already understands instead of picking one on the user's behalf.
            var request = new Intent(Intent.ActionProcessText)
                .SetType("text/plain")
                .PutExtra(Intent.ExtraProcessText, word)
                .PutExtra(Intent.ExtraProcessTextReadonly, true);

            // A chooser rather than starting the first match: several apps can register for this,
            // and which one someone wants for a book is exactly the kind of thing they should
            // still get to pick, the way they would picking it from a text-selection menu.
            activity.StartActivity(Intent.CreateChooser(request, Strings.Reader_TranslateChooser));
        }
        catch (ActivityNotFoundException)
        {
            // No app on the phone answers this. Quietly: a book someone is reading is not the
            // moment to explain what a "process text" handler is.
        }
        catch (Exception ex)
        {
            AppLog.Error($"translating '{word}'", ex);
        }
    }
}
