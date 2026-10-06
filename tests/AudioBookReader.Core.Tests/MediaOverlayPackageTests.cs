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
    public async Task A_stored_narration_is_found_inside_the_package_and_the_text_comes_without_it()
    {
        var path = WriteBook();
        await using var package = File.OpenRead(path);

        var audioFiles = MediaOverlayPackage.AudioFiles(package);
        Assert.Equal(["OEBPS/audio/book.m4b"], audioFiles);

        // The quick answer, from the package document alone, agrees.
        Assert.Equal(audioFiles, MediaOverlayPackage.DeclaredNarration(package));

        var narration = await MediaOverlayPackage.FindStoredNarrationAsync(package, audioFiles!);
        Assert.NotNull(narration);
        Assert.Equal("OEBPS/audio/book.m4b", narration.Entry);
        Assert.Equal(FakeAudio.Length, narration.Length);

        // The stretch of the package file is the recording, byte for byte: what a player streams.
        var played = new byte[narration.Length];
        package.Seek(narration.Offset, SeekOrigin.Begin);
        await package.ReadExactlyAsync(played);
        Assert.Equal(FakeAudio, played);

        // Only the text comes down, and it still reads along.
        var slim = Path.Combine(_directory, "streamed.epub");
        await MediaOverlayPackage.SplitAsync(package, narration.Entry, audioOut: null, slim);

        using (var zip = ZipFile.OpenRead(slim))
            Assert.Null(zip.GetEntry("OEBPS/audio/book.m4b"));

        var text = (await new EpubTextExtractor().ExtractAsync(slim)).Text;
        Assert.NotEmpty(MediaOverlayPackage.ReadAnchors(slim, text, narration.Entry));
        // The same text with the package read as the runs either side of the recording, never
        // touching the recording itself: what streaming fetches, in two requests.
        var around = Path.Combine(_directory, "around.epub");
        await MediaOverlayPackage.SplitAroundAsync(package, narration, around);

        var aroundText = (await new EpubTextExtractor().ExtractAsync(around)).Text;
        Assert.Equal(text.PlainText, aroundText.PlainText);
        Assert.Equal(
            MediaOverlayPackage.ReadAnchors(slim, text, narration.Entry),
            MediaOverlayPackage.ReadAnchors(around, aroundText, narration.Entry));
    }

    [Fact]
    public async Task A_compressed_narration_cannot_be_streamed()
    {
        var path = Path.Combine(_directory, "squeezed.epub");

        using (var source = ZipFile.OpenRead(WriteBook()))
        using (var target = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var entry in source.Entries)
            {
                await using var from = entry.Open();
                await using var to = target.CreateEntry(entry.FullName, CompressionLevel.Optimal).Open();
                await from.CopyToAsync(to);
            }
        }

        await using var package = File.OpenRead(path);
        Assert.Null(await MediaOverlayPackage.FindStoredNarrationAsync(package, ["OEBPS/audio/book.m4b"]));
    }

    /// <summary>The case seen in a real package: a chapter's opening timed into the end of the chapter before.</summary>
    [Fact]
    public void A_chapter_opening_squeezed_into_the_previous_chapter_is_put_back()
    {
        var anchors = new List<Anchor>
        {
            new(10_000, 1_000, 1f),
            new(20_000, 1_150, 1f),      // the previous chapter, read at 15 a second
            new(21_000, 1_200, 1f),      // heading of the next chapter, at 1200 ...
            new(22_000, 1_300, 1f),      // ... and its opening at 100 a second, impossible ...
            new(23_000, 1_400, 1f),
            new(24_000, 1_500, 1f),
            new(60_000, 1_600, 1f),      // ... then a long pause before the next real timing
            new(70_000, 1_750, 1f),
        };

        var repaired = MediaOverlayPackage.RepairChapterOpenings(anchors, [1_200], [55_000]);

        // The squeezed run is gone, and the chapter starts on the recording's chapter mark.
        Assert.DoesNotContain(repaired, a => a.CharOffset is 1_300 or 1_400 or 1_500);
        Assert.Contains(new Anchor(55_000, 1_200, 1f), repaired);
        Assert.Contains(new Anchor(20_000, 1_150, 1f), repaired);
        Assert.Contains(new Anchor(60_000, 1_600, 1f), repaired);
    }

    [Fact]
    public void Without_a_chapter_mark_the_opening_is_placed_at_an_ordinary_pace_before_the_next_timing()
    {
        var anchors = new List<Anchor>
        {
            new(20_000, 1_150, 1f),
            new(21_000, 1_200, 1f),
            new(22_000, 1_350, 1f),
            new(23_000, 1_500, 1f),
            new(60_000, 1_650, 1f),
        };

        var repaired = MediaOverlayPackage.RepairChapterOpenings(anchors, [1_200], []);

        // 450 characters before the next timing, at fifteen a second: thirty seconds earlier.
        Assert.Contains(new Anchor(30_000, 1_200, 1f), repaired);
    }

    [Fact]
    public void An_opening_read_at_an_ordinary_pace_is_left_alone()
    {
        var anchors = new List<Anchor>
        {
            new(20_000, 1_150, 1f),
            new(23_000, 1_200, 1f),
            new(30_000, 1_300, 1f),
            new(37_000, 1_400, 1f),
            new(60_000, 1_500, 1f),
        };

        Assert.Equal(anchors, MediaOverlayPackage.RepairChapterOpenings(anchors, [1_200], [22_000]));
    }

    [Fact]
    public void An_ordinary_epub_has_no_overlay_audio()
    {
        var path = new TestEpubBuilder().Add("one", "One", "<p>Hello.</p>").WriteTo(Path.Combine(_directory, "plain.epub"));

        Assert.Null(MediaOverlayPackage.AudioFiles(path));

        using var stream = File.OpenRead(path);
        Assert.Null(MediaOverlayPackage.DeclaredNarration(stream));
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
