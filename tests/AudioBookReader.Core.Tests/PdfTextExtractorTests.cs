using AudioBookReader.Core.Books;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Outline;
using UglyToad.PdfPig.Outline.Destinations;
using UglyToad.PdfPig.Writer;

namespace AudioBookReader.Core.Tests;

public class PdfTextExtractorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pdf-" + Guid.NewGuid().ToString("N"));

    public PdfTextExtractorTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    /// <summary>
    /// A book laid out the way a typeset PDF is: lines placed one under another, a first-line
    /// indent for each paragraph, and a page number at the foot.
    /// </summary>
    private string Write(string name, IReadOnlyList<string[]> pages, IReadOnlyList<(string Title, int Page)>? outline = null)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);

        for (var p = 0; p < pages.Count; p++)
        {
            var page = builder.AddPage(PageSize.A4);
            var y = 780.0;

            foreach (var line in pages[p])
            {
                // A leading ">" marks the first line of a paragraph, indented as a book sets it.
                var indented = line.StartsWith('>');
                page.AddText(indented ? line[1..] : line, 11, new PdfPoint(indented ? 92 : 72, y), font);
                y -= 14;
            }

            page.AddText((p + 1).ToString(), 9, new PdfPoint(290, 40), font);
        }

        if (outline is not null)
        {
            builder.Bookmarks = new Bookmarks(
            [
                .. outline.Select(o => (BookmarkNode)new DocumentBookmarkNode(
                    o.Title, 0,
                    new ExplicitDestination(o.Page, ExplicitDestinationType.FitPage, ExplicitDestinationCoordinates.Empty),
                    [])),
            ]);
        }

        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, builder.Build());
        return path;
    }

    [Fact]
    public async Task Lines_are_joined_into_paragraphs_across_hyphens_and_pages()
    {
        var path = Write("book.pdf",
        [
            [">The lamp was lit early that even-", "ing, and nobody said why.", ">A second paragraph begins", "here and runs on to the"],
            ["next page without a break.", ">Then a third one."],
        ]);

        var book = await new PdfTextExtractor().ExtractAsync(path);

        Assert.Equal(
            "The lamp was lit early that evening, and nobody said why.\n" +
            "A second paragraph begins here and runs on to the next page without a break.\n" +
            "Then a third one.",
            book.Text.PlainText);
    }

    [Fact]
    public async Task Sentences_and_spans_cover_the_text()
    {
        var path = Write("book.pdf", [[">One sentence here. Another one there."]]);

        var book = await new PdfTextExtractor().ExtractAsync(path);

        Assert.Equal(2, book.Text.Sentences.Count);
        Assert.Contains("data-idx=\"1\"", book.Text.Spine[0].Html);
    }

    [Fact]
    public async Task The_outline_gives_the_chapters()
    {
        var path = Write("book.pdf",
            [[">Front matter."], [">Chapter one text."], [">Chapter two text."]],
            [("One", 2), ("Two", 3)]);

        var book = await new PdfTextExtractor().ExtractAsync(path);

        Assert.Equal(["One", "Two"], book.Chapters.Select(c => c.Title));
        Assert.Equal(0, book.Chapters[0].TextStart);
        Assert.Equal("Chapter two text.", book.Text.PlainText[book.Chapters[1].TextStart!.Value..book.Chapters[1].TextEnd!.Value]);
        Assert.Equal(2, book.Text.Spine.Count);
    }

    [Fact]
    public async Task A_running_header_is_left_out()
    {
        string[] words = ["amber", "birch", "cedar", "dune", "ember", "fjord", "grove", "heath"];
        var pages = words
            .Select(w => new[] { "THE LAMPLIGHTER", $">The {w} was quiet that night.", $">Nobody spoke of the {w} again." })
            .ToList();

        var book = await new PdfTextExtractor().ExtractAsync(Write("book.pdf", pages));

        Assert.DoesNotContain("LAMPLIGHTER", book.Text.PlainText);
        Assert.Contains("Nobody spoke of the heath again.", book.Text.PlainText);
    }

    [Fact]
    public async Task A_pdf_that_lost_its_extension_is_still_recognised()
    {
        var path = Write("download", [[">Some text."]]);

        var book = await new BookTextExtractors().ExtractAsync(path);

        Assert.Equal("Some text.", book.Text.PlainText);
    }
}
