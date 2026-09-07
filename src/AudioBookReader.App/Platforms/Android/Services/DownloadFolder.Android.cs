using AudioBookReader.App.Resources.Strings;
using Android.Provider;
using AndroidUri = Android.Net.Uri;

namespace AudioBookReader.App.Services;

public partial class DownloadFolder
{
    private static Android.Content.ContentResolver Resolver =>
        global::Android.App.Application.Context.ContentResolver
        ?? throw new InvalidOperationException(Strings.Folder_NoSystemAccess);

    /// <summary>
    /// Where the picker opens when the user has not chosen anything yet.
    ///
    /// Audiobooks, because that is where audiobooks are. The system will not hand out a grant
    /// without someone confirming it, but it will start them in the right place — which turns
    /// "find your folder" into one tap.
    /// </summary>
    private const string AudiobooksDocument =
        "content://com.android.externalstorage.documents/document/primary%3AAudiobooks";

    private partial async Task<string?> ChooseCoreAsync(bool startAtAudiobooks)
    {
        var start = startAtAudiobooks ? AndroidUri.Parse(AudiobooksDocument) : null;

        var tree = await MainActivity.PickFolderAsync(start);
        if (tree is null) return null;

        // Taken deliberately, and it is what makes the choice last past a reboot. Without it the
        // folder would have to be chosen again on every download.
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
        if (Location is not { Length: > 0 } location) return Strings.Folder_NotChosen;

        var decoded = AndroidUri.Decode(location) ?? location;
        var colon = decoded.LastIndexOf(':');

        var path = colon >= 0 ? decoded[(colon + 1)..] : decoded;
        return string.IsNullOrWhiteSpace(path) ? Strings.Folder_InternalStorage : path;
    }

    public partial async Task<(Stream Stream, string Location)> CreateAsync(
        IReadOnlyList<string> folders,
        string fileName)
    {
        if (Location is not { Length: > 0 } location)
            throw new InvalidOperationException(Strings.Folder_NotChosenError);

        var tree = AndroidUri.Parse(location)
                   ?? throw new InvalidOperationException(Strings.Folder_Unreadable);

        var directory = DocumentsContract.BuildDocumentUriUsingTree(
                            tree, DocumentsContract.GetTreeDocumentId(tree))
                        ?? throw new InvalidOperationException(Strings.Folder_Gone);

        // Author, then title, the way a shelf is arranged and the way every other audiobook tool
        // lays them out on disk.
        foreach (var name in folders)
            directory = OpenOrCreateDirectory(tree, directory, name);

        // A generic type on purpose. The system picks the extension from the mime type when it
        // recognises one, and for .m4b it does not — which is how a book ends up named "book.mp4".
        var file = FindChild(tree, directory, fileName)
                   ?? DocumentsContract.CreateDocument(Resolver, directory, "application/octet-stream", fileName)
                   ?? throw new IOException(string.Format(Strings.Folder_CannotCreateFile, fileName));

        var stream = Resolver.OpenOutputStream(file, "wt")
                     ?? throw new IOException(string.Format(Strings.Folder_CannotWrite, fileName));

        return await Task.FromResult<(Stream, string)>((stream, file.ToString()!));
    }

    /// <summary>
    /// Finds a subfolder or makes one.
    ///
    /// Looked for first, because creating a folder that already exists does not fail — the provider
    /// quietly makes "Ed McDonald (1)" beside it. Download two books by the same author and you get
    /// two author folders, which is precisely what arranging them was meant to prevent.
    /// </summary>
    private static AndroidUri OpenOrCreateDirectory(AndroidUri tree, AndroidUri parent, string name)
    {
        if (FindChild(tree, parent, name) is { } existing) return existing;

        return DocumentsContract.CreateDocument(Resolver, parent, DocumentsContract.Document.MimeTypeDir, name)
               ?? throw new IOException(string.Format(Strings.Folder_CannotCreateFolder, name));
    }

    /// <summary>A child of this folder with that name, or null.</summary>
    private static AndroidUri? FindChild(AndroidUri tree, AndroidUri parent, string name)
    {
        var children = DocumentsContract.BuildChildDocumentsUriUsingTree(
            tree, DocumentsContract.GetDocumentId(parent));

        if (children is null) return null;

        string[] columns =
        [
            DocumentsContract.Document.ColumnDocumentId,
            DocumentsContract.Document.ColumnDisplayName,
        ];

        try
        {
            using var cursor = Resolver.Query(children, columns, null, null, null);
            if (cursor is null) return null;

            while (cursor.MoveToNext())
            {
                if (!string.Equals(cursor.GetString(1), name, StringComparison.OrdinalIgnoreCase)) continue;

                return DocumentsContract.BuildDocumentUriUsingTree(tree, cursor.GetString(0)!);
            }
        }
        catch (Exception ex)
        {
            AppLog.Info($"could not list '{parent}': {ex.Message}");
        }

        return null;
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
