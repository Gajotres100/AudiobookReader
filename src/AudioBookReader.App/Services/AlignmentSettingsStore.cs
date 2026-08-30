using AudioBookReader.Core.Alignment;

namespace AudioBookReader.App.Services;

/// <summary>
/// The user's alignment preferences.
///
/// Kept in <see cref="Preferences"/> rather than the database: these are a handful of scalars read
/// once when a run starts, and the alignment service needs them before it touches anything else.
/// </summary>
public class AlignmentSettingsStore
{
    private const string BudgetKey = "alignment.budget";
    private const string ChargingOnlyKey = "alignment.chargingOnly";
    private const string ScreenOffOnlyKey = "alignment.screenOffOnly";
    private const string MinimumBatteryKey = "alignment.minimumBattery";
    private const string LiveRefinementKey = "alignment.live";

    /// <summary>
    /// Align the passage being read, as it is read, instead of sampling the whole book in advance.
    ///
    /// The two are alternatives rather than settings of one thing. Sampling maps ten hours for
    /// sixteen percent of the audio but leaves everything between anchors interpolated, and that
    /// interpolation is the whole of the remaining error — measurement showed tripling the sample
    /// rate does not touch it. This measures every second of what is actually being listened to and
    /// nothing else, which costs about half of real time and never runs ahead of the listener.
    /// </summary>
    public bool LiveRefinement
    {
        get => Preferences.Default.Get(LiveRefinementKey, false);
        set => Preferences.Default.Set(LiveRefinementKey, value);
    }

    public CpuBudget Budget
    {
        get
        {
            var name = Preferences.Default.Get(BudgetKey, CpuBudget.Balanced.Name);
            return CpuBudget.Presets.FirstOrDefault(p => p.Name == name) ?? CpuBudget.Balanced;
        }
        set => Preferences.Default.Set(BudgetKey, value.Name);
    }

    /// <summary>Only align while plugged in.</summary>
    public bool ChargingOnly
    {
        get => Preferences.Default.Get(ChargingOnlyKey, false);
        set => Preferences.Default.Set(ChargingOnlyKey, value);
    }

    /// <summary>Only align while the screen is off, so it never competes with what the user is doing.</summary>
    public bool ScreenOffOnly
    {
        get => Preferences.Default.Get(ScreenOffOnlyKey, false);
        set => Preferences.Default.Set(ScreenOffOnlyKey, value);
    }

    /// <summary>Pause below this battery percentage. Zero means no limit.</summary>
    public int MinimumBatteryPercent
    {
        get => Preferences.Default.Get(MinimumBatteryKey, 20);
        set => Preferences.Default.Set(MinimumBatteryKey, Math.Clamp(value, 0, 90));
    }
}
