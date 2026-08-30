using AudioBookReader.Core.Books;
using HtmlAgilityPack;

namespace AudioBookReader.Core.Tests;

public class EpubTextExtractorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("abr-epub-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private async Task<ExtractedBook> ExtractAsync(TestEpubBuilder builder, string name = "book.epub")
    {
        var path = builder.WriteTo(Path.Combine(_dir, name));
        return await new EpubTextExtractor().ExtractAsync(path);
    }

    /// <summary>Concatenates the rendered text of every span carrying one sentence's index.</summary>
    private static string SpanTextFor(ExtractedBook book, int sentenceIndex)
    {
        var text = "";

        foreach (var document in book.Text.Spine)
        {
            var html = new HtmlDocument();
            html.LoadHtml(document.Html);

            var spans = html.DocumentNode.SelectNodes($"//span[@data-idx='{sentenceIndex}']");
            if (spans is null) continue;

            foreach (var span in spans) text += HtmlEntity.DeEntitize(span.InnerText);
        }

        return text;
    }

    // ---- The round trip alignment depends on ----

    [Fact]
    public async Task EverySentenceIsRecoverableFromItsSpans()
    {
        var book = await ExtractAsync(new TestEpubBuilder()
            .Add("c1", "One", "<p>He woke early. The house was <em>bitterly</em> cold, and quiet.</p><p>Nobody stirred.</p>")
            .Add("c2", "Two", "<h2>The Arrival</h2><p>She knocked twice. <strong>No answer.</strong></p>"));

        Assert.NotEmpty(book.Text.Sentences);

        foreach (var sentence in book.Text.Sentences)
        {
            var expected = book.Text.PlainText[sentence.Start..sentence.End];
            Assert.Equal(expected, SpanTextFor(book, sentence.Index));
        }
    }

    [Fact]
    public async Task ASentenceCrossingInlineMarkupKeepsOneIndexAcrossSeveralSpans()
    {
        var book = await ExtractAsync(new TestEpubBuilder()
            .Add("c1", "One", "<p>The house was <em>bitterly</em> cold.</p>"));

        var sentence = Assert.Single(book.Text.Sentences);
        Assert.Equal("The house was bitterly cold.", book.Text.PlainText[sentence.Start..sentence.End]);

        var html = new HtmlDocument();
        html.LoadHtml(book.Text.Spine[0].Html);

        // Three spans: before the emphasis, inside it, and after it — all one sentence.
        var spans = html.DocumentNode.SelectNodes("//span[@data-idx='0']");
        Assert.NotNull(spans);
        Assert.Equal(3, spans.Count);
        Assert.NotNull(html.DocumentNode.SelectSingleNode("//em/span[@data-idx='0']"));
    }

    [Fact]
    public async Task RenderedHtmlKeepsTheOriginalMarkup()
    {
        var book = await ExtractAsync(new TestEpubBuilder()
            .Add("c1", "One", "<h2>Title</h2><p>Body text here.</p><blockquote><p>A quote.</p></blockquote>"));

        var html = book.Text.Spine[0].Html;

        Assert.Contains("<h2>", html);
        Assert.Contains("<blockquote>", html);
        Assert.Contains("data-idx=", html);
    }

    // ---- Text normalization ----

    [Fact]
    public async Task CollapsesWhitespaceAndSeparatesBlocks()
    {
        var book = await ExtractAsync(new TestEpubBuilder()
            .Add("c1", "One", "<p>Spread   across\n   lines.</p><p>Second block.</p>"));

        Assert.Equal("Spread across lines.\nSecond block.", book.Text.PlainText.Trim());
    }

    [Fact]
    public async Task DoesNotRunWordsTogetherAcrossInlineElements()
    {
        var book = await ExtractAsync(new TestEpubBuilder()
            .Add("c1", "One", "<p>one <em>two</em> three</p>"));

        Assert.Equal("one two three", book.Text.PlainText.Trim());

        // The separating spaces must survive rendering too, not just the plain-text stream.
        var html = new HtmlDocument();
        html.LoadHtml(book.Text.Spine[0].Html);
        Assert.Equal("one two three", HtmlEntity.DeEntitize(html.DocumentNode.InnerText).Trim());
    }

    [Fact]
    public async Task IgnoresScriptAndStyleContent()
    {
        var book = await ExtractAsync(new TestEpubBuilder()
            .Add("c1", "One", "<style>p { color: red; }</style><p>Real text.</p><script>alert(1)</script>"));

        Assert.Equal("Real text.", book.Text.PlainText.Trim());
    }

    [Fact]
    public async Task DecodesEntities()
    {
        var book = await ExtractAsync(new TestEpubBuilder()
            .Add("c1", "One", "<p>Caf&#233; &amp; bar.</p>"));

        Assert.Equal("Café & bar.", book.Text.PlainText.Trim());
    }

    [Fact]
    public async Task ReEscapesSpecialCharactersInRenderedHtml()
    {
        var book = await ExtractAsync(new TestEpubBuilder()
            .Add("c1", "One", "<p>Salt &amp; pepper.</p>"));

        Assert.Contains("&amp;", book.Text.Spine[0].Html);
        Assert.Equal("Salt & pepper.", book.Text.PlainText.Trim());
    }

    // ---- Structure ----

    [Fact]
    public async Task SpineRangesCoverTheTextContiguously()
    {
        var book = await ExtractAsync(new TestEpubBuilder()
            .Add("c1", "One", "<p>First document.</p>")
            .Add("c2", "Two", "<p>Second document.</p>")
            .Add("c3", "Three", "<p>Third document.</p>"));

        Assert.Equal(3, book.Text.Spine.Count);
        Assert.Equal(0, book.Text.Spine[0].TextStart);

        for (var i = 1; i < book.Text.Spine.Count; i++)
            Assert.Equal(book.Text.Spine[i - 1].TextEnd, book.Text.Spine[i].TextStart);

        Assert.Equal(book.Text.PlainText.Length, book.Text.Spine[^1].TextEnd);
    }

    [Fact]
    public async Task NoSentenceStraddlesTwoDocuments()
    {
        var book = await ExtractAsync(new TestEpubBuilder()
            .Add("c1", "One", "<p>Ends without punctuation</p>")
            .Add("c2", "Two", "<p>and continues here.</p>"));

        foreach (var sentence in book.Text.Sentences)
        {
            var document = book.Text.Spine[sentence.SpineIndex];
            Assert.InRange(sentence.Start, document.TextStart, document.TextEnd);
            Assert.InRange(sentence.End, document.TextStart, document.TextEnd);
        }
    }

    [Fact]
    public async Task ReadsTitleAndAuthorFromMetadata()
    {
        var book = await ExtractAsync(new TestEpubBuilder { Title = "A Novel", Author = "R. Writer" }
            .Add("c1", "One", "<p>Text.</p>"));

        Assert.Equal("A Novel", book.Text.Title);
        Assert.Equal("R. Writer", book.Text.Author);
    }

    // ---- Chapters ----

    [Fact]
    public async Task BuildsChaptersFromTheTableOfContents()
    {
        var book = await ExtractAsync(new TestEpubBuilder()
            .Add("c1", "The Arrival", "<p>First chapter text.</p>")
            .Add("c2", "The Departure", "<p>Second chapter text.</p>"));

        Assert.Equal(["The Arrival", "The Departure"], book.Chapters.Select(c => c.Title));
        Assert.All(book.Chapters, c => Assert.True(c.HasTextRange));
        Assert.All(book.Chapters, c => Assert.False(c.HasAudioRange));
    }

    [Fact]
    public async Task ChapterRangesCoverTheWholeTextWithoutGaps()
    {
        var book = await ExtractAsync(new TestEpubBuilder()
            .Add("c1", "One", "<p>First.</p>")
            .Add("c2", "Two", "<p>Second.</p>")
            .Add("c3", "Three", "<p>Third.</p>"));

        Assert.Equal(0, book.Chapters[0].TextStart);
        Assert.Equal(book.Text.PlainText.Length, book.Chapters[^1].TextEnd);

        for (var i = 1; i < book.Chapters.Count; i++)
            Assert.Equal(book.Chapters[i - 1].TextEnd, book.Chapters[i].TextStart);
    }

    [Fact]
    public async Task FrontMatterBeforeTheFirstEntryBelongsToTheFirstChapter()
    {
        // The table of contents starts at the second document; the title page must not fall outside
        // every chapter.
        var book = await ExtractAsync(new TestEpubBuilder()
            .Add("cover", "Chapter One", "<p>Title page.</p>", anchor: "start")
            .Add("c2", "Chapter Two", "<p>Second.</p>"));

        Assert.Equal(0, book.Chapters[0].TextStart);
    }

    [Fact]
    public async Task ResolvesAChapterAnchorWithinADocument()
    {
        var book = await ExtractAsync(new TestEpubBuilder()
            .Add("c1", "Preface", "<p>Front matter.</p>")
            .Add("c2", "Chapter One", "<p>Still front matter.</p><h2 id=\"real-start\">Chapter One</h2><p>The story begins.</p>",
                anchor: "real-start"));

        var chapterOne = book.Chapters[1];

        // The anchor sits past the paragraph preceding it, not at the top of the file.
        Assert.StartsWith("Chapter One", book.Text.PlainText[chapterOne.TextStart!.Value..]);
    }
}
