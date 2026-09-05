using Android.App;
using Android.Content;
using Android.OS;
using AudioBookReader.App.Services;
using AudioBookReader.Core.Alignment;
using AudioBookReader.Core.Books;
using AudioBookReader.Core.Data;
using AudioBookReader.Core.Models;
using AndroidUri = Android.Net.Uri;

namespace AudioBookReader.App.Platforms.Android.Alignment;

/// <summary>
/// Runs alignment in the foreground so it keeps going with the app in the background and the
/// screen off — which is where it will spend nearly all of its time, since the user starts a book
/// and then listens to it.
/// </summary>
[Service(Exported = false, ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeDataSync)]
public class AlignmentService : Service
{
    public const string ActionStart = "com.ngaic.audiobookreader.ALIGN_START";
    public const string ActionStop = "com.ngaic.audiobookreader.ALIGN_STOP";
    public const string ExtraBookId = "bookId";

    private const string ChannelId = "alignment";
    private const int NotificationId = 1001;

    private CancellationTokenSource? _cancellation;
    private PowerManager.WakeLock? _wakeLock;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == ActionStop)
        {
            _cancellation?.Cancel();
            return StartCommandResult.NotSticky;
        }

        var bookId = intent?.GetIntExtra(ExtraBookId, -1) ?? -1;
        if (bookId < 0)
        {
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        CreateNotificationChannel();
        StartForeground(NotificationId, BuildNotification("Priprema…", 0));

        _cancellation = new CancellationTokenSource();

        // Only the whole-book run lives here. Sync on the fly runs in the app itself, because it
        // only ever works while the reader is on screen and so needs neither a service nor the
        // notification a service is obliged to post.
        //
        // Started through Task.Run because Android calls OnStartCommand on the main looper, and
        // nothing downstream configures its awaits. Without this, every continuation in the whole
        // alignment pipeline resumes on the UI thread — recognition included.
        _ = Task.Run(() => RunAsync(bookId, _cancellation.Token));

        // Not sticky: a book half-aligned when the process died should resume because the user
        // asked again, not because Android silently restarted the service with a stale intent.
        return StartCommandResult.NotSticky;
    }

    private async Task RunAsync(int bookId, CancellationToken ct)
    {
        var services = IPlatformApplication.Current?.Services;
        var queue = services?.GetService<AlignmentQueue>();

        try
        {
            if (services is null)
                throw new InvalidOperationException("The app is not started; nothing to align with.");

            AcquireWakeLock();

            var database = services.GetRequiredService<LibraryDatabase>();
            var syncMaps = services.GetRequiredService<SyncMapStore>();
            var extractors = services.GetRequiredService<BookTextExtractors>();
            var models = services.GetRequiredService<WhisperModelStore>();

            var preferences = services.GetRequiredService<AlignmentSettingsStore>();
            var budget = preferences.Budget;

            var modelPath = await EnsureModelAsync(models, queue, bookId, ct);

            var chapterCount = (await database.GetChaptersAsync(bookId)).Count;

            // Named rather than detected: the book already told us at import, and leaving it to
            // the recognizer costs an extra pass over every single probe.
            var book = await database.GetBookAsync(bookId);

            await using var transcriber = WhisperTranscriber.Create(
                modelPath, budget, book?.Language ?? "auto");

            // Layered deliberately: the duty cycle paces the work, thermal readings can lower it,
            // and the gate holds everything back while the user's conditions are unmet.
            var thermal = new ThermalAwareThrottle(budget);
            var throttle = new EnvironmentGate(preferences, thermal);

            var aligner = new BookAligner(
                database, syncMaps, extractors, transcriber, throttle: throttle, log: AppLog.Info);

            var progress = new Progress<AlignmentProgress>(p => Publish(queue, bookId, p, chapterCount, thermal, throttle));

            AppLog.Info($"book {bookId}: alignment loop starting");

            await aligner.AlignAsync(bookId, progress, ct);

            queue?.Report(new AlignmentStatus(bookId, AlignmentPhase.Finished, "Poravnanje gotovo", 1));
        }
        // Qualified because Android.OS declares a type of the same name.
        catch (System.OperationCanceledException)
        {
            queue?.Report(new AlignmentStatus(bookId, AlignmentPhase.Stopped, "Zaustavljeno — nastavit će odakle je stalo"));
        }
        catch (Exception ex)
        {
            // Logged as well as shown. Reporting only to the screen meant a run that died left no
            // trace anywhere it could be read back off the device, which is exactly the situation
            // in which someone needs to know what happened.
            AppLog.Error($"aligning book {bookId}", ex);
            queue?.Report(new AlignmentStatus(bookId, AlignmentPhase.Failed, ex.Message));
        }
        finally
        {
            ReleaseWakeLock();
            StopForeground(StopForegroundFlags.Remove);
            StopSelf();
        }
    }

    private async Task<string> EnsureModelAsync(
        WhisperModelStore models,
        AlignmentQueue? queue,
        int bookId,
        CancellationToken ct)
    {
        var model = WhisperModelStore.Tiny;
        if (models.IsDownloaded(model)) return models.PathFor(model);

        queue?.Report(new AlignmentStatus(bookId, AlignmentPhase.DownloadingModel, "Skidanje modela…"));

        // Reported per whole percent rather than per chunk. The download hands back progress every
        // 80 KB, which for a 32 MB model is some four hundred updates — each one marshalled to the
        // UI thread and each one rebuilding a notification, which is enough work to make the app
        // feel stuck while it is supposedly just downloading.
        var lastPercent = -1;

        var progress = new Progress<double>(fraction =>
        {
            var percent = (int)(fraction * 100);
            if (percent == lastPercent) return;
            lastPercent = percent;

            var message = $"Skidanje modela… {percent}%";
            queue?.Report(new AlignmentStatus(bookId, AlignmentPhase.DownloadingModel, message, fraction));
            Notify(message, percent);
        });

        return await models.EnsureAsync(model, progress, ct);
    }

    private void Publish(
        AlignmentQueue? queue,
        int bookId,
        AlignmentProgress progress,
        int chapterCount,
        ThermalAwareThrottle thermal,
        EnvironmentGate gate)
    {
        // Progress arrives per probe within a chapter; the overall fraction blends the chapters
        // already done with how far into the current one this is.
        var withinChapter = progress.ProbeCount > 0
            ? progress.ProbesCompleted / (double)progress.ProbeCount
            : 0;

        var fraction = chapterCount > 0
            ? Math.Clamp((progress.ChapterIndex + withinChapter) / chapterCount, 0, 1)
            : 0;

        // The user needs to know why nothing is moving, or a paused run reads as a broken one.
        var message = gate.BlockedReason
            ?? $"Poglavlje {progress.ChapterIndex + 1} od {chapterCount} — {withinChapter:P0}";

        if (gate.BlockedReason is null && thermal.Status >= ThermalStatus.Moderate)
            message += " — usporeno zbog topline";

        queue?.Report(new AlignmentStatus(
            bookId, AlignmentPhase.Aligning, message, fraction,
            progress.ChapterIndex, chapterCount, withinChapter));

        // The bar tracks the chapter, not the book: on forty chapters the book-wide figure moves
        // once every couple of minutes, and a bar that does not move reads as a job that has hung.
        // The overall share is not lost — it goes in the line beneath.
        Notify(message, (int)(withinChapter * 100), (int)(fraction * 100));
    }

    // ---- Notification ----

    private void CreateNotificationChannel()
    {
        var manager = (NotificationManager?)GetSystemService(NotificationService);
        if (manager is null) return;

        // Low importance: this is a progress report the user can glance at, not something worth
        // a sound or a heads-up banner while they are listening to a book.
        var channel = new NotificationChannel(ChannelId, "Poravnanje teksta", NotificationImportance.Low)
        {
            Description = "Napredak uparivanja audioknjige s tekstom",
        };

        manager.CreateNotificationChannel(channel);
    }

    private Notification BuildNotification(string message, int chapterPercent, int bookPercent = -1)
    {
        var stop = PendingIntent.GetService(
            this,
            0,
            new Intent(this, typeof(AlignmentService)).SetAction(ActionStop),
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var builder = new Notification.Builder(this, ChannelId)
            .SetContentTitle("Poravnanje teksta")
            .SetContentText(message)
            // Not the download glyph: nothing is being fetched, and a download icon on a job that
            // runs for the better part of an hour invites the user to wonder what is being sent.
            .SetSmallIcon(global::Android.Resource.Drawable.StatNotifySync)
            .SetProgress(100, chapterPercent, indeterminate: chapterPercent <= 0)
            .SetOngoing(true)
            .SetOnlyAlertOnce(true)
            .AddAction(new Notification.Action.Builder(null, "Zaustavi", stop).Build());

        // A notification carries one bar and no more, so the book-wide figure is written out
        // instead. Two drawn bars would need a custom layout, and those are restyled by every
        // manufacturer's shade until they look like nothing else on the phone.
        if (bookPercent >= 0) builder.SetSubText($"Cijela knjiga {bookPercent} %");

        return builder.Build();
    }

    private int _lastNotifiedPercent = -1;

    private void Notify(string message, int chapterPercent, int bookPercent = -1)
    {
        // Rebuilt only when the number it shows has changed. Every rebuild allocates a PendingIntent
        // and crosses into the system, and this is called once per probe — the same trap the model
        // download already sidesteps a few lines above.
        if (chapterPercent == _lastNotifiedPercent) return;
        _lastNotifiedPercent = chapterPercent;

        var manager = (NotificationManager?)GetSystemService(NotificationService);
        manager?.Notify(NotificationId, BuildNotification(message, chapterPercent, bookPercent));
    }

    // ---- Wake lock ----

    /// <summary>
    /// Keeps the CPU awake for the run. Without it the device sleeps a few seconds after the
    /// screen goes off, which is the state alignment is meant to work in.
    /// </summary>
    private void AcquireWakeLock()
    {
        var power = (PowerManager?)GetSystemService(PowerService);

        _wakeLock = power?.NewWakeLock(WakeLockFlags.Partial, "AudioBookReader:alignment");
        _wakeLock?.Acquire();
    }

    private void ReleaseWakeLock()
    {
        if (_wakeLock?.IsHeld == true) _wakeLock.Release();

        _wakeLock?.Dispose();
        _wakeLock = null;
    }

    public override void OnDestroy()
    {
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        ReleaseWakeLock();
        base.OnDestroy();
    }
}
