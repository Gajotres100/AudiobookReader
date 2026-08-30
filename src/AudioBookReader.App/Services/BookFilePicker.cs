namespace AudioBookReader.App.Services;

/// <summary>
/// Picks a book file.
///
/// Deliberately asks the system picker for *everything* and validates nothing here. Two lessons
/// went into that. Filtering by MIME type greys out the files this app exists to open, because the
/// type reported for .m4b and .epub varies by device, by storage provider and by how the file
/// arrived. And filtering by extension rejects perfectly good books whose names were rewritten on
/// the way to the phone — cloud storage and messaging apps rename attachments and strip suffixes
/// routinely.
///
/// So the file is accepted here and judged by what it actually contains: the importer refuses
/// audio with no readable duration and text it cannot parse, and says so.
/// </summary>
public class BookFilePicker
{
    public Task<FileResult?> PickAudioAsync(string prompt = "Odaberi audioknjigu") => PickAsync(prompt);

    public Task<FileResult?> PickEbookAsync(string prompt = "Odaberi e-knjigu") => PickAsync(prompt);

    private static Task<FileResult?> PickAsync(string prompt) =>
        FilePicker.Default.PickAsync(new PickOptions { PickerTitle = prompt });
}
