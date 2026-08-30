using AudioBookReader.Core.Books;
using AudioBookReader.Core.Data;
using AudioBookReader.Core.Models;

namespace AudioBookReader.Core.Alignment;

/// <summary>
/// Aligns a whole book, chapter by chapter, and records what it learned as it goes.
///
/// Two properties matter more than speed here. Progress is durable: the map and the book's
/// high-water mark are written after every chapter, so a run stopped by the user, a reboot or
/// thermal backoff resumes where it left off instead of starting over. And progress is useful
/// immediately: chapters are aligned in order, so the reader can follow along in chapter one
/// while the rest is still being processed.
/// </summary>
public class BookAligner(
    LibraryDatabase database,
    SyncMapStore syncMaps,
    BookTextExtractors extractors,
    ITranscriber transcriber,
    AlignmentSettings? settings = null,
    IWorkThrottle? throttle = null,
    Action<string>? log = null)
{
    private readonly AlignmentSettings _settings = settings ?? new AlignmentSettings();

    public async Task AlignAsync(
        int bookId,
        IProgress<AlignmentProgress>? progress = null,
        CancellationToken ct = default)
    {
        var book = await database.GetBookAsync(bookId)
            ?? throw new InvalidOperationException($"No book with id {bookId}.");

        if (!book.IsPaired)
            throw new InvalidOperationException($"Book {bookId} needs both an audiobook and an ebook to align.");

        var chapters = await database.GetChaptersAsync(bookId);
        if (chapters.Count == 0) return;

        var extracted = await extractors.ExtractAsync(book.EbookPath!, ct);
        var tokenized = TokenizedText.Create(extracted.Text.PlainText);
        var textLength = extracted.Text.PlainText.Length;

        var map = await LoadOrCreateMapAsync(book);
        var aligner = new ChapterAligner(tokenized, transcriber, _settings, throttle, log);

        var first = map.FirstChapterNeedingWork(chapters.Count, _settings.BoundaryConfidence);

        log?.Invoke($"book {bookId}: {textLength:N0} chars of text, {chapters.Count} chapters, starting at {first}");

        if (first >= chapters.Count) return;

        book.SyncState = SyncState.InProgress;
        await database.UpdateBookAsync(book);

        var cursor = first == 0 ? 0 : chapters[first - 1].TextEnd ?? 0;
        var remainingMs = chapters.Skip(first).Sum(c => Math.Max(0, c.DurationMs));

        try
        {
            for (var i = first; i < chapters.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var chapter = chapters[i];
                if (!chapter.HasAudioRange) continue;

                var estimatedEnd = EstimateChapterEnd(cursor, textLength, chapter.DurationMs, remainingMs);

                var chapterMap = await aligner.AlignAsync(
                    new ChapterAlignmentRequest(
                        book.AudioPath!,
                        chapter.Index,
                        chapter.StartMs!.Value,
                        chapter.EndMs!.Value,
                        chapter.TextStart ?? cursor,
                        chapter.TextEnd ?? estimatedEnd),
                    progress,
                    ct);

                map.SetChapter(chapterMap);

                log?.Invoke(
                    $"ch{chapter.Index} done: {chapterMap.Anchors.Count(a => a.Confidence > 0.5f)} strong anchors " +
                    $"of {chapterMap.Anchors.Count}, estimated text {chapter.TextStart ?? cursor}..{estimatedEnd}");

                // What alignment measured is a better chapter boundary than the estimate was, and
                // the reader needs these ranges to open the right page for a chapter.
                //
                // Only anchors that came from a real match count. The boundary anchors are guesses
                // carried at low confidence, and taking one as the chapter's end would hand the
                // next chapter a guess dressed up as a measurement — an error that compounds until
                // the predicted position falls outside the search window and every probe after it
                // misses.
                var measured = chapterMap.Anchors
                    .Where(a => a.Confidence > _settings.BoundaryConfidence)
                    .ToList();

                if (measured.Count > 0)
                {
                    chapter.TextStart = measured[0].CharOffset;
                    chapter.TextEnd = ExtrapolateToChapterEnd(measured, chapter.EndMs!.Value, estimatedEnd);

                    await database.UpdateChapterAsync(chapter);
                    cursor = chapter.TextEnd.Value;
                }
                else
                {
                    cursor = estimatedEnd;
                }

                remainingMs -= Math.Max(0, chapter.DurationMs);

                await syncMaps.SaveAsync(bookId, map);
                book.AlignedThroughChapter = i;
                await database.UpdateBookAsync(book);
            }

            book.SyncState = SyncState.Complete;
        }
        catch (OperationCanceledException)
        {
            // Stopping is normal, not a failure: the chapters already written stay usable.
            book.SyncState = book.AlignedThroughChapter >= 0 ? SyncState.Partial : SyncState.Pending;
            await database.UpdateBookAsync(book);
            throw;
        }
        catch
        {
            book.SyncState = SyncState.Failed;
            await database.UpdateBookAsync(book);
            throw;
        }

        await database.UpdateBookAsync(book);
    }

    private async Task<SyncMap> LoadOrCreateMapAsync(Book book)
    {
        var existing = await syncMaps.LoadAsync(book.Id);

        if (existing is not null && existing.MatchesPair(book.AudioHash, book.EbookHash)) return existing;

        // A map for a different pair of files says nothing about this one; start clean.
        book.AlignedThroughChapter = -1;
        return new SyncMap { AudioHash = book.AudioHash, EbookHash = book.EbookHash };
    }

    /// <summary>
    /// Guesses where a chapter ends in the text by giving it the share of the remaining text that
    /// matches its share of the remaining audio. Only ever a starting point — the search window
    /// absorbs the error, and the anchors that come back replace the guess.
    /// </summary>
    /// <summary>
    /// Carries the last measured anchor forward to the chapter's end at the rate the chapter was
    /// actually read, so the next chapter starts from evidence rather than from a proportional
    /// guess.
    /// </summary>
    private static int ExtrapolateToChapterEnd(List<Anchor> measured, long chapterEndMs, int fallback)
    {
        var last = measured[^1];

        if (measured.Count < 2) return Math.Max(last.CharOffset, fallback);

        var first = measured[0];
        var elapsed = last.AudioMs - first.AudioMs;
        if (elapsed <= 0) return Math.Max(last.CharOffset, fallback);

        var charsPerMs = (last.CharOffset - first.CharOffset) / (double)elapsed;
        if (charsPerMs <= 0) return Math.Max(last.CharOffset, fallback);

        return last.CharOffset + (int)Math.Round(charsPerMs * (chapterEndMs - last.AudioMs));
    }

    private static int EstimateChapterEnd(int cursor, int textLength, long chapterMs, long remainingMs)
    {
        if (remainingMs <= 0 || chapterMs <= 0) return textLength;

        var share = Math.Clamp(chapterMs / (double)remainingMs, 0, 1);
        return cursor + (int)Math.Round((textLength - cursor) * share);
    }
}
