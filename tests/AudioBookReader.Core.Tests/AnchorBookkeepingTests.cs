using AudioBookReader.Core.Books;
using AudioBookReader.Core.Models;

namespace AudioBookReader.Core.Tests;

/// <summary>
/// How the map keeps its anchors, and what it admits to not knowing.
///
/// Both of these went wrong quietly. The map clamps outside its anchored range and reports success,
/// which read as "the narrator is here" when it meant "this is as far as I know"; and the live
/// aligner rebuilt every anchor a chapter had every fifteen seconds, which is fine for a minute and
/// ruinous after an hour.
/// </summary>
public class AnchorBookkeepingTests
{
    private static ChapterSyncMap Measured(params (long AudioMs, int CharOffset)[] anchors)
    {
        var map = new ChapterSyncMap { ChapterIndex = 0 };
        foreach (var (at, offset) in anchors) map.Insert(new Anchor(at, offset, 0.9f));

        return map;
    }

    // ---- Covers ----

    [Fact]
    public void KnowsWhichMomentsItActuallyCovers()
    {
        var map = Measured((10_000, 100), (20_000, 300));

        Assert.True(map.Covers(15_000));
        Assert.True(map.Covers(10_000));
        Assert.True(map.Covers(20_000));

        Assert.False(map.Covers(9_999));
        Assert.False(map.Covers(20_001));
    }

    [Fact]
    public void CoversNothingWhenItHasOnlyOneAnchor()
    {
        // One point fixes no interval, so every answer would be that point — which is a clamp
        // wearing the clothes of a measurement.
        Assert.False(Measured((10_000, 100)).Covers(10_000));
    }

    [Fact]
    public void StillAnswersOutsideItsRangeForInterpolation()
    {
        // The clamp itself stays: extrapolating the ends of a chapter is what it is for. Covers is
        // the way to tell that answer apart from a measured one.
        var map = Measured((10_000, 100), (20_000, 300));

        Assert.True(map.TryGetCharOffset(99_000, out var offset));
        Assert.Equal(300, offset);
    }

    // ---- Insert ----

    [Fact]
    public void PlacesAnchorsInOrderWhateverOrderTheyArrive()
    {
        var map = Measured((30_000, 500), (10_000, 100), (20_000, 300));

        Assert.Equal([10_000L, 20_000L, 30_000L], map.Anchors.Select(a => a.AudioMs));
        Assert.Equal([100, 300, 500], map.Anchors.Select(a => a.CharOffset));
    }

    [Fact]
    public void RefusesAnAnchorThatContradictsItsNeighbours()
    {
        var map = Measured((10_000, 100), (20_000, 300));

        // Later in the audio but earlier in the book: one of the three is wrong, and the two that
        // agree with each other are the better bet.
        Assert.False(map.Insert(new Anchor(15_000, 50, 0.9f)));

        // Earlier in the audio but later in the book, the same disagreement mirrored.
        Assert.False(map.Insert(new Anchor(15_000, 400, 0.9f)));

        Assert.Equal(2, map.Anchors.Count);
    }

    [Fact]
    public void RefusesADuplicateMoment()
    {
        var map = Measured((10_000, 100), (20_000, 300));

        Assert.False(map.Insert(new Anchor(20_000, 350, 0.9f)));
        Assert.Equal(2, map.Anchors.Count);
    }

    [Fact]
    public void StaysConsistentUnderThousandsOfInsertions()
    {
        // A long sync-on-the-fly session adds a few anchors every fifteen seconds for hours. The
        // point of this test is not speed but that the invariant every lookup depends on — strictly
        // increasing in both dimensions — still holds at that scale.
        var map = new ChapterSyncMap { ChapterIndex = 0 };

        for (var i = 0; i < 4_000; i++)
            map.Insert(new Anchor(i * 250, i * 4, 0.9f));

        Assert.Equal(4_000, map.Anchors.Count);

        for (var i = 1; i < map.Anchors.Count; i++)
        {
            Assert.True(map.Anchors[i].AudioMs > map.Anchors[i - 1].AudioMs);
            Assert.True(map.Anchors[i].CharOffset > map.Anchors[i - 1].CharOffset);
        }
    }

    // ---- What counts as aligned ----

    private const string Prose = "He woke early. The house was cold. Nobody stirred. She knocked twice.";

    private static BookSync BuildSync(ChapterSyncMap chapter)
    {
        var sentences = SentenceSplitter.Split(Prose)
            .Select((range, i) => new Sentence(i, range.Start, range.End, 0))
            .ToList();

        var text = new BookText
        {
            PlainText = Prose,
            Sentences = sentences,
            Spine = [new SpineDocument { Index = 0, Href = "one.xhtml", TextStart = 0, TextEnd = Prose.Length, Html = "" }],
        };

        var map = new SyncMap();
        map.SetChapter(chapter);

        List<Chapter> chapters =
        [
            new() { Index = 0, Title = "One", StartMs = 0, EndMs = 60_000, TextStart = 0, TextEnd = Prose.Length },
        ];

        return new BookSync(text, map, chapters);
    }

    [Fact]
    public void AChapterHoldingOnlyItsBoundaryGuessesIsNotAligned()
    {
        // Every chapter is seeded with two low-confidence guesses at its ends before any probe runs.
        // Counting those as an alignment told the reader to follow a straight line drawn between
        // two guesses, and told the library the book was finished.
        var guesses = new ChapterSyncMap { ChapterIndex = 0 };
        guesses.Insert(new Anchor(0, 0, ChapterSyncMap.BoundaryConfidence));
        guesses.Insert(new Anchor(60_000, Prose.Length, ChapterSyncMap.BoundaryConfidence));

        Assert.False(BuildSync(guesses).IsAligned(0));
    }

    [Fact]
    public void AChapterWithSomethingActuallyHeardIsAligned() =>
        Assert.True(BuildSync(Measured((0, 0), (60_000, Prose.Length))).IsAligned(0));

    [Fact]
    public void DoesNotReportAPositionItHasOnlyClampedTo()
    {
        // Past the last anchor the map can only repeat itself. Returning that as a real position
        // froze the highlight on one sentence while telling the reader everything was fine.
        var sync = BuildSync(Measured((0, 0), (10_000, 20)));

        Assert.NotNull(sync.CharOffsetAt(5_000));
        Assert.Null(sync.CharOffsetAt(50_000));
    }
}
