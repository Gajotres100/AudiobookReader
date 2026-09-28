using AudioBookReader.Core.Models;

namespace AudioBookReader.Core.Books;

/// <summary>Picks the right extractor for an ebook file.</summary>
public class BookTextExtractors(IEnumerable<IBookTextExtractor>? extractors = null)
{
    /// <summary>
    /// Falls back to the built-in extractors when none are supplied — including when an *empty*
    /// collection is supplied.
    ///
    /// The empty case is not hypothetical: a dependency injection container asked for
    /// <c>IEnumerable&lt;T&gt;</c> hands back an empty collection rather than null when nothing is
    /// registered, so a null-only check leaves this object supporting no formats at all while
    /// looking perfectly constructed. An instance with nothing to extract with is never what the
    /// caller meant.
    /// </summary>
    private readonly List<IBookTextExtractor> _extractors =
        extractors?.ToList() is { Count: > 0 } supplied
            ? supplied
            : [new EpubTextExtractor(), new PlainTextExtractor(), new PdfTextExtractor()];

    public IReadOnlyList<string> SupportedExtensions { get; } = [".epub", ".txt", ".pdf"];

    public bool CanHandle(string path) => _extractors.Any(e => e.CanHandle(path));

    public async Task<ExtractedBook> ExtractAsync(string path, CancellationToken ct = default)
    {
        var key = KeyFor(path);

        if (Remembered(key) is { } cached) return cached;

        // One extraction per file at a time, shared by everyone asking: the library preparing the
        // book most likely to be opened and the reader opening it a moment later wait on the same
        // work instead of doing it twice.
        Task<ExtractedBook> work;

        lock (_gate)
        {
            if (key is not null && _running.TryGetValue(key, out var running))
            {
                work = running;
            }
            else
            {
                work = ExtractFreshAsync(path, key);
                if (key is not null && !work.IsCompleted) _running[key] = work;
            }
        }

        var extracted = await work.WaitAsync(ct).ConfigureAwait(false);
        return new ExtractedBook(extracted.Text, Copy(extracted.Chapters), extracted.Cover);
    }

    private async Task<ExtractedBook> ExtractFreshAsync(string path, string? key)
    {
        try
        {
            var extractor = _extractors.FirstOrDefault(e => e.CanHandle(path))
                ?? ByContent(path)
                ?? throw new NotSupportedException(CoreStrings.Book_UnknownFormat);

            // Not cancelled by whoever asked first: someone else may be waiting on the same result.
            var extracted = await extractor.ExtractAsync(path, CancellationToken.None).ConfigureAwait(false);

            Remember(key, extracted);
            return extracted;
        }
        finally
        {
            if (key is not null) lock (_gate) _running.Remove(key);
        }
    }

    // ---- A few books, kept ----

    /// <summary>
    /// The last few books extracted, so the parts of the app that work on the same book at the same
    /// time stop each parsing their own copy of it — and so going back to a book just left does not
    /// parse it all over again.
    ///
    /// Reading a paired book with measuring switched on runs three readers of it at once — the
    /// reader, the live aligner and background alignment — and each was extracting the file
    /// independently. One entry covered that, but not moving between two books: every switch parsed
    /// the whole EPUB again, two and a half seconds for a large one on a phone, and that pause with
    /// nothing on screen read as a tap that had not worked.
    ///
    /// Three, the most recent first: the book being read, the one just left, and the one prepared
    /// for opening next. Each is a few megabytes, so this stays small.
    /// </summary>
    private const int Keep = 3;

    private readonly object _gate = new();

    private readonly List<(string Key, BookText Text, IReadOnlyList<Chapter> Chapters, byte[]? Cover)> _kept = [];

    private readonly Dictionary<string, Task<ExtractedBook>> _running = [];

    /// <summary>
    /// Identifies a file by more than its name, so a book replaced on disk is not served from the
    /// entry belonging to whatever used to be there.
    /// </summary>
    private static string? KeyFor(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}" : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Not worth failing an extraction over; it only means this one is not cached.
            return null;
        }
    }

    private ExtractedBook? Remembered(string? key)
    {
        if (key is null) return null;

        lock (_gate)
        {
            var index = _kept.FindIndex(k => k.Key == key);
            if (index < 0) return null;

            // Most recent first, so the one left longest is the one dropped next.
            var entry = _kept[index];
            _kept.RemoveAt(index);
            _kept.Insert(0, entry);

            return new ExtractedBook(entry.Text, Copy(entry.Chapters), entry.Cover);
        }
    }

    private void Remember(string? key, ExtractedBook extracted)
    {
        if (key is null) return;

        lock (_gate)
        {
            _kept.RemoveAll(k => k.Key == key);
            _kept.Insert(0, (key, extracted.Text, Copy(extracted.Chapters), extracted.Cover));

            if (_kept.Count > Keep) _kept.RemoveRange(Keep, _kept.Count - Keep);
        }
    }

    /// <summary>
    /// Chapters are handed out fresh every time, while the text itself is shared.
    ///
    /// <see cref="BookText"/> is built once and never written to, so every caller can hold the same
    /// one — that sharing is the entire point of keeping it. <see cref="Chapter"/> is the opposite:
    /// it is a database row, and saving a book's chapters assigns ids straight onto the objects it
    /// is given. Handing the same instances to the next caller would let one book's save rewrite
    /// what another is still reading, so they are copied on the way in and on the way out. There
    /// are a few dozen of them; the cost is nothing beside the text.
    /// </summary>
    private static List<Chapter> Copy(IReadOnlyList<Chapter> chapters) =>
    [
        .. chapters.Select(c => new Chapter
        {
            Id = c.Id,
            BookId = c.BookId,
            Index = c.Index,
            Title = c.Title,
            StartMs = c.StartMs,
            EndMs = c.EndMs,
            TextStart = c.TextStart,
            TextEnd = c.TextEnd,
        }),
    ];

    /// <summary>
    /// Works out the format from the file's first bytes when the name does not say.
    ///
    /// Filenames arrive unreliable: cloud storage and messaging apps rename attachments, strip
    /// extensions, or hand back a display name that never had one. Refusing a perfectly good EPUB
    /// because its name lost four characters on the way to the phone is not defensible when the
    /// first four bytes settle the question.
    /// </summary>
    private IBookTextExtractor? ByContent(string path)
    {
        Span<byte> header = stackalloc byte[4];

        try
        {
            using var stream = File.OpenRead(path);
            if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length) return null;
        }
        catch (IOException)
        {
            return null;
        }

        // EPUB is a zip container: "PK\x03\x04".
        var isZip = header[0] == 0x50 && header[1] == 0x4B && header[2] == 0x03 && header[3] == 0x04;

        if (isZip) return _extractors.FirstOrDefault(e => e is EpubTextExtractor);

        // PDF: "%PDF".
        var isPdf = header[0] == 0x25 && header[1] == 0x50 && header[2] == 0x44 && header[3] == 0x46;

        return isPdf ? _extractors.FirstOrDefault(e => e is PdfTextExtractor) : null;
    }
}
