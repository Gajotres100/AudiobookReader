using System.Globalization;
using AudioBookReader.App.Resources.Strings;
using System.Collections.ObjectModel;
using AudioBookReader.App.Services;
using AudioBookReader.Core.Books;
using AudioBookReader.Core.Data;
using AudioBookReader.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AudioBookReader.App.ViewModels;

/// <summary>One saved place, as the list shows it.</summary>
public class BookmarkRow(Bookmark bookmark, string chapter, string where)
{
    public Bookmark Bookmark { get; } = bookmark;

    public string Chapter { get; } = chapter;

    /// <summary>Timestamp, or the position in the text when there is no audio.</summary>
    public string Where { get; } = where;

    public string Note { get; } = bookmark.Note ?? "";

    public string Preview { get; } = bookmark.TextPreview ?? "";

    public bool HasPreview => Preview.Length > 0;

    public string Created { get; } = bookmark.CreatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
}

[QueryProperty(nameof(BookId), "id")]
public partial class BookmarksViewModel(
    LibraryDatabase database,
    BookTextExtractors extractors,
    PlaybackController playback) : ObservableObject
{
    /// <summary>
    /// How many a book may hold.
    ///
    /// Most players set no limit at all, and this one is a guard rather than a feature: the list
    /// is meant to be scanned, and a few hundred entries stop being a list and become a second
    /// problem. Reaching it says so instead of quietly dropping the oldest, because a bookmark the
    /// user placed is not the app's to discard.
    /// </summary>
    public const int Limit = 50;

    private List<Chapter> _chapters = [];
    private BookText? _text;

    [ObservableProperty]
    public partial int BookId { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNone))]
    [NotifyPropertyChangedFor(nameof(CountText))]
    public partial int Count { get; set; }

    public bool HasNone => Count == 0;

    public string CountText => $"{Count} / {Limit}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string Message { get; set; } = "";

    public bool HasMessage => Message.Length > 0;

    public ObservableCollection<BookmarkRow> Bookmarks { get; } = [];

    public async Task LoadAsync()
    {
        var book = await database.GetBookAsync(BookId);
        if (book is null) return;

        Title = book.Title;
        _chapters = await database.GetChaptersAsync(BookId);

        // Only for the snippet beside each entry, and only when the book has text at all.
        if (book.EbookPath is { } path && _text is null)
        {
            try
            {
                _text = (await extractors.ExtractAsync(path)).Text;
            }
            catch (Exception ex)
            {
                AppLog.Error("reading text for bookmark previews", ex);
            }
        }

        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        var saved = await database.GetBookmarksAsync(BookId);

        Bookmarks.Clear();
        foreach (var bookmark in saved.OrderBy(b => b.PositionMs ?? b.TextOffset ?? 0))
            Bookmarks.Add(Describe(bookmark));

        Count = Bookmarks.Count;
    }

    private BookmarkRow Describe(Bookmark bookmark)
    {
        var chapter = _chapters.FirstOrDefault(c => c.Index == bookmark.ChapterIndex);

        var name = chapter is null || string.IsNullOrWhiteSpace(chapter.Title)
            ? string.Format(Strings.Chapter_Numbered, bookmark.ChapterIndex + 1)
            : chapter.Title;

        var where = bookmark.PositionMs is { } at
            ? Format(at)
            : bookmark.TextOffset is { } offset ? string.Format(Strings.Bookmarks_AtChar, offset.ToString("N0")) : "";

        return new BookmarkRow(bookmark, name, where);
    }

    /// <summary>Saves the spot being listened to, or read when there is no audio.</summary>
    /// <summary>
    /// Leaves the page.
    ///
    /// Its own control, because this page draws no navigation bar — the system's back gesture still
    /// works, and a screen whose only way out is a gesture is a screen some people are stuck on.
    /// </summary>
    [RelayCommand]
    private Task CloseAsync() => Shell.Current.GoToAsync("..");

    [RelayCommand]
    private async Task AddAsync()
    {
        Message = "";

        if (Count >= Limit)
        {
            Message = string.Format(Strings.Bookmarks_LimitReached, Limit);
            return;
        }

        var state = await database.GetReadingStateAsync(BookId);

        var at = playback.BookId == BookId && playback.PositionMs > 0
            ? playback.PositionMs
            : state?.AudioPositionMs;

        var offset = at is null ? state?.TextOffset : null;

        if (at is null && offset is null)
        {
            Message = Strings.Bookmarks_NoPosition;
            return;
        }

        await database.AddBookmarkAsync(new Bookmark
        {
            BookId = BookId,
            PositionMs = at,
            TextOffset = offset,
            ChapterIndex = ChapterAt(at) ?? 0,
            TextPreview = PreviewAt(offset),
            CreatedUtc = DateTime.UtcNow,
        });

        await RefreshAsync();
    }

    private int? ChapterAt(long? positionMs) =>
        positionMs is { } at
            ? _chapters.LastOrDefault(c => c.HasAudioRange && at >= c.StartMs)?.Index ?? 0
            : null;

    /// <summary>A line of the book at this spot, so an entry is recognizable without playing it.</summary>
    private string? PreviewAt(int? offset)
    {
        if (_text is null || offset is not { } start || start >= _text.PlainText.Length) return null;

        var length = Math.Min(90, _text.PlainText.Length - start);
        return _text.PlainText.Substring(start, length).ReplaceLineEndings(" ").Trim();
    }

    /// <summary>
    /// Goes to a bookmark.
    ///
    /// A text position takes precedence and opens the reader there: this page is only ever reached
    /// from the player, so a plain "back" landed on the player itself and left the book's text —
    /// the whole point of a bookmark placed while reading — untouched. A position given in audio
    /// alone stays on the player and just seeks it, since there is no text side to open.
    /// </summary>
    [RelayCommand]
    private async Task OpenAsync(BookmarkRow? row)
    {
        if (row is null) return;

        if (row.Bookmark.TextOffset is { } offset && _text is not null)
        {
            // This page is reached from the player, so the reader may already be sitting further
            // down the stack underneath it (opened before the player was) — pushing another copy
            // on top left two readers stacked, and the second one's own close button then landed
            // back on this page instead of leaving the book. Resetting to the library first
            // guarantees a single, fresh reader on the stack, so closing it behaves the same as
            // opening that same bookmark from anywhere else would.
            await Shell.Current.GoToAsync("//library");
            await Shell.Current.GoToAsync($"reader?id={BookId}&offset={offset}");
            return;
        }

        if (row.Bookmark.PositionMs is { } at && playback.BookId == BookId) playback.SeekTo(at);

        await Shell.Current.GoToAsync("..");
    }

    [RelayCommand]
    private async Task DeleteAsync(BookmarkRow? row)
    {
        if (row is null) return;

        var confirmed = await Dialogs.AskAsync(
            Strings.Dialog_DeleteBookmarkTitle, row.Where, Strings.Common_Delete, Strings.Common_Cancel, destructive: true);

        if (!confirmed) return;

        await database.DeleteBookmarkAsync(row.Bookmark.Id);
        await RefreshAsync();
    }

    private static string Format(long ms)
    {
        var span = TimeSpan.FromMilliseconds(ms);
        return span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss");
    }
}
