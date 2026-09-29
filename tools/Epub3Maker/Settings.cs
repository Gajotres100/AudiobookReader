namespace Epub3Maker;

/// <summary>
/// Settings as plain names and values, from wherever they were given.
///
/// Four places, each overriding the one before, because each suits a different way of running:
///
///   1. a settings file — <c>epub3maker.conf</c> in the data folder, or <c>--config</c>: for a
///      scheduled task, where editing a file beats editing a command line;
///   2. environment variables, <c>EPUB3MAKER_QUALITY=best</c>: what Docker compose passes;
///   3. the command line, <c>--quality best</c>: for trying something once;
///   4. for one book only, a <c>.epub3maker</c> file in its folder: <c>quality=best</c> for the one
///      that deserves a night of listening, <c>skip</c> for one that should be left alone.
///
/// Names are English, with the Croatian ones accepted too (<c>kvaliteta</c>, <c>sidra</c>, …).
/// </summary>
public sealed class Settings
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public static readonly string[] Flags = ["retry", "dry-run", "skip", "help"];

    /// <summary>Every name a setting goes by, to its English one.</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["library"] = "library", ["knjige"] = "library", ["biblioteka"] = "library", ["root"] = "library",
        ["output"] = "output", ["izlaz"] = "output", ["o"] = "output",
        ["quality"] = "quality", ["kvaliteta"] = "quality", ["q"] = "quality",
        ["anchors"] = "anchors", ["sidra"] = "anchors",
        ["model"] = "model", ["m"] = "model",
        ["language"] = "language", ["jezik"] = "language",
        ["threads"] = "threads", ["niti"] = "threads",
        ["window"] = "window", ["prozor"] = "window",
        ["watch"] = "watch", ["gledaj"] = "watch",
        ["retry"] = "retry", ["ponovi"] = "retry",
        ["skip"] = "skip", ["preskoci"] = "skip", ["preskoči"] = "skip",
        ["ffmpeg"] = "ffmpeg",
        ["data"] = "data", ["podaci"] = "data",
        ["priority"] = "priority", ["prioritet"] = "priority",
        ["dry-run"] = "dry-run", ["suho"] = "dry-run", ["list"] = "dry-run", ["popis"] = "dry-run",
        ["config"] = "config", ["postavke"] = "config",
        ["map"] = "map", ["karta"] = "map",
        ["help"] = "help", ["h"] = "help", ["?"] = "help", ["pomoc"] = "help", ["pomoć"] = "help",
        // Kept from the first version of the command line.
        ["whisper"] = "whisper", ["fast"] = "fast", ["brzo"] = "fast",
    };

    public static string? Canonical(string name) =>
        Aliases.TryGetValue(name.Trim().TrimStart('-', '/'), out var canonical) ? canonical : null;

    public string? this[string name] => _values.TryGetValue(name, out var value) ? value : null;

    public bool Has(string name) => _values.ContainsKey(name);

    public bool IsOn(string name) =>
        this[name] is { } value && value.Trim().ToLowerInvariant() is "" or "1" or "true" or "yes" or "da" or "on";

    public void Set(string name, string value)
    {
        // The two old switches were qualities in all but name.
        switch (name)
        {
            case "whisper": _values["quality"] = "better"; return;
            case "fast": _values["quality"] = "light"; return;
            default: _values[name] = value; return;
        }
    }

    /// <summary>Everything in <paramref name="other"/>, over what is here.</summary>
    public Settings With(Settings other)
    {
        var merged = new Settings();
        foreach (var (name, value) in _values) merged._values[name] = value;
        foreach (var (name, value) in other._values) merged._values[name] = value;
        return merged;
    }

    /// <summary>
    /// A settings file: one <c>name = value</c> per line, <c>#</c> for comments, and a name alone
    /// for a switch (<c>skip</c>). A name nobody knows is an error, so a typo does not silently do
    /// nothing for a month.
    /// </summary>
    public static Settings ReadFile(string path)
    {
        var settings = new Settings();
        var number = 0;

        foreach (var raw in File.ReadAllLines(path))
        {
            number++;
            var line = raw.Split('#', 2)[0].Trim();
            if (line.Length == 0) continue;

            var parts = line.Split('=', 2);
            var name = Canonical(parts[0]) ?? throw new ArgumentException($"{path}, redak {number}: nepoznata postavka '{parts[0].Trim()}'");

            settings.Set(name, parts.Length > 1 ? parts[1].Trim().Trim('"') : "");
        }

        return settings;
    }

    /// <summary><c>EPUB3MAKER_QUALITY</c>, <c>EPUB3MAKER_DRY_RUN</c> and so on.</summary>
    public static Settings FromEnvironment()
    {
        var settings = new Settings();

        foreach (var name in Aliases.Values.Distinct())
        {
            var variable = "EPUB3MAKER_" + name.ToUpperInvariant().Replace('-', '_');
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value) settings.Set(name, value);
        }

        return settings;
    }

    /// <summary>
    /// <c>--name value</c> and <c>--switch</c>; anything without dashes is a positional argument.
    /// </summary>
    public static Settings FromCommandLine(IReadOnlyList<string> args, List<string> positional)
    {
        var settings = new Settings();

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];

            if (!(arg.StartsWith('-') || arg == "/?") || arg == "-")
            {
                positional.Add(arg);
                continue;
            }

            // --name=value as well as --name value.
            var equals = arg.IndexOf('=');
            var given = equals > 0 ? arg[..equals] : arg;
            var name = Canonical(given) ?? throw new ArgumentException($"Nepoznata opcija '{given}'. Popis: epub3maker --help");

            if (equals > 0)
                settings.Set(name, arg[(equals + 1)..]);
            else if (Flags.Contains(name) || name is "whisper" or "fast")
                settings.Set(name, "");
            else if (i + 1 < args.Count)
                settings.Set(name, args[++i]);
            else
                throw new ArgumentException($"{given} traži vrijednost");
        }

        return settings;
    }
}
