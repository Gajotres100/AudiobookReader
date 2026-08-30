using System.IO.Compression;
using System.Text;

namespace AudioBookReader.Core.Tests;

/// <summary>
/// Writes a minimal but genuine EPUB 3 file so extraction tests exercise the real reader rather
/// than a stand-in. Bugs in this area come from how real containers are laid out, which a mock
/// would hide.
/// </summary>
public sealed class TestEpubBuilder
{
    public record Section(string Id, string NavTitle, string BodyHtml, string? Anchor = null);

    private readonly List<Section> _sections = [];

    public string Title { get; init; } = "Test Book";
    public string Author { get; init; } = "Test Author";

    public TestEpubBuilder Add(string id, string navTitle, string bodyHtml, string? anchor = null)
    {
        _sections.Add(new Section(id, navTitle, bodyHtml, anchor));
        return this;
    }

    public string WriteTo(string path)
    {
        using var stream = File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);

        // The mimetype entry must come first and be stored uncompressed.
        WriteEntry(zip, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);

        WriteEntry(zip, "META-INF/container.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
              <rootfiles>
                <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
              </rootfiles>
            </container>
            """);

        WriteEntry(zip, "OEBPS/content.opf", BuildPackage());
        WriteEntry(zip, "OEBPS/nav.xhtml", BuildNavigation());

        foreach (var section in _sections)
            WriteEntry(zip, $"OEBPS/{section.Id}.xhtml", BuildSection(section));

        return path;
    }

    private string BuildPackage()
    {
        var manifest = new StringBuilder();
        var spine = new StringBuilder();

        foreach (var section in _sections)
        {
            manifest.AppendLine($"""    <item id="{section.Id}" href="{section.Id}.xhtml" media-type="application/xhtml+xml"/>""");
            spine.AppendLine($"""    <itemref idref="{section.Id}"/>""");
        }

        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="bookid">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:identifier id="bookid">urn:uuid:audiobookreader-test</dc:identifier>
                <dc:title>{Title}</dc:title>
                <dc:language>en</dc:language>
                <dc:creator>{Author}</dc:creator>
              </metadata>
              <manifest>
                <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
            {manifest}  </manifest>
              <spine>
            {spine}  </spine>
            </package>
            """;
    }

    private string BuildNavigation()
    {
        var entries = new StringBuilder();
        foreach (var section in _sections)
        {
            var href = section.Anchor is null ? $"{section.Id}.xhtml" : $"{section.Id}.xhtml#{section.Anchor}";
            entries.AppendLine($"""      <li><a href="{href}">{section.NavTitle}</a></li>""");
        }

        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
            <head><title>Contents</title></head>
            <body>
              <nav epub:type="toc" id="toc">
                <ol>
            {entries}    </ol>
              </nav>
            </body>
            </html>
            """;
    }

    private static string BuildSection(Section section) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <html xmlns="http://www.w3.org/1999/xhtml">
        <head><title>{section.NavTitle}</title></head>
        <body>
        {section.BodyHtml}
        </body>
        </html>
        """;

    private static void WriteEntry(ZipArchive zip, string name, string content, CompressionLevel level = CompressionLevel.Optimal)
    {
        var entry = zip.CreateEntry(name, level);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
