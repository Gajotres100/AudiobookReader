using System.Text;
using AudioBookReader.Core.Models;

namespace AudioBookReader.Core.Books;

/// <summary>
/// Reads a .txt file. Mostly useful for exercising alignment against a book whose text is beyond
/// question, before blaming the aligner for what is really an ebook parsing problem.
/// </summary>
public class PlainTextExtractor : IBookTextExtractor
{
    public bool CanHandle(string path) =>
        Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase);

    public async Task<ExtractedBook> ExtractAsync(string path, CancellationToken ct = default)
    {
        var raw = await File.ReadAllTextAsync(path, ct);
        var (plainText, paragraphs) = Normalize(raw);

        var sentences = SentenceSplitter.Split(plainText)
            .Select((range, i) => new Sentence(i, range.Start, range.End, 0))
            .ToList();

        var text = new BookText
        {
            PlainText = plainText,
            Sentences = sentences,
            Spine =
            [
                new SpineDocument
                {
                    Index = 0,
                    Href = Path.GetFileName(path),
                    TextStart = 0,
                    TextEnd = plainText.Length,
                    Html = RenderHtml(plainText, paragraphs, sentences),
                }
            ],
            Title = Path.GetFileNameWithoutExtension(path),
        };

        return new ExtractedBook(
            text,
            [new Chapter { Index = 0, Title = text.Title!, TextStart = 0, TextEnd = plainText.Length }]);
    }

    /// <summary>
    /// Collapses the file into the same convention the HTML pipeline produces: one newline per
    /// paragraph, no other whitespace runs.
    ///
    /// Plain text wraps lines for the terminal, not for meaning, so a single newline inside a
    /// paragraph becomes a space; only a blank line separates paragraphs. Without this every
    /// wrapped line would read as its own paragraph and split sentences apart.
    /// </summary>
    private static (string PlainText, List<(int Start, int End)> Paragraphs) Normalize(string raw)
    {
        var blocks = raw.Replace("\r\n", "\n").Replace('\r', '\n').Split("\n\n");

        var builder = new StringBuilder();
        var paragraphs = new List<(int, int)>();

        foreach (var block in blocks)
        {
            var collapsed = CollapseWhitespace(block);
            if (collapsed.Length == 0) continue;

            if (builder.Length > 0) builder.Append('\n');

            var start = builder.Length;
            builder.Append(collapsed);
            paragraphs.Add((start, builder.Length));
        }

        return (builder.ToString(), paragraphs);
    }

    private static string CollapseWhitespace(string block)
    {
        var builder = new StringBuilder(block.Length);
        var pendingSpace = false;

        foreach (var c in block)
        {
            if (char.IsWhiteSpace(c))
            {
                if (builder.Length > 0) pendingSpace = true;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    private static string RenderHtml(
        string plainText,
        List<(int Start, int End)> paragraphs,
        IReadOnlyList<Sentence> sentences)
    {
        var html = new StringBuilder();

        foreach (var (start, end) in paragraphs)
        {
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
}
