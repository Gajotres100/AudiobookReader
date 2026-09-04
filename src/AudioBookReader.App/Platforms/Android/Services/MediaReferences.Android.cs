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

    public partial bool IsReference(string location) =>
        location.StartsWith("content://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Asks to keep reading this file indefinitely.
    ///
    /// Returns false when the system will not promise that — some providers hand out one-shot
    /// grants, and a cloud provider may be streaming rather than holding a file at all. A refusal
    /// is not an error: it means this book has to be copied in after all.
    /// </summary>
    public partial bool TryHold(string location)
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
            AppLog.Info($"no persistable permission for {location}; will copy instead");
            return false;
        }
    }

    public partial void Release(string location)
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
    public partial Stream OpenRead(string location)
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
