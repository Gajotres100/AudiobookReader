using AudioBookReader.Core.Models;

namespace AudioBookReader.Core.Tests;

/// <summary>
/// Covers the two things that let the reader use a map while it is still being measured: reading a
/// copy rather than the list being written, and being carried a little past the last anchor.
/// </summary>
public class LiveMapDeliveryTests
{
    private static ChapterSyncMap Measured() => new()
    {
        ChapterIndex = 0,
        Anchors =
        [
            new Anchor(0, 0, 0.9f),
            new Anchor(10_000, 150, 0.9f),
            new Anchor(20_000, 300, 0.9f),
        ],
    };

    [Fact]
    public void CarriesThePositionForwardAtTheMeasuredPace()
    {
        Assert.True(Measured().TryExtrapolateCharOffset(25_000, withinMs: 20_000, out var offset));

        // Fifteen characters a second across the measured stretch, five seconds past the last anchor.
        Assert.Equal(375, offset);
    }

    [Fact]
    public void RefusesToCarryItFurtherThanAsked()
    {
        Assert.False(Measured().TryExtrapolateCharOffset(45_000, withinMs: 20_000, out _));
    }

    [Fact]
    public void RefusesPositionsTheAnchorsAlreadyCover()
    {
        // Inside the range there is real evidence, and the caller must use that rather than a pace.
        Assert.False(Measured().TryExtrapolateCharOffset(15_000, withinMs: 20_000, out _));
    }

    [Fact]
    public void RefusesWhenThereIsNoPaceToRead()
    {
        var single = new ChapterSyncMap { ChapterIndex = 0, Anchors = [new Anchor(0, 0, 0.9f)] };
        Assert.False(single.TryExtrapolateCharOffset(5_000, withinMs: 20_000, out _));
    }

    [Fact]
    public void ASnapshotDoesNotChangeWhenTheOriginalGrows()
    {
        var map = new SyncMap { AudioHash = "a", EbookHash = "b" };
        map.SetChapter(Measured());

        var copy = map.Snapshot();
        map.ForChapter(0)!.Insert(new Anchor(30_000, 450, 0.9f));

        Assert.Equal(3, copy.ForChapter(0)!.Anchors.Count);
        Assert.Equal(4, map.ForChapter(0)!.Anchors.Count);
        Assert.Equal("a", copy.AudioHash);
        Assert.Equal("b", copy.EbookHash);
    }

    [Fact]
    public void CountsTheAudioActuallyHeard()
    {
        // Three anchors ten seconds apart, measured by touching windows: twenty seconds of audio.
        Assert.Equal(20_000, Measured().MeasuredMs(windowMs: 15_000, boundaryConfidence: 0.2f));
    }

    [Fact]
    public void DoesNotCountTheGapsBetweenSparseAnchors()
    {
        // What a whole-book run leaves: anchors a minute apart, everything between interpolated.
        var sampled = new ChapterSyncMap
        {
            ChapterIndex = 0,
            Anchors =
            [
                new Anchor(0, 0, 0.9f),
                new Anchor(60_000, 900, 0.9f),
                new Anchor(120_000, 1800, 0.9f),
            ],
        };

        Assert.Equal(0, sampled.MeasuredMs(windowMs: 15_000, boundaryConfidence: 0.2f));
    }

    [Fact]
    public void IgnoresChapterBoundaryGuesses()
    {
        // Two boundary anchors span a whole chapter and mean nothing was heard in it.
        var visited = new ChapterSyncMap
        {
            ChapterIndex = 0,
            Anchors = [new Anchor(0, 0, 0.2f), new Anchor(1_800_000, 40_000, 0.2f)],
        };

        Assert.Equal(0, visited.MeasuredMs(windowMs: 15_000, boundaryConfidence: 0.2f));
    }

    [Fact]
    public void RefusesToReadAPaceFromOnePhrase()
    {
        // What a chapter holds after a single phrase has been located: two anchors a few hundred
        // milliseconds apart. The rate that falls out of those is tens of times too fast, and
        // carried twenty seconds forward it puts the highlight pages ahead of the voice.
        var onePhrase = new ChapterSyncMap
        {
            ChapterIndex = 0,
            Anchors = [new Anchor(30_000, 5_000, 0.9f), new Anchor(30_400, 5_200, 0.9f)],
        };

        Assert.False(onePhrase.TryExtrapolateCharOffset(35_000, withinMs: 20_000, out _));
    }

    [Fact]
    public void RefusesAPaceNoNarratorCouldKeep()
    {
        // A long stretch can still be measured badly — one anchor in the wrong place is enough.
        var wrong = new ChapterSyncMap
        {
            ChapterIndex = 0,
            Anchors = [new Anchor(0, 0, 0.9f), new Anchor(30_000, 90_000, 0.9f)],
        };

        Assert.False(wrong.TryExtrapolateCharOffset(35_000, withinMs: 20_000, out _));
    }
}
