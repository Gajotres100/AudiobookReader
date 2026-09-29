using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Epub3Maker;

public enum BookStatus
{
    Waiting,
    Working,
    Done,
    Failed,
}

/// <summary>What happened to one book, kept between scans.</summary>
public sealed class BookRecord
{
    public BookStatus Status { get; set; }

    /// <summary>The files as they were when this was recorded; different files, a fresh start.</summary>
    public string Fingerprint { get; set; } = "";

    /// <summary>The quality and spacing asked for; asking for another makes the book again.</summary>
    public string Recipe { get; set; } = "";

    public string? Output { get; set; }

    public DateTime? Started { get; set; }

    public DateTime? Finished { get; set; }

    /// <summary>Hours of work across every night it took.</summary>
    public double Hours { get; set; }

    public int Attempts { get; set; }

    public string? Error { get; set; }

    public int? Sentences { get; set; }

    public int? TimedSentences { get; set; }
}

/// <summary>
/// The scan's memory: which books are done, which failed and why, which one is halfway through.
/// A JSON file in the data folder, readable by a person and safe to delete — the next scan then
/// finds existing EPUB 3 files and takes them as done rather than making them again.
/// </summary>
public sealed class ScanState
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public Dictionary<string, BookRecord> Books { get; set; } = new(StringComparer.Ordinal);

    public static string FilePath => Path.Combine(Paths.Data, "state.json");

    public static string ReportPath => Path.Combine(Paths.Data, "report.txt");

    public static ScanState Load()
    {
        if (!File.Exists(FilePath)) return new ScanState();

        try
        {
            var state = JsonSerializer.Deserialize<ScanState>(File.ReadAllText(FilePath), Json) ?? new ScanState();
            state.Books = new Dictionary<string, BookRecord>(state.Books, StringComparer.Ordinal);
            return state;
        }
        catch (JsonException)
        {
            // Unreadable: start over rather than refuse to work. Finished books are found on disk.
            File.Move(FilePath, FilePath + ".broken", overwrite: true);
            return new ScanState();
        }
    }

    /// <summary>Written whole and then moved into place, so a power cut never leaves half a file.</summary>
    public void Save()
    {
        Directory.CreateDirectory(Paths.Data);

        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, Json));
        File.Move(temporary, FilePath, overwrite: true);

        WriteReport();
    }

    /// <summary>A plain summary for a person: what is done, what is waiting, what went wrong.</summary>
    private void WriteReport()
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"epub3maker — stanje {DateTime.Now:yyyy-MM-dd HH:mm}");
        text.AppendLine();

        foreach (var group in Books.OrderBy(b => b.Key, StringComparer.OrdinalIgnoreCase).GroupBy(b => b.Value.Status))
        {
            text.AppendLine(group.Key switch
            {
                BookStatus.Done => "GOTOVO",
                BookStatus.Working => "U TIJEKU",
                BookStatus.Waiting => "ČEKA",
                _ => "GREŠKA",
            });

            foreach (var (key, record) in group)
            {
                var detail = record.Status switch
                {
                    BookStatus.Done when record.Sentences > 0 =>
                        $"{record.TimedSentences * 100 / record.Sentences}% rečenica, {record.Hours:0.0} h, {record.Recipe}",
                    BookStatus.Done => record.Recipe,
                    BookStatus.Working => $"{record.Hours:0.0} h do sad, {record.Recipe}",
                    BookStatus.Waiting => record.Recipe,
                    _ => record.Error ?? "",
                };

                text.AppendLine(CultureInfo.InvariantCulture, $"  {key}  —  {detail}");
            }

            text.AppendLine();
        }

        File.WriteAllText(ReportPath, text.ToString());
    }
}
