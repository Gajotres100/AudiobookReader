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

    /// <summary>Says what it is doing, for the reader's own status line rather than a notification.</summary>
    public event EventHandler<string>? Progress;

    public bool IsRunning => _running is { IsCompleted: false };

    public void Start(int bookId)
    {
        if (IsRunning) return;

        _cancellation = new CancellationTokenSource();
        _running = RunAsync(bookId, _cancellation.Token);
    }

    public void Stop()
    {
        _cancellation?.Cancel();
        _cancellation = null;
        _running = null;
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
