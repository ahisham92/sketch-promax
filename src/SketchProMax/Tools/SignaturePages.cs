using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SketchProMax.Tools;

/// <summary>
/// Signature pages (the Signature pages button): the one-page quality cover of each submitted document (bill of quantities,
/// method of measurement, specification, report, drawing) with its heading, discipline, project, title, the revision
/// table (Rev, Date, Description, By, CHK, Approved) and the document number with its REV, the office's form code and
/// the logo at the foot. Each document gets its own Word file and PDF, in a TYPE\Word and TYPE\PDF folder.
///
/// The list comes from sheets picked in Revit or an Excel list (Number, Rev, Title, Type columns). Everything the page
/// says about the project (name, discipline, form codes, logo) is the user's, kept on this PC per project: nothing about
/// a project or office is in the code.
///
/// Signatures: each person keeps their own signature on this PC, locked with their own PIN (the picture is stored
/// encrypted with a key made from the PIN, so it can't be used without it). A role's signature goes on the pages only
/// after that person types their PIN in the window; a role nobody signed is left blank to sign by hand.
///
/// No Revit or WPF here, so the tests run it anywhere.
/// </summary>
internal static class SignaturePages
{
    // ---- What a page says ------------------------------------------------------------------------------------------

    /// <summary>One document: its type code (BOQ, MOM, SPC, RPT, DWG...), number, revision and title.</summary>
    internal sealed record Doc(string Type, string Number, string Rev, string Title);

    /// <summary>A document type: its code, the heading at the top of its page and the office's form code at its foot.</summary>
    internal sealed class TypeDef
    {
        public string Code { get; set; } = "";
        public string Heading { get; set; } = "";
        public string Form { get; set; } = "";
    }

    /// <summary>What every page of a project shares. Kept on this PC per project (setup.json).</summary>
    internal sealed class Setup
    {
        public string Project { get; set; } = "";
        public string Discipline { get; set; } = "";
        public string Description { get; set; } = "Approved Issue";
        public string By { get; set; } = "";
        public string Checked { get; set; } = "";
        public string Approved { get; set; } = "";
        /// <summary>REV 01 after the number (two digits) rather than REV 1; the table's Rev column stays as typed.</summary>
        public bool TwoDigitRev { get; set; } = true;
        public List<TypeDef> Types { get; set; } = DefaultTypes();
        /// <summary>The last list, so the window opens on it.</summary>
        public List<Doc> Docs { get; set; } = [];
        public string Folder { get; set; } = "";
    }

    /// <summary>The types every project starts with; headings as the office's pages say them, form codes left to fill.</summary>
    public static List<TypeDef> DefaultTypes() =>
    [
        new() { Code = "BOQ", Heading = "Project bill of quantities" },
        new() { Code = "MOM", Heading = "Project method of measurement" },
        new() { Code = "SPC", Heading = "Project specification" },
        new() { Code = "RPT", Heading = "Report" },
        new() { Code = "CAL", Heading = "Calculations" },
        new() { Code = "DWG", Heading = "Drawings" },
    ];

    /// <summary>The type of <paramref name="code"/>; an unknown code gets a type whose heading is the code itself.</summary>
    public static TypeDef TypeOf(Setup setup, string code)
    {
        code = (code ?? "").Trim();
        return setup.Types.FirstOrDefault(t => string.Equals(t.Code, code, StringComparison.OrdinalIgnoreCase))
               ?? new TypeDef { Code = code.ToUpperInvariant(), Heading = code.Length == 0 ? "Document" : code.ToUpperInvariant() };
    }

    /// <summary>The revision after the number: "REV 01" (two digits) or "REV 1"; a revision that isn't a number stays as typed (REV P02).</summary>
    public static string RevText(string rev, bool twoDigits)
    {
        rev = CleanRev(rev);
        if (rev.Length == 0) rev = "0";
        return "REV " + (twoDigits && int.TryParse(rev, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n < 100 ? n.ToString("00", CultureInfo.InvariantCulture) : rev);
    }

    /// <summary>The revision as the table shows it: "1" for "Rev 01", "rev1", "R1" or "01"; anything else trimmed.</summary>
    public static string CleanRev(string rev)
    {
        rev = (rev ?? "").Trim();
        var m = Regex.Match(rev, @"^(?:rev(?:ision)?\.?|r)?\s*0*(\d+)$", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;
        var p = Regex.Match(rev, @"^(?:rev(?:ision)?\.?)\s*(.+)$", RegexOptions.IgnoreCase);
        return p.Success ? p.Groups[1].Value.Trim() : rev;
    }

    /// <summary>
    /// A document number as typed, split from a REV written after it ("…-ST-01 REV 01" → ("…-ST-01", "1")); the revision
    /// is "" when there is none.
    /// </summary>
    public static (string Number, string Rev) SplitNumber(string text)
    {
        text = Regex.Replace(text ?? "", @"\s+", " ").Trim();
        var m = Regex.Match(text, @"^(.*?)[\s_-]+rev(?:ision)?\.?\s*([A-Za-z]?\d+[A-Za-z]?)$", RegexOptions.IgnoreCase);
        return m.Success && m.Groups[1].Value.Trim().Length > 0 ? (m.Groups[1].Value.Trim(), CleanRev(m.Groups[2].Value)) : (text, "");
    }

    /// <summary>The type code written in a document number (the first part that is a known type), or "".</summary>
    public static string TypeFromNumber(Setup setup, string number)
    {
        foreach (var part in (number ?? "").Split('-', '_', ' ', '.'))
            if (setup.Types.FirstOrDefault(t => t.Code.Length > 0 && string.Equals(t.Code, part.Trim(), StringComparison.OrdinalIgnoreCase)) is { } t) return t.Code;
        return "";
    }

    // ---- The list from Excel -----------------------------------------------------------------------------------------

    /// <summary>The columns the Excel template has, in order.</summary>
    public static readonly string[] ExcelColumns = ["Type", "Number", "Rev", "Title"];

    /// <summary>
    /// The documents of an Excel list (first sheet with a Number column; else the first sheet): a header row within the
    /// first 15 rows names the columns (Number / Document no. / Reference, Rev / Revision, Title / Description / Name,
    /// Type). A number written with its REV ("… REV 01") gives the revision when there is no Rev column; a missing type
    /// is read from the number (…-BOQ-…). Rows without a number are skipped.
    /// </summary>
    public static List<Doc> FromCells(Setup setup, IEnumerable<(int Col, int Row, string Text)> cells)
    {
        var grid = new Dictionary<(int, int), string>();
        foreach (var (c, r, t) in cells) grid[(c, r)] = (t ?? "").Trim();
        string At(int c, int r) => grid.TryGetValue((c, r), out var t) ? t : "";
        int headerRow = -1, number = -1, rev = -1, title = -1, type = -1;
        foreach (var r in grid.Keys.Select(k => k.Item2).Distinct().OrderBy(r => r).Take(15))
        {
            int n = -1, v = -1, t = -1, y = -1;
            foreach (var c in grid.Keys.Where(k => k.Item2 == r).Select(k => k.Item1).OrderBy(c => c))
            {
                var h = Regex.Replace(At(c, r).ToLowerInvariant(), @"[^a-z]", "");
                if (n < 0 && h is "number" or "no" or "docno" or "documentno" or "documentnumber" or "docnumber" or "reference" or "ref" or "drawingno" or "drawingnumber" or "sheetnumber" or "sheetno") n = c;
                else if (v < 0 && h is "rev" or "revision" or "revno" or "currentrevision") v = c;
                else if (t < 0 && h is "title" or "description" or "documenttitle" or "name" or "sheetname" or "drawingtitle") t = c;
                else if (y < 0 && h is "type" or "doctype" or "documenttype" or "kind") y = c;
            }
            if (n < 0) continue;
            (headerRow, number, rev, title, type) = (r, n, v, t, y);
            break;
        }
        var docs = new List<Doc>();
        var rows = grid.Keys.Select(k => k.Item2).Distinct().OrderBy(r => r).ToList();
        if (headerRow < 0)
        {
            // No header: the column whose cells look most like document numbers (letters, digits and two or more dashes).
            var counts = grid.Where(kv => Regex.IsMatch(kv.Value, @"^[A-Za-z0-9]+(-[A-Za-z0-9]+){2,}")).GroupBy(kv => kv.Key.Item1).OrderByDescending(g => g.Count()).FirstOrDefault();
            if (counts is null) return docs;
            number = counts.Key;
        }
        foreach (var r in rows.Where(r => r > headerRow))
        {
            var (num, revFromNumber) = SplitNumber(At(number, r));
            if (num.Length == 0) continue;
            var revText = rev >= 0 ? CleanRev(At(rev, r)) : "";
            if (revText.Length == 0) revText = revFromNumber.Length > 0 ? revFromNumber : "0";
            var typeText = type >= 0 ? At(type, r).ToUpperInvariant() : "";
            if (typeText.Length == 0) typeText = TypeFromNumber(setup, num);
            if (typeText.Length == 0) typeText = "DOC";
            var titleText = title >= 0 ? At(title, r) : "";
            if (titleText.Length == 0 && headerRow < 0)
                titleText = grid.Where(kv => kv.Key.Item2 == r && kv.Key.Item1 != number && kv.Value.Length > 3 && !Regex.IsMatch(kv.Value, @"^\d+$")).OrderByDescending(kv => kv.Value.Length).Select(kv => kv.Value).FirstOrDefault() ?? "";
            docs.Add(new Doc(typeText, num, revText, titleText));
        }
        return docs;
    }

    /// <summary>Reads an Excel list (.xlsx) or a CSV file. Throws InvalidDataException with what to do when it can't.</summary>
    public static List<Doc> ReadList(Setup setup, string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".xls") throw new InvalidDataException("This is an old Excel file (.xls). Open it in Excel and save it as .xlsx, then import that.");
        if (ext == ".csv")
        {
            var cells = new List<(int, int, string)>();
            var lines = File.ReadAllLines(path);
            for (var r = 0; r < lines.Length; r++)
            {
                var parts = SplitCsv(lines[r]);
                for (var c = 0; c < parts.Count; c++) if (parts[c].Trim().Length > 0) cells.Add((c, r + 1, parts[c]));
            }
            return FromCells(setup, cells);
        }
        using var book = XlsxBook.Read(path);
        List<Doc>? first = null;
        foreach (var sheet in book.SheetNames)
        {
            var docs = FromCells(setup, book.Cells(sheet).Select(c => (c.Col, c.Row, c.Text)));
            if (docs.Count > 0) return docs;
            first ??= docs;
        }
        return first ?? [];
    }

    private static List<string> SplitCsv(string line)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else sb.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch is ',' or ';' or '\t') { parts.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        parts.Add(sb.ToString());
        return parts;
    }

    /// <summary>The Excel template to fill: the four columns and two example rows with generic numbers.</summary>
    public static void WriteTemplate(string path) =>
        OfficeXlsx.Write(path, "Signature pages", [new OfficeXlsx.Sheet("Documents", "", ExcelColumns,
            [
                new OfficeXlsx.Row(["BOQ", "PRJ1-FD-SITE-01-BOQ-ST-01", "0", "Final Design Structures Berth 1 - Bill of Quantities"]),
                new OfficeXlsx.Row(["SPC", "PRJ1-FD-SITE-00-SPC-ST-01", "1", "Final Design Structures Specifications"]),
            ], ["text", "text", "text", "text"], [10, 44, 8, 60], false)]);

    // ---- Files ------------------------------------------------------------------------------------------------------

    /// <summary>The file name of a document's page, without extension: "&lt;number&gt; REV 01 signature page", safe for Windows.</summary>
    public static string FileName(Doc doc, bool twoDigits)
    {
        var name = $"{doc.Number} {RevText(doc.Rev, twoDigits)} signature page";
        var clean = new string(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) || ch is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : ch).ToArray()).Trim().TrimEnd('.');
        return clean.Length > 150 ? clean.Substring(0, 150) : clean;
    }

    /// <summary>A folder name for a type code: the code, or "Other" when it has nothing a folder can be named by.</summary>
    public static string TypeFolder(string code)
    {
        var clean = new string((code ?? "").Where(ch => char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '_').ToArray()).Trim();
        return clean.Length == 0 ? "Other" : clean.ToUpperInvariant();
    }

    /// <summary>What Make writes for each document, in list order: its Word and PDF paths (a number listed twice gets (2)).</summary>
    public static List<(Doc Doc, string Word, string Pdf)> Plan(IEnumerable<Doc> docs, string folder, bool twoDigits)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<(Doc, string, string)>();
        foreach (var d in docs)
        {
            var type = TypeFolder(d.Type);
            var name = FileName(d, twoDigits);
            var unique = name;
            for (var i = 2; !used.Add(type + "\\" + unique); i++) unique = $"{name} ({i})";
            list.Add((d, Path.Combine(folder, type, "Word", unique + ".docx"), Path.Combine(folder, type, "PDF", unique + ".pdf")));
        }
        return list;
    }

    /// <summary>What every page of one Make shares: the setup, the date, the logo and the signatures put on by their owners.</summary>
    internal sealed record Common(Setup Setup, DateTime Date, OfficeDocs.Picture? Logo, OfficeDocs.Picture? SignBy, OfficeDocs.Picture? SignChecked, OfficeDocs.Picture? SignApproved);

    /// <summary>Writes every document's Word file and PDF. Returns the files written.</summary>
    public static List<string> Make(IReadOnlyList<Doc> docs, string folder, Common common, CancellationToken ct = default)
    {
        var written = new List<string>();
        foreach (var (doc, word, pdf) in Plan(docs, folder, common.Setup.TwoDigitRev))
        {
            ct.ThrowIfCancellationRequested();
            OfficeDocs.WriteFile(word, s => WriteWord(s, doc, common));
            written.Add(word);
            OfficeDocs.WriteFile(pdf, s => WritePdf(s, doc, common));
            written.Add(pdf);
        }
        return written;
    }

    // ---- The page's layout (points from the top-left of an A4 page), shared by the PDF and the window's preview ---------

    public const double PageW = 595.28, PageH = 841.89, Left = 75, Right = 514;
    /// <summary>The revision table's columns (Rev, Date, Description, By, CHK, Approved), in points from the left edge.</summary>
    public static readonly double[] ColumnX = [75, 124, 178, 376, 422, 468, 514];
    public static readonly string[] ColumnHeads = ["Rev", "Date", "Description", "By", "CHK", "Approved"];
    /// <summary>Where "page 1/1" is centred.</summary>
    public const double PageNoX = 281;
    public const double TableTop = 583, RowH = 18, SignTop = 619, SignH = 26, FootY = 750, LogoTop = 728, LogoH = 36;

    internal enum Kind { Text, Line, Picture }

    internal enum Align { Left, Center, Right }

    /// <summary>One thing on the page: text (Y is its baseline), a line, or a picture in a box (fitted, kept in proportion).</summary>
    internal sealed record Mark(Kind Kind, double X, double Y, double W, double H, string Text = "", double Size = 10, bool Bold = false, Align Align = Align.Left, OfficeDocs.Picture? Picture = null);

    /// <summary>Everything on a document's page, in drawing order.</summary>
    public static List<Mark> Layout(Doc doc, Common c)
    {
        var s = c.Setup;
        var type = TypeOf(s, doc.Type);
        var marks = new List<Mark>();
        void Text(double x, double y, string text, double size, bool bold = false, double w = 0, Align align = Align.Left)
        {
            if (text.Length > 0) marks.Add(new Mark(Kind.Text, x, y, w, 0, text, size, bold, align));
        }
        // Heading, discipline, project (wrapped) and title.
        Text(Left, 115, type.Heading, 20);
        Text(Left, 153, s.Discipline, 10);
        var y = 194.0;
        foreach (var line in Wrap(s.Project, 10, Right - Left)) { Text(Left, y, line, 10); y += 12; }
        y = Math.Max(y + 52, 274);
        foreach (var line in Wrap(doc.Title, 10, Right - Left)) { Text(Left, y, line, 10); y += 12; }

        // The revision table: a header row and the document's row.
        for (var r = 0; r <= 2; r++) marks.Add(new Mark(Kind.Line, Left, TableTop + r * RowH, Right - Left, 0));
        foreach (var x in ColumnX) marks.Add(new Mark(Kind.Line, x, TableTop, 0, 2 * RowH));
        string[] values = [CleanRev(doc.Rev) is { Length: > 0 } r0 ? r0 : "0", c.Date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture), s.Description, s.By, s.Checked, s.Approved];
        for (var i = 0; i < 6; i++)
        {
            double x0 = ColumnX[i], w = ColumnX[i + 1] - ColumnX[i];
            Text(x0, TableTop + 12, ColumnHeads[i], 8, w: w, align: Align.Center);
            Text(x0, TableTop + RowH + 12, values[i], 8, w: w, align: Align.Center);
        }
        // The number with its REV, and the signatures in the By, CHK and Approved columns.
        Text(Left, SignTop + SignH - 9, $"{doc.Number} {RevText(doc.Rev, s.TwoDigitRev)}", 8);
        OfficeDocs.Picture?[] signs = [c.SignBy, c.SignChecked, c.SignApproved];
        for (var i = 0; i < 3; i++)
            if (signs[i] is { } p) marks.Add(new Mark(Kind.Picture, ColumnX[3 + i] + 2, SignTop, ColumnX[4 + i] - ColumnX[3 + i] - 4, SignH, Picture: p));

        // The foot: form code, page 1/1, logo.
        Text(Left, FootY, type.Form, 6.5);
        Text(Left, FootY, "page 1/1", 6.5, w: 2 * (PageNoX - Left), align: Align.Center);
        if (c.Logo is { } logo) marks.Add(new Mark(Kind.Picture, Right - 90, LogoTop, 90, LogoH, Picture: logo, Align: Align.Right));
        return marks;
    }

    /// <summary>Text broken into lines no wider than <paramref name="width"/> points (Helvetica), at spaces.</summary>
    public static List<string> Wrap(string text, double size, double width, bool bold = false)
    {
        var lines = new List<string>();
        foreach (var para in (text ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var line = "";
            foreach (var word in para.Split([' '], StringSplitOptions.RemoveEmptyEntries))
            {
                var next = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && OfficePdf.TextWidth(next, bold, size) > width) { lines.Add(line); line = word; }
                else line = next;
            }
            if (line.Length > 0) lines.Add(line);
        }
        return lines;
    }

    /// <summary>Where a picture goes in its box: fitted, in proportion, centred (right-aligned for the logo).</summary>
    public static (double X, double Y, double W, double H) Fit(Mark m)
    {
        var p = m.Picture!;
        var scale = Math.Min(m.W / p.Width, m.H / p.Height);
        double w = p.Width * scale, h = p.Height * scale;
        var x = m.Align == Align.Right ? m.X + m.W - w : m.X + (m.W - w) / 2;
        return (x, m.Y + (m.H - h) / 2, w, h);
    }

    // ---- PDF -------------------------------------------------------------------------------------------------------

    public static void WritePdf(Stream output, Doc doc, Common c)
    {
        var marks = Layout(doc, c);
        var images = new List<OfficePdf.Image>();
        var content = new StringBuilder("0 0 0 RG 0 0 0 rg 0.6 w\n");
        foreach (var m in marks)
        {
            switch (m.Kind)
            {
                case Kind.Line:
                    content.Append($"{OfficePdf.N(m.X)} {OfficePdf.N(PageH - m.Y)} m {OfficePdf.N(m.X + m.W)} {OfficePdf.N(PageH - m.Y - m.H)} l S\n");
                    break;
                case Kind.Text:
                {
                    var tw = OfficePdf.TextWidth(m.Text, m.Bold, m.Size);
                    var x = m.Align switch { Align.Center => m.X + (m.W - tw) / 2, Align.Right => m.X + m.W - tw, _ => m.X };
                    content.Append($"BT /F{(m.Bold ? 2 : 1)} {OfficePdf.N(m.Size)} Tf 1 0 0 1 {OfficePdf.N(x)} {OfficePdf.N(PageH - m.Y)} Tm ({OfficePdf.Str(OfficePdf.Encode(m.Text))}) Tj ET\n");
                    break;
                }
                case Kind.Picture when OfficePdf.ToPdf(m.Picture!) is { } image:
                {
                    images.Add(image);
                    var (x, y, w, h) = Fit(m);
                    content.Append($"q {OfficePdf.N(w)} 0 0 {OfficePdf.N(h)} {OfficePdf.N(x)} {OfficePdf.N(PageH - y - h)} cm /Im{images.Count} Do Q\n");
                    break;
                }
            }
        }

        var objects = new List<byte[]>();
        int Add(string text) { objects.Add(OfficePdf.Bytes(text)); return objects.Count; }
        int AddStream(string dict, byte[] data)
        {
            var head = OfficePdf.Bytes($"<< {dict} /Length {data.Length} >>\nstream\n");
            var tail = OfficePdf.Bytes("\nendstream");
            objects.Add([.. head, .. data, .. tail]);
            return objects.Count;
        }
        Add("<< /Type /Catalog /Pages 2 0 R >>");
        Add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Add(""); // the page, once its parts have numbers
        var regular = Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        var bold = Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
        var xobjects = new StringBuilder();
        for (var i = 0; i < images.Count; i++)
        {
            var img = images[i];
            int id;
            if (img.Parms == "JPEG")
                id = AddStream($"/Type /XObject /Subtype /Image /Width {img.W} /Height {img.H} /ColorSpace {img.ColorSpace} /BitsPerComponent 8 /Filter /DCTDecode" +
                               (img.ColorSpace == "/DeviceCMYK" ? " /Decode [1 0 1 0 1 0 1 0]" : ""), img.Data);
            else
            {
                var mask = img.Alpha is null ? 0 : AddStream($"/Type /XObject /Subtype /Image /Width {img.W} /Height {img.H} /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode", img.Alpha);
                id = AddStream($"/Type /XObject /Subtype /Image /Width {img.W} /Height {img.H} /ColorSpace {img.ColorSpace} /BitsPerComponent {img.Bpc} /Filter /FlateDecode" +
                               (img.Parms is null ? "" : " /DecodeParms " + img.Parms) + (mask > 0 ? $" /SMask {mask} 0 R" : ""), img.Data);
            }
            xobjects.Append($"/Im{i + 1} {id} 0 R ");
        }
        var stream = AddStream("/Filter /FlateDecode", OfficePdf.Deflate(OfficePdf.Bytes(content.ToString())));
        objects[2] = OfficePdf.Bytes($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {OfficePdf.N(PageW)} {OfficePdf.N(PageH)}] /Contents {stream} 0 R " +
                                     $"/Resources << /Font << /F1 {regular} 0 R /F2 {bold} 0 R >>" + (images.Count > 0 ? $" /XObject << {xobjects}>>" : "") + " >> >>");
        var title = $"{TypeOf(c.Setup, doc.Type).Heading} {doc.Number} {RevText(doc.Rev, c.Setup.TwoDigitRev)}";
        var info = Add($"<< /Title ({OfficePdf.Str(OfficePdf.Encode(title))}) /CreationDate (D:{DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}) >>");

        var offsets = new List<long>();
        var start = OfficePdf.Bytes("%PDF-1.4\n%âãÏÓ\n");
        output.Write(start, 0, start.Length);
        long position = start.Length;
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(position);
            byte[] bytes = [.. OfficePdf.Bytes($"{i + 1} 0 obj\n"), .. objects[i], .. OfficePdf.Bytes("\nendobj\n")];
            output.Write(bytes, 0, bytes.Length);
            position += bytes.Length;
        }
        var xref = new StringBuilder($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) xref.Append(o.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        xref.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R /Info {info} 0 R >>\nstartxref\n{position}\n%%EOF\n");
        var end = OfficePdf.Bytes(xref.ToString());
        output.Write(end, 0, end.Length);
    }

    // ---- Word ------------------------------------------------------------------------------------------------------

    private const string Font = "Arial";

    private static string WRun(string text, double size, bool bold = false)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var half = (int)Math.Round(size * 2);
        return $"<w:r><w:rPr><w:rFonts w:ascii=\"{Font}\" w:hAnsi=\"{Font}\" w:cs=\"{Font}\"/>{(bold ? "<w:b/>" : "")}<w:sz w:val=\"{half}\"/><w:szCs w:val=\"{half}\"/></w:rPr>" +
               $"<w:t xml:space=\"preserve\">{ChecklistDocx.Esc(text)}</w:t></w:r>";
    }

    private static string WPara(string runs, int before = 0, int after = 0, string jc = "") =>
        $"<w:p><w:pPr><w:spacing w:before=\"{before}\" w:after=\"{after}\" w:line=\"240\" w:lineRule=\"auto\"/>{(jc.Length > 0 ? $"<w:jc w:val=\"{jc}\"/>" : "")}</w:pPr>{runs}</w:p>";

    private static int Tw(double points) => (int)Math.Round(points * 20);

    private static string WCell(int width, string content, bool borders) =>
        $"<w:tc><w:tcPr><w:tcW w:w=\"{width}\" w:type=\"dxa\"/>" +
        (borders ? "" : "<w:tcBorders><w:top w:val=\"nil\"/><w:left w:val=\"nil\"/><w:bottom w:val=\"nil\"/><w:right w:val=\"nil\"/></w:tcBorders>") +
        $"<w:vAlign w:val=\"center\"/></w:tcPr>{(content.Length == 0 ? "<w:p/>" : content)}</w:tc>";

    public static void WriteWord(Stream output, Doc doc, Common c)
    {
        var s = c.Setup;
        var type = TypeOf(s, doc.Type);
        var media = new List<(string Name, string RelId, ChecklistDocx.Image Image)>();
        string Picture(OfficeDocs.Picture p, double maxW, double maxH, string name)
        {
            var ext = p.Ext == "png" ? "png" : "jpeg";
            var relId = "rIdImg" + (media.Count + 1);
            media.Add(($"image{media.Count + 1}.{ext}", relId, new ChecklistDocx.Image(p.Bytes, ext)));
            const double MmPerPt = 25.4 / 72;
            return ChecklistDocx.Drawing(relId, media.Count, p.Width, p.Height, maxW * MmPerPt, maxH * MmPerPt, name);
        }

        var body = new StringBuilder();
        // The page starts 72 pt down (top margin); the paragraphs' spacing puts each line where the PDF has it.
        body.Append(WPara(WRun(type.Heading, 20), before: Tw(22)));
        body.Append(WPara(WRun(s.Discipline, 10), before: Tw(24)));
        body.Append(WPara(WRun(s.Project, 10), before: Tw(28)));
        body.Append(WPara(WRun(doc.Title, 10), before: Tw(56)));

        // The revision table, placed on the page where the PDF has it, with the number and signatures under it.
        var widths = Enumerable.Range(0, 6).Select(i => Tw(ColumnX[i + 1] - ColumnX[i])).ToArray();
        var borders = string.Concat(new[] { "top", "left", "bottom", "right", "insideH", "insideV" }.Select(b => $"<w:{b} w:val=\"single\" w:sz=\"6\" w:space=\"0\" w:color=\"000000\"/>"));
        var table = new StringBuilder($"<w:tbl><w:tblPr><w:tblpPr w:leftFromText=\"0\" w:rightFromText=\"0\" w:vertAnchor=\"page\" w:horzAnchor=\"page\" w:tblpX=\"{Tw(Left)}\" w:tblpY=\"{Tw(TableTop)}\"/>" +
            $"<w:tblW w:w=\"{widths.Sum()}\" w:type=\"dxa\"/><w:tblLayout w:type=\"fixed\"/><w:tblBorders>{borders}</w:tblBorders>" +
            "<w:tblCellMar><w:top w:w=\"0\" w:type=\"dxa\"/><w:left w:w=\"40\" w:type=\"dxa\"/><w:bottom w:w=\"0\" w:type=\"dxa\"/><w:right w:w=\"40\" w:type=\"dxa\"/></w:tblCellMar></w:tblPr>" +
            $"<w:tblGrid>{string.Concat(widths.Select(w => $"<w:gridCol w:w=\"{w}\"/>"))}</w:tblGrid>");
        string[] values = [CleanRev(doc.Rev) is { Length: > 0 } r0 ? r0 : "0", c.Date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture), s.Description, s.By, s.Checked, s.Approved];
        foreach (var row in new[] { ColumnHeads, values })
        {
            table.Append($"<w:tr><w:trPr><w:cantSplit/><w:trHeight w:val=\"{Tw(RowH)}\" w:hRule=\"exact\"/></w:trPr>");
            for (var i = 0; i < 6; i++) table.Append(WCell(widths[i], WPara(WRun(row[i], 8), jc: "center"), true));
            table.Append("</w:tr>");
        }
        // The last row has no borders: the number across Rev to Description, then each role's signature (or room to sign).
        table.Append($"<w:tr><w:trPr><w:cantSplit/><w:trHeight w:val=\"{Tw(SignH)}\" w:hRule=\"exact\"/></w:trPr>");
        table.Append($"<w:tc><w:tcPr><w:tcW w:w=\"{widths.Take(3).Sum()}\" w:type=\"dxa\"/><w:gridSpan w:val=\"3\"/><w:tcBorders><w:top w:val=\"single\" w:sz=\"6\" w:space=\"0\" w:color=\"000000\"/><w:left w:val=\"nil\"/><w:bottom w:val=\"nil\"/><w:right w:val=\"nil\"/></w:tcBorders><w:vAlign w:val=\"bottom\"/></w:tcPr>" +
                     $"{WPara(WRun($"{doc.Number} {RevText(doc.Rev, s.TwoDigitRev)}", 8), after: Tw(6))}</w:tc>");
        OfficeDocs.Picture?[] signs = [c.SignBy, c.SignChecked, c.SignApproved];
        for (var i = 0; i < 3; i++)
        {
            var content = signs[i] is { } p ? WPara(Picture(p, ColumnX[4 + i] - ColumnX[3 + i] - 4, SignH, "Signature"), jc: "center") : "";
            table.Append($"<w:tc><w:tcPr><w:tcW w:w=\"{widths[3 + i]}\" w:type=\"dxa\"/><w:tcBorders><w:top w:val=\"single\" w:sz=\"6\" w:space=\"0\" w:color=\"000000\"/><w:left w:val=\"nil\"/><w:bottom w:val=\"nil\"/><w:right w:val=\"nil\"/></w:tcBorders><w:vAlign w:val=\"center\"/></w:tcPr>" +
                         $"{(content.Length == 0 ? "<w:p/>" : content)}</w:tc>");
        }
        table.Append("</w:tr></w:tbl>");
        body.Append(table).Append("<w:p/>");

        // The foot: form code, page 1/1 and the logo, as a borderless table placed where the PDF has them (in the body, so
        // the logo's picture needs no footer part of its own).
        int f1 = Tw(PageNoX - Left - 85), f2 = Tw(170), f3 = Tw(Right - Left) - f1 - f2;
        var none = "<w:top w:val=\"nil\"/><w:left w:val=\"nil\"/><w:bottom w:val=\"nil\"/><w:right w:val=\"nil\"/>";
        body.Append($"<w:tbl><w:tblPr><w:tblpPr w:leftFromText=\"0\" w:rightFromText=\"0\" w:vertAnchor=\"page\" w:horzAnchor=\"page\" w:tblpX=\"{Tw(Left)}\" w:tblpY=\"{Tw(LogoTop)}\"/>" +
                    $"<w:tblW w:w=\"{f1 + f2 + f3}\" w:type=\"dxa\"/><w:tblLayout w:type=\"fixed\"/><w:tblBorders>{none}<w:insideH w:val=\"nil\"/><w:insideV w:val=\"nil\"/></w:tblBorders>" +
                    "<w:tblCellMar><w:top w:w=\"0\" w:type=\"dxa\"/><w:left w:w=\"0\" w:type=\"dxa\"/><w:bottom w:w=\"0\" w:type=\"dxa\"/><w:right w:w=\"0\" w:type=\"dxa\"/></w:tblCellMar></w:tblPr>" +
                    $"<w:tblGrid><w:gridCol w:w=\"{f1}\"/><w:gridCol w:w=\"{f2}\"/><w:gridCol w:w=\"{f3}\"/></w:tblGrid><w:tr><w:trPr><w:trHeight w:val=\"{Tw(LogoH)}\" w:hRule=\"exact\"/></w:trPr>" +
                    WCell(f1, WPara(WRun(type.Form, 6.5)), false) + WCell(f2, WPara(WRun("page 1/1", 6.5), jc: "center"), false) +
                    WCell(f3, c.Logo is { } logo ? WPara(Picture(logo, 90, LogoH, "Logo"), jc: "right") : "", false) + "</w:tr></w:tbl><w:p/>");
        var footer = $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:ftr {ChecklistDocx.Namespaces}><w:p/></w:ftr>";
        var document = $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document {ChecklistDocx.Namespaces}><w:body>{body}" +
                       $"<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/>" +
                       $"<w:pgMar w:top=\"1440\" w:right=\"{11906 - Tw(Right)}\" w:bottom=\"720\" w:left=\"{Tw(Left)}\" w:header=\"708\" w:footer=\"360\" w:gutter=\"0\"/></w:sectPr></w:body></w:document>";
        ChecklistDocx.WritePackage(output, document, footer, media, $"{type.Heading} {doc.Number} {RevText(doc.Rev, s.TwoDigitRev)}");
    }

    // ---- Kept on this PC ---------------------------------------------------------------------------------------------

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>%APPDATA%\SketchProMax\Signature pages (the tests point it elsewhere).</summary>
    internal static string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax", "Signature pages");

    /// <summary>A project name as a folder name.</summary>
    public static string ProjectFolder(string project)
    {
        var clean = new string((project ?? "").Select(ch => Path.GetInvalidFileNameChars().Contains(ch) || ch is '/' or '\\' or ':' ? '_' : ch).ToArray()).Trim().TrimEnd('.');
        return Path.Combine(Root, clean.Length == 0 ? "Default" : clean.Length > 60 ? clean.Substring(0, 60) : clean);
    }

    public static Setup LoadSetup(string project)
    {
        try
        {
            var path = Path.Combine(ProjectFolder(project), "setup.json");
            if (!File.Exists(path)) return new Setup();
            var setup = JsonSerializer.Deserialize<Setup>(File.ReadAllText(path), Json) ?? new Setup();
            setup.Types ??= DefaultTypes();
            setup.Docs ??= [];
            return setup;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new Setup(); }
    }

    public static void SaveSetup(string project, Setup setup) =>
        SafeFile.Write(Path.Combine(ProjectFolder(project), "setup.json"), JsonSerializer.Serialize(setup, Json));

    /// <summary>The projects set up on this PC, newest first.</summary>
    public static List<string> Projects()
    {
        try
        {
            if (!Directory.Exists(Root)) return [];
            return new DirectoryInfo(Root).GetDirectories().Where(d => File.Exists(Path.Combine(d.FullName, "setup.json")))
                .OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d.FullName, "setup.json"))).Select(d => d.Name).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>The project's logo (logo.png or logo.jpg next to its setup), or null.</summary>
    public static OfficeDocs.Picture? Logo(string project)
    {
        foreach (var name in new[] { "logo.png", "logo.jpg" })
        {
            var path = Path.Combine(ProjectFolder(project), name);
            try { if (File.Exists(path) && OfficeDocs.ReadPicture(File.ReadAllBytes(path)) is { } p) return p; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    /// <summary>Keeps a copy of <paramref name="picked"/> as the project's logo; "" removes it. Throws InvalidDataException when it isn't a PNG or JPG.</summary>
    public static void SetLogo(string project, string picked)
    {
        var folder = ProjectFolder(project);
        Directory.CreateDirectory(folder);
        if (picked.Length > 0)
        {
            var bytes = File.ReadAllBytes(picked);
            var p = OfficeDocs.ReadPicture(bytes) ?? throw new InvalidDataException("The logo must be a PNG or JPG picture.");
            foreach (var old in new[] { "logo.png", "logo.jpg" }) File.Delete(Path.Combine(folder, old));
            File.WriteAllBytes(Path.Combine(folder, p.Ext == "png" ? "logo.png" : "logo.jpg"), bytes);
        }
        else foreach (var old in new[] { "logo.png", "logo.jpg" }) File.Delete(Path.Combine(folder, old));
    }

    // ---- People and their locked signatures ----------------------------------------------------------------------------

    /// <summary>A person who signs: initials (what the table shows), name, and their signature locked with their PIN.</summary>
    internal sealed class Signer
    {
        public string Initials { get; set; } = "";
        public string Name { get; set; } = "";
        public string Salt { get; set; } = "";
        public string Check { get; set; } = "";
        public string Iv { get; set; } = "";
        public string Locked { get; set; } = "";
    }

    private const int Rounds = 100_000;

    /// <summary>The key (32 bytes) and the check value (32 bytes) a PIN gives with this salt.</summary>
    private static (byte[] Key, byte[] Check) Derive(string pin, byte[] salt)
    {
        using var kdf = new Rfc2898DeriveBytes(pin, salt, Rounds, HashAlgorithmName.SHA256);
        var bytes = kdf.GetBytes(64);
        using var sha = SHA256.Create();
        return (bytes.Take(32).ToArray(), sha.ComputeHash(bytes.Skip(32).ToArray()));
    }

    /// <summary>Why a PIN can't be used, or null: at least 4 characters.</summary>
    public static string? PinProblem(string pin) => (pin ?? "").Length < 4 ? "The PIN needs at least 4 characters." : null;

    /// <summary>A person with <paramref name="picture"/> (PNG or JPG) locked by <paramref name="pin"/>.</summary>
    public static Signer Lock(string initials, string name, byte[] picture, string pin)
    {
        if (PinProblem(pin) is { } problem) throw new ArgumentException(problem);
        if (OfficeDocs.ReadPicture(picture) is null) throw new InvalidDataException("The signature must be a PNG or JPG picture.");
        var salt = new byte[16];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(salt);
        var (key, check) = Derive(pin, salt);
        using var aes = Aes.Create();
        aes.Key = key;
        aes.GenerateIV();
        using var enc = aes.CreateEncryptor();
        var locked = enc.TransformFinalBlock(picture, 0, picture.Length);
        return new Signer
        {
            Initials = initials.Trim(), Name = name.Trim(), Salt = Convert.ToBase64String(salt), Check = Convert.ToBase64String(check),
            Iv = Convert.ToBase64String(aes.IV), Locked = Convert.ToBase64String(locked),
        };
    }

    /// <summary>The person's signature when <paramref name="pin"/> is theirs; null when it isn't.</summary>
    public static OfficeDocs.Picture? Unlock(Signer signer, string pin)
    {
        try
        {
            var (key, check) = Derive(pin ?? "", Convert.FromBase64String(signer.Salt));
            if (!check.SequenceEqual(Convert.FromBase64String(signer.Check))) return null;
            using var aes = Aes.Create();
            aes.Key = key;
            aes.IV = Convert.FromBase64String(signer.Iv);
            using var dec = aes.CreateDecryptor();
            var locked = Convert.FromBase64String(signer.Locked);
            return OfficeDocs.ReadPicture(dec.TransformFinalBlock(locked, 0, locked.Length));
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException) { return null; }
    }

    private static string SignersPath => Path.Combine(Root, "people.json");

    public static List<Signer> LoadSigners()
    {
        try { return File.Exists(SignersPath) ? JsonSerializer.Deserialize<List<Signer>>(File.ReadAllText(SignersPath), Json) ?? [] : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    public static void SaveSigners(List<Signer> signers) => SafeFile.Write(SignersPath, JsonSerializer.Serialize(signers, Json));

    /// <summary>The person with these initials (dots and spaces ignored: "E.N." is "EN"), or null.</summary>
    public static Signer? FindSigner(IEnumerable<Signer> signers, string initials)
    {
        static string Key(string s) => new string((s ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        var k = Key(initials);
        return k.Length == 0 ? null : signers.FirstOrDefault(s => Key(s.Initials) == k);
    }
}
