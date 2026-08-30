namespace AudioBookReader.App.Services;

public enum AlignmentPhase
{
    Idle,

    /// <summary>Requested, but the background service has not reported anything yet.</summary>
    Starting,

    DownloadingModel,
    Aligning,

    /// <summary>Measuring the passage being listened to, as it is listened to.</summary>
    Following,

    Finished,

    /// <summary>Stopped by the user or by the system; whatever was aligned stays usable.</summary>
    Stopped,

    Failed,
}

public record AlignmentStatus(
    int? BookId = null,
    AlignmentPhase Phase = AlignmentPhase.Idle,
    string Message = "",
    double Fraction = 0,
    int ChapterIndex = 0,
    int ChapterCount = 0)
{
    public bool IsRunning =>
        Phase is AlignmentPhase.Starting or AlignmentPhase.DownloadingModel
            or AlignmentPhase.Aligning or AlignmentPhase.Following;

    /// <summary>Following is bound to the reader being open, so it is stopped differently.</summary>
    public bool IsFollowing => Phase is AlignmentPhase.Following;
}

/// <summary>
/// The one place that knows whether alignment is running and how far along it is.
///
/// It sits between a foreground service that Android constructs on its own and pages that come and
/// go, which is why status lives here rather than in either: the notification and whatever screen
/// happens to be open are both reading the same object, and a page opened halfway through a run
/// sees the current state immediately instead of waiting for the next update.
/// </summary>
public partial class AlignmentQueue
{
    private AlignmentStatus _status = new();

    public event EventHandler<AlignmentStatus>? Changed;

    public AlignmentStatus Status => _status;

    public void Report(AlignmentStatus status)
    {
        _status = status;
        Changed?.Invoke(this, status);
    }

    /// <summary>Begins aligning a book in a foreground service, or does nothing if one is running.</summary>
    public void Start(int bookId)
    {
        if (Status.IsRunning) return;

        // Reported before the service is even asked to start. Bringing up a service, resolving its
        // dependencies and opening a download all take a few seconds during which nothing would
        // otherwise change on screen — and a button that visibly does nothing reads as a hang.
        Report(new AlignmentStatus(bookId, AlignmentPhase.Starting, "Pokrećem poravnanje…"));

        StartCore(bookId);
    }

    private partial void StartCore(int bookId);

    /// <summary>
    /// Begins measuring the passage being listened to, for as long as the reader stays open.
    ///
    /// Silent about failure on purpose: this starts by itself when a reader opens a book, so a
    /// device that cannot do it — no model downloaded yet, a whole-book run already going — should
    /// leave the reader working as it always did rather than putting an error in front of someone
    /// who asked for nothing.
    /// </summary>
    public void StartFollowing(int bookId)
    {
        if (Status.IsRunning) return;

        StartFollowingCore(bookId);
    }

    private partial void StartFollowingCore(int bookId);

    /// <summary>Asks the running alignment to stop. Progress so far is kept.</summary>
    public partial void Stop();
}
