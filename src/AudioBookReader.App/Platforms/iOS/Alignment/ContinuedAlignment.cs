using AudioBookReader.App.Resources.Strings;
using AudioBookReader.App.Services;
using BackgroundTasks;

namespace AudioBookReader.App.Platforms.iOS.Alignment;

/// <summary>
/// Lets a whole-book alignment the user started carry on after they leave the app — iOS 26's
/// continued processing task, the nearest thing iOS has to Android's foreground service.
///
/// Before it, a run only advanced while the app was on screen: the moment the phone was locked the
/// process was frozen, and <see cref="BackgroundAlignmentScheduler"/>'s processing task is an
/// opportunity iOS hands out on its own schedule, typically overnight on a charger. This one is
/// granted for work a person just asked for, and the system shows its progress while it runs —
/// which is also how they can stop it without opening the app.
///
/// The run itself is untouched: this only covers the one already started by
/// <see cref="AlignmentQueue"/>, mirrors its progress, and ends when it does. Older iOS versions
/// simply never submit, and keep the behaviour they had.
/// </summary>
internal static class ContinuedAlignment
{
    public const string TaskIdentifier = "com.gajotres.audiobookreader.alignment.continued";

    private static bool _registered;

    /// <summary>Must run at launch, with the other task registrations.</summary>
    public static void Register()
    {
        if (!OperatingSystem.IsIOSVersionAtLeast(26)) return;

        _registered = BGTaskScheduler.Shared.Register(
            TaskIdentifier, null, task => Handle((BGContinuedProcessingTask)task));
    }

    /// <summary>Asks to keep the run going in the background. Called as the user starts it, while the app is on screen — iOS accepts the request only then.</summary>
    public static void Submit(string subtitle)
    {
        if (!OperatingSystem.IsIOSVersionAtLeast(26) || !_registered) return;

        var request = new BGContinuedProcessingTaskRequest(TaskIdentifier, Strings.Notification_AlignChannel, subtitle)
        {
            // Now or not at all. Queued, it could begin long after this run had ended, and there
            // would be nothing for it to cover.
            Strategy = BGContinuedProcessingTaskRequestSubmissionStrategy.Fail,
        };

        if (!BGTaskScheduler.Shared.Submit(request, out var error))
            AppLog.Info($"could not continue alignment in the background: {error?.LocalizedDescription}");
    }

    private static void Handle(BGContinuedProcessingTask task)
    {
        if (!OperatingSystem.IsIOSVersionAtLeast(26)) return;

        var queue = IPlatformApplication.Current?.Services.GetService<AlignmentQueue>();

        if (queue is null || !queue.Status.IsRunning)
        {
            task.SetTaskCompleted(success: true);
            return;
        }

        const long units = 1_000;
        task.Progress.TotalUnitCount = units;

        var finished = 0;

        void Finish(bool success)
        {
            if (Interlocked.Exchange(ref finished, 1) != 0) return;

            queue.Changed -= OnChanged;
            task.SetTaskCompleted(success);
        }

        // Kept current, not decorative: the system ends a continued task whose progress stops
        // moving, taking it for one that has hung.
        void OnChanged(object? sender, AlignmentStatus status)
        {
            if (!status.IsRunning)
            {
                Finish(status.Phase == AlignmentPhase.Finished);
                return;
            }

            task.Progress.CompletedUnitCount = (long)(Math.Clamp(status.Fraction, 0, 1) * units);
            task.UpdateTitle(Strings.Notification_AlignChannel, status.Message);
        }

        task.ExpirationHandler = () =>
        {
            // The system is reclaiming the time, or the person stopped it from the progress sheet.
            // In the background that ends the run too — stopped, it saves where it got to rather
            // than being frozen in the middle of a probe. On screen it simply carries on as before.
            if (!AppForeground.IsActive) queue.Stop();
            Finish(success: false);
        };

        queue.Changed += OnChanged;
        OnChanged(queue, queue.Status);

        AppLog.Info("alignment continuing in the background (continued processing task)");
    }
}
