using AudioBookReader.Core.Data;
using AudioBookReader.Core.Models;

namespace AudioBookReader.Core.Books;

/// <summary>An audiobook ready to be attached to a library entry.</summary>
public record AudioAttachment(
    string Path,
    string Hash,
    long DurationMs,
    IReadOnlyList<Chapter> Chapters,
    string? Title = null,
    string? Author = null,
    string? CoverPath = null);

/// <summary>An ebook ready to be attached to a library entry.</summary>
public record TextAttachment(
    string Path,
    string Hash,
    int TextLength,
    IReadOnlyList<Chapter> Chapters,
    string? Title = null,
    string? Author = null);

/// <summary>
/// Owns how a book's two media go together.
///
/// Audio and text are independent: a book may be either alone or both, and each side can be
/// attached, replaced or removed at any time without disturbing the other. The rules for what
/// that does to chapters and to the sync map are subtle enough — and easy enough to get subtly
/// wrong at each UI call site — that they live here rather than in the view models.
/// </summary>
public class LibraryService(LibraryDatabase database, SyncMapStore syncMaps)
{
    public async Task<Book> CreateFromAudioAsync(AudioAttachment audio)
    {
        var book = new Book
        {
            Title = audio.Title ?? Path.GetFileNameWithoutExtension(audio.Path),
            Author = audio.Author,
        };

        book.Id = await database.AddBookAsync(book);
        return await AttachAudioAsync(book.Id, audio);
    }

    public async Task<Book> CreateFromTextAsync(TextAttachment text)
    {
        var book = new Book
        {
            Title = text.Title ?? Path.GetFileNameWithoutExtension(text.Path),
            Author = text.Author,
        };

        book.Id = await database.AddBookAsync(book);
        return await AttachTextAsync(book.Id, text);
    }

    /// <summary>
    /// Adds an audiobook to a book, replacing one already there. Chapters come from the audio
    /// whenever it is present: its chapter marks are what playback navigates by, and alignment
    /// then fills in each chapter's text range.
    /// </summary>
    public async Task<Book> AttachAudioAsync(int bookId, AudioAttachment audio)
    {
        var book = await RequireBookAsync(bookId);

        book.AudioPath = audio.Path;
        book.AudioHash = audio.Hash;
        book.DurationMs = audio.DurationMs;
        book.CoverPath ??= audio.CoverPath;
        if (string.IsNullOrWhiteSpace(book.Title)) book.Title = audio.Title ?? book.Title;
        book.Author ??= audio.Author;

        var chapters = CarryTextRangesForward(audio.Chapters, await database.GetChaptersAsync(bookId));
        await database.ReplaceChaptersAsync(bookId, chapters);

        await ApplySyncStateAsync(book, chapters.Count);
        await database.UpdateBookAsync(book);
        return book;
    }

    /// <summary>Adds an ebook to a book, replacing one already there.</summary>
    public async Task<Book> AttachTextAsync(int bookId, TextAttachment text)
    {
        var book = await RequireBookAsync(bookId);

        book.EbookPath = text.Path;
        book.EbookHash = text.Hash;
        book.TextLength = text.TextLength;
        if (string.IsNullOrWhiteSpace(book.Title)) book.Title = text.Title ?? book.Title;
        book.Author ??= text.Author;

        // Audio keeps ownership of the chapter list when it is present; the ebook's own chapters
        // are used only when there is no audio to navigate by.
        var chapters = book.HasAudio
            ? await database.GetChaptersAsync(bookId)
            : text.Chapters.ToList();

        if (!book.HasAudio) await database.ReplaceChaptersAsync(bookId, chapters);

        await ApplySyncStateAsync(book, chapters.Count);
        await database.UpdateBookAsync(book);
        return book;
    }

    /// <summary>
    /// Removes the audiobook, keeping the ebook and everything derived from it.
    /// </summary>
    /// <param name="replacementChapters">
    /// Chapters derived from the ebook, for the case where the existing ones carry no text ranges
    /// (a pairing that was never aligned). Without them such chapters are dropped, since they
    /// describe audio that is no longer there.
    /// </param>
    public async Task<Book> DetachAudioAsync(int bookId, IReadOnlyList<Chapter>? replacementChapters = null)
    {
        var book = await RequireBookAsync(bookId);
        RequireOtherMedium(book, keeping: book.HasText, removing: "audio");

        book.AudioPath = null;
        book.AudioHash = null;
        book.DurationMs = 0;

        var chapters = replacementChapters?.ToList() ?? StripAudioRanges(await database.GetChaptersAsync(bookId));
        await database.ReplaceChaptersAsync(bookId, chapters);

        await ApplySyncStateAsync(book, chapters.Count);
        await database.UpdateBookAsync(book);
        return book;
    }

    /// <summary>Removes the ebook, keeping the audiobook and its chapter marks.</summary>
    public async Task<Book> DetachTextAsync(int bookId)
    {
        var book = await RequireBookAsync(bookId);
        RequireOtherMedium(book, keeping: book.HasAudio, removing: "text");

        book.EbookPath = null;
        book.EbookHash = null;
        book.TextLength = 0;

        var chapters = StripTextRanges(await database.GetChaptersAsync(bookId));
        await database.ReplaceChaptersAsync(bookId, chapters);

        await ApplySyncStateAsync(book, chapters.Count);
        await database.UpdateBookAsync(book);
        return book;
    }

    /// <summary>
    /// Throws away a book's alignment so it can be built again from nothing.
    ///
    /// Needed because a run's mistakes are sticky: a chapter aligned from a bad estimate leaves
    /// anchors that are wrong but present, and every later run treats them as settled. Resuming
    /// cannot undo that — only starting over can.
    /// </summary>
    public async Task<Book> ResetAlignmentAsync(int bookId)
    {
        var book = await RequireBookAsync(bookId);

        syncMaps.Delete(bookId);

        // Text ranges on the chapters were themselves alignment output. Left behind, they would
        // seed the new run with the very estimates that went wrong.
        if (book.HasAudio)
        {
            var chapters = await database.GetChaptersAsync(bookId);

            foreach (var chapter in chapters)
            {
                if (!chapter.HasTextRange) continue;

                chapter.TextStart = null;
                chapter.TextEnd = null;
                await database.UpdateChapterAsync(chapter);
            }
        }

        book.AlignedThroughChapter = -1;
        book.SyncState = book.IsPaired ? SyncState.Pending : SyncState.NotPaired;
        await database.UpdateBookAsync(book);

        return book;
    }

    private async Task<Book> RequireBookAsync(int bookId) =>
        await database.GetBookAsync(bookId) ?? throw new InvalidOperationException($"No book with id {bookId}.");

    private static void RequireOtherMedium(Book book, bool keeping, string removing)
    {
        if (!keeping)
            throw new InvalidOperationException(
                $"Removing the {removing} would leave book {book.Id} with nothing. Delete the book instead.");
    }

    /// <summary>
    /// Recomputes the sync state after either medium changed, reusing a cached map when the same
    /// pair of files comes back.
    ///
    /// This is what makes detaching non-destructive: remove an ebook, put the same one back, and
    /// the hashes still match the stored map, so hours of alignment are not repeated. A different
    /// file fails the hash check and alignment starts over, as it must.
    /// </summary>
    private async Task ApplySyncStateAsync(Book book, int chapterCount)
    {
        if (!book.IsPaired)
        {
            book.SyncState = SyncState.NotPaired;
            book.AlignedThroughChapter = -1;
            return;
        }

        var map = await syncMaps.LoadAsync(book.Id);
        var aligned = map is not null && map.MatchesPair(book.AudioHash, book.EbookHash)
            ? HighestContiguouslyAlignedChapter(map)
            : -1;

        book.AlignedThroughChapter = aligned;
        book.SyncState = aligned switch
        {
            < 0 => SyncState.Pending,
            _ when aligned >= chapterCount - 1 => SyncState.Complete,
            _ => SyncState.Partial,
        };
    }

    /// <summary>
    /// Alignment runs front to back and the reader needs an unbroken run from the start, so a
    /// gap ends the usable range even if later chapters happen to be present.
    /// </summary>
    private static int HighestContiguouslyAlignedChapter(SyncMap map)
    {
        var aligned = -1;
        while (map.ForChapter(aligned + 1) is { IsEmpty: false }) aligned++;
        return aligned;
    }

    /// <summary>
    /// Keeps text ranges already established for a chapter index when the audio is replaced.
    /// Re-importing the same audio in a different container should not throw away alignment that
    /// still describes the same text.
    /// </summary>
    private static List<Chapter> CarryTextRangesForward(IReadOnlyList<Chapter> incoming, List<Chapter> existing)
    {
        var byIndex = existing.ToDictionary(c => c.Index);

        return incoming.Select(c => new Chapter
        {
            Index = c.Index,
            Title = c.Title,
            StartMs = c.StartMs,
            EndMs = c.EndMs,
            TextStart = c.TextStart ?? (byIndex.TryGetValue(c.Index, out var old) ? old.TextStart : null),
            TextEnd = c.TextEnd ?? (byIndex.TryGetValue(c.Index, out var prior) ? prior.TextEnd : null),
        }).ToList();
    }

    private static List<Chapter> StripAudioRanges(List<Chapter> chapters) =>
        Reindex(chapters.Where(c => c.HasTextRange).Select(c =>
        {
            c.StartMs = null;
            c.EndMs = null;
            return c;
        }));

    private static List<Chapter> StripTextRanges(List<Chapter> chapters) =>
        Reindex(chapters.Where(c => c.HasAudioRange).Select(c =>
        {
            c.TextStart = null;
            c.TextEnd = null;
            return c;
        }));

    private static List<Chapter> Reindex(IEnumerable<Chapter> chapters)
    {
        var result = chapters.ToList();
        for (var i = 0; i < result.Count; i++) result[i].Index = i;
        return result;
    }
}
