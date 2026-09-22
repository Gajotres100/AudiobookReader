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
/// <see cref="MPRemoteCommandCenter"/> are what put the lock screen and control-center controls up —
/// there is nothing to start or bind to first. This class is therefore a plain lazily-constructed
/// singleton, not anything the OS instantiates, kept structurally parallel to
/// <c>Platforms/Android/Playback/PlaybackService.cs</c> so the two are easy to compare.
///
/// Android Auto browsing (<c>MediaLibrarySession.ICallback</c> on the Android side) has no
/// CarPlay counterpart here — that is a separate, out-of-scope-for-v1 feature
/// (<c>CarConnection.iOS.cs</c>), not a smaller version of this class.
/// </summary>
public sealed class PlaybackService
{
    public static PlaybackService? Current { get; private set; }

    /// <summary>Builds the singleton if it does not exist yet. Never fails — audio session setup errors are logged, not thrown.</summary>
    public static PlaybackService EnsureRunning()
    {
        return Current ??= new PlaybackService();
    }

    private readonly AVPlayer _player = new();
    private readonly LibraryDatabase? _database;
    private readonly SleepTimer _sleepTimer;
    private readonly NSTimer _pump;

    private AVPlayerItem? _item;
    private string? _loadedPath;
    private long[] _chapterStarts = [];
    private float _speed = 1f;

    // Kept alive for the process lifetime deliberately — this singleton is never torn down, so
    // there is no matching Dispose to unsubscribe from, but the tokens still need a live reference
    // so the runtime does not collect (and silently unregister) the observers underneath them.
    private NSObject? _interruptionObserver;
    private NSObject? _routeChangeObserver;

    private PlaybackService()
    {
        _database = IPlatformApplication.Current?.Services.GetService<LibraryDatabase>();
        _sleepTimer = new SleepTimer(_player);

        ConfigureAudioSession();
        ConfigureRemoteCommands();

        // Same reasoning as Android's 200ms PublishState pump: cheap enough to run the whole time a
        // book is loaded, and frequent enough that a scrubber never visibly lags.
        _pump = NSTimer.CreateRepeatingScheduledTimer(0.2, _ => Tick());
    }

    // ---- Audio session ----

    private void ConfigureAudioSession()
    {
        try
        {
            var session = AVAudioSession.SharedInstance();

            session.SetCategory(AVAudioSessionCategory.Playback, default(AVAudioSessionCategoryOptions), out var categoryError);
            if (categoryError is not null) AppLog.Info($"audio session category: {categoryError.LocalizedDescription}");

            // .SpokenAudio is Apple's mode for narration-first apps (audiobooks, podcasts): it
            // ducks rather than mixes under Siri and short system prompts, the same distinction
            // Android draws by declaring AudioContentTypeSpeech instead of music.
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

    /// <summary>Pauses when a call or another app's audio interrupts, and resumes after if the system says it is safe to.</summary>
    private void OnInterruption(object? sender, AVAudioSessionInterruptionEventArgs e)
    {
        if (e.InterruptionType == AVAudioSessionInterruptionType.Began)
        {
            Pause();
        }
        else if (e.InterruptionType == AVAudioSessionInterruptionType.Ended
                 && e.Option == AVAudioSessionInterruptionOptions.ShouldResume)
        {
            Play();
        }
    }

    /// <summary>Pauses when headphones come out, the same behaviour as Android's SetHandleAudioBecomingNoisy.</summary>
    private void OnRouteChange(object? sender, AVAudioSessionRouteChangeEventArgs e)
    {
        if (e.Reason == AVAudioSessionRouteChangeReason.OldDeviceUnavailable) Pause();
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

        // Chapter navigation. Disabled/enabled per book in Load(), the same asymmetry Android's
        // ChapterNavigation reports through AvailableCommands: a single-chapter book offers neither.
        commands.NextTrackCommand.AddTarget(_ => { SeekToNextChapter(); return MPRemoteCommandHandlerStatus.Success; });
        commands.PreviousTrackCommand.AddTarget(_ => { SeekToPreviousChapter(); return MPRemoteCommandHandlerStatus.Success; });
    }

    /// <summary>Past this far into a chapter, "previous" restarts it rather than moving to the one before — the same threshold and reasoning as Android's ChapterNavigation.</summary>
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

    public void Load(
        string audioPath,
        long startMs,
        float speed,
        string? title = null,
        string? author = null,
        string? coverPath = null,
        long[]? chapterStarts = null,
        int? bookId = null)
    {
        _chapterStarts = chapterStarts ?? [];
        CurrentBookId = bookId;
        Title = title ?? System.IO.Path.GetFileNameWithoutExtension(audioPath);
        Author = author;

        // A fresh book must not inherit the previous one's save clock — see RememberPosition.
        _lastPositionSaved = DateTime.MinValue;

        var commands = MPRemoteCommandCenter.Shared;
        commands.NextTrackCommand.Enabled = _chapterStarts.Length > 1;
        commands.PreviousTrackCommand.Enabled = _chapterStarts.Length > 1;

        if (_loadedPath != audioPath)
        {
            _item = new AVPlayerItem(ResolveUrl(audioPath))
            {
                // Preserves pitch while the rate changes, the same reason Android pins
                // PlaybackParameters' pitch argument at 1 — TimeDomain is tuned for speech, unlike
                // the higher-CPU Spectral algorithm meant for music.
                AudioTimePitchAlgorithm = AVAudioTimePitchAlgorithm.TimeDomain,
            };

            _player.ReplaceCurrentItemWithPlayerItem(_item);
            _loadedPath = audioPath;
        }

        _coverPath = coverPath;
        SetSpeed(speed);
        SeekTo(startMs);
        PublishNowPlayingInfo();
    }

    private string? _coverPath;

    private static NSUrl ResolveUrl(string audioPath) =>
        audioPath.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
            ? NSUrl.FromString(audioPath)!
            : NSUrl.FromFilename(audioPath);

    // ---- Transport ----

    public bool IsReady => true;

    public bool IsReadyToPlay => _item?.Status == AVPlayerItemStatus.ReadyToPlay;

    public bool IsPlaying => _player.TimeControlStatus == AVPlayerTimeControlStatus.Playing;

    public long PositionMs
    {
        get
        {
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

    public void Play()
    {
        try
        {
            AVAudioSession.SharedInstance().SetActive(true);
        }
        catch (Exception ex)
        {
            AppLog.Error("activating the audio session", ex);
        }

        _player.Rate = _speed;
        PublishNowPlayingInfo();
    }

    public void Pause()
    {
        _player.Pause();
        PublishNowPlayingInfo();
    }

    public void SeekTo(long positionMs)
    {
        var clamped = Math.Clamp(positionMs, 0, DurationMs > 0 ? DurationMs : long.MaxValue);
        _player.Seek(CMTime.FromSeconds(clamped / 1000.0, 1000));
        PublishNowPlayingInfo();
    }

    public void Nudge(long deltaMs) => SeekTo(PositionMs + deltaMs);

    public void SetSpeed(float speed)
    {
        _speed = Math.Clamp(speed, 0.5f, 2f);

        // Only actually apply it to the player if already playing — setting Rate on a paused
        // AVPlayer starts it, which SetSpeed must not do.
        if (IsPlaying) _player.Rate = _speed;

        PublishNowPlayingInfo();
    }

    // ---- Sleep timer ----

    public void SleepAfter(TimeSpan delay) => _sleepTimer.ArmForDelay(delay);
    public void SleepAtPosition(long positionMs) => _sleepTimer.ArmForPosition(positionMs);
    public void CancelSleep() => _sleepTimer.Cancel();
    public TimeSpan? SleepRemaining => _sleepTimer.Remaining;

    // ---- Now Playing info (lock screen / control centre) ----

    /// <summary>
    /// Refreshed on load, play, pause, seek and speed change — not on every tick. The system
    /// interpolates the scrubber between updates from PlaybackRate and ElapsedPlaybackTime, so
    /// updating this on every 200ms poll would be redundant work for no visible benefit.
    /// </summary>
    private void PublishNowPlayingInfo()
    {
        var info = new MPNowPlayingInfo
        {
            Title = Title,
            Artist = Author,
            PlaybackDuration = DurationMs / 1000.0,
            ElapsedPlaybackTime = PositionMs / 1000.0,
            PlaybackRate = IsPlaying ? _speed : 0f,
        };

        if (!string.IsNullOrEmpty(_coverPath) && System.IO.File.Exists(_coverPath)
            && UIImage.FromFile(_coverPath) is { } image)
        {
            info.Artwork = new MPMediaItemArtwork(image.Size, _ => image);
        }

        MPNowPlayingInfoCenter.DefaultCenter.NowPlaying = info;
    }

    // ---- Periodic pump: cached state + position autosave ----

    private DateTime _lastPositionSaved = DateTime.MinValue;

    /// <summary>
    /// Writes the playing position down every few seconds, from the service rather than from a
    /// page — same reasoning as the Android pump: a book is mostly listened to while nobody is
    /// looking at a screen, and this runs the whole time regardless.
    /// </summary>
    private void Tick()
    {
        if (_item?.Error is { } error) LastError = error.LocalizedDescription;

        if (!IsPlaying
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
/// Pauses playback at a chosen moment, fading down first. Same shape and reasoning as the Android
/// SleepTimer, ported field for field — the algorithm is not platform-specific, only the player
/// object underneath it (an AVPlayer's Volume property stands in for ExoPlayer's).
/// </summary>
internal sealed class SleepTimer(AVPlayer player)
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
        // A paused book is not being listened to, so the timer waits rather than running down —
        // same reasoning as Android: otherwise pausing to answer the door silently burns the timer.
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

        var speed = Math.Max(player.Rate, 0.1f);
        return TimeSpan.FromMilliseconds((target - positionMs) / speed);
    }
}
