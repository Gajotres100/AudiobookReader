using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Media3.Common;
using AndroidX.Media3.ExoPlayer;
using AndroidX.Media3.Session;
using AudioBookReader.App.Services;

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

        // Tapping the lock-screen controls opens the app rather than doing nothing. Without a
        // session activity the system has nowhere to send the tap, which makes the controls feel
        // like a dead widget belonging to no app.
        var open = PendingIntent.GetActivity(
            this,
            0,
            PackageManager?.GetLaunchIntentForPackage(PackageName!)
                ?.AddFlags(ActivityFlags.SingleTop),
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        // The session sees a player that can move between chapters; the app keeps using the real
        // one directly. Wrapping only for the session is deliberate — everything the wrapper
        // changes is about what the outside world may ask for, and nothing inside the app goes
        // through it.
        var session = new MediaSession.Builder(this, new ChapterNavigation(_player, () => _chapterStarts));
        if (open is not null) session.SetSessionActivity(open);

        _session = session.Build();

        // Registered with the service, not merely built.
        //
        // This is what was missing, and it is not obvious: OnGetSession is consulted only when an
        // external MediaController connects, and this app never makes one — it holds the service
        // directly and calls methods on it. So the session existed, played audio perfectly, and was
        // never handed to the service's notification manager, which is the thing that posts the
        // media notification and publishes the session to the system. The evidence was that the
        // app owned only two notification channels: Media3 creates its own the first time it posts
        // one, and it had never posted.
        //
        // Without it there are no lock-screen controls, nothing in the shade while listening, and
        // no response to headset buttons — because as far as the system is concerned there is no
        // media session at all.
        AddSession(_session);
        _sleepTimer = new SleepTimer(_player);

        Current = this;

        PublishState();
    }

    public override MediaSession? OnGetSession(MediaSession.ControllerInfo? controllerInfo) => _session;

    /// <summary>Where each chapter begins, for the buttons outside the app to move between them.</summary>
    private long[] _chapterStarts = [];

    /// <summary>
    /// A player that can step between chapters, for everything that controls playback from outside.
    ///
    /// The lock screen, the shade, headset buttons and a car all offer previous and next depending
    /// on what the player says it can do — and an audiobook is a single long file, so ExoPlayer
    /// offers "previous" (which restarts the file) and no "next" at all. That asymmetry is exactly
    /// what showed on the lock screen: one button where there should be two.
    ///
    /// A chapter is the right unit for those buttons in an audiobook, so this reports both commands
    /// as available and maps them onto chapter boundaries — the same behaviour the buttons inside
    /// the app already have, which is what makes the two sets of controls agree.
    /// </summary>
    private sealed class ChapterNavigation(IPlayer inner, Func<long[]> chapters) : ForwardingPlayer(inner)
    {
        /// <summary>
        /// Past this far into a chapter, "previous" means the start of this one rather than the one
        /// before. Every music player has trained people on that, and it is what the app's own
        /// button does.
        /// </summary>
        private const long RestartWithinMs = 3_000;

        public override PlayerCommands? AvailableCommands => Chapters().Length > 1
            ? base.AvailableCommands?.BuildUpon()
                ?.Add(InterfaceConsts.CommandSeekToNext)
                ?.Add(InterfaceConsts.CommandSeekToPrevious)
                ?.Build()
            : base.AvailableCommands;

        /// <summary>
        /// Answered here as well as through the command set: ForwardingPlayer passes this straight
        /// to the player underneath, which would go on denying the very commands added above.
        /// </summary>
        public override bool IsCommandAvailable(int command)
        {
            if (Chapters().Length > 1
                && command is InterfaceConsts.CommandSeekToNext or InterfaceConsts.CommandSeekToPrevious)
            {
                return true;
            }

            return base.IsCommandAvailable(command);
        }

        public override void SeekToNext()
        {
            var starts = Chapters();
            if (starts.Length == 0)
            {
                base.SeekToNext();
                return;
            }

            var at = CurrentPosition;
            var next = starts.FirstOrDefault(s => s > at, -1);

            // Past the last chapter there is nowhere to go but the end, which is what stopping at
            // the end of a book looks like.
            SeekTo(next >= 0 ? next : Duration);
        }

        public override void SeekToPrevious()
        {
            var starts = Chapters();
            if (starts.Length == 0)
            {
                base.SeekToPrevious();
                return;
            }

            var at = CurrentPosition;
            var current = starts.LastOrDefault(s => s <= at, 0);

            SeekTo(at - current > RestartWithinMs
                ? current
                : starts.LastOrDefault(s => s < current, 0));
        }

        private long[] Chapters()
        {
            try
            {
                return chapters();
            }
            catch (Exception)
            {
                // Asked from whichever thread the system happens to use. A book being swapped
                // underneath is not worth failing a button press over.
                return [];
            }
        }
    }

    /// <summary>
    /// Stops the service when the user dismisses the app without anything playing, rather than
    /// leaving an idle notification behind.
    /// </summary>
    public override void OnTaskRemoved(Intent? rootIntent)
    {
        if (_player is { PlayWhenReady: false } or null) StopSelf();
        base.OnTaskRemoved(rootIntent);
    }

    // ---- State, readable from any thread ----

    /// <summary>
    /// ExoPlayer may only be touched from the thread that built it — every getter included, and it
    /// throws rather than tolerating it. So the state is copied onto plain fields on that thread and
    /// everyone else reads the copy.
    ///
    /// This is not a nicety. Sync on the fly asks where playback is from a pool thread on every
    /// window; reading the player directly threw <c>IllegalStateException</c> on the first such
    /// question after a book was loaded, which killed the whole run. The reader kept following the
    /// handful of anchors already written and then simply stopped — no error the user could see,
    /// just text that stops moving after half a page.
    /// </summary>
    private readonly Handler _playerThread = new(Looper.MainLooper!);

    private long _statePositionMs;
    private long _stateDurationMs;
    private volatile bool _stateIsPlaying;
    private volatile float _stateSpeed = 1f;

    private static bool OnPlayerThread => Looper.MyLooper() == Looper.MainLooper;

    private int _lastPlaybackState = -1;
    private string? _lastErrorMessage;

    /// <summary>The last thing the player refused to do, or null. Read by the app to say so.</summary>
    public string? LastError { get; private set; }

    /// <summary>Copies the player's state out, and schedules the next copy. Player thread only.</summary>
    private void PublishState()
    {
        if (_player is null) return;

        // Nothing was watching the player. ExoPlayer answers a source it cannot open by going to
        // an error state and staying there — no exception, no callback anybody had registered — so
        // pressing play did nothing at all and left no trace of why. A file moved, a content URI
        // whose permission did not survive a reboot, a codec the device lacks: all of them looked
        // identical from outside, which is to say like a broken button.
        if (_player.PlaybackState != _lastPlaybackState)
        {
            _lastPlaybackState = _player.PlaybackState;

            AppLog.Info(
                $"player: state {Describe(_lastPlaybackState)}, playWhenReady {_player.PlayWhenReady}, " +
                $"duration {_player.Duration} ms");
        }

        if (_player.PlayerError is { } failure)
        {
            var message = $"{failure.ErrorCodeName}: {failure.Message}";

            if (message != _lastErrorMessage)
            {
                _lastErrorMessage = message;
                LastError = failure.ErrorCodeName;

                AppLog.Info($"player FAILED: {message}");
                if (failure.Cause is { } cause) AppLog.Info($"player   caused by {cause}");
            }
        }
        else
        {
            _lastErrorMessage = null;
            LastError = null;
        }

        Interlocked.Exchange(ref _statePositionMs, _player.CurrentPosition);
        Interlocked.Exchange(ref _stateDurationMs, _player.Duration is var d && d > 0 ? d : 0);
        _stateIsPlaying = _player.IsPlaying;
        _stateSpeed = _player.PlaybackParameters?.Speed ?? 1f;

        // Five times a second: fast enough that a scrubber does not visibly lag and that alignment
        // never works from a stale playhead, cheap enough to leave running while a book plays.
        _playerThread.PostDelayed(PublishState, 200);
    }

    /// <summary>True once the player has buffered and is sitting on the position it was sent to.</summary>
    public bool IsReadyToPlay =>
        OnPlayerThread ? _player?.PlaybackState == 3 : _lastPlaybackState == 3;

    private static string Describe(int state) => state switch
    {
        1 => "idle",
        2 => "buffering",
        3 => "ready",
        4 => "ended",
        _ => state.ToString(),
    };

    /// <summary>
    /// Puts a player that has failed back into a state where it can try again.
    ///
    /// ExoPlayer latches an error: once a source has failed, every later play is ignored until the
    /// player is prepared again. Without this, one failure — a file briefly unreadable, a permission
    /// not yet regranted — made the button dead for the life of the process.
    /// </summary>
    public void Retry() => OnPlayer(() =>
    {
        if (_player?.PlayerError is null) return;

        AppLog.Info("player: preparing again after an error");
        _player.Prepare();
    });

    /// <summary>Runs a player call on the player's own thread, immediately when already there.</summary>
    private void OnPlayer(Action work)
    {
        if (OnPlayerThread) work();
        else _playerThread.Post(work);
    }

    // ---- Transport ----

    public bool IsPlaying => OnPlayerThread ? _player?.IsPlaying ?? false : _stateIsPlaying;

    public long PositionMs =>
        OnPlayerThread ? _player?.CurrentPosition ?? 0 : Interlocked.Read(ref _statePositionMs);

    /// <summary>Track length, or 0 before anything is loaded. Media3 reports an unset duration as a sentinel.</summary>
    public long DurationMs =>
        OnPlayerThread
            ? _player?.Duration is { } duration && duration > 0 ? duration : 0
            : Interlocked.Read(ref _stateDurationMs);

    public float Speed => OnPlayerThread ? _player?.PlaybackParameters?.Speed ?? 1f : _stateSpeed;

    public string? LoadedPath { get; private set; }

    /// <param name="title">Shown on the lock screen and in the shade. The file name is a poor stand-in.</param>
    /// <param name="chapterStarts">
    /// Where each chapter begins. What the previous and next buttons outside the app move between:
    /// without it they have nothing to step through, and a single-file audiobook offers no next at
    /// all.
    /// </param>
    public void Load(
        string audioPath,
        long startMs,
        float speed,
        string? title = null,
        string? author = null,
        string? coverPath = null,
        long[]? chapterStarts = null)
    {
        if (!OnPlayerThread)
        {
            _playerThread.Post(() => Load(audioPath, startMs, speed, title, author, coverPath, chapterStarts));
            return;
        }

        if (_player is null) return;

        _chapterStarts = chapterStarts ?? [];

        if (LoadedPath != audioPath)
        {
            // A book is either kept in app storage or referenced where the user has it, so the
            // location is either a path or a content URI and only the first needs wrapping.
            var uri = audioPath.StartsWith("content://", StringComparison.OrdinalIgnoreCase)
                ? global::Android.Net.Uri.Parse(audioPath)!
                : global::Android.Net.Uri.FromFile(new Java.IO.File(audioPath))!;

            // With the title, the author and the cover.
            //
            // These are not decoration: the lock screen, the notification shade, a car head unit
            // and a smartwatch all draw what the session tells them, and a media item built from a
            // bare URI tells them nothing. What showed instead was an anonymous set of controls
            // with no name on it, which is why this app had no presence on the lock screen while
            // every other player did.
            // The bang marks are the binding's doing: every builder setter is typed as returning a
            // nullable builder, though none of them ever returns null.
            var metadata = new MediaMetadata.Builder()
                .SetTitle(title ?? System.IO.Path.GetFileNameWithoutExtension(audioPath))!
                .SetArtist(author)!
                .SetArtworkUri(Artwork(coverPath))!
                .Build();

            _player.SetMediaItem(new MediaItem.Builder()
                .SetUri(uri)!
                .SetMediaMetadata(metadata)!
                .Build());

            _player.Prepare();
            LoadedPath = audioPath;
        }

        SetSpeed(speed);
        _player.SeekTo(startMs);
    }

    /// <summary>The cover as something the system can fetch, or null when the book has none.</summary>
    private static global::Android.Net.Uri? Artwork(string? coverPath) =>
        !string.IsNullOrEmpty(coverPath) && System.IO.File.Exists(coverPath)
            ? global::Android.Net.Uri.FromFile(new Java.IO.File(coverPath))
            : null;

    public void Play() => OnPlayer(() =>
    {
        if (_player is null) return;

        if (_player.PlayerError is not null) Retry();
        _player.Play();
    });

    public void Pause() => OnPlayer(() => _player?.Pause());

    public void SeekTo(long positionMs) => OnPlayer(() =>
        _player?.SeekTo(Math.Clamp(positionMs, 0, DurationMs > 0 ? DurationMs : long.MaxValue)));

    /// <summary>Jumps forward or back, clamped to the track. Used by the ±10 second controls.</summary>
    public void Nudge(long deltaMs) => OnPlayer(() => SeekTo(PositionMs + deltaMs));

    public void SetSpeed(float speed) => OnPlayer(() =>
    {
        if (_player is null) return;

        // Pitch held at 1 regardless of speed, so the narrator still sounds like themselves.
        _player.PlaybackParameters = new PlaybackParameters(Math.Clamp(speed, 0.5f, 2f), 1f);
    });

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

        // Stops the state pump, and any transport call still queued behind it — both would reach a
        // released player otherwise.
        _playerThread.RemoveCallbacksAndMessages(null);

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
