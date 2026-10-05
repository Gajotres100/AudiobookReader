using AudioBookReader.Core.Books;
using AudioBookReader.Core.Data;
using AudioBookReader.Core.Models;

namespace AudioBookReader.App.Services;

/// <summary>
/// Every Audiobookshelf server the app knows, up to <see cref="MaxServers"/>, and which of them the
/// server screen is showing.
///
/// A book remembers the server it came from, and everything done for that book afterwards — playing
/// it from there, sending how far it got, asking where another device left it — goes to that server
/// and no other. The one being browsed is only the one being browsed: switching to another does not
/// move a single book.
///
/// The server set up before several could be kept becomes the first of them, under its original
/// keys, so nothing about it changes and nobody has to sign in again.
/// </summary>
public class ServerConnections(BookImporter importer, DownloadFolder folder, LibraryDatabase database) : IStreamSource
{
    public const int MaxServers = 5;

    private const string IdsKey = "servers.ids";
    private const string ActiveKey = "servers.active";
    private const string OpenOnStartKey = "server.openOnStart";

    private readonly Dictionary<string, ServerConnection> _connections = [];
    private readonly object _gate = new();

    /// <summary>A server being added whose sign-in has not gone through yet, and so is not kept.</summary>
    private string? _pending;

    /// <summary>Raised when a server is added, removed, signed in or switched to.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Land on the server shelf instead of the local library when the app starts. One setting for
    /// the app, not per server: it opens whichever server was being looked at last.
    /// </summary>
    public bool OpenServerOnStart
    {
        get => Preferences.Default.Get(OpenOnStartKey, false);
        set => Preferences.Default.Set(OpenOnStartKey, value);
    }

    /// <summary>The kept servers, in the order they were added.</summary>
    private List<string> Ids
    {
        get
        {
            var stored = Preferences.Default.Get<string?>(IdsKey, null);

            if (stored is null)
            {
                // Before there could be several there was one, under the original keys.
                return new ServerAccount(ServerAccount.LegacyId).IsConfigured ? [ServerAccount.LegacyId] : [];
            }

            return [.. stored.Split(',', StringSplitOptions.RemoveEmptyEntries)];
        }
        set => Preferences.Default.Set(IdsKey, string.Join(',', value));
    }

    /// <summary>Every configured server, and the one being added if there is one.</summary>
    public IReadOnlyList<ServerConnection> All
    {
        get
        {
            var ids = Ids;
            if (_pending is { } pending && !ids.Contains(pending)) ids.Add(pending);

            return [.. ids.Select(Get)];
        }
    }

    public bool IsConfigured => Ids.Any(id => Get(id).Account.IsConfigured);

    /// <summary>Whether another server can be added.</summary>
    public bool CanAdd => Ids.Count < MaxServers && _pending is null;

    /// <summary>The server the server screen shows: the last one looked at, or the first.</summary>
    public ServerConnection? Active
    {
        get
        {
            var all = All;
            var id = Preferences.Default.Get<string?>(ActiveKey, null);

            return all.FirstOrDefault(c => c.Id == id) ?? all.FirstOrDefault();
        }
    }

    public void Activate(string id)
    {
        Preferences.Default.Set(ActiveKey, id);

        // Leaving a server that was being added and never signed in to drops it.
        if (_pending is { } pending && pending != id && !Get(pending).Account.IsConfigured) _pending = null;

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Starts adding a server and shows it. Kept only once its sign-in succeeds, so abandoning the
    /// form leaves nothing behind.
    /// </summary>
    public ServerConnection? Add()
    {
        if (!CanAdd) return null;

        // Short, since it becomes part of every streamed book's location; unique is all it needs.
        var id = Guid.NewGuid().ToString("N")[..8];
        _pending = id;

        Activate(id);
        return Get(id);
    }

    /// <summary>
    /// Forgets a server: its address, tokens and stored sign-in. Books that came from it stay in the
    /// library; ones that play from it stop playing until it is added again.
    /// </summary>
    public void Remove(string id)
    {
        Get(id).Disconnect();

        Ids = [.. Ids.Where(i => i != id)];
        if (_pending == id) _pending = null;

        lock (_gate) _connections.Remove(id);

        if (Preferences.Default.Get<string?>(ActiveKey, null) == id) Preferences.Default.Remove(ActiveKey);

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A server by its id, or null when it is not (or no longer) configured.</summary>
    public ServerConnection? For(string? id)
    {
        id ??= ServerAccount.LegacyId;
        return All.FirstOrDefault(c => c.Id == id);
    }

    /// <summary>The server a book came from, or null for a book that came from none.</summary>
    public ServerConnection? ForBook(Book book) =>
        book.ServerItemId is null ? null : For(book.ServerId ?? ServerAccount.LegacyId);

    /// <summary>Puts every stored sign-in back into its client, for anything played before a server page opened.</summary>
    public async Task RestoreAllAsync()
    {
        foreach (var connection in All.Where(c => c.Account.IsConfigured))
        {
            try
            {
                await connection.RestoreAsync();
            }
            catch (Exception ex)
            {
                AppLog.Info($"server {connection.Id}: not restored ({ex.Message})");
            }
        }
    }

    private ServerConnection Get(string id)
    {
        lock (_gate)
        {
            if (_connections.TryGetValue(id, out var existing)) return existing;

            var account = new ServerAccount(id);
            account.Changed += (_, _) => OnAccountChanged(account);

            var connection = new ServerConnection(account, importer, folder, database);
            _connections[id] = connection;
            return connection;
        }
    }

    /// <summary>A server that has just signed in is kept from now on.</summary>
    private void OnAccountChanged(ServerAccount account)
    {
        if (account.IsConfigured && !Ids.Contains(account.Id))
        {
            Ids = [.. Ids, account.Id];
            if (_pending == account.Id) _pending = null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ---- Streaming: each streamed book goes to its own server ----

    string? IStreamSource.UrlFor(string location)
    {
        if (!StreamedAudio.TryParse(location, out var serverId, out var itemId, out var ino)) return null;
        if (For(serverId)?.StreamUrl(itemId, ino) is not { } url) return null;

        // The stretch rides along on the address, for the player to turn into ranges of the file.
        return StreamedAudio.TrySlice(location, out var offset, out var length)
            ? $"{url}{StreamedAudio.UrlSliceMark}{offset}-{length}"
            : url;
    }

    Task<string?> IStreamSource.FreshTokenAsync(string address, CancellationToken ct)
    {
        var server = StreamedAudio.TryParse(address, out var serverId, out _, out _)
            ? For(serverId)
            : All.FirstOrDefault(c => c.Serves(address));

        return server?.FreshTokenAsync(ct) ?? Task.FromResult<string?>(null);
    }

    Task<Stream> IStreamSource.OpenAsync(string location, CancellationToken ct)
    {
        if (!StreamedAudio.TryParse(location, out var serverId, out var itemId, out var ino))
            throw new ArgumentException($"Not a streamed location: {location}", nameof(location));

        var server = For(serverId) ?? throw new InvalidOperationException("The server this book plays from is no longer set up.");

        return StreamedAudio.TrySlice(location, out var offset, out var length)
            ? OpenSliceAsync(server, itemId, ino, offset, length, ct)
            : server.OpenStreamAsync(itemId, ino, ct);
    }

    private static async Task<Stream> OpenSliceAsync(
        ServerConnection server, string itemId, string ino, long offset, long length, CancellationToken ct) =>
        new SubStream(await server.OpenStreamAsync(itemId, ino, ct), offset, length);
}
