using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Media3.Common;
using AndroidX.Media3.ExoPlayer;
using AndroidX.Media3.Session;

namespace AudioBookReader.App.Platforms.Android.Playback;

/// <summary>
/// Hosts the player.
///
/// A <see cref="MediaSessionService"/> rather than a plain service because that is what makes
/// playback behave the way people expect from an audiobook app: it survives the app going to the
/// background, publishes transport controls to the lock screen and notification shade, and
/// responds to headset and Bluetooth buttons — none of which have to be written here.
/// </summary>
[Service(Exported = true, ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeMediaPlayback)]
[IntentFilter(["androidx.media3.session.MediaSessionService"])]
public class PlaybackService : MediaSessionService
{
    private IExoPlayer? _player;
    private MediaSession? _session;
    private SleepTimer? _sleepTimer;

    /// <summary>The running instance, so the app can arm the sleep timer without another binding.</summary>
    public static PlaybackService? Current { get; private set; }

    public override void OnCreate()
    {
        base.OnCreate();

        // The seek increments are what put usable buttons on the lock screen: Media3 builds its
        // notification from the commands the player advertises, and a player with no seek
        // increments advertises nothing to skip with.
        var builder = new ExoPlayerBuilder(this);
        builder.SetSeekBackIncrementMs(10_000);
        builder.SetSeekForwardIncrementMs(10_000);

        _player = builder.Build()!;

        // Speed changes must not turn the narrator into a chipmunk. The second argument is pitch:
        // holding it at 1 while speed rises is what keeps the voice natural, and an audiobook
        // player is unusable without that.
        _player.PlaybackParameters = new PlaybackParameters(1f, 1f);

        // Holds a partial wake lock while playing, and only while playing.
        //
        // Without it the CPU is free to enter deep sleep once the screen goes off, and playback
        // simply stops a few minutes in — which is precisely how an audiobook is listened to. The
        // player takes the lock when playback starts and drops it when it ends, so an idle app
        // costs nothing.
        _player.SetWakeMode(C.WakeModeLocal);

        // Pause rather than play on into a room when the headphones come out.
        _player.SetHandleAudioBecomingNoisy(true);

        _session = new MediaSession.Builder(this, _player).Build();
        _sleepTimer = new SleepTimer(_player);

        Current = this;
    }

    public override MediaSession? OnGetSession(MediaSession.ControllerInfo? controllerInfo) => _session;

    /// <summary>
    /// Stops the service when the user dismisses the app without anything playing, rather than
    /// leaving an idle notification behind.
    /// </summary>
    public override void OnTaskRemoved(Intent? rootIntent)
    {
        if (_player is { PlayWhenReady: false } or null) StopSelf();
        base.OnTaskRemoved(rootIntent);
    }

    // ---- Transport ----

    public bool IsPlaying => _player?.IsPlaying ?? false;

    public long PositionMs => _player?.CurrentPosition ?? 0;

    /// <summary>Track length, or 0 before anything is loaded. Media3 reports an unset duration as a sentinel.</summary>
    public long DurationMs => _player?.Duration is { } duration && duration > 0 ? duration : 0;

    public float Speed => _player?.PlaybackParameters?.Speed ?? 1f;

    public string? LoadedPath { get; private set; }

    public void Load(string audioPath, long startMs, float speed)
    {
        if (_player is null) return;

        if (LoadedPath != audioPath)
        {
            // A book is either kept in app storage or referenced where the user has it, so the
            // location is either a path or a content URI and only the first needs wrapping.
            var uri = audioPath.StartsWith("content://", StringComparison.OrdinalIgnoreCase)
                ? global::Android.Net.Uri.Parse(audioPath)!
                : global::Android.Net.Uri.FromFile(new Java.IO.File(audioPath))!;

            _player.SetMediaItem(MediaItem.FromUri(uri));
            _player.Prepare();
            LoadedPath = audioPath;
        }

        SetSpeed(speed);
        _player.SeekTo(startMs);
    }

    public void Play() => _player?.Play();

    public void Pause() => _player?.Pause();

    public void SeekTo(long positionMs) =>
        _player?.SeekTo(Math.Clamp(positionMs, 0, DurationMs > 0 ? DurationMs : long.MaxValue));

    /// <summary>Jumps forward or back, clamped to the track. Used by the ±10 second controls.</summary>
    public void Nudge(long deltaMs) => SeekTo(PositionMs + deltaMs);

    public void SetSpeed(float speed)
    {
        if (_player is null) return;

        // Pitch held at 1 regardless of speed, so the narrator still sounds like themselves.
        _player.PlaybackParameters = new PlaybackParameters(Math.Clamp(speed, 0.5f, 2f), 1f);
    }

    // ---- Sleep timer ----

    /// <summary>Pauses after a wall-clock delay, fading out first.</summary>
    public void SleepAfter(TimeSpan delay) => _sleepTimer?.ArmForDelay(delay);

    /// <summary>Pauses when playback reaches a position — used for "until the end of this chapter".</summary>
    public void SleepAtPosition(long positionMs) => _sleepTimer?.ArmForPosition(positionMs);

    public void CancelSleep() => _sleepTimer?.Cancel();

    public TimeSpan? SleepRemaining => _sleepTimer?.Remaining;

    public override void OnDestroy()
    {
        _sleepTimer?.Cancel();

        _session?.Release();
        _session = null;

        _player?.Release();
        _player = null;

        Current = null;
        base.OnDestroy();
    }
}

/// <summary>
/// Pauses playback at a chosen moment, fading down first.
///
/// This lives in the service rather than in a view model on purpose: the whole point of a sleep
/// timer is that it fires with the screen off and the app in the background, which is exactly when
/// a page's timer would have been torn down.
///
/// The fade matters more than it sounds. Cutting narration mid-word is jarring enough to wake
/// someone who was nearly asleep, which defeats the feature.
/// </summary>
internal sealed class SleepTimer(IExoPlayer player)
{
    private static readonly TimeSpan FadeDuration = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly Handler _handler = new(Looper.MainLooper!);

    /// <summary>Time left in countdown mode. Counts down only while the book is playing.</summary>
    private TimeSpan? _remaining;

    /// <summary>Target position in end-of-chapter mode.</summary>
    private long? _stopAtPositionMs;

    private float _volumeBeforeFade = 1f;

    /// <summary>
    /// The exact runnable that was posted. Held because RemoveCallbacks matches on object
    /// identity — passing a freshly built equivalent cancels nothing and leaves the timer running.
    /// </summary>
    private Java.Lang.Runnable? _scheduled;

    public TimeSpan? Remaining => _remaining ?? PositionRemaining();

    public void ArmForDelay(TimeSpan delay)
    {
        Cancel();
        _remaining = delay;
        Start();
    }

    public void ArmForPosition(long positionMs)
    {
        Cancel();
        _stopAtPositionMs = positionMs;
        Start();
    }

    public void Cancel()
    {
        if (_scheduled is not null)
        {
            _handler.RemoveCallbacks(_scheduled);
            _scheduled.Dispose();
            _scheduled = null;
        }

        _remaining = null;
        _stopAtPositionMs = null;

        player.Volume = _volumeBeforeFade;
    }

    private void Start()
    {
        _volumeBeforeFade = player.Volume;
        Schedule();
    }

    private void Schedule()
    {
        _scheduled?.Dispose();
        _scheduled = new Java.Lang.Runnable(Tick);

        _handler.PostDelayed(_scheduled, (long)PollInterval.TotalMilliseconds);
    }

    private void Tick()
    {
        // A paused book is not being listened to, so the timer waits rather than running down.
        // Otherwise setting a timer and pausing to answer the door would silently expire it, and
        // "stop after 30 minutes" would mean 30 minutes of wall clock instead of 30 minutes of
        // book — which is never what the person setting it meant.
        if (!player.IsPlaying)
        {
            player.Volume = _volumeBeforeFade;
            Schedule();
            return;
        }

        if (_remaining is { } left) _remaining = left - PollInterval;

        var untilStop = Remaining;

        if (untilStop is null)
        {
            Cancel();
            return;
        }

        if (untilStop <= TimeSpan.Zero)
        {
            player.Pause();
            Cancel();
            return;
        }

        // Ride the volume down over the last few seconds.
        player.Volume = untilStop < FadeDuration
            ? _volumeBeforeFade * (float)(untilStop.Value.TotalSeconds / FadeDuration.TotalSeconds)
            : _volumeBeforeFade;

        Schedule();
    }

    private TimeSpan? PositionRemaining()
    {
        if (_stopAtPositionMs is not { } target) return null;

        // Real time left, not track time: at 1.5x the last two minutes of a chapter arrive in
        // eighty seconds, and the fade has to start when it sounds right, not when the timeline
        // says so.
        var speed = Math.Max(player.PlaybackParameters?.Speed ?? 1f, 0.1f);
        return TimeSpan.FromMilliseconds((target - player.CurrentPosition) / speed);
    }
}
