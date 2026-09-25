using AudioBookReader.Core.Models;
using VersOne.Epub;

namespace AudioBookReader.Core.Books;

public class EpubTextExtractor : IBookTextExtractor
{
    public bool CanHandle(string path) =>
        Path.GetExtension(path).Equals(".epub", StringComparison.OrdinalIgnoreCase);

    public async Task<ExtractedBook> ExtractAsync(string path, CancellationToken ct = default)
    {
        var epub = await EpubReader.ReadBookAsync(path);

        var accumulator = new TextAccumulator();
        var documents = new List<ParsedHtmlDocument>();
        var files = epub.ReadingOrder.ToList();

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            documents.Add(HtmlTextPipeline.Parse(file.Content, accumulator));
        }

        var plainText = accumulator.Build();

        // Split per document so no sentence ever straddles a file boundary, which would make its
        // highlight impossible to render in one page.
        var sentences = new List<Sentence>();
        for (var i = 0; i < documents.Count; i++)
        {
            foreach (var (start, end) in SentenceSplitter.Split(plainText, documents[i].TextStart, documents[i].TextEnd))
                sentences.Add(new Sentence(sentences.Count, start, end, i));
        }

        var spine = new List<SpineDocument>(documents.Count);
        for (var i = 0; i < documents.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            spine.Add(new SpineDocument
            {
                Index = i,
                Href = files[i].FilePath,
                TextStart = documents[i].TextStart,
                TextEnd = documents[i].TextEnd,
                Html = HtmlTextPipeline.Render(documents[i], plainText, sentences),
                Anchors = documents[i].AnchorOffsets,
            });
        }

        var text = new BookText
        {
            PlainText = plainText,
            Sentences = sentences,
            Spine = spine,
            Title = string.IsNullOrWhiteSpace(epub.Title) ? Path.GetFileNameWithoutExtension(path) : epub.Title,
            Author = string.IsNullOrWhiteSpace(epub.Author) ? null : epub.Author,
        };

        return new ExtractedBook(text, BuildChapters(epub, files, documents, plainText.Length), epub.CoverImage);
    }

    /// <summary>
    /// Derives chapters from the table of contents, falling back to one chapter per spine document
    /// for books whose navigation is missing or unusable.
    /// </summary>
    private static List<Chapter> BuildChapters(
        EpubBook epub,
        List<VersOne.Epub.EpubLocalTextContentFile> files,
        List<ParsedHtmlDocument> documents,
        int textLength)
    {
        var byPath = new Dictionary<string, ParsedHtmlDocument>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < files.Count; i++) byPath[files[i].FilePath] = documents[i];

        var marks = NavigationLinks(epub.Navigation)
            .Select(item => (item.Title, Offset: ResolveOffset(item, byPath)))
            .Where(m => m.Offset is not null)
            .Select(m => (Title: m.Title ?? "", Offset: m.Offset!.Value))
            .OrderBy(m => m.Offset)
            .ToList();

        // Two navigation entries pointing into the same place make one chapter, not an empty one.
        marks = marks.Where((m, i) => i == 0 || m.Offset != marks[i - 1].Offset).ToList();

        if (marks.Count == 0)
        {
            marks = documents
                .Select((d, i) => (Title: string.Format(CoreStrings.Section_Numbered, i + 1), Offset: d.TextStart))
                .ToList();
        }

        if (marks.Count == 0) return [new Chapter { Index = 0, Title = "Text", TextStart = 0, TextEnd = textLength }];

        return [.. marks.Select((mark, i) => new Chapter
        {
            Index = i,
            Title = string.IsNullOrWhiteSpace(mark.Title)
                    ? string.Format(CoreStrings.Chapter_Numbered, i + 1)
                    : mark.Title.Trim(),
            // Anything before the first entry — cover, title page, dedication — belongs to the
            // first chapter rather than to nothing.
            TextStart = i == 0 ? 0 : mark.Offset,
            TextEnd = i + 1 < marks.Count ? marks[i + 1].Offset : textLength,
        })];
    }

    /// <summary>
    /// Flattens the table of contents to the entries that actually point somewhere, descending
    /// through pure headings but not through a chapter's own subsections.
    /// </summary>
    private static IEnumerable<EpubNavigationItem> NavigationLinks(IEnumerable<EpubNavigationItem>? items)
    {
        foreach (var item in items ?? [])
        {
            if (item.Link is not null)
            {
                yield return item;
            }
            else
            {
                foreach (var nested in NavigationLinks(item.NestedItems)) yield return nested;
            }
        }
    }

    private static int? ResolveOffset(EpubNavigationItem item, Dictionary<string, ParsedHtmlDocument> byPath)
    {
        var filePath = item.Link?.ContentFilePath;
        if (filePath is null || !byPath.TryGetValue(filePath, out var document)) return null;

        var anchor = item.Link!.Anchor;
        if (!string.IsNullOrEmpty(anchor) && document.AnchorOffsets.TryGetValue(anchor, out var offset))
            return offset;

        return document.TextStart;
    }
}
