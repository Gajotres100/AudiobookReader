using AudioBookReader.Core.Audio;

namespace AudioBookReader.App.Services;

/// <summary>
/// What the platform itself can tell us about an audio file.
///
/// A fallback, used only when the tag library gives up. It cannot read chapter marks — nothing on
/// the platform can — so a book that comes through here is one long chapter. That is a real loss,
/// and still far better than the alternative: an import that spins for a minute and then says the
/// object reference is not set to an instance of an object.
///
/// Worth having because the files that defeat the tag library are not exotic. The one that
/// prompted this is an ordinary MP4 audiobook named "…m4b.mp3"; the system's own extractor opens
/// it without complaint.
/// </summary>
public static partial class AudioMetadata
{
    /// <summary>Reads what the platform can, or null when even it cannot open the file.</summary>
    public static partial Task<AudioBookInfo?> ReadAsync(string location, string fileName);
}
