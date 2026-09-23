namespace AudioBookReader.App.Services;

/// <summary>
/// The iOS side of "left where the user keeps it". A location the user picked (and so bookmarked),
/// or one inside a bookmarked download folder, is a reference; anything else is an ordinary file
/// the app owns and is read like one — the same split Android draws between content:// URIs and
/// plain paths.
///
/// TryHold does not request anything, unlike Android's. There, the persistable grant can be taken
/// at any later moment because the URI itself is the grantable thing; on iOS a bookmark can only be
/// made while the picker's access is fresh, so the pickers make it themselves and this just
/// confirms that it happened.
/// </summary>
public partial class MediaReferences
{
    public partial bool IsReference(string location) => SecurityScopedBookmarks.Covers(location);

    public partial bool TryHold(string location) => SecurityScopedBookmarks.Covers(location);

    public partial void Release(string location) => SecurityScopedBookmarks.Forget(location);

    public partial Stream OpenRead(string location)
    {
        // Started once per process and deliberately never stopped per read: alignment reads a
        // referenced file in short bursts for hours. A location that is neither bookmarked nor
        // inside a bookmarked folder may still have been granted this session by a picker whose
        // bookmark failed — Access finds that too — and otherwise it is simply a plain path.
        var url = SecurityScopedBookmarks.Access(location);
        return File.OpenRead(url?.Path ?? location);
    }

    public partial bool TryDelete(string location)
    {
        if (!IsReference(location)) return false;

        try
        {
            var url = SecurityScopedBookmarks.Access(location);
            File.Delete(url?.Path ?? location);
            SecurityScopedBookmarks.Forget(location);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Info($"reference: delete failed for {location} ({ex.GetType().Name}: {ex.Message})");
            return false;
        }
    }
}
