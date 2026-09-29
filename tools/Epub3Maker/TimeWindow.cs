using System.Globalization;

namespace Epub3Maker;

/// <summary>
/// The hours of the day the job may work, such as 01:00–07:00 — when nobody is streaming from
/// the server it shares. May run past midnight (22:00–06:00). Stopping at its end loses nothing:
/// the book in progress carries on the next night.
/// </summary>
public readonly record struct TimeWindow(TimeSpan From, TimeSpan To)
{
    public static TimeWindow? Parse(string value)
    {
        var parts = value.Split('-', 2, StringSplitOptions.TrimEntries);

        if (parts.Length == 2
            && TimeSpan.TryParseExact(parts[0], [@"h\:mm", @"hh\:mm", "%h"], CultureInfo.InvariantCulture, out var from)
            && TimeSpan.TryParseExact(parts[1], [@"h\:mm", @"hh\:mm", "%h"], CultureInfo.InvariantCulture, out var to)
            && from != to && from < TimeSpan.FromDays(1) && to <= TimeSpan.FromDays(1))
        {
            return new TimeWindow(from, to);
        }

        return null;
    }

    public bool Contains(DateTime now)
    {
        var time = now.TimeOfDay;
        return From < To ? time >= From && time < To : time >= From || time < To;
    }

    /// <summary>How long until the window closes, from a moment inside it.</summary>
    public TimeSpan Remaining(DateTime now)
    {
        var left = To - now.TimeOfDay;
        return left > TimeSpan.Zero ? left : left + TimeSpan.FromDays(1);
    }

    /// <summary>How long until the window next opens.</summary>
    public TimeSpan UntilOpen(DateTime now)
    {
        var wait = From - now.TimeOfDay;
        return wait >= TimeSpan.Zero ? wait : wait + TimeSpan.FromDays(1);
    }

    public override string ToString() => $@"{From:hh\:mm}-{To:hh\:mm}";
}

public static class Durations
{
    /// <summary>"90" (minutes), "90m", "2h", "1h30m", "45s".</summary>
    public static TimeSpan? Parse(string value)
    {
        value = value.Trim().ToLowerInvariant();
        if (int.TryParse(value, out var minutes) && minutes > 0) return TimeSpan.FromMinutes(minutes);

        var total = TimeSpan.Zero;
        var number = 0;
        var any = false;

        foreach (var c in value)
        {
            if (char.IsDigit(c))
            {
                number = number * 10 + (c - '0');
                continue;
            }

            total += c switch
            {
                'h' => TimeSpan.FromHours(number),
                'm' => TimeSpan.FromMinutes(number),
                's' => TimeSpan.FromSeconds(number),
                _ => TimeSpan.MinValue,
            };

            if (total < TimeSpan.Zero) return null;
            number = 0;
            any = true;
        }

        return any && number == 0 && total > TimeSpan.Zero ? total : null;
    }
}
