namespace AudioBookReader.Core.Alignment;

/// <summary>A stretch of recognized speech and the moment it was spoken, in absolute book time.</summary>
public readonly record struct TranscriptSegment(long StartMs, long EndMs, string Text);

/// <summary>A recognized word, normalized for matching, at the moment it was spoken.</summary>
public readonly record struct TimedWord(string Value, long AtMs);

/// <summary>
/// One timed run of words, as the recognizer broke them up.
///
/// Recognition breaks at phrase boundaries every few seconds, and each break is a moment whose time
/// is known. Locating phrases one at a time rather than the probe as a whole is what lets a long
/// probe be as accurate as a short one: a probe located only at its two ends is a straight line
/// drawn across everything between them, and narration does not travel in straight lines.
/// </summary>
public sealed record TranscriptPhrase(long StartMs, long EndMs, IReadOnlyList<TimedWord> Words);

/// <summary>
/// What one probe heard, with times.
///
/// The times are the point of this type. A probe covers ten seconds and the narrator does not
/// necessarily start speaking at the first of them — audiobooks are full of pauses between
/// paragraphs and scenes — so treating the probe's own start as the moment its first word was
/// spoken puts every anchor out by however long that silence lasted, always in the same direction.
/// Recognition already reports when each segment was spoken; carrying that through costs nothing
/// and removes the error at its source.
/// </summary>
public sealed class Transcript
{
    public required IReadOnlyList<TranscriptSegment> Segments { get; init; }

    /// <summary>Every word of every segment, in order, each carrying the time it was spoken.</summary>
    public required IReadOnlyList<TimedWord> Words { get; init; }

    /// <summary>The same words, kept in the runs the recognizer timed them in.</summary>
    public required IReadOnlyList<TranscriptPhrase> Phrases { get; init; }

    /// <summary>The raw text, for logging and for anything that only wants the words.</summary>
    public string Text => string.Concat(Segments.Select(s => s.Text));

    public static readonly Transcript Empty = new() { Segments = [], Words = [], Phrases = [] };

    public static Transcript FromSegments(IEnumerable<TranscriptSegment> segments)
    {
        var kept = segments.Where(s => s.EndMs >= s.StartMs).ToList();
        var phrases = new List<TranscriptPhrase>();

        foreach (var segment in kept)
        {
            var spoken = TextNormalizer.NormalizeTranscript(segment.Text);
            if (spoken.Count == 0) continue;

            // Recognition times a segment, not a word. Spreading the segment evenly across its
            // words is the best available guess and is wrong by at most a segment's length divided
            // by its word count — a fraction of a second, against the seconds that a probe-start
            // assumption costs.
            var span = segment.EndMs - segment.StartMs;

            var words = new List<TimedWord>(spoken.Count);
            for (var i = 0; i < spoken.Count; i++)
                words.Add(new TimedWord(spoken[i], segment.StartMs + span * i / spoken.Count));

            phrases.Add(new TranscriptPhrase(segment.StartMs, segment.EndMs, words));
        }

        return new Transcript
        {
            Segments = kept,
            Phrases = phrases,
            Words = [.. phrases.SelectMany(p => p.Words)],
        };
    }

    /// <summary>
    /// A transcript from a recognizer that reports no times, spread across the window it covered.
    /// Strictly worse than real timestamps, and here so that such a recognizer still works.
    /// </summary>
    public static Transcript FromText(string text, long startMs, long durationMs) =>
        FromSegments([new TranscriptSegment(startMs, startMs + durationMs, text)]);
}

/// <summary>
/// Turns a stretch of an audiobook into timed words.
///
/// The interface is deliberately this coarse. Decoding the container, resampling to what the model
/// wants and running recognition are all one platform concern — on Android that is MediaCodec
/// feeding whisper.cpp — and none of it changes how alignment works. Keeping the seam here is what
/// lets the whole alignment pipeline be tested without an audio file, a model, or a phone.
/// </summary>
public interface ITranscriber
{
    Task<Transcript> TranscribeAsync(string audioPath, long startMs, long durationMs, CancellationToken ct = default);
}

/// <summary>
/// Lets the host slow alignment down between units of work.
///
/// This is where the CPU budget lives: the Android implementation duty-cycles, keeps the worker on
/// the little cores, and backs off when the device gets warm. Core calls it and asks no questions,
/// so the policy can change without touching the algorithm.
/// </summary>
public interface IWorkThrottle
{
    Task ThrottleAsync(CancellationToken ct = default);
}

/// <summary>A throttle that never waits, for tests and for the desktop harness.</summary>
public sealed class NoThrottle : IWorkThrottle
{
    public static readonly NoThrottle Instance = new();

    public Task ThrottleAsync(CancellationToken ct = default) => Task.CompletedTask;
}
