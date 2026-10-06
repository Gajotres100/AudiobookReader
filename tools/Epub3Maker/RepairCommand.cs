using System.IO.Compression;
using AudioBookReader.Core.Audio;
using AudioBookReader.Core.Books;

namespace Epub3Maker;

/// <summary>
/// <c>epub3maker repair &lt;book.epub | folder&gt;…</c>: puts right EPUB 3s already made, without
/// hearing the narration again. Packages made before chapter openings were repaired have a
/// chapter's heading and first lines timed into the last seconds of the chapter before, so a reader
/// jumping to the chapter plays the end of the previous one. Each file is rewritten in place; one
/// with nothing to repair is left untouched.
/// </summary>
static class RepairCommand
{
    public static bool IsCommand(string arg) => arg is "repair" or "popravi";

    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine("""
                epub3maker repair — popravlja vremena u već napravljenim EPUB 3 knjigama, bez ponovnog slušanja

                  epub3maker repair <knjiga.epub | mapa> [još…]

                Mapa se pretražuje sa svim podmapama. Svaka knjiga se prepiše na istom mjestu; ona kojoj
                ništa ne treba ostaje netaknuta. Popravlja početke poglavlja koje je Whisper upisao u kraj
                prethodnog poglavlja (skok na poglavlje svirao je kraj prethodnog).
                """);
            return args.Length == 0 ? 1 : 0;
        }

        var files = new List<string>();
        foreach (var arg in args)
        {
            if (Directory.Exists(arg))
                files.AddRange(Directory.EnumerateFiles(arg, "*.epub", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase));
            else if (File.Exists(arg))
                files.Add(arg);
            else
                throw new FileNotFoundException($"Nema ni datoteke ni mape '{arg}'.");
        }

        var failed = 0;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            Console.WriteLine(file);

            try
            {
                Console.WriteLine("  " + await RepairAsync(file, ct));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException
                                           or NotSupportedException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                failed++;
                Console.WriteLine("  greška: " + ex.Message);
            }
        }

        return failed > 0 ? 1 : 0;
    }

    private static async Task<string> RepairAsync(string path, CancellationToken ct)
    {
        if (MediaOverlayPackage.AudioFiles(path) is not { Count: > 0 } audio) return "nema naracije, preskačem";
        if (audio.Count > 1) return "naracija je u više datoteka, to ovaj popravak ne radi — preskačem";

        var extracted = await new BookTextExtractors().ExtractAsync(path, ct);

        // The recording's chapter marks say where each chapter really begins. Reading them needs the
        // audio as a file of its own, for a moment, next to the book.
        var marks = new List<long>();
        var temporary = path + ".marks" + Path.GetExtension(audio[0]);

        try
        {
            using (var zip = ZipFile.OpenRead(path))
            {
                if (zip.GetEntry(audio[0]) is not { } entry) throw new InvalidDataException("no narration file");

                await using var from = entry.Open();
                await using var to = File.Create(temporary);
                await from.CopyToAsync(to, ct);
            }

            var info = await AudioBookProbe.ProbeAsync(temporary, ct);
            marks.AddRange(info.Chapters.Select(c => c.StartMs ?? 0));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            // Without marks the opening is put where the narrator's pace says it begins.
        }
        finally
        {
            File.Delete(temporary);
        }

        var moved = await Task.Run(() => MediaOverlayPackage.RepairOverlayTimings(
            path, path, extracted.Text, audio[0], extracted.Chapters.Select(c => c.TextStart ?? 0), marks), ct);

        return moved == 0
            ? "u redu, ništa za popraviti"
            : $"popravljeno: {moved} rečenica na početcima poglavlja vraćeno na pravo mjesto ({marks.Count} oznaka poglavlja u zvuku)";
    }
}
