using System.Diagnostics;

namespace AudioBookReader.Core.Alignment;

/// <summary>
/// Paces alignment by resting in proportion to how long the last unit of work took.
///
/// Halving the duty cycle does more than halve average CPU: it gives the chip time to cool, which
/// is what actually keeps a long run tolerable. Sustained full-tilt work heats the device until
/// the system throttles it, at which point everything on the phone gets slower — including the
/// alignment — so the greedy schedule is not even the fastest one.
///
/// The duty cycle is read fresh on every call rather than captured, so the host can lower it the
/// moment the device reports thermal pressure and raise it again once things settle.
/// </summary>
public class DutyCycleThrottle(
    Func<float> dutyCycle,
    Func<TimeSpan, CancellationToken, Task>? delay = null,
    Func<long>? timestamp = null) : IWorkThrottle
{
    /// <summary>
    /// Ceiling on a single rest. A probe that took unusually long — a stall, or the process being
    /// suspended mid-work — must not translate into a rest of minutes.
    /// </summary>
    public TimeSpan MaximumRest { get; init; } = TimeSpan.FromSeconds(30);

    private readonly Func<TimeSpan, CancellationToken, Task> _delay =
        delay ?? ((duration, ct) => Task.Delay(duration, ct));

    private readonly Func<long> _timestamp = timestamp ?? Stopwatch.GetTimestamp;

    private long? _workStarted;

    public DutyCycleThrottle(CpuBudget budget) : this(() => budget.DutyCycle) { }

    public async Task ThrottleAsync(CancellationToken ct = default)
    {
        var now = _timestamp();

        if (_workStarted is { } startedAt)
        {
            var worked = TimeSpan.FromSeconds((now - startedAt) / (double)Stopwatch.Frequency);
            var rest = RestFor(worked);

            if (rest > TimeSpan.Zero) await _delay(rest, ct);
        }

        // Timed from after the rest, so the next measurement covers work only.
        _workStarted = _timestamp();
    }

    private TimeSpan RestFor(TimeSpan worked)
    {
        var duty = Math.Clamp(dutyCycle(), 0.05f, 1f);
        if (duty >= 1f || worked <= TimeSpan.Zero) return TimeSpan.Zero;

        var rest = worked * (1 / duty - 1);
        return rest > MaximumRest ? MaximumRest : rest;
    }
}
