using AudioBookReader.Core.Models;

namespace AudioBookReader.Core.Books;

/// <summary>An ebook reduced to what the library needs: its text and its chapter divisions.</summary>
/// <param name="Cover">
/// The book's own cover art, when the file carries one. An ebook almost always does, and a book
/// with no audio had nowhere else to get one — so a shelf of novels was a wall of title cards.
/// </param>
public record ExtractedBook(BookText Text, IReadOnlyList<Chapter> Chapters, byte[]? Cover = null);

/// <summary>
/// Reads one ebook format into the internal representation. Every format normalizes to the same
/// shape, so the reader and the aligner never learn where a book came from.
/// </summary>
public interface IBookTextExtractor
{
    bool CanHandle(string path);

    Task<ExtractedBook> ExtractAsync(string path, CancellationToken ct = default);
}
