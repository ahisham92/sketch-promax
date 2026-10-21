using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace SketchProMax.Chat;

/// <summary>
/// What the chat reads from an attached Word (.docx), PowerPoint (.pptx) or Excel (.xlsx, .xlsm) file: the text in reading
/// order (headings, paragraphs, tables as rows of cells, slides one by one, sheets as tables with their row numbers and
/// column letters) and the pictures in it. All are zip files of XML, so no Office or other library is needed. Older .doc,
/// .ppt, .xls and binary .xlsb files are not read.
/// </summary>
internal static class OfficeText
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";

    /// <summary>Pictures smaller than this are logos, bullets and icons: left out.</summary>
    public const int MinPictureBytes = 15_000;

    public sealed record Content(string Text, List<(string Name, byte[] Bytes)> Pictures);

    /// <summary>A workbook's text stops here (about a 1,000-page spreadsheet), so a huge export can't fill the disk or the chat.</summary>
    public const int MaxSheetChars = 4_000_000;

    public static bool IsOffice(string path) => Path.GetExtension(path).ToLowerInvariant() is ".docx" or ".pptx" or ".xlsx" or ".xlsm";

    public static bool IsWorkbook(string path) => Path.GetExtension(path).ToLowerInvariant() is ".xlsx" or ".xlsm";

    /// <summary>The text and pictures of a .docx, .pptx or .xlsx. Throws InvalidDataException when it isn't one.</summary>
    public static Content Read(string path)
    {
        using var zip = OpenZip(File.ReadAllBytes(path));
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var text = Text(zip, ext);
        var media = ext == ".docx" ? "word/media/" : IsWorkbook(path) ? "xl/media/" : "ppt/media/";
        var pictures = new List<(string, byte[])>();
        foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith(media, StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.FullName, StringComparer.Ordinal))
        {
            var type = Path.GetExtension(entry.Name).ToLowerInvariant();
            if (type is not (".png" or ".jpg" or ".jpeg" or ".gif") || entry.Length < MinPictureBytes) continue;
            using var input = entry.Open();
            using var copy = new MemoryStream();
            input.CopyTo(copy);
            pictures.Add((entry.Name, copy.ToArray()));
        }
        return new Content(text, pictures);
    }

    public static Content Read(byte[] bytes, bool word) => Read(bytes, word ? ".docx" : ".pptx");

    /// <summary>The text of a file given as bytes, <paramref name="ext"/> naming its kind (".docx", ".pptx" or ".xlsx").</summary>
    public static Content Read(byte[] bytes, string ext)
    {
        using var zip = OpenZip(bytes);
        return new Content(Text(zip, ext.ToLowerInvariant()), []);
    }

    private static string Text(ZipArchive zip, string ext) => ext switch
    {
        ".docx" => WordText(zip),
        ".xlsx" or ".xlsm" => WorkbookText(zip),
        _ => SlidesText(zip),
    };

    private static ZipArchive OpenZip(byte[] bytes)
    {
        try { return new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read); }
        catch (InvalidDataException) { throw new InvalidDataException("it isn't a Word, PowerPoint or Excel file in the current format (.docx, .pptx or .xlsx)"); }
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

    // ---- Excel --------------------------------------------------------------------------------------------------------

    private static readonly XNamespace X = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    /// <summary>A sheet shows at most this many columns (a wider one says so).</summary>
    private const int MaxColumns = 100;

    /// <summary>
    /// Every sheet in the workbook's order, as a table headed by the column letters, each row led by its row number, so
    /// an answer can name the cell ("B12"). Values are what Excel last showed: formulas' results, dates as dates, percents
    /// as percents (a formula saved with no result shows as "=formula"). Empty rows and columns are left out. The sheets
    /// are read row by row, so a large one isn't held whole.
    /// </summary>
    private static string WorkbookText(ZipArchive zip)
    {
        var book = Xml(zip, "xl/workbook.xml")?.Root ?? throw new InvalidDataException("it has no workbook (xl/workbook.xml)");
        var date1904 = (string?)book.Element(X + "workbookPr")?.Attribute("date1904") is "1" or "true";
        var targets = Xml(zip, "xl/_rels/workbook.xml.rels")?.Root?.Elements(Rels + "Relationship")
            .GroupBy(r => (string?)r.Attribute("Id") ?? "").ToDictionary(g => g.Key, g => (string?)g.First().Attribute("Target") ?? "") ?? [];
        var sheets = book.Element(X + "sheets")?.Elements(X + "sheet").ToList() ?? [];
        if (sheets.Count == 0) throw new InvalidDataException("it has no sheets");
        var shared = SharedStrings(zip);
        var formats = CellFormats(zip);

        var sb = new StringBuilder();
        var stopped = false;
        for (var i = 0; i < sheets.Count && !stopped; i++)
        {
            var name = (string?)sheets[i].Attribute("name") ?? $"Sheet{i + 1}";
            var hidden = (string?)sheets[i].Attribute("state") is "hidden" or "veryHidden" ? " (hidden)" : "";
            sb.Append(i == 0 ? "" : "\n").Append($"=== Sheet {i + 1} of {sheets.Count}: {name}{hidden} ===\n");
            var target = targets.TryGetValue((string?)sheets[i].Attribute(R + "id") ?? "", out var t) ? t : "";
            var entry = target.Length == 0 ? null : zip.GetEntry(target.StartsWith("/", StringComparison.Ordinal) ? target.TrimStart('/') : "xl/" + target);
            if (entry is null) { sb.Append("(a chart or other sheet with no cells)\n"); continue; }
            var rows = SheetRows(entry, shared, formats, date1904, MaxSheetChars - sb.Length, out stopped);
            if (rows.Count == 0) { sb.Append("(empty)\n"); continue; }
            var first = rows.Min(r => r.Cells.Keys.Min());
            var last = rows.Max(r => r.Cells.Keys.Max());
            var wide = last - first + 1 > MaxColumns;
            if (wide) last = first + MaxColumns - 1;
            var columns = Enumerable.Range(first, last - first + 1).ToList();
            sb.Append("| Row | ").Append(string.Join(" | ", columns.Select(ColumnName))).Append(" |\n");
            foreach (var (row, cells) in rows)
                sb.Append("| ").Append(row).Append(" | ")
                  .Append(string.Join(" | ", columns.Select(c => cells.TryGetValue(c, out var v) ? v : ""))).Append(" |\n");
            if (wide) sb.Append($"(only columns {ColumnName(first)} to {ColumnName(last)} are shown; the sheet is wider)\n");
        }
        if (stopped) sb.Append("(the workbook is very large, so its text stops here; open it in Excel for the rest)\n");
        return Tidy(sb.ToString());
    }

    private static XmlReader Reader(Stream stream) =>
        XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreWhitespace = false });

    /// <summary>Each element named <paramref name="local"/>, one at a time, without loading the whole part.</summary>
    private static IEnumerable<XElement> Each(Stream stream, string local)
    {
        using var reader = Reader(stream);
        reader.MoveToContent();
        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == local && reader.NamespaceURI == X.NamespaceName)
            {
                if (XNode.ReadFrom(reader) is XElement e) yield return e;
            }
            else reader.Read();
        }
    }

    private static List<string> SharedStrings(ZipArchive zip)
    {
        var shared = new List<string>();
        var entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return shared;
        using var stream = entry.Open();
        // Phonetic guides (rPh) are reading hints for East Asian text, not part of the string.
        foreach (var si in Each(stream, "si"))
            shared.Add(string.Concat(si.Descendants(X + "t").Where(t => !t.Ancestors(X + "rPh").Any()).Select(t => t.Value)));
        return shared;
    }

    private enum Shown { Plain, Date, Time, DateTime, Percent }

    /// <summary>How each cell style (the cell's s="n") shows a number: as a date, a time, a percent or as it is.</summary>
    private static List<Shown> CellFormats(ZipArchive zip)
    {
        var styles = Xml(zip, "xl/styles.xml")?.Root;
        var custom = styles?.Element(X + "numFmts")?.Elements(X + "numFmt")
            .GroupBy(f => (int?)f.Attribute("numFmtId") ?? -1).ToDictionary(g => g.Key, g => (string?)g.First().Attribute("formatCode") ?? "") ?? [];
        return styles?.Element(X + "cellXfs")?.Elements(X + "xf").Select(xf =>
        {
            var id = (int?)xf.Attribute("numFmtId") ?? 0;
            if (custom.TryGetValue(id, out var code)) return Kind(code);
            return id switch
            {
                >= 14 and <= 17 => Shown.Date,
                >= 18 and <= 21 or 45 or 46 or 47 => Shown.Time,
                22 => Shown.DateTime,
                9 or 10 => Shown.Percent,
                _ => Shown.Plain,
            };
        }).ToList() ?? [];
    }

    /// <summary>What a custom number format shows, from its codes outside quotes, brackets and escapes.</summary>
    private static Shown Kind(string code)
    {
        var bare = Regex.Replace(code.Split(';')[0], "\"[^\"]*\"|\\[[^\\]]*\\]|\\\\.|_.|\\*.", "").ToLowerInvariant();
        var date = bare.IndexOfAny(['y', 'd']) >= 0;
        var time = bare.IndexOfAny(['h', 's']) >= 0;
        if (date && time) return Shown.DateTime;
        if (date || bare.Contains("mmm")) return Shown.Date;
        if (time) return Shown.Time;
        return bare.Contains('%') ? Shown.Percent : Shown.Plain;
    }

    private static List<(int Row, Dictionary<int, string> Cells)> SheetRows(ZipArchiveEntry entry, List<string> shared, List<Shown> formats,
        bool date1904, int room, out bool stopped)
    {
        var rows = new List<(int, Dictionary<int, string>)>();
        var size = 0;
        stopped = room <= 0;
        if (stopped) return rows;
        using var stream = entry.Open();
        foreach (var row in Each(stream, "row"))
        {
            var cells = new Dictionary<int, string>();
            var next = 0;
            foreach (var c in row.Elements(X + "c"))
            {
                var col = (string?)c.Attribute("r") is { } reference ? ColumnIndex(reference) : next;
                if (col < 0) col = next;
                next = col + 1;
                var text = CellText(c, shared, formats, date1904);
                if (text.Length == 0) continue;
                cells[col] = text.Replace("\r", "").Replace('\n', ' ').Replace("|", "/");
                size += cells[col].Length + 3;
            }
            if (cells.Count == 0) continue;
            rows.Add((int.TryParse((string?)row.Attribute("r"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : rows.Count + 1, cells));
            if (size > room) { stopped = true; break; }
        }
        return rows;
    }

    private static string CellText(XElement c, List<string> shared, List<Shown> formats, bool date1904)
    {
        var value = c.Element(X + "v")?.Value ?? "";
        // A formula saved without its result (files written by other programs, not Excel): show the formula itself.
        if (value.Length == 0 && c.Element(X + "f")?.Value is { Length: > 0 } formula) return "=" + formula.Trim();
        switch ((string?)c.Attribute("t") ?? "n")
        {
            case "s":
                return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && i >= 0 && i < shared.Count ? shared[i].Trim() : "";
            case "inlineStr":
                return string.Concat(c.Descendants(X + "t").Select(t => t.Value)).Trim();
            case "b":
                return value == "1" ? "TRUE" : value == "0" ? "FALSE" : value;
            case "str" or "e":
                return value.Trim();
            case "d":
                return value.Replace('T', ' ').Trim();
        }
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return value.Trim();
        var style = int.TryParse((string?)c.Attribute("s"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) && s >= 0 && s < formats.Count ? formats[s] : Shown.Plain;
        return style switch
        {
            Shown.Percent => (number * 100).ToString("G12", CultureInfo.InvariantCulture) + "%",
            Shown.Date or Shown.Time or Shown.DateTime when ExcelDate(number, date1904) is { } day =>
                style == Shown.Time && number < 1 ? day.ToString(day.Second == 0 ? "HH:mm" : "HH:mm:ss", CultureInfo.InvariantCulture)
                : style == Shown.Date || day.TimeOfDay == TimeSpan.Zero ? day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : day.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            // G15 drops binary noise (0.1 + 0.2 is stored as 0.30000000000000004; Excel shows 0.3).
            _ => number.ToString("G15", CultureInfo.InvariantCulture),
        };
    }

    /// <summary>
    /// An Excel date serial as a date, to the second. From 1 March 1900 on it is the same count as an OLE date; before it,
    /// Excel counts a 29 February 1900 that never was (kept from Lotus), so its days are one ahead. The 1904 system starts
    /// four years later.
    /// </summary>
    private static DateTime? ExcelDate(double serial, bool date1904)
    {
        if (date1904) serial += 1462;
        if (serial < 0 || serial >= 2_958_466) return null;
        if (serial >= 1 && serial < 61) serial += 1;
        var d = DateTime.FromOADate(serial).AddMilliseconds(500);
        return new DateTime(d.Year, d.Month, d.Day, d.Hour, d.Minute, d.Second);
    }

    /// <summary>"C12" is column 2 (zero-based); -1 when it starts with no letters.</summary>
    private static int ColumnIndex(string reference)
    {
        var col = 0;
        var any = false;
        foreach (var ch in reference)
        {
            if (ch is >= 'A' and <= 'Z') col = col * 26 + (ch - 'A' + 1);
            else if (ch is >= 'a' and <= 'z') col = col * 26 + (ch - 'a' + 1);
            else break;
            any = true;
        }
        return any ? col - 1 : -1;
    }

    /// <summary>Column 0 is "A", 26 is "AA".</summary>
    private static string ColumnName(int index)
    {
        var name = "";
        for (var n = index + 1; n > 0; n = (n - 1) / 26) name = (char)('A' + (n - 1) % 26) + name;
        return name;
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

    /// <summary>
    /// A small .xlsx for the tests and the field test: a "Piles" sheet (shared and inline text, a number with binary noise,
    /// a date, a percent, a formula's result, a TRUE, a gap row and a far column) and a hidden "Notes" sheet.
    /// </summary>
    public static byte[] SampleWorkbook()
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string xml)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(xml);
            }
            Add("xl/workbook.xml", $$"""
                <?xml version="1.0" encoding="UTF-8"?>
                <workbook xmlns="{{X}}" xmlns:r="{{R}}"><sheets>
                  <sheet name="Piles" sheetId="1" r:id="rId1"/><sheet name="Notes" sheetId="2" state="hidden" r:id="rId2"/>
                </sheets></workbook>
                """);
            Add("xl/_rels/workbook.xml.rels", $$"""
                <?xml version="1.0" encoding="UTF-8"?>
                <Relationships xmlns="{{Rels}}">
                  <Relationship Id="rId1" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Target="/xl/worksheets/sheet2.xml"/>
                </Relationships>
                """);
            Add("xl/sharedStrings.xml", $$"""
                <?xml version="1.0" encoding="UTF-8"?>
                <sst xmlns="{{X}}"><si><t>Mark</t></si><si><t>Diameter (m)</t></si><si><r><t xml:space="preserve">P-</t></r><r><t>01</t></r></si><si><t>Cast</t></si></sst>
                """);
            Add("xl/styles.xml", $$"""
                <?xml version="1.0" encoding="UTF-8"?>
                <styleSheet xmlns="{{X}}"><numFmts><numFmt numFmtId="164" formatCode="dd/mm/yyyy"/></numFmts>
                  <cellXfs><xf numFmtId="0"/><xf numFmtId="164"/><xf numFmtId="9"/></cellXfs></styleSheet>
                """);
            Add("xl/worksheets/sheet1.xml", $$"""
                <?xml version="1.0" encoding="UTF-8"?>
                <worksheet xmlns="{{X}}"><sheetData>
                  <row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c><c r="C1" t="s"><v>3</v></c><c r="D1" t="inlineStr"><is><t>Done</t></is></c></row>
                  <row r="2"><c r="A2" t="s"><v>2</v></c><c r="B2"><v>0.30000000000000004</v></c><c r="C2" s="1"><v>45658</v></c><c r="D2" s="2"><v>0.85</v></c></row>
                  <row r="4"><c r="A4" t="str"><f>A2&amp;"b"</f><v>P-01b</v></c><c r="B4"><f>B2*2</f><v>0.6</v></c><c r="D4" t="b"><v>1</v></c><c r="F4" t="inlineStr"><is><t>far | away</t></is></c></row>
                </sheetData></worksheet>
                """);
            Add("xl/worksheets/sheet2.xml", $$"""
                <?xml version="1.0" encoding="UTF-8"?>
                <worksheet xmlns="{{X}}"><sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>Check toe levels</t></is></c></row></sheetData></worksheet>
                """);
        }
        return output.ToArray();
    }
}
