using SQLite;

namespace AudioBookReader.Core.Models;

/// <summary>
/// A saved place in a book. Carries whichever coordinates were known when it was made — a
/// bookmark set while listening to an unpaired audiobook has only a timestamp, one set while
/// reading has only a text offset, and one set on a synced book has both.
///
/// Bookmarks survive detaching a medium: re-attaching the same file restores their meaning, and
/// a coordinate that currently resolves to nothing simply is not shown.
/// </summary>
[Table("bookmarks")]
public class Bookmark
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed]
    public int BookId { get; set; }

    public long? PositionMs { get; set; }

    public int? TextOffset { get; set; }

    /// <summary>Chapter index at this position, denormalized so the list renders without a join.</summary>
    public int ChapterIndex { get; set; }

    public string? Note { get; set; }

    /// <summary>
    /// Snippet of book text at this position when the text is available. Makes a bookmark
    /// recognizable in the list instead of showing only a timestamp.
    /// </summary>
    public string? TextPreview { get; set; }

    public DateTime CreatedUtc { get; set; }

    [Ignore]
    public bool IsResolvable => PositionMs is not null || TextOffset is not null;
}
