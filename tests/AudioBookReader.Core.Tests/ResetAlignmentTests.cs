using AudioBookReader.Core.Books;
using AudioBookReader.Core.Data;
using AudioBookReader.Core.Models;

namespace AudioBookReader.Core.Tests;

public class ResetAlignmentTests : IAsyncLifetime, IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("abr-reset-tests").FullName;
    private LibraryDatabase _database = null!;
    private SyncMapStore _syncMaps = null!;
    private LibraryService _library = null!;

    public async Task InitializeAsync()
    {
        _database = new LibraryDatabase(Path.Combine(_dir, "library.db"));
        await _database.InitAsync();
        _syncMaps = new SyncMapStore(Path.Combine(_dir, "sync"));
        _library = new LibraryService(_database, _syncMaps);
    }

    public Task DisposeAsync() => _database.CloseAsync();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private async Task<Book> CreateAlignedBookAsync()
    {
        var chapters = Enumerable.Range(0, 3).Select(i => new Chapter
        {
            Index = i,
            Title = $"Chapter {i + 1}",
            StartMs = i * 60_000L,
            EndMs = (i + 1) * 60_000L,
        }).ToList();

        var book = await _library.CreateFromAudioAsync(
            new AudioAttachment("/books/novel.m4b", "audio-1", 180_000, chapters));

        book = await _library.AttachTextAsync(book.Id,
            new TextAttachment("/books/novel.epub", "text-1", 3_000, []));

        var map = new SyncMap { AudioHash = "audio-1", EbookHash = "text-1" };
        map.SetChapter(ChapterSyncMap.FromAnchors(0,
            [new Anchor(0, 0, 0.9f), new Anchor(60_000, 1_000, 0.9f)]));
        await _syncMaps.SaveAsync(book.Id, map);

        // Alignment writes the text ranges it worked out back onto the chapters.
        foreach (var chapter in await _database.GetChaptersAsync(book.Id))
        {
            chapter.TextStart = chapter.Index * 1_000;
            chapter.TextEnd = (chapter.Index + 1) * 1_000;
            await _database.UpdateChapterAsync(chapter);
        }

        book.AlignedThroughChapter = 0;
        book.SyncState = SyncState.Partial;
        await _database.UpdateBookAsync(book);

        return book;
    }

    [Fact]
    public async Task DiscardsTheMapSoTheNextRunStartsFromNothing()
    {
        var book = await CreateAlignedBookAsync();

        await _library.ResetAlignmentAsync(book.Id);

        Assert.False(_syncMaps.Exists(book.Id));

        var reset = await _database.GetBookAsync(book.Id);
        Assert.Equal(-1, reset!.AlignedThroughChapter);
        Assert.Equal(SyncState.Pending, reset.SyncState);
    }

    [Fact]
    public async Task ClearsTheTextRangesAlignmentHadWorkedOut()
    {
        // Those ranges were alignment output. Left behind they would seed the fresh run with the
        // very estimates that went wrong, which is the whole reason for starting over.
        var book = await CreateAlignedBookAsync();

        await _library.ResetAlignmentAsync(book.Id);

        var chapters = await _database.GetChaptersAsync(book.Id);
        Assert.All(chapters, c => Assert.False(c.HasTextRange));
        Assert.All(chapters, c => Assert.True(c.HasAudioRange));
    }

    [Fact]
    public async Task KeepsBothMediaAndTheReadingPosition()
    {
        var book = await CreateAlignedBookAsync();
        await _database.SaveReadingStateAsync(book.Id, audioPositionMs: 42_000, textOffset: 900);

        await _library.ResetAlignmentAsync(book.Id);

        var reset = await _database.GetBookAsync(book.Id);
        Assert.True(reset!.IsPaired);

        var state = await _database.GetReadingStateAsync(book.Id);
        Assert.Equal(42_000, state!.AudioPositionMs);
        Assert.Equal(900, state.TextOffset);
    }

    [Fact]
    public async Task LeavesATextOnlyBooksChaptersAlone()
    {
        // Without audio the chapters came from the ebook, and their text ranges are the only thing
        // locating them — clearing those would leave the book with no structure at all.
        var book = await _library.CreateFromTextAsync(new TextAttachment(
            "/books/novel.epub",
            "text-1",
            3_000,
            [new Chapter { Index = 0, Title = "One", TextStart = 0, TextEnd = 3_000 }]));

        await _library.ResetAlignmentAsync(book.Id);

        var chapters = await _database.GetChaptersAsync(book.Id);
        Assert.All(chapters, c => Assert.True(c.HasTextRange));
    }
}
