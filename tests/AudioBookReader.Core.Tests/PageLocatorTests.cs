using AudioBookReader.Core.Alignment;

namespace AudioBookReader.Core.Tests;

public class PageLocatorTests
{
    private static readonly string Prose = FakeNarration.GenerateProse(sentenceCount: 400);
    private static readonly TokenizedText Book = TokenizedText.Create(Prose);

    /// <summary>A stand-in for what OCR actually reads off a page: mid-book, several sentences.</summary>
    private static string ExcerptAround(int charOffset, int length)
    {
        var start = Math.Clamp(charOffset, 0, Prose.Length);
        var end = Math.Clamp(start + length, start, Prose.Length);
        return Prose[start..end];
    }

    /// <summary>A third of the way in, comfortably clear of both ends for any excerpt length used here.</summary>
    private static readonly int TargetOffset = Prose.Length / 3;

    [Fact]
    public void FindsAVerbatimExcerptWhereItActuallyIsInTheBook()
    {
        var excerpt = ExcerptAround(TargetOffset, 400);

        var location = PageLocator.Locate(Book, excerpt);

        Assert.NotNull(location);
        Assert.InRange(location!.Value.CharOffset, TargetOffset - 20, TargetOffset + 20);
        Assert.True(location.Value.Confidence > 0.9f);
    }

    /// <summary>
    /// A handful of scattered character misreads, at roughly the rate a real scan produces --
    /// not every occurrence of a letter, which would corrupt most of the excerpt's words at once
    /// and test nothing about tolerance that a from-scratch mismatch wouldn't already show.
    /// </summary>
    private static string WithScatteredMisreads(string text)
    {
        var chars = text.ToCharArray();
        for (var i = 5; i < chars.Length; i += 23)
            if (char.IsLetter(chars[i])) chars[i] = chars[i] == 'l' ? '1' : 'x';

        return new string(chars);
    }

    [Fact]
    public void TolerantOfTypicalOcrMisreads()
    {
        var excerpt = WithScatteredMisreads(ExcerptAround(TargetOffset, 400));

        var location = PageLocator.Locate(Book, excerpt);

        Assert.NotNull(location);
        Assert.InRange(location!.Value.CharOffset, TargetOffset - 40, TargetOffset + 40);
    }

    [Fact]
    public void ReturnsNullWhenThePhotoIsFromADifferentBookEntirely()
    {
        var unrelated = FakeNarration.GenerateProse(sentenceCount: 30, seed: 999);

        var location = PageLocator.Locate(Book, unrelated);

        Assert.Null(location);
    }

    [Fact]
    public void ReturnsNullRatherThanTrustAHandfulOfWords()
    {
        // Genuinely lifted from the book, but too short to be sure it isn't a coincidence.
        var excerpt = ExcerptAround(Prose.Length / 6, 40);

        var location = PageLocator.Locate(Book, excerpt);

        Assert.Null(location);
    }

    /// <summary>
    /// The failure this feature was reported with, reduced to its cause.
    ///
    /// A photograph catches more than the page: the facing page, the reading app's own status bar
    /// and file name, a thumb, the desk. None of it is in the book. Scoring the match as a share
    /// of everything the camera saw let that surplus outvote a passage that had lined up
    /// perfectly — on the phone, a page whose opening matched at 0.95 was rejected outright, while
    /// cleaner shots of the same book scraped through at 0.70.
    /// </summary>
    [Fact]
    public void FindsThePageEvenWhenThePhotoCaughtMoreThanThePage()
    {
        var page = ExcerptAround(TargetOffset, 400);
        var surplus = string.Join(' ', FakeNarration.GenerateProse(sentenceCount: 12, seed: 7).Split(' ').Take(120));

        var location = PageLocator.Locate(Book, page + " " + surplus);

        Assert.NotNull(location);
        Assert.InRange(location!.Value.CharOffset, TargetOffset - 20, TargetOffset + 20);
    }

    /// <summary>
    /// The other half of the same change: scoring on the matched run rather than the whole photo
    /// must not turn a few words that happen to line up into a confident answer.
    /// </summary>
    [Fact]
    public void StillRefusesWhenOnlyAHandfulOfWordsLineUp()
    {
        // Long enough to be searched at all, and made of the book's own vocabulary so that short
        // coincidental runs are as likely as they ever get — but not a passage from it.
        var scrambled = string.Join(' ', Prose.Split(' ').Reverse().Take(60));

        Assert.Null(PageLocator.Locate(Book, scrambled));
    }

    [Fact]
    public void ReturnsNullOnEmptyRecognition()
    {
        Assert.Null(PageLocator.Locate(Book, ""));
        Assert.Null(PageLocator.Locate(Book, "   "));
    }

    /// <summary>
    /// The same two answers, asked of a book the size of a real one.
    ///
    /// Smith-Waterman is a local alignment, so the chance of some stretch of an unrelated book
    /// scoring above the confidence bar by coincidence grows with the number of places there are
    /// to look — and every other test here runs against a book roughly twenty times shorter than
    /// the novel this feature is for. The bar that decides "not in this book" is worth proving at
    /// the length where it is actually under pressure, since the cost of it giving way is the
    /// reader opening confidently at a passage that has nothing to do with the photo.
    /// </summary>
    [Fact]
    public void HoldsUpAgainstABookOfRealLength()
    {
        var novel = FakeNarration.GenerateProse(sentenceCount: 8000);
        var full = TokenizedText.Create(novel);

        Assert.InRange(novel.Length, 600_000, 1_000_000);

        var unrelated = FakeNarration.GenerateProse(sentenceCount: 30, seed: 999);
        Assert.Null(PageLocator.Locate(full, unrelated));

        var page = novel.Substring(novel.Length / 3, 1_800);
        var found = PageLocator.Locate(full, page);

        Assert.NotNull(found);
        Assert.InRange(found!.Value.CharOffset, novel.Length / 3 - 40, novel.Length / 3 + 40);
    }
}
