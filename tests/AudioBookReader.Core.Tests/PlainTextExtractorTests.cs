using AudioBookReader.Core.Books;
using HtmlAgilityPack;

namespace AudioBookReader.Core.Tests;

public class PlainTextExtractorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("abr-txt-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private async Task<ExtractedBook> ExtractAsync(string content, string name = "book.txt")
    {
        var path = Path.Combine(_dir, name);
        await File.WriteAllTextAsync(path, content);
        return await new PlainTextExtractor().ExtractAsync(path);
    }

    [Fact]
    public async Task JoinsWrappedLinesIntoOneParagraph()
    {
        var book = await ExtractAsync("He woke early\nand the house\nwas cold.");

        Assert.Equal("He woke early and the house was cold.", book.Text.PlainText);
        Assert.Single(book.Text.Sentences);
    }

    [Fact]
    public async Task TreatsABlankLineAsAParagraphBreak()
    {
        var book = await ExtractAsync("First paragraph.\n\nSecond paragraph.");

        Assert.Equal("First paragraph.\nSecond paragraph.", book.Text.PlainText);
        Assert.Equal(2, book.Text.Sentences.Count);
    }

    [Fact]
    public async Task HandlesWindowsLineEndings()
    {
        var book = await ExtractAsync("First line\r\nsame paragraph.\r\n\r\nNext paragraph.");

        Assert.Equal("First line same paragraph.\nNext paragraph.", book.Text.PlainText);
    }

    [Fact]
    public async Task EverySentenceIsRecoverableFromItsSpans()
    {
        var book = await ExtractAsync("He woke early. The house was cold.\n\nNobody stirred at all.");

        var html = new HtmlDocument();
        html.LoadHtml(book.Text.Spine[0].Html);

        foreach (var sentence in book.Text.Sentences)
        {
            var spans = html.DocumentNode.SelectNodes($"//span[@data-idx='{sentence.Index}']");
            Assert.NotNull(spans);

            var rendered = string.Concat(spans.Select(s => HtmlEntity.DeEntitize(s.InnerText)));
            Assert.Equal(book.Text.PlainText[sentence.Start..sentence.End], rendered);
        }
    }

    [Fact]
    public async Task RendersOneParagraphElementPerParagraph()
    {
        var book = await ExtractAsync("One.\n\nTwo.\n\nThree.");

        var html = new HtmlDocument();
        html.LoadHtml(book.Text.Spine[0].Html);

        var paragraphs = html.DocumentNode.SelectNodes("//p");
        Assert.NotNull(paragraphs);
        Assert.Equal(3, paragraphs.Count);
    }

    [Fact]
    public async Task EscapesMarkupCharactersInTheSource()
    {
        var book = await ExtractAsync("Use <b> & </b> carefully.");

        Assert.Equal("Use <b> & </b> carefully.", book.Text.PlainText);
        Assert.DoesNotContain("<b>", book.Text.Spine[0].Html);
        Assert.Contains("&lt;b&gt;", book.Text.Spine[0].Html);
    }

    [Fact]
    public async Task ProducesOneChapterCoveringTheWholeFile()
    {
        var book = await ExtractAsync("Some text.\n\nMore text.");

        var chapter = Assert.Single(book.Chapters);
        Assert.Equal(0, chapter.TextStart);
        Assert.Equal(book.Text.PlainText.Length, chapter.TextEnd);
        Assert.False(chapter.HasAudioRange);
    }

    [Fact]
    public async Task HandlesAnEmptyFile()
    {
        var book = await ExtractAsync("");

        Assert.Equal("", book.Text.PlainText);
        Assert.Empty(book.Text.Sentences);
    }
}
