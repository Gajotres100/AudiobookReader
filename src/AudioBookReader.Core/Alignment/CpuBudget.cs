namespace AudioBookReader.Core.Alignment;

/// <summary>
/// How hard alignment is allowed to work the device.
///
/// This exists because the usual failure of on-device transcription is not that it is slow but
/// that it makes the phone unpleasant — hot, with a flat battery and a stuttering interface.
/// Alignment is background work with no deadline, so it can afford to be polite, and being polite
/// is mostly free here: chapters are aligned ahead of where the user is listening, so a slower
/// preset means a longer tail in the background rather than any waiting.
/// </summary>
public record CpuBudget
{
    public required string Name { get; init; }

    public required int Threads { get; init; }

    /// <summary>
    /// Run the worker at background priority. On Android this puts the thread in the background
    /// cpuset, which confines it to the efficiency cores — the single biggest reason this app
    /// should stay out of the way when others do not.
    /// </summary>
    public required bool BackgroundPriority { get; init; }

    /// <summary>Share of wall-clock time spent working, 0..1. The rest is spent letting the chip cool.</summary>
    public required float DutyCycle { get; init; }

    /// <summary>
    /// Roughly how many seconds of audio this preset transcribes per second of work. Used only to
    /// show an estimate before the user commits; the real figure is measured as the run proceeds.
    /// </summary>
    public required float RealtimeFactor { get; init; }

    public static readonly CpuBudget Eco = new()
    {
        Name = "Štedljivo",
        Threads = 2,
        BackgroundPriority = true,
        DutyCycle = 0.25f,
        RealtimeFactor = 2f,
    };

    public static readonly CpuBudget Balanced = new()
    {
        Name = "Uravnoteženo",
        Threads = 4,
        BackgroundPriority = true,
        DutyCycle = 0.5f,
        RealtimeFactor = 4f,
    };

    public static readonly CpuBudget Turbo = new()
    {
        Name = "Brzo",
        Threads = 0, // every available core
        BackgroundPriority = false,
        DutyCycle = 1f,
        RealtimeFactor = 8f,
    };

    public static readonly IReadOnlyList<CpuBudget> Presets = [Eco, Balanced, Turbo];

    /// <summary>
    /// Estimates how long aligning a book will take, for the figure shown next to each preset.
    ///
    /// Only a fraction of the audio ever reaches the recognizer, so the estimate is driven by the
    /// probe schedule rather than by the book's length alone — which is why even the most cautious
    /// preset finishes in a fraction of the listening time.
    /// </summary>
    public TimeSpan EstimateAlignmentTime(long audioDurationMs, AlignmentSettings? settings = null)
    {
        var schedule = settings ?? new AlignmentSettings();

        var covered = Math.Clamp(schedule.ProbeDurationMs / (double)schedule.ProbeIntervalMs, 0, 1);
        var transcribedMs = audioDurationMs * covered;

        var workMs = transcribedMs / Math.Max(RealtimeFactor, 0.1f);
        var wallClockMs = workMs / Math.Clamp(DutyCycle, 0.05f, 1f);

        return TimeSpan.FromMilliseconds(wallClockMs);
    }
}
