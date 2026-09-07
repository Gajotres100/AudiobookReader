namespace AudioBookReader.Core.Alignment;

/// <summary>A stretch of recognized speech and the moment it was spoken, in absolute book time.</summary>
/// <param name="Words">
/// The segment's words with the time each was actually spoken, when the recognizer reported them.
/// Null when it did not, and then the words are placed by spreading the segment evenly — a guess
/// that costs about a second on any anchor taken from the middle of a segment.
/// </param>
/// <param name="Probability">
/// How sure the recognizer was of this segment, 0..1, or 1 when it does not say. Carried through
/// to the anchor's confidence so that a badly heard segment can be outvoted by well heard ones.
/// </param>
/// <param name="NoSpeechProbability">How likely it is that this segment is not speech at all.</param>
public readonly record struct TranscriptSegment(
    long StartMs,
    long EndMs,
    string Text,
    IReadOnlyList<TimedWord>? Words = null,
    float Probability = 1f,
    float NoSpeechProbability = 0f);

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
public sealed record TranscriptPhrase(
    long StartMs,
    long EndMs,
    IReadOnlyList<TimedWord> Words,
    float Probability = 1f);

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

    /// <summary>
    /// Above this, the segment is treated as silence the model talked over rather than speech.
    ///
    /// Small models hallucinate on quiet stretches — an audiobook is full of them between scenes —
    /// and a confident sentence invented over a pause is worse than nothing, because it matches
    /// somewhere and drags the interpolation around it.
    /// </summary>
    private const float NotSpeech = 0.6f;

    public static Transcript FromSegments(IEnumerable<TranscriptSegment> segments)
    {
        var kept = segments.Where(s => s.EndMs >= s.StartMs && s.NoSpeechProbability < NotSpeech).ToList();
        var phrases = new List<TranscriptPhrase>();

        foreach (var segment in kept)
        {
            // Real times when the recognizer gave them; otherwise the old guess, which is still
            // better than nothing and is what the synthetic transcriber in the tests produces.
            var words = segment.Words is { Count: > 0 } timed ? timed : SpreadEvenly(segment);
            if (words.Count == 0) continue;

            phrases.Add(new TranscriptPhrase(
                segment.StartMs, segment.EndMs, words, segment.Probability));
        }

        return new Transcript
        {
            Segments = kept,
            Phrases = phrases,
            Words = [.. phrases.SelectMany(p => p.Words)],
        };
    }

    /// <summary>
    /// Places a segment's words by assuming a constant rate across it.
    ///
    /// Wrong by however long the narrator paused inside the segment, which is the error that
    /// per-word times exist to remove. Kept because a recognizer is not obliged to report them.
    /// </summary>
    private static List<TimedWord> SpreadEvenly(TranscriptSegment segment)
    {
        var spoken = TextNormalizer.NormalizeTranscript(segment.Text);
        if (spoken.Count == 0) return [];

        var span = segment.EndMs - segment.StartMs;

        var words = new List<TimedWord>(spoken.Count);
        for (var i = 0; i < spoken.Count; i++)
            words.Add(new TimedWord(spoken[i], segment.StartMs + span * i / spoken.Count));

        return words;
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
