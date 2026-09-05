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
        if (!chapterMap.Covers(audioMs)) return null;

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

        return chapterMap.TryGetAudioMs(charOffset, out var at) ? at : null;
    }

    public long? AudioPositionAtSentence(int sentenceIndex) =>
        sentenceIndex >= 0 && sentenceIndex < text.Sentences.Count
            ? AudioPositionAtChar(text.Sentences[sentenceIndex].Start)
            : null;
}
