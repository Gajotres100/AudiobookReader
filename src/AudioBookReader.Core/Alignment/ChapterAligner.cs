using AudioBookReader.Core.Models;

namespace AudioBookReader.Core.Alignment;

/// <summary>One chapter's worth of work: the audio to listen to, and the text it is expected to be.</summary>
/// <param name="TextStart">Estimated start of the chapter in the book text; refined by the result.</param>
/// <param name="TextEnd">Estimated end of the chapter in the book text.</param>
public record ChapterAlignmentRequest(
    string AudioPath,
    int ChapterIndex,
    long AudioStartMs,
    long AudioEndMs,
    int TextStart,
    int TextEnd);

public record AlignmentProgress(int ChapterIndex, int ProbesCompleted, int ProbeCount, int AnchorsFound);

/// <summary>
/// Aligns one chapter by listening at intervals and finding each snippet in the book text.
///
/// The chapter is the unit of work because it is also the unit of usefulness: once chapter one is
/// aligned the reader can follow along in it while the rest continues in the background, so the
/// user never waits for a whole book to be processed.
/// </summary>
public class ChapterAligner(
    TokenizedText book,
    ITranscriber transcriber,
    AlignmentSettings? settings = null,
    IWorkThrottle? throttle = null,
    Action<string>? log = null)
{
    private readonly AlignmentSettings _settings = settings ?? new AlignmentSettings();
    private readonly IWorkThrottle _throttle = throttle ?? NoThrottle.Instance;

    /// <summary>Bounds on a believable narration rate, set from the chapter's own proportions.</summary>
    private double _floor;
    private double _ceiling;

    /// <summary>How many probes may pass before what has been found is handed back to be saved.</summary>
    private const int CheckpointEvery = 20;

    /// <param name="resumeFrom">
    /// What a previous run already measured in this chapter. Its anchors are kept and the probes
    /// they cover are skipped, so continuing a chapter costs only the part that is missing.
    /// </param>
    /// <param name="checkpoint">
    /// Called every so often with the anchors so far, for the caller to persist.
    ///
    /// This exists because a chapter is not always a small unit of work. An MP3 with no chapter
    /// marks is one chapter covering the whole book, and saving only at the end meant a seven-hour
    /// chapter wrote nothing at all until it finished — so stopping it, or the phone stopping it,
    /// threw away every probe. Hours of work with nothing to show is not a slow feature, it is a
    /// broken one.
    /// </param>
    public async Task<ChapterSyncMap> AlignAsync(
        ChapterAlignmentRequest request,
        IProgress<AlignmentProgress>? progress = null,
        CancellationToken ct = default,
        ChapterSyncMap? resumeFrom = null,
        Func<ChapterSyncMap, Task>? checkpoint = null)
    {
        var probes = ProbePlanner.Plan(request.AudioStartMs, request.AudioEndMs, _settings);
        if (probes.Count == 0) return new ChapterSyncMap { ChapterIndex = request.ChapterIndex };

        // Everything a previous run measured here, kept rather than repeated.
        var anchors = resumeFrom is null ? [] : new List<Anchor>(resumeFrom.Anchors);

        // The chapter's own boundaries are weak anchors: they hold the ends of the interpolation
        // down when probing finds nothing there, but any real match that disagrees will displace
        // them, because chapter marks in the audio and in the ebook seldom agree exactly.
        anchors.Add(new Anchor(request.AudioStartMs, request.TextStart, _settings.BoundaryConfidence));
        anchors.Add(new Anchor(request.AudioEndMs, request.TextEnd, _settings.BoundaryConfidence));

        var charsPerMs = EstimateInitialRate(request);
        _floor = charsPerMs / 5;
        _ceiling = charsPerMs * 5;
        Anchor? lastAccepted = null;
        var matches = 0;

        // Widened after each miss and reset on a hit.
        //
        // A fixed window is unforgiving: once the predicted position drifts further out than the
        // radius, every probe misses, the chapter ends with no anchors, and the next chapter
        // inherits an even worse estimate — one bad guess derails the rest of the book. Searching
        // wider after a failure costs a little time on the probe that already failed and lets the
        // run find its place again.
        var radius = _settings.SearchRadiusTokens;

        // Misses in a row once the radius can grow no further, which is the signal that the run is
        // not drifting but lost.
        var blindMisses = 0;

        for (var i = 0; i < probes.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var probe = probes[i];

            // Already heard, by a run that was interrupted before it could finish. Re-transcribing
            // it would cost the same as the first time and tell us what we already know.
            if (resumeFrom is not null
                && resumeFrom.IsMeasuredAt(probe.StartMs, _settings.ProbeIntervalMs, _settings.MinConfidence))
            {
                progress?.Report(new AlignmentProgress(request.ChapterIndex, i + 1, probes.Count, matches));
                continue;
            }

            // Once the chapter's opening is pinned there is nothing left for the remaining run-in
            // probes to find, and transcribing them would be pure waste. Still reported, though:
            // a dozen probes silently skipped made the chapter's progress jump from nothing to a
            // third in one step, which reads as a stall followed by a glitch.
            if (probe.IsRunIn && lastAccepted is not null)
            {
                progress?.Report(new AlignmentProgress(request.ChapterIndex, i + 1, probes.Count, matches));
                continue;
            }

            var predictedChar = Predict(request, probe.StartMs, lastAccepted, charsPerMs);

            var transcript = await transcriber.TranscribeAsync(request.AudioPath, probe.StartMs, probe.DurationMs, ct);
            var words = transcript.Words;

            if (words.Count > 0)
            {
                var wasPinned = lastAccepted is not null;
                var located = LocatePhrases(transcript, predictedChar, radius);

                // Nothing near where it was expected. Search the whole book with what was heard
                // rather than widen the window a step at a time — at once when this chapter has not
                // yet found its place, since the estimate it started from is then only the book's
                // proportions, and after several misses at the widest radius otherwise, when the
                // text evidently does not run in the order the audio does.
                //
                // Measured on a real book: the text opened with a contents page, maps, notes and a
                // recap that the audio skips, so chapter one began thousands of words past the
                // estimate, and stepping the radius out spent three run-in probes missing it —
                // while the start of the chapter was left interpolated across all that front
                // matter, which in the reader is the highlight racing through pages nobody reads.
                if (located == 0
                    && (!wasPinned || blindMisses >= _settings.MissesBeforeSearchingEverywhere)
                    && TranscriptMatcher.MatchAnywhere(
                        book, transcript, _settings.MinWordsToSearchEverywhere, _settings.SearchEverywhereConfidence)
                        is { } anywhere)
                {
                    log?.Invoke(
                        $"ch{request.ChapterIndex} probe@{probe.StartMs}ms: found by searching the whole book " +
                        $"at char {anywhere.CharOffset}, expected {predictedChar}");

                    // Whatever rate and position were carried here came from a guess about where
                    // the book even was. Start again from what was just found.
                    charsPerMs = EstimateInitialRate(request);
                    lastAccepted = null;

                    located = LocatePhrases(transcript, anywhere.CharOffset, _settings.SearchRadiusTokens);
                }

                // The chapter's opening pinned within its run-in: the boundary guess at its start
                // came from the book's proportions and is now known to be wrong, and kept it would
                // draw a line from the guess to the first real anchor — the highlight sweeping
                // through contents pages and maps in the few seconds before the first line.
                // Replaced by the place the narration leads back to at the chapter's own pace.
                if (!wasPinned && located > 0 && probe.IsRunIn) RestartOpening();

                if (located > 0)
                {
                    matches++;
                    radius = _settings.SearchRadiusTokens;
                    blindMisses = 0;
                }
                else
                {
                    // Counted only once the radius is already at its cap; before that a miss means
                    // the window was too small, which widening is about to fix on its own.
                    if (radius >= _settings.MaximumSearchRadiusTokens) blindMisses++;

                    log?.Invoke(
                        $"ch{request.ChapterIndex} probe@{probe.StartMs}ms: {words.Count} words in " +
                        $"{transcript.Phrases.Count} phrases, none matched within {radius} of char " +
                        $"{predictedChar} — heard: \"{Preview(transcript.Text)}\"");

                    radius = Math.Min(radius * 3, _settings.MaximumSearchRadiusTokens);
                }
            }
            else
            {
                // Distinguishing "heard nothing" from "heard something unrecognizable" is the
                // difference between a decoding problem and a matching problem, and the anchors
                // alone cannot tell them apart afterwards.
                log?.Invoke($"ch{request.ChapterIndex} probe@{probe.StartMs}ms: no speech recognised");
            }

            progress?.Report(new AlignmentProgress(request.ChapterIndex, i + 1, probes.Count, matches));

            // Handed over periodically rather than only at the end, so an interrupted chapter
            // keeps what it heard. Cheap next to a probe, and only when there is something new.
            if (checkpoint is not null && i % CheckpointEvery == CheckpointEvery - 1 && anchors.Count > 2)
                await checkpoint(ChapterSyncMap.FromAnchors(request.ChapterIndex, anchors));

            await _throttle.ThrottleAsync(ct);
        }

        return ChapterSyncMap.FromAnchors(request.ChapterIndex, anchors);

        // Located one timed phrase at a time rather than as a single block. A probe found only at
        // its two ends is a straight line drawn across everything in between, and the longer the
        // probe the worse that line fits — which is what kept probes short and therefore
        // expensive, since recognition costs the same for ten seconds as for thirty. Phrase by
        // phrase, a long probe is a run of anchors instead of a guess.
        int LocatePhrases(Transcript transcript, int firstExpected, int firstReach)
        {
            var located = 0;

            foreach (var phrase in transcript.Phrases)
            {
                ct.ThrowIfCancellationRequested();

                var expected = lastAccepted is { } anchor
                    ? (int)Math.Round(anchor.CharOffset + charsPerMs * (phrase.StartMs - anchor.AudioMs))
                    : firstExpected;

                // Once a phrase in this probe has been placed, the next one is seconds away and
                // must be searched for accordingly. A ten-word phrase is short enough to occur
                // plausibly elsewhere in a novel, so hunting for it across hundreds of words finds
                // confident nonsense — measurably worse than not looking at all, because a wrong
                // anchor drags the interpolation around it.
                var reach = located > 0 ? _settings.PhraseRadiusTokens : firstReach;

                var found = TranscriptMatcher.MatchNear(
                    book,
                    [.. phrase.Words.Select(w => w.Value)],
                    book.TokenIndexAtChar(expected),
                    reach,
                    _settings.MinConfidence);

                if (found is null) continue;

                // Weighting this by the recognizer's own segment probability was tried and
                // reverted: WhisperTranscriber never calls .WithProbabilities(), so
                // phrase.Probability is always 0 in practice, which zeroed every real anchor's
                // confidence. FromAnchors then picks the run with the highest confidence SUM, so a
                // chapter of forty real anchors at 0 lost to the two boundary guesses at 0.2 each —
                // every whole-book run kept nothing but its own starting guesses. If recognizer
                // confidence is worth using later, it needs its own field on Anchor and
                // .WithProbabilities() actually turned on, not a multiply here.
                var opening = new Anchor(
                    phrase.Words[found.Value.TranscriptStart].AtMs,
                    found.Value.CharOffset,
                    found.Value.Confidence);

                var closing = new Anchor(
                    phrase.Words[found.Value.TranscriptEnd].AtMs,
                    found.Value.EndCharOffset,
                    found.Value.Confidence);

                anchors.Add(opening);

                if (closing.AudioMs > opening.AudioMs && closing.CharOffset > opening.CharOffset)
                    anchors.Add(closing);

                charsPerMs = UpdateRate(charsPerMs, lastAccepted, opening);
                lastAccepted = anchors[^1];
                located++;
            }

            return located;
        }

        void RestartOpening()
        {
            var measured = anchors.Where(a => a.Confidence > _settings.BoundaryConfidence).ToList();
            if (measured.Count == 0) return;

            var earliest = measured.MinBy(a => a.AudioMs);

            // At a typical narrating pace rather than the chapter's estimated one: that estimate
            // divides a text range which may still include the front matter, and would lead back
            // into it. Never less than a character, so the two anchors never share an offset.
            const double typicalCharsPerMs = 0.015;
            var leadIn = Math.Max(1, (long)Math.Round(typicalCharsPerMs * (earliest.AudioMs - request.AudioStartMs)));
            var startChar = (int)Math.Clamp(earliest.CharOffset - leadIn, 0, earliest.CharOffset);

            if (startChar >= earliest.CharOffset || earliest.AudioMs <= request.AudioStartMs) return;

            anchors.RemoveAll(a => a.AudioMs == request.AudioStartMs && a.Confidence <= _settings.BoundaryConfidence);
            anchors.Add(new Anchor(request.AudioStartMs, startChar, _settings.BoundaryConfidence));
        }
    }

    /// <summary>Enough of a transcript to recognise it, without filling the log with a chapter.</summary>
    private static string Preview(string transcript)
    {
        var trimmed = transcript.Trim().ReplaceLineEndings(" ");
        return trimmed.Length <= 90 ? trimmed : trimmed[..90] + "…";
    }

    private static double EstimateInitialRate(ChapterAlignmentRequest request)
    {
        var span = request.AudioEndMs - request.AudioStartMs;
        var characters = request.TextEnd - request.TextStart;

        // Falls back to a typical narration rate — about 15 characters a second — when the chapter
        // gives nothing to measure.
        return span > 0 && characters > 0 ? characters / (double)span : 0.015;
    }

    private static int Predict(ChapterAlignmentRequest request, long atMs, Anchor? lastAccepted, double charsPerMs)
    {
        var (fromMs, fromChar) = lastAccepted is { } anchor
            ? (anchor.AudioMs, anchor.CharOffset)
            : (request.AudioStartMs, request.TextStart);

        return (int)Math.Round(fromChar + charsPerMs * (atMs - fromMs));
    }

    /// <summary>
    /// Folds the rate just observed between two anchors into the running estimate.
    /// Smoothed rather than replaced, so a single misplaced anchor cannot skew every prediction
    /// after it and cascade into a chapter of missed probes.
    /// </summary>
    private double UpdateRate(double current, Anchor? previous, Anchor accepted)
    {
        if (previous is not { } from) return current;

        var elapsed = accepted.AudioMs - from.AudioMs;
        if (elapsed <= 0) return current;

        var observed = (accepted.CharOffset - from.CharOffset) / (double)elapsed;
        if (observed <= 0) return current;

        // Bounded, not merely smoothed. After a miss the search widens to twelve thousand tokens,
        // and a confident-but-wrong match inside that span implies a reading rate hundreds of times
        // too fast. Smoothing only halves it, so the next prediction lands past the end of the book,
        // every probe after it misses, and the chapter finishes with nothing to show. No narrator
        // reads at five times or a fifth of the pace the chapter itself implies.
        var plausible = Math.Clamp(observed, _floor, _ceiling);

        return current * (1 - _settings.RateSmoothing) + plausible * _settings.RateSmoothing;
    }
}
