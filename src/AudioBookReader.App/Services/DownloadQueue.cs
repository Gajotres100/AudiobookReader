using AudioBookReader.App.Resources.Strings;

namespace AudioBookReader.App.Services;

public enum DownloadPhase
{
    Idle,

    /// <summary>Asked for, but the background service has not said anything yet.</summary>
    Starting,

    Downloading,

    /// <summary>The bytes are here; the book is being read and added to the library.</summary>
    Importing,

    Finished,

    /// <summary>Called off by the user. Nothing half-written is left behind.</summary>
    Cancelled,

    Failed,
}

/// <param name="ItemId">The book on the server, so a page can tell whether this run is about it.</param>
/// <param name="Fraction">How far through the file being fetched, 0..1.</param>
public record DownloadStatus(
    string ItemId = "",
    string Title = "",
    DownloadPhase Phase = DownloadPhase.Idle,
    string Message = "",
    double Fraction = 0,
    int? BookId = null)
{
    public bool IsRunning =>
        Phase is DownloadPhase.Starting or DownloadPhase.Downloading or DownloadPhase.Importing;

    /// <summary>The figure people actually watch, as a whole percent.</summary>
    public string Percent => Phase == DownloadPhase.Downloading ? Fraction.ToString("P0") : "";
}

/// <summary>
/// The one place that knows whether a book is coming down from the server and how far along it is.
///
/// The same shape as <see cref="AlignmentQueue"/> and for the same reason: the work runs in a
/// foreground service Android constructs on its own, while the page that started it comes and goes.
/// Status lives here so the notification and whatever screen happens to be open read the same
/// object, and so a page reopened halfway through sees the current state at once.
///
/// It also fixes the reason downloads used to die: the transfer was a plain task owned by a view
/// model, so locking the screen backgrounded the app and the system suspended it a minute or two
/// later. A download of three hundred megabytes over a phone connection has to outlive the screen.
/// </summary>
public partial class DownloadQueue
{
    private DownloadStatus _status = new();

    public event EventHandler<DownloadStatus>? Changed;

    public DownloadStatus Status => _status;

    public void Report(DownloadStatus status)
    {
        _status = status;
        Changed?.Invoke(this, status);
    }

    /// <summary>Starts fetching a book in a foreground service.</summary>
    /// <returns>False when one is already running, since only one runs at a time.</returns>
    /// <param name="wantAudio">Whether the narration is among what is missing here.</param>
    /// <param name="wantEbook">Whether the text is.</param>
    /// <param name="attachTo">The library entry this completes, when it completes one.</param>
    /// <param name="serverId">The server the book is on — not necessarily the one shown when it finishes.</param>
    public bool Start(
        string serverId,
        string itemId,
        string title,
        bool toAppStorage,
        bool wantAudio = true,
        bool wantEbook = true,
        int? attachTo = null)
    {
        if (Status.IsRunning) return false;

        // Reported before the service is even asked for. Bringing one up, resolving its
        // dependencies and opening a connection take a few seconds during which nothing would
        // otherwise change on screen, and a button that visibly does nothing reads as a hang.
        Report(new DownloadStatus(itemId, title, DownloadPhase.Starting, Strings.Download_Preparing));

        StartCore(serverId, itemId, title, toAppStorage, wantAudio, wantEbook, attachTo);
        return true;
    }

    private partial void StartCore(
        string serverId, string itemId, string title, bool toAppStorage, bool wantAudio, bool wantEbook, int? attachTo);

    /// <summary>Asks the running download to stop. What it had written is deleted.</summary>
    public partial void Stop();
}
