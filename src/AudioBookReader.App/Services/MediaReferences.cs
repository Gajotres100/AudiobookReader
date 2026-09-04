namespace AudioBookReader.App.Services;

/// <summary>
/// Where a book's files live, and whether the app owns them.
///
/// A book is either <em>referenced</em> — left where the user keeps it, which makes importing
/// instant and costs no storage — or <em>held</em>, copied into app storage. Listening only ever
/// needs a reference. Alignment wants a copy: it reads the file in short bursts for hours, and a
/// reference can be to a cloud document that would be streamed each time, or to a card that can be
/// taken out mid-run.
/// </summary>
public partial class MediaReferences
{
    /// <summary>True when this location belongs to the user rather than to the app.</summary>
    public partial bool IsReference(string location);

    /// <summary>Takes a permission that outlives the process. False when the system will not give one.</summary>
    public partial bool TryHold(string location);

    /// <summary>Gives back a permission, when a book that used it is deleted.</summary>
    public partial void Release(string location);

    /// <summary>Opens the file for reading, wherever it is. The stream is seekable.</summary>
    public partial Stream OpenRead(string location);
}
