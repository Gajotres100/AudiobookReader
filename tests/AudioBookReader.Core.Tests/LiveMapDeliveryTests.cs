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
}
