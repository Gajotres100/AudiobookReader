using AudioBookReader.App.Resources.Strings;
using AudioBookReader.App.Services;
using AudioBookReader.Core.Data;
using AudioBookReader.App.Views;
using AudioBookReader.Core.Servers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AudioBookReader.App.ViewModels;

/// <summary>
/// One book on the server, before deciding to bring it here.
///
/// A page of its own rather than a tap that starts a download. What is about to be copied over a
/// phone's connection is worth seeing first: how long it is, whether it has the text as well as the
/// voice, and — the one that decides everything — whether the server keeps it as one file or forty.
/// </summary>
[QueryProperty(nameof(ItemId), "id")]
public partial class ServerBookViewModel : ObservableObject
{
    private readonly ServerConnection _server;
    private readonly LibraryDatabase _database;
    private readonly DownloadQueue _downloads;

    /// <summary>
    /// Watches the download queue rather than owning the transfer.
    ///
    /// The download lives in a foreground service now, so it outlives this page — and this page
    /// outlives nothing: leave it and come back and it is a new object. Subscribing means a page
    /// reopened mid-download shows where the transfer got to instead of an idle Preuzmi button
    /// over a book that is already half here.
    /// </summary>
    public ServerBookViewModel(
        ServerConnection server,
        LibraryDatabase database,
        DownloadQueue downloads)
    {
        _server = server;
        _database = database;
        _downloads = downloads;

    }

    /// <summary>
    /// Starts listening, and catches up on anything already under way.
    ///
    /// Called every time the page comes forward rather than once, because pushing the destination
    /// chooser over it counts as going away — and a page that unsubscribed on its way to asking
    /// where the book goes would hear nothing about the download it then started.
    /// </summary>
    public void Attach()
    {
        _downloads.Changed -= OnDownloadChanged;
        _downloads.Changed += OnDownloadChanged;

        if (_downloads.Status.ItemId == ItemId) Show(_downloads.Status);
    }

    /// <summary>Called by the page as it goes, so a page nobody can see stops holding the queue.</summary>
    public void Detach() => _downloads.Changed -= OnDownloadChanged;

    private void OnDownloadChanged(object? sender, DownloadStatus status)
    {
        // Another book's download is none of this page's business, except that it is the reason
        // this one cannot start yet — which the button says on its own.
        if (status.ItemId != ItemId) return;

        MainThread.BeginInvokeOnMainThread(() => Show(status));
    }

    private void Show(DownloadStatus status)
    {
        IsDownloading = status.IsRunning;
        Progress = status.Fraction;
        Percent = status.Percent;
        Status = status.Message;

        if (status.BookId is { } id) BookId = id;
    }

    public string ItemId { get; set; } = "";

    private ServerBookDetail? _detail;

    [ObservableProperty]
    public partial string Title { get; set; } = "";

    [ObservableProperty]
    public partial string Author { get; set; } = "";

    [ObservableProperty]
    public partial string CoverUrl { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSeries))]
    public partial string Series { get; set; } = "";

    public bool HasSeries => Series.Length > 0;

    /// <summary>What the server has of it, spelled out rather than in glyphs.</summary>
    [ObservableProperty]
    public partial string Contents { get; set; } = "";

    [ObservableProperty]
    public partial string Length { get; set; } = "";

    [ObservableProperty]
    public partial string Chapters { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CannotDownload))]
    public partial bool CanDownload { get; set; }

    public bool CannotDownload => !CanDownload && Obstacle.Length > 0;

    /// <summary>Why it cannot be brought over, when it cannot.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CannotDownload))]
    public partial string Obstacle { get; set; } = "";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsDownloading { get; set; }

    public bool IsIdle => !IsDownloading;

    [ObservableProperty]
    public partial double Progress { get; set; }

    /// <summary>How far along, as a figure. A bar says whether it moves; this says how much is left.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPercent))]
    public partial string Percent { get; set; } = "";

    public bool HasPercent => Percent.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string Status { get; set; } = "";

    public bool HasStatus => Status.Length > 0;

    /// <summary>Set once the book is here, so the page can offer to open it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInLibrary))]
    public partial int? BookId { get; set; }

    public bool IsInLibrary => BookId is not null;

    public async Task LoadAsync()
    {
        if (_detail is not null || string.IsNullOrEmpty(ItemId)) return;

        IsBusy = true;
        Status = Strings.Server_ReadingDetails;

        try
        {
            _detail = await _server.GetBookAsync(ItemId);

            var book = _detail.Book;

            Title = book.Title;
            Author = book.Author ?? "";
            CoverUrl = _server.CoverUrl(book.Id);
            Series = book.InSeries
                ? string.IsNullOrEmpty(book.Sequence) ? book.Series! : $"{book.Series} #{book.Sequence}"
                : "";

            Contents = Describe(book);
            Length = book.DurationSeconds > 0
                ? string.Format(Strings.Duration_Label, TimeSpan.FromSeconds(book.DurationSeconds).ToString(@"h\:mm\:ss"))
                : "";

            Chapters = _detail.Chapters.Count > 0
                ? string.Format(Strings.Chapter_Count, _detail.Chapters.Count)
                : "";

            // The one thing that decides whether this can be brought over at all, said here rather
            // than discovered by pressing a button that then refuses.
            Obstacle = _detail.AudioFiles.Count > 1
                ? string.Format(Strings.Server_SplitFilesLong, _detail.AudioFiles.Count)
                : _detail.AudioFiles.Count == 0 && _detail.Ebook is null
                    ? Strings.Server_NothingToDownload
                    : "";

            CanDownload = Obstacle.Length == 0;

            // Already here? Then say so instead of offering to fetch it twice. Matched by title,
            // which is all the two sides share before a file has been downloaded and hashed.
            BookId = (await _database.GetBooksAsync())
                .FirstOrDefault(b => string.Equals(b.Title, book.Title, StringComparison.CurrentCultureIgnoreCase))
                ?.Id;

            // A download for this book may already be running — started here and then left, or
            // still going from before this page existed. That outranks the empty line that means
            // nothing is happening.
            Show(_downloads.Status.ItemId == ItemId ? _downloads.Status : new DownloadStatus());
        }
        catch (Exception ex)
        {
            AppLog.Error("reading a book from the server", ex);
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string Describe(ServerBook book) => (book.HasAudio, book.HasEbook) switch
    {
        (true, true) => Strings.Server_HasBoth,
        (true, false) => Strings.Server_AudioOnly,
        (false, true) => Strings.Server_EbookOnly,
        _ => "",
    };

    [RelayCommand]
    private void Cancel()
    {
        _downloads.Stop();
        Status = Strings.Server_Cancelling;
    }

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (_detail is null || !CanDownload || IsDownloading) return;

        // One at a time. Two large transfers over one phone connection finish no sooner together
        // than in turn, and a second foreground service would post a second permanent notification.
        if (_downloads.Status.IsRunning)
        {
            Status = Strings.Download_AlreadyRunning;
            return;
        }

        if (await AgreeOnADestinationAsync() is not { } toAppStorage) return;

        IsDownloading = true;
        Progress = 0;
        Percent = "";
        Status = Strings.Download_Preparing;

        // Handed to a service and forgotten. Everything from here arrives through the queue, which
        // is what lets it carry on with this page closed and the screen locked.
        _downloads.Start(ItemId, Title, toAppStorage);
    }

    /// <summary>
    /// Settles where the book will go, at the one moment the question matters.
    ///
    /// Two places, and only two. A folder of the user's own is where books belong — beside the ones
    /// already there, visible to every other app and over a cable. The app's own storage is the
    /// other honest answer: nobody else can see it and it leaves with the app. Neither should
    /// happen by accident, so both are named and nothing is a fallback.
    ///
    /// Asked here and nowhere else. The page used to offer a folder too, which made the same
    /// decision available twice with no way to tell which one had won.
    ///
    /// A page rather than an action sheet, because the difference between the two answers does not
    /// fit on one line and is the whole point of asking.
    /// </summary>
    /// <returns>Whether to use app storage, or null when the download was called off.</returns>
    private static async Task<bool?> AgreeOnADestinationAsync()
    {
        var services = IPlatformApplication.Current?.Services;
        if (services is null) return null;

        var page = services.GetRequiredService<DownloadDestinationPage>();

        await Shell.Current.Navigation.PushModalAsync(page);

        return await page.Answer;
    }

    [RelayCommand]
    private Task OpenAsync() =>
        BookId is { } id ? Shell.Current.GoToAsync($"book?id={id}") : Task.CompletedTask;

    [RelayCommand]
    private Task CloseAsync() => Shell.Current.GoToAsync("..");
}
