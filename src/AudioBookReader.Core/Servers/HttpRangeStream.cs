using System.Net;

namespace AudioBookReader.Core.Servers;

/// <summary>
/// A file on a server, read as a seekable stream.
///
/// Reading straight through keeps one response open, so copying a whole book in is one request
/// rather than hundreds. A seek elsewhere drops it and opens a new one at the new place with an HTTP
/// range, which is what lets the same hash that a downloaded copy gets — a few samples at the head,
/// middle and tail — be taken from a book that is only ever streamed, for the cost of three
/// megabytes rather than the whole file.
/// </summary>
public sealed class HttpRangeStream : Stream
{
    private readonly Func<long, CancellationToken, Task<HttpResponseMessage>> _open;
    private HttpResponseMessage? _response;
    private Stream? _body;
    private long _position;

    private HttpRangeStream(Func<long, CancellationToken, Task<HttpResponseMessage>> open, long length)
    {
        _open = open;
        Length = length;
    }

    /// <param name="open">
    /// Asks the server for the file from a byte offset onwards. Expected to send a range request, and
    /// to have dealt with signing in, so that this only has to worry about bytes.
    /// </param>
    public static async Task<HttpRangeStream> OpenAsync(
        Func<long, CancellationToken, Task<HttpResponseMessage>> open,
        CancellationToken ct = default)
    {
        var response = await open(0, ct);

        var length = response.Content.Headers.ContentRange?.Length
                     ?? response.Content.Headers.ContentLength
                     ?? throw new NotSupportedException("The server did not say how long the file is.");

        var stream = new HttpRangeStream(open, length);
        stream._response = response;
        stream._body = await response.Content.ReadAsStreamAsync(ct);

        return stream;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length { get; }

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

        target = Math.Clamp(target, 0, Length);

        // Only a real move costs a new request; the one open is still good for reading on.
        if (target != _position) Drop();

        _position = target;
        return _position;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_position >= Length || buffer.Length == 0) return 0;

        if (_body is null) await ReopenAsync(ct);

        var read = await _body!.ReadAsync(buffer, ct);
        _position += read;

        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    // Blocking, for readers that only know the synchronous shape. Never called on a UI thread here:
    // hashing and copying both run on a worker.
    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    private async Task ReopenAsync(CancellationToken ct)
    {
        Drop();

        _response = await _open(_position, ct);
        _body = await _response.Content.ReadAsStreamAsync(ct);

        // A server that ignores ranges answers with the whole file from the start. Correct, if
        // slow: skip forward to where this was asked to be.
        if (_response.StatusCode == HttpStatusCode.OK && _position > 0)
        {
            var skip = new byte[81920];
            var remaining = _position;

            while (remaining > 0)
            {
                var read = await _body.ReadAsync(skip.AsMemory(0, (int)Math.Min(skip.Length, remaining)), ct);
                if (read == 0) break;
                remaining -= read;
            }
        }
    }

    private void Drop()
    {
        _body?.Dispose();
        _response?.Dispose();
        _body = null;
        _response = null;
    }

    public override void Flush() { }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) Drop();
        base.Dispose(disposing);
    }
}
