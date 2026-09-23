using AudioBookReader.App.Resources.Strings;
using AudioBookReader.App.Services;
using AudioBookReader.Core.Alignment;

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

    /// <summary>
    /// Read from a flag the app delegate keeps, not from UIApplication directly: this runs on the
    /// alignment's own thread, and UIKit refuses to be asked anything off the main one — in a debug
    /// build .NET turns that into an exception, which would end the run the first time the check ran.
    /// </summary>
    private static bool IsScreenOn() => AppForeground.IsActive;
}

/// <summary>Whether the app is the one on screen, maintained by <see cref="AppDelegate"/> from UIKit's own lifecycle calls.</summary>
public static class AppForeground
{
    private static volatile bool _active = true;

    public static bool IsActive
    {
        get => _active;
        set => _active = value;
    }
}
