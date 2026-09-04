using Android.OS;
using AudioBookReader.Core.Alignment;

namespace AudioBookReader.App.Platforms.Android.Alignment;

/// <summary>
/// The chosen CPU budget, lowered automatically when the device reports it is getting hot.
///
/// The guarantee this provides is that no preset can cook the phone. A user who picks the fastest
/// setting and then puts the device in a pocket gets slowed down rather than a burning handset and
/// a flat battery — and since thermal throttling would have slowed the work anyway, backing off
/// early mostly costs nothing.
/// </summary>
public sealed class ThermalAwareThrottle : IWorkThrottle
{
    private readonly PowerManager? _power;
    private readonly DutyCycleThrottle _throttle;

    public ThermalAwareThrottle(CpuBudget budget, PowerManager? power = null)
    {
        _power = power ?? (PowerManager?)global::Android.App.Application.Context.GetSystemService(
            global::Android.Content.Context.PowerService);

        _throttle = new DutyCycleThrottle(() => budget.DutyCycle * ThermalScale());
    }

    /// <summary>The most recent thermal reading, for display alongside alignment progress.</summary>
    public ThermalStatus Status { get; private set; } = ThermalStatus.None;

    public Task ThrottleAsync(CancellationToken ct = default) => _throttle.ThrottleAsync(ct);

    /// <summary>
    /// Reads the thermal status on every call rather than subscribing to it.
    ///
    /// This runs between probes, which is every few tens of seconds — far more often than a device
    /// meaningfully changes thermal state. A listener would need an executor, a registration
    /// lifetime and a disposal path to be no more responsive than a property read.
    /// </summary>
    private float ThermalScale()
    {
        try
        {
            Status = _power?.CurrentThermalStatus ?? ThermalStatus.None;
        }
        catch (Exception)
        {
            // Not every device reports thermal state; without it the chosen budget simply stands.
            Status = ThermalStatus.None;
        }

        return Status switch
        {
            ThermalStatus.None => 1f,

            // Light is not "still fine". It is the point at which the system has begun lowering
            // clocks, and measurement on a warm phone showed both decoding and recognition taking
            // twice as long there — a run that pushes through it does the same work at half speed
            // and keeps the chip hot enough to stay that way. Easing off here is what stops the
            // slide, and costs less than the slowdown it avoids.
            ThermalStatus.Light => 0.7f,

            ThermalStatus.Moderate => 0.5f,

            // From severe upward the system is already throttling the whole device. Pushing on
            // would make everything slower, this work included, so it drops to a trickle.
            _ => 0.1f,
        };
    }
}
