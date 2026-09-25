using AudioBookReader.App.Resources.Strings;
using System.Globalization;
using AudioBookReader.App.Services;
using AudioBookReader.App.ViewModels;

namespace AudioBookReader.App.Views;

public partial class ReaderPage : ContentPage
{
    /// <summary>
    /// Scheme the page's script navigates to in order to report a tap. Intercepting a navigation
    /// is the portable way for a WebView to call back into the app.
    /// </summary>
    private const string SeekScheme = "abr://seek/";

    /// <summary>Scheme the page's swipe handler navigates to in order to leave the document.</summary>
    private const string PageScheme = "abr://page/";

    /// <summary>Reports which page of the document is showing, so the footer can say so.</summary>
    private const string PagesScheme = "abr://pages/";

    /// <summary>A pull downwards asks for the chapter list.</summary>
    private const string ChaptersUrl = "abr://chapters";

    /// <summary>Reports the text size after a pinch, so it can be remembered.</summary>
    private const string FontScheme = "abr://font/";

    /// <summary>A plain tap on the page, which shows and hides the controls.</summary>
    private const string TapUrl = "abr://tap";

    /// <summary>A word or phrase selected in the reader, carried URL-encoded.</summary>
    private const string TranslateScheme = "abr://translate/";

    /// <summary>
    /// The sentence a selection starts in, chosen from the reader's menu — the same coordinate
    /// <see cref="SeekScheme"/> already carries, not a raw text offset the page has no reason to
    /// know.
    /// </summary>
    private const string BookmarkScheme = "abr://bookmark/";

    private readonly ReaderViewModel _viewModel;
    private readonly WordTranslator _translator;

    public ReaderPage(ReaderViewModel viewModel, WordTranslator translator)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
        _translator = translator;
    }

    /// <summary>
    /// Attaches the page to its view model.
    ///
    /// Paired with the detach in OnDisappearing, and deliberately not done in the constructor:
    /// those two fire on every window deactivate and reactivate, not once per instance. Subscribing
    /// in the constructor meant that the first time the user left the app and came back, the reader
    /// silently stopped — no highlight, no page turns, no restyling, and nothing in the log.
    /// </summary>
    private void Attach()
    {
        Detach();

        _viewModel.PropertyChanged += OnViewModelChanged;
        _viewModel.HighlightRequested += OnHighlightRequested;
        _viewModel.PageJumpRequested += OnPageJumpRequested;
        _viewModel.AppearanceChanged += OnAppearanceChanged;
    }

    private void Detach()
    {
        _viewModel.PropertyChanged -= OnViewModelChanged;
        _viewModel.HighlightRequested -= OnHighlightRequested;
        _viewModel.PageJumpRequested -= OnPageJumpRequested;
        _viewModel.AppearanceChanged -= OnAppearanceChanged;
    }

    /// <summary>
    /// Restyles the page in place rather than rebuilding it.
    ///
    /// Rebuilding would reload the web view, and a reload loses the page the reader was on — which
    /// is a strange thing to happen because someone chose a different paper colour.
    /// </summary>
    private async void OnAppearanceChanged(object? sender, EventArgs e)
    {
        try
        {
            await Reader.EvaluateJavaScriptAsync(_viewModel.AppearanceScript);
        }
        catch (Exception ex)
        {
            AppLog.Error("applying appearance", ex);
        }
    }

    private async void OnPageJumpRequested(object? sender, int page)
    {
        try
        {
            await Reader.EvaluateJavaScriptAsync($"goToPage({page})");
        }
        catch (Exception ex)
        {
            AppLog.Error($"goToPage({page})", ex);
        }
    }

    /// <summary>
    /// Jumps only once the finger lifts. Turning pages on every value change would fight the
    /// slider's own position updates and make the thumb jump under the user.
    /// </summary>
    private void OnPageSliderChanged(object? sender, EventArgs e)
    {
        if (sender is Slider slider) _viewModel.JumpToPage((int)Math.Round(slider.Value));
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        // Reading along means looking at the screen without touching it, so the usual idle timeout
        // fights the feature: the display goes dark mid-sentence.
        DeviceDisplay.Current.KeepScreenOn = true;

        Attach();

        // An unreadable ebook throws out of here, and an exception from an async void override
        // reaches the Android runtime and kills the app — leaving no way back to the button that
        // would have deleted the broken book.
        try
        {
            await _viewModel.LoadAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("opening the reader", ex);
            _viewModel.FollowStatus = Strings.Reader_CouldNotOpen;
        }
    }

    protected override async void OnDisappearing()
    {
        base.OnDisappearing();

        DeviceDisplay.Current.KeepScreenOn = false;

        // Detached before anything is awaited. Reading the position asks the web view a question
        // while it is being torn down, and that call can come back late or never — so doing the
        // teardown afterwards risks a stale continuation dismantling a page the user has already
        // come back to, which is the exact fault this pairing exists to prevent.
        Detach();
        _viewModel.Leave();
        _ = StopMeasuringUnlessStillShownAsync();

        try
        {
            await _viewModel.SavePositionAsync(await ReadTopSentenceAsync(), leaving: true);
        }
        catch (Exception ex)
        {
            AppLog.Error("saving the reading position", ex);
        }
    }

    /// <summary>
    /// Ends reading-along when the reader was closed, and keeps it when only the screen went off.
    ///
    /// The two look the same from here — locking the phone takes the page away just as going back
    /// does — so the difference is read from what is on screen once the moment has passed: after a
    /// lock the reader is still the shell's page, after closing it is not. The book goes on playing
    /// in a pocket, and measuring it there is what lets the text be in step the moment the screen
    /// comes back, instead of only while someone is watching it.
    /// </summary>
    private async Task StopMeasuringUnlessStillShownAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1));

            if (Shell.Current?.CurrentPage == this) return;

            await _viewModel.StopMeasuringAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("stopping reading-along", ex);
        }
    }

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ReaderViewModel.Html)) return;

        Reader.Source = new HtmlWebViewSource { Html = _viewModel.Html };
    }

    /// <summary>
    /// Brings a sentence to the eye, marked as the narrator's place only when there is a narrator.
    ///
    /// The amber wash means "this is being read aloud right now". A book with no audio, or one
    /// whose text is simply being read rather than followed, has nothing for it to mean — and
    /// painting it anyway left a sentence permanently highlighted in a plain ebook from the moment
    /// it was opened, since nothing would ever call this again to clear it. keepInView does the
    /// half that is always wanted, which is the scrolling.
    /// </summary>
    private Task ShowSentenceAsync(int sentenceIndex) =>
        Reader.EvaluateJavaScriptAsync(
            _viewModel.IsFollowing ? $"highlight({sentenceIndex})" : $"keepInView({sentenceIndex})");

    private async void OnHighlightRequested(object? sender, int sentenceIndex)
    {
        try
        {
            await ShowSentenceAsync(sentenceIndex);
        }
        catch (Exception ex)
        {
            // Worth recording: a highlight that never lands is the whole feature failing quietly.
            AppLog.Error($"highlight({sentenceIndex})", ex);
        }
    }

    private async void OnSelectionBookmarked(string sentenceIndexText)
    {
        if (!int.TryParse(sentenceIndexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sentenceIndex))
            return;

        try
        {
            await _viewModel.SaveSelectionBookmarkAsync(sentenceIndex);
        }
        catch (Exception ex)
        {
            AppLog.Error($"bookmarking sentence {sentenceIndex}", ex);
        }
    }

    /// <summary>
    /// The page talks back by navigating to an <c>abr://</c> URL, which is cancelled here and read
    /// as a message. It is the one bridge that needs no platform-specific code.
    /// </summary>
    private void OnReaderNavigating(object? sender, WebNavigatingEventArgs e)
    {
        if (e.Url.StartsWith(PagesScheme, StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;

            var parts = e.Url[PagesScheme.Length..].TrimEnd('/').Split('/');

            if (parts.Length >= 2 && int.TryParse(parts[0], out var page) && int.TryParse(parts[1], out var count))
            {
                // The sentence at the top rides along so the footer can name the chapter; a page
                // count on its own cannot, since scrolling never changes the document.
                var top = parts.Length >= 3 && int.TryParse(parts[2], out var sentence) ? sentence : -1;
                _viewModel.OnPagesReported(page, count, top);
            }

            return;
        }

        if (e.Url.StartsWith(PageScheme, StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;

            var direction = e.Url[PageScheme.Length..].TrimEnd('/');
            if (direction.Equals("next", StringComparison.OrdinalIgnoreCase))
                _viewModel.NextDocumentCommand.Execute(null);
            else
                _viewModel.PreviousDocumentCommand.Execute(null);

            return;
        }

        if (e.Url.StartsWith(TapUrl, StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            _viewModel.OnTapped();
            return;
        }

        if (e.Url.StartsWith(ChaptersUrl, StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            _viewModel.JumpToChapterCommand.Execute(null);
            return;
        }

        if (e.Url.StartsWith(FontScheme, StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;

            if (int.TryParse(e.Url[FontScheme.Length..].TrimEnd('/'), out var size))
                _viewModel.OnFontSizeChanged(size);

            return;
        }

        if (e.Url.StartsWith(SeekScheme, StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;

            var value = e.Url[SeekScheme.Length..].TrimEnd('/');
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sentenceIndex))
                _viewModel.OnSentenceTapped(sentenceIndex);

            return;
        }

        if (e.Url.StartsWith(TranslateScheme, StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            _translator.Translate(Uri.UnescapeDataString(e.Url[TranslateScheme.Length..].TrimEnd('/')));
            return;
        }

        if (!e.Url.StartsWith(BookmarkScheme, StringComparison.OrdinalIgnoreCase)) return;

        e.Cancel = true;
        OnSelectionBookmarked(Uri.UnescapeDataString(e.Url[BookmarkScheme.Length..].TrimEnd('/')));
    }

    /// <summary>
    /// Opens a freshly loaded document on the right page — its last one when the reader arrived by
    /// swiping backwards, so the text carries on where the eye left it, and otherwise wherever the
    /// remembered sentence is, so a book that was never "following" (an ebook, or a paired one
    /// simply being read rather than listened to) still resumes where it was left rather than at
    /// the top of the chapter.
    ///
    /// EntryPage wins when set: it is only ever set for an explicit swipe between documents, and
    /// that request must not be second-guessed by a highlight left over from before the swipe.
    /// </summary>
    private async void OnReaderNavigated(object? sender, WebNavigatedEventArgs e)
    {
        try
        {
            // Re-applied here because the highlight requested while the previous document was
            // still on screen was asked of a script that did not exist yet.
            if (_viewModel.EntryPage is { } page)
                await Reader.EvaluateJavaScriptAsync($"goToPage({page})");
            else if (_viewModel.PendingHighlight >= 0)
                await ShowSentenceAsync(_viewModel.PendingHighlight);
        }
        catch (Exception)
        {
            // Landing on the first page is a fine fallback.
        }
    }

    private async Task<int?> ReadTopSentenceAsync()
    {
        try
        {
            var result = await Reader.EvaluateJavaScriptAsync("topSentence()");

            return int.TryParse(result?.Trim('"'), out var index) && index >= 0 ? index : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
