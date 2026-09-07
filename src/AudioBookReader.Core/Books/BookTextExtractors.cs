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
            : [new EpubTextExtractor(), new PlainTextExtractor()];

    public IReadOnlyList<string> SupportedExtensions { get; } = [".epub", ".txt"];

    public bool CanHandle(string path) => _extractors.Any(e => e.CanHandle(path));

    public Task<ExtractedBook> ExtractAsync(string path, CancellationToken ct = default)
    {
        var extractor = _extractors.FirstOrDefault(e => e.CanHandle(path))
            ?? ByContent(path)
            ?? throw new NotSupportedException(CoreStrings.Book_UnknownFormat);

        return extractor.ExtractAsync(path, ct);
    }

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

        return isZip
            ? _extractors.FirstOrDefault(e => e is EpubTextExtractor)
            : null;
    }
}
