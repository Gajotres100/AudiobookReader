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

    protected override async void OnAppearing()
    {
        base.OnAppearing();

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
    /// Back closes whatever is covering the player before it leaves the page.
    ///
    /// Both the chapter list and the housekeeping panel sit over the player rather than on pages of
    /// their own, so without this the system gesture would throw the user out of the book to
    /// dismiss a panel.
    /// </summary>
    protected override bool OnBackButtonPressed()
    {
        if (_viewModel.IsDetailsOpen)
        {
            _viewModel.IsDetailsOpen = false;
            return true;
        }

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
    private async void OnBookmarkPressed(object? sender, EventArgs e)
    {
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
