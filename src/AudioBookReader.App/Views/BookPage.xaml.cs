using AudioBookReader.App.Resources.Strings;
using AudioBookReader.App.Services;
using AudioBookReader.App.ViewModels;

namespace AudioBookReader.App.Views;

public partial class BookPage : ContentPage
{
    private readonly BookViewModel _viewModel;

    public BookPage(BookViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    /// <summary>Set while one of the app's dialogs is over the page; see the reader's.</summary>
    private bool _underDialog;

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        if (_underDialog)
        {
            _underDialog = false;
            return;
        }

        try
        {
            await _viewModel.LoadAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("opening the book page", ex);
        }
    }

    /// <summary>
    /// Playback keeps going while this page is gone, so leaving only stops the page's own timer.
    /// The position is written out here rather than trusting the periodic save, so backing out
    /// never loses the last few seconds.
    /// </summary>
    protected override async void OnDisappearing()
    {
        base.OnDisappearing();

        // A dialog over the page — the sleep timer, the speed — is not leaving it, and disposing
        // the view model here left the dialog's answer nothing alive to act on.
        if (Dialogs.IsShowing)
        {
            _underDialog = true;
            return;
        }

        try
        {
            await _viewModel.SavePositionAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("saving the listening position", ex);
        }

        _viewModel.Dispose();
    }

    /// <summary>
    /// Back closes the chapter list before it leaves the page.
    ///
    /// The list sits over the cover rather than on a page of its own, so without this the system
    /// gesture would throw the user out of the book to dismiss it.
    /// </summary>
    protected override bool OnBackButtonPressed()
    {
        if (_viewModel.ChaptersExpanded)
        {
            _viewModel.ChaptersExpanded = false;
            return true;
        }

        return base.OnBackButtonPressed();
    }

    private CancellationTokenSource? _holdingBookmark;

    /// <summary>
    /// Distinguishes a tap on the flag from a hold.
    ///
    /// MAUI has no long-press gesture, so it is timed here: holding past the threshold saves the
    /// spot and marks the press as spent, and a release before then opens the list instead.
    /// </summary>
    /// <summary>When a finger last pressed or let go of the bookmark button; its click is then already dealt with.</summary>
    private DateTime _touchedBookmarkAt = DateTime.MinValue;

    /// <summary>
    /// The remote's OK on the bookmark button. A remote has no press-and-hold — OK arrives as a
    /// click, with no press or release around it — so on a television the button did nothing at
    /// all. It asks instead which of the two the finger would have chosen.
    /// </summary>
    private async void OnBookmarkClicked(object? sender, EventArgs e)
    {
        if (DateTime.UtcNow - _touchedBookmarkAt < TimeSpan.FromSeconds(1)) return;

        try
        {
            var add = Strings.Bookmarks_Add;
            var list = Strings.Bookmarks_Title;

            var choice = await Dialogs.ChooseAsync(Strings.Bookmarks_Title, Strings.Common_Cancel, null, [add, list]);

            if (choice == add) _viewModel.AddBookmarkCommand.Execute(null);
            else if (choice == list) _viewModel.OpenBookmarksCommand.Execute(null);
        }
        catch (Exception ex)
        {
            AppLog.Error("bookmark choice", ex);
        }
    }

    private async void OnBookmarkPressed(object? sender, EventArgs e)
    {
        _touchedBookmarkAt = DateTime.UtcNow;

        _holdingBookmark?.Cancel();
        _holdingBookmark = new CancellationTokenSource();

        var token = _holdingBookmark.Token;

        try
        {
            await Task.Delay(450, token);
            if (token.IsCancellationRequested) return;

            _holdingBookmark = null;
            _viewModel.AddBookmarkCommand.Execute(null);
        }
        catch (TaskCanceledException)
        {
            // Released early, so it was a tap.
        }
    }

    private void OnBookmarkReleased(object? sender, EventArgs e)
    {
        _touchedBookmarkAt = DateTime.UtcNow;

        // Null means the hold already fired and saved a bookmark; this release is its tail.
        if (_holdingBookmark is null) return;

        _holdingBookmark.Cancel();
        _holdingBookmark = null;

        _viewModel.OpenBookmarksCommand.Execute(null);
    }

    /// <summary>
    /// Tells the view model how wide the bar is, since the played part is drawn in pixels.
    /// </summary>
    private void OnTrackSizeChanged(object? sender, EventArgs e)
    {
        if (sender is VisualElement element) _viewModel.TrackWidth = element.Width;
    }

    /// <summary>
    /// Seeks only once the finger lifts. Seeking on every value change would fight the once-a-second
    /// position update and make the thumb jump around under the user.
    /// </summary>
    private void OnScrubberDragCompleted(object? sender, EventArgs e)
    {
        if (sender is Slider slider) _viewModel.SeekToFractionCommand.Execute(slider.Value);
    }
}
