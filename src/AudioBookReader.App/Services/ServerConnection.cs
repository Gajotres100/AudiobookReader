using AudioBookReader.App.Resources.Strings;
using AudioBookReader.Core.Books;
using AudioBookReader.Core.Data;
using AudioBookReader.Core.Models;
using AudioBookReader.Core.Servers;

namespace AudioBookReader.App.Services;

/// <summary>
/// The app's connection to one Audiobookshelf server.
///
/// One of these per configured server, kept by <see cref="ServerConnections"/>, because the token and
/// the connection are one thing. It wraps the Core client with that server's stored account, so no
/// screen has to know how signing in works or where the token is kept.
/// </summary>
public class ServerConnection(
    ServerAccount account,
    BookImporter importer,
    DownloadFolder folder,
    LibraryDatabase database)
{
    private AudiobookshelfClient? _clientInstance;

    /// <summary>
    /// Built on first use rather than with this object, so that trusting a self-signed certificate
    /// — switched on in the sign-in form of a server being added — is in force for that very first
    /// connection instead of from the next launch.
    /// </summary>
    private AudiobookshelfClient _client => _clientInstance ??= new(new HttpClient(BuildHandler(account))
    {
        // Long, because a library scan on a server with a few thousand titles is not fast, and a
        // download of a whole audiobook is slower still.
        Timeout = TimeSpan.FromMinutes(30),
    });

    /// <summary>Which of the configured servers this is; stored with every book that came from it.</summary>
    public string Id => account.Id;

    /// <summary>How the server is shown to the user.</summary>
    public string Name => account.DisplayName;

    public ServerAccount Account => account;

    /// <summary>
    /// The default handler, unless the user has explicitly said this one server's certificate
    /// should not be checked.
    ///
    /// Read once, at the point this whole object is built, which is early in the app's life — the
    /// setting is meant to describe "this server, from now on" rather than something that can
    /// change mid-session, and .NET's handlers refuse to be reconfigured once a request has gone
    /// through one anyway.
    /// </summary>
    private static HttpClientHandler BuildHandler(ServerAccount account)
    {
        var handler = new HttpClientHandler();

        if (account.TrustAnyCertificate)
        {
            AppLog.Info($"server {account.Id}: certificate validation disabled for this connection (user opt-in)");
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        }

        return handler;
    }

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

        await _client.VerifyTokenAsync(ct);

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
    /// It lands in the folder the user chose — beside the audiobooks they already keep — and is
    /// then handed to the ordinary importer, so a book from a server goes through exactly the same
    /// path as one picked off the phone: same tag reading, same hashing, same pairing rules, and
    /// referenced where it lies rather than copied into app storage. It is copied in only if
    /// alignment is started, which is what that copy is for.
    ///
    /// With no folder chosen it falls back to app storage, which works but leaves the book
    /// invisible to everything else on the phone.
    /// </summary>
    /// <param name="toAppStorage">
    /// Keeps the book in the app's own storage instead of the chosen folder. Its own space, out of
    /// sight of everything else on the phone, and gone when the app is uninstalled — which is
    /// exactly right for a book someone wants nowhere near their own files, and wrong by default.
    /// </param>
    /// <param name="itemId">
    /// The book on the server, rather than the record a page happened to be holding. The service
    /// that runs this is handed an intent, not an object, and everything it needs is in the detail
    /// it fetches anyway.
    /// </param>
    /// <param name="wantAudio">
    /// Whether to fetch the narration. False when the book is already here with its audio and only
    /// its other half is missing — there is no sense pulling a gigabyte over again for that.
    /// </param>
    /// <param name="wantEbook">Whether to fetch the text, on the same terms.</param>
    /// <param name="attachTo">
    /// An entry already in the library that this belongs to, so a missing half joins the book it
    /// completes instead of arriving as a second copy of the same title.
    /// </param>
    public async Task<int> ImportAsync(
        string itemId,
        IProgress<ImportProgress>? progress = null,
        CancellationToken ct = default,
        bool toAppStorage = false,
        bool wantAudio = true,
        bool wantEbook = true,
        int? attachTo = null)
    {
        var detail = await Wrap(() => _client.GetBookAsync(itemId, ct));
        var book = detail.Book;

        // One file only, for now. A book split across forty files needs the app to hold more than
        // one audio path per book, and holding it as one long file it is not would be a lie the
        // chapter list and every seek would then repeat.
        //
        // Only when the audio is actually wanted: a split book whose text is the missing half can
        // still have that text fetched, and refusing the whole errand over the half nobody asked
        // for would be the app being difficult for its own sake.
        if (wantAudio && detail.AudioFiles.Count > 1)
            throw new NotSupportedException(
                string.Format(Strings.Server_SplitFilesLong, detail.AudioFiles.Count));

        // Seeded with the book this is completing, when it is completing one.
        int? bookId = attachTo;

        // Everything written for this import, so a download that does not finish leaves nothing
        // behind. What succeeds is kept: it is the book itself, in the user's own folder.
        var written = new List<string>();
        var finished = false;

        try
            {
            if (wantAudio && detail.AudioFiles.Count == 1)
            {
                var file = detail.AudioFiles[0];

                var path = await DownloadAsync(
                    toAppStorage ? null : Shelf(book),
                    file.FileName,
                    (to, report) => _client.DownloadFileAsync(itemId, file.Ino, to, report, ct),
                    string.Format(Strings.Server_DownloadingAudio, book.Title),
                    progress,
                    ct);

                written.Add(path);

                var imported = await importer.ImportAudioAsync(
                    new PickedMedia(path, file.FileName), bookId, progress, ct);

                bookId = imported.Id;
            }

            if (wantEbook && detail.Ebook is { } ebook)
            {
                var path = await DownloadAsync(
                    toAppStorage ? null : Shelf(book),
                    ebook.FileName,
                    (to, report) => _client.DownloadEbookAsync(itemId, to, report, ct),
                    string.Format(Strings.Server_DownloadingText, book.Title),
                    progress,
                    ct);

                written.Add(path);

                var imported = await importer.ImportEbookAsync(
                    new PickedMedia(path, ebook.FileName), bookId, progress, ct);

                bookId ??= imported.Id;
            }

            finished = true;

            var id = bookId ?? throw new NotSupportedException(Strings.Server_NothingToDownload);

            // Remembered so this book is recognised the next time its item is opened, instead of
            // being matched on a title the two sides spell differently.
            await database.LinkToServerAsync(id, itemId, Id);

            return id;
        }
        finally
        {
            if (!finished) foreach (var location in written) Discard(location);
            else foreach (var location in written) DiscardIfTemporary(location);
        }
    }

    /// <summary>
    /// Adds a book to the library that plays from the server, downloading only its text.
    ///
    /// For a device short of space — a television with a few hundred megabytes free cannot hold a
    /// ten-hour book at all — and for anyone who would rather not keep a gigabyte for something they
    /// will listen to once. The text still comes down, because it is small and the reader needs it
    /// page by page; the audio stays where it is and is fetched as it plays.
    /// </summary>
    /// <param name="attachTo">A book already here that this completes, as for a download.</param>
    public async Task<int> AddStreamingAsync(
        string itemId,
        IProgress<ImportProgress>? progress = null,
        CancellationToken ct = default,
        bool wantEbook = true,
        int? attachTo = null)
    {
        var detail = await Wrap(() => _client.GetBookAsync(itemId, ct));
        var book = detail.Book;

        // One file, for the same reason a download needs one: a book is one timeline to this app.
        if (detail.AudioFiles.Count != 1)
            throw new NotSupportedException(
                detail.AudioFiles.Count == 0
                    ? Strings.Server_NothingToDownload
                    : string.Format(Strings.Server_SplitFilesLong, detail.AudioFiles.Count));

        var file = detail.AudioFiles[0];
        var location = StreamedAudio.Location(Id, itemId, file.Ino);

        progress?.Report(new ImportProgress(Strings.Server_PreparingStream, 0));

        // The same hash a downloaded copy of this file would get, from three small pieces of it. On a
        // worker: the hash seeks synchronously, and a seek can close a response, which Android will
        // not allow to touch the network from the UI thread this was started on.
        var hash = await Task.Run(async () =>
        {
            await using var stream = await OpenStreamAsync(itemId, file.Ino, ct);
            return await ContentHash.ComputeAsync(stream, ct);
        }, ct);

        var durationMs = (long)Math.Round(
            (file.DurationSeconds > 0 ? file.DurationSeconds : book.DurationSeconds) * 1000);

        var attachment = new AudioAttachment(
            location,
            hash,
            durationMs,
            ChaptersOf(detail, durationMs),
            book.Title,
            book.Author,
            await SaveCoverAsync(itemId, book.Title, ct));

        var imported = await importer.ImportStreamedAudioAsync(attachment, attachTo);
        var id = imported.Id;

        if (wantEbook && detail.Ebook is { } ebook)
        {
            var written = new List<string>();
            var finished = false;

            try
            {
                var path = await DownloadAsync(
                    null,
                    ebook.FileName,
                    (to, report) => _client.DownloadEbookAsync(itemId, to, report, ct),
                    string.Format(Strings.Server_DownloadingText, book.Title),
                    progress,
                    ct);

                written.Add(path);

                await importer.ImportEbookAsync(new PickedMedia(path, ebook.FileName), id, progress, ct);
                finished = true;
            }
            finally
            {
                if (!finished) foreach (var path in written) Discard(path);
                else foreach (var path in written) DiscardIfTemporary(path);
            }
        }

        await database.LinkToServerAsync(id, itemId, Id);
        return id;
    }

    /// <summary>The server's chapter marks as the library keeps them, or the whole book as one when it has none.</summary>
    private static List<Chapter> ChaptersOf(ServerBookDetail detail, long durationMs)
    {
        var chapters = detail.Chapters
            .Select((c, i) => new Chapter
            {
                Index = i,
                Title = c.Title,
                StartMs = (long)Math.Round(c.StartSeconds * 1000),
                EndMs = (long)Math.Round(c.EndSeconds * 1000),
            })
            .Where(c => c.EndMs > c.StartMs)
            .ToList();

        if (chapters.Count > 0) return chapters;

        return [new Chapter { Index = 0, Title = detail.Book.Title, StartMs = 0, EndMs = durationMs }];
    }

    /// <summary>The server's cover for the item, kept with the library, or null when it has none to give.</summary>
    private async Task<string?> SaveCoverAsync(string itemId, string title, CancellationToken ct)
    {
        try
        {
            using var cover = new MemoryStream();
            await _client.DownloadCoverAsync(itemId, cover, ct);

            return cover.Length > 0
                ? await BookImporter.SaveCoverAsync(cover.ToArray(), title, ct)
                : null;
        }
        catch (Exception ex)
        {
            // A book with no cover is still a book.
            AppLog.Info($"server: no cover for {itemId} ({ex.Message})");
            return null;
        }
    }

    // ---- Progress, shared with every other device signed in to the same account ----

    /// <summary>Whether a server has been set up at all, so there is anywhere to share progress with.</summary>
    public bool IsConfigured => account.Url is not null;

    public async Task<ServerProgress?> GetProgressAsync(string itemId, CancellationToken ct = default)
    {
        await EnsureRestoredAsync();
        return await Wrap(() => _client.GetProgressAsync(itemId, ct));
    }

    public async Task SetProgressAsync(string itemId, double seconds, double durationSeconds, CancellationToken ct = default)
    {
        await EnsureRestoredAsync();
        await Wrap(async () =>
        {
            await _client.SetProgressAsync(itemId, seconds, durationSeconds, ct);
            return true;
        });
    }

    public async Task SetEbookProgressAsync(string itemId, double fraction, CancellationToken ct = default)
    {
        await EnsureRestoredAsync();
        await Wrap(async () =>
        {
            await _client.SetEbookProgressAsync(itemId, fraction, ct);
            return true;
        });
    }

    // ---- Streaming, for the player and for copying a streamed book in ----

    /// <summary>Puts the stored connection back first, for a book played before any server page was opened.</summary>
    private async Task EnsureRestoredAsync()
    {
        if (!IsConnected || !_client.IsSignedIn) await RestoreAsync();
    }

    /// <summary>The address a file of this server plays from, without credentials.</summary>
    public string? StreamUrl(string itemId, string ino) =>
        account.Url is { } url
            ? AudiobookshelfClient.Normalise(url)
              + $"/api/items/{Uri.EscapeDataString(itemId)}/file/{Uri.EscapeDataString(ino)}"
            : null;

    /// <summary>Whether an address worked out from a streamed book points at this server.</summary>
    public bool Serves(string address) =>
        account.Url is { } url
        && address.StartsWith(AudiobookshelfClient.Normalise(url) + "/", StringComparison.OrdinalIgnoreCase);

    public async Task<string?> FreshTokenAsync(CancellationToken ct = default)
    {
        await EnsureRestoredAsync();
        return await _client.FreshTokenAsync(ct);
    }

    public async Task<Stream> OpenStreamAsync(string itemId, string ino, CancellationToken ct = default)
    {
        await EnsureRestoredAsync();

        return await Wrap(() => HttpRangeStream.OpenAsync(
            (from, to, token) => _client.OpenFileAsync(itemId, ino, from, to, token), ct));
    }

    /// <summary>
    /// Writes the file where it is meant to live, and hands the importer a location rather than a
    /// stream.
    ///
    /// The importer reads tags by seeking around the file, which a network stream cannot do, so
    /// the download has to land somewhere first — and where it lands is the user's own folder
    /// whenever they have chosen one.
    /// </summary>
    /// <summary>
    /// Where a book goes under the chosen folder: the author, then the book.
    ///
    /// The audio and the text of one title land together, and two books by the same author land
    /// under one name. That is how every other audiobook tool arranges a library on disk, and it is
    /// what keeps a folder of two hundred books navigable.
    /// </summary>
    private static string[] Shelf(ServerBook book) =>
    [
        DownloadFolder.SafeName(book.Author ?? Strings.Server_UnknownAuthor),
        DownloadFolder.SafeName(book.Title),
    ];

    /// <param name="folders">Where under the chosen folder it goes, or null to keep it in app storage.</param>
    private async Task<string> DownloadAsync(
        IReadOnlyList<string>? folders,
        string fileName,
        Func<Stream, IProgress<double?>, Task> download,
        string message,
        IProgress<ImportProgress>? progress,
        CancellationToken ct)
    {
        var (stream, location) = await OpenDestinationAsync(folders, fileName);

        try
        {
            await using (stream)
            {
                // Reported per whole percent, not per chunk. The copy hands back progress every
                // 128 KB, which on a three hundred megabyte book is a couple of thousand updates,
                // each one marshalled to the UI thread and each one rebuilding a notification.
                var lastPercent = -1;

                var report = new Progress<double?>(fraction =>
                {
                    var percent = (int)((fraction ?? 0) * 100);
                    if (percent == lastPercent) return;

                    lastPercent = percent;
                    progress?.Report(new ImportProgress(message, fraction ?? 0));
                });

                await download(stream, report);
            }

            AppLog.Info($"server: '{fileName}' written to {location}");
            return location;
        }
        catch
        {
            Discard(location);
            throw;
        }
    }

    private async Task<(Stream Stream, string Location)> OpenDestinationAsync(
        IReadOnlyList<string>? folders,
        string fileName)
    {
        if (folders is not null && folder.IsChosen)
        {
            try
            {
                return await folder.CreateAsync(folders, Sanitise(fileName));
            }
            catch (Exception ex)
            {
                // The folder was moved, or the card holding it was taken out. Falling back beats
                // refusing the download, and the log says which happened.
                AppLog.Info($"chosen download folder unusable ({ex.Message}); using app storage");
            }
        }

        Directory.CreateDirectory(AppPaths.Downloads);

        var path = Path.Combine(AppPaths.Downloads, Sanitise(fileName));
        return (File.Create(path), path);
    }

    /// <summary>Removes something this wrote, wherever it wrote it.</summary>
    private void Discard(string location)
    {
        if (IsStaging(location)) TryDelete(location);
        else folder.Delete(location);
    }

    /// <summary>
    /// Removes it only if it was app storage.
    ///
    /// A book in the user's folder is the book, and the library refers to it there. A file in the
    /// app's own downloads directory is a staging copy the importer has already duplicated, so
    /// leaving it would be a second copy of the whole book that nothing will ever open.
    /// </summary>
    private static void DiscardIfTemporary(string location)
    {
        if (IsStaging(location)) TryDelete(location);
    }

    /// <summary>
    /// Whether a location is the app's own staging copy rather than the user's folder.
    ///
    /// Asked by where it is, not by what it looks like. The test used to be "not a content:// URI",
    /// which is what the user's folder happens to be on Android — and on iOS a file in the user's
    /// folder is an ordinary path, so every book downloaded there was deleted the moment its import
    /// succeeded: an empty folder, and a library entry pointing at audio that was no longer there.
    /// </summary>
    private static bool IsStaging(string location) =>
        location.StartsWith(AppPaths.Downloads, StringComparison.Ordinal);

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
