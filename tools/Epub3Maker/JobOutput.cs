namespace Epub3Maker;

/// <summary>What a job says while it runs: finished lines, and how far along it is.</summary>
public interface IJobOutput
{
    void Line(string text);

    /// <summary>How much is done, 0 to 1, and a short detail — speed, time left.</summary>
    void Progress(double done, string detail);
}

/// <summary>
/// For a person at the console: progress rewrites one line in place.
/// </summary>
public sealed class InteractiveOutput(LogFile log) : IJobOutput
{
    private bool _progressShown;

    public void Line(string text)
    {
        if (_progressShown) Console.WriteLine();
        _progressShown = false;

        Console.WriteLine(text);
        log.Write(text.Trim());
    }

    public void Progress(double done, string detail)
    {
        Console.Write($"\r  {done:P1}  {detail}      ");
        _progressShown = true;
    }
}

/// <summary>
/// For a job nobody is watching — a scheduled task, a container — whose output ends up in a log:
/// every line has the time, and progress is a line every five percent or ten minutes rather than
/// thousands of carriage returns.
/// </summary>
public sealed class LoggedOutput(LogFile log) : IJobOutput
{
    private int _lastStep = -1;
    private DateTime _lastShown = DateTime.MinValue;

    public void Line(string text)
    {
        Console.WriteLine($"{DateTime.Now:HH:mm:ss} {text.Trim()}");
        log.Write(text.Trim());
    }

    public void Progress(double done, string detail)
    {
        var step = (int)(done * 20);
        if (step == _lastStep && DateTime.Now - _lastShown < TimeSpan.FromMinutes(10)) return;

        _lastStep = step;
        _lastShown = DateTime.Now;
        Line($"  {done:P1}  {detail}");
    }
}
