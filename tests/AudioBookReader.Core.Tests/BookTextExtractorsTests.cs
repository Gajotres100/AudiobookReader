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

    // ---- Keeping one book ----

    /// <summary>
    /// Reading a paired book with measuring on has the reader, the live aligner and background
    /// alignment all working from the same file at once, and each used to parse and hold its own
    /// copy of it. The text is built once and never written to, so they can share one.
    /// </summary>
    [Fact]
    public async Task AsksForTheSameBookTwiceAndGetsTheSameTextBack()
    {
        var path = WriteEpub("novel.epub");

        var first = await _extractors.ExtractAsync(path);
        var second = await _extractors.ExtractAsync(path);

        Assert.Same(first.Text, second.Text);
    }

    /// <summary>
    /// Chapters are database rows, and saving a book's chapters writes ids straight onto the objects
    /// it is handed. Sharing those instances would let one caller's save rewrite what another is
    /// still reading, so each caller gets its own.
    /// </summary>
    [Fact]
    public async Task ChaptersAreNotSharedBetweenCallers()
    {
        var path = WriteEpub("novel.epub");

        var first = await _extractors.ExtractAsync(path);
        var second = await _extractors.ExtractAsync(path);

        Assert.NotEmpty(first.Chapters);
        Assert.NotSame(first.Chapters[0], second.Chapters[0]);

        // What ReplaceChaptersAsync does to whatever it is given.
        first.Chapters[0].BookId = 42;
        first.Chapters[0].Id = 7;

        var third = await _extractors.ExtractAsync(path);

        Assert.Equal(0, third.Chapters[0].BookId);
        Assert.Equal(0, third.Chapters[0].Id);
    }

    /// <summary>
    /// A book replaced on disk — a re-import, or the same name written again — must not be answered
    /// from the entry belonging to whatever used to be there.
    /// </summary>
    [Fact]
    public async Task ADifferentBookAtTheSamePathIsReadAgain()
    {
        var path = Path.Combine(_dir, "notes.txt");

        await File.WriteAllTextAsync(path, "The first book.");
        var first = await _extractors.ExtractAsync(path);

        // Stamped explicitly: the two writes land in the same millisecond on a fast disk, and the
        // entry is keyed partly on the write time.
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(-30));
        await File.WriteAllTextAsync(path, "A different book entirely.");

        var second = await _extractors.ExtractAsync(path);

        Assert.Equal("A different book entirely.", second.Text.PlainText);
        Assert.NotSame(first.Text, second.Text);
    }

    // ---- Cover art ----

    /// <summary>A book with no audio has nowhere else to get a cover, and an ebook nearly always
    /// carries one — without this a shelf of novels was a wall of title cards.</summary>
    [Fact]
    public async Task ReadsTheCoverOutOfAnEpub()
    {
        var art = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4 };

        var path = new TestEpubBuilder { Title = "A Novel", Cover = art }
            .Add("c1", "One", "<p>The story begins here.</p>")
            .WriteTo(Path.Combine(_dir, "withcover.epub"));

        var book = await _extractors.ExtractAsync(path);

        Assert.NotNull(book.Cover);
        Assert.Equal(art, book.Cover);
    }

    /// <summary>
    /// The kept copy has to carry the cover too. Handing back a book whose art had quietly gone
    /// missing on the second read would be worse than not keeping it at all.
    /// </summary>
    [Fact]
    public async Task TheKeptCopyStillHasTheCover()
    {
        var art = new byte[] { 0x89, 0x50, 0x4E, 0x47, 9, 9, 9 };

        var path = new TestEpubBuilder { Title = "A Novel", Cover = art }
            .Add("c1", "One", "<p>The story begins here.</p>")
            .WriteTo(Path.Combine(_dir, "withcover.epub"));

        await _extractors.ExtractAsync(path);
        var second = await _extractors.ExtractAsync(path);

        Assert.Equal(art, second.Cover);
    }

    [Fact]
    public async Task ABookWithNoCoverSaysSo()
    {
        var book = await _extractors.ExtractAsync(WriteEpub("plain.epub"));

        Assert.Null(book.Cover);
    }
}
