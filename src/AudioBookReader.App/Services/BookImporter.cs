using AudioBookReader.App.Resources.Strings;
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
    MediaReferences references,
    PlaybackController playback)
{
    /// <summary>
    /// Says that the set of books has changed, for anything reading it from outside the app.
    ///
    /// Only where the car's own list would look different: it shows books with audio, so gaining
    /// audio, losing it, or losing the whole book are the three that matter. Attaching text to a
    /// book leaves that list exactly as it was.
    /// </summary>
    private void ShelfChanged() => playback.NotifyLibraryChanged();

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
        using var busy = Busy();

        var referenced = references.TryHold(picked.Location);
        var location = picked.Location;
        var how = referenced ? "referenced" : "copied";

        if (!referenced)
        {
            if (MoveInIfStaged(picked.Location, picked.FileName) is { } moved)
            {
                location = moved;
                how = "moved";
            }
            else
            {
                await using var source = references.OpenRead(picked.Location);
                location = await CopyInAsync(source, picked.FileName, progress, ct);
            }
        }

        AppLog.Info($"audio import: {how} '{location}'");

        try
        {
            progress?.Report(new ImportProgress(Strings.Progress_ReadingChapters, 1));

            var info = await ProbeAsync(location, picked.FileName, referenced, ct);

            // Whether this is an audiobook is decided by what the decoder found, not by the name.
            // A file with no readable duration is not something that can be played, whatever it is
            // called — this is what stops a stray .nfo or cover image becoming a broken entry.
            if (info.DurationMs <= 0)
                throw new NotSupportedException(Strings.Import_NotAudio);

            var hash = await HashAsync(location, ct);

            var coverPath = info.Cover is { Length: > 0 }
                ? await SaveCoverAsync(info.Cover, Path.GetFileNameWithoutExtension(picked.FileName), ct)
                : null;

            var attachment = new AudioAttachment(
                location, hash, info.DurationMs, info.Chapters, info.Title, info.Author, coverPath);

            var imported = attachTo is { } bookId
                ? await library.AttachAudioAsync(bookId, attachment)
                : await RememberNameAsync(await library.CreateFromAudioAsync(attachment), picked.FileName);

            ShelfChanged();
            return imported;
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

    /// <summary>
    /// Reads what the book is, and does not give up on the first reader that cannot.
    ///
    /// The tag library is asked first because it is the only thing that reports chapter marks, and
    /// chapter marks are what make an audiobook navigable. When it refuses — which real books do
    /// provoke; an ordinary MP4 audiobook named "…m4b.mp3" was enough — the platform's own
    /// extractor is asked instead. That loses the chapters and keeps the book.
    /// </summary>
    private async Task<AudioBookInfo> ProbeAsync(string location, string fileName, bool referenced, CancellationToken ct)
    {
        try
        {
            if (!referenced) return await AudioBookProbe.ProbeAsync(location, ct);

            var stream = references.OpenRead(location);
            return await ProbeAndCloseAsync(stream, fileName, ct);
        }
        catch (UnreadableAudioException ex)
        {
            AppLog.Info($"tags unreadable for '{fileName}' ({ex.InnerException?.GetType().Name}); asking the platform");

            return await AudioMetadata.ReadAsync(location, fileName)
                   ?? throw new NotSupportedException(
                       string.Format(Strings.Import_Unreadable, fileName), ex);
        }
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
        using var busy = Busy();

        var book = await database.GetBookAsync(bookId);
        if (book?.AudioPath is not { } location || !references.IsReference(location)) return book;

        progress?.Report(new ImportProgress(Strings.Progress_PreparingAlignment, 0));

        var name = Path.GetFileName(location.TrimEnd('/'));
        if (string.IsNullOrWhiteSpace(name) || !Path.HasExtension(name)) name = $"{book.Title}.m4b";

        // Remembered before AudioPath is redirected, and only if nothing is remembered yet — a
        // second call here (a re-attach, or alignment running again) must not overwrite the real
        // original with whatever the internal copy's own path happens to be.
        book.OriginalAudioPath ??= location;

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
        using var busy = Busy();

        await using var source = references.OpenRead(picked.Location);

        var fileName = picked.FileName;
        var path = await CopyInAsync(source, fileName, progress, ct);

        try
        {
            progress?.Report(new ImportProgress(Strings.Progress_ReadingText, 1));

            var extracted = await extractors.ExtractAsync(path, ct);

            if (extracted.Text.PlainText.Length == 0)
                throw new NotSupportedException(Strings.Import_NoText);

            var hash = await ContentHash.ComputeAsync(path, ct);

            // The file's own cover, when it has one. A book with no audio had nowhere else to get
            // one, so a shelf of novels came out as a wall of title cards.
            var coverPath = extracted.Cover is { Length: > 0 } cover
                ? await SaveCoverAsync(cover, Path.GetFileNameWithoutExtension(picked.FileName), ct)
                : null;

            var attachment = new TextAttachment(
                path,
                hash,
                extracted.Text.PlainText.Length,
                extracted.Chapters,
                extracted.Text.Title,
                extracted.Text.Author,
                TextLanguage.Detect(extracted.Text.PlainText),
                coverPath);

            return attachTo is { } bookId
                ? await library.AttachTextAsync(bookId, attachment)
                : await RememberNameAsync(await library.CreateFromTextAsync(attachment), picked.FileName);
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    /// <summary>
    /// The book already on the shelf that a file of this name completes — the audiobook for an
    /// ebook called the same, or the other way round — or null when there is no single such book.
    /// </summary>
    public async Task<int?> FindPartnerAsync(string fileName, bool isAudio) =>
        BookNames.FindPartner(await database.GetBooksAsync(), fileName, isAudio)?.Id;

    private async Task<Book> RememberNameAsync(Book book, string fileName)
    {
        book.SourceName = Path.GetFileNameWithoutExtension(fileName);
        await database.UpdateBookAsync(book);
        return book;
    }

    /// <summary>
    /// Where a book's own file sits, for a confirmation to name — or null when there is nothing to
    /// offer, because the only copy is the app's and that goes with the entry regardless.
    /// </summary>
    public async Task<string?> OwnFileAsync(int bookId, bool audio)
    {
        var book = await database.GetBookAsync(bookId);

        // OriginalAudioPath is where the user's own file actually is once alignment has redirected
        // AudioPath to an internal copy; falling back to AudioPath covers a book whose audio was
        // never copied in, where AudioPath is still the user's own reference.
        var path = audio ? book?.OriginalAudioPath ?? book?.AudioPath : book?.EbookPath;

        return path is not null && references.IsReference(path) ? Readable(path) : null;
    }

    /// <summary>
    /// A content URI as something a person can check against what they see in a file manager.
    ///
    /// The document id is the readable half — "primary:Audiobooks/Gwynne/Hunger/book.m4b" — and it
    /// is percent-encoded inside the URI. Decoding it is enough; making it prettier than that would
    /// mean guessing at the provider's conventions.
    /// </summary>
    private static string Readable(string location)
    {
        var id = location.Split('/').LastOrDefault() ?? location;
        var decoded = Uri.UnescapeDataString(id);

        return decoded.Contains(':') ? decoded[(decoded.IndexOf(':') + 1)..] : decoded;
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
    /// <param name="alsoReferenced">
    /// Delete the user's own file as well, not only a copy the app made. Asked for explicitly and
    /// never assumed: the file may be the only one they have.
    /// </param>
    public async Task<Book> RemoveAudioAsync(
        int bookId,
        bool alsoReferenced = false,
        CancellationToken ct = default)
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

        // The audio is about to be deleted; see DeleteBookAsync.
        playback.Unload(bookId);

        var book = await library.DetachAudioAsync(bookId, fromText);

        // The internal copy (if alignment made one) and the user's real original are two different
        // paths once OriginalAudioPath exists, and each needs its own call: DeleteIfUnusedAsync
        // deletes an app-owned copy outright but only touches a reference when alsoReferenced says
        // to, so passing both here — instead of only AudioPath, and instead of dropping
        // alsoReferenced on the floor as this used to — reaches the original file the way removing
        // audio is supposed to.
        if (before?.AudioPath is { } path) await DeleteIfUnusedAsync(path, alsoReferenced);
        if (before?.OriginalAudioPath is { } original) await DeleteIfUnusedAsync(original, alsoReferenced);

        ShelfChanged();
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

    public async Task<Book> RemoveEbookAsync(
        int bookId,
        bool alsoReferenced = false,
        CancellationToken ct = default)
    {
        var before = await database.GetBookAsync(bookId);
        var book = await library.DetachTextAsync(bookId);

        if (before?.EbookPath is { } path) await DeleteIfUnusedAsync(path, alsoReferenced);
        return book;
    }

    /// <summary>
    /// Removes a book entirely, along with the files it owns and its sync map.
    ///
    /// Needed as much for mistakes as for tidying: an import that produced something wrong has to
    /// be undoable, or the library fills with entries the user cannot get rid of.
    /// </summary>
    public async Task DeleteBookAsync(
        int bookId,
        bool alsoReferenced = false,
        CancellationToken ct = default)
    {
        var book = await database.GetBookAsync(bookId);

        // Before its files go: a player still holding the audio would play on from a deleted file
        // and keep all of its space until the app closed.
        playback.Unload(bookId);

        await database.DeleteBookAsync(bookId);
        syncMaps.Delete(bookId);

        // Before the early return below: the book is already gone from the library by here, so a
        // car still showing it is showing something that no longer exists.
        ShelfChanged();

        if (book is null) return;

        foreach (var path in new[] { book.AudioPath, book.OriginalAudioPath, book.EbookPath })
            if (path is not null) await DeleteIfUnusedAsync(path, alsoReferenced);

        // Covers are written per import and shared with nothing.
        if (book.CoverPath is { } cover
            && cover.StartsWith(AppPaths.Covers, StringComparison.OrdinalIgnoreCase)
            && File.Exists(cover))
        {
            File.Delete(cover);
        }
    }

    private async Task DeleteIfUnusedAsync(string path, bool alsoReferenced = false)
    {
        // The same file could have been imported into two library entries; deleting it out from
        // under the other one would break a book the user did not touch. This one outranks the
        // choice: it is not a preference, it is another book.
        if (await database.FindBookByMediaPathAsync(path) is not null) return;

        // A referenced file is the user's own, sitting wherever they keep it. Removing it from the
        // library gives back the permission and nothing else — unless the user has said, at the
        // moment of deleting and about this book, that the file should go too.
        if (references.IsReference(path))
        {
            if (alsoReferenced) references.TryDelete(path);

            references.Release(path);
            return;
        }

        if (path.StartsWith(AppPaths.Books, StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            File.Delete(path);
    }

    private static int _inFlight;

    /// <summary>
    /// Whether an import is writing into app storage right now — the file it is making belongs to
    /// no book until it finishes, and must not be taken for one left behind.
    /// </summary>
    public static bool IsImporting => Volatile.Read(ref _inFlight) > 0;

    private static InFlight Busy()
    {
        Interlocked.Increment(ref _inFlight);
        return new InFlight();
    }

    private readonly struct InFlight : IDisposable
    {
        public void Dispose() => Interlocked.Decrement(ref _inFlight);
    }

    /// <summary>
    /// Moves a file the server download staged in app storage into the library, rather than
    /// copying it there.
    ///
    /// Both sit on the same disk, so a move is a rename: instant, and no second copy of a book that
    /// can run to a gigabyte. Copying needed room for the book twice over, and a television with
    /// 4 GB of storage and 640 MB free could not take a book it had enough room to keep.
    /// </summary>
    /// <returns>The new location, or null when this is not a staged download and must be copied.</returns>
    private static string? MoveInIfStaged(string location, string fileName)
    {
        if (!location.StartsWith(AppPaths.Downloads, StringComparison.Ordinal) || !File.Exists(location)) return null;

        try
        {
            Directory.CreateDirectory(AppPaths.Books);

            var path = UniquePath(AppPaths.Books, SafeName(fileName));
            File.Move(location, path);
            return path;
        }
        catch (Exception ex)
        {
            AppLog.Info($"could not move '{location}' into the library ({ex.Message}); copying instead");
            return null;
        }
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
                    progress?.Report(new ImportProgress(string.Format(Strings.Progress_CopyingPercent, percent), copied / (double)total));
                }
                else
                {
                    // Some providers will not give a length. Megabytes still show movement.
                    var megabytes = copied / (1024 * 1024);
                    if (megabytes == lastMegabytes) continue;

                    lastMegabytes = megabytes;
                    progress?.Report(new ImportProgress(string.Format(Strings.Progress_CopyingMb, megabytes), 0));
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

    /// <summary>
    /// Adds audio that stays on the server — nothing downloaded, nothing probed.
    ///
    /// The server has already read the file and says what the chapters and duration are, so the
    /// tag reading an ordinary import does (minutes, on one television, for one book) is skipped
    /// entirely. The hash is taken from the server in three small pieces and matches what a
    /// downloaded copy of the same file would give, so an alignment made elsewhere still fits.
    /// </summary>
    public async Task<Book> ImportStreamedAudioAsync(
        AudioAttachment attachment,
        int? attachTo = null)
    {
        using var busy = Busy();

        AppLog.Info($"audio import: streamed '{attachment.Path}'");

        var imported = attachTo is { } bookId
            ? await library.AttachAudioAsync(bookId, attachment)
            : await library.CreateFromAudioAsync(attachment);

        ShelfChanged();
        return imported;
    }

    /// <summary>Keeps a cover picture with the library — from a file, or from the server for a streamed book.</summary>
    internal static async Task<string> SaveCoverAsync(byte[] cover, string name, CancellationToken ct)
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
