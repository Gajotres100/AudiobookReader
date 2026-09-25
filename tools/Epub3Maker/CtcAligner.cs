using System.Globalization;
using System.Text;
using AudioBookReader.Core.Books;

namespace Epub3Maker;

/// <summary>When one sentence is spoken.</summary>
public readonly record struct SentenceTime(long BeginMs, long EndMs);

/// <summary>How the alignment went, for the summary and for judging whether to trust it.</summary>
public record CtcReport(int Anchors, int Segments, int SkippedSegments, int TimedSentences, int Sentences);

/// <summary>
/// Lines the book's own text up with the narration, letter by letter.
///
/// Two passes over what the model heard. The first reads it greedily — a rough, misspelt
/// transcript — and finds runs of ten letters that occur exactly once in the book and exactly once
/// in that transcript; the longest chain of them that moves forward through both is where the book
/// and the recording agree, and it copes with whatever the recording adds or leaves out: credits,
/// a skipped foreword, a chapter read in a different place. The second pass takes the text between
/// consecutive anchors and finds, with the Viterbi algorithm, the single most likely moment for
/// every one of its letters — which is forced alignment proper, and exact to the 20 ms frame.
///
/// Sentences get their times from their first and last letters. Nothing is interpolated.
/// </summary>
public static class CtcAligner
{
    /// <summary>Letters in a run that has to match exactly to count as an anchor.</summary>
    private const int K = 10;

    /// <summary>The "anything else" token between sentences and in place of numbers.</summary>
    private const int Star = MmsModel.VocabularySize;

    /// <summary>
    /// How much less likely than the best real letter the wildcard is. Enough that it never takes a
    /// letter's place when the letter is plainly there, little enough that it soaks up what the
    /// narrator says that the text does not — "Chapter Seven", a number read out, a sneeze.
    /// </summary>
    private const float StarPenalty = 2.0f;

    /// <summary>Beyond this the text between two anchors is taken not to be narrated as written.</summary>
    private const int MaxSegmentFrames = 10 * 60 * 1000 / MmsModel.FrameMs;

    /// <summary>Letters per second a narrator plausibly reads at; outside it the segment is not trusted.</summary>
    private const double MinLettersPerSecond = 4, MaxLettersPerSecond = 30;

    public static (Dictionary<int, SentenceTime> Times, CtcReport Report) Align(
        BookText text, float[] emissions, Action<string> log)
    {
        var frameCount = emissions.Length / MmsModel.VocabularySize;

        var tokens = Tokenize(text);
        var heard = GreedyDecode(emissions, frameCount);

        var anchors = FindAnchors(tokens, heard);
        log($"ctc: {tokens.Ids.Count} text tokens, {heard.Count} heard letters, {anchors.Count} anchors");

        var firstFrame = new int[tokens.Ids.Count];
        var lastFrame = new int[tokens.Ids.Count];
        Array.Fill(firstFrame, -1);
        Array.Fill(lastFrame, -1);

        var segments = 0;
        var skipped = 0;

        // Segments between anchors, merged until each covers at least ~20 s so the Viterbi has room
        // to be exact at both ends, and split nowhere else.
        var cut = new List<(int Token, int Frame)>();
        foreach (var anchor in anchors)
        {
            if (cut.Count == 0 || anchor.Frame - cut[^1].Frame >= 1000) cut.Add(anchor);
        }

        for (var i = 0; i + 1 < cut.Count; i++)
        {
            var (fromToken, fromFrame) = cut[i];
            var (toToken, toFrame) = cut[i + 1];

            // An anchor is where its letter was first heard; the letter starts a frame or so before.
            fromFrame = Math.Max(0, fromFrame - 1);
            toFrame = Math.Max(fromFrame + 1, toFrame - 1);

            var letters = CountLetters(tokens, fromToken, toToken);
            var seconds = (toFrame - fromFrame) * MmsModel.FrameMs / 1000.0;
            var rate = letters / Math.Max(seconds, 0.001);

            if (toFrame - fromFrame > MaxSegmentFrames || rate < MinLettersPerSecond || rate > MaxLettersPerSecond)
            {
                skipped++;
                log($"ctc: skipped tokens {fromToken}..{toToken} over {seconds:0.0} s ({rate:0.0} letters/s)");
                continue;
            }

            if (AlignSegment(emissions, tokens, fromToken, toToken, fromFrame, toFrame, firstFrame, lastFrame))
                segments++;
            else
                skipped++;
        }

        // The opening and the close, each against the edge of the recording, with a wildcard on the
        // outer side for the credits read there. Only when the text is plausibly all narrated:
        // front matter nobody reads aloud shows up as far too many letters for the time.
        if (cut.Count > 0)
        {
            var (headToken, headFrame) = cut[0];
            if (headToken > 0 && Plausible(CountLetters(tokens, 0, headToken), headFrame, lenient: true)
                && AlignSegment(emissions, tokens, 0, headToken, 0, Math.Max(1, headFrame - 1), firstFrame, lastFrame, starBefore: true))
                segments++;

            var (tailToken, tailFrame) = cut[^1];
            if (tailToken < tokens.Ids.Count && Plausible(CountLetters(tokens, tailToken, tokens.Ids.Count), frameCount - tailFrame, lenient: true)
                && AlignSegment(emissions, tokens, tailToken, tokens.Ids.Count, Math.Max(0, tailFrame - 1), frameCount, firstFrame, lastFrame, starAfter: true))
                segments++;
        }

        var times = SentenceTimes(text, tokens, firstFrame, lastFrame);
        return (times, new CtcReport(anchors.Count, segments, skipped, times.Count, text.Sentences.Count));
    }

    /// <summary>
    /// Whether this many letters fit this many frames at a narrator's pace. Lenient where a
    /// wildcard at the edge can take up any amount of extra audio: then only too fast is refused.
    /// </summary>
    private static bool Plausible(int letters, int frames, bool lenient)
    {
        var rate = letters / Math.Max(frames * MmsModel.FrameMs / 1000.0, 0.001);
        return rate <= MaxLettersPerSecond && (lenient || rate >= MinLettersPerSecond);
    }

    // ---- The text, as the model's letters ----

    public sealed class Tokens
    {
        public List<int> Ids { get; } = [];

        /// <summary>Where in the book's text each token came from.</summary>
        public List<int> Offsets { get; } = [];

        /// <summary>Token index of each sentence's first token, for placing wildcards between them.</summary>
        public HashSet<int> SentenceStarts { get; } = [];
    }

    private static readonly int[] LetterIds = BuildLetterIds();

    private static int[] BuildLetterIds()
    {
        var ids = new int[128];
        Array.Fill(ids, -1);
        for (var i = 0; i < MmsModel.Letters.Length; i++) ids[MmsModel.Letters[i]] = MmsModel.FirstLetter + i;
        return ids;
    }

    /// <summary>
    /// The book as model letters: lower case, accents taken off (č is heard as c), everything
    /// else dropped — except numbers, which are read out as words the text does not spell, and so
    /// become a wildcard.
    /// </summary>
    public static Tokens Tokenize(BookText text)
    {
        var tokens = new Tokens();
        var plain = text.PlainText;
        var sentence = 0;
        var inNumber = false;

        for (var i = 0; i < plain.Length; i++)
        {
            while (sentence < text.Sentences.Count && text.Sentences[sentence].Start <= i)
            {
                tokens.SentenceStarts.Add(tokens.Ids.Count);
                sentence++;
            }

            var c = plain[i];

            if (char.IsDigit(c))
            {
                if (!inNumber) Add(tokens, Star, i);
                inNumber = true;
                continue;
            }

            inNumber = false;

            foreach (var letter in Romanize(c))
                if (letter < 128 && LetterIds[letter] >= 0) Add(tokens, LetterIds[letter], i);
        }

        return tokens;
    }

    private static void Add(Tokens tokens, int id, int offset)
    {
        tokens.Ids.Add(id);
        tokens.Offsets.Add(offset);
    }

    private static string Romanize(char c) => c switch
    {
        'đ' or 'Đ' => "d",
        'ß' => "ss",
        'æ' or 'Æ' => "ae",
        'œ' or 'Œ' => "oe",
        'ø' or 'Ø' => "o",
        'ł' or 'Ł' => "l",
        'þ' or 'Þ' => "th",
        '’' or '‘' or 'ʼ' => "'",
        _ when c < 128 => char.ToLowerInvariant(c).ToString(),
        _ => StripMarks(char.ToLowerInvariant(c).ToString().Normalize(NormalizationForm.FormKD)),
    };

    private static string StripMarks(string s) =>
        string.Concat(s.Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark));

    private static int CountLetters(Tokens tokens, int from, int to)
    {
        var count = 0;
        for (var i = from; i < to; i++) if (tokens.Ids[i] != Star) count++;
        return count;
    }

    // ---- What was heard ----

    private readonly record struct Heard(int Id, int Frame);

    /// <summary>The most likely letter at each frame, repeats and blanks collapsed: a rough transcript.</summary>
    private static List<Heard> GreedyDecode(float[] emissions, int frameCount)
    {
        var heard = new List<Heard>(frameCount / 4);
        var previous = MmsModel.Blank;

        for (var f = 0; f < frameCount; f++)
        {
            var row = emissions.AsSpan(f * MmsModel.VocabularySize, MmsModel.VocabularySize);
            var best = 0;
            for (var v = 1; v < row.Length; v++) if (row[v] > row[best]) best = v;

            if (best != previous && best >= MmsModel.FirstLetter) heard.Add(new Heard(best, f));
            previous = best;
        }

        return heard;
    }

    // ---- Where the book and the recording agree ----

    /// <summary>
    /// Runs of <see cref="K"/> letters unique in both, chained in the order both agree on — the
    /// longest such chain, found in n log n.
    /// </summary>
    private static List<(int Token, int Frame)> FindAnchors(Tokens tokens, List<Heard> heard)
    {
        // Text letters only; wildcards are not heard as anything in particular.
        var letterTokens = new List<int>(tokens.Ids.Count);
        for (var i = 0; i < tokens.Ids.Count; i++) if (tokens.Ids[i] != Star) letterTokens.Add(i);

        var textRuns = UniqueRuns(letterTokens.Count, i => tokens.Ids[letterTokens[i]]);
        var heardRuns = UniqueRuns(heard.Count, i => heard[i].Id);

        var pairs = new List<(int Text, int Heard)>();
        foreach (var (key, heardAt) in heardRuns)
            if (textRuns.TryGetValue(key, out var textAt)) pairs.Add((textAt, heardAt));

        pairs.Sort((a, b) => a.Heard.CompareTo(b.Heard));

        // Longest strictly increasing run of text positions, in the order heard.
        var tails = new List<int>();          // index into pairs of the smallest tail of each length
        var previous = new int[pairs.Count];

        for (var i = 0; i < pairs.Count; i++)
        {
            int lo = 0, hi = tails.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (pairs[tails[mid]].Text < pairs[i].Text) lo = mid + 1; else hi = mid;
            }

            previous[i] = lo > 0 ? tails[lo - 1] : -1;
            if (lo == tails.Count) tails.Add(i); else tails[lo] = i;
        }

        var chain = new List<(int Token, int Frame)>();
        for (var i = tails.Count > 0 ? tails[^1] : -1; i >= 0; i = previous[i])
            chain.Add((letterTokens[pairs[i].Text], heard[pairs[i].Heard].Frame));

        chain.Reverse();
        return DropImplausible(tokens, chain);
    }

    /// <summary>Each run of letters that occurs exactly once, by where it starts.</summary>
    private static Dictionary<ulong, int> UniqueRuns(int count, Func<int, int> idAt)
    {
        var first = new Dictionary<ulong, int>();
        var repeated = new HashSet<ulong>();

        ulong key = 0;
        const ulong mask = (1UL << (5 * K)) - 1;

        for (var i = 0; i < count; i++)
        {
            key = ((key << 5) | (uint)(idAt(i) - MmsModel.FirstLetter)) & mask;
            if (i < K - 1) continue;

            if (!first.TryAdd(key, i - K + 1)) repeated.Add(key);
        }

        foreach (var key2 in repeated) first.Remove(key2);
        return first;
    }

    /// <summary>
    /// Drops an anchor whose neighbours on both sides imply a reading speed no narrator has — the
    /// signature of a run that matched by chance somewhere it does not belong.
    /// </summary>
    private static List<(int Token, int Frame)> DropImplausible(Tokens tokens, List<(int Token, int Frame)> chain)
    {
        bool Plausible((int Token, int Frame) a, (int Token, int Frame) b)
        {
            var seconds = (b.Frame - a.Frame) * MmsModel.FrameMs / 1000.0;
            if (seconds < 2) return true;   // too close to judge
            var rate = CountLetters(tokens, a.Token, b.Token) / seconds;
            return rate is >= MinLettersPerSecond and <= MaxLettersPerSecond;
        }

        for (var pass = 0; pass < 3; pass++)
        {
            var kept = new List<(int Token, int Frame)>(chain.Count);

            for (var i = 0; i < chain.Count; i++)
            {
                var badBefore = i > 0 && !Plausible(chain[i - 1], chain[i]);
                var badAfter = i + 1 < chain.Count && !Plausible(chain[i], chain[i + 1]);
                if (!(badBefore && badAfter)) kept.Add(chain[i]);
            }

            if (kept.Count == chain.Count) break;
            chain = kept;
        }

        return chain;
    }

    // ---- Forced alignment of one stretch ----

    /// <summary>
    /// The most likely frame for every token between two anchors — CTC Viterbi, with a wildcard
    /// allowed before each sentence. Returns false when the audio is too short to hold the text,
    /// which means the anchors were wrong about this stretch.
    /// </summary>
    private static bool AlignSegment(
        float[] emissions, Tokens tokens, int fromToken, int toToken, int fromFrame, int toFrame,
        int[] firstFrame, int[] lastFrame, bool starBefore = false, bool starAfter = false)
    {
        // The sequence to align: the segment's tokens, with a wildcard slipped in before each
        // sentence (other than the first) that is not already preceded by one.
        var sequence = new List<int>();
        var source = new List<int>();   // token index, or -1 for an inserted wildcard

        if (starBefore)
        {
            sequence.Add(Star);
            source.Add(-1);
        }

        for (var t = fromToken; t < toToken; t++)
        {
            if (t > fromToken && tokens.SentenceStarts.Contains(t) && sequence.Count > 0 && sequence[^1] != Star)
            {
                sequence.Add(Star);
                source.Add(-1);
            }

            sequence.Add(tokens.Ids[t]);
            source.Add(t);
        }

        if (starAfter)
        {
            sequence.Add(Star);
            source.Add(-1);
        }

        var frames = toFrame - fromFrame;
        if (sequence.Count == 0 || frames < sequence.Count * 2) return false;

        var states = sequence.Count * 2 + 1;
        int StateToken(int s) => s % 2 == 0 ? MmsModel.Blank : sequence[s / 2];

        float Emission(int frame, int id)
        {
            var row = emissions.AsSpan((fromFrame + frame) * MmsModel.VocabularySize, MmsModel.VocabularySize);
            if (id != Star) return row[id];

            var best = float.NegativeInfinity;
            for (var v = MmsModel.FirstLetter; v < row.Length; v++) best = Math.Max(best, row[v]);
            return best - StarPenalty;
        }

        const double negative = -1e30;
        var previous = new double[states];
        var current = new double[states];
        var back = new byte[(long)frames * states];   // 0 = stay, 1 = from s-1, 2 = from s-2

        Array.Fill(previous, negative);
        previous[0] = Emission(0, MmsModel.Blank);
        previous[1] = Emission(0, StateToken(1));

        for (var f = 1; f < frames; f++)
        {
            // Only states reachable by now, and able still to finish in time, need computing.
            var low = Math.Max(0, states - 2 * (frames - f) - 1);
            var high = Math.Min(states - 1, 2 * f + 1);

            Array.Fill(current, negative);

            for (var s = low; s <= high; s++)
            {
                var best = previous[s];
                byte from = 0;

                if (s > 0 && previous[s - 1] > best) { best = previous[s - 1]; from = 1; }

                if (s > 1 && s % 2 == 1 && sequence[s / 2] != sequence[s / 2 - 1] && previous[s - 2] > best)
                {
                    best = previous[s - 2];
                    from = 2;
                }

                if (best <= negative) continue;

                current[s] = best + Emission(f, StateToken(s));
                back[(long)f * states + s] = from;
            }

            (previous, current) = (current, previous);
        }

        var state = previous[states - 1] >= previous[states - 2] ? states - 1 : states - 2;
        if (previous[state] <= negative) return false;

        for (var f = frames - 1; f >= 0; f--)
        {
            if (state % 2 == 1 && source[state / 2] is var token and >= 0)
            {
                var frame = fromFrame + f;
                if (lastFrame[token] < 0) lastFrame[token] = frame;
                firstFrame[token] = frame;
            }

            if (f > 0) state -= back[(long)f * states + state];
        }

        return true;
    }

    // ---- Sentences ----

    /// <summary>
    /// Each sentence from its first heard letter to where the next sentence begins, so the
    /// highlight moves straight on — unless the next one starts well after, a pause in which
    /// nothing should stay lit.
    /// </summary>
    private static Dictionary<int, SentenceTime> SentenceTimes(BookText text, Tokens tokens, int[] firstFrame, int[] lastFrame)
    {
        var spans = new List<(int Sentence, long Begin, long End)>();
        var t = 0;

        foreach (var sentence in text.Sentences)
        {
            while (t < tokens.Offsets.Count && tokens.Offsets[t] < sentence.Start) t++;

            int first = -1, last = -1;
            for (var i = t; i < tokens.Offsets.Count && tokens.Offsets[i] < sentence.End; i++)
            {
                if (tokens.Ids[i] == Star || firstFrame[i] < 0) continue;
                if (first < 0) first = firstFrame[i];
                last = lastFrame[i];
            }

            if (first < 0) continue;
            spans.Add((sentence.Index, (long)first * MmsModel.FrameMs, (long)(last + 1) * MmsModel.FrameMs));
        }

        var times = new Dictionary<int, SentenceTime>(spans.Count);
        for (var i = 0; i < spans.Count; i++)
        {
            var (index, begin, end) = spans[i];

            if (i + 1 < spans.Count && spans[i + 1].Begin - end is >= 0 and < 1500)
                end = spans[i + 1].Begin;
            else
                end += 250;

            if (end > begin) times[index] = new SentenceTime(begin, end);
        }

        return times;
    }
}
