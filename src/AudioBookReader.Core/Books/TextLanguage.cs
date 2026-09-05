namespace AudioBookReader.Core.Books;

/// <summary>
/// Works out what language a book is written in, from the book's own words.
///
/// Recognition needs telling. Left to itself it spends an extra pass over every probe deciding what
/// language it is hearing — six hundred times for a ten-hour book — to answer a question the text
/// on the shelf beside it already settles. Naming the language removes that pass outright, and it
/// is also what decides whether the faster English-only model can be used at all.
///
/// Counting common words rather than looking at the alphabet, because the alphabet does not
/// separate the cases that matter here: Croatian and English share it almost entirely, and a book
/// whose diacritics were stripped in conversion would be misread by any test that leaned on them.
/// </summary>
public static class TextLanguage
{
    /// <summary>
    /// The handful of words that carry a language.
    ///
    /// Function words, never content words: they are the most frequent tokens in any prose, they
    /// are stable across genre and century, and they are what a translation cannot avoid. Ten each
    /// is enough to separate these six decisively over a page, let alone a book.
    /// </summary>
    private static readonly (string Code, string[] Words)[] Signatures =
    [
        ("en", ["the", "and", "of", "to", "was", "that", "with", "his", "her", "which"]),
        ("hr", ["je", "se", "na", "da", "su", "za", "koji", "bio", "kao", "ali"]),
        ("de", ["der", "die", "und", "den", "nicht", "sich", "war", "mit", "auf", "ein"]),
        ("fr", ["les", "des", "que", "pas", "pour", "dans", "une", "est", "qui", "avec"]),
        ("es", ["que", "los", "las", "por", "con", "una", "para", "del", "como", "pero"]),
        ("it", ["che", "non", "per", "con", "una", "sono", "come", "alla", "nel", "sua"]),
    ];

    /// <summary>How much of the sample to read. A few thousand words settle it; a novel is wasted effort.</summary>
    private const int SampleWords = 4_000;

    /// <summary>
    /// The language code, or null when nothing matched clearly enough to be worth asserting.
    ///
    /// Null is a real answer, not a failure: recognition falls back to detecting for itself, which
    /// is what it did before. Guessing wrong is worse than not guessing, because a named language
    /// is obeyed — tell it Croatian for an English book and every transcript comes back as nonsense.
    /// </summary>
    public static string? Detect(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var counts = new int[Signatures.Length];
        var lookups = Signatures
            .Select(s => new HashSet<string>(s.Words, StringComparer.Ordinal))
            .ToArray();

        var words = 0;

        foreach (var word in Words(text))
        {
            if (++words > SampleWords) break;

            for (var i = 0; i < lookups.Length; i++)
                if (lookups[i].Contains(word)) counts[i]++;
        }

        if (words < 50) return null;

        var best = 0;
        for (var i = 1; i < counts.Length; i++)
            if (counts[i] > counts[best]) best = i;

        // Two thresholds, because two different mistakes are possible. Too few hits altogether means
        // the sample is not prose in any of these languages. A winner that barely leads the runner-up
        // means the languages overlap here — "que" and "una" belong to both Spanish and Italian —
        // and a coin toss between them is worse than admitting ignorance.
        var runnerUp = counts.Where((_, i) => i != best).DefaultIfEmpty(0).Max();

        if (counts[best] < words / 50) return null;
        if (counts[best] < runnerUp * 3 / 2) return null;

        return Signatures[best].Code;
    }

    /// <summary>Lowercased words, without the punctuation that would keep them from matching.</summary>
    private static IEnumerable<string> Words(string text)
    {
        var start = -1;

        for (var i = 0; i <= text.Length; i++)
        {
            var isWord = i < text.Length && char.IsLetter(text[i]);

            if (isWord)
            {
                if (start < 0) start = i;
                continue;
            }

            if (start < 0) continue;

            yield return text[start..i].ToLowerInvariant();
            start = -1;
        }
    }
}
