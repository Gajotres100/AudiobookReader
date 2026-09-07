using AudioBookReader.App.Resources.Strings;
using Android.OS;
using AudioBookReader.App.Services;
using AudioBookReader.Core.Alignment;

namespace AudioBookReader.App.Platforms.Android.Alignment;

/// <summary>
/// Holds alignment back while the conditions the user set are not met, then lets it resume.
///
/// Waiting rather than stopping is the point: the run keeps its place, its loaded model and its
/// progress, so unplugging the phone for ten minutes costs ten minutes rather than the chapter.
/// Wrapping the throttle is where this belongs — it is the one place the pipeline already yields.
/// </summary>
public sealed class EnvironmentGate(AlignmentSettingsStore settings, IWorkThrottle inner) : IWorkThrottle
{
    private static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(15);

    /// <summary>Why work is currently paused, or null when it is running.</summary>
    public string? BlockedReason { get; private set; }

    public async Task ThrottleAsync(CancellationToken ct = default)
    {
        await inner.ThrottleAsync(ct);

        while (BlockingReason() is { } reason)
        {
            BlockedReason = reason;
            await Task.Delay(RecheckInterval, ct);
        }

        BlockedReason = null;
    }

    private string? BlockingReason()
    {
        if (settings.ChargingOnly && !IsCharging()) return Strings.Gate_WaitingForCharger;

        if (settings.MinimumBatteryPercent > 0 && BatteryPercent() < settings.MinimumBatteryPercent)
            return string.Format(Strings.Gate_BatteryBelow, settings.MinimumBatteryPercent);

        if (settings.ScreenOffOnly && IsScreenOn()) return Strings.Gate_WaitingForScreenOff;

        return null;
    }

    private static bool IsCharging()
    {
        try
        {
            return Battery.Default.State is BatteryState.Charging or BatteryState.Full;
        }
        catch (Exception)
        {
            // A device that will not report charging state should not block work forever.
            return true;
        }
    }

    private static int BatteryPercent()
    {
        try
        {
            return (int)(Battery.Default.ChargeLevel * 100);
        }
        catch (Exception)
        {
            return 100;
        }
    }

    private static bool IsScreenOn()
    {
        var power = (PowerManager?)global::Android.App.Application.Context
            .GetSystemService(global::Android.Content.Context.PowerService);

        return power?.IsInteractive ?? false;
    }
}
