using AudioBookReader.Core.Books;
using AudioBookReader.Core.Data;
using AudioBookReader.Core.Models;

namespace AudioBookReader.Core.Tests;

public class LibraryServiceTests : IAsyncLifetime, IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("abr-library-tests").FullName;
    private LibraryDatabase _database = null!;
    private SyncMapStore _syncMaps = null!;
    private LibraryService _service = null!;

    public async Task InitializeAsync()
    {
        _database = new LibraryDatabase(Path.Combine(_dir, "library.db"));
        await _database.InitAsync();
        _syncMaps = new SyncMapStore(Path.Combine(_dir, "sync"));
        _service = new LibraryService(_database, _syncMaps);
    }

    public Task DisposeAsync() => _database.CloseAsync();

    /// <summary>
    /// The case the production caller actually hits: a pairing that was never aligned.
    ///
    /// Chapters belong to the audio while it is present, so they carry no text range until
    /// alignment has filled one in. Stripping the audio ranges from chapters that have no text
    /// ranges leaves nothing at all, and there is no way back — the ebook cannot be removed and
    /// re-added, because by then it is the only medium left. The existing tests all passed because
    /// they exercised the overload that is handed replacements; nothing covered the one-argument
    /// call every screen makes.
    /// </summary>
    [Fact]
    public async Task RemovingUnalignedAudioKeepsTheChaptersWhenGivenTheEbooksOwn()
    {
        var book = await _service.CreateFromAudioAsync(
            new AudioAttachment("book.m4b", "audio-hash", 600_000, AudioChapters(42)));

        await _service.AttachTextAsync(book.Id,
            new TextAttachment("book.epub", "text-hash", 50_000, TextChapters(42)));

        // Nothing has been aligned, so not one chapter has a text range.
        var paired = await _database.GetChaptersAsync(book.Id);
        Assert.Equal(42, paired.Count);
        Assert.All(paired, c => Assert.False(c.HasTextRange));

        await _service.DetachAudioAsync(book.Id, TextChapters(42));

        var after = await _database.GetChaptersAsync(book.Id);
        Assert.Equal(42, after.Count);
        Assert.All(after, c => Assert.True(c.HasTextRange));
    }

    /// <summary>Without replacements there is nothing to keep, which is why the caller must supply them.</summary>
    [Fact]
    public async Task RemovingUnalignedAudioWithNothingToPutInItsPlaceEmptiesTheChapters()
    {
        var book = await _service.CreateFromAudioAsync(
            new AudioAttachment("book.m4b", "audio-hash", 600_000, AudioChapters(42)));

        await _service.AttachTextAsync(book.Id,
            new TextAttachment("book.epub", "text-hash", 50_000, TextChapters(42)));

        await _service.DetachAudioAsync(book.Id);

        Assert.Empty(await _database.GetChaptersAsync(book.Id));
    }

    /// <summary>
    /// The two writers must not lose each other's coordinate.
    ///
    /// The player saves the listening position every few seconds while the reader saves the reading
    /// position on every sentence. Read-then-write left two awaits between reading a row and writing
    /// it back, so each routinely overwrote the other with the value it had read a moment earlier —
    /// a reading position that jumps backwards, which reads as the app forgetting where you were.
    /// </summary>
    [Fact]
    public async Task ConcurrentPositionSavesKeepBothCoordinates()
    {
        var book = await _service.CreateFromAudioAsync(
            new AudioAttachment("book.m4b", "audio-hash", 600_000, AudioChapters(2)));

        await _service.AttachTextAsync(book.Id,
            new TextAttachment("book.epub", "text-hash", 50_000, TextChapters(2)));

        await _database.SaveReadingStateAsync(book.Id, audioPositionMs: 100_000, textOffset: 5_000);

        // Interleaved the way the ticker and the follow loop actually interleave.
        await Task.WhenAll(
            Enumerable.Range(1, 40).Select(i => _database.SaveReadingStateAsync(book.Id, audioPositionMs: 100_000 + i * 1_000))
                .Concat(Enumerable.Range(1, 40).Select(i => _database.SaveReadingStateAsync(book.Id, textOffset: 5_000 + i * 100))));

        var state = await _database.GetReadingStateAsync(book.Id);
        Assert.NotNull(state);

        // Neither writer may have pushed the other back to where it started.
        Assert.True(state.AudioPositionMs > 100_000, $"listening position fell back to {state.AudioPositionMs}");
        Assert.True(state.TextOffset > 5_000, $"reading position fell back to {state.TextOffset}");
    }

    public void Dispose()
    {
        // Cleanup only; a lingering file handle should not fail a test that otherwise passed.
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private static List<Chapter> AudioChapters(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new Chapter
        {
            Index = i,
            Title = $"Chapter {i + 1}",
            StartMs = i * 60_000L,
            EndMs = (i + 1) * 60_000L,
        })];

    private static List<Chapter> TextChapters(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new Chapter
        {
            Index = i,
            Title = $"Chapter {i + 1}",
            TextStart = i * 1_000,
            TextEnd = (i + 1) * 1_000,
        })];

    private static AudioAttachment Audio(string hash = "audio-1", int chapters = 2) =>
        new("/books/novel.m4b", hash, chapters * 60_000L, AudioChapters(chapters), Title: "Novel", Author: "A. Writer");

    private static TextAttachment Text(string hash = "text-1", int chapters = 2) =>
        new("/books/novel.epub", hash, chapters * 1_000, TextChapters(chapters), Title: "Novel");

    private async Task WriteFullSyncMapAsync(int bookId, string audioHash, string ebookHash, int chapters)
    {
        var map = new SyncMap { AudioHash = audioHash, EbookHash = ebookHash };
        for (var i = 0; i < chapters; i++)
        {
            map.SetChapter(ChapterSyncMap.FromAnchors(i,
                [new Anchor(i * 60_000L, i * 1_000, 1f), new Anchor((i + 1) * 60_000L, (i + 1) * 1_000, 1f)]));
        }

        await _syncMaps.SaveAsync(bookId, map);
    }

    // ---- Single-medium books ----

    [Fact]
    public async Task AnAudioOnlyBookIsUsableWithoutAnEbook()
    {
        var book = await _service.CreateFromAudioAsync(Audio());

        Assert.True(book.HasAudio);
        Assert.False(book.HasText);
        Assert.False(book.IsPaired);
        Assert.Equal(SyncState.NotPaired, book.SyncState);
        Assert.Equal(2, (await _database.GetChaptersAsync(book.Id)).Count);
    }

    [Fact]
    public async Task ATextOnlyBookIsUsableWithoutAudio()
    {
        var book = await _service.CreateFromTextAsync(Text());

        Assert.True(book.HasText);
        Assert.False(book.HasAudio);
        Assert.Equal(SyncState.NotPaired, book.SyncState);

        var chapters = await _database.GetChaptersAsync(book.Id);
        Assert.All(chapters, c => Assert.True(c.HasTextRange));
        Assert.All(chapters, c => Assert.False(c.HasAudioRange));
    }

    // ---- Adding the second medium ----

    [Fact]
    public async Task AddingAnEbookToAnAudioBookQueuesAlignment()
    {
        var book = await _service.CreateFromAudioAsync(Audio());
        book = await _service.AttachTextAsync(book.Id, Text());

        Assert.True(book.IsPaired);
        Assert.Equal(SyncState.Pending, book.SyncState);
        Assert.Equal(-1, book.AlignedThroughChapter);
    }

    [Fact]
    public async Task AddingAudioToATextBookQueuesAlignment()
    {
        var book = await _service.CreateFromTextAsync(Text());
        book = await _service.AttachAudioAsync(book.Id, Audio());

        Assert.True(book.IsPaired);
        Assert.Equal(SyncState.Pending, book.SyncState);
    }

    [Fact]
    public async Task AudioOwnsTheChapterListOnceItIsAttached()
    {
        var book = await _service.CreateFromTextAsync(Text(chapters: 2));
        book = await _service.AttachAudioAsync(book.Id, Audio(chapters: 5));

        var chapters = await _database.GetChaptersAsync(book.Id);
        Assert.Equal(5, chapters.Count);
        Assert.All(chapters, c => Assert.True(c.HasAudioRange));
    }

    // ---- Removing a medium ----

    [Fact]
    public async Task RemovingTheEbookKeepsTheAudiobookPlayable()
    {
        var book = await _service.CreateFromAudioAsync(Audio());
        book = await _service.AttachTextAsync(book.Id, Text());

        book = await _service.DetachTextAsync(book.Id);

        Assert.True(book.HasAudio);
        Assert.False(book.HasText);
        Assert.Equal(SyncState.NotPaired, book.SyncState);

        var chapters = await _database.GetChaptersAsync(book.Id);
        Assert.Equal(2, chapters.Count);
        Assert.All(chapters, c => Assert.True(c.HasAudioRange));
        Assert.All(chapters, c => Assert.False(c.HasTextRange));
    }

    [Fact]
    public async Task RemovingTheAudioKeepsTheAlignedTextRanges()
    {
        var book = await _service.CreateFromTextAsync(Text());
        book = await _service.AttachAudioAsync(book.Id, Audio());

        // Stand in for alignment having filled the text ranges in.
        foreach (var chapter in await _database.GetChaptersAsync(book.Id))
        {
            chapter.TextStart = chapter.Index * 1_000;
            chapter.TextEnd = (chapter.Index + 1) * 1_000;
            await _database.UpdateChapterAsync(chapter);
        }

        book = await _service.DetachAudioAsync(book.Id);

        Assert.False(book.HasAudio);
        Assert.Equal(0, book.DurationMs);

        var chapters = await _database.GetChaptersAsync(book.Id);
        Assert.Equal(2, chapters.Count);
        Assert.All(chapters, c => Assert.True(c.HasTextRange));
        Assert.All(chapters, c => Assert.False(c.HasAudioRange));
    }

    [Fact]
    public async Task RemovingAudioFromANeverAlignedPairFallsBackToTheEbooksChapters()
    {
        var book = await _service.CreateFromAudioAsync(Audio(chapters: 4));
        book = await _service.AttachTextAsync(book.Id, Text(chapters: 3));

        book = await _service.DetachAudioAsync(book.Id, TextChapters(3));

        var chapters = await _database.GetChaptersAsync(book.Id);
        Assert.Equal(3, chapters.Count);
        Assert.All(chapters, c => Assert.True(c.HasTextRange));
    }

    [Fact]
    public async Task RemovingTheOnlyMediumIsRefused()
    {
        var audioOnly = await _service.CreateFromAudioAsync(Audio());
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.DetachAudioAsync(audioOnly.Id));

        var textOnly = await _service.CreateFromTextAsync(Text());
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.DetachTextAsync(textOnly.Id));
    }

    // ---- Sync map reuse ----

    [Fact]
    public async Task ReattachingTheSameEbookRestoresTheCompletedAlignment()
    {
        var book = await _service.CreateFromAudioAsync(Audio());
        book = await _service.AttachTextAsync(book.Id, Text());
        await WriteFullSyncMapAsync(book.Id, "audio-1", "text-1", chapters: 2);

        book = await _service.DetachTextAsync(book.Id);
        Assert.Equal(SyncState.NotPaired, book.SyncState);

        book = await _service.AttachTextAsync(book.Id, Text());

        Assert.Equal(SyncState.Complete, book.SyncState);
        Assert.Equal(1, book.AlignedThroughChapter);
    }

    [Fact]
    public async Task AttachingADifferentEbookDoesNotReuseTheOldAlignment()
    {
        var book = await _service.CreateFromAudioAsync(Audio());
        book = await _service.AttachTextAsync(book.Id, Text());
        await WriteFullSyncMapAsync(book.Id, "audio-1", "text-1", chapters: 2);

        book = await _service.DetachTextAsync(book.Id);
        book = await _service.AttachTextAsync(book.Id, Text(hash: "a-different-edition"));

        Assert.Equal(SyncState.Pending, book.SyncState);
        Assert.Equal(-1, book.AlignedThroughChapter);
    }

    [Fact]
    public async Task AnAlignmentThatStoppedPartWayIsReportedAsPartial()
    {
        var book = await _service.CreateFromAudioAsync(Audio(chapters: 4));
        book = await _service.AttachTextAsync(book.Id, Text(chapters: 4));

        // Only the first two chapters were aligned before the run stopped.
        await WriteFullSyncMapAsync(book.Id, "audio-1", "text-1", chapters: 2);

        book = await _service.DetachTextAsync(book.Id);
        book = await _service.AttachTextAsync(book.Id, Text(chapters: 4));

        Assert.Equal(SyncState.Partial, book.SyncState);
        Assert.Equal(1, book.AlignedThroughChapter);
    }

    [Fact]
    public async Task AGapInTheSyncMapEndsTheUsableRange()
    {
        var book = await _service.CreateFromAudioAsync(Audio(chapters: 4));
        book = await _service.AttachTextAsync(book.Id, Text(chapters: 4));

        // Chapters 0 and 2 are aligned but 1 is missing; the reader can only follow through 0.
        var map = new SyncMap { AudioHash = "audio-1", EbookHash = "text-1" };
        map.SetChapter(ChapterSyncMap.FromAnchors(0, [new Anchor(0, 0, 1f), new Anchor(60_000, 1_000, 1f)]));
        map.SetChapter(ChapterSyncMap.FromAnchors(2, [new Anchor(120_000, 2_000, 1f), new Anchor(180_000, 3_000, 1f)]));
        await _syncMaps.SaveAsync(book.Id, map);

        book = await _service.DetachTextAsync(book.Id);
        book = await _service.AttachTextAsync(book.Id, Text(chapters: 4));

        Assert.Equal(0, book.AlignedThroughChapter);
        Assert.Equal(SyncState.Partial, book.SyncState);
    }

    // ---- Reading position ----

    [Fact]
    public async Task ListeningDoesNotOverwriteWhereTheReaderWas()
    {
        var book = await _service.CreateFromAudioAsync(Audio());
        book = await _service.AttachTextAsync(book.Id, Text());

        await _database.SaveReadingStateAsync(book.Id, textOffset: 4_200);
        await _database.SaveReadingStateAsync(book.Id, audioPositionMs: 90_000, speed: 1.5f);

        var state = await _database.GetReadingStateAsync(book.Id);

        Assert.Equal(4_200, state!.TextOffset);
        Assert.Equal(90_000, state.AudioPositionMs);
        Assert.Equal(1.5f, state.Speed);
    }

    // ---- Original audio path ----

    /// <summary>
    /// Confirms the column that survives the app-level EnsureLocalAudioAsync round-trip: the
    /// database itself just needs to persist whatever the caller sets it to, with no help beyond
    /// what every other field already gets from sqlite-net.
    /// </summary>
    [Fact]
    public async Task OriginalAudioPathSurvivesASaveAndReload()
    {
        var book = await _service.CreateFromAudioAsync(Audio());

        book!.OriginalAudioPath = "content://com.example.provider/document/original.m4b";
        await _database.UpdateBookAsync(book);

        var reloaded = await _database.GetBookAsync(book.Id);

        Assert.Equal("content://com.example.provider/document/original.m4b", reloaded!.OriginalAudioPath);
    }

    /// <summary>
    /// The lookup DeleteIfUnusedAsync relies on to avoid deleting a file another book still needs
    /// has to see OriginalAudioPath too, not only AudioPath — otherwise a book whose audio was
    /// copied in for alignment looks unshared even when its original file is the one another entry
    /// still points at.
    /// </summary>
    [Fact]
    public async Task FindByMediaPathMatchesOnTheOriginalAudioPathToo()
    {
        var book = await _service.CreateFromAudioAsync(Audio());

        book!.AudioPath = "/data/app/books/internal-copy.m4b";
        book.OriginalAudioPath = "content://com.example.provider/document/original.m4b";
        await _database.UpdateBookAsync(book);

        var byInternalCopy = await _database.FindBookByMediaPathAsync("/data/app/books/internal-copy.m4b");
        var byOriginal = await _database.FindBookByMediaPathAsync("content://com.example.provider/document/original.m4b");

        Assert.NotNull(byInternalCopy);
        Assert.Equal(book.Id, byInternalCopy!.Id);
        Assert.NotNull(byOriginal);
        Assert.Equal(book.Id, byOriginal!.Id);
    }
}
