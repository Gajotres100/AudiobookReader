using AudioBookReader.App.Platforms.iOS.Playback;

namespace AudioBookReader.App.Services;

public partial class PlaybackController
{
    private static PlaybackService? Service => PlaybackService.Current;

    public partial bool IsReady => Service?.IsReady ?? false;
    public partial bool IsReadyToPlay => Service?.IsReadyToPlay ?? false;
    public partial bool IsPlaying => Service?.IsPlaying ?? false;
    public partial long PositionMs => Service?.PositionMs ?? 0;
    public partial long DurationMs => Service?.DurationMs ?? 0;
    public partial float Speed => Service?.Speed ?? 1f;
    public partial TimeSpan? SleepRemaining => Service?.SleepRemaining;
    public partial string? LastError => Service?.LastError;

    /// <summary>
    /// Builds the singleton if it is not there yet. Unlike Android there is no service to start and
    /// wait on — but it is built on the main thread, because its timers only ever fire on the main
    /// run loop: built from a pool thread, they would be scheduled on a loop nobody runs, and the
    /// position would silently never be saved.
    /// </summary>
    public partial async Task<bool> ConnectAsync(CancellationToken ct)
    {
        if (Service is not null) return true;

        await MainThread.InvokeOnMainThreadAsync(PlaybackService.EnsureRunning);
        return Service is not null;
    }

    private partial void LoadCore(
        int bookId,
        string audioPath,
        long startMs,
        float speed,
        string? title,
        string? author,
        string? coverPath,
        long[]? chapterStarts) =>
        Service?.Load(audioPath, startMs, speed, title, author, coverPath, chapterStarts, bookId);

    public partial void Play() => Service?.Play();
    public partial void Pause() => Service?.Pause();
    public partial void SeekTo(long positionMs) => Service?.SeekTo(positionMs);
    public partial void Nudge(long deltaMs) => Service?.Nudge(deltaMs);
    public partial void SetSpeed(float speed) => Service?.SetSpeed(speed);

    // No CarPlay browsing in v1 — see CarConnection.iOS.cs.
    public partial void NotifyLibraryChanged() { }

    public partial void SleepAfter(TimeSpan delay) => Service?.SleepAfter(delay);
    public partial void SleepAtPosition(long positionMs) => Service?.SleepAtPosition(positionMs);
    public partial void CancelSleep() => Service?.CancelSleep();
}
