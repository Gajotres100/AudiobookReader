using AudioBookReader.Core.Alignment;
using AudioBookReader.Core.Models;

namespace AudioBookReader.Core.Tests;

/// <summary>
/// Continuing a chapter that was interrupted.
///
/// Written after a real failure: an MP3 with no chapter marks is one chapter covering the whole
/// book, the map was saved only when a chapter finished, and a seven-hour chapter that was stopped
/// part of the way through left nothing behind at all. Hours of recognition, no map, and the next
/// run starting from the beginning.
/// </summary>
public class ResumeWithinChapterTests
{
    private const long ChapterMs = 60 * 60 * 1_000;

    private static (FakeNarration Narration, ChapterAligner Aligner, ChapterAlignmentRequest Request) Build()
    {
        var prose = FakeNarration.GenerateProse(sentenceCount: 800);
        var narration = new FakeNarration(prose, ChapterMs);
        var aligner = new ChapterAligner(TokenizedText.Create(prose), narration);

        return (narration, aligner, new ChapterAlignmentRequest("book.mp3", 0, 0, ChapterMs, 0, prose.Length));
    }

    [Fact]
    public async Task HandsBackWhatItHasFoundBeforeTheChapterEnds()
    {
        var (_, aligner, request) = Build();

        var saved = new List<ChapterSyncMap>();

        await aligner.AlignAsync(
            request,
            checkpoint: partial =>
            {
                saved.Add(partial);
                return Task.CompletedTask;
            });

        // An hour of audio is sixty probes, so several check-ins along the way — the point being
        // that stopping at any of them leaves a usable map rather than nothing.
        Assert.NotEmpty(saved);
        Assert.All(saved, m => Assert.NotEmpty(m.Anchors));

        // Each one carries at least what the one before it did: this is progress, not a snapshot
        // that can shrink.
        for (var i = 1; i < saved.Count; i++)
            Assert.True(saved[i].Anchors.Count >= saved[i - 1].Anchors.Count);
    }

    [Fact]
    public async Task DoesNotListenAgainToWhatAPreviousRunAlreadyHeard()
    {
        var (firstNarration, firstAligner, request) = Build();

        var interrupted = new List<ChapterSyncMap>();

        await firstAligner.AlignAsync(
            request,
            checkpoint: partial =>
            {
                interrupted.Add(partial);
                return Task.CompletedTask;
            });

        // Whatever the first run had at its earliest check-in: the state a stopped chapter leaves.
        var partway = interrupted[0];

        var (secondNarration, secondAligner, _) = Build();
        await secondAligner.AlignAsync(request, resumeFrom: partway);

        // The second run skips the stretch the first one covered, so it transcribes less audio.
        Assert.True(
            secondNarration.TranscribeCalls < firstNarration.TranscribeCalls,
            $"resumed run made {secondNarration.TranscribeCalls} probes, fresh run made {firstNarration.TranscribeCalls}");
    }

    [Fact]
    public async Task KeepsTheAnchorsItResumedFrom()
    {
        var (_, aligner, request) = Build();

        var earlier = ChapterSyncMap.FromAnchors(0,
        [
            new Anchor(1_000, 40, 0.9f),
            new Anchor(2_000, 90, 0.9f),
        ]);

        var map = await aligner.AlignAsync(request, resumeFrom: earlier);

        // Both survive into the finished map: continuing a chapter must not discard the reason it
        // was worth continuing.
        Assert.Contains(map.Anchors, a => a.AudioMs == 1_000 && a.CharOffset == 40);
        Assert.Contains(map.Anchors, a => a.AudioMs == 2_000 && a.CharOffset == 90);
    }
}
