namespace AudioBookReader.Core.Alignment;

/// <summary>
/// Knobs for how thoroughly to align. The defaults encode the central bet of this design: the book
/// text is already known, so recognition only has to locate it, and locating it needs a small
/// fraction of the audio rather than all of it.
/// </summary>
public record AlignmentSettings
{
    /// <summary>
    /// How much audio each probe transcribes. Ten seconds is around twenty-five words — ample to
    /// be unambiguous against a text we already have, and the anchor can only be as precise as the
    /// probe is short.
    /// </summary>
    public long ProbeDurationMs { get; init; } = 10_000;

    /// <summary>
    /// Spacing between probes, and the setting that decides whether reading along actually works.
    ///
    /// Everything between anchors is interpolated, and narration is not metronomic — it slows for
    /// description and quickens through dialogue. Measured against a narrator whose pace wanders by
    /// a tenth and who pauses three seconds before each probe's first word:
    ///
    ///   10 s every 5 min    3.9% of the audio   median 101   p95 259 chars (17.3 s)
    ///   10 s every 2 min    8.9%                median  19   p95  58 chars ( 3.9 s)
    ///   10 s every 1 min   16.7%                median   7   p95  23 chars ( 1.5 s)
    ///   10 s every 30 s    32.2%                median   4   p95  23 chars ( 1.5 s)
    ///   10 s every 20 s    47.8%                median   4   p95  23 chars ( 1.5 s)
    ///
    /// One minute is the knee, and past it the tail simply stops responding: tripling coverage
    /// moves the median by two tenths of a second and p95 not at all. Whatever sets that floor, it
    /// is not the distance between anchors, so buying more probes is buying nothing.
    /// </summary>
    public long ProbeIntervalMs { get; init; } = 60_000;

    /// <summary>
    /// How many consecutive probes to spend finding where a chapter's narration actually begins,
    /// past any imprint, credits or music. Costs nothing when the first one already matches.
    /// </summary>
    public int RunInProbes { get; init; } = 12;

    /// <summary>
    /// Length of those opening probes, shorter than the rest.
    ///
    /// An anchor is recorded at the start of its probe, but the match inside it may begin later —
    /// so a probe straddling the end of an intro claims the narration is up to a probe-length
    /// ahead of where it really is. At a chapter's opening that error is the most visible one in
    /// the app, because it is where the reader looks first. Shorter windows bound it to ten
    /// seconds, and since probing stops at the first hit, the usual cost is one short probe rather
    /// than one long one.
    /// </summary>
    public long RunInProbeDurationMs { get; init; } = 10_000;

    /// <summary>Below this share of the transcript lining up, the probe is discarded rather than trusted.</summary>
    public float MinConfidence { get; init; } = 0.45f;

    /// <summary>
    /// How far either side of the predicted position to search, in words. At roughly 150 spoken
    /// words a minute this covers a prediction that is over two minutes out.
    /// </summary>
    public int SearchRadiusTokens { get; init; } = 400;

    /// <summary>
    /// How far to search for a phrase that follows one already located in the same probe.
    ///
    /// Tight on purpose. The previous phrase ended seconds ago, so the position is known to within
    /// a line or two — and a phrase is only a handful of words, short enough to appear convincingly
    /// somewhere else entirely. Searching wide for one does not find it more often, it finds it in
    /// the wrong place.
    /// </summary>
    public int PhraseRadiusTokens { get; init; } = 60;

    /// <summary>
    /// How wide the search may grow after repeated misses. Generous, because a run that has lost
    /// its place is worth spending real effort to recover — the alternative is a chapter, and then
    /// every chapter after it, with no anchors at all.
    /// </summary>
    public int MaximumSearchRadiusTokens { get; init; } = 12_000;

    /// <summary>
    /// How many probes may fail at the widest radius before the whole book is searched.
    ///
    /// The widening radius recovers from drift, but it cannot recover from being in the wrong place
    /// entirely — and that happens: an ebook whose text runs in a different order from the audio,
    /// a collection whose foreword sits in the middle of the file, a chapter list that disagrees
    /// with the recording. Measured on a real pair, the narration began at character 260,000 while
    /// the aligner was predicting 88,000; twelve thousand tokens of radius could never reach it, so
    /// every probe in an hour and a half of work missed and the book aligned to nothing.
    ///
    /// Three, because at the widest radius a miss already means the estimate is badly wrong, and
    /// searching everything is cheap next to the probes being thrown away.
    /// </summary>
    public int MissesBeforeSearchingEverywhere { get; init; } = 3;

    /// <summary>
    /// Weight given to the newest observed narration rate when updating the running estimate.
    /// Smoothing keeps one bad probe from throwing off every prediction that follows.
    /// </summary>
    public double RateSmoothing { get; init; } = 0.5;

    /// <summary>
    /// Confidence assigned to the anchors placed at chapter boundaries. Low on purpose: chapter
    /// marks in the audio and in the ebook rarely line up exactly, so these should give way to any
    /// real match that contradicts them.
    /// </summary>
    public float BoundaryConfidence { get; init; } = 0.2f;

    /// <summary>Denser probing used to refine the passage the reader is currently looking at.</summary>
    public static AlignmentSettings Refinement { get; } = new()
    {
        ProbeDurationMs = 15_000,
        ProbeIntervalMs = 15_000,
        SearchRadiusTokens = 120,
    };
}
