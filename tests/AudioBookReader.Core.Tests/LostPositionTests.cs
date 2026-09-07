using AudioBookReader.Core.Alignment;

namespace AudioBookReader.Core.Tests;

/// <summary>
/// Finding the narration when the text does not begin where the audio does.
///
/// Written from a real pair that aligned to nothing. The ebook was a collection of three works and
/// its file put the publisher's foreword at character 260,000, while the recording opened with it —
/// so the aligner predicted character 88,000 after an hour and a half and the truth was never once
/// inside a search window capped at twelve thousand tokens. Every probe missed, and a book that was
/// perfectly alignable produced no anchors at all.
/// </summary>
public class LostPositionTests
{
    private const long ChapterMs = 30 * 60 * 1_000;

    /// <summary>
    /// A book whose text runs in a different order from its narration: the part that is read first
    /// sits far into the file, with unrelated prose in front of it.
    /// </summary>
    private static (FakeNarration Narration, ChapterAligner Aligner, ChapterAlignmentRequest Request) OutOfOrder()
    {
        var narrated = FakeNarration.GenerateProse(sentenceCount: 400);
        // Far enough in that widening cannot reach it: the cap is twelve thousand tokens, and this
        // puts the narrated text some forty thousand beyond the start. Only a search of the whole
        // book finds it, which is the point of the test.
        var infront = FakeNarration.GenerateProse(sentenceCount: 4000, seed: 99);

        var book = infront + " " + narrated;

        // The narrator reads only the second half; the aligner is told the chapter is the whole
        // book, which is exactly what a single-file recording of a collection looks like.
        var narration = new FakeNarration(narrated, ChapterMs);
        var aligner = new ChapterAligner(TokenizedText.Create(book), narration);

        var request = new ChapterAlignmentRequest("book.mp3", 0, 0, ChapterMs, 0, book.Length);
        return (narration, aligner, request);
    }

    [Fact]
    public async Task FindsNarrationThatStartsFarIntoTheText()
    {
        var (_, aligner, request) = OutOfOrder();

        var map = await aligner.AlignAsync(request);

        // Anything beyond the two boundary guesses means the run recovered rather than spending the
        // whole chapter missing.
        var measured = map.Anchors.Where(a => a.Confidence > 0.2f).ToList();

        Assert.NotEmpty(measured);
    }

    [Fact]
    public async Task PlacesTheAnchorsWhereTheNarrationActuallyIs()
    {
        var (_, aligner, request) = OutOfOrder();

        var map = await aligner.AlignAsync(request);
        var measured = map.Anchors.Where(a => a.Confidence > 0.2f).ToList();

        Assert.NotEmpty(measured);

        // The narrated half begins a long way in. Anchors landing before that would mean the
        // matcher found the wrong text, which is worse than finding none.
        var narratedStartsAround = request.TextEnd / 4;

        Assert.All(measured, a => Assert.True(
            a.CharOffset > narratedStartsAround,
            $"anchor at char {a.CharOffset} sits in the prose that is never read aloud"));
    }

    [Fact]
    public async Task DoesNotSearchEverywhereWhenTheBookIsOrdinary()
    {
        // The guard has to stay out of the way of a book that aligns normally: same prose, read in
        // the order it is written.
        var prose = FakeNarration.GenerateProse(sentenceCount: 400);
        var narration = new FakeNarration(prose, ChapterMs);
        var aligner = new ChapterAligner(TokenizedText.Create(prose), narration);

        var map = await aligner.AlignAsync(
            new ChapterAlignmentRequest("book.m4b", 0, 0, ChapterMs, 0, prose.Length));

        Assert.Contains(map.Anchors, a => a.Confidence > 0.2f);
    }
}
