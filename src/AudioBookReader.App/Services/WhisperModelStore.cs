namespace AudioBookReader.App.Services;

/// <summary>A speech model that can be fetched on demand.</summary>
public record WhisperModel(string Name, string FileName, string Url, long ApproximateBytes);

/// <summary>
/// Keeps the speech model on disk, fetching it the first time alignment needs one.
///
/// The model is downloaded rather than shipped inside the package. At ~31 MB it would dominate the
/// download size of an app most of whose users may never pair an ebook at all, and Play penalises
/// large packages. It is fetched once and kept.
/// </summary>
public class WhisperModelStore(string directory, HttpClient? http = null)
{
    /// <summary>
    /// The multilingual tiny model, quantized to q5_1.
    ///
    /// Multilingual rather than the English-only build of the same size, because Croatian books
    /// have to work. Tiny rather than something larger because the transcript is never read by
    /// anyone: it is fuzzy-matched against the book's own text, which already says what the words
    /// are. Recognition only has to be close enough to locate them, and a bigger model would cost
    /// several times the CPU to be more right about something already known.
    /// </summary>
    public static readonly WhisperModel Tiny = new(
        Name: "Tiny (multilingual)",
        FileName: "ggml-tiny-q5_1.bin",
        Url: "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny-q5_1.bin",
        ApproximateBytes: 32_600_000);

    /// <summary>
    /// The next size up, for anyone who would rather wait than re-read a line.
    ///
    /// Roughly three times the work of <see cref="Tiny"/> and noticeably better at hearing what
    /// was actually said — most of all in languages other than English, where the small model
    /// mishears enough that whole probes are thrown away for not matching. Every discarded probe
    /// is a stretch of book left to interpolation, so recognition quality buys precision and not
    /// just a tidier transcript.
    ///
    /// Offered rather than imposed. It is a real cost on a long book, and the choice belongs to
    /// whoever is waiting for it.
    /// </summary>
    public static readonly WhisperModel Base = new(
        Name: "Base (multilingual)",
        FileName: "ggml-base-q5_1.bin",
        Url: "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base-q5_1.bin",
        ApproximateBytes: 57_800_000);

    /// <summary>The models by their stored id, so a preference survives a change of wording.</summary>
    public static WhisperModel ById(string id) => id == "base" ? Base : Tiny;

    private readonly HttpClient _http = http ?? new HttpClient();

    public string PathFor(WhisperModel model) => Path.Combine(directory, model.FileName);

    public bool IsDownloaded(WhisperModel model)
    {
        var path = PathFor(model);

        // A partial file from an interrupted download must not be mistaken for a usable model;
        // whisper would fail to load it in a way that is hard to trace back to here.
        return File.Exists(path) && new FileInfo(path).Length > model.ApproximateBytes / 2;
    }

    /// <summary>
    /// Returns the model's path, downloading it first if necessary.
    /// </summary>
    /// <param name="progress">Fraction downloaded, 0..1.</param>
    public async Task<string> EnsureAsync(
        WhisperModel model,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var path = PathFor(model);
        if (IsDownloaded(model)) return path;

        Directory.CreateDirectory(directory);
        var temp = path + ".part";

        using var response = await _http.GetAsync(model.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? model.ApproximateBytes;

        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var destination = File.Create(temp))
        {
            var buffer = new byte[81_920];
            var written = 0L;

            while (true)
            {
                var read = await source.ReadAsync(buffer, ct);
                if (read == 0) break;

                await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                written += read;
                progress?.Report(Math.Clamp(written / (double)total, 0, 1));
            }
        }

        // Only now does the file become the model, so a cancelled or failed download leaves
        // nothing that looks usable behind.
        File.Move(temp, path, overwrite: true);
        return path;
    }

    public void Delete(WhisperModel model)
    {
        var path = PathFor(model);
        if (File.Exists(path)) File.Delete(path);
    }
}
