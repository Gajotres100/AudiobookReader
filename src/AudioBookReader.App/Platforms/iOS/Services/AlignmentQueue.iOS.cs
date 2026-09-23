using AudioBookReader.App.Platforms.iOS.Alignment;
using AudioBookReader.App.Resources.Strings;
using AudioBookReader.Core.Alignment;
using AudioBookReader.Core.Books;
using AudioBookReader.Core.Data;
using Foundation;

namespace AudioBookReader.App.Services;

/// <summary>
/// Runs whole-book alignment as a plain foreground Task, while the app is open — the v1 iOS
/// compromise decided during the iOS-port plan. Android's AlignmentService is a foreground service
/// that keeps working for the better part of an hour with the screen off; iOS has no equivalent
/// promise a managed app can make. This class covers the "app is open" half of the story cleanly;
/// the "app is backgrounded" half is a BGProcessingTask calling the same BookAligner.AlignAsync,
/// which is safe to do because BookAligner already checkpoints per chapter regardless of who calls
/// it or for how long.
///
/// No notification, no wake lock: neither concept exists here, and a foregrounded app needs
/// neither — the OS does not suspend a process the user is looking at.
/// </summary>
public partial class AlignmentQueue
{
    /// <summary>
    /// Which book a BGProcessingTask should resume, when iOS wakes the app in the background —
    /// there is no UI context to ask at that point, so this is the only way it can know. Set when
    /// a run genuinely begins and cleared on every terminal state (finished, stopped, failed) so a
    /// run the user cancelled, or one that broke, is never silently resumed behind their back.
    /// </summary>
    internal const string LastBookKey = "ios.alignment.lastBookId";

    private CancellationTokenSource? _cancellation;
    private Task? _running;

    private partial void StartCore(int bookId)
    {
        Preferences.Default.Set(LastBookKey, bookId);

        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;

        var previous = _running;

        _running = Task.Run(async () =>
        {
            if (previous is not null) { try { await previous; } catch { } }
            await RunAsync(bookId, cancellation.Token);
        });
    }

    public partial void Stop() => _cancellation?.Cancel();

    /// <summary>
    /// The entry point a BGProcessingTask calls to continue a run that was in flight when the app
    /// was last backgrounded — see BackgroundAlignmentScheduler.cs. Same method StartCore uses,
    /// under a name that says why something outside this class is allowed to call it.
    /// </summary>
    internal async Task ResumeInBackgroundAsync(int bookId, CancellationToken ct)
    {
        // A run started in the foreground is not cancelled when the app is backgrounded — it is
        // frozen, and thaws the moment iOS wakes the app for this very task. Starting a second run
        // then would load two whisper models and have both writing the same map, the exact overlap
        // Android's service took a generation counter to prevent. So an existing run is allowed to
        // carry on in this window instead, and stopped when the window closes, so it checkpoints
        // rather than being frozen mid-probe again.
        if (_running is { IsCompleted: false } running)
        {
            await using (ct.Register(() => _cancellation?.Cancel()))
            {
                try { await running; } catch { }
            }

            return;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _cancellation = cancellation;

        var run = RunAsync(bookId, cancellation.Token);
        _running = run;
        await run;
    }

    private async Task RunAsync(int bookId, CancellationToken ct)
    {
        var services = IPlatformApplication.Current?.Services;

        try
        {
            if (services is null)
                throw new InvalidOperationException("The app is not started; nothing to align with.");

            var database = services.GetRequiredService<LibraryDatabase>();
            var syncMaps = services.GetRequiredService<SyncMapStore>();
            var extractors = services.GetRequiredService<BookTextExtractors>();
            var models = services.GetRequiredService<WhisperModelStore>();

            var preferences = services.GetRequiredService<AlignmentSettingsStore>();
            var budget = preferences.Budget;

            var modelPath = await EnsureModelAsync(models, preferences, bookId, ct);

            var chapterCount = (await database.GetChaptersAsync(bookId)).Count;
            var book = await database.GetBookAsync(bookId);

            await using var transcriber = WhisperTranscriber.Create(
                modelPath, budget, book?.Language ?? "auto", preferences.WordTimestamps);

            var thermal = new ThermalAwareThrottle(budget);
            var throttle = new EnvironmentGate(preferences, thermal);

            var schedule = new AlignmentSettings { ProbeIntervalMs = preferences.ProbeIntervalMs };

            var aligner = new BookAligner(
                database, syncMaps, extractors, transcriber, schedule, throttle: throttle, log: AppLog.Info);

            var progress = new Progress<AlignmentProgress>(p => Publish(bookId, p, chapterCount, thermal, throttle));

            AppLog.Info($"book {bookId}: alignment loop starting (foreground)");

            await aligner.AlignAsync(bookId, progress, ct);

            Report(new AlignmentStatus(bookId, AlignmentPhase.Finished, Strings.Progress_Finished, 1));
        }
        catch (OperationCanceledException)
        {
            Report(new AlignmentStatus(bookId, AlignmentPhase.Stopped, Strings.Progress_Stopped));
        }
        catch (Exception ex)
        {
            AppLog.Error($"aligning book {bookId}", ex);
            Report(new AlignmentStatus(bookId, AlignmentPhase.Failed, ex.Message));
        }
        finally
        {
            // Only clears the entry this run itself owns — a newer run's Start may already have
            // overwritten it with a different book by the time this one's cleanup runs.
            if (Preferences.Default.Get(LastBookKey, -1) == bookId) Preferences.Default.Remove(LastBookKey);
        }
    }

    private async Task<string> EnsureModelAsync(
        WhisperModelStore models, AlignmentSettingsStore preferences, int bookId, CancellationToken ct)
    {
        var model = preferences.Model;
        if (models.IsDownloaded(model)) return models.PathFor(model);

        Report(new AlignmentStatus(bookId, AlignmentPhase.DownloadingModel, Strings.Progress_DownloadingModel));

        var lastPercent = -1;

        var progress = new Progress<double>(fraction =>
        {
            var percent = (int)(fraction * 100);
            if (percent == lastPercent) return;
            lastPercent = percent;

            var message = string.Format(Strings.Progress_DownloadingModelPercent, percent);
            Report(new AlignmentStatus(bookId, AlignmentPhase.DownloadingModel, message, fraction));
        });

        return await models.EnsureAsync(model, progress, ct);
    }

    private void Publish(
        int bookId, AlignmentProgress progress, int chapterCount, ThermalAwareThrottle thermal, EnvironmentGate gate)
    {
        var withinChapter = progress.ProbeCount > 0
            ? progress.ProbesCompleted / (double)progress.ProbeCount
            : 0;

        var fraction = chapterCount > 0
            ? Math.Clamp((progress.ChapterIndex + withinChapter) / chapterCount, 0, 1)
            : 0;

        var message = gate.BlockedReason
            ?? (chapterCount <= 1
                ? string.Format(Strings.Progress_WholeBook, withinChapter.ToString("P0"))
                : string.Format(
                    Strings.Progress_ChapterOf, progress.ChapterIndex + 1, chapterCount, withinChapter.ToString("P0")));

        if (gate.BlockedReason is null && thermal.Status >= NSProcessInfoThermalState.Serious)
            message += Strings.Progress_ThermalSlowed;

        Report(new AlignmentStatus(
            bookId, AlignmentPhase.Aligning, message, fraction, progress.ChapterIndex, chapterCount, withinChapter));
    }
}
