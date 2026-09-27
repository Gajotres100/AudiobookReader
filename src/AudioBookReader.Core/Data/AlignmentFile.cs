using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Web;

namespace AudioBookReader.Core.Data;

/// <summary>
/// An alignment as a file, for sending any way at all: AirDrop, Quick Share, a chat, e-mail, a
/// shared drive. The same package that travels over the local network, compressed — a whole book's
/// map is a couple of hundred kilobytes of JSON and a tenth of that zipped.
///
/// Nothing about the file has to be trusted: it is accepted only by a book made of exactly the files
/// its two hashes name, which is the test every map already passes.
/// </summary>
public static class AlignmentFile
{
    public const string Extension = ".syncbook";

    public static async Task WriteAsync(Stream destination, AlignmentPackage package, CancellationToken ct = default)
    {
        await using var zip = new GZipStream(destination, CompressionLevel.SmallestSize, leaveOpen: true);
        await JsonSerializer.SerializeAsync(zip, package, AlignmentTransferJsonContext.Default.AlignmentPackage, ct);
    }

    /// <summary>The package in the file, or null when it is not one.</summary>
    public static async Task<AlignmentPackage?> ReadAsync(Stream source, CancellationToken ct = default)
    {
        try
        {
            await using var zip = new GZipStream(source, CompressionMode.Decompress, leaveOpen: true);
            var package = await JsonSerializer.DeserializeAsync(zip, AlignmentTransferJsonContext.Default.AlignmentPackage, ct);

            return package is { Map: not null } ? package : null;
        }
        catch (Exception e) when (e is InvalidDataException or JsonException)
        {
            return null;
        }
    }

    /// <summary>A file name for the book, safe on every system the file might pass through.</summary>
    public static string FileNameFor(string title)
    {
        var safe = new string([.. title.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' or ':' ? '_' : c)]).Trim();
        return (safe.Length > 0 ? safe : "alignment") + Extension;
    }
}

/// <summary>
/// What a device waiting for an alignment puts in its QR code: where to reach it, and a one-time
/// code that lets the sender in without the receiver having to answer a question.
///
/// Every private address the device has, not one: a phone on Wi-Fi and a mobile hotspot at once, or
/// a tablet with two networks, is reachable on whichever the sender shares with it, and the sender
/// simply tries them in turn.
/// </summary>
/// <param name="Name">The receiving device's name, so the sender can say where it sent it.</param>
public record PairingCode(IReadOnlyList<IPAddress> Addresses, int Port, string Key, string Name)
{
    private const string Prefix = "syncbook://pair?";

    public override string ToString() =>
        Prefix
        + $"a={string.Join(',', Addresses)}"
        + $"&p={Port}"
        + $"&k={Uri.EscapeDataString(Key)}"
        + $"&n={Uri.EscapeDataString(Name)}";

    /// <summary>The code read from a QR code, or null when the QR code is something else.</summary>
    public static PairingCode? Parse(string? text)
    {
        if (text is null || !text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return null;

        var query = HttpUtility.ParseQueryString(text[Prefix.Length..]);

        var addresses = (query["a"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(a => IPAddress.TryParse(a, out var address) ? address : null)
            .OfType<IPAddress>()
            .ToList();

        if (addresses.Count == 0) return null;
        if (!int.TryParse(query["p"], out var port) || port is <= 0 or > 65535) return null;
        if (query["k"] is not { Length: > 0 } key) return null;

        return new PairingCode(addresses, port, key, query["n"] ?? "");
    }

    /// <summary>A fresh code, short enough for a small QR code and long enough not to be guessed.</summary>
    public static string NewKey() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8));
}
