namespace AudioBookReader.App.Services;

/// <summary>
/// The iOS side of "left where the user keeps it": a location with a saved security-scoped
/// bookmark is a reference, one without is a plain file this app owns. Android's TryHold takes a
/// raw content:// URI and can ask the system for a permanent grant on it at any later moment,
/// because the URI itself is the grantable thing. iOS has no such moment — a bookmark can only be
/// created while access is fresh, which is at picker time, so BookFilePicker.iOS.cs and
/// DownloadFolder.iOS.cs already create and save the bookmark themselves when the user picks
/// something. TryHold here is therefore just confirming that already happened, not a second chance
/// to request access iOS has no API for granting after the fact.
/// </summary>
public partial class MediaReferences
{
    public partial bool IsReference(string location) => SecurityScopedBookmarks.Exists(location);

    public partial bool TryHold(string location) => SecurityScopedBookmarks.Exists(location);

    public partial void Release(string location) => SecurityScopedBookmarks.Forget(location);

    public partial Stream OpenRead(string location)
    {
        var url = SecurityScopedBookmarks.Resolve(location)
            ?? throw new IOException($"No access to '{location}' — its bookmark could not be resolved.");

        // Access is started by Resolve and deliberately not stopped here: alignment reads a
        // referenced file in short bursts for hours, and starting again on every call is a
        // harmless no-op per Apple's own documentation, while stopping too early mid-run is not.
        // The scope is released when the app itself exits.
        return File.OpenRead(url.Path!);
    }

    public partial bool TryDelete(string location)
    {
        try
        {
            var url = SecurityScopedBookmarks.Resolve(location);
            if (url is null) return false;

            File.Delete(url.Path!);
            SecurityScopedBookmarks.Forget(location);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error($"deleting the referenced file '{location}'", ex);
            return false;
        }
    }
}
