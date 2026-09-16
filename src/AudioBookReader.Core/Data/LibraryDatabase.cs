using AudioBookReader.Core.Models;
using SQLite;

namespace AudioBookReader.Core.Data;

/// <summary>
/// The library store: books, their chapters, bookmarks and reading positions.
/// Sync maps live outside it — see <see cref="SyncMapStore"/>.
/// </summary>
public class LibraryDatabase
{
    private readonly SQLiteAsyncConnection _db;
    private readonly Lazy<Task> _ready;

    public LibraryDatabase(string databasePath)
    {
        _db = new SQLiteAsyncConnection(
            databasePath,
            SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create | SQLiteOpenFlags.SharedCache);

        // Schema creation happens on first use rather than at construction. Doing it eagerly would
        // mean either an async constructor, which does not exist, or blocking whoever builds the
        // container — and on Android that is the UI thread during startup, which simply stops.
        _ready = new Lazy<Task>(CreateTablesAsync);
    }

    /// <summary>Ensures the schema exists. Optional — every operation does this for itself.</summary>
    public Task InitAsync() => _ready.Value;

    private async Task CreateTablesAsync()
    {
        await _db.CreateTableAsync<Book>().ConfigureAwait(false);
        await _db.CreateTableAsync<Chapter>().ConfigureAwait(false);
        await _db.CreateTableAsync<Bookmark>().ConfigureAwait(false);
        await _db.CreateTableAsync<ReadingState>().ConfigureAwait(false);
    }

    private async Task<SQLiteAsyncConnection> ReadyAsync()
    {
        await _ready.Value.ConfigureAwait(false);
        return _db;
    }

    /// <summary>Releases the underlying connection. Call when the app shuts down.</summary>
    public Task CloseAsync() => _db.CloseAsync();

    // ---- Books ----

    public async Task<List<Book>> GetBooksAsync() =>
        // By when the book was last opened, falling back to when it arrived. LastOpenedUtc alone
        // sorted by a column nothing ever wrote, so every row was null and the list came back oldest
        // first — a freshly imported book appeared at the bottom, below titles from a year ago.
        await (await ReadyAsync()).QueryAsync<Book>(
            "select * from books order by coalesce(LastOpenedUtc, AddedUtc) desc");

    /// <summary>
    /// Records that a book was just opened, which is what puts it at the top of the shelf.
    ///
    /// One targeted statement rather than writing the whole row back: alignment runs for hours and
    /// saves its progress onto the same row, and a read-modify-write from here would hand it back
    /// whatever the row looked like when this book was opened — quietly undoing however far it had
    /// got. Nothing else on the row is this method's business.
    /// </summary>
    public async Task MarkOpenedAsync(int bookId) =>
        await (await ReadyAsync()).ExecuteAsync(
            "update books set LastOpenedUtc = ? where Id = ?", DateTime.UtcNow, bookId);

    /// <summary>
    /// Records which server item a book came from, so it is recognised next time it is seen there.
    /// One column, for the same reason as <see cref="MarkOpenedAsync"/>.
    /// </summary>
    public async Task LinkToServerAsync(int bookId, string itemId) =>
        await (await ReadyAsync()).ExecuteAsync(
            "update books set ServerItemId = ? where Id = ?", itemId, bookId);

    public async Task<Book?> GetBookAsync(int id) =>
        await (await ReadyAsync()).Table<Book>().Where(b => b.Id == id).FirstOrDefaultAsync();

    /// <summary>
    /// Finds a book already backed by this file, whichever medium it is. Used on import to offer
    /// attaching to the existing title instead of creating a duplicate.
    /// </summary>
    public async Task<Book?> FindBookByMediaPathAsync(string path) =>
        await (await ReadyAsync()).Table<Book>()
            .Where(b => b.AudioPath == path || b.OriginalAudioPath == path || b.EbookPath == path)
            .FirstOrDefaultAsync();

    public async Task<int> AddBookAsync(Book book)
    {
        book.AddedUtc = DateTime.UtcNow;
        await (await ReadyAsync()).InsertAsync(book);
        return book.Id;
    }

    public async Task UpdateBookAsync(Book book) => await (await ReadyAsync()).UpdateAsync(book);

    public async Task DeleteBookAsync(int bookId)
    {
        var db = await ReadyAsync();

        await db.Table<Chapter>().Where(c => c.BookId == bookId).DeleteAsync();
        await db.Table<Bookmark>().Where(b => b.BookId == bookId).DeleteAsync();
        await db.Table<ReadingState>().Where(r => r.BookId == bookId).DeleteAsync();
        await db.DeleteAsync<Book>(bookId);
    }

    // ---- Chapters ----

    public async Task<List<Chapter>> GetChaptersAsync(int bookId) =>
        await (await ReadyAsync()).Table<Chapter>()
            .Where(c => c.BookId == bookId)
            .OrderBy(c => c.Index)
            .ToListAsync();

    /// <summary>Replaces a book's chapters wholesale — chapter lists are re-derived, never patched.</summary>
    public async Task ReplaceChaptersAsync(int bookId, IEnumerable<Chapter> chapters)
    {
        var db = await ReadyAsync();
        await db.Table<Chapter>().Where(c => c.BookId == bookId).DeleteAsync();

        var rows = chapters.ToList();
        foreach (var c in rows)
        {
            c.Id = 0;
            c.BookId = bookId;
        }

        if (rows.Count > 0) await db.InsertAllAsync(rows);
    }

    public async Task UpdateChapterAsync(Chapter chapter) => await (await ReadyAsync()).UpdateAsync(chapter);

    // ---- Bookmarks ----

    public async Task<List<Bookmark>> GetBookmarksAsync(int bookId) =>
        await (await ReadyAsync()).Table<Bookmark>()
            .Where(b => b.BookId == bookId)
            .OrderBy(b => b.ChapterIndex)
            .ToListAsync();

    public async Task<int> AddBookmarkAsync(Bookmark bookmark)
    {
        bookmark.CreatedUtc = DateTime.UtcNow;
        await (await ReadyAsync()).InsertAsync(bookmark);
        return bookmark.Id;
    }

    public async Task UpdateBookmarkAsync(Bookmark bookmark) => await (await ReadyAsync()).UpdateAsync(bookmark);

    public async Task DeleteBookmarkAsync(int bookmarkId) => await (await ReadyAsync()).DeleteAsync<Bookmark>(bookmarkId);

    // ---- Reading position ----

    public async Task<ReadingState?> GetReadingStateAsync(int bookId) =>
        await (await ReadyAsync()).Table<ReadingState>().Where(r => r.BookId == bookId).FirstOrDefaultAsync();

    /// <summary>
    /// Saves the current position. Called every few seconds while playing, so it is a single
    /// upsert rather than a read-then-write.
    ///
    /// A null coordinate leaves the stored one alone: listening to a paired book must not erase
    /// where the reader was, and vice versa.
    /// </summary>
    public async Task SaveReadingStateAsync(int bookId, long? audioPositionMs = null, int? textOffset = null, float? speed = null)
    {
        var db = await ReadyAsync();

        // One statement, so the two writers cannot lose each other's work.
        //
        // Reading the row and writing it back looks equivalent and is not: the player saves the
        // listening position every few seconds while the reader saves the reading position on every
        // sentence, and with two awaits between the read and the write each routinely overwrites
        // the other's coordinate with the stale value it read a moment earlier. The symptom is a
        // reading position that occasionally jumps backwards — indistinguishable, to the user, from
        // the app forgetting where they were. coalesce keeps whichever coordinate is not being
        // written, inside the statement, where nothing can interleave.
        var updated = await db.ExecuteAsync(
            @"update reading_state
                 set AudioPositionMs = coalesce(?, AudioPositionMs),
                     TextOffset      = coalesce(?, TextOffset),
                     Speed           = coalesce(?, Speed),
                     UpdatedUtc      = ?
               where BookId = ?",
            audioPositionMs, textOffset, speed, DateTime.UtcNow, bookId);

        if (updated > 0) return;

        await db.InsertOrReplaceAsync(new ReadingState
        {
            BookId = bookId,
            AudioPositionMs = audioPositionMs,
            TextOffset = textOffset,
            Speed = speed ?? 1f,
            UpdatedUtc = DateTime.UtcNow,
        });
    }
}
