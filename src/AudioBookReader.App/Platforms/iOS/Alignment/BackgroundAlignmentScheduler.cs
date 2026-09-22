using AudioBookReader.App.Services;
using BackgroundTasks;

namespace AudioBookReader.App.Platforms.iOS.Alignment;

/// <summary>
/// Best-effort continuation of whole-book alignment while the app is backgrounded — decided,
/// deliberately, during the iOS-port plan as part of v1 rather than deferred. This is not
/// Android's equivalent: a foreground service is a promise ("this keeps running"), BGProcessingTask
/// is an opportunity iOS grants or withholds on its own schedule, sometimes not at all if the app
/// is rarely opened. That has to be said honestly rather than sold as parity.
///
/// It is safe precisely because BookAligner.AlignAsync already checkpoints per chapter — a call
/// from here that runs for ninety seconds before the system reclaims the app loses at most the
/// chapter in progress, the same guarantee AlignmentQueue.iOS.cs's own foreground path relies on.
/// </summary>
internal static class BackgroundAlignmentScheduler
{
    public const string TaskIdentifier = "com.gajotres.audiobookreader.alignment";

    /// <summary>Must run before AppDelegate.FinishedLaunching returns — Apple refuses a registration made any later.</summary>
    public static void Register()
    {
        BGTaskScheduler.Shared.Register(TaskIdentifier, null, task => Handle((BGProcessingTask)task));
    }

    /// <summary>Asks iOS for a future opportunity to continue. Call whenever the app backgrounds with a run still in flight, and again from inside the task itself so opportunities keep coming.</summary>
    public static void ScheduleNext()
    {
        var request = new BGProcessingTaskRequest(TaskIdentifier)
        {
            RequiresNetworkConnectivity = false,
            RequiresExternalPower = false,
        };

        if (!BGTaskScheduler.Shared.Submit(request, out var error))
            AppLog.Info($"could not schedule background alignment: {error?.LocalizedDescription}");
    }

    private static void Handle(BGProcessingTask task)
    {
        var cancellation = new CancellationTokenSource();
        task.ExpirationHandler = () => cancellation.Cancel();

        // Scheduled again immediately rather than at the end: if this run itself throws before
        // reaching its own finally, there would otherwise be no next opportunity at all.
        ScheduleNext();

        _ = RunAsync(task, cancellation.Token);
    }

    private static async Task RunAsync(BGProcessingTask task, CancellationToken ct)
    {
        var services = IPlatformApplication.Current?.Services;
        var bookId = Preferences.Default.Get("ios.alignment.lastBookId", -1);

        if (services is null || bookId < 0)
        {
            task.SetTaskCompleted(success: true);
            return;
        }

        var queue = services.GetService<AlignmentQueue>();
        if (queue is null)
        {
            task.SetTaskCompleted(success: true);
            return;
        }

        try
        {
            AppLog.Info($"book {bookId}: alignment resuming in background (BGProcessingTask)");
            await queue.ResumeInBackgroundAsync(bookId, ct);
            task.SetTaskCompleted(success: true);
        }
        catch (Exception ex)
        {
            AppLog.Error($"background alignment for book {bookId}", ex);
            task.SetTaskCompleted(success: false);
        }
    }
}
