using AudioBookReader.Core.Books;
using AudioBookReader.Core.Models;

namespace AudioBookReader.Core.Tests;

public class BookNamesTests
{
    private static Book Audio(int id, string path, string? source = null) =>
        new() { Id = id, AudioPath = path, SourceName = source };

    private static Book Text(int id, string path, string? source = null) =>
        new() { Id = id, EbookPath = path, SourceName = source };

    [Theory]
    [InlineData("Torch.m4b", "torch")]
    [InlineData("Torch_(Unabridged).epub", "torch unabridged")]
    [InlineData("  Stepski  vuk - Hesse.mp3", "stepski vuk hesse")]
    public void Normalize_ignores_extension_case_and_punctuation(string fileName, string expected) =>
        Assert.Equal(expected, BookNames.Normalize(fileName));

    [Fact]
    public void FileNameOf_reads_an_escaped_document_uri() =>
        Assert.Equal(
            "Torch.m4b",
            BookNames.FileNameOf("content://com.android.externalstorage.documents/document/primary%3AAudiobooks%2FTorch.m4b"));

    [Fact]
    public void An_ebook_joins_the_audiobook_with_the_same_name()
    {
        var shelf = new[]
        {
            Audio(1, "content://com.android.externalstorage.documents/document/primary%3ADownload%2FTorch.m4b"),
            Audio(2, "/data/audio/Other Book.m4b"),
        };

        Assert.Equal(1, BookNames.FindPartner(shelf, "Torch.epub", isAudio: false)?.Id);
    }

    [Fact]
    public void An_audiobook_joins_the_ebook_even_when_the_copy_was_renamed()
    {
        var shelf = new[] { Text(7, "/data/books/Torch (2).epub") };

        Assert.Equal(7, BookNames.FindPartner(shelf, "Torch.m4b", isAudio: true)?.Id);
    }

    [Fact]
    public void The_remembered_name_counts_when_the_path_is_only_a_number()
    {
        var shelf = new[] { Audio(3, "content://media/external/audio/media/1234", source: "Torch") };

        Assert.Equal(3, BookNames.FindPartner(shelf, "Torch.epub", isAudio: false)?.Id);
    }

    [Fact]
    public void A_book_that_already_has_that_half_is_not_a_partner()
    {
        var shelf = new[] { new Book { Id = 1, AudioPath = "/a/Torch.m4b", EbookPath = "/b/Torch.epub" } };

        Assert.Null(BookNames.FindPartner(shelf, "Torch.epub", isAudio: false));
        Assert.Null(BookNames.FindPartner(shelf, "Torch.m4b", isAudio: true));
    }

    [Fact]
    public void Two_candidates_are_not_guessed_between()
    {
        var shelf = new[] { Audio(1, "/a/Torch.m4b"), Audio(2, "/b/Torch.mp3") };

        Assert.Null(BookNames.FindPartner(shelf, "Torch.epub", isAudio: false));
    }

    [Fact]
    public void A_different_name_is_a_different_book() =>
        Assert.Null(BookNames.FindPartner([Audio(1, "/a/Torch.m4b")], "Torchlight.epub", isAudio: false));
}
