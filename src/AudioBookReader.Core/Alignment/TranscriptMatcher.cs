namespace AudioBookReader.Core.Alignment;

/// <summary>
/// Where a transcript was found in the book text, and how much to trust it.
///
/// Both ends are reported. A ten-second probe covers a measurable stretch of the book, and knowing
/// where the match stopped as well as where it started turns one probe into two anchors — one at
/// each end — for no additional recognition at all. Half the interpolated gap disappears with it.
/// </summary>
/// <param name="TokenIndex">Index in <see cref="TokenizedText.Tokens"/> where the transcript begins.</param>
/// <param name="CharOffset">Character offset of that token in the book's plain text.</param>
/// <param name="EndCharOffset">Exclusive character offset just past the last matched word.</param>
/// <param name="TranscriptStart">Index of the first transcript word that took part in the match.</param>
/// <param name="TranscriptEnd">Index of the last transcript word that took part in the match.</param>
/// <param name="Confidence">0..1; the share of the transcript that lined up with the book.</param>
public readonly record struct TranscriptMatch(
    int TokenIndex,
    int CharOffset,
    int EndCharOffset,
    int TranscriptStart,
    int TranscriptEnd,
    float Confidence);

/// <summary>
/// Locates a short speech transcript within the book text.
///
/// This is the step that turns "the recognizer heard these words" into "the narrator is here",
/// and it must tolerate the ways recognition goes wrong: substituted words, dropped words, and
/// invented ones. That rules out exact search and makes this a local sequence alignment
/// (Smith-Waterman over words), where mismatches and gaps cost score instead of ending the match.
/// </summary>
public static class TranscriptMatcher
{
    private const int MatchScore = 2;
    private const int MismatchPenalty = -1;
    private const int GapPenalty = -1;

    /// <summary>
    /// Searches a token window for the transcript.
    ///
    /// The window is bounded by the caller because narration advances at a near-constant rate: the
    /// expected position is already known to within seconds, so searching the whole book would be
    /// thousands of times more work and would additionally invite a confident match on a repeated
    /// phrase somewhere else entirely.
    /// </summary>
    /// <returns>The best match, or null if nothing reached <paramref name="minConfidence"/>.</returns>
    public static TranscriptMatch? Match(
        TokenizedText book,
        IReadOnlyList<string> transcript,
        int windowStartToken,
        int windowEndToken,
        float minConfidence = 0.45f)
    {
        var start = Math.Clamp(windowStartToken, 0, book.Count);
        var end = Math.Clamp(windowEndToken, start, book.Count);

        var width = end - start;
        var length = transcript.Count;
        if (width == 0 || length == 0) return null;

        // Two rolling rows: the full matrix is never needed because each cell carries the position
        // its local alignment started from on both sides, which is the only thing traceback would
        // tell us. The cell the best score lands in gives the other end of the match directly.
        var previous = new Cell[width + 1];
        var current = new Cell[width + 1];

        var bestScore = 0;
        var best = default(Cell);
        var bestBookEnd = -1;
        var bestTranscriptEnd = -1;

        for (var j = 1; j <= length; j++)
        {
            var transcriptWord = transcript[j - 1];
            current[0] = default;

            for (var i = 1; i <= width; i++)
            {
                var isMatch = book.Tokens[start + i - 1].Value == transcriptWord;

                var diagonal = Step(previous[i - 1], isMatch ? MatchScore : MismatchPenalty, i - 1, j - 1);
                var fromAbove = Step(previous[i], GapPenalty, i, j - 1);
                var fromLeft = Step(current[i - 1], GapPenalty, i - 1, j - 1);

                var cell = diagonal;
                if (fromAbove.Score > cell.Score) cell = fromAbove;
                if (fromLeft.Score > cell.Score) cell = fromLeft;

                // A local alignment never carries a negative score forward; it restarts instead.
                if (cell.Score <= 0) cell = new Cell(0, i - 1, j - 1);

                current[i] = cell;

                if (cell.Score > bestScore)
                {
                    bestScore = cell.Score;
                    best = cell;
                    bestBookEnd = i;
                    bestTranscriptEnd = j - 1;
                }
            }

            (previous, current) = (current, previous);
        }

        if (bestBookEnd < 0) return null;

        var confidence = Math.Clamp(bestScore / (float)(length * MatchScore), 0f, 1f);
        if (confidence < minConfidence) return null;

        var tokenIndex = start + best.BookOrigin;
        var lastToken = Math.Clamp(start + bestBookEnd - 1, tokenIndex, book.Count - 1);

        return new TranscriptMatch(
            tokenIndex,
            book.Tokens[tokenIndex].CharStart,
            book.Tokens[lastToken].CharEnd,
            Math.Clamp(best.TranscriptOrigin, 0, length - 1),
            Math.Clamp(bestTranscriptEnd, 0, length - 1),
            confidence);
    }

    /// <summary>A score together with where, on both sides, the alignment that produced it began.</summary>
    private readonly record struct Cell(int Score, int BookOrigin, int TranscriptOrigin);

    /// <summary>
    /// Extends a predecessor cell by one move, keeping the positions the alignment started at.
    /// A predecessor that scored nothing is not worth continuing, so the move begins a new
    /// alignment at the given origins.
    /// </summary>
    private static Cell Step(Cell predecessor, int delta, int freshBook, int freshTranscript) =>
        predecessor.Score > 0
            ? predecessor with { Score = predecessor.Score + delta }
            : new Cell(delta, freshBook, freshTranscript);

    /// <summary>
    /// Searches around an expected position, which is how alignment actually calls this: the
    /// previous anchors and a constant narration rate predict where the probe should land, and
    /// the radius is the margin allowed for that prediction being off.
    /// </summary>
    public static TranscriptMatch? MatchNear(
        TokenizedText book,
        IReadOnlyList<string> transcript,
        int expectedTokenIndex,
        int radiusTokens,
        float minConfidence = 0.45f) =>
        Match(
            book,
            transcript,
            expectedTokenIndex - radiusTokens,
            expectedTokenIndex + radiusTokens + transcript.Count,
            minConfidence);
}
