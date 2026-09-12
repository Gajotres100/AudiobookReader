using System.Text;
using AudioBookReader.Core.Alignment;

namespace AudioBookReader.Core.Tests;

/// <summary>
/// Stands in for the recognizer by "narrating" a known text at a known rate, so tests can compare
/// what alignment concluded against the truth it was never told.
/// </summary>
/// <param name="introMs">
/// Audio at the start that is not in the book at all — the publisher's imprint, the narrator's
/// credits, a spoken chapter number. Real audiobooks open with it and nothing can align it.
/// </param>
/// <param name="paceVariation">
/// How much the narrator's speed wanders, as a fraction. Real narration is not metronomic — it
/// slows for description and quickens through dialogue — and that wandering is exactly what
/// interpolating between distant anchors gets wrong. A perfectly even reader would let a sparse
/// map look far better than it is.
/// </param>
/// <param name="pauseMs">
/// Silence before the narrator resumes, at the start of every probe. Audiobooks are full of it —
/// between paragraphs, at scene breaks, after dialogue — and a probe is as likely to land in one as
/// anywhere else. A fake that starts speaking the instant it is asked hides the error this causes.
/// </param>
public sealed class FakeNarration(
    string text,
    long durationMs,
    double noise = 0,
    int seed = 7,
    long introMs = 0,
    double paceVariation = 0,
    long pauseMs = 0)
    : ITranscriber
{
    private readonly Random _random = new(seed);

    public string Text { get; } = text;

    public long DurationMs { get; } = durationMs;

    public long IntroMs { get; } = introMs;

    /// <summary>Where the narrator truly is at a given moment.</summary>
    public int CharAt(long ms)
    {
        if (ms <= IntroMs) return 0;

        var elapsed = Math.Min(ms - IntroMs, DurationMs - IntroMs);
        var progress = elapsed / (double)(DurationMs - IntroMs);

        // Warped so the pace ebbs and flows while staying monotonic and ending where it started.
        const double cycles = 5;
        var warped = progress + paceVariation * Math.Sin(2 * Math.PI * cycles * progress) / (2 * Math.PI * cycles);

        return Math.Clamp((int)Math.Round(Math.Clamp(warped, 0, 1) * Text.Length), 0, Text.Length);
    }

    /// <summary>How many probes were asked for; lets tests assert that most of the audio is skipped.</summary>
    public int TranscribeCalls { get; private set; }

    /// <summary>How long the narrator stays silent at the start of each probe.</summary>
    public long PauseMs { get; } = pauseMs;

    /// <summary>
    /// How long a stretch the recognizer times as one segment. whisper breaks at phrase
    /// boundaries, which land every few seconds, so a longer probe yields proportionally more
    /// segments rather than the same number of longer ones.
    /// </summary>
    private const long SegmentMs = 3_500;

    public Task<Transcript> TranscribeAsync(string audioPath, long startMs, long durationMs, CancellationToken ct = default)
    {
        TranscribeCalls++;

        var endMs = startMs + durationMs;

        if (endMs <= IntroMs)
            return Task.FromResult(Transcript.FromText(
                "This is Audible. Blackwing, written by Ed McDonald, narrated by Colin Mace.",
                startMs,
                durationMs));

        // Nothing is said until the pause is over, and nothing before the intro ends belongs to
        // the book at all.
        var speechStart = Math.Max(startMs + PauseMs, IntroMs);
        if (endMs <= speechStart) return Task.FromResult(Transcript.Empty);

        var segments = new List<TranscriptSegment>();
        var count = (int)Math.Max(1, (endMs - speechStart) / SegmentMs);

        for (var i = 0; i < count; i++)
        {
            var from = speechStart + (endMs - speechStart) * i / count;
            var to = speechStart + (endMs - speechStart) * (i + 1) / count;

            var heard = Text[CharAt(from)..CharAt(to)];
            if (heard.Length == 0) continue;

            // WhisperTranscriber never calls .WithProbabilities(), so every real segment's
            // Probability is 0 in production. Reporting the record's own default of 1 here would
            // have hidden the regression where ChapterAligner multiplied confidence by this field
            // and silently zeroed out every real anchor a whole-book run ever produced.
            segments.Add(new TranscriptSegment(
                from, to, noise > 0 ? Corrupt(heard) : heard, Probability: 0f));
        }

        return Task.FromResult(Transcript.FromSegments(segments));
    }

    /// <summary>
    /// Damages the transcript the way recognition does: substituted, dropped and invented words.
    /// </summary>
    private string Corrupt(string heard)
    {
        var words = heard.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var result = new StringBuilder();

        foreach (var word in words)
        {
            if (_random.NextDouble() < noise)
            {
                switch (_random.Next(3))
                {
                    case 0: continue;                                  // dropped
                    case 1: result.Append("mumble "); continue;        // misheard
                    default: result.Append(word).Append(" and ");      // invented filler
                        continue;
                }
            }

            result.Append(word).Append(' ');
        }

        return result.ToString();
    }

    /// <summary>
    /// Builds prose that never repeats itself closely enough to be genuinely ambiguous, which is
    /// what a real novel is like and what makes a windowed search meaningful.
    /// </summary>
    public static string GenerateProse(int sentenceCount, int seed = 42)
    {
        string[] vocabulary =
        [
            "morning", "window", "frost", "kitchen", "letter", "harbour", "lantern", "orchard",
            "stairs", "shoulder", "whisper", "carriage", "meadow", "thunder", "candle", "ribbon",
            "sailor", "market", "brother", "silence", "gravel", "chimney", "pocket", "blanket",
            "cousin", "railway", "pasture", "envelope", "shutter", "hollow", "cellar", "bramble",
            "walked", "waited", "remembered", "opened", "carried", "listened", "counted", "folded",
            "returned", "noticed", "answered", "climbed", "gathered", "watched", "buried", "raised",
            "quiet", "narrow", "distant", "bitter", "yellow", "hollow", "restless", "crooked",
            "ancient", "gentle", "sudden", "faded", "solemn", "brittle", "weathered", "uneasy",
            "beneath", "beyond", "across", "toward", "within", "against", "beside", "behind",
            "and", "but", "though", "while", "because", "until", "before", "after",
            "the", "a", "her", "his", "their", "that", "which", "some",
        ];

        var random = new Random(seed);
        var prose = new StringBuilder();

        for (var s = 0; s < sentenceCount; s++)
        {
            var length = random.Next(9, 19);
            var sentence = new StringBuilder();

            for (var w = 0; w < length; w++)
            {
                if (w > 0) sentence.Append(' ');
                sentence.Append(vocabulary[random.Next(vocabulary.Length)]);
            }

            sentence[0] = char.ToUpperInvariant(sentence[0]);
            prose.Append(sentence).Append(". ");

            if (s % 6 == 5) prose.Append('\n');
        }

        return prose.ToString().Trim();
    }
}
