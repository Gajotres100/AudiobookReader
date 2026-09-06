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
public sealed class ServerException(string message, HttpStatusCode? status = null, Exception? inner = null)
    : Exception(message, inner)
{
    public HttpStatusCode? Status { get; } = status;

    /// <summary>True when the token is gone or wrong, which is the one failure the app can fix.</summary>
    public bool NeedsSignIn => Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
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

    /// <summary>The server this client is pointed at, normalised, or null before it is set.</summary>
    public string? BaseUrl => _baseUrl;

    public bool IsSignedIn => _token is not null;

    /// <summary>
    /// Points the client at a server and, optionally, gives it a token it already has.
    /// </summary>
    public void Connect(string baseUrl, string? token = null)
    {
        _baseUrl = Normalise(baseUrl);
        _token = token;
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

        if (trimmed.Length == 0) throw new ArgumentException("Adresa servera je prazna.", nameof(baseUrl));

        if (!trimmed.Contains("://", StringComparison.Ordinal)) trimmed = "http://" + trimmed;

        return trimmed;
    }

    /// <summary>Signs in and keeps the token. Returns it so the caller can store it.</summary>
    public async Task<string> SignInAsync(string username, string password, CancellationToken ct = default)
    {
        var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, Url("/login"))
            {
                Content = JsonContent.Create(
                    new Credentials(username, password), ServerJsonContext.Default.Credentials),
            },
            authenticated: false,
            ct);

        var login = await ReadAsync(response, ServerJsonContext.Default.LoginResponse, ct);

        _token = login?.User?.Token
                 ?? throw new ServerException("Prijava je prošla, ali server nije vratio token.");

        return _token;
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
            throw new ServerException("Server je vratio stavku koju ne razumijem.");

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
            : new ServerProgress(body.CurrentTime ?? 0, body.Duration ?? 0, body.IsFinished ?? false);
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
        var update = new ProgressUpdate(currentTimeSeconds, durationSeconds, fraction, fraction >= 0.99);

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

        return new ServerBook(
            item.Id,
            media?.Metadata?.Title ?? "(bez naslova)",
            media?.Metadata?.AuthorName,
            media?.NumAudioFiles ?? media?.AudioFiles?.Count ?? 0,
            media?.EbookFormat ?? media?.EbookFileFormat,
            media?.Duration ?? 0);
    }

    private string Url(string path) =>
        (_baseUrl ?? throw new ServerException("Server nije podešen.")) + path;

    private Task<HttpResponseMessage> GetAsync(string path, CancellationToken ct) =>
        SendAsync(() => new HttpRequestMessage(HttpMethod.Get, Url(path)), authenticated: true, ct);

    private async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> build,
        bool authenticated,
        CancellationToken ct,
        HttpStatusCode? tolerate = null,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        var request = build();

        if (authenticated)
        {
            if (_token is null) throw new ServerException("Nisi prijavljen na server.", HttpStatusCode.Unauthorized);

            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
        }

        HttpResponseMessage response;

        try
        {
            response = await http.SendAsync(request, completion, ct);
        }
        catch (HttpRequestException ex)
        {
            // The distinction the user needs: nothing answered, as opposed to something answering no.
            throw new ServerException($"Ne mogu doći do servera ({_baseUrl}).", null, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ServerException($"Server ({_baseUrl}) ne odgovara.", null, ex);
        }

        if (response.IsSuccessStatusCode || response.StatusCode == tolerate) return response;

        response.Dispose();

        throw new ServerException(
            response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "Prijava na server više ne vrijedi.",
                HttpStatusCode.Forbidden => "Ovaj račun nema pristup tome na serveru.",
                HttpStatusCode.NotFound => "Server ne zna za to.",
                _ => $"Server je odgovorio {(int)response.StatusCode}.",
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
                    $"Odgovor s {_baseUrl} nije ono što Audiobookshelf vraća. Je li adresa točna?",
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
