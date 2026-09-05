using System.Text;
using AudioBookReader.Core.Books;

namespace AudioBookReader.Core.Tests;

/// <summary>
/// Text files do not say what encoding they are in, and guessing wrong is silent.
///
/// The failure this guards against reported success: a Croatian file in the region's usual legacy
/// encoding decoded to a page of replacement characters, the import checked only that the text was
/// not empty, and alignment then could not match any word carrying a diacritic.
/// </summary>
public class PlainTextEncodingTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("abr-encoding-tests").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private const string Croatian =
        "Jutro je bilo hladno i kuća je bila tiha. On je čekao kraj prozora držeći njezino pismo. " +
        "Šuma iza kuće bila je puna đačkih glasova, a čaj se hladio na stolu.";

    private async Task<string> ExtractAsync(byte[] bytes, string name)
    {
        var path = Path.Combine(_dir, name);
        await File.WriteAllBytesAsync(path, bytes);

        var extracted = await new PlainTextExtractor().ExtractAsync(path);
        return extracted.Text.PlainText;
    }

    [Fact]
    public async Task ReadsUtf8()
    {
        var text = await ExtractAsync(Encoding.UTF8.GetBytes(Croatian), "utf8.txt");
        Assert.Contains("kuća", text);
        Assert.DoesNotContain('\uFFFD', text);
    }

    [Fact]
    public async Task ReadsALegacyCentralEuropeanFileRatherThanFillingItWithReplacements()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        byte[] bytes;
        try
        {
            bytes = Encoding.GetEncoding(1250).GetBytes(Croatian);
        }
        catch (Exception)
        {
            return; // No code pages on this host; nothing to assert.
        }

        var text = await ExtractAsync(bytes, "cp1250.txt");

        Assert.DoesNotContain('\uFFFD', text);
        Assert.Contains("kuća", text);
        Assert.Contains("đačkih", text);
    }

    [Fact]
    public async Task HonoursAByteOrderMark()
    {
        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(Croatian)).ToArray();

        var text = await ExtractAsync(utf16, "utf16.txt");

        Assert.Contains("kuća", text);
        Assert.DoesNotContain('\0', text);
    }
}
