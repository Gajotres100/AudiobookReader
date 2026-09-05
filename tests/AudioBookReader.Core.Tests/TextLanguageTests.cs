using AudioBookReader.Core.Books;

namespace AudioBookReader.Core.Tests;

public class TextLanguageTests
{
    /// <summary>Enough prose to be a fair sample, in the shape a book actually comes in.</summary>
    private static string Passage(string sentence) => string.Join(" ", Enumerable.Repeat(sentence, 30));

    private const string English =
        "The morning was cold and the house was quiet, and he waited by the window with her letter " +
        "in his hand, which he had not yet opened.";

    private const string Croatian =
        "Jutro je bilo hladno i kuća je bila tiha, a on je čekao kraj prozora s njezinim pismom u " +
        "ruci, koje još nije otvorio.";

    private const string German =
        "Der Morgen war kalt und das Haus war still, und er wartete mit ihrem Brief in der Hand am " +
        "Fenster, den er nicht geöffnet hatte.";

    [Fact]
    public void RecognisesEnglish() => Assert.Equal("en", TextLanguage.Detect(Passage(English)));

    [Fact]
    public void RecognisesCroatian() => Assert.Equal("hr", TextLanguage.Detect(Passage(Croatian)));

    [Fact]
    public void RecognisesGerman() => Assert.Equal("de", TextLanguage.Detect(Passage(German)));

    [Fact]
    public void RecognisesCroatianEvenWithoutItsDiacritics()
    {
        // Conversion between ebook formats strips them routinely, and the words are what carry the
        // language anyway — which is why this counts words rather than looking at the alphabet.
        var stripped = Passage(Croatian)
            .Replace('č', 'c').Replace('ć', 'c').Replace('ž', 'z').Replace('š', 's').Replace('đ', 'd');

        Assert.Equal("hr", TextLanguage.Detect(stripped));
    }

    [Fact]
    public void SaysNothingRatherThanGuessAtAShortFragment()
    {
        // A named language is obeyed, so a wrong guess is worse than none: tell recognition the
        // wrong language and every transcript comes back as nonsense.
        Assert.Null(TextLanguage.Detect("Hello there."));
        Assert.Null(TextLanguage.Detect(""));
        Assert.Null(TextLanguage.Detect("   "));
    }

    [Fact]
    public void SaysNothingForTextThatIsNotProse()
    {
        var numbers = string.Join(" ", Enumerable.Range(0, 400).Select(i => $"item{i}"));
        Assert.Null(TextLanguage.Detect(numbers));
    }

    [Fact]
    public void ReadsOnlyAsMuchAsItNeedsAndStops()
    {
        // The sample is capped, so detection costs the same for a novella and for a trilogy. Proven
        // by putting a second language past the cap: if it were read, it would change the answer.
        var opening = string.Join(" ", Enumerable.Repeat(English, 250));
        var tail = string.Concat(Enumerable.Repeat(Croatian, 5_000));

        Assert.Equal("en", TextLanguage.Detect(opening + " " + tail));
    }
}
