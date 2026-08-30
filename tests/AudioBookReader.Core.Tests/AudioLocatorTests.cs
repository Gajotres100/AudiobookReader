using AudioBookReader.Core.Alignment;
using AudioBookReader.Core.Models;

namespace AudioBookReader.Core.Tests;

public class AudioLocatorTests
{
    private const long ChapterMs = 30 * 60 * 1_000;

    private static (string Prose, FakeNarration Narration, List<Chapter> Chapters) Build()
    {
        var prose = FakeNarration.GenerateProse(sentenceCount: 400);
        var narration = new FakeNarration(prose, ChapterMs, paceVariation: 0.12, pauseMs: 3_000);

        List<Chapter> chapters =
        [
            new() { Index = 0, StartMs = 0, EndMs = ChapterMs, TextStart = 0, TextEnd = prose.Length },
        ];

        return (prose, narration, chapters);
    }

    private static AudioLocator Locator(string prose, ITranscriber transcriber, List<Chapter> chapters) =>
        new(TokenizedText.Create(prose), chapters, transcriber, prose.Length);

    /// <summary>When the narrator truly reaches a character, found by searching the known truth.</summary>
    private static long TrueTimeOf(FakeNarration narration, int charOffset)
    {
        long lo = 0, hi = ChapterMs;

        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (narration.CharAt(mid) >= charOffset) hi = mid;
            else lo = mid + 1;
        }

        return lo;
    }

    [Fact]
    public async Task FindsAPassageInAudioNothingHasEverAligned()
    {
        // The point of the feature: tapping a sentence works anywhere, not only where a run has
        // already been — and most of a book being read as it aligns has not been.
        var (prose, narration, chapters) = Build();
        var target = prose.Length / 3;

        var found = await Locator(prose, narration, chapters).FindAsync("book.m4b", target, map: null);

        Assert.NotNull(found);

        // Within a couple of seconds of where the narrator actually says it.
        Assert.InRange(Math.Abs(found.AudioMs - TrueTimeOf(narration, target)), 0, 3_000);
    }

    [Theory]
    [InlineData(0.05)]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(0.8)]
    [InlineData(0.95)]
    public async Task LandsCloseWhereverInTheBookTheTargetIs(double through)
    {
        var (prose, narration, chapters) = Build();
        var target = (int)(prose.Length * through);

        var found = await Locator(prose, narration, chapters).FindAsync("book.m4b", target, map: null);

        Assert.NotNull(found);
        Assert.InRange(Math.Abs(found.AudioMs - TrueTimeOf(narration, target)), 0, 3_000);
    }

    [Fact]
    public async Task SpendsOnlyAFewProbesGettingThere()
    {
        // A tap has to feel like a tap. Each probe is ten seconds of audio through recognition, so
        // the difference between converging and scanning is the difference between a wait and a
        // hang.
        var (prose, narration, chapters) = Build();

        await Locator(prose, narration, chapters).FindAsync("book.m4b", prose.Length / 3, map: null);

        Assert.InRange(narration.TranscribeCalls, 1, 4);
    }

    [Fact]
    public async Task BringsBackTheAnchorsItLearnedOnTheWay()
    {
        // The search listens to real audio and matches it; throwing that away would mean paying for
        // it twice, once here and again when reading reaches the same place.
        var (prose, narration, chapters) = Build();

        var found = await Locator(prose, narration, chapters).FindAsync("book.m4b", prose.Length / 2, map: null);

        Assert.NotNull(found);
        Assert.NotEmpty(found.Anchors);
        Assert.All(found.Anchors, a => Assert.True(a.Confidence > 0.5f));
    }

    [Fact]
    public async Task StartsFromTheMapWhereItAlreadyReaches()
    {
        var (prose, narration, chapters) = Build();
        var target = prose.Length / 4;

        // A map that already covers the target should make this nearly free.
        var aligned = await new ChapterAligner(TokenizedText.Create(prose), new FakeNarration(prose, ChapterMs))
            .AlignAsync(new ChapterAlignmentRequest("book.m4b", 0, 0, ChapterMs, 0, prose.Length));

        var map = new SyncMap();
        map.SetChapter(aligned);

        var found = await Locator(prose, narration, chapters).FindAsync("book.m4b", target, map);

        Assert.NotNull(found);
        Assert.InRange(Math.Abs(found.AudioMs - TrueTimeOf(narration, target)), 0, 3_000);
        Assert.InRange(narration.TranscribeCalls, 1, 2);
    }

    [Fact]
    public async Task ReturnsNothingRatherThanGuessingWhenTheAudioDoesNotMatchTheBook()
    {
        var (prose, _, chapters) = Build();

        var found = await Locator(prose, new SilentTranscriber(), chapters)
            .FindAsync("book.m4b", prose.Length / 2, map: null);

        Assert.Null(found);
    }

    [Fact]
    public async Task StopsWhenCancelled()
    {
        var (prose, narration, chapters) = Build();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Locator(prose, narration, chapters).FindAsync("book.m4b", 100, null, null, cancellation.Token));
    }

    private sealed class SilentTranscriber : ITranscriber
    {
        public Task<Transcript> TranscribeAsync(string path, long startMs, long durationMs, CancellationToken ct = default) =>
            Task.FromResult(Transcript.FromText(
                "static hiss unintelligible murmuring background noise nothing recognizable",
                startMs,
                durationMs));
    }
}
