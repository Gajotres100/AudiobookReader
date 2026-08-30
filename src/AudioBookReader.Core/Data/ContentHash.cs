using System.Buffers.Binary;
using System.Security.Cryptography;

namespace AudioBookReader.Core.Data;

/// <summary>Identity hashes for imported files, used to key the sync map cache.</summary>
public static class ContentHash
{
    private const int SampleSize = 1 << 20; // 1 MiB

    /// <summary>
    /// Hashes a file from its length plus three 1 MiB samples (head, middle, tail) rather than
    /// its full contents.
    ///
    /// Audiobooks run to hundreds of megabytes and this is only ever used to answer "is this the
    /// same file I already aligned?" — a question where sampling is sufficient. Hashing whole
    /// files would add seconds to every import to defend against an adversary who does not exist
    /// here. Including the length means truncated or extended files never collide.
    /// </summary>
    public static async Task<string> ComputeAsync(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        var length = stream.Length;

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        Span<byte> lengthBytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(lengthBytes, length);
        sha.AppendData(lengthBytes);

        var buffer = new byte[SampleSize];

        foreach (var offset in SampleOffsets(length))
        {
            stream.Seek(offset, SeekOrigin.Begin);
            var toRead = (int)Math.Min(SampleSize, length - offset);
            var read = await stream.ReadAtLeastAsync(buffer.AsMemory(0, toRead), toRead, throwOnEndOfStream: false, ct);
            sha.AppendData(buffer.AsSpan(0, read));
        }

        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    private static IEnumerable<long> SampleOffsets(long length)
    {
        if (length <= SampleSize * 3L)
        {
            // Small enough that the three samples would overlap; just hash it whole.
            yield return 0;
            yield break;
        }

        yield return 0;
        yield return (length - SampleSize) / 2;
        yield return length - SampleSize;
    }
}
