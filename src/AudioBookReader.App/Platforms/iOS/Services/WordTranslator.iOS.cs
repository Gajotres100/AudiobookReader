using System.Globalization;
using AudioBookReader.App.Resources.Strings;
using UIKit;

namespace AudioBookReader.App.Services;

/// <summary>
/// Translate or explain a word or phrase, the iOS way.
///
/// iOS has no counterpart to Android's "process text" registry, where every translator on the
/// phone offers itself for a selection — so there is no list of apps to show, and until now the
/// button did nothing at all. Instead it offers the three routes iOS does have: the system
/// dictionary (what "Look Up" shows), Google Translate in an in-app browser — which works with
/// nothing installed — and the share sheet, where any installed app that takes text (a translator,
/// DeepL, an assistant) appears.
/// </summary>
public partial class WordTranslator
{
    public partial void Translate(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                Offer(text.Trim());
            }
            catch (Exception ex)
            {
                AppLog.Error($"translating '{text}'", ex);
            }
        });
    }

    private static void Offer(string text)
    {
        if (Presenter() is not { } presenter) return;

        var sheet = UIAlertController.Create(Strings.Reader_TranslateChooser, text, UIAlertControllerStyle.ActionSheet);

        sheet.AddAction(UIAlertAction.Create(Strings.Reader_TranslateDictionary, UIAlertActionStyle.Default, _ =>
            presenter.PresentViewController(new UIReferenceLibraryViewController(text), true, null)));

        sheet.AddAction(UIAlertAction.Create(Strings.Reader_TranslateOnline, UIAlertActionStyle.Default, action =>
        {
            _ = OpenTranslatorAsync(text);
        }));

        sheet.AddAction(UIAlertAction.Create(Strings.Reader_TranslateShare, UIAlertActionStyle.Default, _ =>
        {
            var share = new UIActivityViewController([new Foundation.NSString(text)], null);
            Anchor(share, presenter);
            presenter.PresentViewController(share, true, null);
        }));

        sheet.AddAction(UIAlertAction.Create(Strings.Common_Cancel, UIAlertActionStyle.Cancel, null));

        Anchor(sheet, presenter);
        presenter.PresentViewController(sheet, true, null);
    }

    /// <summary>Into the language the app is shown in, from whatever the book is written in.</summary>
    private static Task OpenTranslatorAsync(string text)
    {
        var target = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        var url = $"https://translate.google.com/?sl=auto&tl={target}&op=translate&text={Uri.EscapeDataString(text)}";

        return Browser.Default.OpenAsync(url, BrowserLaunchMode.SystemPreferred);
    }

    /// <summary>
    /// On an iPad a sheet is a popover and must be pinned to something, or UIKit refuses to show it;
    /// the middle of the screen is as good as anywhere for a word picked from a page.
    /// </summary>
    private static void Anchor(UIViewController sheet, UIViewController presenter)
    {
        if (sheet.PopoverPresentationController is not { } popover || presenter.View is not { } view) return;

        popover.SourceView = view;
        popover.SourceRect = new CoreGraphics.CGRect(view.Bounds.Width / 2, view.Bounds.Height / 2, 0, 0);
        popover.PermittedArrowDirections = 0;
    }

    /// <summary>The controller on top, which is the one that can present another.</summary>
    private static UIViewController? Presenter()
    {
        var controller = Platform.GetCurrentUIViewController();

        while (controller?.PresentedViewController is { } above) controller = above;
        return controller;
    }
}
