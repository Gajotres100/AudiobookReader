using AudioBookReader.Core.Servers;

namespace AudioBookReader.App.Services;

/// <summary>
/// The app's connection to an Audiobookshelf server.
///
/// One of these for the whole app, because the token and the connection are one thing and every
/// screen that asks about the server is asking about the same one. It wraps the Core client with
/// the stored account, so no screen has to know how signing in works or where the token is kept.
/// </summary>
public class ServerConnection(ServerAccount account, BookImporter importer)
{
    private readonly AudiobookshelfClient _client = new(new HttpClient
    {
        // Long, because a library scan on a server with a few thousand titles is not fast, and a
        // download of a whole audiobook is slower still.
        Timeout = TimeSpan.FromMinutes(30),
    });

    public bool IsConnected { get; private set; }

    private bool _listening;

    /// <summary>
    /// Stores every pair the server issues, not only the one sign-in returned.
    ///
    /// Subscribed once, lazily, because the client is built with this object and the event fires
    /// from inside its own calls.
    /// </summary>
    private void KeepTokens()
    {
        if (_listening) return;

        _listening = true;
        _client.TokensChanged += async (_, tokens) => await account.SaveTokensAsync(tokens.Access, tokens.Refresh);
    }

    public string? Url => account.Url;

    /// <summary>
    /// Restores the stored connection, if there is one.
    ///
    /// Cheap and does not talk to the server: it only puts the address and token back into the
    /// client. Whether they still work is discovered by the first request that needs them, which is
    /// the only honest way to find out anyway.
    /// </summary>
    /// <summary>Whether the sign-in is kept, so the switch can show its state.</summary>
    public Task<bool> RemembersSignInAsync() => account.RemembersSignInAsync();

    public async Task<bool> RestoreAsync()
    {
        if (account.Url is not { } url) return IsConnected = false;

        var (token, refresh) = await account.GetTokensAsync();

        // No token but a stored sign-in is still a connection: the first call renews it.
        if (token is null) return IsConnected = await RenewAsync();

        KeepTokens();
        _client.Connect(url, token, refresh);

        return IsConnected = true;
    }

    /// <summary>Signs in with a username and password.</summary>
    /// <param name="remember">
    /// Whether to keep the sign-in itself. The tokens alone last thirty unused days, and a book app
    /// can go longer than that between openings.
    /// </param>
    public async Task SignInAsync(
        string url,
        string username,
        string password,
        bool remember,
        CancellationToken ct = default)
    {
        KeepTokens();
        _client.Connect(url);

        var tokens = await _client.SignInAsync(username, password, ct);

        await account.SaveAsync(AudiobookshelfClient.Normalise(url), tokens.Access, tokens.Refresh);

        if (remember) await account.RememberSignInAsync(username, password);
        else account.ForgetSignIn();

        IsConnected = true;
    }

    /// <summary>
    /// Signs in again from what was stored, when the tokens have run out entirely.
    ///
    /// The last resort, and only reachable if the user asked for the sign-in to be kept. Everything
    /// short of this is handled by the refresh token inside the client.
    /// </summary>
    private async Task<bool> RenewAsync()
    {
        if (account.Url is not { } url) return false;

        var (username, password) = await account.GetSignInAsync();
        if (username is null || password is null) return false;

        try
        {
            AppLog.Info("server: tokens exhausted, signing in again from the stored sign-in");

            KeepTokens();
            _client.Connect(url);

            var tokens = await _client.SignInAsync(username, password);
            await account.SaveTokensAsync(tokens.Access, tokens.Refresh);

            return true;
        }
        catch (Exception ex)
        {
            // The password has been changed on the server, or the account is gone. Keeping it would
            // mean retrying it against every call from now on.
            AppLog.Info($"server: stored sign-in no longer works ({ex.Message})");
            account.ForgetSignIn();

            return false;
        }
    }

    /// <summary>
    /// Connects with a token the user pasted, from the server's own settings page.
    ///
    /// Offered because it is the better way round for anyone who cares: the token can be revoked
    /// without changing the account password, and the password never has to be typed into this app
    /// at all. It is checked against the server before it is kept, so a mistyped token is refused
    /// here rather than at some later screen.
    /// </summary>
    public async Task ConnectWithTokenAsync(string url, string token, CancellationToken ct = default)
    {
        KeepTokens();
        _client.Connect(url, token);

        await _client.GetLibrariesAsync(ct);

        // A pasted API token is the long-lived kind and has nothing to refresh with.
        await account.SaveAsync(AudiobookshelfClient.Normalise(url), token, refresh: null);
        IsConnected = true;
    }

    public void Disconnect()
    {
        account.Forget();
        IsConnected = false;
    }

    /// <summary>Where a book's cover can be fetched from, for a shelf to draw.</summary>
    public string CoverUrl(string itemId) => _client.CoverUrl(itemId);

    public Task<IReadOnlyList<ServerLibraryInfo>> GetLibrariesAsync(CancellationToken ct = default) =>
        Wrap(async () =>
        {
            var libraries = await _client.GetLibrariesAsync(ct);

            return (IReadOnlyList<ServerLibraryInfo>)
                [.. libraries.Where(l => l.IsBooks).Select(l => new ServerLibraryInfo(l.Id, l.Name))];
        });

    /// <summary>One book in full: its files, its chapter marks, and what it is made of.</summary>
    public Task<ServerBookDetail> GetBookAsync(string itemId, CancellationToken ct = default) =>
        Wrap(() => _client.GetBookAsync(itemId, ct));

    public Task<IReadOnlyList<ServerBook>> GetBooksAsync(
        string libraryId,
        IProgress<int>? found = null,
        CancellationToken ct = default) =>
        Wrap(() => _client.GetBooksAsync(libraryId, found, ct));

    /// <summary>
    /// Brings a book down from the server and adds it to the library here.
    ///
    /// The audio and the ebook are downloaded into app storage and then handed to the ordinary
    /// importer, so a book from a server goes through exactly the same path as one picked from the
    /// phone — same tag reading, same hashing, same pairing rules. Nothing downstream needs to know
    /// where it came from.
    /// </summary>
    public async Task<int> ImportAsync(
        ServerBook book,
        IProgress<ImportProgress>? progress = null,
        CancellationToken ct = default)
    {
        var detail = await Wrap(() => _client.GetBookAsync(book.Id, ct));

        // One file only, for now. A book split across forty files needs the app to hold more than
        // one audio path per book, and holding it as one long file it is not would be a lie the
        // chapter list and every seek would then repeat.
        if (detail.AudioFiles.Count > 1)
            throw new NotSupportedException(
                $"„{book.Title}” je na serveru razlomljen na {detail.AudioFiles.Count} datoteka, " +
                "a to još ne znam složiti u jednu knjigu.");

        int? bookId = null;

        // Deleted whichever way this goes. The importer makes its own copy in app storage, so a
        // download left behind is a second copy of a three-hundred-megabyte file that nothing will
        // ever open — and on a cancelled download it is half of one.
        var downloaded = new List<string>();

        try
            {
            if (detail.AudioFiles.Count == 1)
            {
                var file = detail.AudioFiles[0];

                var path = await DownloadAsync(
                    file.FileName,
                    (to, report) => _client.DownloadFileAsync(book.Id, file.Ino, to, report, ct),
                    $"Skidam zvuk — {book.Title}",
                    progress,
                    ct);

                downloaded.Add(path);

                var imported = await importer.ImportAudioAsync(
                    new PickedMedia(path, file.FileName), null, progress, ct);

                bookId = imported.Id;
            }

            if (detail.Ebook is { } ebook)
            {
                var path = await DownloadAsync(
                    ebook.FileName,
                    (to, report) => _client.DownloadEbookAsync(book.Id, to, report, ct),
                    $"Skidam tekst — {book.Title}",
                    progress,
                    ct);

                downloaded.Add(path);

                var imported = await importer.ImportEbookAsync(
                    new PickedMedia(path, ebook.FileName), bookId, progress, ct);

                bookId ??= imported.Id;
            }

            return bookId ?? throw new NotSupportedException(
                $"„{book.Title}” na serveru nema ni zvuka ni teksta.");
        }
        finally
        {
            foreach (var path in downloaded) TryDelete(path);
        }
    }

    /// <summary>
    /// Downloads into a temporary file, and hands the importer a path rather than a stream.
    ///
    /// The importer copies or references a file and reads its tags by seeking around it, which a
    /// network stream cannot do. The temporary copy is deleted whichever way the import goes: it
    /// succeeds and the importer has made its own copy, or it fails and this was rubbish.
    /// </summary>
    private static async Task<string> DownloadAsync(
        string fileName,
        Func<Stream, IProgress<double?>, Task> download,
        string message,
        IProgress<ImportProgress>? progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(AppPaths.Downloads);

        var path = Path.Combine(AppPaths.Downloads, Sanitise(fileName));

        try
        {
            await using (var file = File.Create(path))
            {
                var report = new Progress<double?>(fraction =>
                    progress?.Report(new ImportProgress(message, fraction ?? 0)));

                await download(file, report);
            }

            AppLog.Info($"server: downloaded '{fileName}' ({new FileInfo(path).Length:N0} bytes)");
            return path;
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    /// <summary>Keeps a server's filename from escaping the downloads folder or upsetting the disk.</summary>
    private static string Sanitise(string fileName)
    {
        var name = Path.GetFileName(fileName);

        foreach (var bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');

        return string.IsNullOrWhiteSpace(name) ? "download" : name;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // A leftover in the downloads folder is not worth failing an import over.
        }
    }

    /// <summary>
    /// Runs a call, and drops the connection when the server says the token is no longer good.
    ///
    /// Without this the app would go on believing it is signed in while every request is refused,
    /// which shows up as a library that is mysteriously always empty.
    /// </summary>
    private async Task<T> Wrap<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (ServerException ex) when (ex.NeedsSignIn)
        {
            // One more try, from the sign-in the user asked us to keep. This is the thirty-days-away
            // case: the refresh token is gone, and without this the only way back is typing
            // everything again.
            if (await RenewAsync()) return await call();

            IsConnected = false;
            throw;
        }
    }
}

public record ServerLibraryInfo(string Id, string Name);
