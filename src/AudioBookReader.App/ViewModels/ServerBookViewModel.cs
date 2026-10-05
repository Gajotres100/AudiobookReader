using AudioBookReader.Core.Books;
using AudioBookReader.App.Resources.Strings;
using AudioBookReader.App.Services;
using AudioBookReader.Core.Data;
using AudioBookReader.Core.Models;
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
    private readonly ServerConnections _servers;
    private ServerConnection? _serverInstance;
    private readonly LibraryDatabase _database;

    /// <summary>
    /// The server this book is on: the one being browsed when the page was opened. Held once, so a
    /// switch made elsewhere while this page is open cannot send its download to another server.
    /// </summary>
    private ServerConnection _server =>
        _serverInstance ??= _servers.Active ?? throw new InvalidOperationException(Strings.Server_Gone);
    private readonly DownloadQueue _downloads;
    private readonly CarConnection _car;

    /// <summary>
    /// Watches the download queue rather than owning the transfer.
    ///
    /// The download lives in a foreground service now, so it outlives this page — and this page
    /// outlives nothing: leave it and come back and it is a new object. Subscribing means a page
    /// reopened mid-download shows where the transfer got to instead of an idle Preuzmi button
    /// over a book that is already half here.
    /// </summary>
    public ServerBookViewModel(
        ServerConnections servers,
        LibraryDatabase database,
        DownloadQueue downloads,
        CarConnection car)
    {
        _servers = servers;
        _database = database;
        _downloads = downloads;
        _car = car;
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

        // The half that just arrived is no longer missing, and the offer has to stop naming it.
        // Without this the page went on saying "download the ebook" over an ebook now sitting in
        // the library — the very thing this page was just taught not to do.
        if (status.Phase == DownloadPhase.Finished) _ = ReconsiderAsync();
    }

    private async Task ReconsiderAsync()
    {
        try
        {
            if (_detail is null || BookId is not { } id) return;

            var here = await _database.GetBookAsync(id);

            LocalHasText = here?.HasText == true;
            DecideWhatIsMissing(here);
        }
        catch (Exception ex)
        {
            // The page still works with the offer it has, and the next visit reads it afresh.
            AppLog.Error("re-reading what is still missing", ex);
        }
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

    /// <summary>Which halves this download would actually fetch.</summary>
    public bool WantsAudio { get; private set; } = true;

    /// <summary>Whether both halves are missing, so picking one of them is a real choice.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsHalfChoice))]
    public partial bool CanChooseHalf { get; set; }

    /// <summary>The choice is only on offer while nothing is being fetched.</summary>
    public bool ShowsHalfChoice => CanChooseHalf && IsIdle;

    public bool WantsEbook { get; private set; } = true;

    /// <summary>Whether the audio could be played from the server instead of downloaded.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsStream))]
    public partial bool CanStream { get; set; }

    public bool ShowsStream => CanStream && IsIdle;

    /// <summary>What streaming will do: listen to the audiobook, or read an EPUB 3 along with its narration.</summary>
    [ObservableProperty]
    public partial string StreamText { get; set; } = Strings.ServerBook_Stream;

    /// <summary>What the button says, which is what it will do — the whole book, or the half of it
    /// that is missing.</summary>
    [ObservableProperty]
    public partial string DownloadText { get; set; } = Strings.ServerBook_Download;

    public bool CannotDownload => !CanDownload && Obstacle.Length > 0;

    /// <summary>
    /// What is already here, when something is — the difference between a button that looks
    /// redundant and one that says what it is for. Not a failure, so not dressed as one.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    public partial string Note { get; set; } = "";

    public bool HasNote => Note.Length > 0;

    /// <summary>Why it cannot be brought over, when it cannot.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CannotDownload))]
    public partial string Obstacle { get; set; } = "";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyPropertyChangedFor(nameof(ShowsHalfChoice))]
    [NotifyPropertyChangedFor(nameof(ShowsStream))]
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

    /// <summary>Whether the copy already here has the text, which decides what opening it means.</summary>
    public bool LocalHasText { get; private set; }

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

            // Already here? By the item it came from when that is recorded, and only then by title.
            //
            // Titles are the weaker half of this on purpose: the local one is read from the audio
            // file's own tags and the server's from its metadata, and they disagree often enough —
            // a subtitle, a series suffix, a colon — that a book plainly already here failed to
            // match and was offered for download all over again. The link is exact and survives
            // both sides being renamed; the title is the fallback for books that came over before
            // anything was recording it.
            var books = await _database.GetBooksAsync();

            // The item id together with the server it is on: two servers can each hold a copy of the
            // same book, and each copy is its own.
            var here = books.FirstOrDefault(b =>
                           b.ServerItemId == ItemId && (b.ServerId ?? ServerAccount.LegacyId) == _server.Id)
                ?? books.FirstOrDefault(b =>
                    string.Equals(b.Title, book.Title, StringComparison.CurrentCultureIgnoreCase));

            BookId = here?.Id;
            LocalHasText = here?.HasText == true;

            DecideWhatIsMissing(here);

            if (here is null) _ = OfferReadAlongStreamAsync();

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

    /// <summary>What is on the server that is not already on this phone, and whether it can come.
    ///
    /// The page used to ask one question — can this be downloaded — and answer it without ever
    /// looking at the library, so a book already sitting in Books was offered for download again.
    /// The honest question is per half. A book can be here as narration alone while the server also
    /// keeps the text that would follow it, or the other way round, and that missing half is worth
    /// fetching even though "the book" is already here. Both halves here means there is nothing to
    /// offer at all.
    ///
    /// Split audio only blocks the audio. A book the server keeps in forty files still has one
    /// ebook, and refusing to fetch that because of how the narration is stored would be the app
    /// being difficult about something nobody asked for.
    /// </summary>
    private void DecideWhatIsMissing(Book? here)
    {
        var serverHasAudio = _detail!.AudioFiles.Count > 0;
        var serverHasEbook = _detail.Ebook is not null;
        var splitAudio = _detail.AudioFiles.Count > 1;

        // Wanted means the server has it and this phone does not. A book already here with its
        // narration still wants the text the server keeps beside it — and a book that only plays
        // from the server still wants its audio, for listening offline or for aligning.
        var streamedHere = StreamedAudio.Is(here?.AudioPath);
        WantsAudio = serverHasAudio && (here?.HasAudio != true || streamedHere);
        WantsEbook = serverHasEbook && here?.HasText != true;

        // Split audio stops the audio and nothing else. A book the server keeps in forty files
        // still has one ebook, and refusing to fetch that over how the narration is stored would
        // be the app being difficult about something nobody asked for.
        var audioOutOfReach = WantsAudio && splitAudio;
        if (audioOutOfReach) WantsAudio = false;

        CanDownload = WantsAudio || WantsEbook;

        DownloadText = (WantsAudio, WantsEbook) switch
        {
            (true, true) => Strings.ServerBook_DownloadBoth,
            (true, false) => Strings.ServerBook_DownloadAudio,
            (false, true) => Strings.ServerBook_DownloadEbook,
            _ => Strings.ServerBook_Download,
        };

        // Only worth offering when there is genuinely a choice to make.
        CanChooseHalf = WantsAudio && WantsEbook;

        // Listening from the server needs the same one-file audio a download does, and is pointless
        // for a book that already does.
        CanStream = WantsAudio && !streamedHere;

        // Red, and only for something genuinely in the way.
        Obstacle = !serverHasAudio && !serverHasEbook
            ? Strings.Server_NothingToDownload
            : audioOutOfReach && !CanDownload
                ? string.Format(Strings.Server_SplitFilesLong, _detail.AudioFiles.Count)
                : "";

        // Plain, and for telling the reader what they already have — which is the difference
        // between a button that looks redundant and one that says what it is for.
        Note = here is null
            ? ""
            : streamedHere
                ? Strings.ServerBook_StreamedHere
            : !CanDownload && Obstacle.Length == 0
                ? Strings.ServerBook_AlreadyHere
                : (WantsEbook, WantsAudio) switch
                {
                    (true, false) when here.HasAudio => Strings.ServerBook_AudioHereTextMissing,
                    (false, true) when here.HasText => Strings.ServerBook_TextHereAudioMissing,
                    _ => "",
                };
    }

    /// <summary>
    /// The narration inside the server's EPUB 3, when it can be played from there; null for an
    /// ordinary ebook, and until the package has been looked into.
    /// </summary>
    private StoredNarration? _readAlong;

    /// <summary>
    /// Looks inside an EPUB on the server for narration that can be streamed, and offers to stream
    /// it. In the background, after the page is up: it is a request or two to the end of the
    /// package, and the page has nothing to wait for it about.
    /// </summary>
    private async Task OfferReadAlongStreamAsync()
    {
        if (_detail?.Ebook is not { } ebook || _detail.AudioFiles.Count > 0) return;

        try
        {
            _readAlong = await _server.FindStreamableNarrationAsync(ItemId, ebook);
            if (_readAlong is null) return;

            CanStream = true;
            StreamText = Strings.ServerBook_StreamReadAlong;
            Note = Strings.ServerBook_ReadAlongStreamable;
        }
        catch (Exception ex)
        {
            // Downloading it still works; streaming is only not offered.
            AppLog.Info($"server: could not look inside '{ebook.FileName}' ({ex.Message})");
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
    private Task DownloadAsync() => StartAsync(WantsAudio, WantsEbook);

    /// <summary>
    /// Fetching one half on its own, when both are missing and only one is wanted now.
    ///
    /// A book and its narration are not one thing arriving together: the audio is a gigabyte over
    /// whatever connection is to hand and the text is a rounding error beside it, and someone about
    /// to read on a train has a clear preference about which of those happens first. Offered only
    /// when both are actually missing — with one of them already here there is nothing to choose
    /// between, and the single button above says what it will do.
    /// </summary>
    [RelayCommand]
    private Task DownloadAudioOnlyAsync() => StartAsync(audio: true, ebook: false);

    [RelayCommand]
    private Task DownloadEbookOnlyAsync() => StartAsync(audio: false, ebook: true);

    /// <summary>
    /// Adds the book to play from the server, bringing down only its text.
    ///
    /// Done here rather than through the download service: there is no gigabyte to carry, only three
    /// small pieces of the audio for its fingerprint and the ebook, so it is over in seconds and has
    /// nothing to survive the screen locking for.
    /// </summary>
    [RelayCommand]
    private async Task StreamAsync()
    {
        if (_detail is null || IsDownloading) return;

        if (_downloads.Status.IsRunning)
        {
            Status = Strings.Download_AlreadyRunning;
            return;
        }

        IsDownloading = true;
        Progress = 0;
        Percent = "";
        Status = Strings.Server_PreparingStream;

        try
        {
            var progress = new Progress<ImportProgress>(p =>
            {
                Status = p.Message;
                Progress = p.Fraction;
            });

            BookId = _readAlong is not null && _detail.AudioFiles.Count == 0
                ? await _server.AddStreamingReadAlongAsync(ItemId, progress, attachTo: BookId)
                : await _server.AddStreamingAsync(ItemId, progress, wantEbook: WantsEbook, attachTo: BookId);

            Status = Strings.ServerBook_StreamReady;
            await ReconsiderAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("adding a book to stream", ex);
            Status = ex.Message;
        }
        finally
        {
            IsDownloading = false;
        }
    }

    private async Task StartAsync(bool audio, bool ebook)
    {
        if (_detail is null || IsDownloading || !(audio || ebook)) return;

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
        _downloads.Start(_server.Id, ItemId, Title, toAppStorage, audio, ebook, BookId);
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

    /// <summary>
    /// Goes to the copy already in the library, as whatever it is.
    ///
    /// The same rule the shelf uses, rather than always the player: a book with text opens into the
    /// text, and one without has nothing to read. Sending everything to the player greeted a
    /// text-only book with an empty transport. In a car it is always the player, for the reason
    /// every other route there is.
    /// </summary>
    [RelayCommand]
    private Task OpenAsync() => BookId switch
    {
        null => Task.CompletedTask,
        { } id when LocalHasText && !_car.IsConnected => Shell.Current.GoToAsync($"reader?id={id}"),
        { } id => Shell.Current.GoToAsync($"book?id={id}"),
    };

    [RelayCommand]
    private Task CloseAsync() => Shell.Current.GoToAsync("..");
}
