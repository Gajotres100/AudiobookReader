using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Epub3Maker;

/// <summary>
/// Cuts a stretch out of an audiobook as 16 kHz mono floats, which is what whisper listens to.
///
/// FFmpeg rather than a managed decoder because it reads every container an audiobook comes in —
/// m4b, mp3, opus, flac — and seeks inside them in a moment. It runs as its own process per
/// stretch; starting it costs a few milliseconds against seconds of recognition.
/// </summary>
public static class FfmpegDecoder
{
    public static string Executable { get; set; } = "ffmpeg";

    /// <summary>Whether FFmpeg can be started at all, for a clear message before hours of work.</summary>
    public static bool IsAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(Executable, "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null) return false;

            process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static async Task<float[]> DecodeAsync(string path, long startMs, long durationMs, CancellationToken ct)
    {
        var info = new ProcessStartInfo(Executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // -ss before -i seeks in the input, which is fast and, since FFmpeg 2.1, exact.
        foreach (var argument in new[]
                 {
                     "-nostdin", "-v", "error",
                     "-ss", Seconds(startMs), "-t", Seconds(durationMs),
                     "-i", path,
                     "-vn", "-ac", "1", "-ar", "16000", "-f", "f32le", "pipe:1",
                 })
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException("FFmpeg did not start.");

        var errors = process.StandardError.ReadToEndAsync(ct);

        using var buffer = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(buffer, ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"FFmpeg failed at {Seconds(startMs)} s: {(await errors).Trim()}");

        var bytes = buffer.GetBuffer().AsSpan(0, (int)(buffer.Length / sizeof(float) * sizeof(float)));
        return MemoryMarshal.Cast<byte, float>(bytes).ToArray();
    }

    /// <summary>The whole file as one stream of 16 kHz mono floats, read as it is decoded.</summary>
    public static PcmStream OpenStream(string path)
    {
        var info = new ProcessStartInfo(Executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in new[] { "-nostdin", "-v", "error", "-i", path, "-vn", "-ac", "1", "-ar", "16000", "-f", "f32le", "pipe:1" })
            info.ArgumentList.Add(argument);

        var process = Process.Start(info) ?? throw new InvalidOperationException("FFmpeg did not start.");

        // Drained on the side, or a chatty FFmpeg fills the pipe and stops producing audio.
        _ = process.StandardError.ReadToEndAsync();

        return new PcmStream(process);
    }

    /// <summary>
    /// Joins an audiobook that comes as a file per chapter or per disc into one file, without
    /// re-encoding: the EPUB 3 carries one recording, and the app plays one.
    ///
    /// Only files of one kind can be joined this way; a folder mixing MP3 and M4A is refused with
    /// a reason rather than turned into something half-playable.
    /// </summary>
    public static async Task JoinAsync(IReadOnlyList<string> files, string target, CancellationToken ct)
    {
        var extensions = files.Select(f => Path.GetExtension(f).ToLowerInvariant()).Distinct().ToList();
        if (extensions.Count > 1)
            throw new NotSupportedException($"Audio dijelovi su različitih formata ({string.Join(", ", extensions)}) i ne mogu se spojiti bez prekodiranja.");

        var folder = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(folder);

        // FFmpeg's concat list quotes names in single quotes; a quote inside one is written '\''.
        var list = target + ".txt";
        await File.WriteAllLinesAsync(list, files.Select(f => "file '" + f.Replace("'", "'\\''") + "'"), ct);

        // Named with the real extension so FFmpeg picks the container, and renamed only once whole.
        var partial = Path.Combine(folder, "part-" + Path.GetFileName(target));

        var info = new ProcessStartInfo(Executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in new[] { "-nostdin", "-v", "error", "-y", "-f", "concat", "-safe", "0", "-i", list, "-map", "0:a", "-c", "copy", partial })
            info.ArgumentList.Add(argument);

        using var process = Process.Start(info) ?? throw new InvalidOperationException("FFmpeg did not start.");

        var errors = process.StandardError.ReadToEndAsync(ct);
        await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        File.Delete(list);

        if (process.ExitCode != 0)
        {
            File.Delete(partial);
            throw new InvalidOperationException($"Spajanje audio dijelova nije uspjelo: {(await errors).Trim()}");
        }

        File.Move(partial, target, overwrite: true);
    }

    public sealed class PcmStream(Process process) : IDisposable
    {
        public Stream Stream { get; } = process.StandardOutput.BaseStream;

        public void Dispose()
        {
            try
            {
                if (!process.HasExited) process.Kill();
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }

            process.Dispose();
        }
    }

    private static string Seconds(long ms) =>
        (ms / 1000.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}
