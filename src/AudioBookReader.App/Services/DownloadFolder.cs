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
    public async Task<string?> ChooseAsync()
    {
        var chosen = await ChooseCoreAsync();
        if (chosen is null) return null;

        Location = chosen;
        AppLog.Info($"downloads will go to '{chosen}'");

        return chosen;
    }

    public void Forget() => Location = null;

    private partial Task<string?> ChooseCoreAsync();

    /// <summary>
    /// Creates a file in the chosen folder and hands back a stream to write it and the location it
    /// will be known by afterwards.
    /// </summary>
    public partial Task<(Stream Stream, string Location)> CreateAsync(string fileName);

    /// <summary>Removes a file this created, for a download that did not finish.</summary>
    public partial void Delete(string location);
}
