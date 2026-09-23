using AudioBookReader.App.Resources.Strings;
using AudioBookReader.Core.Alignment;
using AudioBookReader.Core.Books;
using AudioBookReader.Core.Data;
using AudioBookReader.Core.Models;

namespace AudioBookReader.App.Services;

/// <summary>
/// Runs sync on the fly for as long as the reader is open.
///
/// Deliberately not a foreground service. Foreground work has to post a notification, and this
/// only ever runs while the reader is on screen — so the app is already in front of the user, the
/// system has no reason to reclaim it, and a permanent notification claiming something is being
/// downloaded is noise about work the user is watching happen.
///
/// The whole-book alignment is the opposite case and keeps its service: it runs for the better part
/// of an hour with the app closed and the screen off, and that genuinely needs both the promise the
/// service makes and the notification that pays for it.
/// </summary>
public partial class LiveSyncRunner(
    LibraryDatabase database,
    SyncMapStore syncMaps,
    BookTextExtractors extractors,
    PlaybackController playback,
    AlignmentSettingsStore settings,
    WhisperModelStore models)
{
    private CancellationTokenSource? _cancellation;
    private Task? _running;

    /// <summary>
    /// Serialises starting and stopping.
    ///
    /// Without it the two interleave: stopping cleared the fields before it had waited for
    /// anything, so a start arriving in that window saw nothing running and began a second run over
    /// the first — two whisper models, two decoders, and both writing the same map so the later
    /// save discarded the earlier one's work. Reachable on every return to the app, because this is
    /// a singleton while the reader that drives it is created afresh each time.
    /// </summary>
    private readonly SemaphoreSlim _turn = new(1, 1);

    /// <summary>Says what it is doing, for the reader's own status line rather than a notification.</summary>
    public event EventHandler<string>? Progress;

    /// <summary>
    /// Raised when following has given up.
    ///
    /// Separate from <see cref="Progress"/> because the reader deliberately hides progress once the
    /// text is moving — it would sit on top of the page the user is reading. A failure is the one
    /// message that must get through anyway: the symptom of losing this silently is text that
    /// follows for half a page and then stops, with nothing on screen to say why.
    /// </summary>
    public event EventHandler<string>? Failed;

    /// <summary>
    /// Hands the reader the map as it grows, window by window.
    ///
    /// The reader used to re-read the file instead, and the file is written every few windows and
    /// polled every ten seconds — so an anchor could be three quarters of a minute old before the
    /// text could use it, while the narrator was reading through it. Both live in this process, so
    /// there is no reason to make the disk the messenger.
    /// </summary>
    public event EventHandler<SyncMap>? Measured;

    public bool IsRunning => _running is { IsCompleted: false };

    /// <summary>
    /// Begins following, after making sure the previous run has actually finished.
    ///
    /// Waiting matters: a run can be sitting inside a fifteen-second recognition when it is
    /// cancelled, and a reader reopened in that window would otherwise start a second one. Two runs
    /// means two whisper models loaded at once, two hardware decoders, and both writing the same
    /// sync map — the later save silently discarding the earlier one's work.
    /// </summary>
    public async Task StartAsync(int bookId)
    {
        await _turn.WaitAsync();

        try
        {
            await StopWhileHoldingTurnAsync();

            // The token is read into a local before the lambda captures it. Reading the field from
            // inside the lambda defers it to whenever the pool gets round to running it, and a stop
            // arriving first left the lambda dereferencing null — outside RunAsync, so outside its
            // try, so the failure vanished and following simply never began.
            var cancellation = new CancellationTokenSource();
            _cancellation = cancellation;

            // Task.Run for the same reason the whole-book run needs it: this is called from the
            // reader's OnAppearing, on the UI thread, and every await after it would otherwise come
            // back there — putting recognition and matching on the thread drawing the page.
            _running = Task.Run(() => RunAsync(bookId, cancellation.Token));
        }
        finally
        {
            _turn.Release();
        }
    }

    /// <summary>Cancels the run and waits for it to leave, so nothing overlaps the next one.</summary>
    public async Task StopAsync()
    {
        await _turn.WaitAsync();

        try
        {
            await StopWhileHoldingTurnAsync();
        }
        finally
        {
            _turn.Release();
        }
    }

    private async Task StopWhileHoldingTurnAsync()
    {
        var cancellation = _cancellation;
        var running = _running;

        if (cancellation is null) return;

        await cancellation.CancelAsync();

        try
        {
            if (running is not null) await running;
        }
        catch (Exception)
        {
            // RunAsync reports its own failures; this await only exists to know it has stopped.
        }
        finally
        {
            cancellation.Dispose();

            // Cleared only once the run has actually left, so IsRunning never reports idle while a
            // probe is still in flight.
            _cancellation = null;
            _running = null;
        }
    }

    /// <summary>
    /// How many times a failed run is begun again before following gives up for this reading.
    ///
    /// Recognition reaches a hardware decoder and a native model, and either can fail once for
    /// reasons that have gone by the next attempt. Dying on the first of those left the reader with
    /// a map frozen wherever it had reached, which reads as the feature quietly not working.
    /// </summary>
    private const int Attempts = 3;

    private async Task RunAsync(int bookId, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            if (await RunOnceAsync(bookId, attempt, ct)) return;

            ct.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }

        Report(Strings.Live_Stopped);
        Fail(Strings.Live_Stopped);
    }

    /// <returns>True when the run finished on its own terms rather than failing.</returns>
    private async Task<bool> RunOnceAsync(int bookId, int attempt, CancellationToken ct)
    {
        try
        {
            var book = await database.GetBookAsync(bookId);
            if (book?.IsPaired != true) return true;

            if (!await EnsureSomeModelAsync(ct)) return true;

            var transcriber = await CreateTranscriberAsync(settings.Budget, book.Language, ct);
            if (transcriber is null)
            {
                Report(Strings.Live_NoModel);
                Fail(Strings.Live_NoModelHint);
                return true;
            }

            await using (transcriber as IAsyncDisposable ?? new NoDisposal())
            {
                var chapters = await database.GetChaptersAsync(bookId);
                var extracted = await extractors.ExtractAsync(book.EbookPath!, ct);
                var text = extracted.Text.PlainText;

                var map = await syncMaps.LoadAsync(bookId) is { } existing
                          && existing.MatchesPair(book.AudioHash, book.EbookHash)
                    ? existing
                    : new SyncMap { AudioHash = book.AudioHash, EbookHash = book.EbookHash };

                var aligner = new LiveAligner(
                    TokenizedText.Create(text),
                    chapters,
                    transcriber,
                    text.Length,
                    throttle: CreateThrottle(settings.Budget),
                    log: AppLog.Info);

                var progress = new Progress<LiveAlignmentProgress>(p =>
                {
                    var ahead = Math.Max(0, p.AtMs - playback.PositionMs) / 1000;
                    Report(ahead > 0
                        ? string.Format(Strings.Live_MeasuredAhead, ahead)
                        : Strings.Live_Working);
                });

                // Copied synchronously, on the aligner's own thread, while it is between windows
                // and certainly not inserting. Handing the copy to Progress<T> instead would run it
                // on some other pool thread alongside the next window's insertions, which is the
                // torn read this exists to prevent.
                void Publish()
                {
                    var copy = map.Snapshot();
                    MainThread.BeginInvokeOnMainThread(() => Measured?.Invoke(this, copy));
                }

                await aligner.RunAsync(
                    book.AudioPath!,
                    map,
                    () => playback.BookId == bookId ? playback.PositionMs : 0,
                    () => syncMaps.SaveAsync(bookId, map),
                    new PublishingProgress(progress, Publish),
                    ct);
            }

            // RunAsync only leaves by cancellation, so reaching here at all is unexpected.
            return true;
        }
        catch (OperationCanceledException)
        {
            // Closing the reader is how this ends.
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error($"sync on the fly (attempt {attempt} of {Attempts})", ex);
            return false;
        }
    }

    /// <summary>
    /// Makes sure some speech model is on the device, fetching the chosen one when none is.
    ///
    /// This used never to download: opening a book is no reason to spend someone's data. But this
    /// only runs for a book the user has put into reading-along, which is them asking for the model —
    /// and refusing left the reader saying "start aligning from the book's page", about a button
    /// reading-along switches off. The book page already fetches it when the choice is made; this
    /// covers a choice made offline, or before that page learned to.
    /// </summary>
    /// <returns>False when there is still no model, with the reason already on the status line.</returns>
    private async Task<bool> EnsureSomeModelAsync(CancellationToken ct)
    {
        if (models.IsDownloaded(settings.Model)
            || models.IsDownloaded(WhisperModelStore.Tiny)
            || models.IsDownloaded(WhisperModelStore.Base))
        {
            return true;
        }

        var lastPercent = -1;

        var progress = new Progress<double>(fraction =>
        {
            var percent = (int)(fraction * 100);
            if (percent == lastPercent) return;

            lastPercent = percent;
            Report(string.Format(Strings.Progress_DownloadingModelPercent, percent));
        });

        Report(Strings.Progress_DownloadingModel);

        try
        {
            await models.EnsureAsync(settings.Model, progress, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Offline, most likely. Said once and left there, rather than retried three times and
            // then reported as following having stopped, which reads as a fault in the reader.
            AppLog.Error("downloading the speech model for reading-along", ex);
            Report(Strings.Live_NoModel);
            Fail(Strings.Live_NoModelHint);
            return false;
        }
    }

    /// <summary>Reports the status message and hands out the map, because a window is news to both.</summary>
    private sealed class PublishingProgress(IProgress<LiveAlignmentProgress> status, Action publish)
        : IProgress<LiveAlignmentProgress>
    {
        public void Report(LiveAlignmentProgress value)
        {
            publish();
            status.Report(value);
        }
    }

    private void Fail(string message) =>
        MainThread.BeginInvokeOnMainThread(() => Failed?.Invoke(this, message));

    private void Report(string message) =>
        MainThread.BeginInvokeOnMainThread(() => Progress?.Invoke(this, message));

    /// <summary>Builds the recognizer, or null when the model is not on the device yet.</summary>
    private partial Task<ITranscriber?> CreateTranscriberAsync(
        CpuBudget budget,
        string? language,
        CancellationToken ct);

    private partial IWorkThrottle CreateThrottle(CpuBudget budget);

    /// <summary>Stands in when the recognizer has nothing to dispose.</summary>
    private sealed class NoDisposal : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
