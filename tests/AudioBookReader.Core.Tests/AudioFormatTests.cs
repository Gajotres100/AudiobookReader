using AudioBookReader.Core.Audio;

namespace AudioBookReader.Core.Tests;

/// <summary>
/// Covers the one thing that matters here: an audiobook whose name disagrees with its contents.
/// Real books arrive called "…m4b.mp3", and believing the name made the tag reader throw.
/// </summary>
public class AudioFormatTests
{
    private static byte[] Mp4Head()
    {
        var head = new byte[16];
        head[3] = 0x18;
        "ftypM4A "u8.CopyTo(head.AsSpan(4));
        return head;
    }

    [Fact]
    public void RecognisesAnMp4ContainerHoweverItIsNamed()
    {
        Assert.Equal(".m4b", AudioFormat.Sniff(Mp4Head()));
    }

    [Fact]
    public void PrefersTheContentToTheName()
    {
        using var stream = new MemoryStream(Mp4Head());

        Assert.Equal(".m4b", AudioFormat.ExtensionOf(stream, "The Licanius Trilogy.m4b.mp3"));
    }

    [Fact]
    public void LeavesTheStreamWhereItFoundIt()
    {
        // The caller hands this same stream to the tag reader, which starts from wherever it is
        // left. Sixteen bytes short of the front is sixteen bytes of missing header.
        using var stream = new MemoryStream(Mp4Head());

        AudioFormat.ExtensionOf(stream, "book.mp3");

        Assert.Equal(0, stream.Position);
    }

    [Theory]
    [InlineData(new byte[] { (byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0, 0, 0, 0, 0, 0 }, ".mp3")]
    [InlineData(new byte[] { 0xFF, 0xFB, 0x90, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, ".mp3")]
    [InlineData(new byte[] { (byte)'O', (byte)'g', (byte)'g', (byte)'S', 0, 0, 0, 0, 0, 0, 0, 0 }, ".ogg")]
    [InlineData(new byte[] { (byte)'f', (byte)'L', (byte)'a', (byte)'C', 0, 0, 0, 0, 0, 0, 0, 0 }, ".flac")]
    [InlineData(new byte[] { 0x30, 0x26, 0xB2, 0x75, 0, 0, 0, 0, 0, 0, 0, 0 }, ".wma")]
    public void RecognisesTheContainersTheAppAccepts(byte[] head, string expected)
    {
        Assert.Equal(expected, AudioFormat.Sniff(head));
    }

    [Fact]
    public void SaysNothingRatherThanGuessing()
    {
        // An ebook, picked by mistake. Falling back to the name is the caller's decision, not a
        // guess made here.
        Assert.Null(AudioFormat.Sniff("PK........"u8));
    }

    [Fact]
    public void FallsBackToTheNameWhenTheContentSaysNothing()
    {
        using var stream = new MemoryStream("not audio at all"u8.ToArray());

        Assert.Equal(".mp3", AudioFormat.ExtensionOf(stream, "book.mp3"));
    }
}
