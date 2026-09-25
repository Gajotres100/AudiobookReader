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
public class BookCard(Book book, ReadingState? state)
{
    public int Id { get; } = book.Id;

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

    public LibraryViewModel(
        LibraryDatabase database,
        BookImporter importer,
        BookFilePicker picker,
        DownloadQueue downloads,
        AlignmentQueue alignment,
        CarConnection car)
    {
        _database = database;
        _importer = importer;
        _picker = picker;
        _downloads = downloads;
        _alignment = alignment;
        _car = car;
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

        foreach (var book in await _database.GetBooksAsync())
            Books.Add(new BookCard(book, await _database.GetReadingStateAsync(book.Id)));

        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private Task ImportAudioAsync() => ImportAsync(
        _picker.PickAudioAsync(),
        Strings.Progress_LoadingAudiobook,
        (picked, progress, ct) => _importer.ImportAudioAsync(picked, null, progress, ct));

    [RelayCommand]
    private Task ImportEbookAsync() => ImportAsync(
        _picker.PickEbookAsync(),
        Strings.Progress_ReadingEbook,
        (picked, progress, ct) => _importer.ImportEbookAsync(picked, null, progress, ct));

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
    private Task OpenAsync(BookCard? card) => card switch
    {
        null => Task.CompletedTask,
        { HasText: true } when !_car.IsConnected => Shell.Current.GoToAsync($"reader?id={card.Id}"),
        _ => Shell.Current.GoToAsync($"book?id={card.Id}"),
    };

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
