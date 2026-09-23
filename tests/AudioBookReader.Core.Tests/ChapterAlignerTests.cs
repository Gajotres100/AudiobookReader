using AudioBookReader.Core.Alignment;
using AudioBookReader.Core.Models;

namespace AudioBookReader.Core.Tests;

public class ChapterAlignerTests
{
    private const long ChapterMs = 30 * 60 * 1_000;

    private static (FakeNarration Narration, ChapterAligner Aligner, ChapterAlignmentRequest Request) Build(
        double noise = 0,
        AlignmentSettings? settings = null)
    {
        var prose = FakeNarration.GenerateProse(sentenceCount: 400);
        var narration = new FakeNarration(prose, ChapterMs, noise);
        var aligner = new ChapterAligner(TokenizedText.Create(prose), narration, settings);

        var request = new ChapterAlignmentRequest("book.m4b", 0, 0, ChapterMs, 0, prose.Length);
        return (narration, aligner, request);
    }

    /// <summary>Error in characters between the map and the truth, sampled across the chapter.</summary>
    private static List<int> SampleErrors(ChapterSyncMap map, FakeNarration narration, int samples = 200)
    {
        var errors = new List<int>(samples);

        for (var i = 0; i < samples; i++)
        {
            var at = ChapterMs * i / samples;
            Assert.True(map.TryGetCharOffset(at, out var predicted));
            errors.Add(Math.Abs(predicted - narration.CharAt(at)));
        }

        return errors;
    }

    /// <summary>
    /// Error keeping its sign, which is the only way to see a bias.
    ///
    /// Absolute error hides the difference between a map that wobbles either side of the truth and
    /// one that sits consistently ahead of it — and only the second is visible to a reader, as a
    /// highlight that is always the same amount wrong in the same direction.
    /// </summary>
    private static double MeanBias(ChapterSyncMap map, FakeNarration narration, int samples = 200)
    {
        var signed = new List<int>(samples);

        for (var i = 0; i < samples; i++)
        {
            var at = ChapterMs * i / samples;
            Assert.True(map.TryGetCharOffset(at, out var predicted));
            signed.Add(predicted - narration.CharAt(at));
        }

        return signed.Average();
    }

    private static int Percentile(List<int> values, double percentile)
    {
        var sorted = values.Order().ToList();
        return sorted[Math.Clamp((int)(sorted.Count * percentile), 0, sorted.Count - 1)];
    }

    // ---- Accuracy ----

    [Fact]
    public async Task PlacesAnchorsWhereTheNarratorActuallyIs()
    {
        var (narration, aligner, request) = Build();

        var map = await aligner.AlignAsync(request);

        var probeAnchors = map.Anchors.Where(a => a.Confidence > 0.5f).ToList();
        Assert.NotEmpty(probeAnchors);

        foreach (var anchor in probeAnchors)
        {
            // A word is about six characters; being within a couple of words is exact enough.
            Assert.InRange(Math.Abs(anchor.CharOffset - narration.CharAt(anchor.AudioMs)), 0, 40);
        }
    }

    [Fact]
    public async Task InterpolatesBetweenAnchorsCloselyEnoughToFollowAlong()
    {
        var (narration, aligner, request) = Build();

        var map = await aligner.AlignAsync(request);
        var errors = SampleErrors(map, narration);

        // Measured at 4 characters, well inside the five seconds the plan asks for before
        // refinement. The bound is set an order of magnitude above that to catch a regression
        // rather than to record today's number.
        Assert.True(Percentile(errors, 0.95) < 40, $"p95 error was {Percentile(errors, 0.95)} characters");
    }

    [Fact]
    public async Task KeepsUpWithANarratorWhosePaceWanders()
    {
        // The reader notices this one: an error of a couple of seconds is a whole sentence, and it
        // shows as the highlight sitting behind the voice. It comes entirely from interpolating
        // across the gap between anchors, so it is the probe schedule that has to answer for it.
        var prose = FakeNarration.GenerateProse(sentenceCount: 400);
        var narration = new FakeNarration(prose, ChapterMs, paceVariation: 0.12);
        var aligner = new ChapterAligner(TokenizedText.Create(prose), narration);

        var map = await aligner.AlignAsync(new ChapterAlignmentRequest("book.m4b", 0, 0, ChapterMs, 0, prose.Length));
        var errors = SampleErrors(map, narration);

        // Measured at 26 characters - under two seconds, which reads as synchronized.
        Assert.True(Percentile(errors, 0.95) < 45, $"p95 error was {Percentile(errors, 0.95)} characters");
    }

    [Fact]
    public async Task DoesNotRunAheadWhenTheNarratorPausesBeforeSpeaking()
    {
        // Audiobooks are full of silence — between paragraphs, at scene breaks, after dialogue —
        // and a probe lands in one as readily as anywhere else. Timing that probe's anchor by when
        // it started rather than by when the narrator did claims the reader is further along than
        // they are, by the length of the pause, every time and in the same direction.
        //
        // Measured with three-second pauses: median 7 characters, bias +2. Timing anchors from the
        // probe's own start instead gave median 67 and a bias of +64 — four seconds ahead, all
        // chapter long, which is what a highlight sitting a sentence off the voice looks like.
        var prose = FakeNarration.GenerateProse(sentenceCount: 400);
        var narration = new FakeNarration(prose, ChapterMs, paceVariation: 0.12, pauseMs: 3_000);
        var aligner = new ChapterAligner(TokenizedText.Create(prose), narration);

        var map = await aligner.AlignAsync(new ChapterAlignmentRequest("book.m4b", 0, 0, ChapterMs, 0, prose.Length));

        Assert.InRange(MeanBias(map, narration), -20, 20);
        Assert.True(Percentile(SampleErrors(map, narration), 0.95) < 45);
    }

    [Fact]
    public async Task PinsBothEndsOfEveryProbeRatherThanOnlyItsStart()
    {
        // A ten-second probe measures a stretch of the book, not a point in it. Recording where the
        // match ended as well as where it began doubles the anchors for no extra recognition, and
        // the second one lands inside the gap the first would have left to interpolation.
        var (_, aligner, request) = Build();

        var map = await aligner.AlignAsync(request);

        var probeAnchors = map.Anchors.Count(a => a.Confidence > 0.5f);
        var probes = ProbePlanner.Plan(request.AudioStartMs, request.AudioEndMs, new AlignmentSettings())
            .Count(p => !p.IsRunIn);

        Assert.True(probeAnchors >= probes * 2 - 2, $"{probeAnchors} anchors from {probes} probes");
    }

    [Fact]
    public async Task StillFollowsAlongWhenRecognitionIsNoisy()
    {
        var (narration, aligner, request) = Build(noise: 0.25);

        var map = await aligner.AlignAsync(request);
        var errors = SampleErrors(map, narration);

        // Measured at 15 characters — about a second of narration — with a quarter of the words
        // dropped, misheard or invented.
        Assert.True(Percentile(errors, 0.95) < 120, $"p95 error was {Percentile(errors, 0.95)} characters");
    }

    [Fact]
    public async Task AnchorsTheStartAndEndOfTheChapter()
    {
        var (narration, aligner, request) = Build();

        var map = await aligner.AlignAsync(request);

        Assert.True(map.TryGetCharOffset(0, out var atStart));
        Assert.InRange(atStart, 0, 40);

        Assert.True(map.TryGetCharOffset(ChapterMs, out var atEnd));
        Assert.InRange(atEnd, narration.Text.Length - 400, narration.Text.Length);
    }

    // ---- Cost ----

    [Fact]
    public async Task ListensToOnlyAFractionOfTheAudio()
    {
        var settings = new AlignmentSettings();
        var (narration, aligner, request) = Build(settings: settings);

        await aligner.AlignAsync(request);

        // A quarter rather than a third: the shipped spacing is 60 s, per the measured table on
        // AlignmentSettings.ProbeIntervalMs — the bound here tracks whatever that default
        // actually is, not a number picked once and left behind by a later retune.
        var listenedMs = narration.TranscribeCalls * settings.ProbeDurationMs;
        var coverage = settings.ProbeDurationMs / (double)settings.ProbeIntervalMs;
        Assert.True(
            listenedMs < ChapterMs * (coverage + 0.05),
            $"transcribed {listenedMs}ms of {ChapterMs}ms, expected under {coverage:P0} plus slack");
    }

    // ---- Robustness ----

    [Fact]
    public async Task PinsTheOpeningOfAChapterThatBeginsWithAnIntroThatIsNotInTheBook()
    {
        // An audiobook opens with the publisher's imprint and the narrator's credits, none of it in
        // the text. A single long probe at the start therefore matches nothing, leaves the opening
        // unanchored, and the first minutes are left to interpolation — which is where the reader
        // looks first and where drift is most obvious.
        const long introMs = 45_000;

        var prose = FakeNarration.GenerateProse(sentenceCount: 400);
        var narration = new FakeNarration(prose, ChapterMs, introMs: introMs);
        var aligner = new ChapterAligner(TokenizedText.Create(prose), narration);

        var map = await aligner.AlignAsync(new ChapterAlignmentRequest("book.m4b", 0, 0, ChapterMs, 0, prose.Length));

        var early = map.Anchors
            .Where(a => a.Confidence > 0.5f && a.AudioMs < introMs + 60_000)
            .ToList();

        Assert.NotEmpty(early);

        // The anchor is recorded at the start of its probe, so it can only be as accurate as the
        // probe is short — ten seconds of narration is about 150 characters.
        foreach (var anchor in early)
            Assert.InRange(Math.Abs(anchor.CharOffset - narration.CharAt(anchor.AudioMs)), 0, 200);
    }

    [Fact]
    public async Task RecoversWhenTheChapterIsThoughtToStartInTheWrongPlace()
    {
        // The estimate a chapter inherits can be thousands of characters out — on a real book this
        // put the true position outside the search window, so every probe missed, the chapter ended
        // with no anchors, and the next chapter inherited an even worse guess. The search has to
        // widen after failures or one bad estimate derails the rest of the book.
        var prose = FakeNarration.GenerateProse(sentenceCount: 400);
        var narration = new FakeNarration(prose, ChapterMs);
        var aligner = new ChapterAligner(TokenizedText.Create(prose), narration);

        // Claims the chapter starts a fifth of the way into a text it actually starts at zero.
        var misled = new ChapterAlignmentRequest("book.m4b", 0, 0, ChapterMs, prose.Length / 5, prose.Length);

        var map = await aligner.AlignAsync(misled);

        var probeAnchors = map.Anchors.Where(a => a.Confidence > 0.5f).ToList();
        Assert.NotEmpty(probeAnchors);

        foreach (var anchor in probeAnchors)
            Assert.InRange(Math.Abs(anchor.CharOffset - narration.CharAt(anchor.AudioMs)), 0, 40);
    }

    [Fact]
    public async Task PinsChapterOneOnItsFirstProbePastFrontMatterTheAudioSkips()
    {
        // Contents, maps and notes open the text; the narration starts at chapter one. The
        // estimate is the start of the text, well beyond reach of the windowed search, and both
        // stepping the radius out and the old "search everything after three misses" left the
        // first run-in probes unanchored — and the opening drawn as a line from the start of the
        // contents page to the first real match, which reads as the highlight racing through it.
        var frontMatter = FakeNarration.GenerateProse(sentenceCount: 1_400, seed: 99);
        var prose = FakeNarration.GenerateProse(sentenceCount: 600);
        var text = frontMatter + "\n" + prose;
        var offset = frontMatter.Length + 1;

        var narration = new FakeNarration(prose, ChapterMs);
        var aligner = new ChapterAligner(TokenizedText.Create(text), narration);

        var map = await aligner.AlignAsync(new ChapterAlignmentRequest("book.m4b", 0, 0, ChapterMs, 0, text.Length));

        var real = map.Anchors.Where(a => a.Confidence > 0.5f).ToList();
        Assert.NotEmpty(real);

        // Found by the very first probe, not after a string of misses.
        Assert.True(real.Min(a => a.AudioMs) < 10_000, $"first real anchor at {real.Min(a => a.AudioMs)} ms");

        foreach (var anchor in real)
            Assert.InRange(Math.Abs(anchor.CharOffset - (offset + narration.CharAt(anchor.AudioMs))), 0, 40);

        // And the chapter's first moment points at chapter one, not at the contents page.
        Assert.True(map.TryGetCharOffset(0, out var atStart));
        Assert.InRange(atStart, offset - 200, offset + 200);
    }

    [Fact]
    public void RefusesAWholeBookMatchThatFitsTwoPlacesAlmostEqually()
    {
        // A passage that occurs twice is exactly the case where searching everywhere would place
        // the narrator confidently and wrongly. Better to find nothing and keep listening.
        var passage = FakeNarration.GenerateProse(sentenceCount: 3, seed: 5);
        var text =
            FakeNarration.GenerateProse(sentenceCount: 200, seed: 1) + " " + passage + " " +
            FakeNarration.GenerateProse(sentenceCount: 200, seed: 2) + " " + passage + " " +
            FakeNarration.GenerateProse(sentenceCount: 200, seed: 3);

        var heard = Transcript.FromText(passage, 0, 10_000);

        Assert.Null(TranscriptMatcher.MatchAnywhere(TokenizedText.Create(text), heard, minWords: 10, minConfidence: 0.6f));

        var once = FakeNarration.GenerateProse(sentenceCount: 200, seed: 1) + " " + passage + " " +
                   FakeNarration.GenerateProse(sentenceCount: 200, seed: 2);

        Assert.NotNull(TranscriptMatcher.MatchAnywhere(TokenizedText.Create(once), heard, minWords: 10, minConfidence: 0.6f));
    }

    [Fact]
    public async Task DiscardsProbesThatDoNotMatchInsteadOfTrustingThem()
    {
        var prose = FakeNarration.GenerateProse(sentenceCount: 300);
        var aligner = new ChapterAligner(TokenizedText.Create(prose), new SilentTranscriber());

        var map = await aligner.AlignAsync(new ChapterAlignmentRequest("book.m4b", 0, 0, ChapterMs, 0, prose.Length));

        // Only the two weak boundary anchors survive; nothing was invented from the noise.
        Assert.All(map.Anchors, a => Assert.True(a.Confidence <= 0.2f));
    }

    [Fact]
    public async Task ReportsProgressForEveryProbe()
    {
        var (_, aligner, request) = Build();
        var reports = new List<AlignmentProgress>();

        await aligner.AlignAsync(request, new Progress<AlignmentProgress>(reports.Add));

        // Progress<T> marshals asynchronously, so allow the queued callbacks to drain.
        await Task.Delay(50);

        Assert.NotEmpty(reports);
        Assert.All(reports, r => Assert.Equal(0, r.ChapterIndex));
        Assert.Equal(reports[^1].ProbeCount, reports[^1].ProbesCompleted);
    }

    [Fact]
    public async Task StopsWhenCancelled()
    {
        var (_, aligner, request) = Build();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => aligner.AlignAsync(request, progress: null, cancellation.Token));
    }

    [Fact]
    public async Task HandlesAChapterWithNoDuration()
    {
        var prose = FakeNarration.GenerateProse(sentenceCount: 10);
        var aligner = new ChapterAligner(TokenizedText.Create(prose), new FakeNarration(prose, ChapterMs));

        var map = await aligner.AlignAsync(new ChapterAlignmentRequest("book.m4b", 0, 5_000, 5_000, 0, prose.Length));

        Assert.True(map.IsEmpty);
    }

    private sealed class SilentTranscriber : ITranscriber
    {
        public Task<Transcript> TranscribeAsync(string audioPath, long startMs, long durationMs, CancellationToken ct = default) =>
            Task.FromResult(Transcript.FromText(
                "static hiss unintelligible murmuring background noise nothing recognizable",
                startMs,
                durationMs));
    }
}
