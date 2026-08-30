using AudioBookReader.Core.Models;

namespace AudioBookReader.Core.Tests;

public class SyncMapTests
{
    [Fact]
    public void SetChapterKeepsChaptersOrderedRegardlessOfInsertionOrder()
    {
        var map = new SyncMap();

        map.SetChapter(new ChapterSyncMap { ChapterIndex = 3 });
        map.SetChapter(new ChapterSyncMap { ChapterIndex = 0 });
        map.SetChapter(new ChapterSyncMap { ChapterIndex = 2 });

        Assert.Equal([0, 2, 3], map.Chapters.Select(c => c.ChapterIndex));
    }

    [Fact]
    public void SetChapterReplacesAnExistingChapter()
    {
        var map = new SyncMap();
        map.SetChapter(new ChapterSyncMap { ChapterIndex = 1 });
        map.SetChapter(ChapterSyncMap.FromAnchors(1, [new Anchor(0, 0, 1f), new Anchor(10, 10, 1f)]));

        Assert.Single(map.Chapters);
        Assert.Equal(2, map.ForChapter(1)!.Anchors.Count);
    }

    [Fact]
    public void ForChapterReturnsNullForAnUnalignedChapter() =>
        Assert.Null(new SyncMap().ForChapter(7));

    [Fact]
    public void MatchesPairRejectsADifferentFilePair()
    {
        var map = new SyncMap { AudioHash = "a", EbookHash = "b" };

        Assert.True(map.MatchesPair("a", "b"));
        Assert.False(map.MatchesPair("a", "different"));
        Assert.False(map.MatchesPair("different", "b"));
    }

    [Fact]
    public void MatchesPairRejectsAMapWrittenByAnOlderVersion()
    {
        var map = new SyncMap { Version = SyncMap.CurrentVersion - 1, AudioHash = "a", EbookHash = "b" };

        Assert.False(map.MatchesPair("a", "b"));
    }
}
