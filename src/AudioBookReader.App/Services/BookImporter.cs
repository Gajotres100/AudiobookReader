using AudioBookReader.Core.Audio;
using AudioBookReader.Core.Books;
using AudioBookReader.Core.Data;
using AudioBookReader.Core.Models;

namespace AudioBookReader.App.Services;

/// <summary>
/// Brings a file into the library: copies it into app storage, reads what it can from it, and
/// either creates a book or attaches it to one.
///
/// Copying rather than referencing is deliberate. The file picker hands out a permission grant
/// that does not survive a reboot, so a referenced book would quietly stop opening later; and
/// owning the copy is what lets the app avoid asking for access to the user's whole filesystem.
/// </summary>
public class BookImporter(
    LibraryService library,
    LibraryDatabase database,
    SyncMapStore syncMaps,
    BookTextExtractors extractors)
{
    public bool IsAudio(string fileName) => AudioBookProbe.IsSupportedAudioFile(fileName);

    public bool IsEbook(string fileName) => extractors.CanHandle(fileName);

    /// <summary>Imports an audiobook as a new book, or attaches it to <paramref name="attachTo"/>.</summary>
    public async Task<Book> ImportAudioAsync(
        Stream source,
        string fileName,
        int? attachTo = null,
        CancellationToken ct = default)
    {
        var path = await CopyInAsync(source, fileName, ct);

        try
        {
            var info = await AudioBookProbe.ProbeAsync(path, ct);

            // Whether this is an audiobook is decided by what the decoder found, not by the name.
            // A file with no readable duration is not something that can be played, whatever it is
            // called — this is what stops a stray .nfo or cover image becoming a broken entry.
            if (info.DurationMs <= 0)
                throw new NotSupportedException("Ovo ne izgleda kao audio datoteka — nije pronađen zvučni zapis.");

            var hash = await ContentHash.ComputeAsync(path, ct);

            var coverPath = info.Cover is { Length: > 0 }
                ? await SaveCoverAsync(info.Cover, Path.GetFileNameWithoutExtension(fileName), ct)
                : null;

            var attachment = new AudioAttachment(
                path, hash, info.DurationMs, info.Chapters, info.Title, info.Author, coverPath);

            return attachTo is { } bookId
                ? await library.AttachAudioAsync(bookId, attachment)
                : await library.CreateFromAudioAsync(attachment);
        }
        catch
        {
            // The copy is made before the file can be inspected, so a rejected import has to take
            // it back out again or app storage fills with files no book refers to.
            TryDelete(path);
            throw;
        }
    }

    /// <summary>Imports an ebook as a new book, or attaches it to <paramref name="attachTo"/>.</summary>
    public async Task<Book> ImportEbookAsync(
        Stream source,
        string fileName,
        int? attachTo = null,
        CancellationToken ct = default)
    {
        var path = await CopyInAsync(source, fileName, ct);

        try
        {
            var extracted = await extractors.ExtractAsync(path, ct);

            if (extracted.Text.PlainText.Length == 0)
                throw new NotSupportedException("Iz ove datoteke nisam izvukao nikakav tekst.");

            var hash = await ContentHash.ComputeAsync(path, ct);

            var attachment = new TextAttachment(
                path,
                hash,
                extracted.Text.PlainText.Length,
                extracted.Chapters,
                extracted.Text.Title,
                extracted.Text.Author);

            return attachTo is { } bookId
                ? await library.AttachTextAsync(bookId, attachment)
                : await library.CreateFromTextAsync(attachment);
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // Cleanup only; the import's own failure is the thing worth reporting.
        }
    }

    /// <summary>
    /// Removes a medium and deletes the file it used, unless another book still points at it.
    /// </summary>
    public async Task<Book> RemoveAudioAsync(int bookId, CancellationToken ct = default)
    {
        var before = await database.GetBookAsync(bookId);
        var book = await library.DetachAudioAsync(bookId);

        if (before?.AudioPath is { } path) await DeleteIfUnusedAsync(path);
        return book;
    }

    public async Task<Book> RemoveEbookAsync(int bookId, CancellationToken ct = default)
    {
        var before = await database.GetBookAsync(bookId);
        var book = await library.DetachTextAsync(bookId);

        if (before?.EbookPath is { } path) await DeleteIfUnusedAsync(path);
        return book;
    }

    /// <summary>
    /// Removes a book entirely, along with the files it owns and its sync map.
    ///
    /// Needed as much for mistakes as for tidying: an import that produced something wrong has to
    /// be undoable, or the library fills with entries the user cannot get rid of.
    /// </summary>
    public async Task DeleteBookAsync(int bookId, CancellationToken ct = default)
    {
        var book = await database.GetBookAsync(bookId);

        await database.DeleteBookAsync(bookId);
        syncMaps.Delete(bookId);

        if (book is null) return;

        foreach (var path in new[] { book.AudioPath, book.EbookPath })
            if (path is not null) await DeleteIfUnusedAsync(path);

        // Covers are written per import and shared with nothing.
        if (book.CoverPath is { } cover
            && cover.StartsWith(AppPaths.Covers, StringComparison.OrdinalIgnoreCase)
            && File.Exists(cover))
        {
            File.Delete(cover);
        }
    }

    private async Task DeleteIfUnusedAsync(string path)
    {
        // The same file could have been imported into two library entries; deleting it out from
        // under the other one would break a book the user did not touch.
        if (await database.FindBookByMediaPathAsync(path) is not null) return;

        if (path.StartsWith(AppPaths.Books, StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            File.Delete(path);
    }

    private static async Task<string> CopyInAsync(Stream source, string fileName, CancellationToken ct)
    {
        Directory.CreateDirectory(AppPaths.Books);

        var path = UniquePath(AppPaths.Books, SafeName(fileName));

        await using (var destination = File.Create(path))
            await source.CopyToAsync(destination, ct);

        return path;
    }

    private static async Task<string> SaveCoverAsync(byte[] cover, string name, CancellationToken ct)
    {
        Directory.CreateDirectory(AppPaths.Covers);

        var path = UniquePath(AppPaths.Covers, SafeName(name) + ".img");
        await File.WriteAllBytesAsync(path, cover, ct);

        return path;
    }

    private static string SafeName(string fileName)
    {
        var cleaned = string.Concat(fileName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return string.IsNullOrWhiteSpace(cleaned) ? "book" : cleaned;
    }

    /// <summary>Avoids overwriting an existing import of a file with the same name.</summary>
    private static string UniquePath(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path)) return path;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);

        for (var i = 2; ; i++)
        {
            path = Path.Combine(directory, $"{stem} ({i}){extension}");
            if (!File.Exists(path)) return path;
        }
    }
}
