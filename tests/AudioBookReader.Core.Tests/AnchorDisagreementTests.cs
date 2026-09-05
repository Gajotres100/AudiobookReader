using AudioBookReader.Core.Alignment;
using AudioBookReader.Core.Models;

namespace AudioBookReader.Core.Tests;

/// <summary>
/// What happens when a new measurement contradicts one already stored.
///
/// Insertion alone always believes whoever arrived first, which is right for a stray outlier and
/// badly wrong for an early mistake: one confident error would own its stretch of the book forever,
/// and every later session would re-probe it, have everything refused, and give up.
/// </summary>
public class AnchorDisagreementTests
{
    private const long ChapterMs = 10 * 60 * 1_000;

    /// <summary>A transcriber whose first probe is answered with text from somewhere else entirely.</summary>
    private sealed class LiesOnce(FakeNarration truth, string prose) : ITranscriber
    {
        private bool _lied;

        public async Task<Transcript> TranscribeAsync(string path, long startMs, long durationMs, CancellationToken ct = default)
        {
            if (!_lied)
            {
                _lied = true;

                // Words from the far end of the book, delivered as if they were spoken here.
                var from = prose.Length * 4 / 5;
                return Transcript.FromText(prose[from..(from + 250)], startMs, durationMs);
            }

            return await truth.TranscribeAsync(path, startMs, durationMs, ct);
        }
    }

    [Fact]
    public async Task AnEarlyWrongAnchorIsEventuallyOutvotedRatherThanOwningTheChapter()
    {
        var prose = FakeNarration.GenerateProse(sentenceCount: 300);
        var narration = new FakeNarration(prose, ChapterMs, paceVariation: 0.12);

        List<Chapter> chapters =
        [
            new() { Index = 0, StartMs = 0, EndMs = ChapterMs, TextStart = 0, TextEnd = prose.Length },
        ];

        var map = new SyncMap();

        var aligner = new LiveAligner(
            TokenizedText.Create(prose), chapters, new LiesOnce(narration, prose), prose.Length)
        {
            IdleRest = TimeSpan.FromMilliseconds(1),
            MaxLeadMs = 600_000,
        };

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        try
        {
            await aligner.RunAsync(
                "book.m4b", map, () => 0, () => Task.CompletedTask, null, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // Following has no end of its own; the timeout is how this test stops it.
        }

        var chapter = map.ForChapter(0);
        Assert.NotNull(chapter);

        // The truthful anchors, which are the overwhelming majority, must win: the map should read
        // the opening of the chapter as the opening of the book, not as four fifths of the way in.
        Assert.True(chapter.TryGetCharOffset(60_000, out var offset));
        Assert.InRange(offset, 0, prose.Length / 4);
    }

    [Fact]
    public void ALoadedMapThatBreaksTheOrderingIsRepairedRatherThanTrusted()
    {
        // Anchors straight out of JSON have never been checked. Two sharing a character offset make
        // the interpolation divide by zero, and the result is handed to a seek.
        var damaged = new ChapterSyncMap
        {
            ChapterIndex = 0,
            Anchors =
            [
                new Anchor(0, 0, 0.9f),
                new Anchor(20_000, 500, 0.9f),
                new Anchor(30_000, 500, 0.9f),
                new Anchor(10_000, 900, 0.9f),
            ],
        };

        var repaired = ChapterSyncMap.FromAnchors(0, damaged.Anchors);

        for (var i = 1; i < repaired.Anchors.Count; i++)
        {
            Assert.True(repaired.Anchors[i].AudioMs > repaired.Anchors[i - 1].AudioMs);
            Assert.True(repaired.Anchors[i].CharOffset > repaired.Anchors[i - 1].CharOffset);
        }

        Assert.True(repaired.TryGetCharOffset(25_000, out var offset));
        Assert.InRange(offset, 0, 1_000);
    }
}
