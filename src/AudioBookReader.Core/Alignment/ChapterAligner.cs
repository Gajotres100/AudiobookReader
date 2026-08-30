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

            if (words.Count == 0)
            {
                // Distinguishing "heard nothing" from "heard something unrecognizable" is the
                // difference between a decoding problem and a matching problem, and the anchors
                // alone cannot tell them apart afterwards.
                log?.Invoke($"ch{request.ChapterIndex} probe@{probe.StartMs}ms: no speech recognised");
            }
            else
            {
                var match = TranscriptMatcher.MatchNear(
                    book,
                    [.. words.Select(w => w.Value)],
                    book.TokenIndexAtChar(predictedChar),
                    radius,
                    _settings.MinConfidence);

                if (match is not null)
                {
                    // Both ends of the probe, timed by when those words were actually spoken rather
                    // than by when the probe happened to begin. The pair costs nothing beyond the
                    // recognition already done, and it pins the stretch the probe covers instead of
                    // leaving its far end to interpolation.
                    var opening = new Anchor(
                        words[match.Value.TranscriptStart].AtMs,
                        match.Value.CharOffset,
                        match.Value.Confidence);

                    var closing = new Anchor(
                        words[match.Value.TranscriptEnd].AtMs,
                        match.Value.EndCharOffset,
                        match.Value.Confidence);

                    anchors.Add(opening);

                    // Only when it says something the opening does not; a one-word match, or a
                    // recognizer reporting a single instant for the lot, gives no second point.
                    if (closing.AudioMs > opening.AudioMs && closing.CharOffset > opening.CharOffset)
                        anchors.Add(closing);

                    matches++;
                    charsPerMs = UpdateRate(charsPerMs, lastAccepted, opening);
                    lastAccepted = anchors[^1];
                    radius = _settings.SearchRadiusTokens;
                }
                else
                {
                    log?.Invoke(
                        $"ch{request.ChapterIndex} probe@{probe.StartMs}ms: {words.Count} words, no match within " +
                        $"{radius} of char {predictedChar} — heard: \"{Preview(transcript.Text)}\"");

                    radius = Math.Min(radius * 3, _settings.MaximumSearchRadiusTokens);
                }
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
