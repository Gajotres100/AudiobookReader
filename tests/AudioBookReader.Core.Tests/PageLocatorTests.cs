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

    [Fact]
    public void ReturnsNullOnEmptyRecognition()
    {
        Assert.Null(PageLocator.Locate(Book, ""));
        Assert.Null(PageLocator.Locate(Book, "   "));
    }
}
