using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using AudioBookReader.Core.Models;

namespace AudioBookReader.Core.Data;

/// <summary>Where one chapter lies in the text, as alignment measured it.</summary>
public record ChapterRange(int Index, int? TextStart, int? TextEnd);

/// <summary>
/// A book's alignment, packed to travel to another device.
///
/// The map alone is not quite enough: alignment also fills in where each chapter begins in the text,
/// which is what lets the reader open the right page for a chapter, and that lives on the chapters
/// rather than in the map. Both go together. The two hashes say which pair of files this describes,
/// so a device only ever accepts it for a book made of exactly the same audio and text — the same
/// test every map already has to pass.
/// </summary>
/// <param name="From">The sending device's name, for the receiver to show.</param>
/// <param name="Key">
/// The one-time code the receiver showed in its QR code, when it was sent that way. Proof that the
/// person sending is standing in front of the receiver, so it is taken without asking.
/// </param>
public record AlignmentPackage(
    string From,
    string Title,
    string? AudioHash,
    string? EbookHash,
    List<ChapterRange> Chapters,
    SyncMap Map,
    string? Key = null);

/// <summary>What the receiving device made of a package.</summary>
/// <param name="Reason">Why not, when not: <see cref="AlignmentTransfer.NoSuchBook"/> or <see cref="AlignmentTransfer.Declined"/>.</param>
public record AlignmentReply(bool Accepted, string? Reason = null);

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(AlignmentPackage))]
[JsonSerializable(typeof(AlignmentReply))]
internal partial class AlignmentTransferJsonContext : JsonSerializerContext;

/// <summary>
/// The wire format for sending an alignment between devices: a length, then that many bytes of JSON,
/// each way — the package there and the reply back.
///
/// Length-prefixed rather than read to the end so one connection carries the question and its
/// answer, and capped, so a device listening on the local network cannot be made to swallow
/// gigabytes by anything that happens to connect to it.
/// </summary>
public static class AlignmentTransfer
{
    /// <summary>The receiver has no book made of these same two files.</summary>
    public const string NoSuchBook = "no-such-book";

    /// <summary>Someone at the receiver said no.</summary>
    public const string Declined = "declined";

    /// <summary>A densely measured novel is a few hundred kilobytes; this is generous.</summary>
    public const int MaxBytes = 32 * 1024 * 1024;

    public static Task WritePackageAsync(Stream stream, AlignmentPackage package, CancellationToken ct = default) =>
        WriteAsync(stream, JsonSerializer.SerializeToUtf8Bytes(package, AlignmentTransferJsonContext.Default.AlignmentPackage), ct);

    public static async Task<AlignmentPackage?> ReadPackageAsync(Stream stream, CancellationToken ct = default) =>
        await ReadAsync(stream, ct) is { } bytes
            ? JsonSerializer.Deserialize(bytes, AlignmentTransferJsonContext.Default.AlignmentPackage)
            : null;

    public static Task WriteReplyAsync(Stream stream, AlignmentReply reply, CancellationToken ct = default) =>
        WriteAsync(stream, JsonSerializer.SerializeToUtf8Bytes(reply, AlignmentTransferJsonContext.Default.AlignmentReply), ct);

    public static async Task<AlignmentReply?> ReadReplyAsync(Stream stream, CancellationToken ct = default) =>
        await ReadAsync(stream, ct) is { } bytes
            ? JsonSerializer.Deserialize(bytes, AlignmentTransferJsonContext.Default.AlignmentReply)
            : null;

    private static async Task WriteAsync(Stream stream, byte[] body, CancellationToken ct)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, body.Length);

        await stream.WriteAsync(length, ct);
        await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<byte[]?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        if (await stream.ReadAtLeastAsync(header, 4, throwOnEndOfStream: false, ct) < 4) return null;

        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length <= 0 || length > MaxBytes) throw new InvalidDataException($"A message of {length} bytes is not an alignment.");

        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct);

        return body;
    }
}
