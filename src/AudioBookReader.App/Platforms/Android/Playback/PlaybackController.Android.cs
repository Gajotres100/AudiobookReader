using Android.Content;
using AudioBookReader.App.Platforms.Android.Playback;

namespace AudioBookReader.App.Services;

public partial class PlaybackController
{
    private static PlaybackService? Service => PlaybackService.Current;

    public partial bool IsReady => Service is not null;
    public partial bool IsReadyToPlay => Service?.IsReadyToPlay ?? false;
    public partial bool IsPlaying => Service?.IsPlaying ?? false;
    public partial long PositionMs => Service?.PositionMs ?? 0;
    public partial long DurationMs => Service?.DurationMs ?? 0;
    public partial float Speed => Service?.Speed ?? 1f;
    public partial TimeSpan? SleepRemaining => Service?.SleepRemaining;
    public partial string? LastError => Service?.LastError;

    /// <summary>
    /// Starts the service and waits for it to publish itself.
    ///
    /// The service runs in this process, so the app talks to it directly rather than through a
    /// MediaController. That costs nothing here — the MediaSession is still what drives the lock
    /// screen, the notification and headset buttons — and avoids a round trip through a session
    /// connection for calls that are a method away.
    ///
    /// Started with StartService, never StartForegroundService. Since Android 14 a service begun
    /// the foreground way must call startForeground() within seconds or the system kills the whole
    /// process — and Media3 only goes foreground once something is actually playing. Opening a book
    /// without pressing play would therefore have been fatal. Media3 promotes the service itself
    /// when playback starts, which is the moment a notification is warranted anyway.
    /// </summary>
    public partial async Task<bool> ConnectAsync(CancellationToken ct)
    {
        if (Service is not null) return true;

        var context = global::Android.App.Application.Context;
        context.StartService(new Intent(context, typeof(PlaybackService)));

        // Service creation is a message on the main looper, so it is quick but not synchronous.
        for (var attempt = 0; attempt < 40 && Service is null; attempt++)
            await Task.Delay(50, ct);

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

    public partial void SleepAfter(TimeSpan delay) => Service?.SleepAfter(delay);
    public partial void SleepAtPosition(long positionMs) => Service?.SleepAtPosition(positionMs);
    public partial void CancelSleep() => Service?.CancelSleep();
}
