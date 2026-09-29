namespace Epub3Maker;

/// <summary>
/// Where the tool keeps what outlives one run: the models, the work in progress and, when it
/// scans a library, what it has already done.
///
/// One folder, chosen once. By default it is in the user's profile, which is right on a PC; a
/// scheduled task running as SYSTEM or a container has no useful profile, so it can be pointed
/// elsewhere with <c>--data</c> or <c>EPUB3MAKER_DATA</c> — a mounted volume, so a container that
/// is replaced does not throw away hours of alignment and a 360 MB model with it.
/// </summary>
public static class Paths
{
    public static string Data { get; set; } =
        Environment.GetEnvironmentVariable("EPUB3MAKER_DATA") is { Length: > 0 } data
            ? Path.GetFullPath(data)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Epub3Maker");

    public static string Models => Path.Combine(Data, "models");

    public static string Work => Path.Combine(Data, "work");

    public static string Log => Path.Combine(Data, "log.txt");
}

/// <summary>
/// The detailed log: every anchor count, skipped stretch and warning, with the time. The console
/// shows the summary; this is what to read when a book came out wrong.
/// </summary>
public sealed class LogFile : IDisposable
{
    /// <summary>Past this the log is moved aside, so a job that runs every night for a year does not fill the disk.</summary>
    private const long Limit = 10 * 1024 * 1024;

    private readonly object _gate = new();
    private readonly StreamWriter _writer;

    public LogFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (File.Exists(path) && new FileInfo(path).Length > Limit)
            File.Move(path, path + ".old", overwrite: true);

        _writer = new StreamWriter(path, append: true) { AutoFlush = true };
    }

    public void Write(string line)
    {
        lock (_gate) _writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}");
    }

    public void Dispose()
    {
        lock (_gate) _writer.Dispose();
    }
}
