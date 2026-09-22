using AudioBookReader.App.Platforms.iOS.Alignment;
using AudioBookReader.Core.Alignment;

namespace AudioBookReader.App.Services;

public partial class LiveSyncRunner
{
    /// <summary>Builds the recognizer if the model is already on the device. Never downloads — see the Android version's doc comment for why.</summary>
    private partial async Task<ITranscriber?> CreateTranscriberAsync(
        CpuBudget budget, string? language, CancellationToken ct)
    {
        var services = IPlatformApplication.Current?.Services;
        var models = services?.GetService<WhisperModelStore>();
        var preferences = services?.GetService<AlignmentSettingsStore>();

        if (models is null) return null;

        var model = Downloaded(models, preferences?.Model ?? WhisperModelStore.Tiny)
            ?? Downloaded(models, WhisperModelStore.Tiny)
            ?? Downloaded(models, WhisperModelStore.Base);

        if (model is null) return null;

        var wordTimes = preferences?.WordTimestamps ?? true;

        return await Task.Run(
            () => WhisperTranscriber.Create(models.PathFor(model), budget, language ?? "auto", wordTimes), ct);
    }

    private static WhisperModel? Downloaded(WhisperModelStore models, WhisperModel model) =>
        models.IsDownloaded(model) ? model : null;

    /// <summary>Paced by temperature alone, not by the preset's duty cycle — see the Android version's doc comment for why.</summary>
    private partial IWorkThrottle CreateThrottle(CpuBudget budget) =>
        new ThermalAwareThrottle(budget with { DutyCycle = 1f });
}
