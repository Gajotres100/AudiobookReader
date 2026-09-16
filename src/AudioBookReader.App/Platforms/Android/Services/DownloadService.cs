using Android.App;
using Android.Content;
using Android.OS;
using AudioBookReader.App.Resources.Strings;
using AudioBookReader.App.Services;
using DownloadStatus = AudioBookReader.App.Services.DownloadStatus;

namespace AudioBookReader.App.Platforms.Android.Services;

/// <summary>
/// Fetches a book from the server in the foreground, so it keeps going with the app in the
/// background and the screen off.
///
/// It used to be a plain task inside the page that started it, which meant locking the phone
/// backgrounded the app and the system suspended the transfer a minute or two later — leaving a
/// half-written file and a page that still claimed to be downloading. An audiobook is hundreds of
/// megabytes over a phone connection; nobody watches that happen.
///
/// The notification is not decoration either. A foreground service is obliged to post one, and it
/// is the only place the progress can be seen once the app is minimised, which is where it will be.
/// </summary>
[Service(Exported = false, ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeDataSync)]
public class DownloadService : Service
{
    public const string ActionStart = "com.gajotres.audiobookreader.DOWNLOAD_START";
    public const string ActionStop = "com.gajotres.audiobookreader.DOWNLOAD_STOP";
    public const string ExtraItemId = "itemId";
    public const string ExtraTitle = "title";
    public const string ExtraAppStorage = "appStorage";
    public const string ExtraWantAudio = "wantAudio";
    public const string ExtraWantEbook = "wantEbook";

    /// <summary>The library entry a missing half joins, or 0 when this is a whole new book.</summary>
    public const string ExtraAttachTo = "attachTo";

    private const string ChannelId = "downloads";

    // Its own id, so a download and an alignment can both be showing at once — which they can, and
    // sharing one would have each quietly replace the other's notification.
    private const int NotificationId = 1002;

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

        var itemId = intent?.GetStringExtra(ExtraItemId);
        var title = intent?.GetStringExtra(ExtraTitle) ?? "";
        var toAppStorage = intent?.GetBooleanExtra(ExtraAppStorage, false) ?? false;
        var wantAudio = intent?.GetBooleanExtra(ExtraWantAudio, true) ?? true;
        var wantEbook = intent?.GetBooleanExtra(ExtraWantEbook, true) ?? true;
        var attachTo = intent?.GetIntExtra(ExtraAttachTo, 0) ?? 0;

        if (string.IsNullOrEmpty(itemId))
        {
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        CreateNotificationChannel();
        StartForeground(NotificationId, BuildNotification(title, Strings.Download_Preparing, -1));

        _cancellation = new CancellationTokenSource();

        // Android calls this on the main looper and nothing downstream configures its awaits, so
        // without Task.Run every continuation in the import — tag reading and hashing included —
        // would resume on the UI thread.
        _ = Task.Run(() => RunAsync(
            itemId, title, toAppStorage, wantAudio, wantEbook, attachTo, _cancellation.Token));

        // Not sticky: a transfer killed with the process should resume because someone asked again,
        // not because Android replayed a stale intent at a file that is no longer there.
        return StartCommandResult.NotSticky;
    }

    private async Task RunAsync(
        string itemId,
        string title,
        bool toAppStorage,
        bool wantAudio,
        bool wantEbook,
        int attachTo,
        CancellationToken ct)
    {
        var services = IPlatformApplication.Current?.Services;
        var queue = services?.GetService<DownloadQueue>();

        try
        {
            if (services is null)
                throw new InvalidOperationException("The app is not started; nothing to download with.");

            AcquireWakeLock();

            var server = services.GetRequiredService<ServerConnection>();

            // Restored rather than assumed: the service can be brought up by the system with no
            // page having run, and then the client holds no address and no token.
            if (!server.IsConnected) await server.RestoreAsync();

            var progress = new Progress<ImportProgress>(p => Publish(queue, itemId, title, p));

            AppLog.Info($"download: '{title}' starting");

            var bookId = await server.ImportAsync(
                itemId, progress, ct, toAppStorage, wantAudio, wantEbook, attachTo > 0 ? attachTo : null);

            queue?.Report(new DownloadStatus(
                itemId, title, DownloadPhase.Finished, Strings.Server_InLibraryNow, 1, bookId));

            AppLog.Info($"download: '{title}' finished as book {bookId}");
        }
        // Qualified because Android.OS declares a type of the same name.
        catch (System.OperationCanceledException)
        {
            queue?.Report(new DownloadStatus(
                itemId, title, DownloadPhase.Cancelled, Strings.Server_DownloadCancelled));
        }
        catch (Exception ex)
        {
            // Logged as well as shown: a transfer that dies while the phone is in a pocket leaves
            // no other trace, and that is exactly when someone needs to know what happened.
            AppLog.Error($"downloading '{title}'", ex);
            queue?.Report(new DownloadStatus(itemId, title, DownloadPhase.Failed, ex.Message));
        }
        finally
        {
            ReleaseWakeLock();
            StopForeground(StopForegroundFlags.Remove);
            StopSelf();
        }
    }

    private void Publish(DownloadQueue? queue, string itemId, string title, ImportProgress progress)
    {
        // Zero means the step does not know how far along it is — reading tags, hashing, writing
        // the library row. Those are the tail of the job, not the transfer, so the bar goes
        // indeterminate rather than dropping back to the start.
        var phase = progress.Fraction > 0 ? DownloadPhase.Downloading : DownloadPhase.Importing;

        queue?.Report(new DownloadStatus(itemId, title, phase, progress.Message, progress.Fraction));

        Notify(title, progress.Message, phase == DownloadPhase.Downloading ? (int)(progress.Fraction * 100) : -1);
    }

    // ---- Notification ----

    private void CreateNotificationChannel()
    {
        var manager = (NotificationManager?)GetSystemService(NotificationService);
        if (manager is null) return;

        // Low importance: a progress report to glance at, not something worth a sound or a banner
        // over whatever the user is doing while it runs.
        var channel = new NotificationChannel(
            ChannelId, Strings.Notification_DownloadChannel, NotificationImportance.Low)
        {
            Description = Strings.Notification_DownloadChannelBody,
        };

        manager.CreateNotificationChannel(channel);
    }

    private Notification BuildNotification(string title, string message, int percent)
    {
        var stop = PendingIntent.GetService(
            this,
            0,
            new Intent(this, typeof(DownloadService)).SetAction(ActionStop),
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var builder = new Notification.Builder(this, ChannelId)
            .SetContentTitle(title.Length > 0 ? title : Strings.Notification_DownloadChannel)
            .SetContentText(message)
            .SetSmallIcon(global::Android.Resource.Drawable.StatSysDownload)
            .SetProgress(100, Math.Max(percent, 0), indeterminate: percent < 0)
            .SetOngoing(true)
            .SetOnlyAlertOnce(true)
            .AddAction(new Notification.Action.Builder(null, Strings.Common_Stop, stop).Build());

        // The percent as a number as well as a bar. A bar alone answers "is it moving"; on a job
        // that runs for twenty minutes the question is "how much longer", and that needs a figure.
        if (percent >= 0) builder.SetSubText($"{percent} %");

        return builder.Build();
    }

    private int _lastNotifiedPercent = -2;

    private void Notify(string title, string message, int percent)
    {
        // Only when the number changes. Progress arrives every 128 KB, which for a three hundred
        // megabyte book is a couple of thousand updates, and each rebuild allocates a PendingIntent
        // and crosses into the system.
        if (percent == _lastNotifiedPercent) return;
        _lastNotifiedPercent = percent;

        var manager = (NotificationManager?)GetSystemService(NotificationService);
        manager?.Notify(NotificationId, BuildNotification(title, message, percent));
    }

    // ---- Wake lock ----

    /// <summary>
    /// Keeps the CPU awake for the transfer. Without it the device sleeps a few seconds after the
    /// screen goes off, and a long download is exactly the job that has to outlive that.
    /// </summary>
    private void AcquireWakeLock()
    {
        var power = (PowerManager?)GetSystemService(PowerService);

        _wakeLock = power?.NewWakeLock(WakeLockFlags.Partial, "AudioBookReader:download");
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
