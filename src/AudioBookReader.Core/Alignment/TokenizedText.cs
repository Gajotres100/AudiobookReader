using System.Globalization;
using System.Text;

namespace AudioBookReader.Core.Alignment;

/// <summary>A word of the book, normalized for comparison but remembering where it came from.</summary>
/// <param name="Value">Normalized form used for matching.</param>
/// <param name="CharStart">Offset of the word in the book's plain text.</param>
/// <param name="CharEnd">Exclusive end offset in the book's plain text.</param>
public readonly record struct TextToken(string Value, int CharStart, int CharEnd);

/// <summary>
/// The book text as a word sequence with a way back to character offsets.
///
/// Alignment compares words, not characters: speech recognition gets word boundaries roughly right
/// and spelling and punctuation frequently wrong, so words are the only unit where the two sides
/// can be expected to agree.
/// </summary>
public class TokenizedText
{
    public required IReadOnlyList<TextToken> Tokens { get; init; }

    public int Count => Tokens.Count;

    public static TokenizedText Create(string plainText)
    {
        var tokens = new List<TextToken>();
        var i = 0;

        while (i < plainText.Length)
        {
            if (!IsWordCharacter(plainText[i]))
            {
                i++;
                continue;
            }

            var start = i;
            while (i < plainText.Length && IsWordCharacter(plainText[i])) i++;

            var normalized = TextNormalizer.NormalizeWord(plainText.AsSpan(start, i - start));
            if (normalized.Length > 0) tokens.Add(new TextToken(normalized, start, i));
        }

        return new TokenizedText { Tokens = tokens };
    }

    /// <summary>
    /// Apostrophes are word-internal so "don't" stays one token rather than becoming "don" and "t",
    /// which would misalign against a recognizer that writes it either way.
    /// </summary>
    private static bool IsWordCharacter(char c) =>
        char.IsLetterOrDigit(c) || c is '\'' or '’';

    /// <summary>Index of the first token starting at or after <paramref name="charOffset"/>.</summary>
    public int TokenIndexAtChar(int charOffset)
    {
        int lo = 0, hi = Tokens.Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (Tokens[mid].CharStart >= charOffset) hi = mid;
            else lo = mid + 1;
        }
        return lo;
    }

    /// <summary>Character offset of a token, clamping past the end to the end of the text.</summary>
    public int CharOffsetOfToken(int tokenIndex, int textLength) =>
        tokenIndex < Tokens.Count ? Tokens[tokenIndex].CharStart : textLength;
}

/// <summary>Reduces words to a form the book and a speech recognizer can be compared on.</summary>
public static class TextNormalizer
{
    /// <summary>
    /// Lowercases, strips diacritics and drops everything that is not a letter or digit.
    ///
    /// Diacritics go because recognizer output is inconsistent about them — a Croatian narration
    /// transcribed as "cudno" must still match "čudno" in the book. Folding both sides costs
    /// nothing, since the token's character range still points at the original spelling.
    /// </summary>
    public static string NormalizeWord(ReadOnlySpan<char> raw)
    {
        var builder = new StringBuilder(raw.Length);

        foreach (var c in raw.ToString().Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (!char.IsLetterOrDigit(c)) continue;

            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }

    /// <summary>Normalizes a recognizer transcript into the same word form the book uses.</summary>
    public static List<string> NormalizeTranscript(string transcript)
    {
        var words = new List<string>();
        var i = 0;

        while (i < transcript.Length)
        {
            if (!char.IsLetterOrDigit(transcript[i]) && transcript[i] is not ('\'' or '’'))
            {
                i++;
                continue;
            }

            var start = i;
            while (i < transcript.Length &&
                   (char.IsLetterOrDigit(transcript[i]) || transcript[i] is '\'' or '’')) i++;

            var normalized = NormalizeWord(transcript.AsSpan(start, i - start));
            if (normalized.Length > 0) words.Add(normalized);
        }

        return words;
    }
}
