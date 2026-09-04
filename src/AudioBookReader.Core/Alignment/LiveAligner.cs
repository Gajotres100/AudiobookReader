using AudioBookReader.Core.Models;

namespace AudioBookReader.Core.Alignment;

/// <summary>
/// Aligns the passage being listened to, while it is being listened to.
///
/// The whole-book aligner samples: ten seconds a minute, and everything between is interpolated.
/// That is the right trade for a book you have not opened yet — sixteen percent of the audio buys a
/// map of the whole thing — but interpolation is also the entire remaining error, and no amount of
/// extra sampling removes it. Measurement showed why: tripling the sample rate moved the median by
/// two tenths of a second and the tail not at all.
///
/// So this does the opposite trade. It transcribes <em>continuously</em>, in touching windows,
/// starting from wherever the listener is and working forward — there is nothing to interpolate
/// across, because every stretch is measured. It costs about half of real time on the balanced
/// preset, which is affordable precisely because it only ever covers the few minutes around the
/// playhead rather than ten hours.
/// </summary>
public class LiveAligner(
    TokenizedText book,
    IReadOnlyList<Chapter> chapters,
    ITranscriber transcriber,
    int textLength,
    AlignmentSettings? settings = null,
    IWorkThrottle? throttle = null,
    Action<string>? log = null)
{
    private readonly AlignmentSettings _settings = settings ?? AlignmentSettings.Refinement;
    private readonly IWorkThrottle _throttle = throttle ?? NoThrottle.Instance;

    /// <summary>
    /// How far ahead of the listener to work before resting.
    ///
    /// Some lead is essential — recognition has to finish before the narrator arrives — but a large
    /// one is just the whole-book run under another name, spending battery on text the listener may
    /// never reach because they stopped for the night.
    /// </summary>
    public long MaxLeadMs { get; init; } = 120_000;

    /// <summary>How often the map is handed back to be written, in windows.</summary>
    private const int SaveEvery = 4;

    /// <summary>Anything shorter than this is not worth a recognition pass of its own.</summary>
    private const long ShortestWindowMs = 2_000;

    /// <summary>Rest taken when there is nothing to do — far enough ahead, or nothing playing.</summary>
    public TimeSpan IdleRest { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Follows the listener until cancelled.
    /// </summary>
    /// <param name="positionMs">Where playback is now, read fresh on every window.</param>
    /// <param name="saveAsync">Persists the map; called every few windows rather than every one.</param>
    public async Task RunAsync(
        string audioPath,
        SyncMap map,
        Func<long> positionMs,
        Func<Task> saveAsync,
        IProgress<LiveAlignmentProgress>? progress = null,
        CancellationToken ct = default)
    {
        // Where the current forward run began, and how far it has reached. The pair is what tells a
        // listener drifting along behind the work from one who has jumped somewhere else: inside
        // [runStart, next] the audio has been covered by this run, and outside it has not.
        var runStart = long.MinValue;
        var next = long.MinValue;

        var radius = _settings.SearchRadiusTokens;
        var sinceSave = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var head = positionMs();

            if (head < runStart || head > next)
            {
                // Seeked somewhere this run is not working towards, so begin again from there.
                runStart = next = head;
                radius = _settings.SearchRadiusTokens;
            }
            else if (next > head + MaxLeadMs)
            {
                await Task.Delay(IdleRest, ct);
                continue;
            }

            if (ChapterAt(next) is not { } chapter)
            {
                await Task.Delay(IdleRest, ct);
                continue;
            }

            var duration = Math.Min(_settings.ProbeDurationMs, chapter.EndMs!.Value - next);

            // The tail end of a chapter, too short to be worth its own pass.
            if (duration < ShortestWindowMs)
            {
                next = chapter.EndMs.Value;
                continue;
            }

            // Re-listening to something already measured would spend the budget learning what is
            // already known — and re-listening is exactly what happens every time someone goes back
            // a page.
            if (IsAlreadyCovered(map, chapter.Index, next, next + duration))
            {
                next += duration;
                continue;
            }

            var predicted = Predict(map, chapter, next);
            var transcript = await transcriber.TranscribeAsync(audioPath, next, duration, ct);

            if (transcript.Words.Count > 0)
            {
                // Phrase by phrase, for the same reason the whole-book run does it: a window
                // located only at its two ends is a straight line drawn across everything between.
                var located = Record(map, chapter.Index, transcript, predicted, radius);

                if (located > 0)
                {
                    radius = _settings.SearchRadiusTokens;
                    progress?.Report(new LiveAlignmentProgress(chapter.Index, next, 1f));
                }
                else
                {
                    log?.Invoke(
                        $"live ch{chapter.Index} @{next}ms: {transcript.Words.Count} words, no match within " +
                        $"{radius} of char {predicted}");

                    radius = Math.Min(radius * 3, _settings.MaximumSearchRadiusTokens);
                }
            }

            next += duration;

            if (++sinceSave >= SaveEvery)
            {
                sinceSave = 0;
                await saveAsync();
            }

            await _throttle.ThrottleAsync(ct);
        }
    }

    /// <summary>Locates each timed phrase and folds the anchors it yields into the chapter.</summary>
    /// <returns>How many phrases were placed.</returns>
    private int Record(SyncMap map, int chapterIndex, Transcript transcript, int predicted, int radius)
    {
        var anchors = map.ForChapter(chapterIndex)?.Anchors.ToList() ?? [];
        var located = 0;

        var expected = predicted;
        Anchor? last = null;

        foreach (var phrase in transcript.Phrases)
        {
            // Wide only for the first phrase; after that the position is known to within a line.
            var found = TranscriptMatcher.MatchNear(
                book,
                [.. phrase.Words.Select(w => w.Value)],
                book.TokenIndexAtChar(expected),
                located > 0 ? _settings.PhraseRadiusTokens : radius,
                _settings.MinConfidence);

            if (found is null) continue;

            var opening = new Anchor(
                phrase.Words[found.Value.TranscriptStart].AtMs, found.Value.CharOffset, found.Value.Confidence);

            var closing = new Anchor(
                phrase.Words[found.Value.TranscriptEnd].AtMs, found.Value.EndCharOffset, found.Value.Confidence);

            anchors.Add(opening);

            if (closing.AudioMs > opening.AudioMs && closing.CharOffset > opening.CharOffset)
                anchors.Add(closing);

            last = anchors[^1];
            expected = last.Value.CharOffset;
            located++;
        }

        if (located > 0) map.SetChapter(ChapterSyncMap.FromAnchors(chapterIndex, anchors));

        return located;
    }

    /// <summary>
    /// Whether measurements already bracket this window on both sides, leaving nothing inside it
    /// to interpolate.
    ///
    /// Bracketing is the test, not proximity: an anchor merely near the window's start is what a
    /// window that has just been done leaves behind, and treating that as coverage would skip every
    /// second window and leave exactly the gaps this exists to close.
    ///
    /// Boundary anchors do not count. They span a whole chapter, so a book the coarse run had
    /// merely visited would look entirely covered and this would do nothing at all.
    /// </summary>
    private bool IsAlreadyCovered(SyncMap map, int chapterIndex, long fromMs, long toMs)
    {
        if (map.ForChapter(chapterIndex) is not { } existing) return false;

        var measured = existing.Anchors
            .Where(a => a.Confidence > _settings.BoundaryConfidence)
            .Select(a => a.AudioMs)
            .ToList();

        if (measured.Count == 0) return false;

        var window = _settings.ProbeDurationMs;

        // Anchors that sit within a window of each other were produced by touching windows, so the
        // audio between them was heard rather than interpolated. Anything further apart is a gap,
        // and a gap is precisely what this is here to close — which is why sparse anchors left by
        // the whole-book run never count as coverage.
        var spanStart = measured[0];
        var spanEnd = measured[0];

        foreach (var at in measured.Skip(1))
        {
            if (at - spanEnd <= window)
            {
                spanEnd = at;
                continue;
            }

            if (Covers(spanStart, spanEnd)) return true;

            spanStart = spanEnd = at;
        }

        return Covers(spanStart, spanEnd);

        // A span reaches back a window before its first anchor: that anchor was found inside a
        // window that began earlier, so the audio in between was listened to as well. Without this
        // the opening of a chapter could never count as covered, since nothing precedes it.
        bool Covers(long start, long end) => fromMs >= start - window && toMs <= end;
    }

    private Chapter? ChapterAt(long atMs) =>
        chapters.LastOrDefault(c => c.HasAudioRange && atMs >= c.StartMs && atMs < c.EndMs);

    /// <summary>
    /// Where in the text this moment is expected to be, from the best evidence available: the map
    /// where it reaches, the last thing measured in this chapter otherwise, and a proportional
    /// guess when the chapter is untouched. A poor guess costs a widened search, not a failure.
    /// </summary>
    private int Predict(SyncMap map, Chapter chapter, long atMs)
    {
        var rate = RateOf(chapter);

        if (map.ForChapter(chapter.Index) is { Anchors.Count: > 1 } existing)
        {
            // Only inside the anchored range, and checked rather than trusted: the map clamps
            // outside it and still reports success, so asking it about a moment it knows nothing
            // about answers with the last thing it does know — which after a jump is a prediction
            // twenty minutes stale, far enough out that every probe then misses.
            var first = existing.Anchors[0];
            var last = existing.Anchors[^1];

            if (atMs >= first.AudioMs && atMs <= last.AudioMs && existing.TryGetCharOffset(atMs, out var mapped))
                return mapped;

            // Carrying a nearby anchor forward is sound; carrying a distant one is not.
            if (Math.Abs(atMs - last.AudioMs) <= TrustRecentAnchorMs)
                return (int)(last.CharOffset + rate * (atMs - last.AudioMs));
        }

        if (chapter.TextStart is { } start)
            return (int)(start + rate * (atMs - chapter.StartMs!.Value));

        // Nothing known about this chapter at all: place it by its share of the book.
        var bookMs = chapters.LastOrDefault(c => c.HasAudioRange)?.EndMs ?? 0;
        return bookMs > 0 ? (int)(textLength * (atMs / (double)bookMs)) : 0;
    }

    /// <summary>How far an anchor may be from the moment being predicted and still be the best guide.</summary>
    private const long TrustRecentAnchorMs = 120_000;

    /// <summary>
    /// Characters per millisecond for this chapter, measured from its own extent when that is
    /// known. A book-wide constant is a poor stand-in — narrators differ, and so do translations —
    /// and the error it introduces is what pushes the true position outside the search window.
    /// </summary>
    private double RateOf(Chapter chapter)
    {
        const double typicalCharsPerMs = 0.015;

        if (chapter is { TextStart: { } start, TextEnd: { } end } && chapter.DurationMs > 0 && end > start)
            return (end - start) / (double)chapter.DurationMs;

        return typicalCharsPerMs;
    }
}

/// <param name="AtMs">The moment just measured, so the caller can say how far ahead it is working.</param>
public record LiveAlignmentProgress(int ChapterIndex, long AtMs, float Confidence);
