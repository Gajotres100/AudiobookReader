using System.Net;
using AudioBookReader.Core.Data;
using AudioBookReader.Core.Models;

namespace AudioBookReader.Core.Tests;

public class AlignmentFileTests
{
    private static AlignmentPackage Package(string? key = null)
    {
        var map = new SyncMap { AudioHash = "audio", EbookHash = "ebook" };
        map.SetChapter(new ChapterSyncMap { ChapterIndex = 0, Anchors = [new(1000, 0, 1f), new(5000, 60, 1f)] });

        return new AlignmentPackage("Galaxy S22", "Torch", "audio", "ebook", [new ChapterRange(0, 0, 60)], map, key);
    }

    [Fact]
    public async Task A_package_survives_the_trip_through_a_file()
    {
        using var file = new MemoryStream();
        await AlignmentFile.WriteAsync(file, Package());

        file.Position = 0;
        var read = await AlignmentFile.ReadAsync(file);

        Assert.NotNull(read);
        Assert.Equal("Torch", read.Title);
        Assert.True(read.Map.MatchesPair("audio", "ebook"));
        Assert.Equal(2, read.Map.ForChapter(0)!.Anchors.Count);
        Assert.Equal(60, read.Chapters[0].TextEnd);
    }

    [Fact]
    public async Task Something_else_is_not_taken_for_an_alignment()
    {
        using var junk = new MemoryStream("this is a photo, honestly"u8.ToArray());
        Assert.Null(await AlignmentFile.ReadAsync(junk));
    }

    [Fact]
    public void The_file_name_is_safe_anywhere() =>
        Assert.Equal("Torch_ The Burning.syncbook", AlignmentFile.FileNameFor("Torch: The Burning"));

    [Fact]
    public void A_pairing_code_reads_back_as_written()
    {
        var code = new PairingCode(
            [IPAddress.Parse("192.168.1.20"), IPAddress.Parse("10.0.0.7")], 47351, PairingCode.NewKey(), "Nikola's iPad");

        var read = PairingCode.Parse(code.ToString());

        Assert.NotNull(read);
        Assert.Equal(code.Addresses, read.Addresses);
        Assert.Equal(47351, read.Port);
        Assert.Equal(code.Key, read.Key);
        Assert.Equal("Nikola's iPad", read.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://example.com")]
    [InlineData("syncbook://pair?a=&p=47351&k=AB")]
    [InlineData("syncbook://pair?a=192.168.1.2&p=0&k=AB")]
    [InlineData("syncbook://pair?a=192.168.1.2&p=47351")]
    public void Anything_else_in_a_QR_code_is_not_a_pairing_code(string? text) =>
        Assert.Null(PairingCode.Parse(text));

    [Fact]
    public void Two_keys_are_never_the_same() => Assert.NotEqual(PairingCode.NewKey(), PairingCode.NewKey());
}
