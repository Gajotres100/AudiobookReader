namespace AudioBookReader.Core.Alignment;

/// <summary>One piece of a recognized segment, as the model emitted it.</summary>
/// <param name="Text">The token's own text, which may be a fragment of a word.</param>
/// <param name="AtMs">When it was spoken, in book time.</param>
public readonly record struct SpokenToken(string Text, long AtMs);

/// <summary>
/// Joins the recognizer's tokens back into words, each carrying the moment it was spoken.
///
/// This exists because a token is not a word. Whisper emits subword pieces — "nar", "ator" — and
/// marks a new word by a leading space rather than by a boundary of its own. Sticking them back
/// together is the only way to get from tokens to something that can be matched against a book.
///
/// Worth the trouble because the alternative is a guess. Without token times, a segment's words
/// are placed by spreading the segment evenly across them, which assumes the narrator speaks at a
/// constant rate for the three to eight seconds a segment covers. Narrators pause mid-sentence,
/// and every anchor taken from the middle of a segment inherits that error — regardless of how
/// many anchors there are, which is why measuring more often never moved it.
/// </summary>
public static class TokenWords
{
    /// <summary>
    /// Merges tokens into normalized words.
    ///
    /// A word's time is the time of its <em>first</em> token: that is the moment it began, and an
    /// anchor marks where the narrator was, not where they had got to by the end of the word.
    /// </summary>
    public static List<TimedWord> Merge(IEnumerable<SpokenToken> tokens)
    {
        var words = new List<TimedWord>();

        // The word being assembled, and when its first token was spoken.
        var pending = new System.Text.StringBuilder();
        var pendingAt = 0L;

        void Flush()
        {
            if (pending.Length == 0) return;

            // Normalized here rather than by the caller, so a word built from three tokens goes
            // through exactly the same treatment as one the book's own tokenizer produced —
            // lowercased, stripped of diacritics and punctuation. Anything that normalizes to
            // nothing was punctuation on its own and is not a word.
            var normalized = TextNormalizer.NormalizeWord(pending.ToString());
            if (normalized.Length > 0) words.Add(new TimedWord(normalized, pendingAt));

            pending.Clear();
        }

        foreach (var token in tokens)
        {
            if (IsSpecial(token.Text)) continue;

            // A token can carry more than one word when the model emits a whole short phrase.
            // Splitting on whitespace and keeping the token's own time for each is the honest
            // answer: they were all spoken at about that moment, and the alternative is dropping
            // the extras.
            var first = true;

            foreach (var range in Split(token.Text))
            {
                var piece = token.Text[range];

                // A leading space is how the model says "new word". Without one, this token
                // continues whatever came before — which is the whole point of merging.
                if (!first || StartsWord(token.Text, range)) Flush();

                if (pending.Length == 0) pendingAt = token.AtMs;

                pending.Append(piece);
                first = false;
            }
        }

        Flush();
        return words;
    }

    /// <summary>
    /// Control tokens rather than speech: timestamps, the beginning-of-text marker, language tags.
    /// Whisper emits them inline and they are not words, however much they look like text.
    /// </summary>
    private static bool IsSpecial(string? text) =>
        string.IsNullOrWhiteSpace(text)
        || (text.StartsWith("[_", StringComparison.Ordinal) && text.EndsWith("]", StringComparison.Ordinal))
        || (text.StartsWith("<|", StringComparison.Ordinal) && text.EndsWith("|>", StringComparison.Ordinal));

    /// <summary>Whether the piece at this range opens a new word, i.e. whitespace precedes it.</summary>
    private static bool StartsWord(string text, Range range)
    {
        var (start, _) = range.GetOffsetAndLength(text.Length);
        return start > 0 && char.IsWhiteSpace(text[start - 1]);
    }

    /// <summary>The non-whitespace runs of a token, as ranges into it.</summary>
    private static List<Range> Split(string text)
    {
        var ranges = new List<Range>();

        for (var i = 0; i < text.Length;)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
                continue;
            }

            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;

            ranges.Add(start..i);
        }

        return ranges;
    }
}
