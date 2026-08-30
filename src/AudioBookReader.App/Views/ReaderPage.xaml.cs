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

    private readonly ReaderViewModel _viewModel;

    public ReaderPage(ReaderViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;

        _viewModel.PropertyChanged += OnViewModelChanged;
        _viewModel.HighlightRequested += OnHighlightRequested;
        _viewModel.PageJumpRequested += OnPageJumpRequested;
        _viewModel.AppearanceChanged += OnAppearanceChanged;
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

        await _viewModel.LoadAsync();
    }

    protected override async void OnDisappearing()
    {
        base.OnDisappearing();

        DeviceDisplay.Current.KeepScreenOn = false;

        await _viewModel.SavePositionAsync(await ReadTopSentenceAsync());

        _viewModel.PropertyChanged -= OnViewModelChanged;
        _viewModel.HighlightRequested -= OnHighlightRequested;
        _viewModel.PageJumpRequested -= OnPageJumpRequested;
        _viewModel.AppearanceChanged -= OnAppearanceChanged;
        _viewModel.Dispose();
    }

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ReaderViewModel.Html)) return;

        Reader.Source = new HtmlWebViewSource { Html = _viewModel.Html };
    }

    private async void OnHighlightRequested(object? sender, int sentenceIndex)
    {
        try
        {
            await Reader.EvaluateJavaScriptAsync($"highlight({sentenceIndex})");
        }
        catch (Exception ex)
        {
            // Worth recording: a highlight that never lands is the whole feature failing quietly.
            AppLog.Error($"highlight({sentenceIndex})", ex);
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

            var parts = e.Url[PagesScheme.Length..].Split('/');
            if (parts.Length == 2 && int.TryParse(parts[0], out var page) && int.TryParse(parts[1], out var count))
                _viewModel.OnPagesReported(page, count);

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

        if (!e.Url.StartsWith(SeekScheme, StringComparison.OrdinalIgnoreCase)) return;

        e.Cancel = true;

        var value = e.Url[SeekScheme.Length..].TrimEnd('/');
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sentenceIndex))
            _viewModel.OnSentenceTapped(sentenceIndex);
    }

    /// <summary>
    /// Opens a freshly loaded document on the right page — its last one when the reader arrived by
    /// swiping backwards, so the text carries on where the eye left it.
    /// </summary>
    private async void OnReaderNavigated(object? sender, WebNavigatedEventArgs e)
    {
        try
        {
            // Re-applied here because the highlight requested while the previous document was
            // still on screen was asked of a script that did not exist yet.
            if (_viewModel.IsFollowing && _viewModel.PendingHighlight >= 0)
                await Reader.EvaluateJavaScriptAsync($"highlight({_viewModel.PendingHighlight})");
            else if (_viewModel.EntryPage != 0)
                await Reader.EvaluateJavaScriptAsync($"goToPage({_viewModel.EntryPage})");
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
