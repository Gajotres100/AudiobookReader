using AudioBookReader.Core.Alignment;

namespace Epub3Maker;

/// <summary>
/// How hard to listen: three settings that trade hours of processor time for precision.
///
///   light   Whisper tiny, 10 s of every minute      ~1–1.5 s between anchors, interpolated
///   better  Whisper base, 15 s of every 30 s        ~0.5–1 s
///   best    MMS forced alignment, the whole book    20–80 ms, every sentence measured
///
/// The first two are the app's own aligner — sampled, with the text between anchors estimated —
/// and their density can be changed with <see cref="ParseAnchors"/>. The third hears every letter,
/// so there is nothing to space out.
/// </summary>
public enum Quality
{
    Light,
    Better,
    Best,
}

public static class Qualities
{
    public static Quality? Parse(string value) => value.Trim().ToLowerInvariant() switch
    {
        "light" or "lagano" or "fast" or "brzo" => Quality.Light,
        "better" or "bolje" or "good" or "dobro" => Quality.Better,
        "best" or "najbolje" or "mms" => Quality.Best,
        _ => null,
    };

    public static string Name(Quality quality) => quality switch
    {
        Quality.Light => "light",
        Quality.Better => "better",
        _ => "best",
    };

    /// <summary>The Whisper model a quality uses unless another is asked for.</summary>
    public static string DefaultModel(Quality quality) => quality == Quality.Light ? "tiny" : "base";

    /// <summary>
    /// Seconds between the starts of two probes: a word — sparse, medium, dense — or a number.
    /// Null when the value is neither.
    /// </summary>
    public static int? ParseAnchors(string value) => value.Trim().ToLowerInvariant() switch
    {
        "sparse" or "rijetko" => 60,
        "medium" or "srednje" => 30,
        "dense" or "gusto" => 15,
        var number when int.TryParse(number.TrimEnd('s'), out var seconds) && seconds is >= 5 and <= 600 => seconds,
        _ => null,
    };

    /// <summary>
    /// The sampling for a Whisper quality. Each probe is at most as long as the gap between probes,
    /// so asking for anchors every 10 s listens to the whole book in 10 s pieces rather than
    /// overlapping ones.
    /// </summary>
    public static AlignmentSettings Settings(Quality quality, int? anchorSeconds)
    {
        var (probe, interval) = quality == Quality.Light ? (10_000L, 60_000L) : (15_000L, 30_000L);

        if (anchorSeconds is { } seconds) interval = seconds * 1000L;

        return new AlignmentSettings
        {
            ProbeDurationMs = Math.Min(probe, interval),
            ProbeIntervalMs = interval,
        };
    }
}
