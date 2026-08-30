using AudioBookReader.Core.Models;

namespace AudioBookReader.Core.Books;

/// <summary>An ebook reduced to what the library needs: its text and its chapter divisions.</summary>
public record ExtractedBook(BookText Text, IReadOnlyList<Chapter> Chapters);

/// <summary>
/// Reads one ebook format into the internal representation. Every format normalizes to the same
/// shape, so the reader and the aligner never learn where a book came from.
/// </summary>
public interface IBookTextExtractor
{
    bool CanHandle(string path);

    Task<ExtractedBook> ExtractAsync(string path, CancellationToken ct = default);
}
