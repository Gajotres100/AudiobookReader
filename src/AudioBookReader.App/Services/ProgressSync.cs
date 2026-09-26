using AudioBookReader.Core.Data;
using AudioBookReader.Core.Models;

namespace AudioBookReader.App.Services;

/// <summary>A place in a book that another device reached more recently than this one.</summary>
/// <param name="AudioMs">Where listening had got to, for the player.</param>
/// <param name="TextOffset">Where reading had got to, for the reader.</param>
public record ElsewherePosition(int BookId, long? AudioMs, int? TextOffset, DateTimeOffset When);

/// <summary>
/// Keeps where each book was left in step with the Audiobookshelf server, so another device — or
/// the server's own web page and app — picks up where this one stopped.
///
/// Only for books that came from the server, since only those have an item there to record against.
/// Sent every half minute while something changes, and at once when listening pauses or the reader
/// closes; read when a book is opened or play is pressed, and offered rather than applied, because
/// jumping someone hours through a book they had deliberately gone back in is worse than asking.
///
/// Quiet about failure: a phone on a train is offline half the time, and every missed update is
/// followed by another one.
/// </summary>
public sealed class ProgressSync(ServerConnections servers, LibraryDatabase database)
{
    /// <summary>Reached by the players, which are built by the platform rather than handed services.</summary>
    internal static ProgressSync? Current { get; set; }

    private static readonly TimeSpan Every = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Less apart than this, and two positions are the same place — not worth asking about. Wider
    /// than the half minute between updates, so a device never mistakes its own last update, a few
    /// seconds behind where it has since got to, for someone else's.
    /// </summary>
    private const long SameAudioMs = 45_000;

    /// <summary>About a page of text.</summary>
    private const int SameTextChars = 1_500;

    private sealed class Pending
    {
        public double Value = double.NaN;
        public double Total;
        public DateTime LastSent = DateTime.MinValue;
        public bool Scheduled;
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<(int BookId, bool Text), Pending> _pending = [];

    /// <param name="now">Send without waiting out the half minute — listening has just stopped.</param>
    public void NoteAudio(int bookId, long positionMs, long durationMs, bool now = false) =>
        Note((bookId, false), positionMs, durationMs, now);

    /// <param name="now">Send without waiting out the half minute — the reader is closing.</param>
    public void NoteText(int bookId, int offset, int textLength, bool now = false) =>
        Note((bookId, true), offset, textLength, now);

    private void Note((int BookId, bool Text) key, double value, double total, bool now)
    {
        if (!servers.IsConfigured || total <= 0) return;

        TimeSpan? wait = null;
        var send = false;

        lock (_gate)
        {
            if (!_pending.TryGetValue(key, out var entry)) _pending[key] = entry = new Pending();

            // Something actually happened here. Audio is only reported while it plays, so every
            // report counts; text is also reported when the reader merely closes, so only a page
            // that moved counts — opening a book and closing it again is not reading it.
            if (!key.Text || (!double.IsNaN(entry.Value) && Math.Abs(entry.Value - value) >= 1))
                MarkActive(key.BookId);

            entry.Value = value;
            entry.Total = total;

            var due = entry.LastSent + Every;

            if (now || DateTime.UtcNow >= due)
            {
                entry.LastSent = DateTime.UtcNow;
                entry.Scheduled = false;
                send = true;
            }
            else if (!entry.Scheduled)
            {
                // The latest value goes out when the half minute is up, even if nothing else
                // arrives by then — so the last stretch before stopping is never lost.
                entry.Scheduled = true;
                wait = due - DateTime.UtcNow;
            }
        }

        if (send) _ = SendAsync(key, value, total);
        else if (wait is { } delay) _ = FlushLaterAsync(key, delay);
    }

    private async Task FlushLaterAsync((int BookId, bool Text) key, TimeSpan delay)
    {
        await Task.Delay(delay);

        double value, total;

        lock (_gate)
        {
            if (!_pending.TryGetValue(key, out var entry) || !entry.Scheduled) return;

            entry.Scheduled = false;
            entry.LastSent = DateTime.UtcNow;
            value = entry.Value;
            total = entry.Total;
        }

        await SendAsync(key, value, total);
    }

    private async Task SendAsync((int BookId, bool Text) key, double value, double total)
    {
        try
        {
            if (await database.GetBookAsync(key.BookId) is not { ServerItemId: { } itemId } book) return;
            if (servers.ForBook(book) is not { } server) return;

            if (key.Text)
                await server.SetEbookProgressAsync(itemId, value / total);
            else
                await server.SetProgressAsync(itemId, value / 1000, total / 1000);
        }
        catch (Exception ex)
        {
            AppLog.Info($"progress not sent for book {key.BookId} ({ex.Message})");
        }
    }

    // ---- What this device has done, and what it has already been offered ----
    //
    // Kept apart from the reading state's own timestamp on purpose. That one moves whenever the
    // position is written, and it is written when a page closes or a paused player sits on screen —
    // so a device that had merely been looked at always seemed newer than the one actually playing,
    // and the offer never came.

    private static string ActiveKey(int bookId) => $"sync.active.{bookId}";
    private static string SeenKey(int bookId) => $"sync.seen.{bookId}";

    private static void MarkActive(int bookId) =>
        Preferences.Default.Set(ActiveKey(bookId), DateTime.UtcNow.Ticks);

    private static DateTimeOffset LastActive(int bookId) =>
        new(new DateTime(Preferences.Default.Get(ActiveKey(bookId), 0L), DateTimeKind.Utc));

    /// <summary>
    /// Records that this server position has been offered and answered, either way, so the same one
    /// is not offered again. A newer one — the other device still playing — still will be.
    /// </summary>
    public void Acknowledge(ElsewherePosition position) =>
        Preferences.Default.Set(SeenKey(position.BookId), position.When.ToUnixTimeMilliseconds());

    /// <summary>
    /// Where the server says this book was left, when another device got there after this one last
    /// did anything with the book, it is somewhere else, and it has not been offered already. Null
    /// when there is nothing worth asking about, or the server does not answer in time — opening a
    /// book or pressing play must never wait long on the network.
    /// </summary>
    /// <param name="textLength">The book's text length, to turn the server's fraction back into a place.</param>
    /// <param name="patience">How long the server gets to answer.</param>
    public async Task<ElsewherePosition?> FindNewerAsync(
        Book book, bool forText, int textLength = 0, TimeSpan? patience = null)
    {
        if (book.ServerItemId is not { } itemId || servers.ForBook(book) is not { Account.IsConfigured: true } server)
            return null;

        try
        {
            using var timeout = new CancellationTokenSource(patience ?? TimeSpan.FromSeconds(5));
            var remote = await server.GetProgressAsync(itemId, timeout.Token);

            if (remote?.UpdatedAt is not { } when) return null;

            // Already offered and answered.
            if (when.ToUnixTimeMilliseconds() <= Preferences.Default.Get(SeenKey(book.Id), 0L)) return null;

            // This device did something with the book after the server last heard from anyone —
            // listening offline, say — and that is the newer truth. A little grace for clocks that
            // do not quite agree.
            if (when <= LastActive(book.Id).AddSeconds(10)) return null;

            var local = await database.GetReadingStateAsync(book.Id);

            if (forText)
            {
                if (remote.EbookFraction is not { } fraction || textLength <= 0) return null;

                var offset = (int)Math.Round(fraction * textLength);
                if (Math.Abs(offset - (local?.TextOffset ?? 0)) < SameTextChars) return null;

                return new ElsewherePosition(book.Id, null, offset, when);
            }

            var audioMs = (long)Math.Round(remote.CurrentTimeSeconds * 1000);
            if (audioMs <= 0 || Math.Abs(audioMs - (local?.AudioPositionMs ?? 0)) < SameAudioMs) return null;

            return new ElsewherePosition(book.Id, audioMs, null, when);
        }
        catch (Exception ex)
        {
            AppLog.Info($"could not read progress from the server for book {book.Id} ({ex.Message})");
            return null;
        }
    }
}
