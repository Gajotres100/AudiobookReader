using Android.Content;
using Microsoft.Win32.SafeHandles;
using AndroidUri = Android.Net.Uri;

namespace AudioBookReader.App.Services;

/// <summary>
/// Lets a book be played where the user keeps it, instead of being copied into app storage.
///
/// Copying a book was costing half a minute and twice the storage for something the system is
/// perfectly willing to hand over in place. What made copying look necessary was the belief that a
/// picker's permission dies with the process — it does, unless the grant is taken persistably,
/// which is exactly what this does.
///
/// The reference is kept as the URI's own text, so a book's stored location is either an ordinary
/// path or a <c>content://</c> URI and the difference is visible wherever it matters.
/// </summary>
public partial class MediaReferences
{
    private static Context Context => global::Android.App.Application.Context;

    private partial bool IsReferenceCore(string location) =>
        location.StartsWith("content://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Asks to keep reading this file indefinitely.
    ///
    /// Returns false when the system will not promise that — some providers hand out one-shot
    /// grants, and a cloud provider may be streaming rather than holding a file at all. A refusal
    /// is not an error: it means this book has to be copied in after all.
    /// </summary>
    private partial bool TryHoldCore(string location)
    {
        if (!IsReference(location)) return false;

        try
        {
            Context.ContentResolver?.TakePersistableUriPermission(
                AndroidUri.Parse(location)!, ActivityFlags.GrantReadUriPermission);

            return true;
        }
        catch (Java.Lang.SecurityException)
        {
            // A document inside a folder the app already holds: the grant is on the tree, not on
            // this URI, so taking one for it is refused — while reading it is not. If it opens, it
            // is ours to keep, and copying it in would be a second copy of a book the user can see
            // in their own folder.
            if (CanOpen(location)) return true;

            AppLog.Info($"no persistable permission for {location}; will copy instead");
            return false;
        }
    }

    private static bool CanOpen(string location)
    {
        try
        {
            using var descriptor = Context.ContentResolver?.OpenFileDescriptor(
                AndroidUri.Parse(location)!, "r");

            return descriptor is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private partial void ReleaseCore(string location)
    {
        if (!IsReference(location)) return;

        try
        {
            Context.ContentResolver?.ReleasePersistableUriPermission(
                AndroidUri.Parse(location)!, ActivityFlags.GrantReadUriPermission);
        }
        catch (Java.Lang.SecurityException)
        {
            // Already gone, which is the state we wanted anyway.
        }
    }

    /// <summary>
    /// Opens the file for reading, seekably.
    ///
    /// Through a file descriptor rather than <c>OpenInputStream</c> because the callers seek: the
    /// identity hash samples three places in the file, and the tag reader jumps around looking for
    /// chapter marks. A plain input stream from a provider is forward-only.
    /// </summary>
    private partial bool TryDeleteCore(string location)
    {
        if (!IsReference(location)) return false;

        var uri = AndroidUri.Parse(location)!;

        try
        {
            // The provider decides. A document from a tree the user granted is deletable; one from
            // a cloud provider that only streams may not be, and saying so is better than pretending.
            var deleted = global::Android.Provider.DocumentsContract.DeleteDocument(Context.ContentResolver!, uri);

            AppLog.Info($"reference: delete {(deleted ? "succeeded" : "refused")} for {location}");
            return deleted;
        }
        catch (Java.Lang.SecurityException ex) when (ThroughGrantedFolder(uri) is { } inFolder)
        {
            // A file picked on its own is granted for reading, not deleting — but the same file is
            // often inside a folder the user granted whole, and that grant may delete it. A book
            // imported as a single file before its folder was granted left its audiobook behind,
            // 1.3 GB of it, after "delete the file too".
            try
            {
                var deleted = global::Android.Provider.DocumentsContract.DeleteDocument(Context.ContentResolver!, inFolder);
                AppLog.Info($"reference: delete {(deleted ? "succeeded" : "refused")} for {location} through its folder ({ex.Message})");
                return deleted;
            }
            catch (Exception again)
            {
                AppLog.Info($"reference: delete failed for {location}, also through its folder ({again.GetType().Name}: {again.Message})");
                return false;
            }
        }
        catch (Exception ex)
        {
            AppLog.Info($"reference: delete failed for {location} ({ex.GetType().Name}: {ex.Message})");
            return false;
        }
    }

    /// <summary>
    /// The same document reached through a folder the user granted with write access, or null —
    /// a tree whose own document is an ancestor of this one, in the same provider.
    /// </summary>
    private static AndroidUri? ThroughGrantedFolder(AndroidUri document)
    {
        try
        {
            if (!global::Android.Provider.DocumentsContract.IsDocumentUri(Context, document)) return null;

            var id = global::Android.Provider.DocumentsContract.GetDocumentId(document);
            if (string.IsNullOrEmpty(id)) return null;

            foreach (var grant in Context.ContentResolver!.PersistedUriPermissions)
            {
                if (!grant.IsWritePermission || grant.Uri is not { } tree || tree.Authority != document.Authority) continue;
                if (!(tree.PathSegments is { Count: >= 2 } segments && segments[0] == "tree")) continue;

                var treeId = global::Android.Provider.DocumentsContract.GetTreeDocumentId(tree);
                if (string.IsNullOrEmpty(treeId)) continue;

                // "primary:Audiobooks" holds "primary:Audiobooks/Author/Book.m4b"; the root of a
                // volume, "primary:", holds everything on it.
                var inside = id == treeId
                             || id.StartsWith(treeId.EndsWith(':') ? treeId : treeId + "/", StringComparison.Ordinal);

                if (inside) return global::Android.Provider.DocumentsContract.BuildDocumentUriUsingTree(tree, id);
            }
        }
        catch (Exception ex)
        {
            AppLog.Info($"reference: no folder grant found for {document} ({ex.GetType().Name}: {ex.Message})");
        }

        return null;
    }

    private partial Stream OpenReadCore(string location)
    {
        if (!IsReference(location)) return File.OpenRead(location);

        var descriptor = Context.ContentResolver?.OpenFileDescriptor(AndroidUri.Parse(location)!, "r")
            ?? throw new IOException($"Could not open {location}.");

        // Owned by the ParcelFileDescriptor, so the handle must not close it — disposing the
        // stream releases the descriptor through the wrapper below.
        var handle = new SafeFileHandle(descriptor.Fd, ownsHandle: false);

        return new DescriptorStream(new FileStream(handle, FileAccess.Read), descriptor);
    }

    /// <summary>Keeps the descriptor alive for as long as the stream reading it.</summary>
    private sealed class DescriptorStream(FileStream inner, global::Android.OS.ParcelFileDescriptor descriptor)
        : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                descriptor.Close();
                descriptor.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
