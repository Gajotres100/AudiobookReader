using AudioBookReader.Core.Alignment;
using AudioBookReader.Core.Models;

namespace AudioBookReader.Core.Tests;

public class LiveAlignerTests
{
    private const long ChapterMs = 30 * 60 * 1_000;

    /// <summary>
    /// Stops the run after a fixed number of windows, since following never ends on its own, and
    /// records where each one was, which is what most of these tests are actually about.
    /// </summary>
    private sealed class StopAfter(ITranscriber inner, int windows, CancellationTokenSource cancellation) : ITranscriber
    {
        public List<long> Windows { get; } = [];

        public int Calls => Windows.Count;

        public Task<Transcript> TranscribeAsync(string path, long startMs, long durationMs, CancellationToken ct = default)
        {
            Windows.Add(startMs);
            if (Windows.Count > windows) cancellation.Cancel();

            return inner.TranscribeAsync(path, startMs, durationMs, ct);
        }
    }

    private sealed class Fixture
    {
        public required string Prose { get; init; }
        public required FakeNarration Narration { get; init; }
        public required List<Chapter> Chapters { get; init; }
        public required SyncMap Map { get; init; }

        public static Fixture Create(long pauseMs = 3_000, bool knownTextRange = true)
        {
            var prose = FakeNarration.GenerateProse(sentenceCount: 400);

            return new Fixture
            {
                Prose = prose,
                Narration = new FakeNarration(prose, ChapterMs, paceVariation: 0.12, pauseMs: pauseMs),
                Chapters =
                [
                    new Chapter
                    {
                        Index = 0,
                        StartMs = 0,
                        EndMs = ChapterMs,
                        TextStart = knownTextRange ? 0 : null,
                        TextEnd = knownTextRange ? prose.Length : null,
                    },
                ],
                Map = new SyncMap(),
            };
        }

        public LiveAligner Aligner(ITranscriber transcriber, long maxLeadMs = 120_000) =>
            new(TokenizedText.Create(Prose), Chapters, transcriber, Prose.Length)
            {
                MaxLeadMs = maxLeadMs,
                IdleRest = TimeSpan.FromMilliseconds(1),
            };
    }

    /// <summary>
    /// Runs until the transcriber has been asked for <paramref name="windows"/> windows.
    ///
    /// The wall-clock cancellation is a backstop, not the mechanism: a run that decides it has
    /// nothing to do transcribes nothing, and without it a mistake there would hang the suite
    /// rather than fail it.
    /// </summary>
    private static async Task<StopAfter> RunAsync(
        Fixture fixture,
        Func<long> positionMs,
        int windows,
        long maxLeadMs = 600_000)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var transcriber = new StopAfter(fixture.Narration, windows, cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Aligner(transcriber, maxLeadMs).RunAsync(
                "book.m4b",
                fixture.Map,
                positionMs,
                () => Task.CompletedTask,
                progress: null,
                cancellation.Token));

        return transcriber;
    }

    // ---- Accuracy ----

    [Fact]
    public async Task MeasuresTheStretchAheadOfTheListenerInsteadOfInterpolatingIt()
    {
        var fixture = Fixture.Create();

        await RunAsync(fixture, () => 0, windows: 8);

        var chapter = fixture.Map.ForChapter(0);
        Assert.NotNull(chapter);

        // Eight touching fifteen-second windows cover two minutes; every moment inside was heard.
        //
        // Sampling starts after the first few seconds: the narrator's opening pause is silence no
        // recognizer can place, so the very beginning is the one moment nothing measures.
        for (var at = 5_000L; at < 100_000; at += 5_000)
        {
            Assert.True(chapter.TryGetCharOffset(at, out var predicted), $"nothing covers {at} ms");
            Assert.InRange(Math.Abs(predicted - fixture.Narration.CharAt(at)), 0, 40);
        }
    }

    [Fact]
    public async Task IsMarkedlyMorePreciseThanSamplingTheSameStretch()
    {
        // The point of the feature: over the passage being listened to, measuring every second of
        // it beats sampling a sixth of it and interpolating the rest.
        var fixture = Fixture.Create();
        await RunAsync(fixture, () => 0, windows: 8);

        var live = WorstError(fixture.Map.ForChapter(0)!, fixture.Narration, upToMs: 100_000);

        var sampled = await new ChapterAligner(
                TokenizedText.Create(fixture.Prose),
                new FakeNarration(fixture.Prose, ChapterMs, paceVariation: 0.12, pauseMs: 3_000))
            .AlignAsync(new ChapterAlignmentRequest("book.m4b", 0, 0, ChapterMs, 0, fixture.Prose.Length));

        var coarse = WorstError(sampled, fixture.Narration, upToMs: 100_000);

        Assert.True(live < coarse, $"live {live} characters, sampled {coarse}");
    }

    private static int WorstError(ChapterSyncMap map, FakeNarration narration, long upToMs)
    {
        var worst = 0;

        for (var at = 5_000L; at < upToMs; at += 2_000)
            if (map.TryGetCharOffset(at, out var predicted))
                worst = Math.Max(worst, Math.Abs(predicted - narration.CharAt(at)));

        return worst;
    }

    // ---- Following ----

    [Fact]
    public async Task WorksForwardFromWhereverTheListenerIs()
    {
        var fixture = Fixture.Create();
        const long startedAt = 12 * 60 * 1_000;

        var transcriber = await RunAsync(fixture, () => startedAt, windows: 4);

        // Nothing before the listener was listened to; the minute after them was measured.
        Assert.All(transcriber.Windows, at => Assert.True(at >= startedAt, $"listened at {at} ms"));

        var chapter = fixture.Map.ForChapter(0)!;

        Assert.True(chapter.TryGetCharOffset(startedAt + 30_000, out var predicted));
        Assert.InRange(Math.Abs(predicted - fixture.Narration.CharAt(startedAt + 30_000)), 0, 40);
    }

    [Fact]
    public async Task RestsOnceItIsFarEnoughAheadRatherThanAligningTheWholeBook()
    {
        var fixture = Fixture.Create();

        // A minute of lead over a stationary listener is four fifteen-second windows; asking for
        // twelve means the run must spend the rest of its time idle.
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var transcriber = new StopAfter(fixture.Narration, windows: 12, cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Aligner(transcriber, maxLeadMs: 60_000).RunAsync(
                "book.m4b", fixture.Map, () => 0, () => Task.CompletedTask, null, cancellation.Token));

        Assert.InRange(transcriber.Calls, 1, 6);
    }

    [Fact]
    public async Task DoesNotListenAgainToWhatIsAlreadyMeasured()
    {
        // Going back a page is the commonest thing a reader does, and re-recognising audio already
        // measured would spend the whole budget learning what is known.
        var fixture = Fixture.Create();

        await RunAsync(fixture, () => 0, windows: 8);

        var second = await RunAsync(fixture, () => 0, windows: 4);

        Assert.NotEmpty(second.Windows);
        Assert.All(second.Windows, at => Assert.True(at >= 100_000, $"listened again at {at} ms"));
    }

    [Fact]
    public async Task PicksUpAgainWhenTheListenerSeeksSomewhereNothingHasCovered()
    {
        var fixture = Fixture.Create();
        var position = 0L;

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var transcriber = new StopAfter(fixture.Narration, windows: 8, cancellation);

        // Jumps twenty minutes in after the first two windows, the way a listener skipping ahead does.
        var seen = 0;
        long Position()
        {
            if (++seen == 3) position = 20 * 60 * 1_000;
            return position;
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Aligner(transcriber).RunAsync(
                "book.m4b", fixture.Map, Position, () => Task.CompletedTask, null, cancellation.Token));

        var chapter = fixture.Map.ForChapter(0)!;

        Assert.True(chapter.TryGetCharOffset(20 * 60 * 1_000 + 20_000, out var predicted));
        Assert.InRange(Math.Abs(predicted - fixture.Narration.CharAt(20 * 60 * 1_000 + 20_000)), 0, 40);
    }

    // ---- Robustness ----

    [Fact]
    public async Task FindsItsPlaceInAChapterNothingHasEverMeasured()
    {
        // No text range on the chapter and no map: the only estimate available is the chapter's
        // share of the book, which can be thousands of characters out. The search has to widen
        // until it locks on rather than giving up.
        var fixture = Fixture.Create(knownTextRange: false);

        await RunAsync(fixture, () => 5 * 60 * 1_000, windows: 6);

        var chapter = fixture.Map.ForChapter(0);
        Assert.NotNull(chapter);
        Assert.Contains(chapter.Anchors, a => a.Confidence > 0.5f);
    }

    [Fact]
    public async Task SavesPeriodicallySoAStoppedRunKeepsWhatItLearned()
    {
        var fixture = Fixture.Create();
        var saves = 0;

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var transcriber = new StopAfter(fixture.Narration, windows: 12, cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Aligner(transcriber, maxLeadMs: 600_000).RunAsync(
                "book.m4b",
                fixture.Map,
                () => 0,
                () => { saves++; return Task.CompletedTask; },
                null,
                cancellation.Token));

        Assert.InRange(saves, 2, 4);
    }
}
