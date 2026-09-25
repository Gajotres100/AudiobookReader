namespace AudioBookReader.App.Services;

/// <summary>
/// Where a book's files live, and whether the app owns them.
///
/// A book is either <em>referenced</em> — left where the user keeps it, which makes importing
/// instant and costs no storage — or <em>held</em>, copied into app storage. Listening only ever
/// needs a reference. Alignment wants a copy: it reads the file in short bursts for hours, and a
/// reference can be to a cloud document that would be streamed each time, or to a card that can be
/// taken out mid-run.
///
/// Audio streamed from an Audiobookshelf server is a reference too (see <see cref="StreamedAudio"/>),
/// answered here before the platform is asked: the server's file is never the app's to delete, and
/// reading it means asking the server.
/// </summary>
public partial class MediaReferences
{
    /// <summary>True when this location belongs to the user rather than to the app.</summary>
    public bool IsReference(string location) =>
        StreamedAudio.Is(location) || IsReferenceCore(location);

    /// <summary>Takes a permission that outlives the process. False when the system will not give one.</summary>
    public bool TryHold(string location) =>
        StreamedAudio.Is(location) || TryHoldCore(location);

    /// <summary>Gives back a permission, when a book that used it is deleted.</summary>
    public void Release(string location)
    {
        if (!StreamedAudio.Is(location)) ReleaseCore(location);
    }

    /// <summary>Opens the file for reading, wherever it is. The stream is seekable.</summary>
    public Stream OpenRead(string location)
    {
        if (!StreamedAudio.Is(location)) return OpenReadCore(location);

        var source = StreamedAudio.Source ?? throw new InvalidOperationException("Not connected to the server.");

        // Blocking by necessity — the callers expect a stream in hand — and only ever reached from
        // a worker: hashing, and copying a book in for alignment.
        return source.OpenAsync(location).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Deletes the user's own file, when they have asked for that.
    ///
    /// Separate from <see cref="Release"/>, which only gives back the permission, because the two
    /// are different decisions and only one of them is destructive. Returns false rather than
    /// throwing when the provider refuses — a grant can lapse and a file can be moved, and the
    /// library entry still has to go either way.
    ///
    /// Never for a streamed book: that file is the server's, and removing a book from this device
    /// must not reach into anyone's library on the server.
    /// </summary>
    public bool TryDelete(string location) =>
        !StreamedAudio.Is(location) && TryDeleteCore(location);

    private partial bool IsReferenceCore(string location);
    private partial bool TryHoldCore(string location);
    private partial void ReleaseCore(string location);
    private partial Stream OpenReadCore(string location);
    private partial bool TryDeleteCore(string location);
}
