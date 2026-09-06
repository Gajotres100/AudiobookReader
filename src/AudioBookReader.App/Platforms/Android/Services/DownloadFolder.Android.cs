using Android.Provider;
using AndroidUri = Android.Net.Uri;

namespace AudioBookReader.App.Services;

public partial class DownloadFolder
{
    private static Android.Content.ContentResolver Resolver =>
        global::Android.App.Application.Context.ContentResolver
        ?? throw new InvalidOperationException("Nema pristupa datotekama sustava.");

    private partial async Task<string?> ChooseCoreAsync()
    {
        var tree = await MainActivity.PickFolderAsync();
        if (tree is null) return null;

        // The grant has to be taken deliberately, and it is what makes the choice last past a
        // reboot. Without it the folder would have to be chosen again on every download.
        Resolver.TakePersistableUriPermission(
            tree,
            Android.Content.ActivityFlags.GrantReadUriPermission
            | Android.Content.ActivityFlags.GrantWriteUriPermission);

        return tree.ToString();
    }

    /// <summary>
    /// The folder's name as the user knows it.
    ///
    /// A tree URI reads as "content://com.android.externalstorage.documents/tree/primary%3AAudiobooks",
    /// which is nobody's idea of a folder. The part after the colon is the path they chose.
    /// </summary>
    public partial string Describe()
    {
        if (Location is not { Length: > 0 } location) return "Nije odabrana";

        var decoded = AndroidUri.Decode(location) ?? location;
        var colon = decoded.LastIndexOf(':');

        var path = colon >= 0 ? decoded[(colon + 1)..] : decoded;
        return string.IsNullOrWhiteSpace(path) ? "Interna pohrana" : path;
    }

    public partial async Task<(Stream Stream, string Location)> CreateAsync(string fileName)
    {
        if (Location is not { Length: > 0 } location)
            throw new InvalidOperationException("Mapa za preuzimanje nije odabrana.");

        var tree = AndroidUri.Parse(location)
                   ?? throw new InvalidOperationException("Mapa za preuzimanje nije čitljiva.");

        var directory = DocumentsContract.BuildDocumentUriUsingTree(
                            tree, DocumentsContract.GetTreeDocumentId(tree))
                        ?? throw new InvalidOperationException("Mapa za preuzimanje više ne postoji.");

        // A generic type on purpose. The system decides the extension from the mime type when it
        // recognises one, and for .m4b it does not — which is how a book arrives named "book.mp4".
        var file = DocumentsContract.CreateDocument(
                       Resolver, directory, "application/octet-stream", fileName)
                   ?? throw new IOException($"Ne mogu napraviti '{fileName}' u odabranoj mapi.");

        var stream = Resolver.OpenOutputStream(file, "w")
                     ?? throw new IOException($"Ne mogu pisati u '{fileName}'.");

        return await Task.FromResult<(Stream, string)>((stream, file.ToString()!));
    }

    public partial void Delete(string location)
    {
        try
        {
            if (AndroidUri.Parse(location) is { } uri) DocumentsContract.DeleteDocument(Resolver, uri);
        }
        catch (Exception ex)
        {
            // A leftover in the user's own folder is theirs to remove; failing an import over it
            // would be worse.
            AppLog.Info($"could not remove '{location}': {ex.Message}");
        }
    }
}
