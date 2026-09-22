using AudioBookReader.App.Resources.Strings;
using AudioBookReader.App.Services;
using AudioBookReader.Core.Alignment;
using UIKit;

namespace AudioBookReader.App.Platforms.iOS.Alignment;

/// <summary>
/// Holds alignment back while the conditions the user set are not met — the iOS counterpart of
/// Android's EnvironmentGate, ported field for field except for the "screen on" check.
///
/// iOS has no public API for whether the screen is lit while the app is in the foreground, because
/// a foregrounded app already implies the screen is on. What "screen off" actually distinguishes on
/// this platform is UIApplication.SharedApplication.ApplicationState: Active while the app is what
/// the user is looking at, Background otherwise — which lines up with how alignment itself is
/// staged here. The foreground-only path (AlignmentQueue.iOS.cs) only ever runs while Active, so a
/// user who enables "screen off only" correctly blocks it outright; the BGProcessingTask extension
/// (a later stage) runs precisely when this reports Background, which is the moment this setting
/// means to allow.
/// </summary>
public sealed class EnvironmentGate(AlignmentSettingsStore settings, IWorkThrottle inner) : IWorkThrottle
{
    private static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(15);

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

    private static bool IsScreenOn() => UIApplication.SharedApplication.ApplicationState == UIApplicationState.Active;
}
