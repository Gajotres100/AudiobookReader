namespace AudioBookReader.Core.Alignment;

/// <summary>A stretch of audio to transcribe.</summary>
/// <param name="IsRunIn">
/// One of the back-to-back probes covering the opening of a chapter. They exist only to catch the
/// moment real narration begins, so once one of them matches the rest are skipped.
/// </param>
public readonly record struct ProbeWindow(long StartMs, long DurationMs, bool IsRunIn = false)
{
    public long EndMs => StartMs + DurationMs;
}

/// <summary>Decides where in a chapter to listen.</summary>
public static class ProbePlanner
{
    /// <summary>
    /// Spaces probes evenly through the chapter, always covering its start and its end.
    ///
    /// The ends matter more than the middle: interpolation between anchors is reliable, but
    /// anything outside the outermost anchors can only be clamped, so a chapter anchored only in
    /// its middle would follow the narration poorly exactly where the reader starts and finishes.
    /// </summary>
    public static List<ProbeWindow> Plan(long chapterStartMs, long chapterEndMs, AlignmentSettings settings)
    {
        var length = chapterEndMs - chapterStartMs;
        if (length <= 0) return [];

        var duration = Math.Min(settings.ProbeDurationMs, length);
        var probes = new List<ProbeWindow>();

        // A chapter rarely opens with the book's own words. An audiobook begins with the
        // publisher's imprint and the narrator's credits; a chapter may open with music or a
        // spoken number. A single probe at the start therefore often finds nothing, leaving the
        // opening minutes to interpolation — which is precisely where a reader notices the text
        // running behind the voice. These consecutive probes walk forward until real narration
        // starts; the aligner abandons the rest as soon as one of them lands.
        var runIn = Math.Min(settings.RunInProbeDurationMs, length);

        for (var i = 0; i < settings.RunInProbes; i++)
        {
            var at = chapterStartMs + i * runIn;
            if (i > 0 && at + runIn > chapterEndMs) break;

            probes.Add(new ProbeWindow(at, runIn, IsRunIn: true));
        }

        var interval = Math.Max(settings.ProbeIntervalMs, duration);
        for (var at = chapterStartMs + interval; at + duration < chapterEndMs; at += interval)
            if (at > probes[^1].StartMs) probes.Add(new ProbeWindow(at, duration));

        var lastStart = chapterEndMs - duration;
        if (lastStart > probes[^1].StartMs) probes.Add(new ProbeWindow(lastStart, duration));

        return probes;
    }
}
