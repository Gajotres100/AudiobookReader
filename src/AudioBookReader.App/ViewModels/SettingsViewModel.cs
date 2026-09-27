using System.Collections.ObjectModel;
using AudioBookReader.App.Services;
using AudioBookReader.App.Resources.Strings;
using AudioBookReader.Core.Alignment;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AudioBookReader.App.ViewModels;

/// <summary>One CPU preset as the settings list shows it.</summary>
public class BudgetOption(CpuBudget budget, bool isSelected)
{
    public CpuBudget Budget { get; } = budget;

    public string Name { get; } = budget.Id switch
    {
        "eco" => Strings.Budget_Thrifty,
        "balanced" => Strings.Budget_Balanced,
        _ => Strings.Budget_Fast,
    };

    public bool IsSelected { get; } = isSelected;

    // Deliberately no time estimate here: EstimateAlignmentTime only knows the reference book's
    // length, not the one actually being aligned, and depends on the device staying cool and idle
    // the whole time. Showing a number a real run routinely misses reads as a broken promise
    // rather than a preset description.

    public string Detail { get; } = budget.Id switch
    {
        "eco" => Strings.Budget_ThriftyDetail,
        "balanced" => Strings.Budget_BalancedDetail,
        _ => Strings.Budget_FastDetail,
    };
}

/// <summary>One language the app can speak, as the setting lists it.</summary>
public record LanguageOption(string Code, string Name);

/// <summary>One speech model, as the setting offers it.</summary>
/// <param name="Id">Stored, so it survives a change of wording or language.</param>
public record RecognitionOption(string Id, string Name, string Detail, bool IsSelected);

/// <summary>
/// One probe spacing for aligning a book in advance, as the setting offers it.
/// </summary>
/// <param name="IntervalMs">
/// Stored directly rather than an id: the four spacings are the setting, with nothing about the
/// wording that could change independently of the number itself.
/// </param>
public record ProbeSpacingOption(long IntervalMs, string Name, string Detail, bool IsSelected);

public partial class SettingsViewModel(
    AlignmentSettingsStore settings,
    Language language,
    ServerConnections servers) : ObservableObject
{
    public ObservableCollection<BudgetOption> Budgets { get; } = [];

    /// <summary>
    /// The languages on offer.
    ///
    /// Each named in itself rather than in the language currently showing — somebody who has landed
    /// in the wrong one needs to recognise their own, and "Croatian" is no help to a person looking
    /// for "Hrvatski".
    /// </summary>
    public IReadOnlyList<LanguageOption> Languages { get; } =
    [
        new("", Strings.Settings_LanguageSystem),
        new("hr", "Hrvatski"),
        new("en", "English"),
    ];

    /// <summary>
    /// The chosen language. Setting it rebuilds the screens, so this view model is on its way out
    /// as it returns — which is why nothing is done after the call.
    /// </summary>
    public LanguageOption? SelectedLanguage
    {
        get => Languages.FirstOrDefault(l => l.Code == language.Current) ?? Languages[0];
        set
        {
            if (value is not null) language.Set(value.Code);
        }
    }

    [ObservableProperty]
    public partial string SelectedBudgetName { get; set; } = "";

    /// <summary>The two speech models, with the cost of the better one said out loud.</summary>
    public ObservableCollection<RecognitionOption> Recognitions { get; } = [];

    /// <summary>
    /// The four probe spacings aligning a book in advance can use, with the cost of each said out
    /// loud rather than hidden the way the CPU presets are: combining a dense spacing with the
    /// slowest preset can now take longer than the book itself, which someone choosing between
    /// them has to be able to see coming.
    /// </summary>
    public ObservableCollection<ProbeSpacingOption> ProbeSpacings { get; } = [];

    /// <summary>
    /// Ask the model when each word was spoken.
    ///
    /// A setting rather than a constant because the feature is experimental upstream and its cost
    /// has not been measured on every device. If it slows a phone down, this turns it off without
    /// waiting for a new build.
    /// </summary>
    public bool WordTimestamps
    {
        get => settings.WordTimestamps;
        set
        {
            if (value == settings.WordTimestamps) return;

            settings.WordTimestamps = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Detailed following diagnostics in the log; off unless something needs explaining.</summary>
    [ObservableProperty]
    public partial bool VerboseLog { get; set; }

    [ObservableProperty]
    public partial bool ChargingOnly { get; set; }

    [ObservableProperty]
    public partial bool ScreenOffOnly { get; set; }

    [ObservableProperty]
    public partial double MinimumBatteryPercent { get; set; }

    public string MinimumBatteryText => MinimumBatteryPercent <= 0
        ? Strings.Settings_NoBatteryLimit
        : string.Format(Strings.Settings_PauseBelow, MinimumBatteryPercent.ToString("0"));

    /// <summary>Whether the setting below is worth showing at all — nothing to jump to otherwise.</summary>
    [ObservableProperty]
    public partial bool HasServer { get; set; }

    [ObservableProperty]
    public partial bool OpenServerOnStart { get; set; }

    partial void OnOpenServerOnStartChanged(bool value) => servers.OpenServerOnStart = value;

    public void Load()
    {
        HasServer = servers.IsConfigured;
        OpenServerOnStart = servers.OpenServerOnStart;

        var current = settings.Budget;

        Budgets.Clear();
        foreach (var preset in CpuBudget.Presets)
            Budgets.Add(new BudgetOption(preset, preset.Id == current.Id));

        SelectedBudgetName = current.Id;

        Recognitions.Clear();
        Recognitions.Add(new RecognitionOption(
            "tiny", Strings.Recognition_Tiny, Strings.Recognition_TinyDetail, settings.Recognition != "base"));
        Recognitions.Add(new RecognitionOption(
            "base", Strings.Recognition_Base, Strings.Recognition_BaseDetail, settings.Recognition == "base"));

        var currentInterval = settings.ProbeIntervalMs;

        ProbeSpacings.Clear();
        ProbeSpacings.Add(new ProbeSpacingOption(
            60_000, Strings.Spacing_60, Strings.Spacing_60Detail, currentInterval == 60_000));
        ProbeSpacings.Add(new ProbeSpacingOption(
            45_000, Strings.Spacing_45, Strings.Spacing_45Detail, currentInterval == 45_000));
        ProbeSpacings.Add(new ProbeSpacingOption(
            30_000, Strings.Spacing_30, Strings.Spacing_30Detail, currentInterval == 30_000));
        ProbeSpacings.Add(new ProbeSpacingOption(
            15_000, Strings.Spacing_15, Strings.Spacing_15Detail, currentInterval == 15_000));

        OnPropertyChanged(nameof(WordTimestamps));
        VerboseLog = settings.VerboseLog;
        ChargingOnly = settings.ChargingOnly;
        ScreenOffOnly = settings.ScreenOffOnly;
        MinimumBatteryPercent = settings.MinimumBatteryPercent;
    }

    public void Select(BudgetOption option)
    {
        settings.Budget = option.Budget;
        Load();
    }

    /// <summary>
    /// Changes the model. Books already aligned keep their maps — this only decides what listens
    /// to the next one.
    /// </summary>
    public void Select(RecognitionOption option)
    {
        settings.Recognition = option.Id;
        Load();
    }

    public void Select(ProbeSpacingOption option)
    {
        settings.ProbeIntervalMs = option.IntervalMs;
        Load();
    }

    partial void OnVerboseLogChanged(bool value) => settings.VerboseLog = value;

    /// <summary>
    /// Hands the log to the share sheet, as one file with the rolled-over half first so it reads in
    /// order. Copied rather than shared in place: the log is appended to while the app runs, and a
    /// receiving app reading it mid-write would get a torn last line at best.
    /// </summary>
    /// <summary>
    /// Where other devices can send an alignment to this one — for typing in on an iPhone or iPad,
    /// which cannot always find it on the network by itself. Read each time the page is built,
    /// since the address changes with the network.
    /// </summary>
    public string ShareAddressText =>
        AlignmentShare.LocalAddresses() is { Count: > 0 } addresses
            ? string.Format(Strings.Settings_ShareAddress, string.Join(", ", addresses))
            : Strings.Settings_ShareNoAddress;

    [RelayCommand]
    private async Task ShareLogAsync()
    {
        try
        {
            var copy = Path.Combine(FileSystem.CacheDirectory, "syncbook-log.txt");

            await using (var output = File.Create(copy))
            {
                foreach (var part in new[] { AppPaths.Log + ".old", AppPaths.Log })
                {
                    if (!File.Exists(part)) continue;

                    await using var input = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    await input.CopyToAsync(output);
                }
            }

            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = Strings.Settings_ShareLog,
                File = new ShareFile(copy, "text/plain"),
            });
        }
        catch (Exception ex)
        {
            AppLog.Error("sharing the log", ex);
        }
    }

    partial void OnChargingOnlyChanged(bool value) => settings.ChargingOnly = value;

    partial void OnScreenOffOnlyChanged(bool value) => settings.ScreenOffOnly = value;

    partial void OnMinimumBatteryPercentChanged(double value)
    {
        settings.MinimumBatteryPercent = (int)value;
        OnPropertyChanged(nameof(MinimumBatteryText));
    }

    // ---- Help and feedback ----

    /// <summary>
    /// Where the app and the version are, said plainly, because a bug report without them is a
    /// guess.
    /// </summary>
    public string VersionText => string.Format(Strings.Settings_VersionLine, AppInfo.VersionString);

    /// <summary>
    /// Opens a report with the version, phone and Android version already in it.
    ///
    /// The three things every report needs and nobody wants to type: the person reporting knows
    /// what went wrong, not which build they are on. Sent as query parameters that match the field
    /// ids in the issue form, which GitHub fills in for them.
    ///
    /// Deliberately the issue tracker and not an email address: a report in an inbox is a
    /// conversation with one person that nobody else can find, search, or add to.
    /// </summary>
    [RelayCommand]
    private Task ReportProblemAsync() => OpenFormAsync("bug.yml", withDetails: true);

    [RelayCommand]
    private Task SuggestIdeaAsync() => OpenFormAsync("idea.yml", withDetails: false);

    private static async Task OpenFormAsync(string template, bool withDetails)
    {
        var url = $"https://github.com/Gajotres100/syncbook/issues/new?template={template}";

        if (withDetails)
        {
            url += $"&version={Uri.EscapeDataString(AppInfo.VersionString)}"
                + $"&device={Uri.EscapeDataString($"{DeviceInfo.Manufacturer} {DeviceInfo.Model}")}"
                + $"&android={Uri.EscapeDataString($"Android {DeviceInfo.VersionString}")}";
        }

        try
        {
            await Browser.Default.OpenAsync(url, BrowserLaunchMode.SystemPreferred);
        }
        catch (Exception ex)
        {
            // A phone with no browser at all, which is rare enough to be worth a line in the log
            // rather than a dialog explaining something nobody can act on.
            AppLog.Error("opening the issue form", ex);
        }
    }
}
