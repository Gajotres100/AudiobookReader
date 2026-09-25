using System.Text;
using System.Text.RegularExpressions;
using AudioBookReader.Core.Models;

namespace AudioBookReader.Core.Books;

/// <summary>
/// Recognises the two halves of one book by their file names.
///
/// An audiobook and its ebook fetched from the same place usually differ only in the extension —
/// "Torch.m4b" and "Torch.epub" — while the titles inside them disagree as often as not: one is
/// read from the audio's tags, the other from the ebook's metadata. So the name the user sees on
/// the files is what decides, and a second file added on its own joins the first instead of
/// becoming a second entry on the shelf.
/// </summary>
public static partial class BookNames
{
    /// <summary>
    /// A file name reduced to what identifies the book: no extension, no case, and every run of
    /// spaces, underscores and punctuation made one space — "Torch_(Unabridged).m4b" and
    /// "torch (unabridged).epub" are the same book.
    /// </summary>
    public static string Normalize(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName.Trim());
        var builder = new StringBuilder(stem.Length);
        var gap = false;

        foreach (var c in stem.Normalize(NormalizationForm.FormC))
        {
            if (char.IsLetterOrDigit(c))
            {
                if (gap && builder.Length > 0) builder.Append(' ');
                builder.Append(char.ToLowerInvariant(c));
                gap = false;
            }
            else
            {
                gap = true;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// The file name at the end of a location: a path, a file URL, or an Android document URI whose
    /// last segment is escaped ("primary%3AAudiobooks%2FTorch.m4b").
    /// </summary>
    public static string FileNameOf(string location)
    {
        var unescaped = location.Contains('%') ? Uri.UnescapeDataString(location) : location;
        var cut = unescaped.LastIndexOfAny(['/', '\\', ':']);

        return cut >= 0 ? unescaped[(cut + 1)..] : unescaped;
    }

    /// <summary>Every name this book's files have gone by, normalised.</summary>
    public static IEnumerable<string> NamesOf(Book book)
    {
        if (!string.IsNullOrWhiteSpace(book.SourceName)) yield return Normalize(book.SourceName);

        foreach (var location in new[] { book.AudioPath, book.OriginalAudioPath, book.EbookPath })
        {
            if (string.IsNullOrWhiteSpace(location)) continue;

            // A copy made in the app's own storage may have had " (2)" added to keep it from
            // overwriting another file of the same name — that is not part of the book's name.
            var name = CopySuffix().Replace(Path.GetFileNameWithoutExtension(FileNameOf(location)), "");
            yield return Normalize(name);
        }
    }

    /// <summary>
    /// The one book on the shelf that is waiting for this file: it has the other half only, and one
    /// of its files carries the same name. Null when there is none — or more than one, since
    /// guessing between them would pair a book with the wrong text.
    /// </summary>
    public static Book? FindPartner(IEnumerable<Book> books, string fileName, bool isAudio)
    {
        var name = Normalize(fileName);
        if (name.Length == 0) return null;

        var matches = books
            .Where(b => isAudio ? b is { HasAudio: false, HasText: true } : b is { HasText: false, HasAudio: true })
            .Where(b => NamesOf(b).Contains(name))
            .Take(2)
            .ToList();

        return matches.Count == 1 ? matches[0] : null;
    }

    [GeneratedRegex(@" \(\d+\)$")]
    private static partial Regex CopySuffix();
}
