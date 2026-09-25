using System.Text;
using AudioBookReader.Core.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Outline;

namespace AudioBookReader.Core.Books;

/// <summary>
/// Reads a PDF into the same shape as every other format: running text in paragraphs, split into
/// chapters.
///
/// A PDF has no paragraphs, only glyphs placed on pages, so they are recovered from the layout: a
/// line that starts further in, or follows a larger gap than the page's usual spacing, starts a new
/// one. Lines inside a paragraph are joined back up, hyphenation undone, and a paragraph broken by
/// the end of a page is carried on to the next. Page numbers and the running header or footer
/// repeated on every page are left out, since they are not part of the text anybody narrates.
///
/// Deliberately done with plain line geometry rather than a full layout analysis: this runs every
/// time a book is opened, on a phone, and books are set in a single column — which is the case the
/// simple approach gets right. A PDF that is only scanned images has no text in it at all, and the
/// import says so.
/// </summary>
public class PdfTextExtractor : IBookTextExtractor
{
    /// <summary>Pages per chapter when the file has no outline of its own to go by.</summary>
    private const int PagesPerSection = 20;

    public bool CanHandle(string path) =>
        Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    public Task<ExtractedBook> ExtractAsync(string path, CancellationToken ct = default) =>
        Task.Run(() => Extract(path, ct), ct);

    private static ExtractedBook Extract(string path, CancellationToken ct)
    {
        using var document = PdfDocument.Open(path);

        var pages = new List<List<string>>(document.NumberOfPages);
        foreach (var page in document.GetPages())
        {
            ct.ThrowIfCancellationRequested();
            pages.Add(ParagraphsOf(page));
        }

        DropRepeatedMargins(pages);

        var chapterPages = ChapterPages(document, pages.Count);
        var startsChapter = chapterPages.Select(c => c.Page).ToHashSet();

        // ---- The running text ----

        var builder = new StringBuilder();
        var paragraphs = new List<(int Start, int End)>();
        var pageStarts = new int[pages.Count];

        for (var p = 0; p < pages.Count; p++)
        {
            pageStarts[p] = builder.Length == 0 ? 0 : builder.Length + 1;

            for (var i = 0; i < pages[p].Count; i++)
            {
                var paragraph = pages[p][i];

                if (i == 0 && paragraphs.Count > 0 && !startsChapter.Contains(p) && Continues(builder, paragraph))
                {
                    AppendJoined(builder, paragraph);
                    paragraphs[^1] = (paragraphs[^1].Start, builder.Length);
                    pageStarts[p] = builder.Length;
                    continue;
                }

                if (builder.Length > 0) builder.Append('\n');

                var start = builder.Length;
                builder.Append(paragraph);
                paragraphs.Add((start, builder.Length));
            }
        }

        var plainText = builder.ToString();
        var title = FirstNonEmpty(document.Information.Title, Path.GetFileNameWithoutExtension(path))!;

        // ---- Chapters, and one reader document for each ----

        var marks = chapterPages
            .Select(c => (c.Title, Offset: Math.Min(pageStarts[c.Page], plainText.Length)))
            .ToList();

        // Two outline entries on one page, or a page with no text left, would make an empty chapter.
        marks = marks.Where((m, i) => i == 0 || m.Offset > marks[i - 1].Offset).ToList();

        var chapters = marks.Select((mark, i) => new Chapter
        {
            Index = i,
            Title = mark.Title,
            TextStart = i == 0 ? 0 : mark.Offset,
            TextEnd = i + 1 < marks.Count ? marks[i + 1].Offset : plainText.Length,
        }).ToList();

        if (chapters.Count == 0)
            chapters.Add(new Chapter { Index = 0, Title = title, TextStart = 0, TextEnd = plainText.Length });

        var sentences = new List<Sentence>();
        for (var i = 0; i < chapters.Count; i++)
        {
            foreach (var (start, end) in SentenceSplitter.Split(plainText, chapters[i].TextStart!.Value, chapters[i].TextEnd!.Value))
                sentences.Add(new Sentence(sentences.Count, start, end, i));
        }

        var spine = chapters.Select((chapter, i) => new SpineDocument
        {
            Index = i,
            Href = $"pdf-{i}",
            Title = chapter.Title,
            TextStart = chapter.TextStart!.Value,
            TextEnd = chapter.TextEnd!.Value,
            Html = RenderHtml(plainText, paragraphs, sentences, chapter.TextStart!.Value, chapter.TextEnd!.Value),
        }).ToList();

        var text = new BookText
        {
            PlainText = plainText,
            Sentences = sentences,
            Spine = spine,
            Title = title,
            Author = FirstNonEmpty(document.Information.Author),
        };

        return new ExtractedBook(text, chapters, CoverOf(document));
    }

    // ---- Lines and paragraphs on one page ----

    private sealed record Line(string Text, double Left, double Right, double Bottom, double Height);

    /// <summary>The page's words gathered into lines, top to bottom.</summary>
    private static List<Line> LinesOf(Page page)
    {
        var words = page.GetWords()
            .Where(w => !string.IsNullOrWhiteSpace(w.Text))
            .OrderByDescending(w => w.BoundingBox.Bottom)
            .ToList();

        var lines = new List<List<Word>>();

        foreach (var word in words)
        {
            var height = Math.Max(word.BoundingBox.Height, 1);

            // Same line when the baselines agree to within half a letter: superscripts and a
            // slightly lifted glyph stay with their line instead of making one of their own.
            if (lines.Count > 0 && Math.Abs(lines[^1][0].BoundingBox.Bottom - word.BoundingBox.Bottom) < height * 0.5)
                lines[^1].Add(word);
            else
                lines.Add([word]);
        }

        return
        [
            .. lines.Select(l =>
            {
                var ordered = l.OrderBy(w => w.BoundingBox.Left).ToList();
                return new Line(
                    string.Join(' ', ordered.Select(w => w.Text)),
                    ordered[0].BoundingBox.Left,
                    ordered[^1].BoundingBox.Right,
                    ordered[0].BoundingBox.Bottom,
                    ordered.Max(w => w.BoundingBox.Height));
            }),
        ];
    }

    internal static List<string> ParagraphsOf(Page page)
    {
        List<Line> lines;

        try
        {
            lines = LinesOf(page);
        }
        catch (Exception)
        {
            // A page this cannot take apart still has its text, just without paragraphs.
            return string.IsNullOrWhiteSpace(page.Text) ? [] : [Collapse(page.Text)];
        }

        if (lines.Count == 0) return [];

        var gaps = lines.Zip(lines.Skip(1), (a, b) => a.Bottom - b.Bottom).Where(g => g > 0).OrderBy(g => g).ToList();
        var usualGap = gaps.Count > 0 ? gaps[gaps.Count / 2] : 0;

        var margin = lines.Min(l => l.Left);
        var rightEdge = lines.Max(l => l.Right);

        var paragraphs = new List<string>();
        var current = new StringBuilder();

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];

            if (current.Length > 0)
            {
                var previous = lines[i - 1];
                var gap = previous.Bottom - line.Bottom;

                var widerGap = usualGap > 0 && gap > usualGap * 1.4;
                var indented = line.Left > margin + line.Height * 0.8;
                var previousEndedShort = previous.Right < rightEdge - line.Height * 3 && EndsSentence(previous.Text);

                if (widerGap || indented || previousEndedShort)
                {
                    paragraphs.Add(current.ToString());
                    current.Clear();
                }
            }

            if (current.Length == 0) current.Append(Collapse(line.Text));
            else AppendJoined(current, Collapse(line.Text));
        }

        if (current.Length > 0) paragraphs.Add(current.ToString());

        return [.. paragraphs.Where(p => !IsPageNumber(p))];
    }

    /// <summary>
    /// Leaves out what is printed on every page but is not the book: the running header with the
    /// title, the footer with the author. Found as the same first or last line recurring on a good
    /// share of the pages, compared without its digits, since the page number usually rides along.
    /// </summary>
    private static void DropRepeatedMargins(List<List<string>> pages)
    {
        if (pages.Count < 6) return;

        var counts = new Dictionary<string, int>();

        // Only pages with something else on them: a page holding a single short paragraph is text,
        // whatever it looks like.
        foreach (var page in pages.Where(p => p.Count >= 2))
        {
            foreach (var candidate in page.Take(1).Concat(page.TakeLast(1)).Where(p => p.Length <= 80).Select(MarginKey).Distinct())
                counts[candidate] = counts.GetValueOrDefault(candidate) + 1;
        }

        var repeated = counts.Where(c => c.Key.Length > 0 && c.Value >= pages.Count * 0.3).Select(c => c.Key).ToHashSet();
        if (repeated.Count == 0) return;

        foreach (var page in pages.Where(p => p.Count >= 2))
        {
            if (page.Count > 0 && page[0].Length <= 80 && repeated.Contains(MarginKey(page[0]))) page.RemoveAt(0);
            if (page.Count > 0 && page[^1].Length <= 80 && repeated.Contains(MarginKey(page[^1]))) page.RemoveAt(page.Count - 1);
        }
    }

    private static string MarginKey(string text) =>
        new string([.. text.Where(c => !char.IsDigit(c))]).Trim().ToLowerInvariant();

    private static bool IsPageNumber(string text)
    {
        var trimmed = text.Trim().Trim('-', '–', '—', ' ', '.', '|').Trim();
        return trimmed.Length > 0 && trimmed.Length <= 8 &&
               (trimmed.All(char.IsDigit) || trimmed.All(c => "ivxlcdmIVXLCDM".Contains(c)));
    }

    // ---- Joining text back up ----

    /// <summary>
    /// Whether a page's first paragraph is really the previous one carrying on: it did not end a
    /// sentence and this one does not start one.
    /// </summary>
    private static bool Continues(StringBuilder text, string next) =>
        text.Length > 0 && next.Length > 0 && !EndsSentence(text[^1].ToString()) && !char.IsUpper(next[0]);

    private static bool EndsSentence(string text)
    {
        var trimmed = text.TrimEnd();
        return trimmed.Length > 0 && ".!?:…\"”’»«".Contains(trimmed[^1]);
    }

    /// <summary>Adds a line to the one before it, undoing a hyphen that only split a word to fit.</summary>
    private static void AppendJoined(StringBuilder text, string next)
    {
        if (text.Length >= 2 && text[^1] == '-' && char.IsLetter(text[^2]) && next.Length > 0 && char.IsLower(next[0]))
        {
            text.Length--;
            text.Append(next);
        }
        else
        {
            text.Append(' ').Append(next);
        }
    }

    private static string Collapse(string text)
    {
        var builder = new StringBuilder(text.Length);
        var space = false;

        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                space = builder.Length > 0;
                continue;
            }

            if (space) builder.Append(' ');
            space = false;
            builder.Append(c);
        }

        return builder.ToString();
    }

    // ---- Chapters ----

    /// <summary>
    /// Where the chapters begin, from the file's own outline when it has one, and otherwise every
    /// <see cref="PagesPerSection"/> pages — a book of several hundred pages as a single chapter
    /// would leave nothing to navigate by.
    /// </summary>
    private static List<(string Title, int Page)> ChapterPages(PdfDocument document, int pageCount)
    {
        var fromOutline = new List<(string, int)>();

        try
        {
            if (document.TryGetBookmarks(out var bookmarks, false))
            {
                var nodes = bookmarks.GetNodes()
                    .OfType<DocumentBookmarkNode>()
                    .Where(n => n.PageNumber >= 1 && n.PageNumber <= pageCount)
                    .ToList();

                // The shallowest level that actually divides the book: a lone top-level entry for
                // the whole title says nothing, so its children are used instead.
                var levels = nodes.Select(n => n.Level).Distinct().Order().ToList();
                var level = levels.FirstOrDefault(l => nodes.Where(n => n.Level <= l).Select(n => n.PageNumber).Distinct().Count() >= 3,
                                                  levels.Count > 0 ? levels[^1] : 0);

                fromOutline =
                [
                    .. nodes
                        .Where(n => n.Level <= level)
                        .OrderBy(n => n.PageNumber)
                        .DistinctBy(n => n.PageNumber)
                        .Select(n => (Collapse(n.Title ?? ""), n.PageNumber - 1)),
                ];
            }
        }
        catch (Exception)
        {
            // A broken outline is not a reason to refuse the book.
            fromOutline = [];
        }

        if (fromOutline.Count > 0)
        {
            // Anything before the first entry belongs to the first chapter rather than to nothing.
            if (fromOutline[0].Item2 > 0) fromOutline[0] = (fromOutline[0].Item1, 0);

            return
            [
                .. fromOutline.Select((c, i) => (
                    string.IsNullOrWhiteSpace(c.Item1) ? string.Format(CoreStrings.Chapter_Numbered, i + 1) : c.Item1,
                    c.Item2)),
            ];
        }

        return
        [
            .. Enumerable.Range(0, (pageCount + PagesPerSection - 1) / PagesPerSection)
                .Select(i => (string.Format(CoreStrings.Section_Numbered, i + 1), i * PagesPerSection)),
        ];
    }

    // ---- Rendering ----

    private static string RenderHtml(
        string plainText,
        List<(int Start, int End)> paragraphs,
        IReadOnlyList<Sentence> sentences,
        int from,
        int to)
    {
        var html = new StringBuilder();

        foreach (var (paragraphStart, paragraphEnd) in paragraphs)
        {
            var start = Math.Max(paragraphStart, from);
            var end = Math.Min(paragraphEnd, to);
            if (start >= end) continue;

            html.Append("<p>");

            foreach (var (sentenceIndex, pieceStart, pieceEnd) in HtmlTextPipeline.SplitBySentences(start, end, sentences))
            {
                var content = HtmlTextPipeline.Escape(plainText[pieceStart..pieceEnd]);

                if (sentenceIndex is null) html.Append(content);
                else html.Append("<span data-idx=\"").Append(sentenceIndex.Value).Append("\">").Append(content).Append("</span>");
            }

            html.Append("</p>");
        }

        return html.ToString();
    }

    // ---- Cover ----

    /// <summary>The largest picture on the first page, which for a book is its cover.</summary>
    private static byte[]? CoverOf(PdfDocument document)
    {
        try
        {
            if (document.NumberOfPages == 0) return null;

            var image = document.GetPage(1).GetImages()
                .OrderByDescending(i => i.Bounds.Width * i.Bounds.Height)
                .FirstOrDefault();

            if (image is null) return null;
            if (image.TryGetPng(out var png)) return png;

            // A JPEG is stored as a JPEG, and can be kept as it is.
            var raw = image.RawMemory.Span;
            return raw.Length > 2 && raw[0] == 0xFF && raw[1] == 0xD8 ? raw.ToArray() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
}
