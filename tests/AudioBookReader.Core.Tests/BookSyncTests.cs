using AudioBookReader.Core.Books;
using AudioBookReader.Core.Models;

namespace AudioBookReader.Core.Tests;

public class BookSyncTests
{
    private const string Prose =
        "He woke early.\nThe house was cold.\nNobody stirred.\n" +
        "She knocked twice.\nNo answer came.\nHe opened the door.";

    private const long ChapterMs = 60_000;

    private static readonly int Midpoint = Prose.IndexOf("She knocked", StringComparison.Ordinal);

    private static BookText BuildText()
    {
        var sentences = SentenceSplitter.Split(Prose)
            .Select((range, i) => new Sentence(i, range.Start, range.End, 0))
            .ToList();

        return new BookText
        {
            PlainText = Prose,
            Sentences = sentences,
            Spine =
            [
                new SpineDocument { Index = 0, Href = "one.xhtml", TextStart = 0, TextEnd = Midpoint, Html = "" },
                new SpineDocument { Index = 1, Href = "two.xhtml", TextStart = Midpoint, TextEnd = Prose.Length, Html = "" },
            ],
        };
    }

    private static List<Chapter> BuildChapters() =>
    [
        new() { Index = 0, Title = "One", StartMs = 0, EndMs = ChapterMs, TextStart = 0, TextEnd = Midpoint },
        new() { Index = 1, Title = "Two", StartMs = ChapterMs, EndMs = 2 * ChapterMs, TextStart = Midpoint, TextEnd = Prose.Length },
    ];

    private static SyncMap BuildMap(bool alignSecondChapter = true)
    {
        var map = new SyncMap();

        map.SetChapter(ChapterSyncMap.FromAnchors(0,
            [new Anchor(0, 0, 1f), new Anchor(ChapterMs, Midpoint, 1f)]));

        if (alignSecondChapter)
        {
            map.SetChapter(ChapterSyncMap.FromAnchors(1,
                [new Anchor(ChapterMs, Midpoint, 1f), new Anchor(2 * ChapterMs, Prose.Length, 1f)]));
        }

        return map;
    }

    private static BookSync Build(bool alignSecondChapter = true) =>
        new(BuildText(), BuildMap(alignSecondChapter), BuildChapters());

    // ---- Following the narration ----

    [Fact]
    public void FindsTheSentenceBeingSpoken()
    {
        var sync = Build();

        // A quarter into the first chapter is the second of its three sentences.
        var sentence = sync.SentenceAt(ChapterMs / 3);

        Assert.NotNull(sentence);
        Assert.Equal("The house was cold.", Prose[sentence.Value.Start..sentence.Value.End]);
    }

    [Fact]
    public void CrossesIntoTheSecondChapter()
    {
        var sync = Build();

        var sentence = sync.SentenceAt(ChapterMs + ChapterMs / 2);

        Assert.NotNull(sentence);
        Assert.Equal("No answer came.", Prose[sentence.Value.Start..sentence.Value.End]);
    }

    [Fact]
    public void NamesTheDocumentTheReaderShouldHaveOpen()
    {
        var sync = Build();

        Assert.Equal(0, sync.DocumentAt(1_000)?.Index);
        Assert.Equal(1, sync.DocumentAt(ChapterMs + 1_000)?.Index);
    }

    // ---- Tap to seek ----

    [Fact]
    public void SeeksToWhereASentenceIsSpoken()
    {
        var sync = Build();
        var text = BuildText();

        var lastOfFirstChapter = text.Sentences.Last(s => s.End <= Midpoint);

        var at = sync.AudioPositionAtSentence(lastOfFirstChapter.Index);

        Assert.NotNull(at);
        Assert.InRange(at.Value, 0, ChapterMs);

        // Seeking there and asking again lands back on the same sentence.
        Assert.Equal(lastOfFirstChapter.Index, sync.SentenceAt(at.Value)?.Index);
    }

    [Fact]
    public void RoundTripsBetweenTextAndAudio()
    {
        var sync = Build();

        for (var offset = 0; offset < Prose.Length; offset += 7)
        {
            var at = sync.AudioPositionAtChar(offset);
            Assert.NotNull(at);

            var back = sync.CharOffsetAt(at.Value);
            Assert.NotNull(back);
            Assert.InRange(Math.Abs(back.Value - offset), 0, 2);
        }
    }

    [Fact]
    public void ReturnsNothingForASentenceIndexThatDoesNotExist()
    {
        var sync = Build();

        Assert.Null(sync.AudioPositionAtSentence(-1));
        Assert.Null(sync.AudioPositionAtSentence(9_999));
    }

    // ---- Partial alignment ----

    [Fact]
    public void ReportsWhichChaptersCanBeFollowed()
    {
        var sync = Build(alignSecondChapter: false);

        Assert.True(sync.IsAligned(0));
        Assert.False(sync.IsAligned(1));
    }

    [Fact]
    public void GivesNoPositionForAChapterThatIsNotAlignedYet()
    {
        var sync = Build(alignSecondChapter: false);

        Assert.NotNull(sync.SentenceAt(1_000));
        Assert.Null(sync.SentenceAt(ChapterMs + 1_000));
        Assert.Null(sync.CharOffsetAt(ChapterMs + 1_000));
    }

    [Fact]
    public void GivesNoSeekTargetForTextInAnUnalignedChapter()
    {
        var sync = Build(alignSecondChapter: false);

        Assert.NotNull(sync.AudioPositionAtChar(10));
        Assert.Null(sync.AudioPositionAtChar(Midpoint + 10));
    }

    // ---- Chapter lookup ----

    [Fact]
    public void FindsTheChapterForAPositionInEitherDimension()
    {
        var sync = Build();

        Assert.Equal(0, sync.ChapterAt(0)?.Index);
        Assert.Equal(1, sync.ChapterAt(ChapterMs)?.Index);
        Assert.Equal(0, sync.ChapterAtChar(0)?.Index);
        Assert.Equal(1, sync.ChapterAtChar(Midpoint)?.Index);
    }

    [Fact]
    public void ClampsAPositionPastTheEndToTheLastChapter()
    {
        var sync = Build();

        Assert.Equal(1, sync.ChapterAt(99 * ChapterMs)?.Index);
        Assert.Equal(1, sync.ChapterAtChar(99_999)?.Index);
    }
}
