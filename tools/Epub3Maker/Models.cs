namespace Epub3Maker;

/// <summary>
/// The speech models, fetched once into the user's profile and kept.
///
/// The same files the phone downloads, from the same place. On a PC the larger ones are affordable,
/// and they hear more of the narration correctly — which means more anchors and less guessing
/// between them.
/// </summary>
public static class Models
{
    public static readonly string[] Names = ["tiny", "base", "small", "medium"];

    public static string Directory => Paths.Models;

    /// <summary>
    /// The English-only variant for an English book: the same size, faster and more accurate on
    /// English, and useless on anything else.
    /// </summary>
    public static string FileName(string model, string? language) =>
        language == "en" ? $"ggml-{model}.en.bin" : $"ggml-{model}.bin";

    public static Task<string> EnsureAsync(string model, string? language, CancellationToken ct)
    {
        var name = FileName(model, language);
        return EnsureFileAsync($"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{name}", name, ct);
    }

    /// <summary>A model file, downloaded the first time it is needed and kept.</summary>
    public static async Task<string> EnsureFileAsync(string url, string name, CancellationToken ct)
    {
        var path = Path.Combine(Directory, name);

        if (File.Exists(path)) return path;

        System.IO.Directory.CreateDirectory(Directory);

        Console.WriteLine($"Preuzimam model {name} (jednom) …");

        using var http = new HttpClient { Timeout = TimeSpan.FromHours(1) };
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? 0;
        var partial = path + ".part";

        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var target = File.Create(partial))
        {
            var buffer = new byte[1 << 20];
            long done = 0, shown = -1;
            int read;

            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;

                if (total > 0 && done * 100 / total != shown)
                {
                    shown = done * 100 / total;
                    Console.Write($"\r  {shown,3} %  ({done / 1_048_576} / {total / 1_048_576} MB)");
                }
            }
        }

        Console.WriteLine();

        // Renamed only once complete, so an interrupted download is never mistaken for a model.
        File.Move(partial, path, overwrite: true);
        return path;
    }
}
