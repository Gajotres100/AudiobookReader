using SQLite;

namespace AudioBookReader.Core.Models;

/// <summary>
/// A chapter of a book. Which halves are filled in depends on what the book has: an audio-only
/// book has times but no text range, a text-only book the reverse, a paired book both.
/// </summary>
[Table("chapters")]
public class Chapter
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed]
    public int BookId { get; set; }

    /// <summary>Zero-based position in the book. Matches <see cref="ChapterSyncMap.ChapterIndex"/>.</summary>
    public int Index { get; set; }

    public string Title { get; set; } = "";

    public long? StartMs { get; set; }
    public long? EndMs { get; set; }

    /// <summary>Range in the book's plain text that this chapter covers, when the text is known.</summary>
    public int? TextStart { get; set; }

    public int? TextEnd { get; set; }

    [Ignore]
    public bool HasAudioRange => StartMs is not null && EndMs is not null;

    [Ignore]
    public bool HasTextRange => TextStart is not null && TextEnd is not null;

    [Ignore]
    public long DurationMs => HasAudioRange ? EndMs!.Value - StartMs!.Value : 0;
}
