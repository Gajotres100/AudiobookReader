using System.Net;

namespace AudioBookReader.Core.Servers;

/// <summary>
/// A file on a server, read as a seekable stream.
///
/// Asks for the file in bounded pieces with HTTP ranges: small straight after a seek — which is
/// what lets the hash a downloaded copy gets, a few samples at the head, middle and tail, be taken
/// from a book that is only ever streamed for the cost of three megabytes — and growing while it is
/// read straight through, so copying a whole book in is dozens of requests rather than thousands.
///
/// Bounded, never open-ended. An open-ended response abandoned after a seek is the rest of the file
/// still on its way, and Android's HTTP stack drains it on close — on whatever thread closed it,
/// which on the first attempt was the UI thread, and Android refuses network work there outright.
/// </summary>
public sealed class HttpRangeStream : Stream
{
    /// <summary>The first piece after a seek, and the piece a hash sample needs.</summary>
    private const long SmallestPiece = 1 << 20;

    /// <summary>The largest piece asked for while reading straight through.</summary>
    private const long LargestPiece = 16L << 20;

    private readonly Func<long, long, CancellationToken, Task<HttpResponseMessage>> _open;
    private HttpResponseMessage? _response;
    private Stream? _body;
    private long _position;

    /// <summary>Where the piece being read ends, exclusive.</summary>
    private long _pieceEnd;

    /// <summary>How big the next piece will be; doubles while reading on, and resets on a seek.</summary>
    private long _nextPiece = SmallestPiece;

    private HttpRangeStream(Func<long, long, CancellationToken, Task<HttpResponseMessage>> open, long length)
    {
        _open = open;
        Length = length;
    }

    /// <param name="open">
    /// Asks the server for the bytes from the first offset to the second, inclusive — a range
    /// request, with signing in already dealt with, so that this only has to worry about bytes.
    /// </param>
    public static async Task<HttpRangeStream> OpenAsync(
        Func<long, long, CancellationToken, Task<HttpResponseMessage>> open,
        CancellationToken ct = default)
    {
        var response = await open(0, SmallestPiece - 1, ct);

        // The whole length, from the range header; a server that ignores ranges sends the whole file
        // and says how long it is in the ordinary way.
        var length = response.Content.Headers.ContentRange?.Length
                     ?? response.Content.Headers.ContentLength
                     ?? throw new NotSupportedException("The server did not say how long the file is.");

        var stream = new HttpRangeStream(open, length);
        await stream.AdoptAsync(response, 0, ct);

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

        // Only a real move costs a new request; the piece open is still good for reading on.
        if (target != _position)
        {
            Drop();
            _nextPiece = SmallestPiece;
        }

        _position = target;
        return _position;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_position >= Length || buffer.Length == 0) return 0;

        if (_body is null || _position >= _pieceEnd) await NextPieceAsync(ct);

        var wanted = (int)Math.Min(buffer.Length, _pieceEnd - _position);
        var read = await _body!.ReadAsync(buffer[..wanted], ct);

        // A piece that ends early — a connection cut short — is fetched again from here next time.
        if (read == 0) Drop();

        _position += read;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    // Blocking, for readers that only know the synchronous shape. Callers run it on a worker.
    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    private async Task NextPieceAsync(CancellationToken ct)
    {
        Drop();

        var from = _position;
        var to = Math.Min(Length, from + _nextPiece) - 1;

        _nextPiece = Math.Min(_nextPiece * 2, LargestPiece);

        await AdoptAsync(await _open(from, to, ct), from, ct);
    }

    private async Task AdoptAsync(HttpResponseMessage response, long from, CancellationToken ct)
    {
        _response = response;
        _body = await response.Content.ReadAsStreamAsync(ct);

        if (response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentRange is { To: { } to })
        {
            _pieceEnd = to + 1;
            return;
        }

        // A server that ignores ranges answers with the whole file from the start. Correct, if slow:
        // skip forward to where this was asked to be, and treat the rest of the file as the piece.
        _pieceEnd = Length;

        var skip = new byte[81920];
        var remaining = from;

        while (remaining > 0)
        {
            var read = await _body.ReadAsync(skip.AsMemory(0, (int)Math.Min(skip.Length, remaining)), ct);
            if (read == 0) break;
            remaining -= read;
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
