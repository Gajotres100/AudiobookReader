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
        await _viewModel.LoadAsync();
    }

    /// <summary>
    /// Playback keeps going while this page is gone, so leaving only stops the page's own timer.
    /// The position is written out here rather than trusting the periodic save, so backing out
    /// never loses the last few seconds.
    /// </summary>
    protected override async void OnDisappearing()
    {
        base.OnDisappearing();
        await _viewModel.SavePositionAsync();
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

    /// <summary>
    /// Seeks only once the finger lifts. Seeking on every value change would fight the once-a-second
    /// position update and make the thumb jump around under the user.
    /// </summary>
    private void OnScrubberDragCompleted(object? sender, EventArgs e)
    {
        if (sender is Slider slider) _viewModel.SeekToFractionCommand.Execute(slider.Value);
    }
}
