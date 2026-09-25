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

        var extractor = _extractors.FirstOrDefault(e => e.CanHandle(path))
            ?? ByContent(path)
            ?? throw new NotSupportedException(CoreStrings.Book_UnknownFormat);

        var extracted = await extractor.ExtractAsync(path, ct).ConfigureAwait(false);

        Remember(key, extracted);
        return extracted;
    }

    // ---- One book, kept ----

    /// <summary>
    /// The last book extracted, so the parts of the app that work on the same book at the same time
    /// stop each parsing their own copy of it.
    ///
    /// Reading a paired book with measuring switched on runs three of them at once — the reader, the
    /// live aligner and background alignment — and each was extracting the file independently and
    /// holding the result for as long as it was working. For a novel that is three copies of several
    /// megabytes to answer identical questions about identical bytes.
    ///
    /// One entry, because that is the shape of the demand: everything in the app is looking at one
    /// book. Opening another replaces it, which is also what frees the previous one.
    /// </summary>
    private readonly object _gate = new();

    private string? _key;
    private BookText? _text;
    private IReadOnlyList<Chapter>? _chapters;
    private byte[]? _cover;

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
            return _key == key && _text is not null && _chapters is not null
                ? new ExtractedBook(_text, Copy(_chapters), _cover)
                : null;
        }
    }

    private void Remember(string? key, ExtractedBook extracted)
    {
        if (key is null) return;

        lock (_gate)
        {
            _key = key;
            _text = extracted.Text;
            _chapters = Copy(extracted.Chapters);
            _cover = extracted.Cover;
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
