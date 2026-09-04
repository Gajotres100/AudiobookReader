using Android.Provider;
using AndroidUri = Android.Net.Uri;

namespace AudioBookReader.App.Services;

public partial class BookFilePicker
{
    /// <summary>
    /// Opens the system document picker and keeps the document itself.
    ///
    /// <c>ACTION_OPEN_DOCUMENT</c> rather than <c>ACTION_GET_CONTENT</c>, because only the former
    /// hands back a lasting reference: asked with the persistable flag, the grant survives reboots,
    /// which is what lets an audiobook be played where it lies instead of being copied in.
    /// </summary>
    private partial async Task<PickedMedia?> PickAsync(string prompt)
    {
        var uri = await MainActivity.PickDocumentAsync(prompt);
        if (uri is null) return null;

        return new PickedMedia(uri.ToString()!, DisplayNameOf(uri));
    }

    /// <summary>
    /// The document's own name, which the last segment of a URI is not — providers hand out opaque
    /// identifiers, and the name decides the extension every parser here keys off.
    /// </summary>
    private static string DisplayNameOf(AndroidUri uri)
    {
        var resolver = global::Android.App.Application.Context.ContentResolver;

        try
        {
            using var cursor = resolver?.Query(uri, [IOpenableColumns.DisplayName], null, null, null);

            if (cursor is not null && cursor.MoveToFirst() && !cursor.IsNull(0))
                return cursor.GetString(0) ?? Fallback(uri);
        }
        catch (Exception ex)
        {
            AppLog.Error($"reading the name of {uri}", ex);
        }

        return Fallback(uri);
    }

    private static string Fallback(AndroidUri uri) =>
        uri.LastPathSegment is { Length: > 0 } segment ? segment : "book";
}
