using AudioBookReader.Core.Models;

namespace AudioBookReader.Core.Books;

/// <summary>
/// Answers the two questions the synced reader asks, in both directions: given where the audio is,
/// which sentence is being spoken, and given a sentence the user tapped, where in the audio it is.
///
/// The sync map stores raw character offsets per chapter; this puts them together with the book's
/// structure so callers never handle offsets or chapter lookup themselves.
/// </summary>
public class BookSync(BookText text, SyncMap map, IReadOnlyList<Chapter> chapters)
{
    public BookText Text => text;

    /// <summary>
    /// How far past the last anchor the narrator's position may be estimated rather than refused.
    ///
    /// Zero for a finished map, where beyond the last anchor genuinely means unmeasured. Set while
    /// sync on the fly is running, where it means the opposite: the last anchor is only seconds
    /// ahead of the voice by construction, and refusing everything past it makes the highlight
    /// stop and start with every window instead of moving with the narration.
    /// </summary>
    public long ExtrapolateAheadMs { get; init; }

    /// <summary>
    /// True when this chapter holds something actually heard, not merely visited.
    ///
    /// Every chapter map is seeded with two low-confidence guesses at the chapter boundaries, so
    /// "not empty" is true even when no probe matched a single word. Treating that as aligned told
    /// the reader to follow a straight line drawn between two guesses, and told the library the
    /// book was finished.
    /// </summary>
    public bool IsAligned(int chapterIndex) =>
        map.ForChapter(chapterIndex)?.HasMeasurement(ChapterSyncMap.BoundaryConfidence) == true;

    public Chapter? ChapterAt(long audioMs) =>
        chapters.FirstOrDefault(c => c.HasAudioRange && audioMs >= c.StartMs && audioMs < c.EndMs)
        ?? chapters.LastOrDefault(c => c.HasAudioRange);

    public Chapter? ChapterAtChar(int charOffset) =>
        chapters.FirstOrDefault(c => c.HasTextRange && charOffset >= c.TextStart && charOffset < c.TextEnd)
        ?? chapters.LastOrDefault(c => c.HasTextRange);

    /// <summary>Where in the book text the narrator is, or null if this chapter is not aligned yet.</summary>
    public int? CharOffsetAt(long audioMs)
    {
        if (ChapterAt(audioMs) is not { } chapter) return null;
        if (map.ForChapter(chapter.Index) is not { } chapterMap) return null;

        // Outside the anchored range the map clamps, and a clamped answer here freezes the
        // highlight on one sentence while reporting that all is well, so the reader never learns
        // that this part of the book has simply not been measured yet.
        if (!chapterMap.Covers(audioMs))
            return ExtrapolateAheadMs > 0
                   && chapterMap.TryExtrapolateCharOffset(audioMs, ExtrapolateAheadMs, out var ahead)
                ? ahead
                : null;

        return chapterMap.TryGetCharOffset(audioMs, out var offset) ? offset : null;
    }

    /// <summary>
    /// Where the narrator is, but only where that is actually measured.
    ///
    /// For callers that will act on the answer rather than draw it: moving playback on the strength
    /// of an interpolation across a twenty-minute gap moves it somewhere arbitrary, and then the
    /// next reading is taken from wherever that landed.
    /// </summary>
    public int? MeasuredCharOffsetAt(long audioMs, long windowMs)
    {
        if (ChapterAt(audioMs) is not { } chapter) return null;
        if (map.ForChapter(chapter.Index) is not { } chapterMap) return null;
        if (!chapterMap.IsMeasuredAt(audioMs, windowMs, ChapterSyncMap.BoundaryConfidence)) return null;

        return chapterMap.TryGetCharOffset(audioMs, out var offset) ? offset : null;
    }

    /// <summary>The sentence to highlight for the current playback position.</summary>
    public Sentence? SentenceAt(long audioMs) =>
        CharOffsetAt(audioMs) is { } offset ? text.SentenceAt(offset) : null;

    /// <summary>The document the reader should have open for the current playback position.</summary>
    public SpineDocument? DocumentAt(long audioMs) =>
        CharOffsetAt(audioMs) is { } offset ? text.SpineAt(offset) : null;

    /// <summary>
    /// Where to seek to hear a place in the text. This is what makes tap-to-seek work, and it is
    /// also the reader's escape hatch: if a highlight has drifted, tapping the right sentence puts
    /// the audio back where it belongs.
    /// </summary>
    public long? AudioPositionAtChar(int charOffset)
    {
        if (ChapterAtChar(charOffset) is not { } chapter) return null;
        if (map.ForChapter(chapter.Index) is not { } chapterMap) return null;

        // The same refusal <see cref="CharOffsetAt"/> makes, and for a worse symptom. Outside the
        // anchored range the map clamps and still reports success, so asking where chapter two is
        // read while only chapter one has been measured answered with the last anchor of chapter
        // one — and the caller dutifully sent playback back there. Jumping forward in the book
        // therefore left the narrator reading the chapter you had just left.
        if (!chapterMap.CoversChar(charOffset)) return null;

        return chapterMap.TryGetAudioMs(charOffset, out var at) ? at : null;
    }

    /// <summary>
    /// Where to seek for a place in the text, or for the first place shortly after it that the map
    /// does cover.
    ///
    /// A chapter's very first character is the likeliest one to fall outside its own anchors: the
    /// heading, the drop capital and the first few words are often what a probe skipped over, so
    /// the exact offset refuses and the caller falls back to guessing at a book that is fully
    /// mapped a sentence later. Walking forward a little turns that into an exact answer.
    /// </summary>
    public long? AudioPositionAtOrAfterChar(int charOffset, int withinChars)
    {
        if (AudioPositionAtChar(charOffset) is { } here) return here;

        var sentence = text.SentenceAt(charOffset);
        if (sentence is null) return null;

        for (var i = sentence.Value.Index + 1; i < text.Sentences.Count; i++)
        {
            var next = text.Sentences[i];
            if (next.Start - charOffset > withinChars) break;

            if (AudioPositionAtChar(next.Start) is { } at) return at;
        }

        return null;
    }

    public long? AudioPositionAtSentence(int sentenceIndex) =>
        sentenceIndex >= 0 && sentenceIndex < text.Sentences.Count
            ? AudioPositionAtChar(text.Sentences[sentenceIndex].Start)
            : null;
}
