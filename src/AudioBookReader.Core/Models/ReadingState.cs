using SQLite;

namespace AudioBookReader.Core.Models;

/// <summary>
/// Where the user left off in a book, and their per-book preferences. One row per book.
///
/// Both positions are tracked independently because a book may have only one medium, and because
/// a paired book can be read silently or listened to without the text open. When the two are
/// paired and synced, whichever position moved last updates the other.
/// </summary>
[Table("reading_state")]
public class ReadingState
{
    [PrimaryKey]
    public int BookId { get; set; }

    /// <summary>Playback position, or null if the book has never been listened to.</summary>
    public long? AudioPositionMs { get; set; }

    /// <summary>Offset into the book's plain text, or null if it has never been read.</summary>
    public int? TextOffset { get; set; }

    public float Speed { get; set; } = 1.0f;

    public DateTime UpdatedUtc { get; set; }
}
