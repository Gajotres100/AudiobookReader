using System.Collections.ObjectModel;
using AudioBookReader.App.Services;
using AudioBookReader.Core.Alignment;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AudioBookReader.App.ViewModels;

/// <summary>One CPU preset as the settings list shows it.</summary>
public class BudgetOption(CpuBudget budget, bool isSelected)
{
    /// <summary>A ten-hour audiobook, so the presets can be compared on something concrete.</summary>
    private const long ReferenceBookMs = 10 * 60 * 60 * 1000L;

    public CpuBudget Budget { get; } = budget;

    public string Name { get; } = budget.Name;

    public bool IsSelected { get; } = isSelected;

    public string Estimate { get; } = Describe(budget.EstimateAlignmentTime(ReferenceBookMs));

    public string Detail { get; } = budget.Name switch
    {
        "Štedljivo" => "Male jezgre, 25% vremena. Telefon se praktički ne primijeti.",
        "Uravnoteženo" => "Male jezgre, 50% vremena. Radi u pozadini dok normalno koristiš telefon.",
        _ => "Sve jezgre, bez pauza. Najbolje uz 'samo dok puni' i ugašen ekran.",
    };

    private static string Describe(TimeSpan estimate) =>
        estimate.TotalHours >= 1
            ? $"~{estimate.TotalHours:0.#} h za 10-satnu knjigu"
            : $"~{estimate.TotalMinutes:0} min za 10-satnu knjigu";
}

public partial class SettingsViewModel(AlignmentSettingsStore settings) : ObservableObject
{
    public ObservableCollection<BudgetOption> Budgets { get; } = [];

    [ObservableProperty]
    public partial string SelectedBudgetName { get; set; } = "";

    [ObservableProperty]
    public partial bool ChargingOnly { get; set; }

    [ObservableProperty]
    public partial bool ScreenOffOnly { get; set; }

    [ObservableProperty]
    public partial double MinimumBatteryPercent { get; set; }

    public string MinimumBatteryText => MinimumBatteryPercent <= 0
        ? "Bez ograničenja baterije"
        : $"Pauziraj ispod {MinimumBatteryPercent:0}% baterije";

    public void Load()
    {
        var current = settings.Budget;

        Budgets.Clear();
        foreach (var preset in CpuBudget.Presets)
            Budgets.Add(new BudgetOption(preset, preset.Name == current.Name));

        SelectedBudgetName = current.Name;
        ChargingOnly = settings.ChargingOnly;
        ScreenOffOnly = settings.ScreenOffOnly;
        MinimumBatteryPercent = settings.MinimumBatteryPercent;
    }

    public void Select(BudgetOption option)
    {
        settings.Budget = option.Budget;
        Load();
    }

    partial void OnChargingOnlyChanged(bool value) => settings.ChargingOnly = value;

    partial void OnScreenOffOnlyChanged(bool value) => settings.ScreenOffOnly = value;

    partial void OnMinimumBatteryPercentChanged(double value)
    {
        settings.MinimumBatteryPercent = (int)value;
        OnPropertyChanged(nameof(MinimumBatteryText));
    }
}
