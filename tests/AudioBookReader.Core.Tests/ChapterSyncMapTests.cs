using AudioBookReader.Core.Models;

namespace AudioBookReader.Core.Tests;

public class ChapterSyncMapTests
{
    private static ChapterSyncMap Map(params (long Ms, int Char)[] anchors) =>
        ChapterSyncMap.FromAnchors(0, anchors.Select(a => new Anchor(a.Ms, a.Char, 1f)));

    [Fact]
    public void EmptyMapResolvesNothing()
    {
        var map = new ChapterSyncMap();

        Assert.False(map.TryGetAudioMs(100, out _));
        Assert.False(map.TryGetCharOffset(100, out _));
        Assert.True(map.IsEmpty);
    }

    [Fact]
    public void SingleAnchorResolvesToItselfInBothDirections()
    {
        var map = Map((5_000, 500));

        Assert.True(map.TryGetAudioMs(9_999, out var ms));
        Assert.Equal(5_000, ms);

        Assert.True(map.TryGetCharOffset(1, out var offset));
        Assert.Equal(500, offset);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(250, 2_500)]
    [InlineData(500, 5_000)]
    [InlineData(750, 7_500)]
    [InlineData(1_000, 10_000)]
    public void InterpolatesLinearlyBetweenAnchors(int charOffset, long expectedMs)
    {
        var map = Map((0, 0), (5_000, 500), (10_000, 1_000));

        Assert.True(map.TryGetAudioMs(charOffset, out var ms));
        Assert.Equal(expectedMs, ms);
    }

    [Fact]
    public void InterpolatesWithinTheCorrectSegmentWhenSpacingIsUneven()
    {
        // Second segment covers 3x the text in the same time, as it would where the narrator
        // reads faster or the anchors happen to land further apart.
        var map = Map((0, 0), (10_000, 500), (20_000, 2_000));

        Assert.True(map.TryGetAudioMs(250, out var earlyMs));
        Assert.Equal(5_000, earlyMs);

        Assert.True(map.TryGetAudioMs(1_250, out var lateMs));
        Assert.Equal(15_000, lateMs);
    }

    [Fact]
    public void ClampsOutsideTheAnchoredRange()
    {
        var map = Map((1_000, 100), (2_000, 200));

        Assert.True(map.TryGetAudioMs(-50, out var beforeMs));
        Assert.Equal(1_000, beforeMs);

        Assert.True(map.TryGetAudioMs(99_999, out var afterMs));
        Assert.Equal(2_000, afterMs);

        Assert.True(map.TryGetCharOffset(0, out var beforeChar));
        Assert.Equal(100, beforeChar);

        Assert.True(map.TryGetCharOffset(99_999, out var afterChar));
        Assert.Equal(200, afterChar);
    }

    [Fact]
    public void RoundTripsThroughBothDirections()
    {
        var map = Map((0, 0), (30_000, 2_800), (65_000, 6_100), (120_000, 11_500));

        for (var offset = 0; offset <= 11_500; offset += 137)
        {
            Assert.True(map.TryGetAudioMs(offset, out var ms));
            Assert.True(map.TryGetCharOffset(ms, out var back));

            // Rounding to whole milliseconds costs at most a character either way.
            Assert.InRange(back, offset - 1, offset + 1);
        }
    }

    [Fact]
    public void DropsAnAnchorThatContradictsTheRest()
    {
        // A bad fuzzy match placed the third anchor back near the start of the chapter.
        var map = ChapterSyncMap.FromAnchors(0,
        [
            new Anchor(1_000, 100, 0.9f),
            new Anchor(2_000, 200, 0.9f),
            new Anchor(3_000, 50, 0.9f),
            new Anchor(4_000, 400, 0.9f),
        ]);

        Assert.Equal(3, map.Anchors.Count);
        Assert.DoesNotContain(map.Anchors, a => a.CharOffset == 50);
    }

    [Fact]
    public void PrefersTheHigherConfidenceReadingWhenTwoChainsConflict()
    {
        // Two mutually exclusive interpretations of the same chapter: a pair of confident anchors
        // against a pair of doubtful ones. The confident pair should win.
        var map = ChapterSyncMap.FromAnchors(0,
        [
            new Anchor(1_000, 5_000, 0.2f),
            new Anchor(2_000, 6_000, 0.2f),
            new Anchor(3_000, 100, 0.95f),
            new Anchor(4_000, 200, 0.95f),
        ]);

        Assert.Equal(2, map.Anchors.Count);
        Assert.All(map.Anchors, a => Assert.True(a.Confidence > 0.9f));
    }

    [Fact]
    public void KeepsEveryAnchorWhenTheyAreAlreadyConsistent()
    {
        var map = Map((0, 0), (1_000, 10), (2_000, 20), (3_000, 30));

        Assert.Equal(4, map.Anchors.Count);
    }

    [Fact]
    public void SortsAnchorsGivenOutOfOrder()
    {
        var map = Map((3_000, 30), (1_000, 10), (2_000, 20));

        Assert.Equal([10, 20, 30], map.Anchors.Select(a => a.CharOffset));
    }
}
