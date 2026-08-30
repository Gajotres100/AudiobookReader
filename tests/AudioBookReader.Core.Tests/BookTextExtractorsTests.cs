using AudioBookReader.Core.Books;

namespace AudioBookReader.Core.Tests;

public class BookTextExtractorsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("abr-extractors-tests").FullName;
    private readonly BookTextExtractors _extractors = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteEpub(string fileName) =>
        new TestEpubBuilder { Title = "A Novel" }
            .Add("c1", "One", "<p>The story begins here.</p>")
            .WriteTo(Path.Combine(_dir, fileName));

    [Fact]
    public async Task StillWorksWhenConstructedWithAnEmptyExtractorList()
    {
        // A DI container asked for IEnumerable<T> supplies an empty collection, not null, when
        // nothing is registered — which once left this class supporting no formats at all while
        // looking perfectly constructed, and only on the device.
        var fromContainer = new BookTextExtractors([]);

        var book = await fromContainer.ExtractAsync(WriteEpub("novel.epub"));

        Assert.Contains("The story begins here.", book.Text.PlainText);
        Assert.True(fromContainer.CanHandle("anything.epub"));
    }

    [Fact]
    public async Task ReadsAnEpubByItsExtension()
    {
        var book = await _extractors.ExtractAsync(WriteEpub("novel.epub"));

        Assert.Contains("The story begins here.", book.Text.PlainText);
    }

    [Fact]
    public async Task ReadsAnEpubWhoseNameLostItsExtension()
    {
        // Cloud storage and messaging apps rename attachments and strip suffixes routinely, and
        // refusing a perfectly good book over four missing characters is not defensible when the
        // first four bytes settle it.
        var book = await _extractors.ExtractAsync(WriteEpub("novel"));

        Assert.Contains("The story begins here.", book.Text.PlainText);
    }

    [Fact]
    public async Task ReadsAnEpubWithAMisleadingExtension()
    {
        var book = await _extractors.ExtractAsync(WriteEpub("novel.download"));

        Assert.Contains("The story begins here.", book.Text.PlainText);
    }

    [Fact]
    public async Task ReadsPlainTextByItsExtension()
    {
        var path = Path.Combine(_dir, "notes.txt");
        await File.WriteAllTextAsync(path, "Just some text.");

        var book = await _extractors.ExtractAsync(path);

        Assert.Equal("Just some text.", book.Text.PlainText);
    }

    [Fact]
    public async Task RefusesSomethingThatIsNeither()
    {
        var path = Path.Combine(_dir, "cover.jpg");

        // A JPEG header — not a zip, not text we should pretend to understand.
        await File.WriteAllBytesAsync(path, [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46]);

        await Assert.ThrowsAsync<NotSupportedException>(() => _extractors.ExtractAsync(path));
    }

    [Fact]
    public async Task RefusesAFileTooShortToIdentify()
    {
        var path = Path.Combine(_dir, "empty.bin");
        await File.WriteAllBytesAsync(path, [0x01]);

        await Assert.ThrowsAsync<NotSupportedException>(() => _extractors.ExtractAsync(path));
    }
}
