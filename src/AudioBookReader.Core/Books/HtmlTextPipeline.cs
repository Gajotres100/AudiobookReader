using System.Globalization;
using System.Text;
using HtmlAgilityPack;

namespace AudioBookReader.Core.Books;

/// <summary>
/// Builds the book's plain text across documents, applying HTML whitespace rules: runs of
/// whitespace collapse to one space, and block elements become newlines.
///
/// The newline convention matters — <see cref="SentenceSplitter"/> treats every newline as a hard
/// paragraph break, which is what lets headings and unpunctuated dialogue lines split correctly.
/// </summary>
internal sealed class TextAccumulator
{
    private readonly StringBuilder _text = new();
    private bool _pendingSpace;

    public int Length => _text.Length;

    public string Build() => _text.ToString();

    public void BlockBreak()
    {
        _pendingSpace = false;
        while (_text.Length > 0 && _text[^1] == ' ') _text.Length--;
        if (_text.Length > 0 && _text[^1] != '\n') _text.Append('\n');
    }

    /// <summary>
    /// Appends one HTML text node, returning the range it occupies in the accumulated text, or
    /// null if it was pure whitespace and contributed nothing worth highlighting.
    /// </summary>
    public (int Start, int End)? Append(string raw)
    {
        var text = HtmlEntity.DeEntitize(raw) ?? raw;
        var start = -1;

        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                if (_text.Length > 0 && _text[^1] != '\n') _pendingSpace = true;
                continue;
            }

            if (_pendingSpace)
            {
                // The separating space must fall inside some node's range, or nothing would emit
                // it when the document is rendered back and "was <em>bitterly</em>" would come
                // out as "wasbitterly". It belongs to whichever node resumes the text.
                if (start < 0) start = _text.Length;
                _text.Append(' ');
                _pendingSpace = false;
            }

            if (start < 0) start = _text.Length;
            _text.Append(c);
        }

        return start < 0 ? null : (start, _text.Length);
    }
}

/// <summary>One parsed document, holding everything needed to render it back with sentence spans.</summary>
internal sealed class ParsedHtmlDocument
{
    public required HtmlDocument Document { get; init; }
    public required HtmlNode Root { get; init; }

    /// <summary>Text nodes in document order with the range each occupies in the book text.</summary>
    public required List<(HtmlTextNode Node, int Start, int End)> TextNodes { get; init; }

    /// <summary>Offsets of elements carrying an <c>id</c>, so a table-of-contents link with a
    /// fragment resolves to the exact position rather than the start of the file.</summary>
    public required Dictionary<string, int> AnchorOffsets { get; init; }

    public required int TextStart { get; init; }
    public required int TextEnd { get; init; }
}

/// <summary>
/// Turns a book's HTML into one plain-text stream and back into HTML with every sentence wrapped
/// in <c>&lt;span data-idx="N"&gt;</c>.
///
/// The two halves are one component because they must agree exactly: the offsets alignment works
/// in have to name the same characters the reader can highlight. Deriving both from a single walk
/// of the same DOM is what guarantees that, instead of matching text back onto the page afterwards
/// and hoping.
/// </summary>
internal static class HtmlTextPipeline
{
    private static readonly HashSet<string> BlockElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "address", "article", "aside", "blockquote", "br", "dd", "div", "dl", "dt", "figcaption",
        "figure", "footer", "h1", "h2", "h3", "h4", "h5", "h6", "header", "hr", "li", "main",
        "nav", "ol", "p", "pre", "section", "table", "tbody", "td", "tfoot", "th", "thead", "tr", "ul",
    };

    private static readonly HashSet<string> SkippedElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "noscript", "head", "svg", "template",
    };

    /// <summary>Reads a document's text into <paramref name="text"/> without modifying the DOM yet.</summary>
    public static ParsedHtmlDocument Parse(string html, TextAccumulator text)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html ?? "");

        var root = document.DocumentNode.SelectSingleNode("//body") ?? document.DocumentNode;

        var start = text.Length;
        var nodes = new List<(HtmlTextNode, int, int)>();
        var anchors = new Dictionary<string, int>(StringComparer.Ordinal);

        Walk(root, text, nodes, anchors);

        // Keep documents from running together into one sentence.
        text.BlockBreak();

        return new ParsedHtmlDocument
        {
            Document = document,
            Root = root,
            TextNodes = nodes,
            AnchorOffsets = anchors,
            TextStart = start,
            TextEnd = text.Length,
        };
    }

    private static void Walk(
        HtmlNode node,
        TextAccumulator text,
        List<(HtmlTextNode, int, int)> nodes,
        Dictionary<string, int> anchors)
    {
        foreach (var child in node.ChildNodes)
        {
            switch (child.NodeType)
            {
                case HtmlNodeType.Text:
                    if (child is HtmlTextNode textNode && text.Append(textNode.Text) is var range && range is not null)
                        nodes.Add((textNode, range.Value.Start, range.Value.End));
                    break;

                case HtmlNodeType.Element:
                    if (SkippedElements.Contains(child.Name)) break;

                    var isBlock = BlockElements.Contains(child.Name);
                    if (isBlock) text.BlockBreak();

                    // Recorded after the break so the anchor lands on its own content, not on the
                    // trailing whitespace of whatever came before it.
                    var id = child.GetAttributeValue("id", null);
                    if (!string.IsNullOrEmpty(id)) anchors[id] = text.Length;

                    Walk(child, text, nodes, anchors);

                    if (isBlock) text.BlockBreak();
                    break;
            }
        }
    }

    /// <summary>
    /// Rewrites the parsed document, wrapping each sentence in a span. A sentence crossing inline
    /// markup produces several spans sharing one index, so the reader highlights by attribute
    /// rather than assuming one element per sentence.
    ///
    /// The first span of each sentence also carries an id. The reader does not use it — it selects
    /// on the attribute — but EPUB 3 Media Overlays can only point at an element, and a sentence
    /// split across three spans by an italic needs one agreed place to point at. Sentence indices
    /// are unique across the whole book, so the id is too.
    /// </summary>
    public static string Render(ParsedHtmlDocument parsed, string plainText, IReadOnlyList<Sentence> sentences)
    {
        // Nodes come in document order and pieces left to right within each, so a sentence's first
        // span is simply the first one seen for that index. One counter is enough; no second pass.
        var stamped = -1;

        foreach (var (node, start, end) in parsed.TextNodes)
        {
            var parent = node.ParentNode;
            if (parent is null) continue;

            foreach (var (sentenceIndex, pieceStart, pieceEnd) in SplitBySentences(start, end, sentences))
            {
                var content = plainText[pieceStart..pieceEnd];

                HtmlNode piece;

                if (sentenceIndex is not { } index)
                {
                    piece = TextNode(parsed.Document, content);
                }
                else
                {
                    piece = SpanNode(parsed.Document, index, content, first: index > stamped);
                    stamped = Math.Max(stamped, index);
                }

                parent.InsertBefore(piece, node);
            }

            parent.RemoveChild(node);
        }

        return parsed.Root.InnerHtml;
    }

    /// <summary>The id given to the first span of a sentence, and the one a Media Overlay points at.</summary>
    public static string SentenceId(int sentenceIndex) =>
        "s" + sentenceIndex.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Cuts the range <paramref name="start"/>..<paramref name="end"/> into pieces that each lie
    /// wholly inside one sentence or wholly outside every sentence. The gaps matter: the
    /// whitespace between sentences belongs to no sentence and must still be emitted, or the text
    /// would render with words run together.
    /// </summary>
    public static IEnumerable<(int? SentenceIndex, int Start, int End)> SplitBySentences(
        int start, int end, IReadOnlyList<Sentence> sentences)
    {
        var position = start;
        var index = FirstSentenceEndingAfter(sentences, position);

        while (position < end)
        {
            if (index >= sentences.Count || sentences[index].Start >= end)
            {
                yield return (null, position, end);
                yield break;
            }

            if (position < sentences[index].Start)
            {
                var gapEnd = Math.Min(end, sentences[index].Start);
                yield return (null, position, gapEnd);
                position = gapEnd;
                continue;
            }

            var pieceEnd = Math.Min(end, sentences[index].End);
            yield return (sentences[index].Index, position, pieceEnd);

            if (pieceEnd == sentences[index].End) index++;
            position = pieceEnd;
        }
    }

    private static int FirstSentenceEndingAfter(IReadOnlyList<Sentence> sentences, int position)
    {
        int lo = 0, hi = sentences.Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (sentences[mid].End > position) hi = mid;
            else lo = mid + 1;
        }
        return lo;
    }

    private static HtmlTextNode TextNode(HtmlDocument document, string content) =>
        document.CreateTextNode(Escape(content));

    private static HtmlNode SpanNode(HtmlDocument document, int sentenceIndex, string content, bool first)
    {
        var span = document.CreateElement("span");
        span.SetAttributeValue("data-idx", sentenceIndex.ToString(CultureInfo.InvariantCulture));

        if (first) span.SetAttributeValue("id", SentenceId(sentenceIndex));

        span.AppendChild(TextNode(document, content));
        return span;
    }

    /// <summary>Escapes text content. Only the three characters that can break out of it need it.</summary>
    public static string Escape(string content) =>
        content.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
