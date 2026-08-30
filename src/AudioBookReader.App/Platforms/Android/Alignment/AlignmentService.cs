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

    /// <summary>Measure the passage being listened to, rather than sampling the whole book.</summary>
    public const string ActionFollow = "com.ngaic.audiobookreader.ALIGN_FOLLOW";

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

        _ = intent?.Action == ActionFollow
            ? FollowAsync(bookId, _cancellation.Token)
            : RunAsync(bookId, _cancellation.Token);

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

            await using var transcriber = WhisperTranscriber.Create(modelPath, budget);

            // Layered deliberately: the duty cycle paces the work, thermal readings can lower it,
            // and the gate holds everything back while the user's conditions are unmet.
            var thermal = new ThermalAwareThrottle(budget);
            var throttle = new EnvironmentGate(preferences, thermal);

            var aligner = new BookAligner(
                database, syncMaps, extractors, transcriber, throttle: throttle, log: AppLog.Info);

            var progress = new Progress<AlignmentProgress>(p => Publish(queue, bookId, p, chapterCount, thermal, throttle));

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
            queue?.Report(new AlignmentStatus(bookId, AlignmentPhase.Failed, ex.Message));
        }
        finally
        {
            ReleaseWakeLock();
            StopForeground(StopForegroundFlags.Remove);
            StopSelf();
        }
    }

    /// <summary>
    /// Measures the passage being listened to, for as long as the reader stays open.
    ///
    /// Unlike the whole-book run this has no end of its own — it follows the playhead and is
    /// stopped when the reader closes. The environment gate is deliberately not applied: the user
    /// is holding the phone and reading, so "only while charging" and "only while the screen is
    /// off" would switch the feature off exactly when it is wanted.
    /// </summary>
    private async Task FollowAsync(int bookId, CancellationToken ct)
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
            var playback = services.GetRequiredService<PlaybackController>();
            var budget = services.GetRequiredService<AlignmentSettingsStore>().Budget;

            var book = await database.GetBookAsync(bookId)
                ?? throw new InvalidOperationException($"No book with id {bookId}.");

            if (!book.IsPaired) return;

            var modelPath = await EnsureModelAsync(models, queue, bookId, ct);

            var chapters = await database.GetChaptersAsync(bookId);
            var extracted = await extractors.ExtractAsync(book.EbookPath!, ct);
            var text = extracted.Text.PlainText;

            var map = await syncMaps.LoadAsync(bookId) is { } existing
                      && existing.MatchesPair(book.AudioHash, book.EbookHash)
                ? existing
                : new SyncMap { AudioHash = book.AudioHash, EbookHash = book.EbookHash };

            await using var transcriber = WhisperTranscriber.Create(modelPath, budget);

            var aligner = new LiveAligner(
                TokenizedText.Create(text),
                chapters,
                transcriber,
                text.Length,
                throttle: new ThermalAwareThrottle(budget),
                log: AppLog.Info);

            queue?.Report(new AlignmentStatus(bookId, AlignmentPhase.Following, "Pratim naraciju…"));

            var lastReported = 0L;

            var progress = new Progress<LiveAlignmentProgress>(p =>
            {
                // Only when the second changes: this fires every fifteen seconds of audio, and the
                // notification is the only place it shows.
                var ahead = Math.Max(0, p.AtMs - playback.PositionMs) / 1000;
                if (ahead == lastReported) return;

                lastReported = ahead;

                var message = $"Pratim naraciju — izmjereno {ahead} s unaprijed";
                queue?.Report(new AlignmentStatus(bookId, AlignmentPhase.Following, message));
                Notify(message, percent: 0);
            });

            await aligner.RunAsync(
                book.AudioPath!,
                map,
                () => playback.BookId == bookId ? playback.PositionMs : 0,
                () => syncMaps.SaveAsync(bookId, map),
                progress,
                ct);
        }
        catch (System.OperationCanceledException)
        {
            queue?.Report(new AlignmentStatus(bookId, AlignmentPhase.Stopped, ""));
        }
        catch (Exception ex)
        {
            AppLog.Error("live alignment", ex);
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
            ?? $"Poglavlje {progress.ChapterIndex + 1} od {chapterCount}";

        if (gate.BlockedReason is null && thermal.Status >= ThermalStatus.Moderate)
            message += " — usporeno zbog topline";

        queue?.Report(new AlignmentStatus(
            bookId, AlignmentPhase.Aligning, message, fraction, progress.ChapterIndex, chapterCount));

        Notify(message, (int)(fraction * 100));
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

    private Notification BuildNotification(string message, int percent)
    {
        var stop = PendingIntent.GetService(
            this,
            0,
            new Intent(this, typeof(AlignmentService)).SetAction(ActionStop),
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        return new Notification.Builder(this, ChannelId)
            .SetContentTitle("Poravnanje teksta")
            .SetContentText(message)
            .SetSmallIcon(global::Android.Resource.Drawable.StatSysDownload)
            .SetProgress(100, percent, indeterminate: percent <= 0)
            .SetOngoing(true)
            .SetOnlyAlertOnce(true)
            .AddAction(new Notification.Action.Builder(null, "Zaustavi", stop).Build())
            .Build();
    }

    private void Notify(string message, int percent)
    {
        var manager = (NotificationManager?)GetSystemService(NotificationService);
        manager?.Notify(NotificationId, BuildNotification(message, percent));
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
