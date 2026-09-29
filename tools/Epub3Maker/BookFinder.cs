using System.Security.Cryptography;
using System.Text;
using AudioBookReader.Core.Audio;
using AudioBookReader.Core.Books;

namespace Epub3Maker;

/// <summary>
/// One book found in the library: an ebook, the audio that narrates it, and where its EPUB 3 goes.
/// </summary>
/// <param name="Key">What identifies it from one scan to the next: its folder and ebook, relative to the library.</param>
/// <param name="Problem">Why it cannot be made, when it cannot; the files are then only what was found.</param>
/// <param name="Settings">Its own settings, from a <c>.epub3maker</c> file in its folder.</param>
public sealed record FoundBook(
    string Key,
    string Folder,
    string? Ebook,
    IReadOnlyList<string> Audio,
    string Output,
    string? Problem,
    Settings Settings)
{
    /// <summary>
    /// Changes when any of its files does — name, size or time written — so a book whose audio was
    /// replaced is made again, without reading hundreds of megabytes on every scan to find out.
    /// </summary>
    public string Fingerprint { get; } = FingerprintOf(Ebook, Audio);

    private static string FingerprintOf(string? ebook, IReadOnlyList<string> audio)
    {
        var text = new StringBuilder();

        foreach (var file in audio.Prepend(ebook).OfType<string>())
        {
            var info = new FileInfo(file);
            text.Append(info.Name).Append('|').Append(info.Exists ? info.Length : -1).Append('|')
                .Append(info.Exists ? info.LastWriteTimeUtc.Ticks : 0).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..16];
    }
}

/// <summary>
/// Walks a library and pairs every ebook with its narration.
///
/// Laid out the way Audiobookshelf and most people keep audiobooks — a folder per book, however
/// deep (<c>Author/Series/Title/</c>) — and a folder that holds both an EPUB and audio is a book.
/// The audio may be one file or many: a file per chapter, or discs in their own subfolders
/// (<c>CD1/</c>, <c>CD2/</c>), joined in natural order.
///
/// When a folder holds several books side by side, each ebook is paired with the audio of the same
/// name (<c>Torch.epub</c> with <c>Torch.m4b</c>), the same rule the app uses. What cannot be
/// paired with certainty is reported, not guessed at: aligning a book to the wrong narration is
/// a night of work for nothing.
/// </summary>
public static class BookFinder
{
    public const string BookSettingsFile = ".epub3maker";

    public static List<FoundBook> Find(string library, string output, IEnumerable<string> excluded)
    {
        library = Path.GetFullPath(library);

        var skip = excluded.Append(output)
            .Select(p => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .ToList();

        var books = new List<FoundBook>();
        Walk(library, library, output, skip, books);
        return books;
    }

    private static void Walk(string library, string folder, string output, List<string> skip, List<FoundBook> books)
    {
        if (skip.Any(s => string.Equals(s, folder, StringComparison.OrdinalIgnoreCase))) return;

        string[] files, folders;

        try
        {
            files = Directory.GetFiles(folder);
            folders = Directory.GetDirectories(folder);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return;
        }

        var ebooks = files.Where(IsEbook).Order(NaturalStringComparer.Instance).ToList();
        var audio = files.Where(AudioBookProbe.IsSupportedAudioFile).Order(NaturalStringComparer.Instance).ToList();

        // Discs in subfolders: audio only, no ebook of their own.
        var discs = new List<string>();
        if (ebooks.Count > 0 && audio.Count == 0)
        {
            foreach (var sub in folders.Order(NaturalStringComparer.Instance))
            {
                var subFiles = SafeFiles(sub);
                if (subFiles.Any(IsEbook)) continue;

                var subAudio = subFiles.Where(AudioBookProbe.IsSupportedAudioFile).Order(NaturalStringComparer.Instance).ToList();
                if (subAudio.Count == 0) continue;

                audio.AddRange(subAudio);
                discs.Add(sub);
            }
        }

        if (ebooks.Count > 0 && audio.Count > 0)
        {
            var settings = File.Exists(Path.Combine(folder, BookSettingsFile))
                ? SafeRead(Path.Combine(folder, BookSettingsFile))
                : new Settings();

            foreach (var book in Pair(library, folder, output, ebooks, audio, settings)) books.Add(book);
        }

        foreach (var sub in folders.Order(NaturalStringComparer.Instance))
        {
            if (discs.Contains(sub)) continue;
            if (Path.GetFileName(sub).StartsWith('.')) continue;

            Walk(library, sub, output, skip, books);
        }
    }

    private static IEnumerable<FoundBook> Pair(
        string library, string folder, string output, List<string> ebooks, List<string> audio, Settings settings)
    {
        var relative = Path.GetRelativePath(library, folder);

        FoundBook Book(string? ebook, IReadOnlyList<string> sound, string? problem)
        {
            var name = ebook is not null ? Path.GetFileName(ebook) : Path.GetFileName(folder) + ".epub";
            var target = relative == "." ? Path.Combine(output, name) : Path.Combine(output, relative, name);
            var key = (relative == "." ? name : Path.Combine(relative, name)).Replace('\\', '/');

            return new FoundBook(key, folder, ebook, sound, target, problem, settings);
        }

        // One ebook: all of the folder's audio is its narration, however many files.
        if (ebooks.Count == 1)
        {
            yield return Book(ebooks[0], audio, null);
            yield break;
        }

        // Several: each takes the audio file of its own name, if exactly one has it.
        var paired = 0;

        foreach (var ebook in ebooks)
        {
            var name = BookNames.Normalize(Path.GetFileName(ebook));
            var match = audio.Where(a => BookNames.Normalize(Path.GetFileName(a)) == name).Take(2).ToList();

            if (match.Count == 1)
            {
                paired++;
                yield return Book(ebook, match, null);
            }
        }

        if (paired == 0)
        {
            yield return Book(null, audio,
                $"{ebooks.Count} e-knjige u istoj mapi, a nijedna se ne zove kao audio — ne znam koja je koja. " +
                $"Preimenuj ih da se poklapaju s audiom ili ih razdvoji u zasebne mape.");
        }
    }

    /// <summary>An EPUB that is not already an EPUB 3 with narration — ours from an earlier run, or anyone's.</summary>
    private static bool IsEbook(string path) =>
        Path.GetExtension(path).Equals(".epub", StringComparison.OrdinalIgnoreCase)
        && MediaOverlayPackage.AudioFiles(path) is null;

    private static string[] SafeFiles(string folder)
    {
        try
        {
            return Directory.GetFiles(folder);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    private static Settings SafeRead(string path)
    {
        try
        {
            return Settings.ReadFile(path);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine("Upozorenje: " + ex.Message);
            return new Settings();
        }
    }
}
