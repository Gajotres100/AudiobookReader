using AudioBookReader.Core.Books;

namespace AudioBookReader.Core.Tests;

public class SentenceSplitterTests
{
    private static string[] SplitToStrings(string text) =>
        SentenceSplitter.Split(text).Select(s => text[s.Start..s.End]).ToArray();

    [Fact]
    public void SplitsOnSentenceTerminators() =>
        Assert.Equal(
            ["He woke early.", "The house was cold.", "Was anyone awake?"],
            SplitToStrings("He woke early. The house was cold. Was anyone awake?"));

    [Fact]
    public void KeepsClosingQuotesWithTheSentenceTheyEnd() =>
        Assert.Equal(
            ["\"Get out!\"", "She did not move."],
            SplitToStrings("\"Get out!\" She did not move."));

    [Fact]
    public void DoesNotSplitOnATitleAbbreviation() =>
        Assert.Equal(
            ["Dr. Sharma arrived at noon."],
            SplitToStrings("Dr. Sharma arrived at noon."));

    [Fact]
    public void DoesNotSplitOnInitials() =>
        Assert.Equal(
            ["J. R. R. Tolkien wrote it."],
            SplitToStrings("J. R. R. Tolkien wrote it."));

    [Fact]
    public void DoesNotSplitOnACroatianAbbreviation() =>
        Assert.Equal(
            ["Vidi npr. drugo poglavlje."],
            SplitToStrings("Vidi npr. drugo poglavlje."));

    [Fact]
    public void DoesNotSplitInsideADecimalNumber() =>
        Assert.Equal(
            ["It cost 3.50 that morning."],
            SplitToStrings("It cost 3.50 that morning."));

    [Fact]
    public void DoesNotSplitWhenTheNextWordIsLowercase() =>
        Assert.Equal(
            ["The Ph.D. thesis was late."],
            SplitToStrings("The Ph.D. thesis was late."));

    [Fact]
    public void SplitsOnParagraphBreaksWithoutPunctuation() =>
        Assert.Equal(
            ["Chapter One", "The Arrival", "It began in autumn."],
            SplitToStrings("Chapter One\nThe Arrival\nIt began in autumn."));

    [Fact]
    public void TreatsAnEllipsisFollowedByACapitalAsABreak() =>
        Assert.Equal(
            ["He hesitated…", "Then he spoke."],
            SplitToStrings("He hesitated… Then he spoke."));

    [Fact]
    public void KeepsRunsOfTerminatorsTogether() =>
        Assert.Equal(
            ["What?!", "You cannot mean it."],
            SplitToStrings("What?! You cannot mean it."));

    [Fact]
    public void EmitsTheTrailingFragmentWhenTextEndsWithoutPunctuation() =>
        Assert.Equal(
            ["A complete one.", "And then a fragment"],
            SplitToStrings("A complete one. And then a fragment"));

    [Fact]
    public void IgnoresLeadingAndTrailingWhitespace() =>
        Assert.Equal(["Only this."], SplitToStrings("   \n  Only this.   \n\n "));

    [Fact]
    public void ReturnsNothingForBlankText()
    {
        Assert.Empty(SentenceSplitter.Split(""));
        Assert.Empty(SentenceSplitter.Split("   \n\n  \t "));
    }

    [Fact]
    public void SentencesCoverTheTextInOrderWithoutOverlapping()
    {
        const string text = "First one. Second one! Third?\nFourth paragraph starts here.";

        var sentences = SentenceSplitter.Split(text);

        var previousEnd = 0;
        foreach (var (start, end) in sentences)
        {
            Assert.True(start >= previousEnd, "sentences overlap");
            Assert.True(end > start, "empty sentence emitted");
            previousEnd = end;
        }

        Assert.True(previousEnd <= text.Length);
    }

    [Fact]
    public void SplittingASubRangeReturnsAbsoluteOffsets()
    {
        const string text = "Skip this. Take this one. And this.";
        const int rangeStart = 11;

        var sentences = SentenceSplitter.Split(text, rangeStart, text.Length);

        Assert.Equal("Take this one.", text[sentences[0].Start..sentences[0].End]);
        Assert.Equal(rangeStart, sentences[0].Start);
    }
}
