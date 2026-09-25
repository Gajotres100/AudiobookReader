using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AudioBookReader.Core.Servers;

/// <summary>
/// Thrown when the server answers, but not with what was asked for.
///
/// Separate from a network failure on purpose: "the server said no" and "there is no server there"
/// need different words in front of the user, and only one of them is worth retrying.
/// </summary>
/// <summary>
/// What the app has to keep in order to stay signed in.
///
/// Two, not one, and both matter. The access token is what every request carries and it is good for
/// about an hour; the refresh token is what buys the next one, for thirty days, and it is rotated
/// each time it is used — so storing the pair from sign-in and never updating it is only slightly
/// better than storing nothing.
/// </summary>
public record ServerTokens(string Access, string? Refresh);

public sealed class ServerException(string message, HttpStatusCode? status = null, Exception? inner = null)
    : Exception(message, inner)
{
    public HttpStatusCode? Status { get; } = status;

    /// <summary>True when the token is gone or wrong, which is the one failure the app can fix.</summary>
    public bool NeedsSignIn => Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
}

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Credentials))]
[JsonSerializable(typeof(LoginResponse))]
[JsonSerializable(typeof(LibrariesResponse))]
[JsonSerializable(typeof(ItemsResponse))]
[JsonSerializable(typeof(WireItem))]
[JsonSerializable(typeof(WireProgress))]
[JsonSerializable(typeof(ProgressUpdate))]
internal partial class ServerJsonContext : JsonSerializerContext;

/// <summary>
/// Talks to an Audiobookshelf server.
///
/// Lives in Core with nothing but <see cref="HttpClient"/> so it can be exercised without a phone,
/// and so the same code could one day serve a desktop tool. It knows about books, not about this
/// app's library: it hands back plain records and copies bytes into streams the caller opens.
///
/// Everything is addressed against a base URL the user typed, so the URL is normalised once here
/// rather than at every call site — people type "myserver:13378", "http://myserver/", and
/// "https://books.example.com/audiobookshelf" and mean the same thing by all three.
/// </summary>
public class AudiobookshelfClient(HttpClient http)
{
    private string? _baseUrl;
    private string? _token;
    private string? _refreshToken;

    /// <summary>The server this client is pointed at, normalised, or null before it is set.</summary>
    public string? BaseUrl => _baseUrl;

    public bool IsSignedIn => _token is not null;

    /// <summary>
    /// Raised whenever the server hands out a new pair, so the caller can store them.
    ///
    /// It happens on its own schedule, not only at sign-in: an access token lasts an hour, and the
    /// refresh that replaces it usually rotates the refresh token too. A caller that stores only
    /// what sign-in returned would be back to typing a password every hour.
    /// </summary>
    public event EventHandler<ServerTokens>? TokensChanged;

    /// <summary>
    /// Points the client at a server and, optionally, gives it tokens it already has.
    /// </summary>
    public void Connect(string baseUrl, string? token = null, string? refreshToken = null)
    {
        _baseUrl = Normalise(baseUrl);
        _token = token;
        _refreshToken = refreshToken;
    }

    /// <summary>
    /// Turns what a person typed into a URL that can be built on.
    ///
    /// A bare host means http, not https: these servers are usually on a home network with no
    /// certificate, and defaulting to https there fails in a way that reads as "the app is broken"
    /// rather than "add the scheme".
    /// </summary>
    public static string Normalise(string baseUrl)
    {
        var trimmed = (baseUrl ?? "").Trim().TrimEnd('/');

        if (trimmed.Length == 0) throw new ArgumentException(CoreStrings.Server_EmptyAddress, nameof(baseUrl));

        if (!trimmed.Contains("://", StringComparison.Ordinal)) trimmed = "http://" + trimmed;

        return trimmed;
    }

    /// <summary>
    /// How long signing in may take before the address is taken to be wrong.
    ///
    /// The HttpClient this is handed waits half an hour, because a whole audiobook downloads through
    /// it. A sign-in is a few hundred bytes — but to an address where nothing answers at all, a
    /// mistyped IP on the home network, the connection neither succeeds nor fails, and the screen
    /// said "Connecting…" for as long as anyone was willing to watch it. That read as the app
    /// ignoring a wrong address rather than reporting it.
    /// </summary>
    public static readonly TimeSpan QuickTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Runs a small request under <see cref="QuickTimeout"/>, saying which address did not answer.</summary>
    private async Task<T> QuicklyAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(QuickTimeout);

        try
        {
            return await call(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ServerException(string.Format(CoreStrings.Server_CheckAddress, _baseUrl));
        }
    }

    /// <summary>Signs in and keeps the tokens. Returns them so the caller can store them.</summary>
    public Task<ServerTokens> SignInAsync(
        string username,
        string password,
        CancellationToken ct = default) => QuicklyAsync(async quick =>
    {
        HttpResponseMessage response;

        try
        {
            response = await SendAsync(
                () =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, Url("/login"))
                    {
                        Content = JsonContent.Create(
                            new Credentials(username, password), ServerJsonContext.Default.Credentials),
                    };

                    // Without this the server keeps the refresh token to itself, in a cookie, and
                    // hands back only an access token good for an hour. This is the header its own
                    // mobile app sends, and it is the difference between signing in once and hourly.
                    request.Headers.Add("x-return-tokens", "true");
                    return request;
                },
                authenticated: false,
                quick);
        }
        catch (ServerException ex) when (ex.Status == HttpStatusCode.Unauthorized)
        {
            // The same 401 that means "your sign-in has lapsed" everywhere else means "that was the
            // wrong password" here, and saying the former to someone who just typed it is baffling.
            throw new ServerException(CoreStrings.Server_WrongCredentials, HttpStatusCode.Unauthorized, ex);
        }

        var login = await ReadAsync(response, ServerJsonContext.Default.LoginResponse, quick);

        return Adopt(login?.User)
               ?? throw new ServerException(CoreStrings.Server_NoToken);
    }, ct);

    /// <summary>
    /// Checks a pasted API token by asking for something only a signed-in client may see, under the
    /// same short wait as signing in.
    /// </summary>
    public Task VerifyTokenAsync(CancellationToken ct = default) => QuicklyAsync(async quick =>
    {
        try
        {
            return await GetLibrariesAsync(quick);
        }
        catch (ServerException ex) when (ex.NeedsSignIn)
        {
            throw new ServerException(CoreStrings.Server_TokenRejected, ex.Status, ex);
        }
    }, ct);

    /// <summary>
    /// Takes whatever the server offered, newest scheme first.
    ///
    /// Older servers issue one long-lived API token as <c>token</c> and nothing else; newer ones
    /// issue a short <c>accessToken</c> and a <c>refreshToken</c> beside it. Preferring the access
    /// token and keeping the old field as a fallback means both kinds of server work without the
    /// caller knowing which it is talking to.
    /// </summary>
    private ServerTokens? Adopt(LoginUser? user)
    {
        if ((user?.AccessToken ?? user?.Token) is not { Length: > 0 } access) return null;

        _token = access;
        _refreshToken = user?.RefreshToken ?? _refreshToken;

        var tokens = new ServerTokens(access, _refreshToken);
        TokensChanged?.Invoke(this, tokens);

        return tokens;
    }

    /// <summary>
    /// One refresh at a time.
    ///
    /// The server rotates the refresh token each time it is used, so two refreshes racing — a
    /// streaming player and a page both finding the hour up at once — would spend it twice, and the
    /// second would find it already spent and sign the user out.
    /// </summary>
    private readonly SemaphoreSlim _refreshing = new(1, 1);

    /// <summary>Trades the refresh token for a fresh access token.</summary>
    /// <param name="stale">The access token that was found wanting; a refresh already done since is not repeated.</param>
    /// <returns>True when it worked and the call that prompted it is worth retrying.</returns>
    private async Task<bool> RefreshAsync(CancellationToken ct, string? stale = null)
    {
        var observed = stale ?? _token;

        await _refreshing.WaitAsync(ct);

        try
        {
            // Someone else refreshed while this waited, and the token they got is the answer.
            if (_token is not null && _token != observed) return true;

            return await RefreshCoreAsync(ct);
        }
        finally
        {
            _refreshing.Release();
        }
    }

    private async Task<bool> RefreshCoreAsync(CancellationToken ct)
    {
        if (_refreshToken is not { Length: > 0 } refresh) return false;

        HttpResponseMessage response;

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, Url("/auth/refresh"));
            request.Headers.Add("x-refresh-token", refresh);

            response = await http.SendAsync(request, ct);
        }
        catch (Exception)
        {
            return false;
        }

        if (!response.IsSuccessStatusCode)
        {
            response.Dispose();

            // Thirty days gone, or the session was revoked from the server. Either way this token
            // is spent, and keeping it would mean retrying against it forever.
            _refreshToken = null;
            return false;
        }

        var body = await ReadAsync(response, ServerJsonContext.Default.LoginResponse, ct);
        return Adopt(body?.User) is not null;
    }

    public async Task<IReadOnlyList<ServerLibrary>> GetLibrariesAsync(CancellationToken ct = default)
    {
        var response = await GetAsync("/api/libraries", ct);
        var body = await ReadAsync(response, ServerJsonContext.Default.LibrariesResponse, ct);

        return
        [
            .. (body?.Libraries ?? [])
                .Where(l => l.Id is not null)
                .Select(l => new ServerLibrary(l.Id!, l.Name ?? "", l.MediaType ?? ""))
        ];
    }

    /// <summary>
    /// Everything in a library, in title order.
    ///
    /// Paged through here rather than by the caller. A library of a few thousand titles is one
    /// request at a time to the server and one list to whoever asked, and the alternative is every
    /// screen learning how the server counts.
    /// </summary>
    public async Task<IReadOnlyList<ServerBook>> GetBooksAsync(
        string libraryId,
        IProgress<int>? found = null,
        CancellationToken ct = default)
    {
        var books = new List<ServerBook>();

        for (var page = 0; ; page++)
        {
            var response = await GetAsync(
                $"/api/libraries/{Uri.EscapeDataString(libraryId)}/items?limit={PageSize}&page={page}&sort=media.metadata.title",
                ct);

            var body = await ReadAsync(response, ServerJsonContext.Default.ItemsResponse, ct);
            var results = body?.Results ?? [];

            books.AddRange(results.Select(Describe).OfType<ServerBook>());
            found?.Report(books.Count);

            if (results.Count < PageSize) return books;
        }
    }

    private const int PageSize = 100;

    public async Task<ServerBookDetail> GetBookAsync(string itemId, CancellationToken ct = default)
    {
        var response = await GetAsync($"/api/items/{Uri.EscapeDataString(itemId)}", ct);
        var item = await ReadAsync(response, ServerJsonContext.Default.WireItem, ct);

        if (item is null || Describe(item) is not { } book)
            throw new ServerException(CoreStrings.Server_UnknownItem);

        var media = item.Media;

        var audio = (media?.AudioFiles ?? [])
            .Where(f => f.Ino is not null)
            .Select(f => new ServerFile(f.Ino!, f.Metadata?.Filename ?? f.Ino!, f.Duration ?? 0))
            .ToList();

        var ebook = media?.EbookFile is { Ino: not null } file
            ? new ServerFile(file.Ino, file.Metadata?.Filename ?? file.Ino, 0)
            : null;

        var chapters = (media?.Chapters ?? [])
            .Select(c => new ServerChapter(c.Start, c.End, c.Title ?? ""))
            .ToList();

        return new ServerBookDetail(book, audio, ebook, chapters);
    }

    /// <summary>
    /// Where one of an item's files can be played from.
    ///
    /// Without a token in it on purpose: a player asks for a book in pieces for as long as it is
    /// listened to, far longer than an access token lasts, so the caller adds a current one to each
    /// request (see <see cref="FreshTokenAsync"/>) rather than baking in one that expires mid-chapter.
    /// </summary>
    public string FileUrl(string itemId, string ino) =>
        Url($"/api/items/{Uri.EscapeDataString(itemId)}/file/{Uri.EscapeDataString(ino)}");

    /// <summary>Opens a stretch of one of an item's files, from one byte offset to another inclusive.</summary>
    public Task<HttpResponseMessage> OpenFileAsync(string itemId, string ino, long from, long to, CancellationToken ct = default) =>
        SendAsync(
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, FileUrl(itemId, ino));
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(from, to);
                return request;
            },
            authenticated: true,
            ct,
            completion: HttpCompletionOption.ResponseHeadersRead);

    /// <summary>The item's cover image, copied into a stream the caller owns.</summary>
    public Task DownloadCoverAsync(string itemId, Stream destination, CancellationToken ct = default) =>
        CopyAsync($"/api/items/{Uri.EscapeDataString(itemId)}/cover", destination, null, ct);

    /// <summary>
    /// An access token good for a while yet, renewing it first when it is about to run out.
    ///
    /// For the one caller that cannot retry on its own: a player fetching the book in pieces. Every
    /// other request here renews on a refusal and tries again; a player would instead stop dead an
    /// hour into the book with an error. Renewed a few minutes early, so the piece in flight at the
    /// hour mark never meets an expired token.
    /// </summary>
    public async Task<string?> FreshTokenAsync(CancellationToken ct = default)
    {
        var token = _token;

        if (token is not null
            && ExpiresAt(token) is { } expires
            && expires - DateTimeOffset.UtcNow < TimeSpan.FromMinutes(5))
        {
            await RefreshAsync(ct, token);
        }

        return _token;
    }

    /// <summary>When a signed token runs out, read from the token itself. Null for a token that never does.</summary>
    private static DateTimeOffset? ExpiresAt(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2) return null;

            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');

            using var json = JsonDocument.Parse(Convert.FromBase64String(payload));

            return json.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : null;
        }
        catch (Exception)
        {
            // Not a signed token at all — an older server's long-lived API key — and nothing to renew.
            return null;
        }
    }

    /// <summary>Where an item's cover can be fetched from, token included, for a view to load.</summary>
    public string CoverUrl(string itemId) =>
        Url($"/api/items/{Uri.EscapeDataString(itemId)}/cover?token={Uri.EscapeDataString(_token ?? "")}");

    /// <summary>
    /// Copies one of an item's files into a stream the caller owns.
    /// </summary>
    /// <param name="progress">Fraction copied, or null while the server does not say how big it is.</param>
    public Task DownloadFileAsync(
        string itemId,
        string ino,
        Stream destination,
        IProgress<double?>? progress = null,
        CancellationToken ct = default) =>
        CopyAsync(
            $"/api/items/{Uri.EscapeDataString(itemId)}/file/{Uri.EscapeDataString(ino)}/download",
            destination, progress, ct);

    /// <summary>The same for the item's ebook, which the server addresses separately.</summary>
    public Task DownloadEbookAsync(
        string itemId,
        Stream destination,
        IProgress<double?>? progress = null,
        CancellationToken ct = default) =>
        CopyAsync($"/api/items/{Uri.EscapeDataString(itemId)}/ebook", destination, progress, ct);

    /// <summary>Where the server thinks listening had reached, or null if it has never heard.</summary>
    public async Task<ServerProgress?> GetProgressAsync(string itemId, CancellationToken ct = default)
    {
        var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, Url($"/api/me/progress/{Uri.EscapeDataString(itemId)}")),
            authenticated: true,
            ct,
            // A book nobody has opened has no progress, and that is an answer rather than a failure.
            tolerate: HttpStatusCode.NotFound);

        if (response.StatusCode == HttpStatusCode.NotFound) return null;

        var body = await ReadAsync(response, ServerJsonContext.Default.WireProgress, ct);

        return body is null
            ? null
            : new ServerProgress(
                body.CurrentTime ?? 0,
                body.Duration ?? 0,
                body.IsFinished ?? false,
                body.EbookProgress,
                body.LastUpdate is > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(body.LastUpdate.Value) : null);
    }

    /// <summary>
    /// Tells the server how far through the text reading has reached, as a fraction of the book.
    ///
    /// A fraction rather than the server's own location format, which describes a page in its web
    /// reader: between devices running this app the fraction of the same text comes back as exactly
    /// the same character, and the web reader at least shows the right percentage.
    /// </summary>
    public async Task SetEbookProgressAsync(string itemId, double fraction, CancellationToken ct = default)
    {
        var update = new ProgressUpdate(EbookProgress: Math.Clamp(fraction, 0, 1));

        await SendAsync(
            () => new HttpRequestMessage(
                HttpMethod.Patch, Url($"/api/me/progress/{Uri.EscapeDataString(itemId)}"))
            {
                Content = JsonContent.Create(update, ServerJsonContext.Default.ProgressUpdate),
            },
            authenticated: true,
            ct);
    }

    /// <summary>Tells the server where listening has reached.</summary>
    public async Task SetProgressAsync(
        string itemId,
        double currentTimeSeconds,
        double durationSeconds,
        CancellationToken ct = default)
    {
        var fraction = durationSeconds > 0
            ? Math.Clamp(currentTimeSeconds / durationSeconds, 0, 1)
            : 0;

        // Ninety-nine percent is finished as far as anyone listening is concerned; the last seconds
        // of an audiobook are credits, and a book left one percent short never leaves the shelf.
        var update = new ProgressUpdate(
            CurrentTime: currentTimeSeconds,
            Duration: durationSeconds,
            Progress: fraction,
            IsFinished: fraction >= 0.99);

        await SendAsync(
            () => new HttpRequestMessage(
                HttpMethod.Patch, Url($"/api/me/progress/{Uri.EscapeDataString(itemId)}"))
            {
                Content = JsonContent.Create(update, ServerJsonContext.Default.ProgressUpdate),
            },
            authenticated: true,
            ct);
    }

    // ---- Plumbing ----

    private static ServerBook? Describe(WireItem item)
    {
        if (item.Id is null) return null;

        var media = item.Media;
        var (series, sequence) = ServerBook.SplitSeries(media?.Metadata?.SeriesName);

        return new ServerBook(
            item.Id,
            media?.Metadata?.Title ?? CoreStrings.Server_Untitled,
            media?.Metadata?.Author,
            media?.NumAudioFiles ?? media?.AudioFiles?.Count ?? 0,
            // Three places, because the server uses different ones depending on which call this
            // came from: the listing carries a format field, while the full reply for a single
            // book can carry only the file itself. Reading the format alone left a book with an
            // ebook plainly attached described as "audiobook only" on its own page — the same
            // trap the author fields set, one field lower.
            media?.EbookFormat
                ?? media?.EbookFileFormat
                ?? media?.EbookFile?.Metadata?.Ext?.TrimStart('.'),
            media?.Duration ?? 0,
            series,
            sequence,
            item.AddedAt is > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(item.AddedAt.Value) : null);
    }

    private string Url(string path) =>
        (_baseUrl ?? throw new ServerException(CoreStrings.Server_NotConfigured)) + path;

    private Task<HttpResponseMessage> GetAsync(string path, CancellationToken ct) =>
        SendAsync(() => new HttpRequestMessage(HttpMethod.Get, Url(path)), authenticated: true, ct);

    private async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> build,
        bool authenticated,
        CancellationToken ct,
        HttpStatusCode? tolerate = null,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead,
        bool mayRefresh = true)
    {
        var request = build();
        var used = _token;

        if (authenticated)
        {
            if (used is null) throw new ServerException(CoreStrings.Server_NotSignedIn, HttpStatusCode.Unauthorized);

            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", used);
        }

        HttpResponseMessage response;

        try
        {
            response = await http.SendAsync(request, completion, ct);
        }
        catch (HttpRequestException ex)
        {
            // The distinction the user needs: nothing answered, as opposed to something answering no.
            throw new ServerException(
                string.Format(CoreStrings.Server_Unreachable, _baseUrl), null, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ServerException(
                string.Format(CoreStrings.Server_NoAnswer, _baseUrl), null, ex);
        }

        if (response.IsSuccessStatusCode || response.StatusCode == tolerate) return response;

        // An access token lasts an hour, so this is the ordinary case rather than the exceptional
        // one: trade the refresh token for a new one and try the same call again, exactly once.
        if (response.StatusCode == HttpStatusCode.Unauthorized && authenticated && mayRefresh)
        {
            response.Dispose();

            if (await RefreshAsync(ct, used))
                return await SendAsync(build, authenticated, ct, tolerate, completion, mayRefresh: false);

            throw new ServerException(
                CoreStrings.Server_SignInExpired, HttpStatusCode.Unauthorized);
        }

        response.Dispose();

        throw new ServerException(
            response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => CoreStrings.Server_SignInExpired,
                HttpStatusCode.Forbidden => CoreStrings.Server_Forbidden,
                HttpStatusCode.NotFound => CoreStrings.Server_NotFound,
                _ => string.Format(CoreStrings.Server_Replied, (int)response.StatusCode),
            },
            response.StatusCode);
    }

    private async Task<T?> ReadAsync<T>(
        HttpResponseMessage response,
        JsonTypeInfo<T> type,
        CancellationToken ct)
    {
        using (response)
        {
            try
            {
                return await response.Content.ReadFromJsonAsync(type, ct);
            }
            catch (JsonException ex)
            {
                // Almost always a login page or a proxy's error page rather than the API: the URL
                // points at something, just not at Audiobookshelf.
                throw new ServerException(
                    string.Format(CoreStrings.Server_NotAudiobookshelf, _baseUrl),
                    response.StatusCode, ex);
            }
        }
    }

    private async Task CopyAsync(
        string path,
        Stream destination,
        IProgress<double?>? progress,
        CancellationToken ct)
    {
        // Headers first: a file is hundreds of megabytes and must not be buffered in memory before
        // a single byte reaches the disk.
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, Url(path)),
            authenticated: true,
            ct,
            completion: HttpCompletionOption.ResponseHeadersRead);

        var total = response.Content.Headers.ContentLength;

        await using var source = await response.Content.ReadAsStreamAsync(ct);

        var buffer = new byte[128 * 1024];
        long copied = 0;

        while (true)
        {
            var read = await source.ReadAsync(buffer, ct);
            if (read == 0) break;

            await destination.WriteAsync(buffer.AsMemory(0, read), ct);

            copied += read;
            progress?.Report(total is > 0 ? copied / (double)total.Value : null);
        }
    }
}
