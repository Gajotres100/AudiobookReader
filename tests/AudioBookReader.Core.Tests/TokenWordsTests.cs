using AudioBookReader.Core.Alignment;

namespace AudioBookReader.Core.Tests;

/// <summary>
/// Joining the recognizer's tokens back into words.
///
/// Worth testing on its own because it is the one piece of genuinely new logic in the move from
/// guessed word times to measured ones, and because getting it wrong is silent: a word split in
/// two still matches somewhere, just in the wrong place.
/// </summary>
public class TokenWordsTests
{
    private static List<TimedWord> Merge(params (string Text, long AtMs)[] tokens) =>
        TokenWords.Merge([.. tokens.Select(t => new SpokenToken(t.Text, t.AtMs))]);

    [Fact]
    public void JoinsSubwordPiecesIntoOneWord()
    {
        // How whisper actually emits a longer word: a leading space opens it, the rest continue it.
        var words = Merge((" nar", 1_000), ("rat", 1_100), ("or", 1_200));

        var word = Assert.Single(words);
        Assert.Equal("narrator", word.Value);
    }

    [Fact]
    public void TimesAWordFromItsFirstToken()
    {
        // The moment the word began. An anchor marks where the narrator was, not where they had
        // got to by the end of the word.
        var words = Merge((" nar", 1_000), ("rat", 1_100), ("or", 1_200));

        Assert.Equal(1_000, Assert.Single(words).AtMs);
    }

    [Fact]
    public void ALeadingSpaceStartsANewWord()
    {
        var words = Merge((" the", 500), (" cat", 900), (" sat", 1_400));

        Assert.Equal(["the", "cat", "sat"], words.Select(w => w.Value));
        Assert.Equal([500L, 900L, 1_400L], words.Select(w => w.AtMs));
    }

    [Fact]
    public void SplitsATokenThatCarriesSeveralWords()
    {
        // Short phrases arrive whole often enough that dropping the extras would lose real words.
        var words = Merge((" the cat sat", 500));

        Assert.Equal(["the", "cat", "sat"], words.Select(w => w.Value));
        Assert.All(words, w => Assert.Equal(500, w.AtMs));
    }

    [Fact]
    public void SkipsControlTokens()
    {
        var words = Merge(("[_BEG_]", 0), ("<|hr|>", 0), (" riječ", 700), ("<|endoftext|>", 900));

        Assert.Equal("rijec", Assert.Single(words).Value);
    }

    [Fact]
    public void DropsPunctuationThatStandsAlone()
    {
        // A comma is its own token and normalizes to nothing. It must not become a word, and it
        // must not swallow the time of the word after it.
        var words = Merge((" stao", 1_000), (",", 1_100), (" pa", 1_300));

        Assert.Equal(["stao", "pa"], words.Select(w => w.Value));
        Assert.Equal([1_000L, 1_300L], words.Select(w => w.AtMs));
    }

    [Fact]
    public void KeepsPunctuationAttachedToItsWord()
    {
        var words = Merge((" kraj", 2_000), (".", 2_100));

        Assert.Equal("kraj", Assert.Single(words).Value);
    }

    [Fact]
    public void NormalizesTheSameWayTheBookDoes()
    {
        // Both sides of the match go through TextNormalizer, so a word built from three tokens has
        // to come out exactly as the book's own tokenizer would have produced it.
        var merged = Assert.Single(Merge((" Čitao", 100)));

        Assert.Equal(TokenizedText.Create("Čitao").Tokens[0].Value, merged.Value);
    }

    [Fact]
    public void ReturnsNothingForNothing() => Assert.Empty(Merge());
}

/// <summary>
/// The transcript's own handling of word times: real ones when given, the old even spread when not.
/// </summary>
public class TranscriptWordTimeTests
{
    [Fact]
    public void UsesReportedWordTimesWhenTheyAreThere()
    {
        // The point of the whole change: a narrator who pauses in the middle of a segment. Spread
        // evenly, "sat" would be timed at the halfway mark; it was actually spoken near the end.
        IReadOnlyList<TimedWord> timed =
        [
            new("the", 0),
            new("cat", 200),
            new("sat", 4_800),
        ];

        var transcript = Transcript.FromSegments(
            [new TranscriptSegment(0, 5_000, "the cat sat", timed)]);

        Assert.Equal([0L, 200L, 4_800L], transcript.Words.Select(w => w.AtMs));
    }

    [Fact]
    public void FallsBackToSpreadingWhenNoneAreReported()
    {
        var transcript = Transcript.FromSegments([new TranscriptSegment(0, 3_000, "the cat sat")]);

        Assert.Equal([0L, 1_000L, 2_000L], transcript.Words.Select(w => w.AtMs));
    }

    [Fact]
    public void DiscardsSegmentsTheModelItselfCallsSilence()
    {
        // Small models invent sentences over the pauses between scenes, and a confident invention
        // matches somewhere and drags the interpolation around it.
        var transcript = Transcript.FromSegments(
        [
            new TranscriptSegment(0, 2_000, "real words here"),
            new TranscriptSegment(2_000, 4_000, "thanks for watching", NoSpeechProbability: 0.95f),
        ]);

        Assert.Single(transcript.Phrases);
        Assert.Equal("real words here", transcript.Segments[0].Text);
    }

    [Fact]
    public void CarriesRecognitionConfidenceOntoThePhrase()
    {
        var transcript = Transcript.FromSegments(
            [new TranscriptSegment(0, 2_000, "muffled words", Probability: 0.3f)]);

        Assert.Equal(0.3f, Assert.Single(transcript.Phrases).Probability, 3);
    }

    [Fact]
    public void DefaultsToFullConfidenceForARecognizerThatDoesNotSay()
    {
        var transcript = Transcript.FromSegments([new TranscriptSegment(0, 2_000, "plain words")]);

        Assert.Equal(1f, Assert.Single(transcript.Phrases).Probability, 3);
    }
}
