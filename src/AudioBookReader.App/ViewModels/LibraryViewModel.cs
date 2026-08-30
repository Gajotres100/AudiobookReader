using System.Collections.ObjectModel;
using AudioBookReader.App.Services;
using AudioBookReader.Core.Data;
using AudioBookReader.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AudioBookReader.App.ViewModels;

/// <summary>One book as the library grid shows it.</summary>
public class BookCard(Book book, ReadingState? state)
{
    public int Id { get; } = book.Id;
    public string Title { get; } = book.Title;
    public string Author { get; } = book.Author ?? "";
    public string? CoverPath { get; } = book.CoverPath;

    /// <summary>
    /// What this book actually has. Shown because the app is equally happy with an audiobook, an
    /// ebook, or both, and the difference decides what opening it will do.
    /// </summary>
    public string MediaBadge { get; } = (book.HasAudio, book.HasText) switch
    {
        (true, true) => "🎧 📖",
        (true, false) => "🎧",
        (false, true) => "📖",
        _ => "",
    };

    public string SyncBadge { get; } = book.SyncState switch
    {
        SyncState.Complete => "sinkronizirano",
        SyncState.Partial => "djelomično sinkronizirano",
        SyncState.InProgress => "poravnavanje u tijeku",
        SyncState.Pending when book.IsPaired => "čeka poravnanje",
        SyncState.Failed => "poravnanje nije uspjelo",
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
        if (!book.HasAudio) return "Samo tekst";

        var length = TimeSpan.FromMilliseconds(book.DurationMs);
        return length.TotalHours >= 1
            ? $"{(int)length.TotalHours} h {length.Minutes} min"
            : $"{length.Minutes} min";
    }
}

public partial class LibraryViewModel(
    LibraryDatabase database,
    BookImporter importer,
    BookFilePicker picker) : ObservableObject
{
    public ObservableCollection<BookCard> Books { get; } = [];

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string BusyMessage { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => !string.IsNullOrEmpty(Error);

    public bool IsEmpty => Books.Count == 0 && !IsBusy;

    [RelayCommand]
    public async Task LoadAsync()
    {
        Books.Clear();

        foreach (var book in await database.GetBooksAsync())
            Books.Add(new BookCard(book, await database.GetReadingStateAsync(book.Id)));

        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private Task ImportAudioAsync() => ImportAsync(
        picker.PickAudioAsync(),
        "Učitavam audioknjigu…",
        (stream, name, ct) => importer.ImportAudioAsync(stream, name, null, ct));

    [RelayCommand]
    private Task ImportEbookAsync() => ImportAsync(
        picker.PickEbookAsync(),
        "Čitam e-knjigu…",
        (stream, name, ct) => importer.ImportEbookAsync(stream, name, null, ct));

    private async Task ImportAsync(
        Task<FileResult?> pick,
        string busyMessage,
        Func<Stream, string, CancellationToken, Task<Book>> import)
    {
        Error = null;

        var picked = await pick;
        if (picked is null) return;

        IsBusy = true;
        BusyMessage = busyMessage;

        try
        {
            AppLog.Info($"import starting: '{picked.FileName}' from '{picked.FullPath}'");

            // Reading through the FileResult rather than its path: on Android the picker hands back
            // a content URI that is not a real filesystem path.
            await using var stream = await picked.OpenReadAsync();
            await import(stream, picked.FileName, CancellationToken.None);

            await LoadAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error($"import of '{picked.FileName}'", ex);
            Error = $"Uvoz nije uspio: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            BusyMessage = "";
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

        var confirmed = await Shell.Current.DisplayAlertAsync(
            "Obrisati knjigu?",
            $"„{card.Title}” i njene datoteke bit će trajno obrisane.",
            "Obriši",
            "Odustani");

        if (!confirmed) return;

        try
        {
            await importer.DeleteBookAsync(card.Id);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            // A failure here must not take the process down with it.
            Error = $"Brisanje nije uspjelo: {ex.Message}";
        }
    }

    [RelayCommand]
    private static Task OpenAsync(BookCard? card) =>
        card is null ? Task.CompletedTask : Shell.Current.GoToAsync($"book?id={card.Id}");
}
