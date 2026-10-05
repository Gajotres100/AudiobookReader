using System.IO.Compression;
using AudioBookReader.Core.Books;

namespace AudioBookReader.Core.Tests;

public class ZipLayoutTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "zip-" + Guid.NewGuid().ToString("N"));

    public ZipLayoutTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static byte[] Noise(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private string Write(params (string Name, byte[] Bytes, CompressionLevel Level)[] files)
    {
        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".zip");

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, bytes, level) in files)
        {
            using var to = zip.CreateEntry(name, level).Open();
            to.Write(bytes);
        }

        return path;
    }

    [Fact]
    public async Task A_stored_file_is_found_where_its_bytes_are()
    {
        var text = "Chapter one. It was a dark and stormy night."u8.ToArray();
        var audio = Noise(300_000, 1);

        var path = Write(
            ("mimetype", "application/epub+zip"u8.ToArray(), CompressionLevel.NoCompression),
            ("OEBPS/one.xhtml", text, CompressionLevel.Optimal),
            ("OEBPS/audio/book.m4b", audio, CompressionLevel.NoCompression),
            ("OEBPS/two.xhtml", text, CompressionLevel.Optimal));

        await using var file = File.OpenRead(path);
        var layout = await ZipLayout.ReadAsync(file);

        Assert.Equal(["mimetype", "OEBPS/one.xhtml", "OEBPS/audio/book.m4b", "OEBPS/two.xhtml"], layout.Select(e => e.Name));

        var entry = layout.Single(e => e.Name == "OEBPS/audio/book.m4b");
        Assert.True(entry.IsStored);
        Assert.Equal(audio.Length, entry.Size);
        Assert.False(layout.Single(e => e.Name == "OEBPS/one.xhtml").IsStored);

        var offset = await ZipLayout.DataOffsetAsync(file, entry);

        await using var window = new SubStream(file, offset, entry.Size, ownsInner: false);
        using var copy = new MemoryStream();
        await window.CopyToAsync(copy);

        Assert.Equal(audio, copy.ToArray());
    }

    [Fact]
    public async Task A_window_seeks_and_stops_at_its_own_end()
    {
        var bytes = Noise(1000, 2);
        await using var window = new SubStream(new MemoryStream(bytes), 100, 50);

        Assert.Equal(50, window.Length);

        window.Seek(-10, SeekOrigin.End);
        var tail = new byte[100];
        var read = await window.ReadAsync(tail);

        Assert.Equal(10, read);
        Assert.Equal(bytes[140..150], tail[..10]);
        Assert.Equal(0, await window.ReadAsync(tail));
    }

    [Fact]
    public async Task Something_that_is_not_a_zip_is_refused()
    {
        await using var stream = new MemoryStream(Noise(5000, 3));
        await Assert.ThrowsAsync<InvalidDataException>(() => ZipLayout.ReadAsync(stream));
    }
}
