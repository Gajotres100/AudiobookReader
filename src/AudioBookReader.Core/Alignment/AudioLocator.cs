using AudioBookReader.Core.Models;

namespace AudioBookReader.Core.Alignment;

/// <summary>What a search turned up: where in the audio, and the anchors it learned on the way.</summary>
public record AudioLocation(long AudioMs, int ChapterIndex, IReadOnlyList<Anchor> Anchors, float Confidence);

/// <summary>
/// Finds a passage of the book in the audio, without the book having been aligned first.
///
/// This is what makes the text tappable everywhere rather than only where a run has already been:
/// point at a sentence, and it works out when the narrator says it. The map answers that instantly
/// where it reaches, but it reaches only where something has listened — and the whole appeal of
/// aligning as you read is that most of the book has not been listened to yet.
///
/// The search converges quickly because narration is nearly linear: probe once, discover where the
/// narrator actually was at that moment, and the distance from there to the target divided by the
/// reading rate says how far to jump. That is Newton's method on a monotonic function, and it lands
/// within a sentence or two in two or three probes rather than scanning.
/// </summary>
public class AudioLocator(
    TokenizedText book,
    IReadOnlyList<Chapter> chapters,
    ITranscriber transcriber,
    int textLength,
    AlignmentSettings? settings = null,
    IWorkThrottle? throttle = null,
    Action<string>? log = null)
{
    private readonly AlignmentSettings _settings = settings ?? new AlignmentSettings();
    private readonly IWorkThrottle _throttle = throttle ?? NoThrottle.Instance;

    /// <summary>How many probes to spend before giving up. Each is one window of recognition.</summary>
    public int MaximumProbes { get; init; } = 8;

    /// <summary>
    /// Close enough to stop, in characters. A probe's own span is about this wide, so asking for
    /// better would be asking the search to resolve detail a single window cannot hold — the two
    /// anchors it leaves behind supply the rest.
    /// </summary>
    public int ToleranceChars { get; init; } = 200;

    /// <summary>
    /// Locates a character offset in the audio.
    /// </summary>
    /// <param name="map">Consulted for a starting estimate; may be null or cover nothing useful.</param>
    /// <returns>Null when nothing matched well enough to be worth acting on.</returns>
    public async Task<AudioLocation?> FindAsync(
        string audioPath,
        int charOffset,
        SyncMap? map,
        IProgress<int>? probesUsed = null,
        CancellationToken ct = default)
    {
        var bookMs = chapters.LastOrDefault(c => c.HasAudioRange)?.EndMs ?? 0;
        if (bookMs <= 0 || textLength <= 0) return null;

        var at = Estimate(charOffset, map, bookMs);
        var rate = Rate(map, bookMs);

        // Wide to begin with, because the estimate may be a proportional guess over a whole book,
        // and narrowed once something has actually been found.
        var radius = _settings.MaximumSearchRadiusTokens;

        var anchors = new List<Anchor>();
        AudioLocation? best = null;

        for (var probe = 0; probe < MaximumProbes; probe++)
        {
            ct.ThrowIfCancellationRequested();
            probesUsed?.Report(probe + 1);

            at = Math.Clamp(at, 0, Math.Max(0, bookMs - _settings.ProbeDurationMs));

            var transcript = await transcriber.TranscribeAsync(audioPath, at, _settings.ProbeDurationMs, ct);

            if (transcript.Words.Count == 0)
            {
                // Silence tells us nothing about where we are, so step past it rather than jumping.
                at += _settings.ProbeDurationMs;
                await _throttle.ThrottleAsync(ct);
                continue;
            }

            var match = TranscriptMatcher.MatchNear(
                book,
                [.. transcript.Words.Select(w => w.Value)],
                book.TokenIndexAtChar(PredictedChar(at, charOffset, anchors, rate, bookMs)),
                radius,
                _settings.MinConfidence);

            if (match is null)
            {
                log?.Invoke($"locate: no match at {at} ms within {radius} tokens");

                at += _settings.ProbeDurationMs;
                radius = Math.Min(radius * 2, book.Count);

                await _throttle.ThrottleAsync(ct);
                continue;
            }

            var opening = new Anchor(
                transcript.Words[match.Value.TranscriptStart].AtMs, match.Value.CharOffset, match.Value.Confidence);

            var closing = new Anchor(
                transcript.Words[match.Value.TranscriptEnd].AtMs, match.Value.EndCharOffset, match.Value.Confidence);

            anchors.Add(opening);
            if (closing.AudioMs > opening.AudioMs && closing.CharOffset > opening.CharOffset) anchors.Add(closing);

            // Two anchors from one window measure the local rate directly, which beats any guess.
            if (closing.AudioMs > opening.AudioMs && closing.CharOffset > opening.CharOffset)
                rate = (closing.CharOffset - opening.CharOffset) / (double)(closing.AudioMs - opening.AudioMs);

            var distance = charOffset - opening.CharOffset;

            log?.Invoke(
                $"locate: probe {probe + 1} at {at} ms is char {opening.CharOffset}, " +
                $"target {charOffset}, {distance} away");

            if (charOffset >= opening.CharOffset && charOffset <= closing.CharOffset)
            {
                // The target fell inside this window, so its two anchors time it directly.
                var window = ChapterSyncMap.FromAnchors(0, [opening, closing]);
                if (window.TryGetAudioMs(charOffset, out var exact))
                    return Finish(exact, anchors, match.Value.Confidence);
            }

            // Whatever distance is left converts to time at the rate just measured. Returning the
            // window's own start instead would leave the answer short by however far into the
            // window the target sat — up to the whole window.
            var landing = opening.AudioMs + (long)(distance / Math.Max(rate, 0.001));

            if (Math.Abs(distance) <= ToleranceChars)
                return Finish(landing, anchors, match.Value.Confidence);

            best = Finish(landing, anchors, match.Value.Confidence);

            // Having found where the narrator really was, the next probe starts from the landing
            // just computed — close enough that the window usually contains the target outright.
            at = landing;
            radius = _settings.SearchRadiusTokens * 4;

            await _throttle.ThrottleAsync(ct);
        }

        return best;
    }

    private AudioLocation? Finish(long audioMs, List<Anchor> anchors, float confidence)
    {
        if (ChapterAt(audioMs) is not { } chapter) return null;

        // Only the anchors belonging to the chapter being returned; the search may have visited
        // others on its way, and an anchor filed under the wrong chapter is worse than none.
        var mine = anchors
            .Where(a => a.AudioMs >= chapter.StartMs && a.AudioMs < chapter.EndMs)
            .ToList();

        return new AudioLocation(audioMs, chapter.Index, mine, confidence);
    }

    /// <summary>Where the narrator is expected to be at a moment, for the matcher to search around.</summary>
    private int PredictedChar(long atMs, int target, List<Anchor> found, double rate, long bookMs)
    {
        if (found.Count > 0)
        {
            var last = found[^1];
            return (int)(last.CharOffset + rate * (atMs - last.AudioMs));
        }

        // Nothing found yet, so the target itself is the best guess available: the estimate that
        // sent us to this moment was built from it.
        return target;
    }

    private long Estimate(int charOffset, SyncMap? map, long bookMs)
    {
        if (map is not null)
        {
            foreach (var chapter in map.Chapters)
            {
                if (chapter.Anchors.Count < 2) continue;

                var first = chapter.Anchors[0];
                var last = chapter.Anchors[^1];

                if (charOffset >= first.CharOffset && charOffset <= last.CharOffset &&
                    chapter.TryGetAudioMs(charOffset, out var known))
                    return known;
            }
        }

        return (long)(bookMs * (charOffset / (double)textLength));
    }

    private double Rate(SyncMap? map, long bookMs)
    {
        // Whatever the map has already measured is the best evidence about this narrator's pace.
        var measured = map?.Chapters
            .SelectMany(c => c.Anchors)
            .Where(a => a.Confidence > _settings.BoundaryConfidence)
            .ToList();

        if (measured is { Count: > 1 })
        {
            var span = measured[^1].AudioMs - measured[0].AudioMs;
            var chars = measured[^1].CharOffset - measured[0].CharOffset;

            if (span > 0 && chars > 0) return chars / (double)span;
        }

        return bookMs > 0 ? textLength / (double)bookMs : 0.015;
    }

    private Chapter? ChapterAt(long atMs) =>
        chapters.LastOrDefault(c => c.HasAudioRange && atMs >= c.StartMs && atMs < c.EndMs)
        ?? chapters.LastOrDefault(c => c.HasAudioRange);
}
