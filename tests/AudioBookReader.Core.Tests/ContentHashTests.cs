using AudioBookReader.Core.Data;

namespace AudioBookReader.Core.Tests;

public class ContentHashTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("abr-hash-tests").FullName;

    private async Task<string> WriteFileAsync(string name, byte[] content)
    {
        var path = Path.Combine(_dir, name);
        await File.WriteAllBytesAsync(path, content);
        return path;
    }

    private static byte[] Pattern(int length, byte seed = 0)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++) bytes[i] = (byte)((i * 31 + seed) % 251);
        return bytes;
    }

    [Fact]
    public async Task IdenticalContentHashesTheSame()
    {
        var a = await WriteFileAsync("a.bin", Pattern(64 * 1024));
        var b = await WriteFileAsync("b.bin", Pattern(64 * 1024));

        Assert.Equal(await ContentHash.ComputeAsync(a), await ContentHash.ComputeAsync(b));
    }

    [Fact]
    public async Task DifferentContentOfTheSameLengthHashesDifferently()
    {
        var a = await WriteFileAsync("a.bin", Pattern(64 * 1024));
        var b = await WriteFileAsync("b.bin", Pattern(64 * 1024, seed: 7));

        Assert.NotEqual(await ContentHash.ComputeAsync(a), await ContentHash.ComputeAsync(b));
    }

    [Fact]
    public async Task DifferentLengthsHashDifferently()
    {
        var a = await WriteFileAsync("a.bin", Pattern(64 * 1024));
        var b = await WriteFileAsync("b.bin", Pattern(65 * 1024));

        Assert.NotEqual(await ContentHash.ComputeAsync(a), await ContentHash.ComputeAsync(b));
    }

    [Fact]
    public async Task NoticesAChangeInEachSampledRegionOfALargeFile()
    {
        // Larger than 3 MiB, so head, middle and tail are sampled separately rather than whole.
        const int size = 8 * 1024 * 1024;
        var original = Pattern(size);
        var baseline = await ContentHash.ComputeAsync(await WriteFileAsync("base.bin", original));

        foreach (var (name, position) in new[] { ("head", 16), ("middle", size / 2), ("tail", size - 16) })
        {
            var mutated = (byte[])original.Clone();
            mutated[position] ^= 0xFF;

            var hash = await ContentHash.ComputeAsync(await WriteFileAsync($"{name}.bin", mutated));
            Assert.True(hash != baseline, $"change at the {name} of the file went unnoticed");
        }
    }

    [Fact]
    public async Task HandlesAnEmptyFile()
    {
        var path = await WriteFileAsync("empty.bin", []);

        Assert.False(string.IsNullOrEmpty(await ContentHash.ComputeAsync(path)));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
