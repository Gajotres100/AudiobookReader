using AudioBookReader.Core.Books;
using AudioBookReader.App.Resources.Strings;
using System.Collections.ObjectModel;
using AudioBookReader.App.Services;
using AudioBookReader.App.Views;
using AudioBookReader.Core.Data;
using AudioBookReader.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AudioBookReader.App.ViewModels;

/// <summary>One book as the library grid shows it.</summary>
public partial class BookCard(Book book, ReadingState? state) : ObservableObject
{
    public int Id { get; } = book.Id;

    /// <summary>The text to read in before opening, so the wait happens here with something showing.</summary>
    public string? EbookPath { get; } = book.EbookPath;

    /// <summary>Tapped and on its way open: a spinner over the cover says the tap was taken.</summary>
    [ObservableProperty]
    public partial bool IsOpening { get; set; }

    /// <summary>Decides which of the app's three shapes this book opens into.</summary>
    public bool HasText { get; } = book.HasText;
    public string Title { get; } = book.Title;
    public string Author { get; } = book.Author ?? "";
    public string? CoverPath { get; } = book.CoverPath;

    /// <summary>
    /// What this book actually has. Shown because the app is equally happy with an audiobook, an
    /// ebook, or both, and the difference decides what opening it will do.
    ///
    /// A cloud instead of headphones when the audio plays from the server: the book is on the shelf
    /// like any other, but it needs the network and takes no space, and that is worth seeing at a
    /// glance.
    /// </summary>
    public string MediaBadge { get; } = (book.HasAudio, book.HasText) switch
    {
        (true, true) => $"{AudioGlyph(book)} 📖",
        (true, false) => AudioGlyph(book),
        (false, true) => "📖",
        _ => "",
    };

    /// <summary>
    /// Came whole as an EPUB 3 with its own narration. Marked with the EP3 icon instead of the
    /// headphones and the book: it is not two halves put together, it is one thing.
    /// </summary>
    public bool IsReadAlong { get; } = book.IsReadAlong;

    public bool ShowsMediaBadge => !IsReadAlong;

    /// <summary>Whether the audio stays on the server.</summary>
    public bool IsStreamed { get; } = StreamedAudio.Is(book.AudioPath);

    private static string AudioGlyph(Book book) => StreamedAudio.Is(book.AudioPath) ? "☁" : "🎧";

    public string SyncBadge { get; } = book.SyncState switch
    {
        SyncState.Complete => Strings.Sync_Complete,
        SyncState.Partial => Strings.Sync_Partial,
        SyncState.InProgress => Strings.Sync_InProgress,
        SyncState.Pending when book.IsPaired => Strings.Sync_Pending,
        SyncState.Failed => Strings.Sync_Failed,
        _ => "",
    };

    public string Subtitle { get; } = Describe(book);

    public bool HasSyncBadge => SyncBadge.Length > 0;

    public bool HasCover => !string.IsNullOrEmpty(CoverPath);

    /// <summary>0..1 through the audio, for the progress bar under the cover.</summary>
    public double Progress { get; } =
        book.DurationMs > 0 && state?.AudioPositionMs is { } at
            ? Math.Clamp(at / (double)book.DurationMs, 0, 1)
            : 0;

    public bool HasProgress => Progress > 0.001;

    private static string Describe(Book book)
    {
        if (!book.HasAudio) return Strings.Media_TextOnly;

        var length = TimeSpan.FromMilliseconds(book.DurationMs);
        var duration = length.TotalHours >= 1
            ? string.Format(Strings.Duration_HoursMinutes, (int)length.TotalHours, length.Minutes)
            : string.Format(Strings.Duration_Minutes, length.Minutes);

        // Said in words as well as by the cloud, since a glyph alone is easy to read past.
        return StreamedAudio.Is(book.AudioPath) ? $"{duration} · {Strings.Library_Streamed}" : duration;
    }
}

public partial class LibraryViewModel : ObservableObject
{
    private readonly LibraryDatabase _database;
    private readonly BookImporter _importer;
    private readonly BookFilePicker _picker;
    private readonly DownloadQueue _downloads;
    private readonly AlignmentQueue _alignment;
    private readonly CarConnection _car;
    private readonly BookTextExtractors _extractors;

    public LibraryViewModel(
        LibraryDatabase database,
        BookImporter importer,
        BookFilePicker picker,
        DownloadQueue downloads,
        AlignmentQueue alignment,
        CarConnection car,
        BookTextExtractors extractors)
    {
        _database = database;
        _importer = importer;
        _picker = picker;
        _downloads = downloads;
        _alignment = alignment;
        _car = car;
        _extractors = extractors;
    }

    /// <summary>
    /// Listens for background work that changes what is on the shelf.
    ///
    /// The shelf used to reload only when it came forward, which was enough while everything that
    /// changed it was something the user did on another page. It is not enough now: a download
    /// finishes in a service, on its own schedule, and it may well finish with this very page open
    /// — and then the book that just arrived is nowhere to be seen until the app is restarted.
    ///
    /// Alignment is here for the same reason. It does not add a book, but it changes the badge that
    /// says whether one is aligned, and a badge that only updates on a restart is the same bug.
    /// </summary>
    public void Attach()
    {
        _downloads.Changed -= OnDownloadChanged;
        _downloads.Changed += OnDownloadChanged;

        _alignment.Changed -= OnAlignmentChanged;
        _alignment.Changed += OnAlignmentChanged;
    }

    public void Detach()
    {
        _downloads.Changed -= OnDownloadChanged;
        _alignment.Changed -= OnAlignmentChanged;
    }

    // Only when the work has actually ended. Both queues report progress continuously, and
    // reloading the shelf on every tick would mean rebuilding every row several times a second.
    private void OnDownloadChanged(object? sender, DownloadStatus status)
    {
        if (status.Phase == DownloadPhase.Finished) ReloadInBackground();
    }

    private void OnAlignmentChanged(object? sender, AlignmentStatus status)
    {
        if (status.Phase is AlignmentPhase.Finished or AlignmentPhase.Stopped) ReloadInBackground();
    }

    /// <summary>
    /// Reloads without anyone waiting, because nobody can: these arrive from a service thread.
    /// </summary>
    private void ReloadInBackground() => MainThread.BeginInvokeOnMainThread(async () =>
    {
        try
        {
            await LoadAsync();
        }
        catch (Exception ex)
        {
            // Not shown. The user did not ask for this reload and has nothing to do about it
            // failing; the next time the page comes forward will try again.
            AppLog.Error("refreshing the library after background work", ex);
        }
    });

    public ObservableCollection<BookCard> Books { get; } = [];

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string BusyMessage { get; set; } = "";

    /// <summary>0..1 through the copy, or 0 while the source will not say how large it is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBusyFraction))]
    public partial double BusyFraction { get; set; }

    public bool HasBusyFraction => BusyFraction > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => !string.IsNullOrEmpty(Error);

    public bool IsEmpty => Books.Count == 0 && !IsBusy;

    [RelayCommand]
    public async Task LoadAsync()
    {
        Books.Clear();

        var books = await _database.GetBooksAsync();

        foreach (var book in books)
            Books.Add(new BookCard(book, await _database.GetReadingStateAsync(book.Id)));

        OnPropertyChanged(nameof(IsEmpty));

        PrepareMostLikely(books);
    }

    /// <summary>
    /// Reads the text of the book most likely to be opened next — the one read last, at the top of
    /// the shelf — while the shelf is being looked at, so it opens at once instead of after two or
    /// three seconds of parsing with nothing on screen, which read as a tap that had not worked.
    ///
    /// In the background and at no one's expense: the parsed text is shared with the reader when it
    /// opens, and if the reader asks while this is still going it waits for this rather than
    /// starting over.
    /// </summary>
    private void PrepareMostLikely(IEnumerable<Book> books)
    {
        if (books.FirstOrDefault(b => b.HasText)?.EbookPath is not { } path) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await _extractors.ExtractAsync(path);
            }
            catch (Exception ex)
            {
                // Only a head start; the reader reports its own failure if the book cannot be read.
                AppLog.Info($"library: preparing '{Path.GetFileName(path)}' failed ({ex.Message})");
            }
        });
    }

    [RelayCommand]
    private Task ImportAudioAsync() => ImportAsync(
        _picker.PickAudioAsync(),
        Strings.Progress_LoadingAudiobook,
        (picked, progress, ct) => ImportOrCompleteAsync(picked, isAudio: true, progress, ct));

    [RelayCommand]
    private Task ImportEbookAsync() => ImportAsync(
        _picker.PickEbookAsync(),
        Strings.Progress_ReadingEbook,
        (picked, progress, ct) => ImportOrCompleteAsync(picked, isAudio: false, progress, ct));

    [RelayCommand]
    private Task ImportReadAlongAsync() => ImportAsync(
        _picker.PickEbookAsync(Strings.Picker_ChooseReadAlong),
        Strings.Progress_ReadingEbook,
        (picked, progress, ct) => _importer.ImportReadAlongAsync(picked, progress, ct));

    /// <summary>
    /// Adds the file as a book of its own — unless a book already here is waiting for exactly this
    /// half. "Torch.epub" added after "Torch.m4b" joins it, the same as adding it from the book's
    /// own page would, instead of standing next to it as a second, text-only Torch.
    /// </summary>
    private async Task<Book> ImportOrCompleteAsync(
        PickedMedia picked, bool isAudio, IProgress<ImportProgress> progress, CancellationToken ct)
    {
        var partner = await _importer.FindPartnerAsync(picked.FileName, isAudio);
        if (partner is { } id) AppLog.Info($"import: '{picked.FileName}' completes book {id}");

        var book = isAudio
            ? await _importer.ImportAudioAsync(picked, partner, progress, ct)
            : await _importer.ImportEbookAsync(picked, partner, progress, ct);

        // Now a read-along, and alignment wants the audio as a local copy — as when the second half
        // is added from the book's page.
        if (partner is not null && book.IsPaired)
            book = await _importer.EnsureLocalAudioAsync(book.Id, progress, ct) ?? book;

        return book;
    }

    private async Task ImportAsync(
        Task<PickedMedia?> pick,
        string busyMessage,
        Func<PickedMedia, IProgress<ImportProgress>, CancellationToken, Task<Book>> import)
    {
        Error = null;

        var picked = await pick;
        if (picked is null) return;

        IsBusy = true;
        BusyMessage = busyMessage;
        BusyFraction = 0;

        try
        {
            AppLog.Info($"import starting: '{picked.FileName}' from '{picked.Location}'");

            var progress = new Progress<ImportProgress>(p =>
            {
                BusyMessage = p.Message;
                BusyFraction = p.Fraction;
            });

            // The importer is handed the pick itself, not a stream: whether the file can be left
            // where it is — which is what makes an import instant — is its decision to make.
            await import(picked, progress, CancellationToken.None);

            await LoadAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error($"import of '{picked.FileName}'", ex);
            Error = string.Format(Strings.Error_ImportFailed, ex.Message);
        }
        finally
        {
            IsBusy = false;
            BusyMessage = "";
            BusyFraction = 0;
        }
    }

    /// <summary>
    /// Removes a book and its files. Reachable from the list itself so a bad import can be undone
    /// without having to open it first — which may be exactly what is broken.
    /// </summary>
    [RelayCommand]
    private async Task DeleteAsync(BookCard? card)
    {
        if (card is null) return;

        // Whichever file the book has of its own, so the confirmation can name what would go.
        var file = await _importer.OwnFileAsync(card.Id, audio: true)
                   ?? await _importer.OwnFileAsync(card.Id, audio: false);

        if (await BookViewModel.ConfirmDeletionAsync(
                Strings.Dialog_DeleteBookTitle,
                string.Format(Strings.Dialog_DeleteBookBody, card.Title),
                file) is not { } choice)
        {
            return;
        }

        try
        {
            await _importer.DeleteBookAsync(card.Id, choice.AlsoFile);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            // A failure here must not take the process down with it.
            Error = string.Format(Strings.Error_DeleteFailed, ex.Message);
        }
    }

    /// <summary>Books that live on an Audiobookshelf server rather than on this phone.</summary>
    [RelayCommand]
    private static Task OpenServerAsync() => Shell.Current.GoToAsync("server");

    /// <summary>
    /// Opens the book as whatever it is.
    ///
    /// The app is three things and a book is only ever one of them: an audiobook opens the player,
    /// and anything with text opens the text — plain reading when that is all there is, reading
    /// with the narration attached when the book is paired. Sending every book to the player first
    /// meant a novel with no audio was greeted by an empty transport, and a paired book made you
    /// press "Čitaj" every single time to get to the thing you came for.
    ///
    /// A car is the exception, and takes the audio half of everything. The reader is a page nobody
    /// behind a wheel may look at, and following it would set recognition running for a screen that
    /// must stay unread — so while a car screen is attached, every book opens into the player.
    /// </summary>
    [RelayCommand]
    private async Task OpenAsync(BookCard? card)
    {
        // One book at a time: a second tap while the first is still opening would push the page
        // twice. The command is refused while it runs, and this covers a tap on the same card.
        if (card is null || card.IsOpening) return;

        // Logged so a tap that seemed to do nothing can be told from one that arrived and was slow.
        AppLog.Info($"library: tapped book {card.Id}");

        card.IsOpening = true;

        try
        {
            var readsText = card.HasText && !_car.IsConnected;

            // The slow part of opening a book is reading its text — seconds, for a large one. Done
            // here, under the spinner on the cover, rather than on a reader page that has already
            // opened and then sits empty; the reader then finds the text ready.
            if (readsText && card.EbookPath is { } path)
            {
                try
                {
                    await Task.Run(() => _extractors.ExtractAsync(path));
                }
                catch (Exception ex)
                {
                    // The reader says what is wrong with a book it cannot read; this was only a head start.
                    AppLog.Info($"library: reading book {card.Id} ahead failed ({ex.Message})");
                }
            }

            await Shell.Current.GoToAsync(readsText ? $"reader?id={card.Id}" : $"book?id={card.Id}");
        }
        finally
        {
            card.IsOpening = false;
        }
    }

    /// <summary>
    /// There is nothing to buy. The app is free and always will be — this exists purely because a
    /// $ icon sitting next to three real import buttons is funnier than not having one.
    /// </summary>
    [RelayCommand]
    private static async Task PremiumAsync()
    {
        var yes = await Dialogs.AskAsync(
            Strings.Premium_ConfirmTitle, Strings.Premium_ConfirmBody, Strings.Common_Yes, Strings.Common_No);

        if (yes) await Shell.Current.Navigation.PushModalAsync(new PremiumPage());
    }
}
