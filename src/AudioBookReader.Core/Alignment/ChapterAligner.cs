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

    public async Task<ChapterSyncMap> AlignAsync(
        ChapterAlignmentRequest request,
        IProgress<AlignmentProgress>? progress = null,
        CancellationToken ct = default)
    {
        var probes = ProbePlanner.Plan(request.AudioStartMs, request.AudioEndMs, _settings);
        if (probes.Count == 0) return new ChapterSyncMap { ChapterIndex = request.ChapterIndex };

        var anchors = new List<Anchor>();

        // The chapter's own boundaries are weak anchors: they hold the ends of the interpolation
        // down when probing finds nothing there, but any real match that disagrees will displace
        // them, because chapter marks in the audio and in the ebook seldom agree exactly.
        anchors.Add(new Anchor(request.AudioStartMs, request.TextStart, _settings.BoundaryConfidence));
        anchors.Add(new Anchor(request.AudioEndMs, request.TextEnd, _settings.BoundaryConfidence));

        var charsPerMs = EstimateInitialRate(request);
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

        for (var i = 0; i < probes.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var probe = probes[i];

            // Once the chapter's opening is pinned there is nothing left for the remaining run-in
            // probes to find, and transcribing them would be pure waste.
            if (probe.IsRunIn && lastAccepted is not null) continue;

            var predictedChar = Predict(request, probe.StartMs, lastAccepted, charsPerMs);

            var transcript = await transcriber.TranscribeAsync(request.AudioPath, probe.StartMs, probe.DurationMs, ct);
            var words = transcript.Words;

            if (words.Count > 0)
            {
                // Located one timed phrase at a time rather than as a single block. A probe found
                // only at its two ends is a straight line drawn across everything in between, and
                // the longer the probe the worse that line fits — which is what kept probes short
                // and therefore expensive, since recognition costs the same for ten seconds as for
                // thirty. Phrase by phrase, a long probe is a run of anchors instead of a guess.
                var located = 0;

                foreach (var phrase in transcript.Phrases)
                {
                    ct.ThrowIfCancellationRequested();

                    var expected = lastAccepted is { } anchor
                        ? (int)Math.Round(anchor.CharOffset + charsPerMs * (phrase.StartMs - anchor.AudioMs))
                        : predictedChar;

                    // Once a phrase in this probe has been placed, the next one is seconds away and
                    // must be searched for accordingly. A ten-word phrase is short enough to occur
                    // plausibly elsewhere in a novel, so hunting for it across hundreds of words
                    // finds confident nonsense — measurably worse than not looking at all, because
                    // a wrong anchor drags the interpolation around it.
                    var reach = located > 0 ? _settings.PhraseRadiusTokens : radius;

                    var found = TranscriptMatcher.MatchNear(
                        book,
                        [.. phrase.Words.Select(w => w.Value)],
                        book.TokenIndexAtChar(expected),
                        reach,
                        _settings.MinConfidence);

                    if (found is null) continue;

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

                if (located > 0)
                {
                    matches++;
                    radius = _settings.SearchRadiusTokens;
                }
                else
                {
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

            await _throttle.ThrottleAsync(ct);
        }

        return ChapterSyncMap.FromAnchors(request.ChapterIndex, anchors);
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

        return current * (1 - _settings.RateSmoothing) + observed * _settings.RateSmoothing;
    }
}
