namespace AudioBookReader.Core.Books;

/// <summary>
/// Splits normalized book text into sentences.
///
/// Deliberately a heuristic rather than a parser: the output drives highlighting and the unit of
/// alignment refinement, so an occasional over- or under-split costs the reader a slightly early
/// or late highlight, not correctness. It runs over entire books on a phone, which rules out
/// anything heavier.
///
/// Callers must pass text where a newline always means a paragraph break — see the extractors.
/// </summary>
public static class SentenceSplitter
{
    /// <summary>
    /// Words that end in a period without ending a sentence. Missing one costs a spurious split
    /// mid-sentence, so the list covers the common English and Croatian cases rather than trying
    /// to be exhaustive.
    /// </summary>
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        // Titles and names
        "mr", "mrs", "ms", "messrs", "dr", "prof", "st", "sr", "jr", "capt", "col", "gen", "lt",
        "sgt", "rev", "hon", "gđa", "gđica", "mag",
        // References and measures
        "vs", "etc", "inc", "ltd", "co", "no", "vol", "ch", "chap", "pp", "fig", "cf", "al",
        "approx", "est", "ed", "eds", "trans", "op", "cit", "ibid",
        // Places
        "ave", "blvd", "rd", "mt", "ft",
        // Croatian
        "npr", "itd", "tzv", "str", "god", "sv", "ul", "br", "odn", "usp", "tj", "dipl",
    };

    /// <summary>Splits the whole string.</summary>
    public static List<(int Start, int End)> Split(string text) => Split(text, 0, text.Length);

    /// <summary>
    /// Splits the <paramref name="rangeStart"/>..<paramref name="rangeEnd"/> region, returning
    /// absolute offsets so per-document results concatenate into one book-wide list.
    /// </summary>
    public static List<(int Start, int End)> Split(string text, int rangeStart, int rangeEnd)
    {
        var sentences = new List<(int, int)>();
        var start = -1;
        var i = rangeStart;

        while (i < rangeEnd)
        {
            if (start < 0)
            {
                if (char.IsWhiteSpace(text[i])) { i++; continue; }
                start = i;
            }

            var c = text[i];

            if (c == '\n')
            {
                // Paragraph break: always ends a sentence, terminator or not. Books are full of
                // headings and dialogue lines that carry no final punctuation.
                Emit(sentences, text, start, i);
                start = -1;
                i++;
                continue;
            }

            if (IsTerminator(c))
            {
                var end = i + 1;
                while (end < rangeEnd && (IsTerminator(text[end]) || IsClosing(text[end]))) end++;

                if (EndsSentence(text, start, i, end, rangeEnd))
                {
                    Emit(sentences, text, start, end);
                    start = -1;
                }

                i = end;
                continue;
            }

            i++;
        }

        if (start >= 0) Emit(sentences, text, start, rangeEnd);
        return sentences;
    }

    private static void Emit(List<(int, int)> sentences, string text, int start, int end)
    {
        while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
        if (end > start) sentences.Add((start, end));
    }

    private static bool IsTerminator(char c) => c is '.' or '!' or '?' or '…';

    private static bool IsClosing(char c) => c is '"' or '\'' or '»' or '”' or '’' or ')' or ']' or '}';

    /// <summary>
    /// Decides whether a run of terminators actually ends the sentence, given what precedes and
    /// follows it.
    /// </summary>
    private static bool EndsSentence(string text, int sentenceStart, int terminator, int end, int rangeEnd)
    {
        // Must be the last thing before a break in the text.
        if (end < rangeEnd && !char.IsWhiteSpace(text[end])) return false;

        if (text[terminator] == '.' && IsAbbreviation(text, sentenceStart, terminator)) return false;

        var next = end;
        while (next < rangeEnd && char.IsWhiteSpace(text[next])) next++;
        if (next >= rangeEnd) return true;

        return StartsSentence(text[next]);
    }

    private static bool StartsSentence(char c) =>
        char.IsUpper(c) || char.IsDigit(c) ||
        c is '"' or '\'' or '«' or '“' or '‘' or '(' or '[' or '—' or '–';

    private static bool IsAbbreviation(string text, int sentenceStart, int period)
    {
        var wordStart = period;
        while (wordStart > sentenceStart && char.IsLetter(text[wordStart - 1])) wordStart--;

        var length = period - wordStart;
        if (length == 0) return false;

        // A lone letter before the period is an initial ("J. R. R. Tolkien"), not a sentence end.
        if (length == 1) return true;

        return Abbreviations.Contains(text.AsSpan(wordStart, length).ToString());
    }
}
