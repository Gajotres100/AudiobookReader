using AudioBookReader.App.Platforms.Android.Alignment;
using AudioBookReader.Core.Alignment;

namespace AudioBookReader.App.Services;

public partial class LiveSyncRunner
{
    /// <summary>
    /// Builds the recognizer if the model is already on the device.
    ///
    /// Never downloads. This starts by itself when a reader opens a book, and pulling thirty
    /// megabytes over someone's mobile data because they opened a book is not a decision to make on
    /// their behalf — the whole-book alignment asks for the model in a place where the user has
    /// clearly chosen to wait for it.
    /// </summary>
    private partial async Task<ITranscriber?> CreateTranscriberAsync(
        CpuBudget budget,
        string? language,
        CancellationToken ct)
    {
        var services = IPlatformApplication.Current?.Services;
        var models = services?.GetService<WhisperModelStore>();

        var model = WhisperModelStore.Tiny;
        if (models is null || !models.IsDownloaded(model)) return null;

        return await Task.Run(
            () => WhisperTranscriber.Create(models.PathFor(model), budget, language ?? "auto"), ct);
    }

    /// <summary>
    /// Paced by temperature alone, not by the preset's duty cycle.
    ///
    /// The duty cycle exists to rein in work that would otherwise run flat out for an hour. This
    /// cannot: it stops once it is two minutes ahead of the listener and waits to be caught up, so
    /// it already spends about half its time idle and costs nothing to leave unthrottled. Applying
    /// the preset's cycle on top would be worse than pointless — on the most cautious setting it
    /// would be given a quarter of the time it needs to keep pace with playback, which is not a
    /// slower read-along but no read-along at all.
    ///
    /// The environment gate is left out for its own reason: "only while charging" and "only while
    /// the screen is off" would switch this off exactly when it is wanted, since the user is
    /// holding the phone and reading.
    /// </summary>
    private partial IWorkThrottle CreateThrottle(CpuBudget budget) =>
        new ThermalAwareThrottle(budget with { DutyCycle = 1f });
}
