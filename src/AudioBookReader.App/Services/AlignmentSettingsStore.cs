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
    private const string VerboseLogKey = "diagnostics.verbose";
    private const string RecognitionKey = "alignment.recognition";
    private const string WordTimesKey = "alignment.wordTimes";
    private const string ProbeIntervalKey = "alignment.probeIntervalMs";

    /// <summary>
    /// The spacings this can actually be set to, from sparsest to densest. A stored value outside
    /// this list — left over from a build that offered a different set — falls back to the default
    /// rather than aligning at a spacing nobody chose and nothing here can show a name for.
    /// </summary>
    public static readonly long[] ProbeIntervalOptionsMs = [60_000, 45_000, 30_000, 15_000];

    /// <summary>
    /// Write the detailed following diagnostics to the log.
    ///
    /// Off by default because they fire several times a second for the whole time a book is open.
    /// Exposed at all because the alternative — gating them behind a build flag — means the one
    /// person who can reproduce a following problem is the one person who cannot record it.
    /// </summary>
    public bool VerboseLog
    {
        get => Preferences.Default.Get(VerboseLogKey, false);
        set
        {
            Preferences.Default.Set(VerboseLogKey, value);
            AppLog.Verbose = value;
        }
    }
    // How a book gets its read-along — sampled in advance, or measured as it is read — is not
    // here. It belongs to the book, is chosen on the book's own page, and is stored with it.

    public CpuBudget Budget
    {
        get
        {
            var id = Preferences.Default.Get(BudgetKey, CpuBudget.Balanced.Id);

            // Older versions stored the Croatian preset name. Anyone upgrading has one of those
            // written down, and losing their choice over a rename would be a poor trade.
            id = id switch
            {
                "Štedljivo" => CpuBudget.Eco.Id,
                "Uravnoteženo" => CpuBudget.Balanced.Id,
                "Brzo" => CpuBudget.Turbo.Id,
                _ => id,
            };

            return CpuBudget.Presets.FirstOrDefault(p => p.Id == id) ?? CpuBudget.Balanced;
        }
        set => Preferences.Default.Set(BudgetKey, value.Id);
    }

    /// <summary>
    /// Which speech model to align with: "tiny" or "base".
    ///
    /// An id rather than a name, for the same reason the CPU preset uses one — it is written into
    /// the settings and has to survive both a change of wording and a change of language.
    /// </summary>
    public string Recognition
    {
        get => Preferences.Default.Get(RecognitionKey, "tiny");
        set => Preferences.Default.Set(RecognitionKey, value == "base" ? "base" : "tiny");
    }

    public WhisperModel Model => WhisperModelStore.ById(Recognition);

    /// <summary>
    /// Spacing between probes when a book is aligned ahead of time, in milliseconds.
    ///
    /// Only that path. Reading along while reading (<see cref="LiveSyncRunner"/>) measures the
    /// passage under the eye as it is read and always uses its own fixed spacing regardless of
    /// this setting — the two answer different questions ("how far ahead is the whole book
    /// mapped" against "is this paragraph right now"), and retuning one must not retune the other.
    /// </summary>
    public long ProbeIntervalMs
    {
        get
        {
            var stored = Preferences.Default.Get(ProbeIntervalKey, 60_000L);
            return Array.IndexOf(ProbeIntervalOptionsMs, stored) >= 0 ? stored : 60_000L;
        }
        set => Preferences.Default.Set(
            ProbeIntervalKey, Array.IndexOf(ProbeIntervalOptionsMs, value) >= 0 ? value : 60_000L);
    }

    /// <summary>
    /// Ask the model when each word was spoken, rather than assuming a constant rate across the
    /// few seconds a segment covers.
    ///
    /// On by default because the assumption is wrong wherever a narrator pauses mid-sentence, and
    /// that error lands on every anchor taken from the middle of a segment. Exposed because the
    /// feature is marked experimental upstream and its cost has not been timed on every device —
    /// if it turns out to slow a phone down materially, this is how it gets turned off without a
    /// new build.
    /// </summary>
    public bool WordTimestamps
    {
        get => Preferences.Default.Get(WordTimesKey, true);
        set => Preferences.Default.Set(WordTimesKey, value);
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
