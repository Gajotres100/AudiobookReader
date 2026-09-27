using AudioBookReader.App.Resources.Strings;

namespace AudioBookReader.App.Services;

/// <summary>A chosen file: where it is, and what it is called.</summary>
/// <param name="Location">A path, or a <c>content://</c> URI when the file stays where the user keeps it.</param>
public record PickedMedia(string Location, string FileName);

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
///
/// The system picker is asked for directly rather than through the cross-platform one, which
/// copies the chosen file into the app's cache before handing it over. For a half-gigabyte
/// audiobook that meant the file was copied twice and the wait doubled, for a copy that is thrown
/// away — and it hid the document behind a cache path, so the file could never simply be left where
/// it was.
/// </summary>
public partial class BookFilePicker
{
    public Task<PickedMedia?> PickAudioAsync(string? prompt = null) =>
        PickAsync(prompt ?? Strings.Picker_ChooseAudiobook);

    public Task<PickedMedia?> PickEbookAsync(string? prompt = null) =>
        PickAsync(prompt ?? Strings.Picker_ChooseEbook);

    /// <summary>Any file at all — for an alignment someone sent, which has no book of its own to be.</summary>
    public Task<PickedMedia?> PickFileAsync(string prompt) => PickAsync(prompt);

    private partial Task<PickedMedia?> PickAsync(string prompt);
}
