using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace SketchProMax.Tools;

/// <summary>
/// Writes the checklist as an Excel workbook in the office's layout, so it can be edited in Excel and imported back
/// (ChecklistMath.ReadXlsx reads it): title, header fields, the "#" / "Check" table with a text row per section and a
/// numbered row per check, then Sign-off and the line naming who signs. Plain SpreadsheetML written by hand (inline
/// strings), no library. No Revit or WPF, so the tests compile it.
/// </summary>
internal static class ChecklistXlsx
{
    private static readonly string[] Headings =
        ["#", "Check (what to do)", "Example / how to check", "Where it came from", "Found it on", "Priority", "Done (Y/N/NA)", "Checked by", "Date", "Notes"];

    private const int Wrap = 1, Bold = 2, Section = 3, Head = 4;

    public static void Write(string path, ChecklistMath.Definition def)
    {
        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        var temp = path + ".tmp";
        using (var file = File.Create(temp)) Write(file, def);
        if (File.Exists(path)) File.Delete(path);
        File.Move(temp, path);
    }

    public static void Write(Stream output, ChecklistMath.Definition def)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        void Entry(string name, string xml)
        {
            using var w = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
            w.Write(xml);
        }
        Entry("[Content_Types].xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
            "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
            "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/></Types>");
        Entry("_rels/.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
        Entry("xl/workbook.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
            "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Checklist\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
        Entry("xl/_rels/workbook.xml.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
            "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
        // Styles: 0 plain, 1 wrapped, 2 bold, 3 section (bold on light grey), 4 table heading (bold, wrapped, grey).
        Entry("xl/styles.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
            "<fonts count=\"2\"><font><sz val=\"10\"/><name val=\"Arial\"/></font><font><b/><sz val=\"10\"/><name val=\"Arial\"/></font></fonts>" +
            "<fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill>" +
            "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFE7E6E6\"/><bgColor indexed=\"64\"/></patternFill></fill></fills>" +
            "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
            "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
            "<cellXfs count=\"5\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
            "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyAlignment=\"1\"><alignment vertical=\"top\" wrapText=\"1\"/></xf>" +
            "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>" +
            "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\"/>" +
            "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyAlignment=\"1\"><alignment vertical=\"top\" wrapText=\"1\"/></xf></cellXfs>" +
            "</styleSheet>");
        Entry("xl/worksheets/sheet1.xml", Sheet(def));
    }

    private static string Sheet(ChecklistMath.Definition def)
    {
        var rows = new StringBuilder();
        var r = 0;
        void Row(params (int Col, string Text, int Style, bool Number)[] cells)
        {
            r++;
            rows.Append("<row r=\"").Append(r).Append("\">");
            foreach (var (col, text, style, number) in cells)
            {
                if (text.Length == 0) continue;
                var at = (char)('A' + col) + r.ToString(CultureInfo.InvariantCulture);
                var s = style == 0 ? "" : $" s=\"{style}\"";
                if (number) rows.Append($"<c r=\"{at}\"{s}><v>{Esc(text)}</v></c>");
                else rows.Append($"<c r=\"{at}\"{s} t=\"inlineStr\"><is><t xml:space=\"preserve\">{Esc(text)}</t></is></c>");
            }
            rows.Append("</row>");
        }
        (int, string, int, bool) T(int col, string text, int style = 0) => (col, text ?? "", style, false);

        Row(T(0, def.Title.Length > 0 ? def.Title : "Pre-submission checklist", Bold));
        Row();
        foreach (var f in def.Fields) Row(T(0, f.Label, Bold), T(2, f.Example.Length > 0 ? "e.g. " + f.Example : ""));
        Row();
        var headRow = r + 1;
        Row(Headings.Select((h, i) => T(i, h, Head)).ToArray());
        foreach (var section in def.Sections)
        {
            Row(T(0, section.Title, Section));
            foreach (var item in section.Items)
            {
                var number = double.TryParse(item.Number, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
                Row((0, item.Number, Wrap, number), T(1, item.Check, Wrap), T(2, item.Example, Wrap), T(3, item.From, Wrap), T(4, item.FoundOn, Wrap),
                    T(5, item.Priority, Wrap), T(9, Notes(item.Waiting), Wrap));
            }
        }
        Row();
        Row(T(0, "Sign-off", Bold));
        Row(T(0, string.Join("     ", def.Signatories.Select(s => s + ": ____________________")) + "     Date: __________"));
        Row(T(0, "To change the checks: edit, add or delete rows above (a text row in column A starts a stage, a number starts a check), " +
                 "keep the heading row, save, then in Revit open Pre-submission checklist > Edit the checklist > Import from Excel."));

        var cols = "<cols><col min=\"1\" max=\"1\" width=\"7\" customWidth=\"1\"/><col min=\"2\" max=\"2\" width=\"60\" customWidth=\"1\"/>" +
                   "<col min=\"3\" max=\"3\" width=\"50\" customWidth=\"1\"/><col min=\"4\" max=\"4\" width=\"34\" customWidth=\"1\"/>" +
                   "<col min=\"5\" max=\"6\" width=\"12\" customWidth=\"1\"/><col min=\"7\" max=\"9\" width=\"11\" customWidth=\"1\"/>" +
                   "<col min=\"10\" max=\"10\" width=\"40\" customWidth=\"1\"/></cols>";
        var freeze = $"<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"{headRow}\" topLeftCell=\"A{headRow + 1}\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>";
        return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
               freeze + cols + "<sheetData>" + rows + "</sheetData></worksheet>";
    }

    /// <summary>The Notes cell for a check that waits on an answer: "Waiting: …" (ReadXlsx strips it again).</summary>
    private static string Notes(string waiting)
    {
        waiting = (waiting ?? "").Trim();
        if (waiting.Length == 0) return "";
        return Regex.IsMatch(waiting, @"^(Open point|TBC)\b", RegexOptions.IgnoreCase) ? waiting : "Waiting: " + waiting;
    }

    private static string Esc(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\r': break;
                default:
                    // XML 1.0 refuses most control characters; tabs and line breaks stay.
                    if (c < ' ' && c != '\n' && c != '\t') continue;
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}
