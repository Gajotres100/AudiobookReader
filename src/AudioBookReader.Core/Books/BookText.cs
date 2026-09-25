namespace AudioBookReader.Core.Books;

/// <summary>
/// One sentence of the book. Sentences are the unit the reader highlights and the unit alignment
/// refines to: words are jittery to follow and cost far more to align, paragraphs are too coarse
/// to feel synchronized.
/// </summary>
/// <param name="Index">Position in <see cref="BookText.Sentences"/>, and the value of the
/// <c>data-idx</c> attribute in the rendered HTML.</param>
/// <param name="Start">Inclusive offset into <see cref="BookText.PlainText"/>.</param>
/// <param name="End">Exclusive offset into <see cref="BookText.PlainText"/>.</param>
/// <param name="SpineIndex">Which document of the book this sentence belongs to.</param>
public readonly record struct Sentence(int Index, int Start, int End, int SpineIndex)
{
    public int Length => End - Start;
}

/// <summary>
/// One document of the book (an EPUB spine item, or the whole file for plain text), carrying the
/// HTML the reader renders.
/// </summary>
public record SpineDocument
{
    public required int Index { get; init; }
    public required string Href { get; init; }
    public string? Title { get; init; }

    /// <summary>Range of <see cref="BookText.PlainText"/> this document covers.</summary>
    public required int TextStart { get; init; }

    public required int TextEnd { get; init; }

    /// <summary>
    /// Body HTML with every sentence wrapped in <c>&lt;span data-idx="N"&gt;</c>. A sentence that
    /// spans inline markup produces several spans sharing one index, so highlighting selects all
    /// of them rather than assuming one element per sentence.
    /// </summary>
    public required string Html { get; init; }

    /// <summary>
    /// Where each element carrying an <c>id</c> begins in <see cref="BookText.PlainText"/>. What an
    /// EPUB 3 Media Overlay points at is exactly such an id, so this is how a ready-made overlay
    /// becomes anchors. Empty for formats that have none.
    /// </summary>
    public IReadOnlyDictionary<string, int> Anchors { get; init; } = new Dictionary<string, int>();
}

/// <summary>
/// A book reduced to one continuous plain-text stream plus the structure needed to get back to
/// the rendered page.
///
/// The single stream is what makes alignment tractable: anchors are plain integer offsets into
/// it, independent of how the book was chopped into files. <see cref="Spine"/> and
/// <see cref="Sentences"/> map any offset back to something displayable.
/// </summary>
public class BookText
{
    public required string PlainText { get; init; }
    public required IReadOnlyList<Sentence> Sentences { get; init; }
    public required IReadOnlyList<SpineDocument> Spine { get; init; }

    public string? Title { get; init; }
    public string? Author { get; init; }

    public int Length => PlainText.Length;

    /// <summary>
    /// The sentence containing <paramref name="charOffset"/>, or the nearest one when the offset
    /// falls in the gap between sentences. Returns null only for an empty book.
    /// </summary>
    public Sentence? SentenceAt(int charOffset)
    {
        if (Sentences.Count == 0) return null;

        var lo = 0;
        var hi = Sentences.Count - 1;
        while (lo < hi)
        {
            var mid = lo + (hi - lo + 1) / 2;
            if (Sentences[mid].Start <= charOffset) lo = mid;
            else hi = mid - 1;
        }

        return Sentences[lo];
    }

    public SpineDocument? SpineAt(int charOffset) =>
        Spine.FirstOrDefault(d => charOffset < d.TextEnd) ?? Spine.LastOrDefault();

    /// <summary>Text around an offset, for bookmark previews and alignment diagnostics.</summary>
    public string Excerpt(int charOffset, int length = 80)
    {
        if (PlainText.Length == 0) return "";

        var start = Math.Clamp(charOffset, 0, Math.Max(0, PlainText.Length - 1));
        var end = Math.Min(PlainText.Length, start + length);
        return PlainText[start..end].Replace('\n', ' ').Trim();
    }
}
