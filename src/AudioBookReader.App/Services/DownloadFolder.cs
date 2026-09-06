namespace AudioBookReader.App.Services;

/// <summary>
/// Where books coming down from a server are put.
///
/// The app's own storage was the wrong answer. Books downloaded there are invisible to everything
/// else on the phone — no file manager, no USB, no other player — and they sit apart from the
/// audiobooks the user already keeps in one place. So the folder is theirs to choose, and what is
/// downloaded lands beside what they already have.
///
/// Nothing is copied into app storage until alignment needs it. That path already exists: a book
/// referenced where it lies is copied in only when the aligner has to seek around it, and removed
/// again with the book.
/// </summary>
public partial class DownloadFolder
{
    private const string FolderKey = "download.folder";

    /// <summary>The chosen folder, as an opaque handle, or null while none has been chosen.</summary>
    public string? Location
    {
        get => Preferences.Default.Get<string?>(FolderKey, null);
        private set
        {
            if (value is null) Preferences.Default.Remove(FolderKey);
            else Preferences.Default.Set(FolderKey, value);
        }
    }

    public bool IsChosen => !string.IsNullOrEmpty(Location);

    /// <summary>What to show the user, which is not the same as what the system calls it.</summary>
    public partial string Describe();

    /// <summary>Asks for a folder and keeps the right to write in it. Null when nothing was chosen.</summary>
    /// <param name="startAtAudiobooks">Opens the picker at the Audiobooks folder rather than at the root.</param>
    public async Task<string?> ChooseAsync(bool startAtAudiobooks = true)
    {
        var chosen = await ChooseCoreAsync(startAtAudiobooks);
        if (chosen is null) return null;

        Location = chosen;
        AppLog.Info($"downloads will go to '{chosen}'");

        return chosen;
    }

    public void Forget() => Location = null;

    private partial Task<string?> ChooseCoreAsync(bool startAtAudiobooks);

    /// <summary>
    /// Creates a file under the chosen folder and hands back a stream to write it and the location
    /// it will be known by afterwards.
    /// </summary>
    /// <param name="folders">
    /// Subfolders to put it under, made if they are not there — author, then title. That is how
    /// every other audiobook tool lays them out, and it is what makes a folder of two hundred books
    /// navigable rather than a wall of filenames.
    /// </param>
    public partial Task<(Stream Stream, string Location)> CreateAsync(
        IReadOnlyList<string> folders,
        string fileName);

    /// <summary>Turns a title or an author into something a filesystem will accept as a folder.</summary>
    public static string SafeName(string name)
    {
        var cleaned = new string([.. name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)]);

        // Trailing dots and spaces are legal in a string and not in a directory name on most of the
        // filesystems a phone can be carrying.
        cleaned = cleaned.Trim().TrimEnd('.').Trim();

        return cleaned.Length == 0 ? "Nepoznato" : cleaned;
    }

    /// <summary>Removes a file this created, for a download that did not finish.</summary>
    public partial void Delete(string location);
}
