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

    /// <summary>
    /// Reads the file as text, refusing to guess quietly.
    ///
    /// The default is UTF-8 with a replacement fallback, which never fails and is exactly the
    /// problem: a Croatian text file saved in Windows-1250 has every c, c, z, s and d turned into a
    /// replacement character, the import reports success because the text is not empty, and
    /// alignment then cannot match a single word carrying one. Decoding strictly and falling back
    /// to the legacy code page turns a book that looked imported into a book that reads.
    /// </summary>
    private static async Task<string> ReadTextAsync(string path, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(path, ct);

        // A byte-order mark settles it outright, whatever the encoding is.
        if (HasByteOrderMark(bytes)) return DecodeWithDetectedMark(bytes);

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // Not UTF-8. Central European is the likeliest alternative for the languages this app
            // is used in, and it decodes every byte, so this cannot fail in turn.
            return LegacyEncoding().GetString(bytes);
        }
    }

    private static bool HasByteOrderMark(byte[] bytes) =>
        bytes.Length >= 2 &&
        ((bytes[0] == 0xFF && bytes[1] == 0xFE) ||
         (bytes[0] == 0xFE && bytes[1] == 0xFF) ||
         (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF));

    private static string DecodeWithDetectedMark(byte[] bytes)
    {
        if (bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
    }

    /// <summary>Windows-1250 where the platform has it, Latin-1 where it does not.</summary>
    private static Encoding LegacyEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1250);
        }
        catch (Exception)
        {
            return Encoding.Latin1;
        }
    }

    public async Task<ExtractedBook> ExtractAsync(string path, CancellationToken ct = default)
    {
        var raw = await ReadTextAsync(path, ct);
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
