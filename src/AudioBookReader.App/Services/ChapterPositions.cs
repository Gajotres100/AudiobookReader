namespace AudioBookReader.App.Services;

/// <summary>
/// Where each chapter was left, so going back to one resumes it instead of restarting it.
///
/// Written when a chapter is left for another one, and consumed when it is entered again. Consuming
/// is what makes "finished" work without anything having to notice a chapter ending: a chapter
/// resumed and then listened or read through to its end leaves no entry behind, so the next visit
/// starts it from the top — the same as a chapter left within sight of its end, which is written
/// as finished rather than as a place.
///
/// Kept in Preferences like the reader's other per-book places: it is small, per device, and losing
/// it costs a chapter start rather than anything that matters.
/// </summary>
public static class ChapterPositions
{
    /// <summary>The narration, in milliseconds, by the library's chapter index.</summary>
    public const string Audio = "audio";

    /// <summary>The text, as a character offset, by the ebook's own chapter index.</summary>
    public const string Text = "text";

    /// <summary>Notes where a chapter is being left, or forgets it when that is at its end.</summary>
    /// <param name="finishedWithin">How close to the end counts as having finished the chapter.</param>
    public static void Leave(string kind, int bookId, int chapterIndex, long position, long start, long end, long finishedWithin)
    {
        var key = Key(kind, bookId, chapterIndex);

        if (position < start || position > end || end - position <= finishedWithin)
            Preferences.Default.Remove(key);
        else
            Preferences.Default.Set(key, position);
    }

    /// <summary>Where to resume a chapter being entered, or null for its start. Forgets it either way.</summary>
    public static long? Enter(string kind, int bookId, int chapterIndex)
    {
        var key = Key(kind, bookId, chapterIndex);
        var stored = Preferences.Default.Get(key, -1L);

        Preferences.Default.Remove(key);
        return stored >= 0 ? stored : null;
    }

    private static string Key(string kind, int bookId, int chapterIndex) =>
        $"chapter.{kind}.{bookId}.{chapterIndex}";
}
