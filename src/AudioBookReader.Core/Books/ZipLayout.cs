using System.Buffers.Binary;
using System.Text;

namespace AudioBookReader.Core.Books;

/// <summary>One file in a zip, as it lies in the archive.</summary>
/// <param name="Method">0 when stored as it is; anything else is compressed.</param>
/// <param name="LocalHeaderOffset">Where its local header starts; its bytes follow that header.</param>
public sealed record ZipEntryLayout(string Name, int Method, long CompressedSize, long Size, long LocalHeaderOffset)
{
    public bool IsStored => Method == 0 && CompressedSize == Size;
}

/// <summary>
/// Where each file of a zip lies, read from the archive's directory alone.
///
/// For an EPUB 3 that is still on a server. A package made for reading along carries hours of
/// narration stored uncompressed, so the recording is one unbroken run of bytes inside it — and
/// knowing where that run begins is all a player needs to play it straight from the server, a range
/// at a time, without the package ever being downloaded. The directory sits at the end of the file
/// and costs a request or two to read.
///
/// <see cref="System.IO.Compression.ZipArchive"/> reads the same directory but keeps the offsets to
/// itself, which is the one thing needed here.
/// </summary>
public static class ZipLayout
{
    private const uint EndOfDirectory = 0x06054b50;
    private const uint Zip64Locator = 0x07064b50;
    private const uint Zip64EndOfDirectory = 0x06064b50;
    private const uint DirectoryEntry = 0x02014b50;
    private const uint LocalHeader = 0x04034b50;

    /// <summary>Every file in the archive, in the order the directory lists them.</summary>
    public static async Task<List<ZipEntryLayout>> ReadAsync(Stream zip, CancellationToken ct = default)
    {
        // The end-of-directory record is the last thing in the file, after a comment of up to 64 KB.
        var tailLength = (int)Math.Min(zip.Length, 22 + ushort.MaxValue);
        var tail = await ReadAtAsync(zip, zip.Length - tailLength, tailLength, ct);

        var end = -1;
        for (var i = tail.Length - 22; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) == EndOfDirectory)
            {
                end = i;
                break;
            }
        }

        if (end < 0) throw new InvalidDataException("Not a zip archive: no directory at its end.");

        long count = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(end + 10));
        long size = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end + 12));
        long offset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end + 16));

        // An archive past 4 GB, or with that many files, keeps the real figures in a Zip64 record
        // that a locator just before this one points at.
        if (offset == uint.MaxValue || size == uint.MaxValue || count == ushort.MaxValue)
        {
            var locator = end - 20;
            if (locator < 0 || BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(locator)) != Zip64Locator)
                throw new InvalidDataException("A Zip64 archive without its locator.");

            var recordAt = (long)BinaryPrimitives.ReadUInt64LittleEndian(tail.AsSpan(locator + 8));
            var record = await ReadAtAsync(zip, recordAt, 56, ct);

            if (BinaryPrimitives.ReadUInt32LittleEndian(record) != Zip64EndOfDirectory)
                throw new InvalidDataException("A Zip64 locator pointing at nothing.");

            count = (long)BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(32));
            size = (long)BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(40));
            offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(48));
        }

        var directory = await ReadAtAsync(zip, offset, checked((int)size), ct);
        var entries = new List<ZipEntryLayout>((int)Math.Min(count, 100_000));
        var at = 0;

        while (at + 46 <= directory.Length && BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(at)) == DirectoryEntry)
        {
            var span = directory.AsSpan(at);

            int method = BinaryPrimitives.ReadUInt16LittleEndian(span[10..]);
            long compressed = BinaryPrimitives.ReadUInt32LittleEndian(span[20..]);
            long uncompressed = BinaryPrimitives.ReadUInt32LittleEndian(span[24..]);
            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(span[28..]);
            int extraLength = BinaryPrimitives.ReadUInt16LittleEndian(span[30..]);
            int commentLength = BinaryPrimitives.ReadUInt16LittleEndian(span[32..]);
            long local = BinaryPrimitives.ReadUInt32LittleEndian(span[42..]);

            var name = Encoding.UTF8.GetString(span.Slice(46, nameLength));

            // Fields too big for four bytes are in the Zip64 extra field, in this order, and only
            // those that overflowed.
            var extra = span.Slice(46 + nameLength, extraLength);
            for (var e = 0; e + 4 <= extra.Length;)
            {
                int id = BinaryPrimitives.ReadUInt16LittleEndian(extra[e..]);
                int length = BinaryPrimitives.ReadUInt16LittleEndian(extra[(e + 2)..]);
                var field = extra.Slice(e + 4, Math.Min(length, extra.Length - e - 4));

                if (id == 0x0001)
                {
                    var f = 0;
                    if (uncompressed == uint.MaxValue && f + 8 <= field.Length) { uncompressed = (long)BinaryPrimitives.ReadUInt64LittleEndian(field[f..]); f += 8; }
                    if (compressed == uint.MaxValue && f + 8 <= field.Length) { compressed = (long)BinaryPrimitives.ReadUInt64LittleEndian(field[f..]); f += 8; }
                    if (local == uint.MaxValue && f + 8 <= field.Length) local = (long)BinaryPrimitives.ReadUInt64LittleEndian(field[f..]);
                }

                e += 4 + length;
            }

            entries.Add(new ZipEntryLayout(name, method, compressed, uncompressed, local));
            at += 46 + nameLength + extraLength + commentLength;
        }

        return entries;
    }

    /// <summary>
    /// Where a file's own bytes begin. Read from its local header rather than worked out from the
    /// directory, because the two may carry different extra fields — and a guess off by a few bytes
    /// would play as noise.
    /// </summary>
    public static async Task<long> DataOffsetAsync(Stream zip, ZipEntryLayout entry, CancellationToken ct = default)
    {
        var header = await ReadAtAsync(zip, entry.LocalHeaderOffset, 30, ct);

        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != LocalHeader)
            throw new InvalidDataException($"No local header where the directory puts '{entry.Name}'.");

        int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(26));
        int extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28));

        return entry.LocalHeaderOffset + 30 + nameLength + extraLength;
    }

    private static async Task<byte[]> ReadAtAsync(Stream stream, long offset, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        stream.Seek(offset, SeekOrigin.Begin);
        await stream.ReadExactlyAsync(buffer, ct);
        return buffer;
    }
}

/// <summary>
/// A stretch of another stream, read as if it were a whole file: position 0 is where the stretch
/// starts and the length is the stretch's. How a recording stored inside a package is played,
/// probed and hashed exactly as if it were a file of its own.
/// </summary>
public sealed class SubStream(Stream inner, long start, long length, bool ownsInner = true) : Stream
{
    private long _position;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _position = Math.Clamp(origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => length + offset,
        }, 0, length);

        return _position;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var left = (int)Math.Min(buffer.Length, length - _position);
        if (left <= 0) return 0;

        if (inner.Position != start + _position) inner.Seek(start + _position, SeekOrigin.Begin);

        var read = inner.Read(buffer[..left]);
        _position += read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var left = (int)Math.Min(buffer.Length, length - _position);
        if (left <= 0) return 0;

        if (inner.Position != start + _position) inner.Seek(start + _position, SeekOrigin.Begin);

        var read = await inner.ReadAsync(buffer[..left], ct);
        _position += read;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override void Flush() { }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && ownsInner) inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (ownsInner) await inner.DisposeAsync();
        await base.DisposeAsync();
    }
}

/// <summary>
/// A file read with one stretch of it missing — the bytes before and after, fetched in a piece each,
/// standing in for the whole file.
///
/// For taking the text out of an EPUB 3 that is still on a server. Its narration is one unbroken
/// stretch in the middle of the package, and everything else is a hundred small files on either
/// side of it; read one by one, each cost a request of its own, and the text of one book took a
/// hundred requests and as many megabytes. Fetched as the two runs either side of the recording it
/// is two requests, and the zip reader finds every file where it expects it. The stretch left out
/// reads as zeros: reading the text never asks for it.
/// </summary>
public sealed class GappedStream(byte[] head, long gapLength, byte[] tail) : Stream
{
    private long _position;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => head.Length + gapLength + tail.Length;

    public override long Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, Length);
    }

    public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
    {
        SeekOrigin.Begin => offset,
        SeekOrigin.Current => _position + offset,
        _ => Length + offset,
    };

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_position >= Length || buffer.Length == 0) return 0;

        if (_position < head.Length)
        {
            var count = (int)Math.Min(buffer.Length, head.Length - _position);
            head.AsSpan((int)_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        var tailStart = head.Length + gapLength;
        if (_position < tailStart)
        {
            // Zeros. The zip reader looks for its directory by reading backwards from the end in
            // blocks, and a block can reach into the recording; zeros are never mistaken for the
            // signature it is after, and nothing that reads the text ever reads in here.
            var zeros = (int)Math.Min(buffer.Length, tailStart - _position);
            buffer[..zeros].Clear();
            _position += zeros;
            return zeros;
        }

        var at = (int)(_position - tailStart);
        var fromTail = Math.Min(buffer.Length, tail.Length - at);
        tail.AsSpan(at, fromTail).CopyTo(buffer);
        _position += fromTail;
        return fromTail;
    }

    public override void Flush() { }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
