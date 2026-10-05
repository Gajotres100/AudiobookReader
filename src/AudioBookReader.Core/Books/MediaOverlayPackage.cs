using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using AudioBookReader.Core.Data;
using AudioBookReader.Core.Models;

namespace AudioBookReader.Core.Books;

/// <summary>
/// An EPUB 3 that carries its own narration and its own alignment — Media Overlays — taken apart
/// into what the library keeps: the audio as a file of its own, the book without it, and the
/// overlay's timings as a sync map.
///
/// Nothing is listened to. Every <c>&lt;par&gt;</c> in the overlay already says which element of
/// the text is spoken when, so it becomes an anchor directly, at full confidence, and the book opens
/// already following along.
///
/// The audio has to come out of the package rather than be played from inside it: the player wants
/// a file, and the ebook reader loads every file of a package into memory — which, for a package
/// with ten hours of narration inside it, is the difference between opening and crashing.
/// </summary>
public static class MediaOverlayPackage
{
    private static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";

    /// <summary>
    /// The audio files the book's overlays play, in the order they are first used — or null when
    /// the file is an ordinary EPUB with no overlays at all.
    /// </summary>
    public static IReadOnlyList<string>? AudioFiles(string epubPath)
    {
        try
        {
            using var file = File.OpenRead(epubPath);
            return AudioFiles(file);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>The same, from an open package — one still on a server, read a range at a time.</summary>
    public static IReadOnlyList<string>? AudioFiles(Stream epub)
    {
        try
        {
            using var zip = new ZipArchive(epub, ZipArchiveMode.Read, leaveOpen: true);
            var package = Package.Load(zip);

            var audio = new List<string>();

            foreach (var smil in package.OverlaysInReadingOrder())
            {
                foreach (var par in Pars(zip, smil))
                    if (par.AudioEntry is { } entry && !audio.Contains(entry)) audio.Add(entry);
            }

            return audio.Count > 0 ? audio : null;
        }
        catch (Exception e) when (e is InvalidDataException or System.Xml.XmlException or IOException)
        {
            // Not a package this can read; the ordinary extractor gets to say what is wrong with it.
            return null;
        }
    }

    /// <summary>
    /// Writes the overlay's audio to <paramref name="audioOut"/> and the rest of the book, without
    /// any audio in it, to <paramref name="bookOut"/>.
    /// </summary>
    public static async Task SplitAsync(string epubPath, string audioEntry, Stream audioOut, string bookOut, CancellationToken ct = default)
    {
        await using var file = File.OpenRead(epubPath);
        await SplitAsync(file, audioEntry, audioOut, bookOut, ct);
    }

    /// <summary>
    /// The same from an open package. With no <paramref name="audioOut"/> the audio is left where it
    /// is — the book is streamed, and only the text comes down: a package on a server is read a
    /// range at a time, so the hours of narration in it are never fetched at all.
    /// </summary>
    public static async Task SplitAsync(Stream epub, string audioEntry, Stream? audioOut, string bookOut, CancellationToken ct = default)
    {
        using var zip = new ZipArchive(epub, ZipArchiveMode.Read, leaveOpen: true);
        var package = Package.Load(zip);

        var entry = zip.GetEntry(audioEntry) ?? throw new InvalidDataException($"The package has no '{audioEntry}'.");

        if (audioOut is not null)
        {
            await using var source = entry.Open();
            await source.CopyToAsync(audioOut, 1 << 20, ct);
        }

        var audioEntries = package.AudioEntries();

        // The package document, less the audio it no longer holds.
        foreach (var item in package.Items.Where(i => audioEntries.Contains(package.EntryOf(i))).ToList())
            item.Remove();

        using var output = ZipFile.Open(bookOut, ZipArchiveMode.Create);

        using (var writer = new StreamWriter(output.CreateEntry("mimetype", CompressionLevel.NoCompression).Open()))
            await writer.WriteAsync("application/epub+zip");

        foreach (var file in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();

            if (file.FullName is "mimetype" || file.FullName.EndsWith('/') || audioEntries.Contains(file.FullName)) continue;

            await using var to = output.CreateEntry(file.FullName, CompressionLevel.Optimal).Open();

            if (file.FullName == package.Path)
            {
                package.Document.Save(to);
            }
            else
            {
                await using var from = file.Open();
                await from.CopyToAsync(to, ct);
            }
        }
    }

    /// <summary>
    /// Where the narration lies inside a package, when it can be played from there: one recording,
    /// stored rather than compressed, so that its bytes are a single unbroken run of the file. Null
    /// otherwise — several recordings, or one compressed — and the package has to be downloaded.
    /// </summary>
    public static async Task<StoredNarration?> FindStoredNarrationAsync(
        Stream epub, IReadOnlyList<string> audioFiles, CancellationToken ct = default)
    {
        if (audioFiles.Count != 1) return null;

        var layout = await ZipLayout.ReadAsync(epub, ct);
        var entry = layout.FirstOrDefault(e => e.Name == audioFiles[0]);
        if (entry is not { IsStored: true, Size: > 0 }) return null;

        var offset = await ZipLayout.DataOffsetAsync(epub, entry, ct);
        return new StoredNarration(entry.Name, offset, entry.Size);
    }

    /// <summary>
    /// Every overlay timing that points at <paramref name="audioEntry"/>, as an anchor into the
    /// book's text: the moment a sentence starts being spoken, and where that sentence starts.
    /// </summary>
    public static List<Anchor> ReadAnchors(string epubPath, BookText text, string audioEntry)
    {
        using var zip = ZipFile.OpenRead(epubPath);
        var package = Package.Load(zip);

        var entries = zip.Entries.Select(e => e.FullName).ToHashSet(StringComparer.Ordinal);

        // Which spine document each content file became.
        var documents = new Dictionary<string, SpineDocument>(StringComparer.Ordinal);
        foreach (var document in text.Spine)
            if (EpubPaths.Resolve(document.Href, package.Directory, entries) is { } entry) documents[entry] = document;

        var anchors = new List<Anchor>();

        foreach (var smil in package.OverlaysInReadingOrder())
        {
            foreach (var par in Pars(zip, smil))
            {
                if (par.AudioEntry != audioEntry || par.BeginMs is not { } begin) continue;
                if (par.TextEntry is null || !documents.TryGetValue(par.TextEntry, out var document)) continue;

                // An overlay without a fragment points at the whole document; its start is the best
                // there is.
                var offset = par.Fragment is { } fragment
                    ? document.Anchors.TryGetValue(fragment, out var at) ? at : (int?)null
                    : document.TextStart;

                if (offset is not { } charOffset) continue;

                // An inline element's offset is taken before the space that separates it from the
                // previous one; the sentence itself starts at its first letter.
                while (charOffset < text.PlainText.Length && char.IsWhiteSpace(text.PlainText[charOffset])) charOffset++;

                anchors.Add(new Anchor(begin, charOffset, 1f));
            }
        }

        return [.. anchors.OrderBy(a => a.AudioMs)];
    }

    /// <summary>
    /// The anchors laid out over the audio's chapters, as the map the library keeps and the text
    /// range each chapter turned out to cover.
    /// </summary>
    public static (SyncMap Map, List<ChapterRange> Ranges) BuildMap(
        IReadOnlyList<Anchor> anchors,
        IReadOnlyList<Chapter> chapters,
        int textLength,
        string? audioHash,
        string? ebookHash)
    {
        var map = new SyncMap { AudioHash = audioHash, EbookHash = ebookHash };
        var starts = new List<(int Index, int? Start)>();

        foreach (var chapter in chapters.OrderBy(c => c.Index))
        {
            var from = chapter.StartMs ?? 0;
            var to = chapter.EndMs ?? long.MaxValue;

            var inside = Monotone(anchors.Where(a => a.AudioMs >= from && a.AudioMs < to));
            if (inside.Count > 0) map.SetChapter(new ChapterSyncMap { ChapterIndex = chapter.Index, Anchors = inside });

            starts.Add((chapter.Index, inside.Count > 0 ? inside.Min(a => a.CharOffset) : null));
        }

        // Each chapter's text runs from its first anchor to where the next chapter's begins, so the
        // chapters tile the book without gaps: front matter belongs to the first, and a chapter
        // with no overlay at all is left for the next one to cover.
        var ranges = new List<ChapterRange>();
        for (var i = 0; i < starts.Count; i++)
        {
            if (starts[i].Start is not { } start)
            {
                ranges.Add(new ChapterRange(starts[i].Index, null, null));
                continue;
            }

            var next = starts.Skip(i + 1).Select(s => s.Start).FirstOrDefault(s => s is not null) ?? textLength;
            ranges.Add(new ChapterRange(starts[i].Index, ranges.All(r => r.TextStart is null) ? 0 : start, next));
        }

        return (map, ranges);
    }

    /// <summary>
    /// The anchors in time order, keeping only those that also move forward through the text.
    ///
    /// Not the map's own outlier filter, which weighs every anchor against every other: that is
    /// right for a few dozen guesses per chapter and far too slow for an overlay timing every
    /// sentence of a chapter-less ten-hour recording. An overlay is measured, not guessed; the only
    /// thing to guard against is a stray entry pointing backwards.
    /// </summary>
    private static List<Anchor> Monotone(IEnumerable<Anchor> anchors)
    {
        var kept = new List<Anchor>();

        foreach (var anchor in anchors.OrderBy(a => a.AudioMs).ThenBy(a => a.CharOffset))
        {
            if (kept.Count > 0 && (anchor.AudioMs <= kept[^1].AudioMs || anchor.CharOffset <= kept[^1].CharOffset)) continue;
            kept.Add(anchor);
        }

        return kept;
    }

    // ---- The overlay files ----

    private sealed record Par(string? TextEntry, string? Fragment, string? AudioEntry, long? BeginMs);

    private static IEnumerable<Par> Pars(ZipArchive zip, string smilEntry)
    {
        if (zip.GetEntry(smilEntry) is not { } entry) yield break;

        XDocument smil;
        using (var stream = entry.Open()) smil = XDocument.Load(stream);

        var directory = EpubPaths.DirectoryOf(smilEntry);

        foreach (var par in smil.Descendants().Where(e => e.Name.LocalName == "par"))
        {
            var textSrc = (string?)par.Elements().FirstOrDefault(e => e.Name.LocalName == "text")?.Attribute("src");
            var audio = par.Elements().FirstOrDefault(e => e.Name.LocalName == "audio");
            var audioSrc = (string?)audio?.Attribute("src");

            string? textEntry = null, fragment = null;
            if (textSrc is not null)
            {
                var hash = textSrc.IndexOf('#');
                textEntry = EpubPaths.Combine(directory, Uri.UnescapeDataString(hash < 0 ? textSrc : textSrc[..hash]));
                fragment = hash < 0 ? null : Uri.UnescapeDataString(textSrc[(hash + 1)..]);
            }

            var audioEntry = audioSrc is null ? null : EpubPaths.Combine(directory, Uri.UnescapeDataString(audioSrc));
            var begin = SmilClock.TryParse((string?)audio?.Attribute("clipBegin") ?? "0", out var ms) ? ms : (long?)null;

            yield return new Par(textEntry, fragment, audioEntry, begin);
        }
    }

    // ---- The package document ----

    private sealed class Package
    {
        public required string Path { get; init; }
        public required string Directory { get; init; }
        public required XDocument Document { get; init; }

        public List<XElement> Items => [.. Document.Root!.Element(Opf + "manifest")?.Elements(Opf + "item") ?? []];

        public string EntryOf(XElement item) =>
            EpubPaths.Combine(Directory, Uri.UnescapeDataString((string?)item.Attribute("href") ?? ""));

        public static Package Load(ZipArchive zip)
        {
            var container = zip.GetEntry("META-INF/container.xml") ?? throw new InvalidDataException("No container.xml.");

            XDocument containerXml;
            using (var stream = container.Open()) containerXml = XDocument.Load(stream);

            var path = (string?)containerXml.Descendants().FirstOrDefault(e => e.Name.LocalName == "rootfile")?.Attribute("full-path")
                       ?? throw new InvalidDataException("No package document.");

            var entry = zip.GetEntry(path) ?? throw new InvalidDataException($"No '{path}'.");

            XDocument document;
            using (var stream = entry.Open()) document = XDocument.Load(stream);

            return new Package { Path = path, Directory = EpubPaths.DirectoryOf(path), Document = document };
        }

        /// <summary>The overlays in the order their documents are read, then any the spine does not name.</summary>
        public List<string> OverlaysInReadingOrder()
        {
            var items = Items;
            var byId = items.Where(i => i.Attribute("id") is not null).ToDictionary(i => (string)i.Attribute("id")!);

            var overlays = new List<string>();

            foreach (var itemref in Document.Root!.Element(Opf + "spine")?.Elements(Opf + "itemref") ?? [])
            {
                if (!byId.TryGetValue((string?)itemref.Attribute("idref") ?? "", out var content)) continue;
                if (!byId.TryGetValue((string?)content.Attribute("media-overlay") ?? "", out var smil)) continue;

                var entry = EntryOf(smil);
                if (!overlays.Contains(entry)) overlays.Add(entry);
            }

            foreach (var smil in items.Where(i => (string?)i.Attribute("media-type") == "application/smil+xml"))
            {
                var entry = EntryOf(smil);
                if (!overlays.Contains(entry)) overlays.Add(entry);
            }

            return overlays;
        }

        public HashSet<string> AudioEntries() =>
        [
            .. Items
                .Where(i => ((string?)i.Attribute("media-type") ?? "").StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
                .Select(EntryOf),
        ];
    }
}

/// <summary>Paths inside an EPUB package, which are zip entry names joined by slashes.</summary>
/// <summary>The narration of a package, as a stretch of the package file itself.</summary>
public sealed record StoredNarration(string Entry, long Offset, long Length);

public static class EpubPaths
{
    public static string DirectoryOf(string entry)
    {
        var cut = entry.LastIndexOf('/');
        return cut < 0 ? "" : entry[..cut];
    }

    /// <summary>Joins a directory and a relative href, resolving "." and "..".</summary>
    public static string Combine(string directory, string relative)
    {
        var parts = new List<string>();

        foreach (var part in (directory.Length > 0 ? directory + "/" + relative : relative).Replace('\\', '/').Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == ".." && parts.Count > 0) parts.RemoveAt(parts.Count - 1);
            else parts.Add(part);
        }

        return string.Join('/', parts);
    }

    /// <summary>
    /// The entry an extractor's document path names, whether it gave the full path in the archive
    /// or one relative to the package document.
    /// </summary>
    public static string? Resolve(string href, string packageDirectory, IReadOnlySet<string> entries)
    {
        var decoded = Uri.UnescapeDataString(href).Replace('\\', '/').TrimStart('/');

        if (entries.Contains(decoded)) return decoded;

        var underPackage = Combine(packageDirectory, decoded);
        if (entries.Contains(underPackage)) return underPackage;

        return entries.FirstOrDefault(e => e.EndsWith("/" + decoded, StringComparison.Ordinal));
    }
}

/// <summary>SMIL clock values, as Media Overlays write them.</summary>
public static class SmilClock
{
    /// <summary>
    /// "1:02:03.5", "02:03.5", "12.5s", "2.5min", "1.5h", "345ms" or plain seconds, in milliseconds.
    /// </summary>
    public static bool TryParse(string value, out long ms)
    {
        ms = 0;
        var text = value.Trim();
        if (text.Length == 0) return false;

        if (text.Contains(':'))
        {
            var parts = text.Split(':');
            if (parts.Length is < 2 or > 3) return false;

            double total = 0;
            foreach (var part in parts)
            {
                if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return false;
                total = total * 60 + number;
            }

            ms = (long)Math.Round(total * 1000);
            return true;
        }

        var (digits, scale) =
            text.EndsWith("ms", StringComparison.Ordinal) ? (text[..^2], 1d) :
            text.EndsWith("min", StringComparison.Ordinal) ? (text[..^3], 60_000d) :
            text.EndsWith('h') ? (text[..^1], 3_600_000d) :
            text.EndsWith('s') ? (text[..^1], 1000d) :
            (text, 1000d);

        if (!double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount)) return false;

        ms = (long)Math.Round(amount * scale);
        return true;
    }
}
