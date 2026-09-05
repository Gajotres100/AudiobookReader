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
        Console.WriteLine($"{Tag} {what} FAILED: {ex.GetType().Name}: {ex.Message}");

        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
            Console.WriteLine($"{Tag}   caused by {inner.GetType().Name}: {inner.Message}");

        Console.WriteLine($"{Tag} {ex.StackTrace}");
    }

    public static void Info(string message) => Console.WriteLine($"{Tag} {message}");

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
