using AudioBookReader.Core.Alignment;
using Foundation;

namespace AudioBookReader.App.Platforms.iOS.Alignment;

/// <summary>
/// The chosen CPU budget, lowered automatically when the device reports it is getting hot — the
/// iOS counterpart of Android's ThermalAwareThrottle. <see cref="NSProcessInfo.ThermalState"/> is
/// a near 1:1 replacement for Android's PowerManager.CurrentThermalStatus: both are a four-step
/// scale the OS itself computes from skin temperature, battery and CPU load, read here rather than
/// subscribed to for the same reason Android does not subscribe either — this runs between probes,
/// every few tens of seconds, far more often than thermal state meaningfully changes.
/// </summary>
public sealed class ThermalAwareThrottle : IWorkThrottle
{
    private readonly DutyCycleThrottle _throttle;

    public ThermalAwareThrottle(CpuBudget budget)
    {
        _throttle = new DutyCycleThrottle(() => budget.DutyCycle * ThermalScale());
    }

    public NSProcessInfoThermalState Status { get; private set; } = NSProcessInfoThermalState.Nominal;

    public Task ThrottleAsync(CancellationToken ct = default) => _throttle.ThrottleAsync(ct);

    private float ThermalScale()
    {
        try
        {
            Status = NSProcessInfo.ProcessInfo.ThermalState;
        }
        catch (Exception)
        {
            Status = NSProcessInfoThermalState.Nominal;
        }

        return Status switch
        {
            NSProcessInfoThermalState.Nominal => 1f,

            // Same reasoning as Android's Light tier: the OS has already begun lowering clocks by
            // the time it reports Fair, so easing off here is what stops decode and recognition
            // both sliding to half speed rather than a response to it.
            NSProcessInfoThermalState.Fair => 0.7f,

            NSProcessInfoThermalState.Serious => 0.5f,

            _ => 0.1f, // Critical
        };
    }
}
