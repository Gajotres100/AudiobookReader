using AudioBookReader.App.Resources.Strings;
namespace AudioBookReader.App.Services;

public enum AlignmentPhase
{
    Idle,

    /// <summary>Requested, but the background service has not reported anything yet.</summary>
    Starting,

    DownloadingModel,
    Aligning,
    Finished,

    /// <summary>Stopped by the user or by the system; whatever was aligned stays usable.</summary>
    Stopped,

    Failed,
}

/// <param name="Fraction">How far through the whole book, 0..1.</param>
/// <param name="ChapterFraction">
/// How far through the chapter being worked on. Shown alongside the book-wide figure because on a
/// forty-chapter book the overall bar moves once every couple of minutes, which is indistinguishable
/// from nothing happening.
/// </param>
public record AlignmentStatus(
    int? BookId = null,
    AlignmentPhase Phase = AlignmentPhase.Idle,
    string Message = "",
    double Fraction = 0,
    int ChapterIndex = 0,
    int ChapterCount = 0,
    double ChapterFraction = 0)
{
    public bool IsRunning =>
        Phase is AlignmentPhase.Starting or AlignmentPhase.DownloadingModel or AlignmentPhase.Aligning;
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

        // Pairing a book starts alignment on its own, so this is reached even where the switches
        // are hidden. Said once, rather than started and failed.
        if (!SpeechSupport.IsAvailable)
        {
            Report(new AlignmentStatus(bookId, AlignmentPhase.Failed, Strings.Details_AlignmentUnsupported));
            return;
        }

        // Reported before the service is even asked to start. Bringing up a service, resolving its
        // dependencies and opening a download all take a few seconds during which nothing would
        // otherwise change on screen — and a button that visibly does nothing reads as a hang.
        Report(new AlignmentStatus(bookId, AlignmentPhase.Starting, Strings.Progress_StartingAlignment));

        StartCore(bookId);
    }

    private partial void StartCore(int bookId);

    /// <summary>Asks the running alignment to stop. Progress so far is kept.</summary>
    public partial void Stop();

    /// <summary>
    /// Stops a run and waits for the service to actually confirm it, rather than firing the stop
    /// intent and hoping.
    ///
    /// <see cref="Stop"/> only sends the intent — Android can take a moment to tear the foreground
    /// service down, and a caller about to delete the very map that run is checkpointing to needs
    /// the writing to have actually stopped, not just been asked to. Capped rather than unbounded:
    /// a service that never reports back must not leave deletion stuck forever.
    /// </summary>
    public async Task StopAndWaitAsync(CancellationToken ct = default)
    {
        if (!Status.IsRunning) return;

        var stopped = new TaskCompletionSource();
        void OnChanged(object? sender, AlignmentStatus status)
        {
            if (!status.IsRunning) stopped.TrySetResult();
        }

        Changed += OnChanged;
        try
        {
            Stop();
            await Task.WhenAny(stopped.Task, Task.Delay(TimeSpan.FromSeconds(5), ct));
        }
        finally
        {
            Changed -= OnChanged;
        }
    }
}
