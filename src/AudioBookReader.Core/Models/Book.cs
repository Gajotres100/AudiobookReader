using SQLite;

namespace AudioBookReader.Core.Models;

/// <summary>How far along the audio/text alignment is for a book.</summary>
public enum SyncState
{
    /// <summary>One of the two media is missing, so there is nothing to align.</summary>
    NotPaired,

    /// <summary>Both media present, alignment queued but not started.</summary>
    Pending,

    /// <summary>Alignment running. Chapters up to <see cref="Book.AlignedThroughChapter"/> are usable.</summary>
    InProgress,

    /// <summary>Alignment stopped before finishing. Already-aligned chapters stay usable.</summary>
    Partial,

    Complete,
    Failed,
}

/// <summary>
/// A title in the library. Either medium may be absent: a book can be audio only, text only, or
/// both, and either side can be attached or detached later without losing the other.
/// </summary>
[Table("books")]
public class Book
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed]
    public string? AudioPath { get; set; }

    [Indexed]
    public string? EbookPath { get; set; }

    public string Title { get; set; } = "";
    public string? Author { get; set; }
    public string? CoverPath { get; set; }

    /// <summary>Total audio length, or 0 for a text-only book.</summary>
    public long DurationMs { get; set; }

    /// <summary>Characters of extracted book text, or 0 for an audio-only book.</summary>
    public int TextLength { get; set; }

    /// <summary>Content hash of the audio file. With <see cref="EbookHash"/> it keys the sync map cache.</summary>
    public string? AudioHash { get; set; }

    public string? EbookHash { get; set; }

    public SyncState SyncState { get; set; } = SyncState.NotPaired;

    /// <summary>
    /// Highest chapter index whose alignment is finished, or -1 when none. Lets a run interrupted
    /// by the user, a reboot or thermal backoff resume instead of restarting.
    /// </summary>
    public int AlignedThroughChapter { get; set; } = -1;

    public DateTime AddedUtc { get; set; }
    public DateTime? LastOpenedUtc { get; set; }

    [Ignore]
    public bool HasAudio => !string.IsNullOrEmpty(AudioPath);

    [Ignore]
    public bool HasText => !string.IsNullOrEmpty(EbookPath);

    /// <summary>Both media present, so alignment is possible (though maybe not done yet).</summary>
    [Ignore]
    public bool IsPaired => HasAudio && HasText;

    /// <summary>True once at least one chapter is aligned, so the reader can follow along.</summary>
    [Ignore]
    public bool HasUsableSync => IsPaired && AlignedThroughChapter >= 0;
}
