using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Epub3Maker;

/// <summary>
/// Meta's MMS forced-alignment model: what it hears, as the probability of every letter every
/// 20 milliseconds.
///
/// Trained for exactly this job — lining up a known text with speech — on 1130 languages, Croatian
/// among them, with romanised letters as its alphabet. Unlike a transcriber it does not have to be
/// right about the words; it only has to say how likely each letter is at each moment, and the text
/// we already have does the rest.
///
/// The model is Meta's, released CC-BY-NC 4.0: fine for making your own books, not for selling them.
/// </summary>
public sealed class MmsModel : IDisposable
{
    /// <summary>The model's alphabet; <see cref="Blank"/> is CTC's "nothing new here".</summary>
    public const string Letters = "aienoutsrmkldghybpwcvjzf'qx";

    public const int Blank = 0;
    public const int FirstLetter = 4;
    public const int VocabularySize = 31;

    /// <summary>Samples of 16 kHz audio per output frame: one frame is 20 ms.</summary>
    public const int SamplesPerFrame = 320;

    public const int FrameMs = 20;

    private const string Url = "https://huggingface.co/romara-labs/mms-300m-1130-forced-aligner-ONNX/resolve/main/model.q8.onnx";

    private readonly InferenceSession _session;
    private readonly bool _wantsMask;

    private MmsModel(InferenceSession session)
    {
        _session = session;
        _wantsMask = session.InputMetadata.ContainsKey("attention_mask");
    }

    public static async Task<MmsModel> LoadAsync(CancellationToken ct)
    {
        var path = await Models.EnsureFileAsync(Url, "mms-300m-1130-forced-aligner.q8.onnx", ct);

        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = Environment.ProcessorCount,
        };

        return new MmsModel(new InferenceSession(path, options));
    }

    /// <summary>
    /// Listens to the whole book and returns log-probabilities, <see cref="VocabularySize"/> per
    /// frame, one frame per 20 ms of audio.
    ///
    /// The audio goes through in 30-second windows with a second of context on either side, of
    /// which only the middle is kept: a letter cut in half at a window's edge is heard wrongly, and
    /// the context is what lets the model hear it whole.
    /// </summary>
    public float[] Emissions(string audioPath, long durationMs, Action<double> progress, CancellationToken ct)
    {
        const int hop = 30 * 16_000;     // 1500 frames
        const int context = 16_000;      // 50 frames

        var totalFrames = (int)(durationMs / FrameMs) + 2;
        var emissions = new float[(long)totalFrames * VocabularySize];

        using var pcm = FfmpegDecoder.OpenStream(audioPath);

        // Holds audio from sample `bufferStart` on; never more than one window's worth.
        var buffer = new float[hop + 2 * context];
        long bufferStart = 0;
        var filled = 0;
        var ended = false;

        for (long start = 0; ; start += hop)
        {
            ct.ThrowIfCancellationRequested();

            // Drop what is no longer needed as left context, then top up to the right context.
            var drop = (int)Math.Max(0, start - context - bufferStart);
            if (drop > 0)
            {
                buffer.AsSpan(drop, filled - drop).CopyTo(buffer);
                filled -= drop;
                bufferStart += drop;
            }

            if (!ended) filled += ReadInto(pcm.Stream, buffer.AsSpan(filled), ref ended);

            var windowFrom = Math.Max(0, start - context);
            var windowTo = bufferStart + filled;
            if (windowTo <= start) break;

            var window = buffer.AsSpan((int)(windowFrom - bufferStart), (int)(windowTo - windowFrom));
            if (window.Length < SamplesPerFrame * 2) break;

            var frames = Run(window);
            var framesInWindow = frames.Length / VocabularySize;

            // Keep only the frames whose audio lies in [start, start + hop).
            var firstKept = (int)((start - windowFrom) / SamplesPerFrame);
            var lastKept = (int)Math.Min(framesInWindow, (start + hop - windowFrom) / SamplesPerFrame);
            var firstGlobal = windowFrom / SamplesPerFrame;

            for (var f = firstKept; f < lastKept; f++)
            {
                var global = firstGlobal + f;
                if (global >= totalFrames) break;

                frames.AsSpan(f * VocabularySize, VocabularySize)
                    .CopyTo(emissions.AsSpan((int)(global * VocabularySize), VocabularySize));
            }

            progress(Math.Min(1, (start + hop) / 16.0 / durationMs));

            if (ended && windowTo <= start + hop) break;
        }

        return emissions;
    }

    /// <summary>One window through the model, returned as log-probabilities.</summary>
    private float[] Run(ReadOnlySpan<float> samples)
    {
        // Zero mean, unit variance: what the model saw in training.
        double sum = 0, squares = 0;
        foreach (var s in samples) sum += s;
        var mean = sum / samples.Length;
        foreach (var s in samples) squares += (s - mean) * (s - mean);
        var scale = 1 / Math.Sqrt(squares / samples.Length + 1e-7);

        var input = new DenseTensor<float>([1, samples.Length]);
        for (var i = 0; i < samples.Length; i++) input.Buffer.Span[i] = (float)((samples[i] - mean) * scale);

        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("input_values", input) };

        if (_wantsMask)
        {
            var mask = new DenseTensor<long>([1, samples.Length]);
            mask.Buffer.Span.Fill(1);
            inputs.Add(NamedOnnxValue.CreateFromTensor("attention_mask", mask));
        }

        using var results = _session.Run(inputs);
        var logits = results.First().AsTensor<float>();

        var frames = logits.Dimensions[1];
        var output = new float[frames * VocabularySize];

        for (var f = 0; f < frames; f++)
        {
            var max = float.NegativeInfinity;
            for (var v = 0; v < VocabularySize; v++) max = Math.Max(max, logits[0, f, v]);

            double total = 0;
            for (var v = 0; v < VocabularySize; v++) total += Math.Exp(logits[0, f, v] - max);

            var log = (float)(max + Math.Log(total));
            for (var v = 0; v < VocabularySize; v++) output[f * VocabularySize + v] = logits[0, f, v] - log;
        }

        return output;
    }

    private static int ReadInto(Stream stream, Span<float> target, ref bool ended)
    {
        var bytes = MemoryMarshal.AsBytes(target);
        var total = 0;

        while (total < bytes.Length && !ended)
        {
            var read = stream.Read(bytes[total..]);
            if (read == 0) ended = true;
            total += read;
        }

        // A float split across reads waits for its other half next time — except at the very end,
        // where there is none.
        return total / sizeof(float);
    }

    public void Dispose() => _session.Dispose();
}
