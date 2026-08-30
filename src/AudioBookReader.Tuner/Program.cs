using System.Diagnostics;
using AudioBookReader.Core.Audio;
using AudioBookReader.Core.Books;

// Development harness. Not shipped: it runs the same Core code the phone runs, so a book that
// misbehaves can be diagnosed in seconds instead of through a deploy cycle.

if (args.Length == 0)
{
    Console.WriteLine("Usage: tuner <file> [more files...]");
    Console.WriteLine("  Ebooks are extracted; audio files are probed. Reports what the app would see.");
    return 1;
}

var extractors = new BookTextExtractors();
var failures = 0;

foreach (var path in args)
{
    Console.WriteLine();
    Console.WriteLine(new string('─', 78));
    Console.WriteLine(Path.GetFileName(path));
    Console.WriteLine(new string('─', 78));

    if (!File.Exists(path))
    {
        Console.WriteLine("  MISSING");
        failures++;
        continue;
    }

    Console.WriteLine($"  size       {new FileInfo(path).Length:N0} bytes");

    var stopwatch = Stopwatch.StartNew();

    try
    {
        if (AudioBookProbe.IsSupportedAudioFile(path))
        {
            var info = await AudioBookProbe.ProbeAsync(path);

            Console.WriteLine($"  title      {info.Title}");
            Console.WriteLine($"  author     {info.Author}");
            Console.WriteLine($"  duration   {TimeSpan.FromMilliseconds(info.DurationMs)}");
            Console.WriteLine($"  chapters   {info.Chapters.Count}");
            Console.WriteLine($"  cover      {(info.Cover is { Length: > 0 } c ? $"{c.Length:N0} bytes" : "none")}");

            foreach (var chapter in info.Chapters.Take(5))
                Console.WriteLine($"    {chapter.Index,3}  {TimeSpan.FromMilliseconds(chapter.StartMs ?? 0):hh\\:mm\\:ss}  {chapter.Title}");
        }
        else
        {
            var book = await extractors.ExtractAsync(path);

            Console.WriteLine($"  title      {book.Text.Title}");
            Console.WriteLine($"  author     {book.Text.Author}");
            Console.WriteLine($"  documents  {book.Text.Spine.Count}");
            Console.WriteLine($"  characters {book.Text.PlainText.Length:N0}");
            Console.WriteLine($"  sentences  {book.Text.Sentences.Count:N0}");
            Console.WriteLine($"  chapters   {book.Chapters.Count}");

            foreach (var chapter in book.Chapters.Take(5))
                Console.WriteLine($"    {chapter.Index,3}  {chapter.TextStart,8}  {chapter.Title}");

            Console.WriteLine();
            Console.WriteLine($"  opening    {book.Text.Excerpt(0, 120)}");
        }

        Console.WriteLine($"  took       {stopwatch.ElapsedMilliseconds:N0} ms");
    }
    catch (Exception ex)
    {
        failures++;

        Console.WriteLine();
        Console.WriteLine($"  FAILED after {stopwatch.ElapsedMilliseconds:N0} ms");
        Console.WriteLine($"  {ex.GetType().Name}: {ex.Message}");

        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
            Console.WriteLine($"    caused by {inner.GetType().Name}: {inner.Message}");

        Console.WriteLine();
        Console.WriteLine(ex.StackTrace);
    }
}

return failures == 0 ? 0 : 1;
