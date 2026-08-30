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
}
