namespace AudioBookReader.App.Services;

/// <summary>
/// The app's handle on playback.
///
/// View models talk to this and never to the platform service, so a page can be created and torn
/// down without playback noticing — which is the whole point of the player living in a service.
/// Position is read on demand rather than pushed: the only thing that needs it continuously is a
/// scrubber on a visible page, and having that page poll is simpler than keeping a listener alive
/// across the service's lifetime.
/// </summary>
public partial class PlaybackController
{
    private int? _bookId;

    /// <summary>
    /// Book currently loaded, so a page can tell whether it is looking at what is playing.
    ///
    /// Null once the service is gone, and that is the whole point. This object is a singleton and
    /// outlives the service by design — but the service stops itself when the app is dismissed with
    /// nothing playing, and the field went on claiming a book was loaded. Every caller asks the same
    /// question, "is this book the one loaded", and every one of them got yes: the reader skipped
    /// loading it, play was forwarded to a service that no longer existed, and the button did
    /// nothing at all with nothing to show for it.
    /// </summary>
    public int? BookId => IsReady ? _bookId : null;

    /// <summary>Whether the playback service is actually up.</summary>
    public partial bool IsReady { get; }

    /// <summary>
    /// Whether the player has finished buffering and is sitting where it was sent.
    ///
    /// A seek is a request. Until this is true the player is still on the old position or fetching
    /// the new one, and playing then plays the passage being left.
    /// </summary>
    public partial bool IsReadyToPlay { get; }

    public partial bool IsPlaying { get; }
    public partial long PositionMs { get; }
    public partial long DurationMs { get; }
    public partial float Speed { get; }
    public partial TimeSpan? SleepRemaining { get; }

    /// <summary>Why the last attempt to play produced silence, or null. Named codes, not prose.</summary>
    public partial string? LastError { get; }

    /// <summary>Starts the playback service if it is not running and waits for it to come up.</summary>
    public partial Task<bool> ConnectAsync(CancellationToken ct = default);

    public partial void Play();
    public partial void Pause();
    public partial void SeekTo(long positionMs);
    public partial void Nudge(long deltaMs);
    public partial void SetSpeed(float speed);

    public partial void SleepAfter(TimeSpan delay);

    /// <summary>Stops when playback reaches a position — how "until the end of this chapter" is done.</summary>
    public partial void SleepAtPosition(long positionMs);

    public partial void CancelSleep();

    private partial void LoadCore(string audioPath, long startMs, float speed);

    public async Task<bool> LoadAsync(int bookId, string audioPath, long startMs, float speed, CancellationToken ct = default)
    {
        if (!await ConnectAsync(ct)) return false;

        LoadCore(audioPath, startMs, speed);
        _bookId = bookId;
        return true;
    }

    public void TogglePlayPause()
    {
        if (IsPlaying) Pause();
        else
        {
            _ = EnsureNotificationsAllowedAsync();
            Play();
        }
    }

    private bool _askedAboutNotifications;

    /// <summary>
    /// Asks for notification permission the first time something is played.
    ///
    /// Without it the lock screen and shade show nothing at all — no play, no pause, no skip —
    /// because from Android 13 a denied permission silently suppresses the media notification the
    /// session would otherwise publish. Asked here rather than at startup so the request arrives
    /// attached to the thing it is for.
    /// </summary>
    private async Task EnsureNotificationsAllowedAsync()
    {
        if (_askedAboutNotifications) return;
        _askedAboutNotifications = true;

        try
        {
            if (await Permissions.CheckStatusAsync<Permissions.PostNotifications>() != PermissionStatus.Granted)
                await Permissions.RequestAsync<Permissions.PostNotifications>();
        }
        catch (Exception)
        {
            // An older Android with no such permission, or a refusal. Playback works either way.
        }
    }
}
