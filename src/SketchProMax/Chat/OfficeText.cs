using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace SketchProMax.Chat;

/// <summary>
/// What the chat reads from an attached Word (.docx) or PowerPoint (.pptx) file: the text in reading order (headings,
/// paragraphs, tables as rows of cells, slides one by one) and the pictures in it. Both are zip files of XML, so no
/// Office or other library is needed. Older .doc and .ppt files are not read.
/// </summary>
internal static class OfficeText
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";

    /// <summary>Pictures smaller than this are logos, bullets and icons: left out.</summary>
    public const int MinPictureBytes = 15_000;

    public sealed record Content(string Text, List<(string Name, byte[] Bytes)> Pictures);

    public static bool IsOffice(string path) => Path.GetExtension(path).ToLowerInvariant() is ".docx" or ".pptx";

    /// <summary>The text and pictures of a .docx or .pptx. Throws InvalidDataException when it isn't one.</summary>
    public static Content Read(string path)
    {
        using var zip = OpenZip(File.ReadAllBytes(path));
        var word = Path.GetExtension(path).Equals(".docx", StringComparison.OrdinalIgnoreCase);
        var text = word ? WordText(zip) : SlidesText(zip);
        var media = word ? "word/media/" : "ppt/media/";
        var pictures = new List<(string, byte[])>();
        foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith(media, StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.FullName, StringComparer.Ordinal))
        {
            var ext = Path.GetExtension(entry.Name).ToLowerInvariant();
            if (ext is not (".png" or ".jpg" or ".jpeg" or ".gif") || entry.Length < MinPictureBytes) continue;
            using var input = entry.Open();
            using var copy = new MemoryStream();
            input.CopyTo(copy);
            pictures.Add((entry.Name, copy.ToArray()));
        }
        return new Content(text, pictures);
    }

    public static Content Read(byte[] bytes, bool word)
    {
        using var zip = OpenZip(bytes);
        return new Content(word ? WordText(zip) : SlidesText(zip), []);
    }

    private static ZipArchive OpenZip(byte[] bytes)
    {
        try { return new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read); }
        catch (InvalidDataException) { throw new InvalidDataException("it isn't a Word or PowerPoint file in the current format (.docx or .pptx)"); }
    }

    private static XDocument? Xml(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name);
        if (entry is null) return null;
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XDocument.Load(reader);
    }

    // ---- Word ---------------------------------------------------------------------------------------------------------

    private static string WordText(ZipArchive zip)
    {
        var body = Xml(zip, "word/document.xml")?.Root?.Element(W + "body")
                   ?? throw new InvalidDataException("it has no document text (word/document.xml)");
        var sb = new StringBuilder();
        foreach (var block in body.Elements())
        {
            if (block.Name == W + "p") Paragraph(sb, block);
            else if (block.Name == W + "tbl") Table(sb, block);
            else if (block.Name == W + "sdt") foreach (var p in block.Descendants(W + "p")) Paragraph(sb, p);
        }
        return Tidy(sb.ToString());
    }

    private static void Paragraph(StringBuilder sb, XElement p)
    {
        var line = Runs(p);
        if (line.Length == 0) { sb.Append('\n'); return; }
        var style = p.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val")?.Value ?? "";
        var heading = Regex.Match(style, @"^(?:Heading|heading)\s?(\d)$");
        if (heading.Success) sb.Append(new string('#', Math.Min(6, int.Parse(heading.Groups[1].Value)))).Append(' ');
        else if (style.Equals("Title", StringComparison.OrdinalIgnoreCase)) sb.Append("# ");
        else if (p.Element(W + "pPr")?.Element(W + "numPr") is not null || style.StartsWith("List", StringComparison.OrdinalIgnoreCase)) sb.Append("- ");
        sb.Append(line).Append('\n');
    }

    private static void Table(StringBuilder sb, XElement table)
    {
        sb.Append('\n');
        foreach (var row in table.Elements(W + "tr"))
            sb.Append("| ").Append(string.Join(" | ", row.Elements(W + "tc").Select(c =>
                string.Join(" ", c.Descendants(W + "p").Select(Runs).Where(t => t.Length > 0)).Replace("|", "/")))).Append(" |\n");
        sb.Append('\n');
    }

    /// <summary>A paragraph's text: its runs, tabs and line breaks, and inserted (tracked) text, without deleted text.</summary>
    private static string Runs(XElement p)
    {
        var sb = new StringBuilder();
        foreach (var e in p.Descendants())
        {
            if (e.Ancestors(W + "del").Any()) continue;
            if (e.Name == W + "t") sb.Append(e.Value);
            else if (e.Name == W + "tab") sb.Append('\t');
            else if (e.Name == W + "br" || e.Name == W + "cr") sb.Append('\n');
        }
        return sb.ToString().Trim();
    }

    // ---- PowerPoint ---------------------------------------------------------------------------------------------------

    private static string SlidesText(ZipArchive zip)
    {
        var slides = SlideOrder(zip);
        if (slides.Count == 0) throw new InvalidDataException("it has no slides (ppt/slides)");
        var sb = new StringBuilder();
        for (var i = 0; i < slides.Count; i++)
        {
            sb.Append(i == 0 ? "" : "\n").Append($"=== Slide {i + 1} of {slides.Count} ===\n");
            var doc = Xml(zip, slides[i]);
            if (doc?.Root is null) continue;
            foreach (var p in doc.Root.Descendants(A + "p"))
            {
                var line = string.Concat(p.Descendants().Select(e => e.Name == A + "t" ? e.Value : e.Name == A + "br" ? "\n" : "")).Trim();
                if (line.Length > 0) sb.Append(line).Append('\n');
            }
        }
        return Tidy(sb.ToString());
    }

    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Rels = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>
    /// The slides as the presentation shows them (presentation.xml's slide list, which leaves out none and follows any
    /// reordering), falling back to the slide files by number.
    /// </summary>
    private static List<string> SlideOrder(ZipArchive zip)
    {
        var files = zip.Entries
            .Select(e => (Name: e.FullName, Match: Regex.Match(e.FullName, @"^ppt/slides/slide(\d+)\.xml$", RegexOptions.IgnoreCase)))
            .Where(x => x.Match.Success)
            .OrderBy(x => int.Parse(x.Match.Groups[1].Value))
            .Select(x => x.Name)
            .ToList();
        try
        {
            var targets = Xml(zip, "ppt/_rels/presentation.xml.rels")?.Root?.Elements(Rels + "Relationship")
                .ToDictionary(r => (string?)r.Attribute("Id") ?? "", r => "ppt/" + ((string?)r.Attribute("Target") ?? "").TrimStart('/').Replace("ppt/", ""));
            var listed = Xml(zip, "ppt/presentation.xml")?.Root?.Element(P + "sldIdLst")?.Elements(P + "sldId")
                .Select(id => targets is not null && targets.TryGetValue((string?)id.Attribute(R + "id") ?? "", out var t) ? t : null)
                .Where(t => t is not null && zip.GetEntry(t) is not null).Select(t => t!).ToList();
            if (listed is { Count: > 0 }) return listed;
        }
        catch (Exception ex) when (ex is XmlException or ArgumentException or InvalidDataException) { }
        return files;
    }

    private static string Tidy(string text) => Regex.Replace(text, @"\n{3,}", "\n\n").Trim();

    /// <summary>
    /// A small .docx (a heading, a paragraph, a bullet and a two-row table) or .pptx (two slides) for the tests and the
    /// field test: only the parts this class reads.
    /// </summary>
    public static byte[] Sample(bool word)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string xml)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(xml);
            }
            if (word)
                Add("word/document.xml", $$"""
                    <?xml version="1.0" encoding="UTF-8"?>
                    <w:document xmlns:w="{{W}}"><w:body>
                      <w:p><w:pPr><w:pStyle w:val="Heading1"/></w:pPr><w:r><w:t>Concrete works</w:t></w:r></w:p>
                      <w:p><w:r><w:t xml:space="preserve">Slabs are </w:t></w:r><w:r><w:t>C40.</w:t></w:r><w:del><w:r><w:t>C30</w:t></w:r></w:del></w:p>
                      <w:p><w:pPr><w:numPr/></w:pPr><w:r><w:t>Cover 40 mm</w:t></w:r></w:p>
                      <w:tbl><w:tr><w:tc><w:p><w:r><w:t>Element</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>Grade</w:t></w:r></w:p></w:tc></w:tr>
                             <w:tr><w:tc><w:p><w:r><w:t>Columns</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>C50</w:t></w:r></w:p></w:tc></w:tr></w:tbl>
                    </w:body></w:document>
                    """);
            else
                for (var n = 1; n <= 2; n++)
                    Add($"ppt/slides/slide{n}.xml", $$"""
                        <?xml version="1.0" encoding="UTF-8"?>
                        <p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="{{A}}"><p:cSld><p:spTree>
                          <p:sp><p:txBody><a:p><a:r><a:t>Slide {{n}} title</a:t></a:r></a:p><a:p><a:r><a:t>Point </a:t></a:r><a:r><a:t>{{n}}</a:t></a:r></a:p></p:txBody></p:sp>
                        </p:spTree></p:cSld></p:sld>
                        """);
        }
        return output.ToArray();
    }
}
