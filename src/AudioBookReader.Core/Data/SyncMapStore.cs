using System.Text.Json;
using System.Text.Json.Serialization;
using AudioBookReader.Core.Models;

namespace AudioBookReader.Core.Data;

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(SyncMap))]
internal partial class SyncMapJsonContext : JsonSerializerContext;

/// <summary>
/// Reads and writes per-book sync maps as JSON files under a directory.
///
/// Alignment persists the map after every chapter so a run interrupted by the user, a reboot or
/// thermal backoff resumes instead of restarting. Writes therefore go through a temp file and a
/// rename, so a kill mid-write cannot leave a truncated map behind.
/// </summary>
public class SyncMapStore(string directory)
{
    private string PathFor(int bookId) => Path.Combine(directory, $"book-{bookId}.sync.json");

    public bool Exists(int bookId) => File.Exists(PathFor(bookId));

    public async Task<SyncMap?> LoadAsync(int bookId)
    {
        var path = PathFor(bookId);
        if (!File.Exists(path)) return null;

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync(stream, SyncMapJsonContext.Default.SyncMap);
        }
        catch (JsonException)
        {
            // A corrupt map is not worth failing the book over; alignment can rebuild it.
            return null;
        }
    }

    public async Task SaveAsync(int bookId, SyncMap map)
    {
        Directory.CreateDirectory(directory);

        var path = PathFor(bookId);
        var temp = path + ".tmp";

        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, map, SyncMapJsonContext.Default.SyncMap);
        }

        File.Move(temp, path, overwrite: true);
    }

    public void Delete(int bookId)
    {
        var path = PathFor(bookId);
        if (File.Exists(path)) File.Delete(path);
    }
}
