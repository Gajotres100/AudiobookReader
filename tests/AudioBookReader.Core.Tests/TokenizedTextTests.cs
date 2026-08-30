using AudioBookReader.Core.Alignment;

namespace AudioBookReader.Core.Tests;

public class TokenizedTextTests
{
    [Fact]
    public void SplitsOnPunctuationAndWhitespace() =>
        Assert.Equal(
            ["he", "woke", "early", "the", "house", "was", "cold"],
            TokenizedText.Create("He woke early. The house was cold!").Tokens.Select(t => t.Value));

    [Fact]
    public void KeepsApostrophesInsideWords() =>
        Assert.Equal(
            ["dont", "stop"],
            TokenizedText.Create("Don't stop").Tokens.Select(t => t.Value));

    [Fact]
    public void FoldsDiacriticsSoRecognizerOutputStillMatches()
    {
        // A recognizer that writes "cudno" must line up with a book that writes "čudno".
        var book = TokenizedText.Create("Bilo je čudno jutro.");
        var heard = TextNormalizer.NormalizeTranscript("bilo je cudno jutro");

        Assert.Equal(heard, book.Tokens.Select(t => t.Value));
    }

    [Fact]
    public void TokenOffsetsPointAtTheOriginalSpelling()
    {
        const string text = "Bilo je čudno jutro.";
        var tokens = TokenizedText.Create(text).Tokens;

        var third = tokens[2];
        Assert.Equal("cudno", third.Value);
        Assert.Equal("čudno", text[third.CharStart..third.CharEnd]);
    }

    [Fact]
    public void KeepsDigits() =>
        Assert.Equal(["room", "237"], TokenizedText.Create("Room 237.").Tokens.Select(t => t.Value));

    [Fact]
    public void ProducesNothingForTextWithoutWords() =>
        Assert.Empty(TokenizedText.Create("... --- !!!").Tokens);

    [Fact]
    public void FindsTheTokenStartingAtACharacterOffset()
    {
        const string text = "He woke early. The house was cold.";
        var tokenized = TokenizedText.Create(text);

        var index = tokenized.TokenIndexAtChar(text.IndexOf("house", StringComparison.Ordinal));

        Assert.Equal("house", tokenized.Tokens[index].Value);
    }

    [Fact]
    public void ReturnsTheEndIndexForAnOffsetPastTheLastToken()
    {
        var tokenized = TokenizedText.Create("Two words");

        Assert.Equal(tokenized.Count, tokenized.TokenIndexAtChar(9_999));
    }

    [Fact]
    public void NormalizesATranscriptTheSameWayAsTheBook() =>
        Assert.Equal(
            ["he", "woke", "early", "dont", "stop"],
            TextNormalizer.NormalizeTranscript(" He woke, early... don't STOP! "));
}
