using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using SketchProMax.Chat;

namespace SketchProMax.Tools;

/// <summary>
/// What make_document writes from, shared by its Word, PowerPoint and PDF writers: the Markdown the chat wrote, read
/// into blocks (with pictures and page breaks), pictures from this PC, and the small zip and XML helpers. Everything is
/// written by hand (no Office or library needed) and has no Revit or WPF, so the tests run it anywhere.
/// </summary>
internal static class OfficeDocs
{
    /// <summary>A picture line in the Markdown: ![caption](C:\path\picture.png).</summary>
    internal sealed record ImageBlock(string Path, string Caption) : Markdown.Block;

    /// <summary>A line that says [page break] (Word and PDF start a new page there).</summary>
    internal sealed record PageBreak : Markdown.Block;

    /// <summary>A picture read from disk: its bytes, png or jpeg, and its size in pixels.</summary>
    internal sealed record Picture(byte[] Bytes, string Ext, int Width, int Height);

    private static readonly Regex ImageLine = new(@"^!\[([^\]]*)\]\(\s*<?([^)>]+?)>?\s*\)$", RegexOptions.CultureInvariant);

    /// <summary>The Markdown's blocks, with picture lines and [page break] lines as their own blocks.</summary>
    public static List<Markdown.Block> Blocks(string? markdown)
    {
        var list = new List<Markdown.Block>();
        foreach (var block in Markdown.Parse(markdown))
        {
            if (block is Markdown.Paragraph p)
            {
                // The reader joins a paragraph's lines with spaces (hard breaks stay as \n), so several picture lines
                // without blank lines between them arrive as one paragraph: split it again before each picture.
                var text = new StringBuilder();
                void Flush()
                {
                    if (text.Length > 0) list.Add(new Markdown.Paragraph(text.ToString().Trim()));
                    text.Clear();
                }
                foreach (var line in p.Text.Split('\n'))
                {
                    if (text.Length > 0) text.Append('\n');
                    foreach (var part in Regex.Split(line, @"(?=!\[[^\]]*\]\()"))
                    {
                        var t = part.Trim();
                        var m = ImageLine.Match(t);
                        if (m.Success)
                        {
                            Flush();
                            list.Add(new ImageBlock(m.Groups[2].Value.Trim().Trim('"'), m.Groups[1].Value.Trim()));
                        }
                        else if (Regex.IsMatch(t, @"^\[\s*page\s*break\s*\]$", RegexOptions.IgnoreCase))
                        {
                            Flush();
                            list.Add(new PageBreak());
                        }
                        else if (t.Length > 0)
                        {
                            if (text.Length > 0 && text[text.Length - 1] != '\n') text.Append(' ');
                            text.Append(t);
                        }
                    }
                }
                Flush();
            }
            else list.Add(block);
        }
        return list;
    }

    /// <summary>Reads a PNG or JPEG picture; null when it is missing, too big or not one of those.</summary>
    public static Picture? LoadPicture(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            if (new FileInfo(path).Length > 40L * 1024 * 1024) return null;
            var bytes = File.ReadAllBytes(path);
            return ReadPicture(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    /// <summary>The picture's type and size from its header; null when it is not a PNG or JPEG.</summary>
    public static Picture? ReadPicture(byte[] bytes)
    {
        if (bytes.Length > 24 && bytes[0] == 0x89 && bytes[1] == (byte)'P' && bytes[2] == (byte)'N' && bytes[3] == (byte)'G')
        {
            int w = BigEndian(bytes, 16), h = BigEndian(bytes, 20);
            return w > 0 && h > 0 ? new Picture(bytes, "png", w, h) : null;
        }
        if (bytes.Length > 4 && bytes[0] == 0xFF && bytes[1] == 0xD8)
        {
            var i = 2;
            while (i + 9 < bytes.Length)
            {
                if (bytes[i] != 0xFF) { i++; continue; }
                var marker = bytes[i + 1];
                if (marker == 0xFF) { i++; continue; }
                if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7) { i += 2; continue; }
                var length = (bytes[i + 2] << 8) | bytes[i + 3];
                // Start of frame (any kind but DHT, JPG and DAC): height then width.
                if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
                {
                    int h = (bytes[i + 5] << 8) | bytes[i + 6], w = (bytes[i + 7] << 8) | bytes[i + 8];
                    return w > 0 && h > 0 ? new Picture(bytes, "jpeg", w, h) : null;
                }
                i += 2 + length;
            }
        }
        return null;
    }

    /// <summary>The number of colour components of a JPEG (1 grey, 3 colour, 4 CMYK); 3 when it can't be read.</summary>
    public static int JpegComponents(byte[] bytes)
    {
        var i = 2;
        while (i + 9 < bytes.Length)
        {
            if (bytes[i] != 0xFF) { i++; continue; }
            var marker = bytes[i + 1];
            if (marker == 0xFF) { i++; continue; }
            if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7) { i += 2; continue; }
            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC)) return bytes[i + 9];
            i += 2 + ((bytes[i + 2] << 8) | bytes[i + 3]);
        }
        return 3;
    }

    internal static int BigEndian(byte[] b, int at) => (b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3];

    /// <summary>The cell or span text with Markdown marks taken out (for Excel and table cells measured as plain text).</summary>
    public static string Plain(string text) => string.Concat(Markdown.Inline(text).Select(s => s.Text));

    /// <summary>XML text, with characters XML can't hold left out.</summary>
    public static string Esc(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s!.Length + 16);
        foreach (var c in s)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                default:
                    if (c >= 0x20 || c is '\t' or '\n' or '\r') sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>A file name made from a title: no characters Windows refuses, not too long.</summary>
    public static string SafeName(string title, string fallback = "Document")
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { ':', '*', '?', '"', '<', '>', '|', '/', '\\' }).ToArray();
        var name = new string((title ?? "").Select(c => invalid.Contains(c) || c < 32 ? ' ' : c).ToArray());
        name = Regex.Replace(name, @"\s+", " ").Trim().TrimEnd('.', ' ');
        if (name.Length > 80) name = name.Substring(0, 80).TrimEnd('.', ' ');
        return name.Length == 0 ? fallback : name;
    }

    /// <summary>A path in <paramref name="folder"/> for <paramref name="name"/> that isn't taken: "Name.xlsx", then "Name (2).xlsx"…</summary>
    public static string FreePath(string folder, string name, string ext)
    {
        var path = Path.Combine(folder, name + ext);
        for (var i = 2; File.Exists(path) && i < 1000; i++) path = Path.Combine(folder, $"{name} ({i}){ext}");
        return path;
    }

    /// <summary>A zip being written as an Office file (each part UTF-8 without a byte order mark).</summary>
    internal sealed class Package : IDisposable
    {
        private readonly ZipArchive _zip;

        public Package(Stream output) => _zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        public void Text(string name, string text)
        {
            using var w = new StreamWriter(_zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
            w.Write(text);
        }

        public void Bytes(string name, byte[] bytes)
        {
            // Pictures are compressed already.
            using var s = _zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
            s.Write(bytes, 0, bytes.Length);
        }

        public void Dispose() => _zip.Dispose();
    }

    /// <summary>Writes the file through a temporary one, so a failure never leaves half a file under the real name.</summary>
    public static void WriteFile(string path, Action<Stream> write)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var temp = path + ".tmp";
        try
        {
            using (var file = File.Create(temp)) write(file);
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
        }
    }

    public const string XmlHead = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>";

    /// <summary>docProps/core.xml: the title and when it was made.</summary>
    public static string Core(string title, DateTime when) =>
        XmlHead + "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" " +
        "xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\" xmlns:dcmitype=\"http://purl.org/dc/dcmitype/\" " +
        "xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">" +
        $"<dc:title>{Esc(title)}</dc:title><dc:creator>Sketch ProMax</dc:creator>" +
        $"<dcterms:created xsi:type=\"dcterms:W3CDTF\">{when.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)}</dcterms:created>" +
        $"<dcterms:modified xsi:type=\"dcterms:W3CDTF\">{when.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)}</dcterms:modified>" +
        "</cp:coreProperties>";

    /// <summary>A number cell's text in Markdown ("1,250.50", "-3", "12%"), for right-aligning number columns.</summary>
    public static bool LooksNumeric(string text)
    {
        var t = Plain(text).Trim();
        return t.Length > 0 && Regex.IsMatch(t, @"^[-+(]?[£$€]?\s?\d[\d,\s]*(\.\d+)?\)?\s?(%|[a-zA-Z²³/]{1,6})?$", RegexOptions.CultureInvariant);
    }
}

/// <summary>
/// An Excel workbook (.xlsx) as make_document writes it: per sheet an optional title, a header row (bold, shaded,
/// frozen, with filters, repeated on every printed page), rows of text, numbers and formulas, bold rows for totals,
/// thin borders, columns sized to their content, and number formats per column (2 decimals where any number has
/// them). Prints fitted to the page width.
/// </summary>
internal static class OfficeXlsx
{
    /// <summary>A formula cell (without or with the leading =); Excel calculates it when the file opens.</summary>
    internal sealed record Formula(string Expression);

    internal sealed record Row(IReadOnlyList<object?> Cells, bool Bold = false);

    /// <param name="Formats">Per column: auto, text, integer, decimal, percent (empty or missing: auto).</param>
    /// <param name="Widths">Per column, in characters (0 or missing: sized to the content).</param>
    internal sealed record Sheet(string Name, string Title, IReadOnlyList<string> Columns, IReadOnlyList<Row> Rows,
        IReadOnlyList<string> Formats, IReadOnlyList<double> Widths, bool Landscape);

    /// <summary>The row the first data row lands on (1-based): after the title (and a blank row) and the header.</summary>
    public static int FirstDataRow(Sheet s) => (s.Title.Length > 0 ? 2 : 0) + (s.Columns.Count > 0 ? 1 : 0) + 1;

    // Number formats: General, #,##0, #,##0.00, 0.00% (built in), and Excel's text format.
    private static readonly int[] NumFmts = [0, 3, 4, 10, 49];

    public static IReadOnlyList<string> SheetNames(IEnumerable<Sheet> sheets)
    {
        var names = new List<string>();
        foreach (var s in sheets)
        {
            var n = new string(s.Name.Where(ch => "[]:*?/\\".IndexOf(ch) < 0 && ch >= 32).ToArray()).Trim().Trim('\'');
            if (n.Length == 0) n = "Sheet" + (names.Count + 1);
            if (n.Length > 31) n = n.Substring(0, 31);
            var unique = n;
            for (var i = 2; names.Contains(unique, StringComparer.OrdinalIgnoreCase); i++) unique = n.Substring(0, Math.Min(n.Length, 27)) + " " + i;
            names.Add(unique);
        }
        return names;
    }

    public static void Write(string path, string title, IReadOnlyList<Sheet> sheets) =>
        OfficeDocs.WriteFile(path, s => Write(s, title, sheets, DateTime.Now));

    public static void Write(Stream output, string title, IReadOnlyList<Sheet> sheets, DateTime when)
    {
        if (sheets.Count == 0) throw new ArgumentException("A workbook needs at least one sheet.");
        var names = SheetNames(sheets);
        using var zip = new OfficeDocs.Package(output);
        zip.Text("[Content_Types].xml", OfficeDocs.XmlHead +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
            "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
            "<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/>" +
            string.Concat(names.Select((_, i) => $"<Override PartName=\"/xl/worksheets/sheet{i + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>")) +
            "</Types>");
        zip.Text("_rels/.rels", OfficeDocs.XmlHead +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
            "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/>" +
            "</Relationships>");
        zip.Text("docProps/core.xml", OfficeDocs.Core(title, when));

        var defined = new StringBuilder();
        for (var i = 0; i < sheets.Count; i++)
        {
            var s = sheets[i];
            var quoted = "'" + names[i].Replace("'", "''") + "'";
            var header = s.Columns.Count == 0 ? 0 : FirstDataRow(s) - 1;
            var width = Math.Max(s.Columns.Count, s.Rows.Count == 0 ? 0 : s.Rows.Max(r => r.Cells.Count));
            if (header > 0)
            {
                defined.Append($"<definedName name=\"_xlnm._FilterDatabase\" localSheetId=\"{i}\" hidden=\"1\">{OfficeDocs.Esc(quoted)}!$A${header}:${Column(Math.Max(1, width) - 1)}${header + s.Rows.Count}</definedName>");
                defined.Append($"<definedName name=\"_xlnm.Print_Titles\" localSheetId=\"{i}\">{OfficeDocs.Esc(quoted)}!${header}:${header}</definedName>");
            }
        }
        var formulas = sheets.Any(s => s.Rows.Any(r => r.Cells.Any(c => c is Formula)));
        zip.Text("xl/workbook.xml", OfficeDocs.XmlHead +
            "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
            "<bookViews><workbookView/></bookViews><sheets>" +
            string.Concat(names.Select((n, i) => $"<sheet name=\"{OfficeDocs.Esc(n)}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>")) + "</sheets>" +
            (defined.Length > 0 ? "<definedNames>" + defined + "</definedNames>" : "") +
            (formulas ? "<calcPr fullCalcOnLoad=\"1\"/>" : "") + "</workbook>");
        zip.Text("xl/_rels/workbook.xml.rels", OfficeDocs.XmlHead +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            string.Concat(names.Select((_, i) => $"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>")) +
            $"<Relationship Id=\"rId{names.Count + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
        zip.Text("xl/styles.xml", Styles());
        for (var i = 0; i < sheets.Count; i++) zip.Text($"xl/worksheets/sheet{i + 1}.xml", SheetXml(sheets[i]));
    }

    // Cell styles: 0 default, 1 title, 2 header; then body styles by (bold, format): 3 + bold * 5 + format index.
    private const int TitleStyle = 1, HeaderStyle = 2;
    private static int Body(bool bold, int format) => 3 + (bold ? NumFmts.Length : 0) + format;

    private static string Styles()
    {
        var xfs = new StringBuilder();
        xfs.Append("<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>");
        xfs.Append("<xf numFmtId=\"0\" fontId=\"2\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>");
        xfs.Append("<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment vertical=\"center\" wrapText=\"1\"/></xf>");
        foreach (var bold in new[] { false, true })
            foreach (var fmt in NumFmts)
                xfs.Append($"<xf numFmtId=\"{fmt}\" fontId=\"{(bold ? 1 : 0)}\" fillId=\"{(bold ? 3 : 0)}\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"" +
                           (fmt == 49 ? " quotePrefix=\"1\"" : "") + "><alignment vertical=\"top\" wrapText=\"1\"/></xf>");
        var count = 3 + 2 * NumFmts.Length;
        return OfficeDocs.XmlHead + "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
            "<fonts count=\"3\"><font><sz val=\"11\"/><color theme=\"1\"/><name val=\"Calibri\"/><family val=\"2\"/></font>" +
            "<font><b/><sz val=\"11\"/><color theme=\"1\"/><name val=\"Calibri\"/><family val=\"2\"/></font>" +
            "<font><b/><sz val=\"14\"/><color rgb=\"FF1F3A5F\"/><name val=\"Calibri\"/><family val=\"2\"/></font></fonts>" +
            "<fills count=\"4\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill>" +
            "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFDCE6F2\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
            "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFF2F2F2\"/><bgColor indexed=\"64\"/></patternFill></fill></fills>" +
            "<borders count=\"2\"><border><left/><right/><top/><bottom/><diagonal/></border>" +
            "<border><left style=\"thin\"><color rgb=\"FFBFBFBF\"/></left><right style=\"thin\"><color rgb=\"FFBFBFBF\"/></right>" +
            "<top style=\"thin\"><color rgb=\"FFBFBFBF\"/></top><bottom style=\"thin\"><color rgb=\"FFBFBFBF\"/></bottom><diagonal/></border></borders>" +
            "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
            $"<cellXfs count=\"{count}\">{xfs}</cellXfs>" +
            "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
            "</styleSheet>";
    }

    /// <summary>The format index (into NumFmts) for each column: what was asked, else 2 decimals where any number has them.</summary>
    internal static int[] ColumnFormats(Sheet s, int width)
    {
        var result = new int[width];
        for (var c = 0; c < width; c++)
        {
            var asked = c < s.Formats.Count ? (s.Formats[c] ?? "").Trim().ToLowerInvariant() : "";
            result[c] = asked switch
            {
                "text" => 4,
                "integer" or "int" or "whole" => 1,
                "decimal" or "money" or "currency" or "number" => 2,
                "percent" or "%" => 3,
                // Auto: 2 decimals where a number has them, and for a column of formulas only (amounts and totals).
                _ => s.Rows.Any(r => c < r.Cells.Count && Number(r.Cells[c]) is { } d && Math.Abs(d - Math.Round(d)) > 1e-9)
                     || s.Rows.Any(r => c < r.Cells.Count && r.Cells[c] is Formula) && s.Rows.All(r => c >= r.Cells.Count || r.Cells[c] is null or Formula) ? 2 : 0,
            };
        }
        return result;
    }

    private static string SheetXml(Sheet s)
    {
        var width = Math.Max(s.Columns.Count, s.Rows.Count == 0 ? 0 : s.Rows.Max(r => r.Cells.Count));
        var formats = ColumnFormats(s, width);
        var sb = new StringBuilder(OfficeDocs.XmlHead);
        sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");
        sb.Append("<sheetPr><pageSetUpPr fitToPage=\"1\"/></sheetPr>");
        var header = s.Columns.Count == 0 ? 0 : FirstDataRow(s) - 1;
        sb.Append("<sheetViews><sheetView workbookViewId=\"0\"");
        sb.Append(header > 0 ? $"><pane ySplit=\"{header}\" topLeftCell=\"A{header + 1}\" activePane=\"bottomLeft\" state=\"frozen\"/><selection pane=\"bottomLeft\" activeCell=\"A{header + 1}\" sqref=\"A{header + 1}\"/></sheetView>" : "/>");
        sb.Append("</sheetViews><sheetFormatPr defaultRowHeight=\"15\"/>");
        if (width > 0)
        {
            sb.Append("<cols>");
            for (var c = 0; c < width; c++)
            {
                var w = c < s.Widths.Count && s.Widths[c] > 0 ? Math.Min(120, s.Widths[c]) : AutoWidth(s, c, formats[c]);
                sb.Append($"<col min=\"{c + 1}\" max=\"{c + 1}\" width=\"{w.ToString("0.##", CultureInfo.InvariantCulture)}\" customWidth=\"1\"/>");
            }
            sb.Append("</cols>");
        }
        sb.Append("<sheetData>");
        var r = 1;
        if (s.Title.Length > 0)
        {
            sb.Append($"<row r=\"1\" ht=\"21\" customHeight=\"1\"><c r=\"A1\" s=\"{TitleStyle}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{OfficeDocs.Esc(s.Title)}</t></is></c></row>");
            r = 3;
        }
        if (s.Columns.Count > 0)
        {
            sb.Append($"<row r=\"{r}\">");
            for (var c = 0; c < s.Columns.Count; c++)
                sb.Append($"<c r=\"{Column(c)}{r}\" s=\"{HeaderStyle}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{OfficeDocs.Esc(OfficeDocs.Plain(s.Columns[c]))}</t></is></c>");
            sb.Append("</row>");
            r++;
        }
        foreach (var row in s.Rows)
        {
            sb.Append($"<row r=\"{r}\">");
            for (var c = 0; c < width; c++)
            {
                var cell = c < row.Cells.Count ? row.Cells[c] : null;
                var at = Column(c) + r;
                var style = Body(row.Bold, formats[c]);
                switch (cell)
                {
                    case null:
                        sb.Append($"<c r=\"{at}\" s=\"{style}\"/>");
                        break;
                    case Formula f:
                        sb.Append($"<c r=\"{at}\" s=\"{Body(row.Bold, formats[c] == 4 ? 0 : formats[c])}\"><f>{OfficeDocs.Esc(f.Expression.Trim().TrimStart('='))}</f></c>");
                        break;
                    default:
                        if (Number(cell) is { } d && formats[c] != 4)
                        {
                            sb.Append($"<c r=\"{at}\" s=\"{style}\"><v>{d.ToString("R", CultureInfo.InvariantCulture)}</v></c>");
                            break;
                        }
                        // Text is never calculated, and text that starts like a formula keeps Excel's ' prefix when edited.
                        var text = Text(cell);
                        if (Xlsx.FormulaLike(text)) style = Body(row.Bold, 4);
                        sb.Append($"<c r=\"{at}\" s=\"{style}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{OfficeDocs.Esc(text)}</t></is></c>");
                        break;
                }
            }
            sb.Append("</row>");
            r++;
        }
        sb.Append("</sheetData>");
        if (header > 0 && width > 0) sb.Append($"<autoFilter ref=\"A{header}:{Column(width - 1)}{header + s.Rows.Count}\"/>");
        sb.Append("<printOptions gridLines=\"0\"/><pageMargins left=\"0.5\" right=\"0.5\" top=\"0.6\" bottom=\"0.6\" header=\"0.3\" footer=\"0.3\"/>");
        sb.Append($"<pageSetup paperSize=\"9\" orientation=\"{(s.Landscape ? "landscape" : "portrait")}\" fitToWidth=\"1\" fitToHeight=\"0\"/>");
        sb.Append("<headerFooter><oddFooter>&amp;L&amp;A&amp;RPage &amp;P of &amp;N</oddFooter></headerFooter>");
        sb.Append("</worksheet>");
        return sb.ToString();
    }

    private static double AutoWidth(Sheet s, int c, int format)
    {
        var longest = c < s.Columns.Count ? OfficeDocs.Plain(s.Columns[c]).Length + 2 : 0;
        foreach (var row in s.Rows)
        {
            if (c >= row.Cells.Count || row.Cells[c] is not { } cell) continue;
            int length;
            if (cell is Formula) length = 12;
            else if (Number(cell) is { } d && format != 4)
                length = d.ToString(format == 2 ? "#,##0.00" : format == 1 ? "#,##0" : format == 3 ? "0.00%" : "0.##########", CultureInfo.InvariantCulture).Length + 1;
            else length = Text(cell).Split('\n').Max(l => l.Length);
            longest = Math.Max(longest, length);
        }
        return Math.Max(8, Math.Min(60, longest * 1.1 + 2));
    }

    private static string Text(object cell) => cell is string s ? s : Convert.ToString(cell, CultureInfo.InvariantCulture) ?? "";

    internal static double? Number(object? cell)
    {
        switch (cell)
        {
            case double d: return double.IsNaN(d) || double.IsInfinity(d) ? null : d;
            case float f: return f;
            case int i: return i;
            case long l: return l;
            case decimal m: return (double)m;
            default: return null;
        }
    }

    internal static string Column(int index)
    {
        var name = "";
        for (var n = index + 1; n > 0; n = (n - 1) / 26) name = (char)('A' + (n - 1) % 26) + name;
        return name;
    }
}
