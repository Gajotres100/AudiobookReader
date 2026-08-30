using AudioBookReader.Core.Alignment;

namespace AudioBookReader.Core.Tests;

public class TranscriptMatcherTests
{
    /// <summary>A passage long enough that a wrong match has somewhere to hide.</summary>
    private const string Passage = """
        He woke early that morning and the house was bitterly cold. Frost had climbed the inside
        of the window and the fire in the grate had died to nothing at all. Somewhere below him a
        door closed, carefully, as though whoever closed it did not wish to be heard. He lay very
        still and listened to the silence that followed and found that he could not tell whether
        anyone was moving down there or whether the old house was simply settling on its timbers
        as it always did in the cold. After a long while he pushed back the blankets and set his
        feet on the boards and went to find out for himself what the morning had brought him.
        """;

    private static readonly TokenizedText Book = TokenizedText.Create(Passage);

    private static List<string> Heard(string text) => TextNormalizer.NormalizeTranscript(text);

    /// <summary>Token index of a phrase, as the answer the matcher should arrive at.</summary>
    private static int ExpectedTokenIndex(string phrase)
    {
        var words = Heard(phrase);
        for (var i = 0; i + words.Count <= Book.Count; i++)
        {
            var found = true;
            for (var j = 0; j < words.Count && found; j++)
                found = Book.Tokens[i + j].Value == words[j];

            if (found) return i;
        }

        throw new InvalidOperationException($"'{phrase}' is not in the test passage.");
    }

    // ---- Finding the position ----

    [Fact]
    public void FindsAnExactTranscript()
    {
        const string phrase = "door closed carefully as though whoever closed it did not wish to be heard";

        var match = TranscriptMatcher.Match(Book, Heard(phrase), 0, Book.Count);

        Assert.NotNull(match);
        Assert.Equal(ExpectedTokenIndex(phrase), match.Value.TokenIndex);
        Assert.Equal(1f, match.Value.Confidence, 0.001f);
    }

    [Fact]
    public void FindsATranscriptWithSubstitutedWords()
    {
        // "bitterly" misheard as "bitter", "grate" as "great" — the shape recognition errors take.
        var match = TranscriptMatcher.Match(
            Book,
            Heard("and the house was bitter cold frost had climbed the inside of the window and the fire in the great had died"),
            0, Book.Count);

        Assert.NotNull(match);
        Assert.InRange(match.Value.TokenIndex, ExpectedTokenIndex("and the house was") - 2, ExpectedTokenIndex("and the house was") + 2);
        Assert.True(match.Value.Confidence > 0.7f, $"confidence was {match.Value.Confidence}");
    }

    [Fact]
    public void FindsATranscriptWithDroppedWords()
    {
        var match = TranscriptMatcher.Match(
            Book,
            Heard("he lay very still listened to the silence followed and found he could not tell"),
            0, Book.Count);

        Assert.NotNull(match);
        Assert.InRange(match.Value.TokenIndex, ExpectedTokenIndex("he lay very still") - 2, ExpectedTokenIndex("he lay very still") + 2);
    }

    [Fact]
    public void FindsATranscriptWithInventedWords()
    {
        var match = TranscriptMatcher.Match(
            Book,
            Heard("after a very long while he then pushed back the blankets and set his own feet on the boards"),
            0, Book.Count);

        Assert.NotNull(match);
        Assert.InRange(match.Value.TokenIndex, ExpectedTokenIndex("after a long while") - 2, ExpectedTokenIndex("after a long while") + 2);
    }

    [Fact]
    public void ReportsTheCharacterOffsetOfTheMatch()
    {
        const string phrase = "somewhere below him a door closed";

        var match = TranscriptMatcher.Match(Book, Heard(phrase), 0, Book.Count);

        Assert.NotNull(match);
        Assert.StartsWith("Somewhere below him", Passage[match.Value.CharOffset..]);
    }

    // ---- Rejecting what it should not match ----

    [Fact]
    public void RejectsATranscriptThatIsNotInTheBook()
    {
        var match = TranscriptMatcher.Match(
            Book,
            Heard("quarterly revenue exceeded analyst expectations across every regional segment"),
            0, Book.Count);

        Assert.Null(match);
    }

    [Fact]
    public void RejectsAMatchBelowTheConfidenceThreshold()
    {
        var transcript = Heard("door closed carefully as though whoever closed it");

        Assert.NotNull(TranscriptMatcher.Match(Book, transcript, 0, Book.Count, minConfidence: 0.9f));
        Assert.Null(TranscriptMatcher.Match(Book, Heard("some entirely unrelated words here"), 0, Book.Count, minConfidence: 0.9f));
    }

    [Fact]
    public void ScoresAnExactTranscriptAboveANoisyOne()
    {
        const string phrase = "he pushed back the blankets and set his feet on the boards";

        var exact = TranscriptMatcher.Match(Book, Heard(phrase), 0, Book.Count);
        var noisy = TranscriptMatcher.Match(Book, Heard("he pushed back the blanket and sat his feet on some boards"), 0, Book.Count);

        Assert.NotNull(exact);
        Assert.NotNull(noisy);
        Assert.True(exact.Value.Confidence > noisy.Value.Confidence);
    }

    // ---- The search window ----

    [Fact]
    public void DoesNotFindAPhraseOutsideTheWindow()
    {
        const string phrase = "he pushed back the blankets";
        var at = ExpectedTokenIndex(phrase);

        // A window ending well before the phrase must not report it.
        Assert.Null(TranscriptMatcher.Match(Book, Heard(phrase), 0, at - 20, minConfidence: 0.8f));
    }

    [Fact]
    public void MatchNearSearchesAroundThePredictedPosition()
    {
        const string phrase = "found that he could not tell whether anyone was moving";
        var at = ExpectedTokenIndex(phrase);

        // The prediction is off by a dozen words, well within the radius.
        var match = TranscriptMatcher.MatchNear(Book, Heard(phrase), at + 12, radiusTokens: 40);

        Assert.NotNull(match);
        Assert.Equal(at, match.Value.TokenIndex);
    }

    [Fact]
    public void MatchNearMissesWhenThePredictionIsFurtherOutThanTheRadius()
    {
        const string phrase = "frost had climbed the inside of the window";
        var at = ExpectedTokenIndex(phrase);

        Assert.Null(TranscriptMatcher.MatchNear(Book, Heard(phrase), at + 200, radiusTokens: 10, minConfidence: 0.8f));
    }

    [Fact]
    public void PrefersTheOccurrenceInsideTheWindowWhenAPhraseRepeats()
    {
        var book = TokenizedText.Create(
            "the door closed carefully. " + string.Join(" ", Enumerable.Repeat("filler words here", 40)) +
            " the door closed carefully.");

        var secondOccurrence = book.Count - 4;

        var match = TranscriptMatcher.MatchNear(book, Heard("the door closed carefully"), secondOccurrence, radiusTokens: 10);

        Assert.NotNull(match);
        Assert.InRange(match.Value.TokenIndex, secondOccurrence - 2, secondOccurrence + 2);
    }

    // ---- Degenerate input ----

    [Fact]
    public void ReturnsNothingForAnEmptyTranscript() =>
        Assert.Null(TranscriptMatcher.Match(Book, [], 0, Book.Count));

    [Fact]
    public void ReturnsNothingForAnEmptyWindow() =>
        Assert.Null(TranscriptMatcher.Match(Book, Heard("he woke early"), 10, 10));

    [Fact]
    public void ClampsAWindowThatRunsOutsideTheBook()
    {
        var match = TranscriptMatcher.Match(Book, Heard("he woke early that morning"), -500, Book.Count + 500);

        Assert.NotNull(match);
        Assert.Equal(0, match.Value.TokenIndex);
    }
}
