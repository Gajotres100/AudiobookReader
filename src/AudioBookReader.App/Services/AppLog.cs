namespace AudioBookReader.App.Services;

/// <summary>
/// Writes failures somewhere they can be read back off the device.
///
/// Exists because the app now catches its exceptions instead of crashing, which is right for the
/// user and terrible for diagnosis: a caught error leaves nothing in the system log, so a failure
/// the user describes as "it doesn't work" has no trace behind it. The tag makes it greppable.
/// </summary>
public static class AppLog
{
    public const string Tag = "[ABR]";

    public static void Error(string what, Exception ex)
    {
        Write($"{what} FAILED: {ex.GetType().Name}: {ex.Message}");

        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
            Write($"  caused by {inner.GetType().Name}: {inner.Message}");

        Write(ex.StackTrace ?? "");
    }

    public static void Info(string message) => Write(message);

    // ---- Keeping it ----
    //
    // The system log is a ring buffer a few megabytes wide, shared with every app on the phone. A
    // problem reported the next morning has usually scrolled out of it — which is exactly what
    // happened the first time an alignment failed and there was nothing left to read. Alignment
    // runs for hours; its evidence has to outlive a buffer measured in minutes.

    private static readonly object Pen = new();

    /// <summary>Where the log is written. Set once at startup; nothing is kept before that.</summary>
    public static string? File { get; set; }

    /// <summary>Rolled at a megabyte, keeping one previous file. Small enough to pull over a cable.</summary>
    private const long RollAt = 1024 * 1024;

    private static void Write(string message)
    {
        var line = $"{Tag} {message}";
        Console.WriteLine(line);

        if (File is not { Length: > 0 } path) return;

        // Never the reason something failed. A log that throws while reporting a failure loses the
        // failure and adds one of its own.
        try
        {
            lock (Pen)
            {
                if (System.IO.File.Exists(path) && new FileInfo(path).Length > RollAt)
                    System.IO.File.Move(path, path + ".old", overwrite: true);

                System.IO.File.AppendAllText(
                    path, $"{DateTime.Now:MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // Nothing to do and nowhere to say it.
        }
    }

    /// <summary>
    /// Detail worth having only while chasing something specific.
    ///
    /// Off by default, and off in a way that matters: the callers pass a function rather than a
    /// string, so the interpolation never runs. The lines this guards fire from a timer five times
    /// a second, and building their text to then throw it away is a real cost on the thread drawing
    /// the page. Turn it on from Settings when something needs explaining.
    /// </summary>
    public static bool Verbose { get; set; }

    public static void Detail(Func<string> message)
    {
        if (Verbose) Console.WriteLine($"{Tag} {message()}");
    }
}
