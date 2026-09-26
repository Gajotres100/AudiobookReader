using AudioBookReader.App.Resources.Strings;

namespace AudioBookReader.App.Services;

/// <summary>
/// Fetches a book from the server as a plain foreground Task.
///
/// Android needs a foreground service and a wake lock here because a download must outlive the
/// screen going off. iOS has no such concept: a Task just keeps running for as long as the app is
/// active, and the point of a foreground-only compromise is the same one AlignmentQueue.iOS.cs
/// already made for whole-book alignment — this is the "while the app is open" half of the story,
/// not a background-surviving transfer. A true background download (URLSession background
/// sessions, delegate-driven) is future work, not needed for the app to be usable.
/// </summary>
public partial class DownloadQueue
{
    private CancellationTokenSource? _cancellation;

    private partial void StartCore(
        string serverId, string itemId, string title, bool toAppStorage, bool wantAudio, bool wantEbook, int? attachTo)
    {
        _cancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;

        // Task.Run for the reason Android's DownloadService gives: this is called from the UI
        // thread, and without it every continuation in the import — tag reading and hashing a
        // file of hundreds of megabytes included — would resume there and freeze the screen.
        _ = Task.Run(() => RunAsync(serverId, itemId, title, toAppStorage, wantAudio, wantEbook, attachTo, cancellation.Token));
    }

    public partial void Stop() => _cancellation?.Cancel();

    private async Task RunAsync(
        string serverId,
        string itemId,
        string title,
        bool toAppStorage,
        bool wantAudio,
        bool wantEbook,
        int? attachTo,
        CancellationToken ct)
    {
        try
        {
            var servers = IPlatformApplication.Current?.Services.GetService<ServerConnections>()
                          ?? throw new InvalidOperationException("The app is not started; nothing to download with.");

            var server = servers.For(serverId) ?? throw new InvalidOperationException(Strings.Server_Gone);

            if (!server.IsConnected) await server.RestoreAsync();

            var progress = new Progress<ImportProgress>(p =>
            {
                var phase = p.Fraction > 0 ? DownloadPhase.Downloading : DownloadPhase.Importing;
                Report(new DownloadStatus(itemId, title, phase, p.Message, p.Fraction));
            });

            AppLog.Info($"download: '{title}' starting");

            var bookId = await server.ImportAsync(itemId, progress, ct, toAppStorage, wantAudio, wantEbook, attachTo);

            Report(new DownloadStatus(itemId, title, DownloadPhase.Finished, Strings.Server_InLibraryNow, 1, bookId));
            AppLog.Info($"download: '{title}' finished as book {bookId}");
        }
        catch (OperationCanceledException)
        {
            Report(new DownloadStatus(itemId, title, DownloadPhase.Cancelled, Strings.Server_DownloadCancelled));
        }
        catch (Exception ex)
        {
            AppLog.Error($"downloading '{title}'", ex);
            Report(new DownloadStatus(itemId, title, DownloadPhase.Failed, ex.Message));
        }
    }
}
