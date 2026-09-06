namespace AudioBookReader.Core.Audio;

/// <summary>
/// Works out what an audio file actually is, from its first bytes rather than its name.
///
/// Names lie, and audiobooks are where they lie most: a file arrives called
/// "…The Licanius Trilogy.m4b.mp3" and is an MP4 container throughout. The tag reader is told what
/// it is holding by extension, so it picked its MP3 reader for an MP4 stream and threw a
/// NullReferenceException forty seconds into the import — after the whole file had been read over
/// the content provider, and with nothing on screen to say what had happened.
///
/// Every container this app accepts announces itself in its first few bytes, so there is no reason
/// to ask the name.
/// </summary>
public static class AudioFormat
{
    /// <summary>How much of the head is needed to recognise any of them.</summary>
    private const int SignatureBytes = 16;

    /// <summary>
    /// The extension the content says it is, or null when nothing recognises it.
    ///
    /// Returns an extension rather than a format name because that is what the tag reader wants,
    /// and because it keeps the fallback — the file's own name — the same kind of thing.
    /// </summary>
    public static string? Sniff(ReadOnlySpan<byte> head)
    {
        if (head.Length < 12) return null;

        // MP4 family: a box length, then "ftyp". Covers .m4b, .m4a and .mp4 alike; which of the
        // three it is depends on the brand and on nothing this app cares about.
        if (head[4] == 'f' && head[5] == 't' && head[6] == 'y' && head[7] == 'p') return ".m4b";

        if (head[0] == 'I' && head[1] == 'D' && head[2] == '3') return ".mp3";

        // A bare MPEG frame: eleven set bits of sync. An MP3 with no tag starts this way.
        if (head[0] == 0xFF && (head[1] & 0xE0) == 0xE0) return ".mp3";

        if (head[0] == 'O' && head[1] == 'g' && head[2] == 'g' && head[3] == 'S') return ".ogg";
        if (head[0] == 'f' && head[1] == 'L' && head[2] == 'a' && head[3] == 'C') return ".flac";

        if (head[0] == 'R' && head[1] == 'I' && head[2] == 'F' && head[3] == 'F'
            && head[8] == 'W' && head[9] == 'A' && head[10] == 'V' && head[11] == 'E')
            return ".wav";

        // ASF, which is what a .wma is.
        if (head[0] == 0x30 && head[1] == 0x26 && head[2] == 0xB2 && head[3] == 0x75) return ".wma";

        return null;
    }

    /// <summary>
    /// What to tell the tag reader for this stream. Leaves the stream where it found it.
    /// </summary>
    public static string ExtensionOf(Stream stream, string fileName)
    {
        var fallback = Path.GetExtension(fileName);

        if (!stream.CanSeek) return fallback;

        var start = stream.Position;

        try
        {
            Span<byte> head = stackalloc byte[SignatureBytes];
            stream.ReadExactly(head);

            return Sniff(head) ?? fallback;
        }
        catch (EndOfStreamException)
        {
            // Too short to be audio at all; let the reader fail on its own terms.
            return fallback;
        }
        finally
        {
            stream.Position = start;
        }
    }

    /// <summary>The same question for a file on disk.</summary>
    public static string ExtensionOf(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return ExtensionOf(stream, path);
        }
        catch (IOException)
        {
            return Path.GetExtension(path);
        }
    }
}
