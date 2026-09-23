using AVFoundation;
using AudioBookReader.App.Services;
using AudioBookReader.Core.Data;
using CoreMedia;
using Foundation;
using MediaPlayer;
using UIKit;

namespace AudioBookReader.App.Platforms.iOS.Playback;

/// <summary>
/// Hosts the player.
///
/// The Android counterpart is a <c>MediaLibraryService</c> because Android needs an explicit
/// foreground service to survive backgrounding and to publish lock-screen controls at all. iOS has
/// no service concept: an <see cref="AVAudioSession"/> in the <c>Playback</c> category, together
/// with the <c>audio</c> background mode declared in Info.plist, is what keeps the process alive
/// while sound is coming out of it, and <see cref="MPNowPlayingInfoCenter"/>/
/// <see cref="MPRemoteCommandCenter"/> are what put the lock screen and control-center controls up.
/// This class is therefore a plain singleton, kept structurally parallel to
/// <c>Platforms/Android/Playback/PlaybackService.cs</c> so the two are easy to compare.
///
/// Everything that touches the player or the Now Playing centre runs on the main thread, the same
/// rule Android's version keeps for ExoPlayer — AVFoundation is more forgiving about it, but the
/// timers here only fire on the main run loop, and the media centre is UIKit territory.
/// </summary>
public sealed class PlaybackService
{
    public static PlaybackService? Current { get; private set; }

    /// <summary>Builds the singleton if it does not exist yet. Main thread only: its timers belong to that run loop.</summary>
    public static PlaybackService EnsureRunning() => Current ??= new PlaybackService();

    private readonly AVPlayer _player = new();
    private readonly LibraryDatabase? _database;
    private readonly SleepTimer _sleepTimer;
    private readonly NSTimer _pump;

    private AVPlayerItem? _item;
    private string? _loadedPath;
    private long[] _chapterStarts = [];
    private float _speed = 1f;

    /// <summary>
    /// A seek asked for before the file was ready.
    ///
    /// AVPlayer quietly drops a seek made before its item is ready to play — and the very first
    /// thing a book does is load and seek to where it was left. Without holding the request until
    /// the item is ready, every book resumed from its opening line. ExoPlayer accepts an early seek
    /// on its own, which is why Android never needed this.
    /// </summary>
    private long? _pendingSeekMs;

    /// <summary>Play was pressed while a seek was still pending; start once it has landed.</summary>
    private bool _playWhenReady;

    // Kept for the process lifetime deliberately — this singleton is never torn down, but the
    // tokens still need a live reference so the observers underneath them are not collected.
    private NSObject? _interruptionObserver;
    private NSObject? _routeChangeObserver;

    private PlaybackService()
    {
        _database = IPlatformApplication.Current?.Services.GetService<LibraryDatabase>();
        _sleepTimer = new SleepTimer(_player, () => _speed);

        ConfigureAudioSession();
        ConfigureRemoteCommands();

        // Same reasoning as Android's 200ms PublishState pump.
        _pump = NSTimer.CreateRepeatingScheduledTimer(0.2, _ => Tick());
    }

    /// <summary>Runs on the main thread, immediately when already there — Android's OnPlayer, for AVFoundation.</summary>
    private static void OnMain(Action work)
    {
        if (NSThread.IsMain) work();
        else MainThread.BeginInvokeOnMainThread(work);
    }

    // ---- Audio session ----

    private void ConfigureAudioSession()
    {
        try
        {
            var session = AVAudioSession.SharedInstance();

            session.SetCategory(AVAudioSessionCategory.Playback, default(AVAudioSessionCategoryOptions), out var categoryError);
            if (categoryError is not null) AppLog.Info($"audio session category: {categoryError.LocalizedDescription}");

            // .SpokenAudio is Apple's mode for narration-first apps: it ducks rather than mixes under
            // Siri and short system prompts — the distinction Android draws with a speech content type.
            session.SetMode(AVAudioSessionMode.SpokenAudio, out var modeError);
            if (modeError is not null) AppLog.Info($"audio session mode: {modeError.LocalizedDescription}");

            _interruptionObserver = AVAudioSession.Notifications.ObserveInterruption(OnInterruption);
            _routeChangeObserver = AVAudioSession.Notifications.ObserveRouteChange(OnRouteChange);
        }
        catch (Exception ex)
        {
            AppLog.Error("configuring the audio session", ex);
        }
    }

    /// <summary>
    /// Whether playback was running when an interruption began. Resuming unconditionally when it
    /// ends meant a book paused by the user started talking by itself once a phone call hung up.
    /// </summary>
    private bool _resumeAfterInterruption;

    private void OnInterruption(object? sender, AVAudioSessionInterruptionEventArgs e) => OnMain(() =>
    {
        if (e.InterruptionType == AVAudioSessionInterruptionType.Began)
        {
            _resumeAfterInterruption = IsPlaying;
            Pause();
        }
        else if (e.InterruptionType == AVAudioSessionInterruptionType.Ended)
        {
            if (_resumeAfterInterruption && e.Option.HasFlag(AVAudioSessionInterruptionOptions.ShouldResume)) Play();
            _resumeAfterInterruption = false;
        }
    });

    /// <summary>Pauses when headphones come out, the same behaviour as Android's SetHandleAudioBecomingNoisy.</summary>
    private void OnRouteChange(object? sender, AVAudioSessionRouteChangeEventArgs e)
    {
        if (e.Reason == AVAudioSessionRouteChangeReason.OldDeviceUnavailable) OnMain(Pause);
    }

    // ---- Remote commands (lock screen / control centre / headset buttons) ----

    private void ConfigureRemoteCommands()
    {
        var commands = MPRemoteCommandCenter.Shared;

        commands.PlayCommand.Enabled = true;
        commands.PlayCommand.AddTarget(_ => { Play(); return MPRemoteCommandHandlerStatus.Success; });

        commands.PauseCommand.Enabled = true;
        commands.PauseCommand.AddTarget(_ => { Pause(); return MPRemoteCommandHandlerStatus.Success; });

        commands.TogglePlayPauseCommand.Enabled = true;
        commands.TogglePlayPauseCommand.AddTarget(_ =>
        {
            if (IsPlaying) Pause(); else Play();
            return MPRemoteCommandHandlerStatus.Success;
        });

        // ±10s, the same increment as Android's seek-back/seek-forward.
        commands.SkipForwardCommand.Enabled = true;
        commands.SkipForwardCommand.PreferredIntervals = [10.0];
        commands.SkipForwardCommand.AddTarget(_ => { Nudge(10_000); return MPRemoteCommandHandlerStatus.Success; });

        commands.SkipBackwardCommand.Enabled = true;
        commands.SkipBackwardCommand.PreferredIntervals = [10.0];
        commands.SkipBackwardCommand.AddTarget(_ => { Nudge(-10_000); return MPRemoteCommandHandlerStatus.Success; });

        commands.ChangePlaybackPositionCommand.Enabled = true;
        commands.ChangePlaybackPositionCommand.AddTarget(e =>
        {
            if (e is MPChangePlaybackPositionCommandEvent positioned)
                SeekTo((long)(positioned.PositionTime * 1000));

            return MPRemoteCommandHandlerStatus.Success;
        });

        // Chapter navigation, enabled per book in Load() — the same asymmetry Android's
        // ChapterNavigation reports: a single-chapter book offers neither.
        commands.NextTrackCommand.AddTarget(_ => { SeekToNextChapter(); return MPRemoteCommandHandlerStatus.Success; });
        commands.PreviousTrackCommand.AddTarget(_ => { SeekToPreviousChapter(); return MPRemoteCommandHandlerStatus.Success; });
    }

    /// <summary>Past this far into a chapter, "previous" restarts it — the same threshold as Android's ChapterNavigation.</summary>
    private const long RestartWithinMs = 3_000;

    private void SeekToNextChapter()
    {
        if (_chapterStarts.Length == 0) return;

        var at = PositionMs;
        var next = _chapterStarts.FirstOrDefault(s => s > at, -1);

        SeekTo(next >= 0 ? next : DurationMs);
    }

    private void SeekToPreviousChapter()
    {
        if (_chapterStarts.Length == 0) return;

        var at = PositionMs;
        var current = _chapterStarts.LastOrDefault(s => s <= at, 0);

        SeekTo(at - current > RestartWithinMs
            ? current
            : _chapterStarts.LastOrDefault(s => s < current, 0));
    }

    // ---- Loading ----

    public string? Title { get; private set; }
    public string? Author { get; private set; }
    public int? CurrentBookId { get; private set; }

    private MPMediaItemArtwork? _artwork;

    public void Load(
        string audioPath,
        long startMs,
        float speed,
        string? title = null,
        string? author = null,
        string? coverPath = null,
        long[]? chapterStarts = null,
        int? bookId = null) => OnMain(() =>
    {
        _chapterStarts = chapterStarts ?? [];
        CurrentBookId = bookId;
        Title = title ?? System.IO.Path.GetFileNameWithoutExtension(audioPath);
        Author = author;

        // A fresh book must not inherit the previous one's save clock — see Tick.
        _lastPositionSaved = DateTime.MinValue;

        var commands = MPRemoteCommandCenter.Shared;
        commands.NextTrackCommand.Enabled = _chapterStarts.Length > 1;
        commands.PreviousTrackCommand.Enabled = _chapterStarts.Length > 1;

        // Built once per book rather than on every Now Playing refresh, which happens on every
        // play, pause and seek — reading and decoding the cover from disk each time was wasted work.
        _artwork = !string.IsNullOrEmpty(coverPath) && File.Exists(coverPath)
                   && UIImage.FromFile(coverPath) is { } image
            ? new MPMediaItemArtwork(image.Size, _ => image)
            : null;

        if (_loadedPath != audioPath) ReplaceItem(audioPath);

        SetSpeed(speed);
        SeekTo(startMs);
        PublishNowPlayingInfo();
    });

    private void ReplaceItem(string audioPath)
    {
        _item = new AVPlayerItem(SecurityScopedBookmarks.UrlFor(audioPath))
        {
            // Preserves pitch while the rate changes, the same reason Android pins PlaybackParameters'
            // pitch at 1 — TimeDomain is tuned for speech, unlike the costlier Spectral meant for music.
            AudioTimePitchAlgorithm = AVAudioTimePitchAlgorithm.TimeDomain,
        };

        _player.ReplaceCurrentItemWithPlayerItem(_item);
        _loadedPath = audioPath;
        _publishedReady = false;
    }

    // ---- Transport ----

    public bool IsReady => true;

    public bool IsReadyToPlay => _item?.Status == AVPlayerItemStatus.ReadyToPlay && _pendingSeekMs is null;

    /// <summary>
    /// Whether playback is running or about to, rather than only whether sound is coming out this
    /// instant. Reporting false during the brief wait after Play made a quick second tap on the
    /// toggle read the book as stopped and start it again instead of pausing it.
    /// </summary>
    public bool IsPlaying => _playWhenReady || _player.TimeControlStatus != AVPlayerTimeControlStatus.Paused;

    public long PositionMs
    {
        get
        {
            if (_pendingSeekMs is { } pending) return pending;

            var time = _player.CurrentTime;
            return time.IsInvalid || time.IsIndefinite ? 0 : (long)(time.Seconds * 1000);
        }
    }

    public long DurationMs
    {
        get
        {
            var duration = _item?.Duration ?? CMTime.Invalid;
            return duration.IsInvalid || duration.IsIndefinite ? 0 : (long)(duration.Seconds * 1000);
        }
    }

    public float Speed => _speed;

    public string? LastError { get; private set; }

    public void Play() => OnMain(() =>
    {
        // A failed item latches, like ExoPlayer's error state: nothing plays again until it is
        // rebuilt. Android's Retry does the same thing by preparing again.
        if (_item?.Status == AVPlayerItemStatus.Failed && _loadedPath is { } path)
        {
            AppLog.Info("player: rebuilding the item after an error");
            var at = PositionMs;
            ReplaceItem(path);
            _pendingSeekMs = at;
        }

        try
        {
            AVAudioSession.SharedInstance().SetActive(true);
        }
        catch (Exception ex)
        {
            AppLog.Error("activating the audio session", ex);
        }

        if (_pendingSeekMs is not null)
        {
            // Started by Tick once the seek has landed, so the opening of the file is not heard first.
            _playWhenReady = true;
            return;
        }

        _player.Rate = _speed;
        PublishNowPlayingInfo();
    });

    public void Pause() => OnMain(() =>
    {
        _playWhenReady = false;
        _player.Pause();
        PublishNowPlayingInfo();
    });

    public void SeekTo(long positionMs) => OnMain(() =>
    {
        var clamped = Math.Clamp(positionMs, 0, DurationMs > 0 ? DurationMs : long.MaxValue);

        if (_item?.Status != AVPlayerItemStatus.ReadyToPlay)
        {
            _pendingSeekMs = clamped;
            return;
        }

        _pendingSeekMs = null;
        SeekExactly(clamped);
        PublishNowPlayingInfo();
    });

    /// <summary>
    /// Seeks to the exact position asked for. AVPlayer's default seek is allowed to land anywhere
    /// it finds convenient — seconds away, in compressed audio — which is fine for a scrubber and
    /// useless for read-along, where tapping a sentence has to start that sentence.
    /// </summary>
    private void SeekExactly(long positionMs) =>
        _player.Seek(CMTime.FromSeconds(positionMs / 1000.0, 1000), CMTime.Zero, CMTime.Zero);

    public void Nudge(long deltaMs) => SeekTo(PositionMs + deltaMs);

    public void SetSpeed(float speed) => OnMain(() =>
    {
        _speed = Math.Clamp(speed, 0.5f, 2f);

        // Setting Rate on a paused AVPlayer starts it, which SetSpeed must not do.
        if (_player.TimeControlStatus != AVPlayerTimeControlStatus.Paused) _player.Rate = _speed;

        PublishNowPlayingInfo();
    });

    // ---- Sleep timer ----

    public void SleepAfter(TimeSpan delay) => OnMain(() => _sleepTimer.ArmForDelay(delay));
    public void SleepAtPosition(long positionMs) => OnMain(() => _sleepTimer.ArmForPosition(positionMs));
    public void CancelSleep() => OnMain(_sleepTimer.Cancel);
    public TimeSpan? SleepRemaining => _sleepTimer.Remaining;

    // ---- Now Playing info (lock screen / control centre) ----

    /// <summary>
    /// Refreshed on load, play, pause, seek, speed change and once the file is ready — not on every
    /// tick. The system extrapolates the scrubber from PlaybackRate and ElapsedPlaybackTime.
    /// </summary>
    private void PublishNowPlayingInfo()
    {
        MPNowPlayingInfoCenter.DefaultCenter.NowPlaying = new MPNowPlayingInfo
        {
            Title = Title,
            Artist = Author,
            PlaybackDuration = DurationMs / 1000.0,
            ElapsedPlaybackTime = PositionMs / 1000.0,
            PlaybackRate = _player.TimeControlStatus == AVPlayerTimeControlStatus.Playing ? _speed : 0f,
            Artwork = _artwork,
        };
    }

    // ---- Periodic pump: pending seeks, errors, position autosave ----

    private DateTime _lastPositionSaved = DateTime.MinValue;
    private bool _publishedReady;

    private void Tick()
    {
        var status = _item?.Status;

        if (status == AVPlayerItemStatus.Failed)
        {
            var message = _item?.Error?.LocalizedDescription ?? "unknown";
            if (LastError != message) AppLog.Info($"player FAILED: {message}");
            LastError = message;
        }
        else
        {
            LastError = null;
        }

        if (status == AVPlayerItemStatus.ReadyToPlay)
        {
            if (_pendingSeekMs is { } pending)
            {
                _pendingSeekMs = null;
                SeekExactly(Math.Clamp(pending, 0, DurationMs > 0 ? DurationMs : long.MaxValue));
            }

            if (_playWhenReady)
            {
                _playWhenReady = false;
                _player.Rate = _speed;
            }

            // The duration is only known now; the lock screen was told zero when the book loaded.
            if (!_publishedReady)
            {
                _publishedReady = true;
                PublishNowPlayingInfo();
            }
        }

        RememberPosition();
    }

    /// <summary>
    /// Writes the playing position down every few seconds, from the player rather than from a page,
    /// for the same reason as Android: a book is mostly listened to while nobody is looking.
    /// Only while genuinely playing, so a player parked on a pending seek writes nothing.
    /// </summary>
    private void RememberPosition()
    {
        if (_player.TimeControlStatus != AVPlayerTimeControlStatus.Playing
            || CurrentBookId is not { } bookId
            || _database is not { } database
            || DateTime.UtcNow - _lastPositionSaved <= TimeSpan.FromSeconds(5))
        {
            return;
        }

        _lastPositionSaved = DateTime.UtcNow;

        var positionMs = PositionMs;
        var speed = _speed;

        _ = SaveAsync();

        async Task SaveAsync()
        {
            try
            {
                await database.SaveReadingStateAsync(bookId, audioPositionMs: positionMs, speed: speed);
            }
            catch (Exception ex)
            {
                AppLog.Error("saving the playing position", ex);
            }
        }
    }
}

/// <summary>
/// Pauses playback at a chosen moment, fading down first — Android's SleepTimer, ported field for
/// field; only the player underneath differs.
/// </summary>
internal sealed class SleepTimer(AVPlayer player, Func<float> speed)
{
    private static readonly TimeSpan FadeDuration = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private NSTimer? _timer;
    private TimeSpan? _remaining;
    private long? _stopAtPositionMs;
    private float _volumeBeforeFade = 1f;

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
        _timer?.Invalidate();
        _timer = null;

        _remaining = null;
        _stopAtPositionMs = null;

        player.Volume = _volumeBeforeFade;
    }

    private void Start()
    {
        _volumeBeforeFade = player.Volume;
        Schedule();
    }

    private void Schedule() =>
        _timer = NSTimer.CreateScheduledTimer(PollInterval, _ => Tick());

    private void Tick()
    {
        // A paused book is not being listened to, so the timer waits rather than running down.
        if (player.TimeControlStatus != AVPlayerTimeControlStatus.Playing)
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

        player.Volume = untilStop < FadeDuration
            ? _volumeBeforeFade * (float)(untilStop.Value.TotalSeconds / FadeDuration.TotalSeconds)
            : _volumeBeforeFade;

        Schedule();
    }

    private TimeSpan? PositionRemaining()
    {
        if (_stopAtPositionMs is not { } target) return null;

        var time = player.CurrentTime;
        var positionMs = time.IsInvalid || time.IsIndefinite ? 0 : time.Seconds * 1000;

        // The chosen speed, not the player's current rate: that is zero while paused, and a paused
        // book showed an end-of-chapter timer ten times longer than it really was.
        return TimeSpan.FromMilliseconds((target - positionMs) / Math.Max(speed(), 0.1f));
    }
}
