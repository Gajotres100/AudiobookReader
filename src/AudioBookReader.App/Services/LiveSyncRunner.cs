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
    AlignmentSettingsStore settings)
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

    private async Task RunAsync(int bookId, CancellationToken ct)
    {
        try
        {
            var book = await database.GetBookAsync(bookId);
            if (book?.IsPaired != true) return;

            var transcriber = await CreateTranscriberAsync(settings.Budget, ct);
            if (transcriber is null)
            {
                Report("Model za prepoznavanje još nije preuzet.");
                return;
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
                    Report(ahead > 0 ? $"Sync on the fly — izmjereno {ahead} s unaprijed" : "Sync on the fly…");
                });

                await aligner.RunAsync(
                    book.AudioPath!,
                    map,
                    () => playback.BookId == bookId ? playback.PositionMs : 0,
                    () => syncMaps.SaveAsync(bookId, map),
                    progress,
                    ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Closing the reader is how this ends.
        }
        catch (Exception ex)
        {
            AppLog.Error("sync on the fly", ex);
            Report("Sync on the fly je stao.");
        }
    }

    private void Report(string message) =>
        MainThread.BeginInvokeOnMainThread(() => Progress?.Invoke(this, message));

    /// <summary>Builds the recognizer, or null when the model is not on the device yet.</summary>
    private partial Task<ITranscriber?> CreateTranscriberAsync(CpuBudget budget, CancellationToken ct);

    private partial IWorkThrottle CreateThrottle(CpuBudget budget);

    /// <summary>Stands in when the recognizer has nothing to dispose.</summary>
    private sealed class NoDisposal : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
