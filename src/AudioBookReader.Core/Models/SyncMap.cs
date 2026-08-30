namespace AudioBookReader.Core.Models;

/// <summary>
/// A single point where a position in the audio is known to correspond to a position in the
/// book text. Everything between anchors is linearly interpolated.
/// </summary>
/// <param name="AudioMs">Absolute position in the audio file.</param>
/// <param name="CharOffset">Offset into the book's normalized plain text.</param>
/// <param name="Confidence">Fuzzy match score that produced this anchor, 0..1.</param>
public readonly record struct Anchor(long AudioMs, int CharOffset, float Confidence);

/// <summary>
/// Anchors for one chapter, kept strictly increasing in both dimensions so lookups are
/// unambiguous in either direction.
/// </summary>
public class ChapterSyncMap
{
    public int ChapterIndex { get; set; }

    /// <summary>Strictly increasing in both <see cref="Anchor.AudioMs"/> and <see cref="Anchor.CharOffset"/>.</summary>
    public List<Anchor> Anchors { get; set; } = [];

    public bool IsEmpty => Anchors.Count == 0;

    /// <summary>
    /// True when at least one anchor came from an actual match rather than from a chapter boundary.
    ///
    /// The distinction matters everywhere: a chapter holding only its two boundary guesses looks
    /// aligned and behaves as if it is not — the text will not follow there, and a run that skips
    /// it as "done" leaves a hole that resuming can never fill.
    /// </summary>
    public bool HasMeasurement(float boundaryConfidence) =>
        Anchors.Any(a => a.Confidence > boundaryConfidence);

    /// <summary>
    /// Builds a chapter map from raw anchors, discarding those that contradict the rest.
    ///
    /// A bad fuzzy match can place an anchor far from where it belongs, and a single such outlier
    /// would otherwise distort every interpolation around it. So rather than trusting insertion
    /// order, this keeps the largest mutually consistent (strictly increasing in both dimensions)
    /// subset, weighted by confidence — a weighted longest-increasing-subsequence. Anchor counts
    /// per chapter are small (tens), so the quadratic solution is the right trade.
    /// </summary>
    public static ChapterSyncMap FromAnchors(int chapterIndex, IEnumerable<Anchor> anchors)
    {
        var sorted = anchors.OrderBy(a => a.AudioMs).ThenBy(a => a.CharOffset).ToList();
        return new ChapterSyncMap { ChapterIndex = chapterIndex, Anchors = LongestConsistentRun(sorted) };
    }

    private static List<Anchor> LongestConsistentRun(List<Anchor> sorted)
    {
        if (sorted.Count == 0) return [];

        // best[i] = total confidence of the best consistent run ending at i; prev[i] = its predecessor.
        var best = new float[sorted.Count];
        var prev = new int[sorted.Count];

        var bestEnd = 0;
        for (var i = 0; i < sorted.Count; i++)
        {
            best[i] = sorted[i].Confidence;
            prev[i] = -1;

            for (var j = 0; j < i; j++)
            {
                if (sorted[j].AudioMs >= sorted[i].AudioMs) continue;
                if (sorted[j].CharOffset >= sorted[i].CharOffset) continue;

                var candidate = best[j] + sorted[i].Confidence;
                if (candidate > best[i])
                {
                    best[i] = candidate;
                    prev[i] = j;
                }
            }

            if (best[i] > best[bestEnd]) bestEnd = i;
        }

        var run = new List<Anchor>();
        for (var i = bestEnd; i >= 0; i = prev[i]) run.Add(sorted[i]);
        run.Reverse();
        return run;
    }

    /// <summary>Maps a text offset to a position in the audio. Clamps outside the anchored range.</summary>
    public bool TryGetAudioMs(int charOffset, out long audioMs)
    {
        audioMs = 0;
        if (Anchors.Count == 0) return false;

        if (Anchors.Count == 1 || charOffset <= Anchors[0].CharOffset)
        {
            audioMs = Anchors[0].AudioMs;
            return true;
        }

        var last = Anchors[^1];
        if (charOffset >= last.CharOffset)
        {
            audioMs = last.AudioMs;
            return true;
        }

        var hi = UpperBound(a => a.CharOffset > charOffset);
        var lo = Anchors[hi - 1];
        var up = Anchors[hi];

        var t = (double)(charOffset - lo.CharOffset) / (up.CharOffset - lo.CharOffset);
        audioMs = lo.AudioMs + (long)Math.Round(t * (up.AudioMs - lo.AudioMs));
        return true;
    }

    /// <summary>Maps a position in the audio to a text offset. Clamps outside the anchored range.</summary>
    public bool TryGetCharOffset(long audioMs, out int charOffset)
    {
        charOffset = 0;
        if (Anchors.Count == 0) return false;

        if (Anchors.Count == 1 || audioMs <= Anchors[0].AudioMs)
        {
            charOffset = Anchors[0].CharOffset;
            return true;
        }

        var last = Anchors[^1];
        if (audioMs >= last.AudioMs)
        {
            charOffset = last.CharOffset;
            return true;
        }

        var hi = UpperBound(a => a.AudioMs > audioMs);
        var lo = Anchors[hi - 1];
        var up = Anchors[hi];

        var t = (double)(audioMs - lo.AudioMs) / (up.AudioMs - lo.AudioMs);
        charOffset = lo.CharOffset + (int)Math.Round(t * (up.CharOffset - lo.CharOffset));
        return true;
    }

    /// <summary>Index of the first anchor satisfying <paramref name="isAbove"/>. Callers guarantee one exists.</summary>
    private int UpperBound(Func<Anchor, bool> isAbove)
    {
        var lo = 0;
        var hi = Anchors.Count - 1;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (isAbove(Anchors[mid])) hi = mid;
            else lo = mid + 1;
        }
        return lo;
    }
}

/// <summary>
/// The full audio/text correspondence for one book. Stored as JSON next to the library database
/// rather than in it: it is written whole, read whole, and large enough that row-level access
/// buys nothing.
/// </summary>
public class SyncMap
{
    /// <summary>
    /// Bumped when anchors from an older run can no longer be trusted, as opposed to merely being
    /// sparse.
    ///
    /// Version 2 times every anchor by when the narrator spoke the words rather than by when the
    /// probe that caught them began. A version 1 map has the pause before each probe's first word
    /// baked into it as a one-directional error, and no amount of resuming corrects an anchor a
    /// previous run already placed — so those maps are retired rather than extended.
    /// </summary>
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>True when this map was built by an older, less accurate method.</summary>
    public bool IsStale => Version != CurrentVersion;

    /// <summary>Hashes of the pair this map was computed from. A mismatch invalidates the cache.</summary>
    public string? AudioHash { get; set; }

    public string? EbookHash { get; set; }

    public List<ChapterSyncMap> Chapters { get; set; } = [];

    public ChapterSyncMap? ForChapter(int chapterIndex) =>
        Chapters.FirstOrDefault(c => c.ChapterIndex == chapterIndex);

    /// <summary>Replaces the map for one chapter, keeping <see cref="Chapters"/> ordered by index.</summary>
    public void SetChapter(ChapterSyncMap map)
    {
        Chapters.RemoveAll(c => c.ChapterIndex == map.ChapterIndex);
        var at = Chapters.FindIndex(c => c.ChapterIndex > map.ChapterIndex);
        if (at < 0) Chapters.Add(map);
        else Chapters.Insert(at, map);
    }

    /// <summary>How many chapters are genuinely aligned — the number worth showing a user.</summary>
    public int MeasuredChapterCount(float boundaryConfidence) =>
        Chapters.Count(c => c.HasMeasurement(boundaryConfidence));

    /// <summary>
    /// The first chapter a run should work on: the first without a real measurement, whether it
    /// was never attempted or attempted and found nothing.
    /// </summary>
    public int FirstChapterNeedingWork(int chapterCount, float boundaryConfidence)
    {
        for (var i = 0; i < chapterCount; i++)
            if (ForChapter(i) is not { } chapter || !chapter.HasMeasurement(boundaryConfidence))
                return i;

        return chapterCount;
    }

    public bool MatchesPair(string? audioHash, string? ebookHash) =>
        Version == CurrentVersion && AudioHash == audioHash && EbookHash == ebookHash;
}
