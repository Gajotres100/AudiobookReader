using AudioBookReader.Core.Audio;
using AudioBookReader.Core.Books;
using AudioBookReader.Core.Data;
using AudioBookReader.Core.Models;

namespace AudioBookReader.App.Services;

/// <summary>How far an import has got, for a screen that would otherwise look frozen.</summary>
/// <param name="Fraction">0..1, or 0 when the source will not say how large it is.</param>
public record ImportProgress(string Message, double Fraction);

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
    BookTextExtractors extractors,
    MediaReferences references)
{
    public bool IsAudio(string fileName) => AudioBookProbe.IsSupportedAudioFile(fileName);

    public bool IsEbook(string fileName) => extractors.CanHandle(fileName);

    /// <summary>
    /// Imports an audiobook, leaving the file where the user keeps it when the system allows.
    ///
    /// An audiobook is hundreds of megabytes and copying one takes half a minute and twice the
    /// storage — for a file the system will happily keep serving in place. So the copy is not made
    /// unless it has to be, which is why this is instant for the common case of simply adding
    /// something to listen to. A book that is later paired with its text gets copied in then, since
    /// alignment is the one job that reads the file over and over for hours.
    /// </summary>
    public async Task<Book> ImportAudioAsync(
        PickedMedia picked,
        int? attachTo = null,
        IProgress<ImportProgress>? progress = null,
        CancellationToken ct = default)
    {
        var referenced = references.TryHold(picked.Location);
        var location = picked.Location;

        if (!referenced)
        {
            await using var source = references.OpenRead(picked.Location);
            location = await CopyInAsync(source, picked.FileName, progress, ct);
        }

        AppLog.Info($"audio import: {(referenced ? "referenced" : "copied")} '{location}'");

        try
        {
            progress?.Report(new ImportProgress("Čitam poglavlja…", 1));

            var info = await ProbeAsync(location, picked.FileName, referenced, ct);

            // Whether this is an audiobook is decided by what the decoder found, not by the name.
            // A file with no readable duration is not something that can be played, whatever it is
            // called — this is what stops a stray .nfo or cover image becoming a broken entry.
            if (info.DurationMs <= 0)
                throw new NotSupportedException("Ovo ne izgleda kao audio datoteka — nije pronađen zvučni zapis.");

            var hash = await HashAsync(location, ct);

            var coverPath = info.Cover is { Length: > 0 }
                ? await SaveCoverAsync(info.Cover, Path.GetFileNameWithoutExtension(picked.FileName), ct)
                : null;

            var attachment = new AudioAttachment(
                location, hash, info.DurationMs, info.Chapters, info.Title, info.Author, coverPath);

            return attachTo is { } bookId
                ? await library.AttachAudioAsync(bookId, attachment)
                : await library.CreateFromAudioAsync(attachment);
        }
        catch
        {
            // A copy is made before the file can be inspected, so a rejected import has to take it
            // back out again or app storage fills with files no book refers to. A reference owns
            // nothing, but the permission it took should not be kept either.
            if (referenced) references.Release(location);
            else TryDelete(location);

            throw;
        }
    }

    private Task<AudioBookInfo> ProbeAsync(string location, string fileName, bool referenced, CancellationToken ct)
    {
        if (!referenced) return AudioBookProbe.ProbeAsync(location, ct);

        var stream = references.OpenRead(location);
        return ProbeAndCloseAsync(stream, fileName, ct);
    }

    private static async Task<AudioBookInfo> ProbeAndCloseAsync(Stream stream, string fileName, CancellationToken ct)
    {
        await using (stream)
            return await AudioBookProbe.ProbeAsync(stream, fileName, ct);
    }

    private async Task<string> HashAsync(string location, CancellationToken ct)
    {
        await using var stream = references.OpenRead(location);
        return await ContentHash.ComputeAsync(stream, ct);
    }

    /// <summary>
    /// Makes sure a book's audio is a local copy, copying it in if it is only referenced.
    ///
    /// Called when a book gains its second medium and becomes a read-along. Alignment reads the
    /// audio in short bursts over hours; a reference can point at a cloud document that would be
    /// fetched afresh every time, or at a card the user can pull out mid-run. The identity hash is
    /// unchanged by copying, so alignment already done is not invalidated.
    /// </summary>
    public async Task<Book?> EnsureLocalAudioAsync(
        int bookId,
        IProgress<ImportProgress>? progress = null,
        CancellationToken ct = default)
    {
        var book = await database.GetBookAsync(bookId);
        if (book?.AudioPath is not { } location || !references.IsReference(location)) return book;

        progress?.Report(new ImportProgress("Pripremam za poravnanje…", 0));

        var name = Path.GetFileName(location.TrimEnd('/'));
        if (string.IsNullOrWhiteSpace(name) || !Path.HasExtension(name)) name = $"{book.Title}.m4b";

        await using (var source = references.OpenRead(location))
            book.AudioPath = await CopyInAsync(source, name, progress, ct);

        await database.UpdateBookAsync(book);
        references.Release(location);

        AppLog.Info($"audio copied in for alignment: '{book.AudioPath}'");
        return book;
    }

    /// <summary>
    /// Imports an ebook, always copying it in.
    ///
    /// Unlike an audiobook this is a few megabytes and copies faster than the eye notices, and the
    /// text is read repeatedly — every page turn, every alignment pass — so owning it is worth the
    /// space it costs.
    /// </summary>
    public async Task<Book> ImportEbookAsync(
        PickedMedia picked,
        int? attachTo = null,
        IProgress<ImportProgress>? progress = null,
        CancellationToken ct = default)
    {
        await using var source = references.OpenRead(picked.Location);

        var fileName = picked.FileName;
        var path = await CopyInAsync(source, fileName, progress, ct);

        try
        {
            progress?.Report(new ImportProgress("Čitam tekst…", 1));

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
                extracted.Text.Author,
                TextLanguage.Detect(extracted.Text.PlainText));

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

        // The ebook's own chapters, read back before the audio's are discarded.
        //
        // Without them the book loses every chapter it had. Chapters belong to the audio while it
        // is present, so they carry no text range until alignment has filled one in — and stripping
        // the audio ranges from chapters that have no text ranges leaves nothing at all. Removing an
        // audiobook from a pairing that was never aligned would empty a forty-chapter ebook, and
        // there is no way back: the ebook cannot be removed and re-added, because by then it is the
        // only medium left.
        var fromText = await ChaptersFromTextAsync(before, ct);

        var book = await library.DetachAudioAsync(bookId, fromText);

        if (before?.AudioPath is { } path) await DeleteIfUnusedAsync(path);
        return book;
    }

    /// <summary>The ebook's chapters, or null when there is no readable ebook to ask.</summary>
    private async Task<IReadOnlyList<Chapter>?> ChaptersFromTextAsync(Book? book, CancellationToken ct)
    {
        if (book?.EbookPath is not { } path) return null;

        try
        {
            var extracted = await extractors.ExtractAsync(path, ct);
            return extracted.Chapters.Count > 0 ? extracted.Chapters : null;
        }
        catch (Exception ex)
        {
            // Better to fall back to the old behaviour than to refuse the removal outright.
            AppLog.Error("reading the ebook's chapters while removing the audio", ex);
            return null;
        }
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

        // A referenced file is the user's own, sitting wherever they keep it. Removing it from the
        // library gives back the permission and nothing else — deleting someone's audiobook because
        // they tidied their library would be unforgivable.
        if (references.IsReference(path))
        {
            references.Release(path);
            return;
        }

        if (path.StartsWith(AppPaths.Books, StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            File.Delete(path);
    }

    /// <summary>
    /// Copies the picked file into app storage, saying how far along it is.
    ///
    /// An audiobook is often several hundred megabytes, so this is the slow part of an import and
    /// there is no making it instant — but a screen that sits still for half a minute reads as a
    /// hung app, while the same wait with a percentage on it reads as work.
    /// </summary>
    private static async Task<string> CopyInAsync(
        Stream source,
        string fileName,
        IProgress<ImportProgress>? progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(AppPaths.Books);

        var path = UniquePath(AppPaths.Books, SafeName(fileName));

        // A megabyte at a time. The picker hands back a stream from a content provider, and each
        // read crosses a process boundary — the default eighty-kilobyte buffer pays that toll more
        // than ten times as often.
        const int bufferSize = 1024 * 1024;

        var total = TotalLength(source);
        var buffer = new byte[bufferSize];

        long copied = 0;
        var lastPercent = -1;
        var lastMegabytes = -1L;

        await using (var destination = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, useAsync: true))
        {
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                copied += read;

                if (total > 0)
                {
                    var percent = (int)(copied * 100 / total);
                    if (percent == lastPercent) continue;

                    lastPercent = percent;
                    progress?.Report(new ImportProgress($"Kopiram… {percent}%", copied / (double)total));
                }
                else
                {
                    // Some providers will not give a length. Megabytes still show movement.
                    var megabytes = copied / (1024 * 1024);
                    if (megabytes == lastMegabytes) continue;

                    lastMegabytes = megabytes;
                    progress?.Report(new ImportProgress($"Kopiram… {megabytes} MB", 0));
                }
            }
        }

        return path;
    }

    /// <summary>The source's size, or zero when it will not say — content streams often will not.</summary>
    private static long TotalLength(Stream source)
    {
        try
        {
            return source.CanSeek ? source.Length : 0;
        }
        catch (NotSupportedException)
        {
            return 0;
        }
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
