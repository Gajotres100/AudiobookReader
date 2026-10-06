using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using AudioBookReader.Core.Books;
using AudioBookReader.Core.Models;
using HtmlAgilityPack;

namespace Epub3Maker;

/// <summary>What was written, for the summary at the end.</summary>
public record WriteReport(int Sentences, int TimedSentences, int Documents, int DocumentsWithOverlay, long Bytes);

/// <summary>
/// Turns the original EPUB into an EPUB 3 with Media Overlays, keeping everything it already had.
///
/// The book is not rebuilt from extracted text: every stylesheet, picture and font in the original
/// stays, and each content document is only rewritten with its sentences wrapped in spans — the same
/// spans the app's reader highlights, whose first piece carries an id a Media Overlay can point at.
/// Next to each document goes a SMIL file listing, sentence by sentence, the stretch of the audio
/// that speaks it; the audio itself goes into the package unchanged, stored rather than compressed.
/// </summary>
public static partial class Epub3Writer
{
    private static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace Smil = "http://www.w3.org/ns/SMIL";
    private static readonly XNamespace Ops = "http://www.idpf.org/2007/ops";
    private static readonly XNamespace Xhtml = "http://www.w3.org/1999/xhtml";

    /// <summary>The class a reading system puts on the sentence being spoken.</summary>
    private const string ActiveClass = "-epub-media-overlay-active";

    private const string ActiveStyle =
        "." + ActiveClass + " { background-color: rgba(255, 196, 0, 0.35); border-radius: 3px; }";

    public static WriteReport Write(
        string epubPath,
        ExtractedBook book,
        IReadOnlyDictionary<int, SentenceTime> times,
        string audioPath,
        long audioDurationMs,
        string outputPath,
        Action<string> warn)
    {
        using var source = ZipFile.OpenRead(epubPath);

        var opfPath = FindPackage(source);
        var opfDirectory = DirectoryOf(opfPath);
        var opf = XDocument.Load(source.GetEntry(opfPath)!.Open(), LoadOptions.PreserveWhitespace);

        var entries = source.Entries.Select(e => e.FullName).ToHashSet(StringComparer.Ordinal);

        var audioExtension = Path.GetExtension(audioPath).ToLowerInvariant();
        var audioEntry = Combine(opfDirectory, "audio/book" + audioExtension);

        var replaced = new Dictionary<string, string>(StringComparer.Ordinal);
        var overlays = new List<(string Document, string SmilEntry, string SmilId, long DurationMs)>();

        var text = book.Text;
        var timed = 0;

        for (var i = 0; i < text.Spine.Count; i++)
        {
            var document = text.Spine[i];

            if (Resolve(document.Href, opfDirectory, entries) is not { } entry)
            {
                warn($"dokument '{document.Href}' nije pronađen u EPUB-u, preskačem");
                continue;
            }

            using (var reader = new StreamReader(source.GetEntry(entry)!.Open()))
            {
                var (xhtml, problem) = Rebuild(reader.ReadToEnd(), document.Html);
                if (xhtml is null)
                {
                    warn($"'{entry}' se ne da zapisati kao ispravan XHTML ({problem}), ostaje bez sinkronizacije");
                    continue;
                }

                replaced[entry] = xhtml;
            }

            var clips = ClipsFor(text, i, times, audioDurationMs);
            if (clips.Count == 0) continue;

            timed += clips.Count;

            var smilEntry = Path.ChangeExtension(entry, ".smil");
            var smilId = "mo-" + i.ToString(CultureInfo.InvariantCulture);

            replaced[smilEntry] = BuildSmil(entry, smilEntry, audioEntry, clips);
            overlays.Add((entry, smilEntry, smilId, clips.Sum(c => c.EndMs - c.BeginMs)));
        }

        var navEntry = UpdatePackage(opf, opfDirectory, overlays, audioEntry, AudioMediaType(audioExtension), audioDurationMs);

        if (navEntry is not null)
            replaced[navEntry] = BuildNav(book, navEntry, replaced.Keys.ToHashSet(), opfDirectory, entries);

        // ---- The package ----

        var temporary = outputPath + ".part";

        using (var output = ZipFile.Open(temporary, ZipArchiveMode.Create))
        {
            // First and uncompressed, as the format requires, so a reader can tell what this is
            // from its first bytes.
            using (var mime = new StreamWriter(output.CreateEntry("mimetype", CompressionLevel.NoCompression).Open(), new UTF8Encoding(false)))
                mime.Write("application/epub+zip");

            foreach (var entry in source.Entries)
            {
                if (entry.FullName is "mimetype" || entry.FullName == opfPath || replaced.ContainsKey(entry.FullName)) continue;
                if (entry.FullName.EndsWith('/')) continue;

                using var from = entry.Open();
                using var to = output.CreateEntry(entry.FullName, CompressionLevel.Optimal).Open();
                from.CopyTo(to);
            }

            foreach (var (name, content) in replaced)
                WriteText(output, name, content);

            using (var to = output.CreateEntry(opfPath, CompressionLevel.Optimal).Open())
            using (var writer = XmlWriter.Create(to, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false }))
                opf.Save(writer);

            // Stored: it is compressed audio already, and a reader seeks straight into it.
            using (var from = File.OpenRead(audioPath))
            using (var to = output.CreateEntry(audioEntry, CompressionLevel.NoCompression).Open())
                from.CopyTo(to);
        }

        File.Move(temporary, outputPath, overwrite: true);

        return new WriteReport(
            text.Sentences.Count, timed, text.Spine.Count, overlays.Count, new FileInfo(outputPath).Length);
    }

    // ---- Content documents ----

    /// <summary>
    /// The original document with its body replaced by the one carrying sentence spans, written as
    /// XHTML — or null when the result would not parse as XML, since a reading system refuses a
    /// broken document outright and the original is better than nothing.
    /// </summary>
    private static (string? Xhtml, string? Problem) Rebuild(string original, string body)
    {
        var document = new HtmlDocument
        {
            OptionOutputOriginalCase = true,
            OptionWriteEmptyNodes = true,
        };

        document.LoadHtml(original);

        var bodyNode = document.DocumentNode.SelectSingleNode("//body");
        if (bodyNode is null) return (null, "nema <body>");

        bodyNode.InnerHtml = body;

        if (document.DocumentNode.SelectSingleNode("//head") is { } head)
        {
            var style = document.CreateElement("style");
            style.SetAttributeValue("type", "text/css");
            style.AppendChild(document.CreateTextNode(ActiveStyle));
            head.AppendChild(style);
        }

        // HtmlAgilityPack writes void elements the HTML way, "<br>", which XML reads as an element
        // that never ends. They never have content, so closing each where it opens is exact.
        var xhtml = VoidElement().Replace(document.DocumentNode.OuterHtml, "<$1$2 />");

        try
        {
            using var reader = XmlReader.Create(new StringReader(xhtml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
            while (reader.Read()) { }
        }
        catch (XmlException ex)
        {
            return (null, ex.Message);
        }

        return (xhtml, null);
    }

    // ---- Timing ----

    private readonly record struct Clip(int Sentence, long BeginMs, long EndMs);

    /// <summary>
    /// The timed sentences of one document, in reading order, never overlapping the one before.
    /// Sentences with no time are left out, and a reading system simply does not highlight them.
    /// </summary>
    private static List<Clip> ClipsFor(BookText text, int spineIndex, IReadOnlyDictionary<int, SentenceTime> times, long durationMs)
    {
        var clips = new List<Clip>();
        var previousEnd = 0L;

        foreach (var sentence in text.Sentences)
        {
            if (sentence.SpineIndex != spineIndex || !times.TryGetValue(sentence.Index, out var time)) continue;

            var begin = Math.Clamp(Math.Max(time.BeginMs, previousEnd), 0, durationMs);
            var end = Math.Clamp(time.EndMs, 0, durationMs);
            if (end <= begin) continue;

            clips.Add(new Clip(sentence.Index, begin, end));
            previousEnd = end;
        }

        return clips;
    }

    /// <summary>
    /// Sentence times read off a sync map — the phone's, or the whisper aligner's — where the
    /// sentence starts and where the next one starts, as far as the map reaches.
    /// </summary>
    public static Dictionary<int, SentenceTime> TimesFromMap(BookText text, SyncMap map)
    {
        var times = new Dictionary<int, SentenceTime>();

        for (var i = 0; i < text.Sentences.Count; i++)
        {
            var sentence = text.Sentences[i];

            var chapter = map.Chapters.FirstOrDefault(c => c.CoversChar(sentence.Start) && c.CoversChar(Math.Max(sentence.Start, sentence.End - 1)));
            if (chapter is null || !chapter.TryGetAudioMs(sentence.Start, out var begin)) continue;

            var endChar = i + 1 < text.Sentences.Count && chapter.CoversChar(text.Sentences[i + 1].Start)
                ? text.Sentences[i + 1].Start
                : sentence.End;

            if (chapter.TryGetAudioMs(endChar, out var end) && end > begin)
                times[sentence.Index] = new SentenceTime(begin, end);
        }

        return times;
    }

    /// <summary>
    /// Sentence times with any chapter opening squeezed into the end of the chapter before put
    /// back — see <see cref="MediaOverlayPackage.RepairChapterOpenings"/>. Whisper hears a chapter's
    /// heading and first lines late, and the map then has them spoken in the last seconds of the
    /// previous chapter, so a reader jumping to the chapter plays the end of the one before.
    /// Sentences of a repaired opening are spread at the narrator's pace from the chapter's real
    /// start; every other time stays as it was.
    /// </summary>
    /// <param name="repaired">How many sentences were given a new time.</param>
    public static Dictionary<int, SentenceTime> RepairChapterOpenings(
        BookText text, Dictionary<int, SentenceTime> times,
        IEnumerable<int> textChapterStarts, IEnumerable<long> audioChapterStarts, out int repaired)
    {
        repaired = 0;

        var timed = text.Sentences.Where(s => times.ContainsKey(s.Index)).OrderBy(s => s.Start).ToList();
        var anchors = timed.Select(s => new Anchor(times[s.Index].BeginMs, s.Start, 1f)).ToList();

        var fixedAnchors = MediaOverlayPackage.RepairChapterOpenings(anchors, textChapterStarts, audioChapterStarts);
        if (fixedAnchors.SequenceEqual(anchors)) return times;

        var chars = fixedAnchors.Select(a => a.CharOffset).ToArray();

        long BeginAt(int offset)
        {
            var i = Array.BinarySearch(chars, offset);
            if (i >= 0) return fixedAnchors[i].AudioMs;

            i = ~i;
            if (i == 0) return fixedAnchors[0].AudioMs;
            if (i == chars.Length) return fixedAnchors[^1].AudioMs;

            var (a, b) = (fixedAnchors[i - 1], fixedAnchors[i]);
            return a.AudioMs + (long)((b.AudioMs - a.AudioMs) * (double)(offset - a.CharOffset) / (b.CharOffset - a.CharOffset));
        }

        var begins = timed.Select(s => BeginAt(s.Start)).ToArray();
        var result = new Dictionary<int, SentenceTime>(times.Count);

        for (var k = 0; k < timed.Count; k++)
        {
            var old = times[timed[k].Index];
            var moved = begins[k] != old.BeginMs;
            if (moved) repaired++;

            var end = old.EndMs;
            if (k + 1 < timed.Count)
            {
                var next = times[timed[k + 1].Index].BeginMs;

                // A sentence that ran up to the next one still does, wherever the next one went; one
                // that ended on its own last word keeps that end unless the next now starts sooner.
                end = moved || old.EndMs >= next ? begins[k + 1] : Math.Min(old.EndMs, begins[k + 1]);
            }

            if (end > begins[k]) result[timed[k].Index] = new SentenceTime(begins[k], end);
        }

        return result;
    }

    private static string BuildSmil(string documentEntry, string smilEntry, string audioEntry, List<Clip> clips)
    {
        var directory = DirectoryOf(smilEntry);
        var documentHref = Relative(directory, documentEntry);
        var audioHref = Relative(directory, audioEntry);

        var seq = new XElement(Smil + "seq",
            new XAttribute("id", "seq1"),
            new XAttribute(Ops + "textref", documentHref),
            clips.Select(c => new XElement(Smil + "par",
                new XAttribute("id", "p" + c.Sentence.ToString(CultureInfo.InvariantCulture)),
                new XElement(Smil + "text", new XAttribute("src", $"{documentHref}#s{c.Sentence.ToString(CultureInfo.InvariantCulture)}")),
                new XElement(Smil + "audio",
                    new XAttribute("src", audioHref),
                    new XAttribute("clipBegin", Clock(c.BeginMs)),
                    new XAttribute("clipEnd", Clock(c.EndMs))))));

        var smil = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement(Smil + "smil",
                new XAttribute(XNamespace.Xmlns + "epub", Ops),
                new XAttribute("version", "3.0"),
                new XElement(Smil + "body", seq)));

        return Serialize(smil);
    }

    // ---- The package document ----

    /// <summary>
    /// Makes the package an EPUB 3 that declares its overlays and its audio. Returns where a
    /// navigation document has to be written when the book, being EPUB 2, had none.
    /// </summary>
    private static string? UpdatePackage(
        XDocument opf,
        string opfDirectory,
        List<(string Document, string SmilEntry, string SmilId, long DurationMs)> overlays,
        string audioEntry,
        string audioMediaType,
        long audioDurationMs)
    {
        var package = opf.Root!;
        package.SetAttributeValue("version", "3.0");

        var metadata = package.Element(Opf + "metadata")!;
        var manifest = package.Element(Opf + "manifest")!;

        var items = manifest.Elements(Opf + "item").ToList();
        var ids = items.Select(i => (string?)i.Attribute("id")).OfType<string>().ToHashSet();

        string UniqueId(string wanted)
        {
            var id = wanted;
            for (var n = 2; ids.Contains(id); n++) id = $"{wanted}-{n}";
            ids.Add(id);
            return id;
        }

        foreach (var (document, smilEntry, smilId, _) in overlays)
        {
            var id = UniqueId(smilId);

            var item = items.FirstOrDefault(i =>
                Combine(opfDirectory, Uri.UnescapeDataString((string?)i.Attribute("href") ?? "")) == document);

            item?.SetAttributeValue("media-overlay", id);

            manifest.Add(new XElement(Opf + "item",
                new XAttribute("id", id),
                new XAttribute("href", Relative(opfDirectory, smilEntry)),
                new XAttribute("media-type", "application/smil+xml")));

            var overlayDuration = overlays.First(o => o.SmilEntry == smilEntry).DurationMs;
            metadata.Add(Meta("media:duration", Clock(overlayDuration), refines: "#" + id));
        }

        manifest.Add(new XElement(Opf + "item",
            new XAttribute("id", UniqueId("audio")),
            new XAttribute("href", Relative(opfDirectory, audioEntry)),
            new XAttribute("media-type", audioMediaType)));

        metadata.Add(Meta("media:duration", Clock(audioDurationMs)));
        metadata.Add(Meta("media:active-class", ActiveClass));

        if (!metadata.Elements(Opf + "meta").Any(m => (string?)m.Attribute("property") == "dcterms:modified"))
            metadata.Add(Meta("dcterms:modified", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)));

        var hasNav = items.Any(i => ((string?)i.Attribute("properties") ?? "").Split(' ').Contains("nav"));
        if (hasNav) return null;

        var navEntry = Combine(opfDirectory, "nav.xhtml");
        manifest.Add(new XElement(Opf + "item",
            new XAttribute("id", UniqueId("nav")),
            new XAttribute("href", "nav.xhtml"),
            new XAttribute("media-type", "application/xhtml+xml"),
            new XAttribute("properties", "nav")));

        return navEntry;
    }

    private static XElement Meta(string property, string value, string? refines = null)
    {
        var meta = new XElement(Opf + "meta", new XAttribute("property", property), value);
        if (refines is not null) meta.SetAttributeValue("refines", refines);
        return meta;
    }

    /// <summary>
    /// The table of contents EPUB 3 requires, for a book that only had EPUB 2's. Built from the
    /// chapters the app found, each pointing at its first sentence.
    /// </summary>
    private static string BuildNav(
        ExtractedBook book, string navEntry, HashSet<string> rewritten, string opfDirectory, HashSet<string> entries)
    {
        var text = book.Text;
        var directory = DirectoryOf(navEntry);
        var links = new List<XElement>();

        foreach (var chapter in book.Chapters)
        {
            var start = chapter.TextStart ?? 0;
            if (text.SpineAt(start) is not { } document) continue;
            if (Resolve(document.Href, opfDirectory, entries) is not { } entry) continue;

            var href = Relative(directory, entry);
            var sentence = text.Sentences.FirstOrDefault(s => s.SpineIndex == document.Index && s.End > start);

            if (rewritten.Contains(entry) && sentence.End > 0)
                href += "#s" + sentence.Index.ToString(CultureInfo.InvariantCulture);

            links.Add(new XElement(Xhtml + "li", new XElement(Xhtml + "a", new XAttribute("href", href), chapter.Title)));
        }

        var title = text.Title ?? "Contents";

        var nav = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType("html", null, null, null),
            new XElement(Xhtml + "html",
                new XAttribute(XNamespace.Xmlns + "epub", Ops),
                new XElement(Xhtml + "head", new XElement(Xhtml + "title", title)),
                new XElement(Xhtml + "body",
                    new XElement(Xhtml + "nav",
                        new XAttribute(Ops + "type", "toc"),
                        new XAttribute("id", "toc"),
                        new XElement(Xhtml + "h1", title),
                        new XElement(Xhtml + "ol", links)))));

        return Serialize(nav);
    }

    // ---- Paths and small things ----

    private static string FindPackage(ZipArchive source)
    {
        var container = source.GetEntry("META-INF/container.xml")
            ?? throw new InvalidDataException("EPUB nema META-INF/container.xml.");

        var document = XDocument.Load(container.Open());
        var rootfile = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "rootfile");

        return (string?)rootfile?.Attribute("full-path")
            ?? throw new InvalidDataException("EPUB ne kaže gdje mu je paket (content.opf).");
    }

    /// <summary>Finds a document the extractor named, whether it gave the full path or one relative to the package.</summary>
    private static string? Resolve(string href, string opfDirectory, HashSet<string> entries)
    {
        var decoded = Uri.UnescapeDataString(href).Replace('\\', '/').TrimStart('/');

        if (entries.Contains(decoded)) return decoded;

        var underPackage = Combine(opfDirectory, decoded);
        if (entries.Contains(underPackage)) return underPackage;

        return entries.FirstOrDefault(e => e.EndsWith("/" + decoded, StringComparison.Ordinal));
    }

    private static string DirectoryOf(string entry)
    {
        var cut = entry.LastIndexOf('/');
        return cut < 0 ? "" : entry[..cut];
    }

    /// <summary>Joins zip paths and resolves "..", which zip entries never contain.</summary>
    private static string Combine(string directory, string relative)
    {
        var parts = new List<string>();

        foreach (var part in (directory.Length > 0 ? directory + "/" + relative : relative).Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == ".." && parts.Count > 0) parts.RemoveAt(parts.Count - 1);
            else parts.Add(part);
        }

        return string.Join('/', parts);
    }

    /// <summary>The href that leads from a directory in the package to an entry.</summary>
    private static string Relative(string fromDirectory, string toEntry)
    {
        var from = new Uri("http://epub/" + (fromDirectory.Length > 0 ? fromDirectory + "/" : ""));
        var to = new Uri("http://epub/" + toEntry);

        return from.MakeRelativeUri(to).ToString();
    }

    [System.Text.RegularExpressions.GeneratedRegex(
        @"<(area|base|br|col|embed|hr|img|input|link|meta|param|source|track|wbr)\b((?:[^>""'/]|/(?!>)|""[^""]*""|'[^']*')*)/?>",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex VoidElement();

    private static string Clock(long ms)
    {
        var time = TimeSpan.FromMilliseconds(ms);
        return string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}");
    }

    private static string AudioMediaType(string extension) => extension switch
    {
        ".mp3" => "audio/mpeg",
        ".ogg" or ".opus" => "audio/ogg",
        ".flac" => "audio/flac",
        _ => "audio/mp4",
    };

    private static string Serialize(XDocument document)
    {
        using var buffer = new MemoryStream();

        using (var writer = XmlWriter.Create(buffer, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true }))
            document.Save(writer);

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteText(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
