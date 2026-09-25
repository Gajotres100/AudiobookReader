using System.Net;
using System.Net.Http.Headers;
using AudioBookReader.Core.Data;
using AudioBookReader.Core.Servers;

namespace AudioBookReader.Core.Tests;

public class HttpRangeStreamTests
{
    /// <summary>Serves a byte array the way a server serves a file: honouring ranges, counting requests.</summary>
    private sealed class FileServer(byte[] file, bool honoursRanges = true)
    {
        public int Requests { get; private set; }

        public Task<HttpResponseMessage> OpenAsync(long from, CancellationToken ct)
        {
            Requests++;

            var start = honoursRanges ? (int)from : 0;
            var response = new HttpResponseMessage(honoursRanges && from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(file, start, file.Length - start),
            };

            if (honoursRanges)
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, file.Length - 1, file.Length);

            return Task.FromResult(response);
        }
    }

    private static byte[] File(int length)
    {
        var bytes = new byte[length];
        new Random(3).NextBytes(bytes);
        return bytes;
    }

    [Fact]
    public async Task AStreamedBookHashesTheSameAsTheDownloadedFile()
    {
        // The hash is what ties an alignment to its audio. A book streamed on one device and
        // downloaded on another must agree on it, or an alignment made on one is useless on the other.
        var file = File(5 * 1024 * 1024 + 123);
        var server = new FileServer(file);

        await using var streamed = await HttpRangeStream.OpenAsync(server.OpenAsync);

        var overTheNetwork = await ContentHash.ComputeAsync(streamed);
        var fromDisk = await ContentHash.ComputeAsync(new MemoryStream(file));

        Assert.Equal(fromDisk, overTheNetwork);

        // Three samples, not the whole file: the opening response, then one per jump.
        Assert.InRange(server.Requests, 1, 3);
    }

    [Fact]
    public async Task ReadingStraightThroughIsOneRequest()
    {
        var file = File(3 * 1024 * 1024);
        var server = new FileServer(file);

        await using var streamed = await HttpRangeStream.OpenAsync(server.OpenAsync);
        var copy = new MemoryStream();
        await streamed.CopyToAsync(copy);

        Assert.Equal(file, copy.ToArray());
        Assert.Equal(1, server.Requests);
    }

    [Fact]
    public async Task StillReadsTheRightBytesFromAServerThatIgnoresRanges()
    {
        var file = File(200_000);
        var server = new FileServer(file, honoursRanges: false);

        // Length from Content-Length, since there is no Content-Range to read it from.
        await using var streamed = await HttpRangeStream.OpenAsync(server.OpenAsync);

        streamed.Seek(150_000, SeekOrigin.Begin);
        var buffer = new byte[100];
        var read = await streamed.ReadAtLeastAsync(buffer, buffer.Length);

        Assert.Equal(100, read);
        Assert.Equal(file.AsSpan(150_000, 100).ToArray(), buffer);
    }
}
