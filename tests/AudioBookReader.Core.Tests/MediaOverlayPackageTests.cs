using System.IO.Compression;
using AudioBookReader.Core.Books;
using AudioBookReader.Core.Data;
using AudioBookReader.Core.Models;

namespace AudioBookReader.Core.Tests;

public class MediaOverlayPackageTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mo-" + Guid.NewGuid().ToString("N"));

    public MediaOverlayPackageTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static readonly byte[] FakeAudio = [1, 2, 3, 4, 5, 6, 7, 8];

    /// <summary>Two chapters, each sentence with an id, and an overlay timing every sentence.</summary>
    private string WriteBook()
    {
        var path = Path.Combine(_directory, "book.epub");

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        void Add(string name, string content)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(content);
        }

        Add("mimetype", "application/epub+zip");
        Add("META-INF/container.xml",
            """<?xml version="1.0"?><container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>""");

        Add("OEBPS/content.opf", """
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>Keeper</dc:title><dc:identifier id="id">x</dc:identifier><dc:language>en</dc:language></metadata>
              <manifest>
                <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
                <item id="c1" href="Text/one.xhtml" media-type="application/xhtml+xml" media-overlay="m1"/>
                <item id="c2" href="Text/two.xhtml" media-type="application/xhtml+xml" media-overlay="m2"/>
                <item id="m1" href="Text/one.smil" media-type="application/smil+xml"/>
                <item id="m2" href="Text/two.smil" media-type="application/smil+xml"/>
                <item id="a" href="audio/book.m4b" media-type="audio/mp4"/>
              </manifest>
              <spine><itemref idref="c1"/><itemref idref="c2"/></spine>
            </package>
            """);

        Add("OEBPS/nav.xhtml", """<?xml version="1.0"?><html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><head><title>t</title></head><body><nav epub:type="toc"><ol><li><a href="Text/one.xhtml">One</a></li><li><a href="Text/two.xhtml">Two</a></li></ol></nav></body></html>""");
        Add("OEBPS/Text/one.xhtml", """<?xml version="1.0"?><html xmlns="http://www.w3.org/1999/xhtml"><head><title>1</title></head><body><p><span id="a1">The keeper woke early.</span> <span id="a2">He climbed the stairs.</span></p></body></html>""");
        Add("OEBPS/Text/two.xhtml", """<?xml version="1.0"?><html xmlns="http://www.w3.org/1999/xhtml"><head><title>2</title></head><body><p><span id="b1">The storm came at night.</span></p></body></html>""");

        Add("OEBPS/Text/one.smil", """
            <smil xmlns="http://www.w3.org/ns/SMIL" version="3.0"><body><seq>
              <par><text src="one.xhtml#a1"/><audio src="../audio/book.m4b" clipBegin="0:00:01.000" clipEnd="0:00:03.000"/></par>
              <par><text src="one.xhtml#a2"/><audio src="../audio/book.m4b" clipBegin="3s" clipEnd="5.5s"/></par>
            </seq></body></smil>
            """);
        Add("OEBPS/Text/two.smil", """
            <smil xmlns="http://www.w3.org/ns/SMIL" version="3.0"><body><seq>
              <par><text src="two.xhtml#b1"/><audio src="../audio/book.m4b" clipBegin="00:10.000" clipEnd="00:12.000"/></par>
            </seq></body></smil>
            """);

        using (var audio = zip.CreateEntry("OEBPS/audio/book.m4b", CompressionLevel.NoCompression).Open())
            audio.Write(FakeAudio);

        return path;
    }

    [Fact]
    public void An_ordinary_epub_has_no_overlay_audio()
    {
        var path = new TestEpubBuilder().Add("one", "One", "<p>Hello.</p>").WriteTo(Path.Combine(_directory, "plain.epub"));

        Assert.Null(MediaOverlayPackage.AudioFiles(path));
    }

    [Fact]
    public async Task The_audio_comes_out_and_the_book_opens_without_it()
    {
        var path = WriteBook();

        Assert.Equal(["OEBPS/audio/book.m4b"], MediaOverlayPackage.AudioFiles(path));

        var slim = Path.Combine(_directory, "slim.epub");
        using var audio = new MemoryStream();
        await MediaOverlayPackage.SplitAsync(path, "OEBPS/audio/book.m4b", audio, slim);

        Assert.Equal(FakeAudio, audio.ToArray());

        using (var zip = ZipFile.OpenRead(slim))
        {
            Assert.Null(zip.GetEntry("OEBPS/audio/book.m4b"));
            Assert.Equal("mimetype", zip.Entries[0].FullName);
        }

        var book = await new EpubTextExtractor().ExtractAsync(slim);
        Assert.Contains("The storm came at night.", book.Text.PlainText);
    }

    [Fact]
    public async Task Every_timed_sentence_becomes_an_anchor_where_it_starts()
    {
        var path = WriteBook();
        var slim = Path.Combine(_directory, "slim.epub");

        using (var audio = new MemoryStream())
            await MediaOverlayPackage.SplitAsync(path, "OEBPS/audio/book.m4b", audio, slim);

        var text = (await new EpubTextExtractor().ExtractAsync(slim)).Text;
        var anchors = MediaOverlayPackage.ReadAnchors(slim, text, "OEBPS/audio/book.m4b");

        Assert.Equal([1000L, 3000L, 10000L], anchors.Select(a => a.AudioMs));
        Assert.Equal("He climbed", text.PlainText.Substring(anchors[1].CharOffset, 10));
        Assert.Equal("The storm", text.PlainText.Substring(anchors[2].CharOffset, 9));
    }

    [Fact]
    public void Anchors_are_laid_over_the_audio_chapters()
    {
        Anchor[] anchors = [new(1000, 0, 1f), new(3000, 23, 1f), new(10000, 46, 1f), new(11000, 60, 1f)];
        Chapter[] chapters =
        [
            new() { Index = 0, StartMs = 0, EndMs = 8000 },
            new() { Index = 1, StartMs = 8000, EndMs = 12000 },
        ];

        var (map, ranges) = MediaOverlayPackage.BuildMap(anchors, chapters, 70, "a", "e");

        Assert.True(map.MatchesPair("a", "e"));
        Assert.Equal(2, map.Chapters.Count);
        Assert.Equal([new ChapterRange(0, 0, 46), new ChapterRange(1, 46, 70)], ranges);
    }

    [Theory]
    [InlineData("1:02:03.5", 3_723_500)]
    [InlineData("02:03.250", 123_250)]
    [InlineData("12.5s", 12_500)]
    [InlineData("2.5min", 150_000)]
    [InlineData("345ms", 345)]
    [InlineData("7", 7_000)]
    public void Smil_clock_values_are_read_in_every_form(string value, long expected)
    {
        Assert.True(SmilClock.TryParse(value, out var ms));
        Assert.Equal(expected, ms);
    }
}
