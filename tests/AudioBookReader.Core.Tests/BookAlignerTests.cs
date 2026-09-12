using AudioBookReader.Core.Alignment;
using AudioBookReader.Core.Books;
using AudioBookReader.Core.Data;
using AudioBookReader.Core.Models;

namespace AudioBookReader.Core.Tests;

public class BookAlignerTests : IAsyncLifetime, IDisposable
{
    private const int ChapterCount = 4;
    private const long ChapterMs = 10 * 60 * 1_000;
    private const long BookMs = ChapterCount * ChapterMs;

    private readonly string _dir = Directory.CreateTempSubdirectory("abr-align-tests").FullName;
    private LibraryDatabase _database = null!;
    private SyncMapStore _syncMaps = null!;
    private LibraryService _library = null!;
    private BookTextExtractors _extractors = null!;
    private string _ebookPath = null!;
    private string _bookText = null!;

    public async Task InitializeAsync()
    {
        _database = new LibraryDatabase(Path.Combine(_dir, "library.db"));
        await _database.InitAsync();
        _syncMaps = new SyncMapStore(Path.Combine(_dir, "sync"));
        _library = new LibraryService(_database, _syncMaps);
        _extractors = new BookTextExtractors();

        _ebookPath = Path.Combine(_dir, "novel.txt");
        await File.WriteAllTextAsync(_ebookPath, FakeNarration.GenerateProse(sentenceCount: 600));

        // The narration must speak exactly the text the aligner will read, so take it from the
        // extractor rather than from the file.
        _bookText = (await _extractors.ExtractAsync(_ebookPath)).Text.PlainText;
    }

    public Task DisposeAsync() => _database.CloseAsync();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private static List<Chapter> Chapters() =>
        [.. Enumerable.Range(0, ChapterCount).Select(i => new Chapter
        {
            Index = i,
            Title = $"Chapter {i + 1}",
            StartMs = i * ChapterMs,
            EndMs = (i + 1) * ChapterMs,
        })];

    private async Task<Book> CreatePairedBookAsync()
    {
        var book = await _library.CreateFromAudioAsync(
            new AudioAttachment("/books/novel.m4b", "audio-1", BookMs, Chapters(), Title: "Novel"));

        return await _library.AttachTextAsync(book.Id,
            new TextAttachment(_ebookPath, "text-1", _bookText.Length, [], Title: "Novel"));
    }

    private BookAligner Aligner(ITranscriber transcriber) =>
        new(_database, _syncMaps, _extractors, transcriber);

    private FakeNarration Narration(double noise = 0) => new(_bookText, BookMs, noise);

    // ---- The happy path ----

    [Fact]
    public async Task AlignsEveryChapterAndMarksTheBookComplete()
    {
        var book = await CreatePairedBookAsync();

        await Aligner(Narration()).AlignAsync(book.Id);

        var aligned = await _database.GetBookAsync(book.Id);
        Assert.Equal(SyncState.Complete, aligned!.SyncState);
        Assert.Equal(ChapterCount - 1, aligned.AlignedThroughChapter);

        var map = await _syncMaps.LoadAsync(book.Id);
        Assert.NotNull(map);
        Assert.Equal(ChapterCount, map.Chapters.Count);
        Assert.All(map.Chapters, c => Assert.False(c.IsEmpty));
    }

    [Fact]
    public async Task FillsInEachChaptersTextRange()
    {
        var book = await CreatePairedBookAsync();

        await Aligner(Narration()).AlignAsync(book.Id);

        var chapters = await _database.GetChaptersAsync(book.Id);
        Assert.All(chapters, c => Assert.True(c.HasTextRange));

        // Ranges advance through the book rather than piling up at the start.
        for (var i = 1; i < chapters.Count; i++)
            Assert.True(chapters[i].TextStart >= chapters[i - 1].TextStart);
    }

    [Fact]
    public async Task TheResultingMapTracksTheNarrationAcrossChapterBoundaries()
    {
        var book = await CreatePairedBookAsync();
        var narration = Narration();

        await Aligner(narration).AlignAsync(book.Id);
        var map = await _syncMaps.LoadAsync(book.Id);

        for (var chapter = 0; chapter < ChapterCount; chapter++)
        {
            var chapterMap = map!.ForChapter(chapter)!;

            // Sample inside the chapter, away from the boundary anchors.
            var at = chapter * ChapterMs + ChapterMs / 2;
            Assert.True(chapterMap.TryGetCharOffset(at, out var predicted));

            Assert.InRange(Math.Abs(predicted - narration.CharAt(at)), 0, 200);
        }
    }

    // ---- Interruption and resumption ----

    [Fact]
    public async Task StoppingPartWayKeepsWhatWasAlreadyAligned()
    {
        var book = await CreatePairedBookAsync();
        using var cancellation = new CancellationTokenSource();

        var transcriber = new CancelAfter(Narration(), calls: 10, cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Aligner(transcriber).AlignAsync(book.Id, null, cancellation.Token));

        var stopped = await _database.GetBookAsync(book.Id);
        Assert.Equal(SyncState.Partial, stopped!.SyncState);
        Assert.InRange(stopped.AlignedThroughChapter, 0, ChapterCount - 2);

        var map = await _syncMaps.LoadAsync(book.Id);
        Assert.NotNull(map);
        Assert.NotEmpty(map.Chapters);
    }

    [Fact]
    public async Task ResumingContinuesFromWhereItStoppedInsteadOfStartingOver()
    {
        var book = await CreatePairedBookAsync();
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Aligner(new CancelAfter(Narration(), calls: 10, cancellation)).AlignAsync(book.Id, null, cancellation.Token));

        var stoppedAt = (await _database.GetBookAsync(book.Id))!.AlignedThroughChapter;

        var resumed = Narration();
        await Aligner(resumed).AlignAsync(book.Id);

        var finished = await _database.GetBookAsync(book.Id);
        Assert.Equal(SyncState.Complete, finished!.SyncState);

        // The resumed run only listened to the chapters that were still outstanding.
        var wholeBookCalls = await CountCallsForAFullRunAsync();
        var remainingShare = (ChapterCount - 1 - stoppedAt) / (double)ChapterCount;
        Assert.True(
            resumed.TranscribeCalls < wholeBookCalls,
            $"resumed run made {resumed.TranscribeCalls} calls, a full run makes {wholeBookCalls} (~{remainingShare:P0} expected)");
    }

    private async Task<int> CountCallsForAFullRunAsync()
    {
        var book = await CreatePairedBookAsync();
        var narration = Narration();
        await Aligner(narration).AlignAsync(book.Id);
        return narration.TranscribeCalls;
    }

    /// <summary>
    /// Reproduces the exact shape a real bug left behind: every chapter marked "aligned through"
    /// in the book row, but a map on disk holding nothing but the two low-confidence boundary
    /// guesses per chapter — the signature of a build that scored every real anchor at 0 and lost
    /// all of them to <see cref="ChapterSyncMap.FromAnchors"/>'s own tie-breaker. A book caught by
    /// that bug must recover once a fixed build runs, not stay "Complete" with nothing usable.
    /// </summary>
    [Fact]
    public async Task RedoesAChapterThatIsMarkedDoneButHoldsOnlyBoundaryGuesses()
    {
        var book = await CreatePairedBookAsync();
        await Aligner(Narration()).AlignAsync(book.Id);

        var healthy = await _database.GetBookAsync(book.Id);
        Assert.Equal(SyncState.Complete, healthy!.SyncState);

        // Poison chapter 0 exactly as the bug did: strip it down to the two boundary anchors,
        // which by construction sit at BoundaryConfidence and nothing higher.
        var map = await _syncMaps.LoadAsync(book.Id);
        var chapters = await _database.GetChaptersAsync(book.Id);
        var boundary = chapters[0];

        var poisoned = new ChapterSyncMap
        {
            ChapterIndex = boundary.Index,
            Anchors =
            [
                new Anchor(boundary.StartMs!.Value, boundary.TextStart!.Value, 0.2f),
                new Anchor(boundary.EndMs!.Value, boundary.TextEnd!.Value, 0.2f),
            ],
        };
        map!.SetChapter(poisoned);
        await _syncMaps.SaveAsync(book.Id, map);

        var healing = Narration();
        await Aligner(healing).AlignAsync(book.Id);

        // It listened again — a book that skipped straight past the poisoned chapter because the
        // book row still claimed it was done would make no calls at all.
        Assert.True(healing.TranscribeCalls > 0);

        var healedMap = await _syncMaps.LoadAsync(book.Id);
        var healedChapter = healedMap!.ForChapter(boundary.Index);
        Assert.NotNull(healedChapter);
        Assert.True(healedChapter!.HasMeasurement(0.2f), "the poisoned chapter should hold real anchors again");

        var final = await _database.GetBookAsync(book.Id);
        Assert.Equal(SyncState.Complete, final!.SyncState);
    }

    /// <summary>
    /// The companion to the healing test above: a chapter that finished with a genuinely good
    /// measurement, sitting ahead of one that did not, must not be redone just because a LATER
    /// chapter is unmeasured. Healing walks forward from chapter 0 and stops at the first bad
    /// chapter it finds — it does not, say, jump straight to whichever chapter is worst.
    /// </summary>
    [Fact]
    public async Task DoesNotRedoAHealthyChapterAheadOfAnUnrelatedGap()
    {
        var book = await CreatePairedBookAsync();
        await Aligner(Narration()).AlignAsync(book.Id);

        var map = await _syncMaps.LoadAsync(book.Id);
        var chapters = await _database.GetChaptersAsync(book.Id);

        // Poison the last chapter only; chapters before it keep their real measurements.
        var last = chapters[^1];
        map!.SetChapter(new ChapterSyncMap
        {
            ChapterIndex = last.Index,
            Anchors =
            [
                new Anchor(last.StartMs!.Value, last.TextStart!.Value, 0.2f),
                new Anchor(last.EndMs!.Value, last.TextEnd!.Value, 0.2f),
            ],
        });
        await _syncMaps.SaveAsync(book.Id, map);

        var healing = Narration();
        await Aligner(healing).AlignAsync(book.Id);

        // Only the poisoned chapter needed redoing, not the whole book.
        var wholeBookCalls = await CountCallsForAFullRunAsync();
        Assert.True(
            healing.TranscribeCalls < wholeBookCalls,
            $"healing run made {healing.TranscribeCalls} calls, a full run makes {wholeBookCalls}");

        var healedMap = await _syncMaps.LoadAsync(book.Id);
        Assert.True(healedMap!.ForChapter(last.Index)!.HasMeasurement(0.2f));
    }

    // ---- Refusals ----

    [Fact]
    public async Task RefusesToAlignAnAudioOnlyBook()
    {
        var book = await _library.CreateFromAudioAsync(
            new AudioAttachment("/books/novel.m4b", "audio-1", BookMs, Chapters()));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Aligner(Narration()).AlignAsync(book.Id));
    }

    [Fact]
    public async Task RefusesToAlignATextOnlyBook()
    {
        var book = await _library.CreateFromTextAsync(
            new TextAttachment(_ebookPath, "text-1", _bookText.Length, []));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Aligner(Narration()).AlignAsync(book.Id));
    }

    [Fact]
    public async Task MarksTheBookFailedWhenTranscriptionBreaks()
    {
        var book = await CreatePairedBookAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Aligner(new BrokenTranscriber()).AlignAsync(book.Id));

        var failed = await _database.GetBookAsync(book.Id);
        Assert.Equal(SyncState.Failed, failed!.SyncState);
    }

    private sealed class CancelAfter(ITranscriber inner, int calls, CancellationTokenSource cancellation) : ITranscriber
    {
        private int _seen;

        public Task<Transcript> TranscribeAsync(string audioPath, long startMs, long durationMs, CancellationToken ct = default)
        {
            if (++_seen > calls) cancellation.Cancel();
            return inner.TranscribeAsync(audioPath, startMs, durationMs, ct);
        }
    }

    private sealed class BrokenTranscriber : ITranscriber
    {
        public Task<Transcript> TranscribeAsync(string audioPath, long startMs, long durationMs, CancellationToken ct = default) =>
            throw new InvalidOperationException("the decoder gave up");
    }
}
