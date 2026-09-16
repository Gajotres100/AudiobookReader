using System.Net;
using System.Text;
using AudioBookReader.Core.Servers;

namespace AudioBookReader.Core.Tests;

/// <summary>
/// Exercised against recorded answers rather than a live server, so the tests say what the client
/// does with a given reply — including the replies that are not the happy one, which is where a
/// network client earns its keep.
/// </summary>
public class AudiobookshelfClientTests
{
    private sealed class Recorded(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(answer(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (AudiobookshelfClient Client, Recorded Handler) Connected(
        Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        var handler = new Recorded(answer);
        var client = new AudiobookshelfClient(new HttpClient(handler));

        client.Connect("books.local:13378", "a-token");
        return (client, handler);
    }

    // ---- The address people actually type ----

    [Theory]
    [InlineData("books.local:13378", "http://books.local:13378")]
    [InlineData("http://books.local:13378/", "http://books.local:13378")]
    [InlineData("  https://books.example.com/abs/  ", "https://books.example.com/abs")]
    public void AcceptsTheAddressHoweverItIsWritten(string typed, string expected)
    {
        Assert.Equal(expected, AudiobookshelfClient.Normalise(typed));
    }

    [Fact]
    public void AssumesPlainHttpForABareHost()
    {
        // These servers live on home networks without certificates. Defaulting to https there fails
        // in a way that reads as "the app is broken" rather than "add the scheme".
        Assert.StartsWith("http://", AudiobookshelfClient.Normalise("192.168.1.10:13378"));
    }

    // ---- Signing in ----

    [Fact]
    public async Task TakesTheTokenFromWhereTheServerPutsIt()
    {
        var (client, handler) = Connected(_ => Json("""{"user":{"id":"u1","token":"tok-123"}}"""));

        var tokens = await client.SignInAsync("nikola", "hunter2");

        Assert.Equal("tok-123", tokens.Access);
        Assert.Equal("http://books.local:13378/login", handler.Requests[0].RequestUri?.ToString());
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
    }

    [Fact]
    public async Task SaysSoWhenTheAddressPointsAtSomethingThatIsNotTheServer()
    {
        // A router's login page, a proxy error, a plain web server. All answer 200 with HTML.
        var (client, _) = Connected(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><body>It works!</body></html>", Encoding.UTF8, "text/html"),
        });

        var failure = await Assert.ThrowsAsync<ServerException>(() => client.SignInAsync("a", "b"));

        // Compared against the resource rather than a phrase typed here, so the test keeps its
        // meaning in whatever language the run happens to be in.
        Assert.Equal(
            string.Format(CoreStrings.Server_NotAudiobookshelf, "http://books.local:13378"),
            failure.Message);
    }

    [Fact]
    public async Task DistinguishesAWrongPasswordFromAnUnreachableServer()
    {
        var (refused, _) = Connected(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var said = await Assert.ThrowsAsync<ServerException>(() => refused.SignInAsync("a", "b"));
        Assert.True(said.NeedsSignIn);

        var (unreachable, _) = Connected(_ => throw new HttpRequestException("no route to host"));

        var silent = await Assert.ThrowsAsync<ServerException>(() => unreachable.SignInAsync("a", "b"));
        Assert.False(silent.NeedsSignIn);
        Assert.Equal(
            string.Format(CoreStrings.Server_Unreachable, "http://books.local:13378"),
            silent.Message);
    }

    [Fact]
    public async Task ReadsTheAuthorFromTheShortFormTheShelfReturns()
    {
        var (client, _) = Connected(_ => Json("""
            {"results":[{"id":"a","media":{"metadata":{"title":"T","authorName":"Ed McDonald"}}}]}
            """));

        var books = await client.GetBooksAsync("lib");

        Assert.Equal("Ed McDonald", Assert.Single(books).Author);
    }

    [Fact]
    public async Task ReadsTheAuthorFromTheLongFormOneBookReturns()
    {
        // Asked for in full, the server names the authors one by one and leaves authorName out —
        // which is why a book's own page showed no author and a download landed under
        // "Unknown author" while the shelf beside it had the name all along.
        var (client, _) = Connected(_ => Json("""
            {"id":"a","media":{"metadata":{"title":"T","authors":[{"id":"1","name":"John Gwynne"}]}}}
            """));

        var detail = await client.GetBookAsync("a");

        Assert.Equal("John Gwynne", detail.Book.Author);
    }

    [Fact]
    public async Task JoinsSeveralAuthors()
    {
        var (client, _) = Connected(_ => Json("""
            {"id":"a","media":{"metadata":{"title":"T","authors":[{"name":"One"},{"name":"Two"}]}}}
            """));

        var detail = await client.GetBookAsync("a");

        Assert.Equal("One, Two", detail.Book.Author);
    }

    [Fact]
    public async Task SendsTheTokenOnEveryRequestThatNeedsIt()
    {
        var (client, handler) = Connected(_ => Json("""{"libraries":[]}"""));

        await client.GetLibrariesAsync();

        Assert.Equal("Bearer", handler.Requests[0].Headers.Authorization?.Scheme);
        Assert.Equal("a-token", handler.Requests[0].Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task RefusesToAskWithoutATokenRatherThanBeingTurnedAway()
    {
        var client = new AudiobookshelfClient(new HttpClient(new Recorded(_ => Json("{}"))));
        client.Connect("books.local");

        var failure = await Assert.ThrowsAsync<ServerException>(() => client.GetLibrariesAsync());
        Assert.True(failure.NeedsSignIn);
    }

    // ---- Libraries and books ----

    [Fact]
    public async Task ReadsTheLibrariesAndKnowsWhichHoldBooks()
    {
        var (client, _) = Connected(_ => Json("""
            {"libraries":[
              {"id":"lib_1","name":"Audiobooks","mediaType":"book"},
              {"id":"lib_2","name":"Podcasts","mediaType":"podcast"}
            ]}
            """));

        var libraries = await client.GetLibrariesAsync();

        Assert.Equal(2, libraries.Count);
        Assert.True(libraries[0].IsBooks);
        Assert.False(libraries[1].IsBooks);
    }

    [Fact]
    public async Task PagesThroughALibraryUntilTheServerRunsOut()
    {
        var page = 0;

        var (client, handler) = Connected(request =>
        {
            // First page full, second page short: the second is how the client knows to stop.
            var results = request.RequestUri!.Query.Contains("page=0")
                ? string.Join(",", Enumerable.Range(0, 100).Select(i => Item($"i{i}", $"Book {i}")))
                : Item("last", "The Last One");

            page++;
            return Json($$"""{"results":[{{results}}],"total":101}""");
        });

        var books = await client.GetBooksAsync("lib_1");

        Assert.Equal(101, books.Count);
        Assert.Equal(2, page);
        Assert.Contains("sort=media.metadata.title", handler.Requests[0].RequestUri?.Query);
    }

    /// <summary>One item as the server sends it, with only the fields the client reads.</summary>
    private static string Item(string id, string title, int audioFiles = 1, string? ebook = null)
    {
        var format = ebook is null ? "null" : "\"" + ebook + "\"";

        return "{\"id\":\"" + id + "\",\"media\":{"
               + "\"metadata\":{\"title\":\"" + title + "\",\"authorName\":\"Ed McDonald\"},"
               + "\"numAudioFiles\":" + audioFiles + ","
               + "\"ebookFormat\":" + format + ","
               + "\"duration\":41280.9}}";
    }

    [Fact]
    public async Task DescribesWhatEachTitleActuallyHas()
    {
        var (client, _) = Connected(_ => Json($$"""
            {"results":[
              {{Item("a", "Only Audio")}},
              {{Item("b", "Both", audioFiles: 40, ebook: "epub")}},
              {{Item("c", "Only Text", audioFiles: 0)}}
            ],"total":3}
            """));

        var books = await client.GetBooksAsync("lib_1");

        Assert.True(books[0].HasAudio);
        Assert.False(books[0].HasEbook);

        Assert.True(books[1].HasAudio);
        Assert.True(books[1].HasEbook);
        Assert.Equal(40, books[1].AudioFileCount);

        Assert.False(books[2].HasAudio);
    }

    [Fact]
    public async Task ReadsTheFilesAndChaptersOfOneBook()
    {
        var (client, _) = Connected(_ => Json("""
            {"id":"item_1","media":{
              "metadata":{"title":"Blackwing","authorName":"Ed McDonald"},
              "numAudioFiles":2,"ebookFileFormat":"epub","duration":3600,
              "audioFiles":[
                {"ino":"101","duration":1800,"metadata":{"filename":"01.mp3","ext":".mp3"}},
                {"ino":"102","duration":1800,"metadata":{"filename":"02.mp3","ext":".mp3"}}
              ],
              "ebookFile":{"ino":"200","metadata":{"filename":"Blackwing.epub","ext":".epub"}},
              "chapters":[
                {"id":0,"start":0,"end":1800,"title":"One"},
                {"id":1,"start":1800,"end":3600,"title":"Two"}
              ]}}
            """));

        var book = await client.GetBookAsync("item_1");

        Assert.Equal("Blackwing", book.Book.Title);
        Assert.Equal(2, book.AudioFiles.Count);
        Assert.Equal("01.mp3", book.AudioFiles[0].FileName);
        Assert.Equal(1800, book.AudioFiles[1].DurationSeconds);
        Assert.Equal("200", book.Ebook?.Ino);
        Assert.Equal("Two", book.Chapters[1].Title);
    }

    // ---- Downloading ----

    [Fact]
    public async Task CopiesAFileWithoutHoldingItInMemory()
    {
        var payload = new byte[300_000];
        Random.Shared.NextBytes(payload);

        var (client, handler) = Connected(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload),
        });

        var seen = new List<double?>();
        using var destination = new MemoryStream();

        await client.DownloadFileAsync("item_1", "101", destination, new Progress<double?>(seen.Add));

        Assert.Equal(payload, destination.ToArray());
        Assert.Contains("/api/items/item_1/file/101/download", handler.Requests[0].RequestUri?.ToString());
    }

    // ---- Progress ----

    [Fact]
    public async Task TreatsABookNobodyHasOpenedAsAnAnswerRatherThanAFailure()
    {
        var (client, _) = Connected(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        Assert.Null(await client.GetProgressAsync("item_1"));
    }

    [Fact]
    public async Task ReadsWhereListeningHadReached()
    {
        var (client, _) = Connected(_ => Json(
            """{"libraryItemId":"item_1","currentTime":1234.5,"duration":41280.9,"isFinished":false}"""));

        var progress = await client.GetProgressAsync("item_1");

        Assert.Equal(1234.5, progress?.CurrentTimeSeconds);
        Assert.False(progress?.IsFinished);
    }

    [Fact]
    public async Task CountsABookAsFinishedBeforeTheCredits()
    {
        // The last seconds of an audiobook are credits. A book left one percent short never leaves
        // the shelf, on the server or anywhere else.
        string? body = null;

        var (client, handler) = Connected(_ => new HttpResponseMessage(HttpStatusCode.OK));

        await client.SetProgressAsync("item_1", currentTimeSeconds: 3_570, durationSeconds: 3_600);

        body = await handler.Requests[0].Content!.ReadAsStringAsync();

        Assert.Equal(HttpMethod.Patch, handler.Requests[0].Method);
        Assert.Contains("\"isFinished\":true", body);
    }

    // ---- Staying signed in ----

    [Fact]
    public async Task AsksForTheRefreshTokenRatherThanLettingTheServerKeepIt()
    {
        // Without the header the server puts the refresh token in a cookie and hands back only an
        // access token good for an hour. This is the difference between signing in once and
        // signing in hourly.
        var (client, handler) = Connected(_ => Json(
            """{"user":{"accessToken":"acc-1","refreshToken":"ref-1","token":"legacy"}}"""));

        var tokens = await client.SignInAsync("nikola", "hunter2");

        Assert.True(handler.Requests[0].Headers.Contains("x-return-tokens"));
        Assert.Equal("acc-1", tokens.Access);
        Assert.Equal("ref-1", tokens.Refresh);
    }

    [Fact]
    public async Task StillWorksWithAnOlderServerThatIssuesOneLongLivedToken()
    {
        var (client, _) = Connected(_ => Json("""{"user":{"token":"legacy-only"}}"""));

        var tokens = await client.SignInAsync("nikola", "hunter2");

        Assert.Equal("legacy-only", tokens.Access);
        Assert.Null(tokens.Refresh);
    }

    [Fact]
    public async Task TradesAnExpiredTokenForAFreshOneAndRepeatsTheCall()
    {
        var asked = new List<string>();

        var handler = new Recorded(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            asked.Add(path);

            if (path == "/auth/refresh") return Json("""{"user":{"accessToken":"acc-2","refreshToken":"ref-2"}}""");

            // The first library request carries the stale token; the second carries the new one.
            return request.Headers.Authorization?.Parameter == "acc-2"
                ? Json("""{"libraries":[{"id":"lib_1","name":"Books","mediaType":"book"}]}""")
                : new HttpResponseMessage(HttpStatusCode.Unauthorized);
        });

        var client = new AudiobookshelfClient(new HttpClient(handler));
        client.Connect("books.local", "acc-1", "ref-1");

        ServerTokens? kept = null;
        client.TokensChanged += (_, tokens) => kept = tokens;

        var libraries = await client.GetLibrariesAsync();

        Assert.Single(libraries);
        Assert.Equal(["/api/libraries", "/auth/refresh", "/api/libraries"], asked);
        Assert.Equal("ref-2", kept?.Refresh);
    }

    [Fact]
    public async Task GivesUpOnceWhenTheRefreshTokenIsSpentRatherThanLoopingOnIt()
    {
        var attempts = 0;

        var handler = new Recorded(request =>
        {
            attempts++;
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        });

        var client = new AudiobookshelfClient(new HttpClient(handler));
        client.Connect("books.local", "acc-1", "ref-1");

        var failure = await Assert.ThrowsAsync<ServerException>(() => client.GetLibrariesAsync());

        Assert.True(failure.NeedsSignIn);

        // The call, then the refusal to refresh. Not a third.
        Assert.Equal(2, attempts);
    }

    /// <summary>
    /// The full reply for a single book can describe its ebook only by the file, with no format
    /// field anywhere — and the page then called a book with an ebook plainly attached
    /// "audiobook only", and offered to download a half it already had a way to get.
    /// </summary>
    [Fact]
    public async Task SeesAnEbookDescribedOnlyByItsFile()
    {
        var (client, _) = Connected(_ => Json("""
            {"id":"li_1","media":{
              "metadata":{"title":"Sufficiently Advanced Magic","authorName":"Andrew Rowe"},
              "audioFiles":[{"ino":"1","duration":3600,"metadata":{"filename":"part1.m4b"}}],
              "ebookFile":{"ino":"200","metadata":{"filename":"magic.epub","ext":".epub"}},
              "duration":3600,"chapters":[]}}
            """));

        var detail = await client.GetBookAsync("li_1");

        Assert.True(detail.Book.HasEbook);
        Assert.Equal("epub", detail.Book.EbookFormat);
        Assert.NotNull(detail.Ebook);
    }
}
