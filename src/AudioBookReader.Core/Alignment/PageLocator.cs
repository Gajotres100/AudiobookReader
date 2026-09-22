namespace AudioBookReader.Core.Alignment;

/// <summary>Where a photographed page was found in the book, in characters.</summary>
public readonly record struct PageLocation(int CharOffset, int EndCharOffset, float Confidence);

/// <summary>
/// Finds a photographed page of the book somewhere in its own text -- the same phrase matching
/// alignment already uses to place a spoken probe, run here against OCR'd text instead of a
/// transcript, and with nothing already known about where the page might be. A page carries far
/// more words than a ten-second probe does, so the search runs the whole book rather than a
/// predicted window: there is no playhead here to predict a position from.
/// </summary>
public static class PageLocator
{
    /// <summary>
    /// A page's worth of recognized text carries enough signal to be held to a stricter bar than
    /// a speech probe -- a probe has to settle for whatever a few seconds of narration gave it,
    /// but a mismatched photo is exactly the case where the search should say no rather than land
    /// on a coincidental scrap of dialogue somewhere else in the book.
    /// </summary>
    public const float DefaultMinConfidence = 0.55f;

    /// <summary>
    /// Below this many recognized words, no confidence is trustworthy: a short phrase can score
    /// well by chance against a book long enough to contain it somewhere by accident, and a
    /// blurry or badly lit photo is exactly what yields too little text to search on at all.
    /// </summary>
    public const int MinimumWords = 12;

    /// <summary>
    /// How long the run that actually lines up has to be. This, rather than the share of the photo
    /// that lined up, is what keeps a coincidence out: a camera sees whatever is in front of it,
    /// but no stretch of a book turns up twenty consecutive words of another page by accident.
    /// </summary>
    public const int MinimumMatchedWords = 20;

    /// <param name="book">The book being searched, already tokenized once by the caller.</param>
    /// <param name="recognizedText">Whatever the OCR pass read off the photo, raw and unnormalized.</param>
    /// <returns>Null when there was too little text to trust, or nothing matched well enough.</returns>
    public static PageLocation? Locate(
        TokenizedText book, string recognizedText, float minConfidence = DefaultMinConfidence)
    {
        var words = TextNormalizer.NormalizeTranscript(recognizedText);
        if (words.Count < MinimumWords) return null;

        // Scored on the run that lined up, not on everything the camera happened to see.
        //
        // The matcher reports the share of the whole transcript it placed, which is the right
        // measure for a speech probe: a probe is ten seconds of nothing but narration, so anything
        // it fails to place is a failure. A photograph is not like that. It catches the facing
        // page, the reader's own chrome, a thumb, the edge of the desk — none of which is in the
        // book and all of which counts against the share. Measured on a real one: a page whose
        // opening matched the book at 0.95 scored under the bar for the whole photo and was
        // rejected, while cleaner shots of the same book scraped through at 0.70. Taking the
        // matched span as the denominator asks the question that actually matters — does a solid
        // run of this page appear in this book — and leaves the surrounding junk out of it.
        if (TranscriptMatcher.Match(book, words, 0, book.Count, minConfidence: 0f) is not { } m)
            return null;

        var matched = m.TranscriptEnd - m.TranscriptStart + 1;
        if (matched < MinimumMatchedWords) return null;

        var confidence = Math.Clamp(m.Confidence * words.Count / matched, 0f, 1f);
        if (confidence < minConfidence) return null;

        return new PageLocation(m.CharOffset, m.EndCharOffset, confidence);
    }
}
