using System.Globalization;
using System.IO;
using System.Text;
using static SketchProMax.Tools.BerthCostMath;

namespace SketchProMax.Tools;

/// <summary>
/// The base formats of the berth costing files, built in (no file to give): the BOQ Excel of a berth (items lettered by
/// section, quantities and descriptions from the model, To Collection per section, the Collection, page header and
/// footer), the Word back-up calculation (lettered sections, a table per berth with its coloured heading, Calculation
/// and Total rows, the reinforcement tables) and the costing workbook (a summary per berth with US$ per lm, a sheet per
/// berth and the unit costs). A project can replace a base with its own template (exported from here, edited, imported:
/// BerthCostExports fills it). The project's own words (header lines, document number) come from Details. No Revit.
/// </summary>
internal static class BerthCostDocs
{
    private static string F(double v, int dp) => v.ToString(dp == 0 ? "0" : "0." + new string('0', dp), CultureInfo.InvariantCulture);
    private static string Esc(string? s) => OfficeDocs.Esc(s);

    // =====================================================================================================================
    // A small styled workbook writer (fonts, borders, wrap, widths, heights, header and footer, print titles, breaks)
    // =====================================================================================================================

    internal sealed class Style
    {
        public string Font = "Times New Roman";
        public double Size = 12;
        public bool Bold, Underline, Wrap, Shade;
        public string Align = "", VAlign = "top", Format = "General";
        public bool Top, Bottom, DoubleBottom, Box;
        public string Key => $"{Font}|{Size}|{Bold}|{Underline}|{Wrap}|{Shade}|{Align}|{VAlign}|{Format}|{Top}|{Bottom}|{DoubleBottom}|{Box}";
    }

    internal sealed class Sheet
    {
        public string Name = "Sheet1";
        public readonly SortedDictionary<int, SortedDictionary<int, (object Value, int Style)>> Cells = [];
        public readonly Dictionary<int, double> Heights = [];
        public double[] Widths = [];
        public readonly List<string> Merges = [];
        public readonly List<int> Breaks = [];
        public string Header = "", Footer = "";
        public int TitleRows;
        public bool Landscape;
    }

    /// <summary>A formula cell (without =).</summary>
    internal sealed record Formula(string Text);

    internal sealed class Book
    {
        public readonly List<Sheet> Sheets = [];
        private readonly List<Style> _styles = [new()];
        private readonly Dictionary<string, int> _index = new() { [new Style().Key] = 0 };

        public int S(Style s)
        {
            if (_index.TryGetValue(s.Key, out var i)) return i;
            _styles.Add(s);
            return _index[s.Key] = _styles.Count - 1;
        }

        public static void Put(Sheet sh, int row, int col, object value, int style = 0)
        {
            if (!sh.Cells.TryGetValue(row, out var r)) sh.Cells[row] = r = [];
            r[col] = (value, style);
        }

        public void Save(string path, string title) => OfficeDocs.WriteFile(path, s => Write(s, title));

        private void Write(Stream output, string title)
        {
            using var zip = new OfficeDocs.Package(output);
            zip.Text("[Content_Types].xml", OfficeDocs.XmlHead +
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
                "<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/>" +
                string.Concat(Sheets.Select((_, i) => $"<Override PartName=\"/xl/worksheets/sheet{i + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>")) +
                "</Types>");
            zip.Text("_rels/.rels", OfficeDocs.XmlHead +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/>" +
                "</Relationships>");
            zip.Text("docProps/core.xml", OfficeDocs.Core(title, DateTime.Now));
            var defined = new StringBuilder();
            for (var i = 0; i < Sheets.Count; i++)
                if (Sheets[i].TitleRows > 0)
                    defined.Append($"<definedName name=\"_xlnm.Print_Titles\" localSheetId=\"{i}\">{Esc("'" + Sheets[i].Name.Replace("'", "''") + "'")}!$1:${Sheets[i].TitleRows}</definedName>");
            zip.Text("xl/workbook.xml", OfficeDocs.XmlHead +
                "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                "<bookViews><workbookView/></bookViews><sheets>" +
                string.Concat(Sheets.Select((s, i) => $"<sheet name=\"{Esc(s.Name)}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>")) + "</sheets>" +
                (defined.Length > 0 ? "<definedNames>" + defined + "</definedNames>" : "") + "<calcPr fullCalcOnLoad=\"1\"/></workbook>");
            zip.Text("xl/_rels/workbook.xml.rels", OfficeDocs.XmlHead +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                string.Concat(Sheets.Select((_, i) => $"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>")) +
                $"<Relationship Id=\"rId{Sheets.Count + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
            zip.Text("xl/styles.xml", StylesXml());
            for (var i = 0; i < Sheets.Count; i++) zip.Text($"xl/worksheets/sheet{i + 1}.xml", SheetXml(Sheets[i]));
        }

        private string StylesXml()
        {
            var formats = _styles.Select(s => s.Format).Where(f => f != "General").Distinct().ToList();
            int FmtId(string f) => f == "General" ? 0 : 164 + formats.IndexOf(f);
            // Font 0 is the Normal style's, which sets the width unit of the columns: Arial 10, as the bills are.
            var fonts = new[] { (Font: "Arial", Size: 10.0, Bold: false, Underline: false) }.Concat(_styles.Select(s => (s.Font, s.Size, s.Bold, s.Underline))).Distinct().ToList();
            var borders = _styles.Select(s => (s.Top, s.Bottom, s.DoubleBottom, s.Box)).Distinct().ToList();
            var sb = new StringBuilder(OfficeDocs.XmlHead + "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
            if (formats.Count > 0) sb.Append($"<numFmts count=\"{formats.Count}\">" + string.Concat(formats.Select(f => $"<numFmt numFmtId=\"{FmtId(f)}\" formatCode=\"{Esc(f)}\"/>")) + "</numFmts>");
            sb.Append($"<fonts count=\"{fonts.Count}\">");
            foreach (var f in fonts)
                sb.Append("<font>" + (f.Bold ? "<b/>" : "") + (f.Underline ? "<u/>" : "") + $"<sz val=\"{F(f.Size, 1)}\"/><name val=\"{Esc(f.Font)}\"/></font>");
            sb.Append("</fonts><fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill>" +
                "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFDAE9F8\"/><bgColor indexed=\"64\"/></patternFill></fill></fills>");
            sb.Append($"<borders count=\"{borders.Count}\">");
            foreach (var b in borders)
            {
                string Side(string name, bool on, string style = "thin") => on ? $"<{name} style=\"{style}\"><color auto=\"1\"/></{name}>" : $"<{name}/>";
                sb.Append("<border>" + Side("left", b.Box) + Side("right", b.Box) + Side("top", b.Top || b.Box) +
                    (b.DoubleBottom ? Side("bottom", true, "double") : Side("bottom", b.Bottom || b.Box)) + "<diagonal/></border>");
            }
            sb.Append("</borders><cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>");
            sb.Append($"<cellXfs count=\"{_styles.Count}\">");
            foreach (var s in _styles)
            {
                var align = (s.Align.Length > 0 ? $" horizontal=\"{s.Align}\"" : "") + (s.VAlign.Length > 0 ? $" vertical=\"{s.VAlign}\"" : "") + (s.Wrap ? " wrapText=\"1\"" : "");
                sb.Append($"<xf numFmtId=\"{FmtId(s.Format)}\" fontId=\"{fonts.IndexOf((s.Font, s.Size, s.Bold, s.Underline))}\" fillId=\"{(s.Shade ? 2 : 0)}\" borderId=\"{borders.IndexOf((s.Top, s.Bottom, s.DoubleBottom, s.Box))}\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\">" +
                    (align.Length > 0 ? $"<alignment{align}/>" : "") + "</xf>");
            }
            sb.Append("</cellXfs><cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>");
            return sb.ToString();
        }

        private static string SheetXml(Sheet sh)
        {
            var sb = new StringBuilder(OfficeDocs.XmlHead + "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");
            sb.Append("<sheetPr><pageSetUpPr fitToPage=\"1\"/></sheetPr>");
            sb.Append("<sheetViews><sheetView workbookViewId=\"0\"" + (sh.TitleRows > 0 ? "><pane ySplit=\"" + sh.TitleRows + "\" topLeftCell=\"A" + (sh.TitleRows + 1) + "\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView>" : "/>") + "</sheetViews>");
            sb.Append("<sheetFormatPr defaultRowHeight=\"15.6\"/>");
            if (sh.Widths.Length > 0)
                sb.Append("<cols>" + string.Concat(sh.Widths.Select((w, i) => $"<col min=\"{i + 1}\" max=\"{i + 1}\" width=\"{F(w, 2)}\" customWidth=\"1\"/>")) + "</cols>");
            sb.Append("<sheetData>");
            var rows = sh.Cells.Keys.Concat(sh.Heights.Keys).Distinct().OrderBy(r => r);
            foreach (var r in rows)
            {
                sb.Append($"<row r=\"{r}\"" + (sh.Heights.TryGetValue(r, out var h) ? $" ht=\"{F(h, 2)}\" customHeight=\"1\"" : "") + ">");
                if (sh.Cells.TryGetValue(r, out var cells))
                    foreach (var (c, (value, style)) in cells.Select(kv => (kv.Key, kv.Value)))
                    {
                        var at = OfficeXlsx.Column(c) + r.ToString(CultureInfo.InvariantCulture);
                        var st = style > 0 ? $" s=\"{style}\"" : "";
                        switch (value)
                        {
                            case Formula f: sb.Append($"<c r=\"{at}\"{st}><f>{Esc(f.Text)}</f></c>"); break;
                            case double d: sb.Append($"<c r=\"{at}\"{st}><v>{d.ToString("R", CultureInfo.InvariantCulture)}</v></c>"); break;
                            case int n: sb.Append($"<c r=\"{at}\"{st}><v>{n}</v></c>"); break;
                            case string t when t.Length == 0: sb.Append($"<c r=\"{at}\"{st}/>"); break;
                            default: sb.Append($"<c r=\"{at}\"{st} t=\"inlineStr\"><is><t xml:space=\"preserve\">{Esc(value?.ToString())}</t></is></c>"); break;
                        }
                    }
                sb.Append("</row>");
            }
            sb.Append("</sheetData>");
            if (sh.Merges.Count > 0) sb.Append($"<mergeCells count=\"{sh.Merges.Count}\">" + string.Concat(sh.Merges.Select(m => $"<mergeCell ref=\"{m}\"/>")) + "</mergeCells>");
            sb.Append("<pageMargins left=\"0.5\" right=\"0.5\" top=\"1.2\" bottom=\"0.9\" header=\"0.3\" footer=\"0.3\"/>");
            sb.Append($"<pageSetup paperSize=\"9\" orientation=\"{(sh.Landscape ? "landscape" : "portrait")}\" fitToWidth=\"1\" fitToHeight=\"0\"/>");
            if (sh.Header.Length > 0 || sh.Footer.Length > 0)
                sb.Append("<headerFooter>" + (sh.Header.Length > 0 ? $"<oddHeader>{Esc(sh.Header)}</oddHeader>" : "") + (sh.Footer.Length > 0 ? $"<oddFooter>{Esc(sh.Footer)}</oddFooter>" : "") + "</headerFooter>");
            if (sh.Breaks.Count > 0)
                sb.Append($"<rowBreaks count=\"{sh.Breaks.Count}\" manualBreakCount=\"{sh.Breaks.Count}\">" + string.Concat(sh.Breaks.Select(b => $"<brk id=\"{b}\" max=\"16383\" man=\"1\"/>")) + "</rowBreaks>");
            sb.Append("</worksheet>");
            return sb.ToString();
        }
    }

    /// <summary>A header or footer section's text: Excel codes escaped, the font set, lines kept.</summary>
    private static string HeaderText(string text, int size = 0) =>
        "&\"Times New Roman,Regular\"" + (size > 0 ? "&" + size.ToString(CultureInfo.InvariantCulture) : "") + (text ?? "").Replace("&", "&&").Replace("\r\n", "\n");

    /// <summary>About how tall a wrapped cell's row must be (Times 12 in a column <paramref name="width"/> characters wide).</summary>
    private static double RowHeight(string text, double width)
    {
        var perLine = Math.Max(10, width * 1.12);
        var lines = (text ?? "").Split('\n').Sum(l => Math.Max(1, (int)Math.Ceiling(l.Length / perLine)));
        return Math.Max(15.6, lines * 15.6);
    }

    // =====================================================================================================================
    // The BOQ
    // =====================================================================================================================

    /// <summary>The items a berth's BOQ lists: ticked, with a description and a quantity, in BOQ order.</summary>
    public static List<(Item Item, double Qty)> BoqItems(Project p, Berth b) =>
        p.Items.Where(i => i.InFiles && i.BoqText.Trim().Length > 0).OrderBy(i => i.BoqOrder).ThenBy(i => i.No)
            .Select(i => (i, Quantity(p, b, i.No).Qty)).Where(x => x.Qty is > 0).Select(x => (x.i, x.Qty!.Value)).ToList();

    /// <summary>
    /// The BOQ of one berth in the base format. Returns notes (numbers the model doesn't give). Rates and amounts are left
    /// to price unless Details says to show the unit rates; the amounts are formulas either way.
    /// </summary>
    public static List<string> WriteBoq(Project p, Berth b, string path)
    {
        var notes = new List<string>();
        var book = new Book();
        var sh = new Sheet { Name = "ST", Widths = [5.1, 49.9, 9.5, 7.0, 11.0, 13.6], TitleRows = 2 };
        book.Sheets.Add(sh);
        var d = p.Details;
        int St(Action<Style> set) { var s = new Style(); set(s); return book.S(s); }
        var head = St(s => { s.Align = "center"; s.VAlign = "center"; });
        var headLine = St(s => { s.Align = "center"; s.Bottom = true; });
        var works = St(s => { s.Bold = true; s.Underline = true; s.Wrap = true; });
        var under = St(s => { s.Underline = true; s.Wrap = true; });
        var letter = St(s => { s.Align = "center"; });
        var text = St(s => { s.Wrap = true; s.Align = "left"; });
        var qty = St(s => { s.Format = "#,##0"; s.Align = "right"; });
        var unit = St(s => { s.Align = "center"; });
        var rate = St(s => { s.Format = "#,##0.00"; s.Align = "right"; });
        var amount = St(s => { s.Format = "#,##0"; s.Align = "right"; });
        var collect = St(s => { s.Align = "center"; });
        var collectSum = St(s => { s.Format = "#,##0"; s.Align = "right"; s.Top = true; });
        var summary = St(s => { s.Format = "#,##0"; s.Align = "right"; s.DoubleBottom = true; });

        string[] heads = ["Item", "Description", "Qty", "Unit", "Rate", "Amount"];
        for (var c = 0; c < heads.Length; c++) Book.Put(sh, 1, c, heads[c], head);
        for (var c = 0; c < 6; c++) Book.Put(sh, 2, c, c is 4 or 5 ? p.Currency : "", headLine);
        var row = 4;
        Book.Put(sh, row, 1, d.Works, works);
        row += 2;
        var title = ForBerth(d.Title, b);
        Book.Put(sh, row, 1, title, under);
        sh.Heights[row] = RowHeight(title, 49.9);
        row += 2;

        var sections = new List<(string Name, int SumRow)>();
        var items = BoqItems(p, b);
        foreach (var section in items.GroupBy(x => x.Item.Section.Split('|')[0].Trim()))
        {
            Book.Put(sh, row, 1, section.Key.Length > 0 ? section.Key : "OTHER ITEMS", under);
            row += 2;
            var first = row;
            var sub = "";
            var n = 0;
            foreach (var (item, q) in section)
            {
                var parts = item.Section.Split('|');
                var here = parts.Length > 1 ? ForBerth(parts[1].Trim(), b) : "";
                if (here != sub && here.Length > 0) { Book.Put(sh, row, 1, here, under); row += 2; }
                sub = here;
                var words = Describe(p, b, item, notes);
                Book.Put(sh, row, 0, ((char)('A' + n % 26)).ToString(), letter);
                n++;
                Book.Put(sh, row, 1, words, text);
                sh.Heights[row] = RowHeight(words, 49.9);
                Book.Put(sh, row, 2, Math.Round(q), qty);
                Book.Put(sh, row, 3, item.BoqUnit, unit);
                if (d.BoqRates && RateFor(p, b, item) is > 0 and var r) Book.Put(sh, row, 4, Math.Round(r, 2), rate);
                else Book.Put(sh, row, 4, "", rate);
                Book.Put(sh, row, 5, new Formula($"IF(E{row}=\"\",\"\",C{row}*E{row})"), amount);
                row += 2;
            }
            Book.Put(sh, row, 2, "To Collection", collect);
            sh.Merges.Add($"C{row}:D{row}");
            Book.Put(sh, row, 5, new Formula($"IF(COUNT(F{first}:F{row - 1})=0,\"\",SUM(F{first}:F{row - 1}))"), collectSum);
            sections.Add((section.Key, row));
            sh.Breaks.Add(row);
            row += 2;
        }
        // The collection: each section's total, then the works' total to the summary.
        Book.Put(sh, row, 1, "COLLECTION", under);
        row += 2;
        foreach (var (name, sum) in sections)
        {
            Book.Put(sh, row, 1, name, text);
            Book.Put(sh, row, 5, new Formula($"F{sum}"), amount);
            row += 2;
        }
        Book.Put(sh, row, 1, d.Works, works);
        row++;
        // Without rates (a bill for pricing) the amounts stay empty, not 0.
        var refs = string.Join(",", sections.Select(s => $"F{s.SumRow}"));
        Book.Put(sh, row, 1, "To Summary", text);
        Book.Put(sh, row, 4, p.Currency, unit);
        Book.Put(sh, row, 5, new Formula(sections.Count > 0 ? $"IF(COUNT({refs})=0,\"\",SUM({refs}))" : "\"\""), summary);

        var left = string.Join("\n", new[] { d.HeaderLeft }.Where(x => x.Trim().Length > 0));
        sh.Header = (left.Length > 0 ? "&L" + HeaderText(left) : "") + (d.HeaderRight.Trim().Length > 0 ? "&R" + HeaderText(d.HeaderRight) : "");
        sh.Footer = "&L" + HeaderText(d.Works + (d.DocNumber.Trim().Length > 0 ? "\n" + ForBerth(d.DocNumber, b) : ""), 8) +
            "&R" + HeaderText($"Berth {(b.Number.Length > 0 ? b.Number : b.Name)}\n ST - Page &P of &N", 9).Replace("&&P", "&P").Replace("&&N", "&N");
        book.Save(path, $"{b.Title} BOQ");
        return notes.Distinct().ToList();
    }

    /// <summary>The BOQ's file name: the document number when set, else "BOQ Berth n".</summary>
    public static string BoqName(Project p, Berth b) =>
        OfficeDocs.SafeName(p.Details.DocNumber.Trim().Length > 0 ? ForBerth(p.Details.DocNumber, b) : $"BOQ Berth {(b.Number.Length > 0 ? b.Number.PadLeft(2, '0') : b.Name)}");

    // =====================================================================================================================
    // The costing workbook
    // =====================================================================================================================

    /// <summary>
    /// The costing workbook: Summary (each berth's length, structural works, preliminaries, total and US$ per lm, and the
    /// whole), a sheet per berth (each item's quantity, where it came from, rate and amount by group) and Unit Costs.
    /// The amounts are formulas on the rates, so a rate changed in Excel follows through.
    /// </summary>
    public static void WriteCosting(Project p, IReadOnlyList<Berth> berths, string path)
    {
        var book = new Book();
        int St(Action<Style> set) { var s = new Style { Font = "Arial", Size = 10, VAlign = "center" }; set(s); return book.S(s); }
        var title = St(s => { s.Bold = true; s.Size = 14; });
        var head = St(s => { s.Bold = true; s.Shade = true; s.Box = true; s.Wrap = true; s.Align = "center"; });
        var cell = St(s => { s.Box = true; });
        var wrap = St(s => { s.Box = true; s.Wrap = true; });
        var num = St(s => { s.Box = true; s.Format = "#,##0.00"; });
        var money = St(s => { s.Box = true; s.Format = "#,##0"; });
        var pct = St(s => { s.Box = true; s.Format = "0%"; });
        var bold = St(s => { s.Box = true; s.Bold = true; });
        var boldMoney = St(s => { s.Box = true; s.Bold = true; s.Format = "#,##0"; });

        // Unit costs first in the data (the berth sheets point at them); shown last.
        var rates = new Sheet { Name = "Unit Costs", Widths = [12, 46, 8, 14], TitleRows = 3 };
        Book.Put(rates, 1, 0, "Unit costs", title);
        string[] rh = ["Code", "Item", "Unit", "Rate (" + p.Currency + ")"];
        for (var c = 0; c < rh.Length; c++) Book.Put(rates, 3, c, rh[c], head);
        var rateRow = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var rr = 4;
        foreach (var r in p.Rates)
        {
            Book.Put(rates, rr, 0, r.Code, cell);
            Book.Put(rates, rr, 1, r.Name, wrap);
            Book.Put(rates, rr, 2, r.Unit, cell);
            Book.Put(rates, rr, 3, r.Value is { } v ? v : "", num);
            rateRow[r.Code] = rr++;
        }

        var summary = new Sheet { Name = "Summary", Widths = [26, 12, 12, 18, 16, 18, 14, 12], TitleRows = 3, Landscape = true };
        book.Sheets.Add(summary);
        Book.Put(summary, 1, 0, "Berth costing" + (p.Details.Works.Length > 0 ? " · " + p.Details.Works : ""), title);
        string[] sh = ["Berth", "Site", "Quay length (m)", "Structural works (C1-C9)", "Preliminaries & demolition", "Total", p.Currency + " / lm (structural)", "Complete"];
        for (var c = 0; c < sh.Length; c++) Book.Put(summary, 3, c, sh[c], head);
        var srow = 4;
        foreach (var b in berths)
        {
            var name = SheetName(b, book.Sheets.Select(s => s.Name));
            var bs = new Sheet { Name = name, Widths = [8, 6, 46, 12, 8, 14, 16, 12], TitleRows = 4, Landscape = true };
            book.Sheets.Add(bs);
            Book.Put(bs, 1, 0, b.Title + (b.Site.Length > 0 ? " · " + b.Site : ""), title);
            Book.Put(bs, 2, 0, $"Quay length {(b.Length is { } l ? F(l, 1) + " m" : "not set")}" + (b.Exclude.Trim().Length > 0 ? $" · excludes {b.Exclude.Trim()}" : ""), cell);
            string[] bh = ["Group", "No", "Item", "Quantity", "Unit", "Rate", "Amount", "From"];
            for (var c = 0; c < bh.Length; c++) Book.Put(bs, 4, c, bh[c], head);
            var r = 5;
            var groupRows = new List<(string Code, int Row)>();
            foreach (var (code, gname) in Groups)
            {
                var lines = Costs(p, b).Where(x => x.Item.Group == code && (x.Qty is > 0 || x.Item.Rule.Length > 0)).ToList();
                if (lines.Count == 0) continue;
                var first = r;
                foreach (var line in lines)
                {
                    Book.Put(bs, r, 0, code, cell);
                    Book.Put(bs, r, 1, line.Item.No, cell);
                    Book.Put(bs, r, 2, line.Item.Name, wrap);
                    Book.Put(bs, r, 3, line.Qty is { } lq ? Math.Round(lq, 2) : "", num);
                    Book.Put(bs, r, 4, line.Item.Unit, cell);
                    // A rate of the list points at Unit Costs (changed there, it follows); a rebuilt pile rate is its value.
                    if (!line.Item.RateCode.StartsWith("pile:", StringComparison.Ordinal) && rateRow.TryGetValue(line.Item.RateCode, out var at))
                        Book.Put(bs, r, 5, new Formula($"'Unit Costs'!D{at}"), num);
                    else Book.Put(bs, r, 5, Math.Round(line.Rate, 2), num);
                    Book.Put(bs, r, 6, new Formula($"IF(D{r}=\"\",0,D{r}*N(F{r}))"), money);
                    Book.Put(bs, r, 7, line.From switch { Source.Model => "model", Source.Hand => "by hand", Source.Worked => "worked out", _ => "" }, cell);
                    r++;
                }
                Book.Put(bs, r, 2, $"{code} {gname}", bold);
                Book.Put(bs, r, 6, new Formula($"SUM(G{first}:G{r - 1})"), boldMoney);
                groupRows.Add((code, r));
                r += 2;
            }
            var structural = string.Join("+", groupRows.Where(g => g.Code != "C10").Select(g => $"G{g.Row}"));
            var prelims = string.Join("+", groupRows.Where(g => g.Code == "C10").Select(g => $"G{g.Row}"));
            Book.Put(bs, r, 2, "Structural works (C1-C9)", bold);
            Book.Put(bs, r, 6, new Formula(structural.Length > 0 ? structural : "0"), boldMoney);
            var structuralAt = r++;
            Book.Put(bs, r, 2, "Preliminaries & demolition (C10)", bold);
            Book.Put(bs, r, 6, new Formula(prelims.Length > 0 ? prelims : "0"), boldMoney);
            var prelimsAt = r++;
            Book.Put(bs, r, 2, $"{p.Currency} per linear metre (structural)", bold);
            Book.Put(bs, r, 6, b.Length is > 0 ? new Formula($"G{structuralAt}/{b.Length.Value.ToString("R", CultureInfo.InvariantCulture)}") : "", boldMoney);

            var q = "'" + name.Replace("'", "''") + "'";
            Book.Put(summary, srow, 0, b.Title, cell);
            Book.Put(summary, srow, 1, b.Site, cell);
            Book.Put(summary, srow, 2, b.Length is { } len ? len : "", num);
            Book.Put(summary, srow, 3, new Formula($"{q}!G{structuralAt}"), money);
            Book.Put(summary, srow, 4, new Formula($"{q}!G{prelimsAt}"), money);
            Book.Put(summary, srow, 5, new Formula($"D{srow}+E{srow}"), money);
            Book.Put(summary, srow, 6, new Formula($"IF(N(C{srow})>0,D{srow}/C{srow},\"\")"), money);
            Book.Put(summary, srow, 7, Completeness(p, b) is { } done ? done / 100 : "", pct);
            srow++;
        }
        if (berths.Count > 1)
        {
            Book.Put(summary, srow, 0, "All berths", bold);
            Book.Put(summary, srow, 2, new Formula($"SUM(C4:C{srow - 1})"), boldMoney);
            foreach (var c in new[] { 3, 4, 5 })
                Book.Put(summary, srow, c, new Formula($"SUM({OfficeXlsx.Column(c)}4:{OfficeXlsx.Column(c)}{srow - 1})"), boldMoney);
            Book.Put(summary, srow, 6, new Formula($"IF(N(C{srow})>0,D{srow}/C{srow},\"\")"), boldMoney);
        }
        book.Sheets.Add(rates);
        book.Save(path, "Berth costing");
    }

    private static string SheetName(Berth b, IEnumerable<string> taken)
    {
        var name = new string(b.Title.Where(ch => "[]:*?/\\'".IndexOf(ch) < 0).ToArray()).Trim();
        if (name.Length > 31) name = name.Substring(0, 31);
        var unique = name;
        for (var i = 2; taken.Contains(unique, StringComparer.OrdinalIgnoreCase); i++) unique = name.Substring(0, Math.Min(name.Length, 27)) + " " + i;
        return unique;
    }

    // =====================================================================================================================
    // The Word back-up calculation
    // =====================================================================================================================

    private const string Fill = "FBE2D5", CalcFill = "DAE9F8", TotalFill = "DAF2D0";

    /// <summary>A table kind's heading, its Calculation row and its column widths (twips).</summary>
    internal static (string[] Head, string[]? Calc, int[] Widths) TableOf(string kind, double steel) => kind switch
    {
        "core" => (["Item", "Pile Diameter (m)", "Area (m²)", "Top Level (mCD)", "Bottom Level (mCD)", "Height (m)", "Count", "Volume (m³)"],
            ["Calculation", "—", "π/4 × (Diameter)²", "", "", "Top Level - Bottom Level", "—", "Area x Height x Count"], [1710, 1072, 1260, 1080, 1170, 1260, 810, 2340]),
        "tube" => (["Item", "Pile Diameter (m)", "Inner Diameter (m)", "Area (m²)", "Top Level (mCD)", "Bottom Level (mCD)", "Height (m)", "Count", "Volume (m³)", "Weight (Ton)"],
            ["Calculation", "—", "—", "π/4 × (Outer Diameter)² - π/4 × (Inner Diameter)²", "", "", "Top Level - Bottom Level", "—", "Area x Height x Count", "Volume x " + F(steel, 2)],
            [1710, 1080, 1072, 1538, 816, 1074, 1040, 794, 939, 895]),
        "pile" => (["Item", "Pile Diameter (m)", "Top Level (mCD)", "Bottom Level (mCD)", "Height (m)", "Count", "Quantity (m)"],
            ["Calculation", "—", "", "", "Top Level - Bottom Level", "—", "Height x Count"], [1615, 1072, 951, 953, 1164, 1080, 1710]),
        "slab" => (["Item", "Area (m²)", "Thickness (m)", "Volume (m³)"], ["Calculation", "—", "", "Area x Thickness"], [1795, 923, 1195, 1117]),
        "rebar" => (["Item", "Rate (kg/m³)", "Volume (m³)", "Reinforcement Weight (Ton)"], null, [2155, 883, 939, 1617]),
        _ => ([], null, []),
    };

    private static string Run(string text, int halfPoints = 20, bool bold = false, bool underline = false)
    {
        if (text.Length == 0) return "";
        var props = "<w:rPr><w:rFonts w:ascii=\"Arial\" w:hAnsi=\"Arial\" w:cs=\"Arial\"/>" + (bold ? "<w:b/><w:bCs/>" : "") + (underline ? "<w:u w:val=\"single\"/>" : "") +
            $"<w:sz w:val=\"{halfPoints}\"/><w:szCs w:val=\"{halfPoints}\"/></w:rPr>";
        var parts = text.Split('\t');
        var sb = new StringBuilder();
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0) sb.Append("<w:r>" + props + "<w:tab/></w:r>");
            if (parts[i].Length > 0) sb.Append("<w:r>" + props + $"<w:t xml:space=\"preserve\">{Esc(parts[i])}</w:t></w:r>");
        }
        return sb.ToString();
    }

    private static string Para(string text, int size = 20, bool bold = false, bool underline = false, string align = "", int numId = 0, string spacing = "", bool tabs = false) =>
        "<w:p><w:pPr>" + (numId > 0 ? $"<w:pStyle w:val=\"ListParagraph\"/><w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"{numId}\"/></w:numPr>" : "") +
        (tabs ? "<w:tabs><w:tab w:val=\"left\" w:pos=\"4320\"/></w:tabs>" : "") + spacing + (align.Length > 0 ? $"<w:jc w:val=\"{align}\"/>" : "") + "</w:pPr>" +
        Run(text, size, bold, underline) + "</w:p>";

    private static string TableXml(string kind, IReadOnlyList<string[]> rows, string[]? total, double steel)
    {
        var (head, calc, widths) = TableOf(kind, steel);
        var width = widths.Sum();
        var sb = new StringBuilder($"<w:tbl><w:tblPr><w:tblW w:w=\"{width}\" w:type=\"dxa\"/><w:tblInd w:w=\"{(width > 8300 ? -(width - 8300) / 2 : 0)}\" w:type=\"dxa\"/>" +
            "<w:tblBorders><w:top w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"auto\"/><w:left w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"auto\"/>" +
            "<w:bottom w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"auto\"/><w:right w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"auto\"/>" +
            "<w:insideH w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"auto\"/><w:insideV w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"auto\"/></w:tblBorders>" +
            "<w:tblLayout w:type=\"fixed\"/><w:tblLook w:val=\"04A0\"/></w:tblPr><w:tblGrid>" + string.Concat(widths.Select(w => $"<w:gridCol w:w=\"{w}\"/>")) + "</w:tblGrid>");
        void Row(string[] cells, string fill, bool bold, int height, bool header)
        {
            sb.Append($"<w:tr><w:trPr>{(header ? "<w:tblHeader/>" : "")}<w:trHeight w:val=\"{height}\"/></w:trPr>");
            for (var i = 0; i < widths.Length; i++)
            {
                var t = i < cells.Length ? cells[i] : "";
                sb.Append($"<w:tc><w:tcPr><w:tcW w:w=\"{widths[i]}\" w:type=\"dxa\"/>" + (fill.Length > 0 ? $"<w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"{fill}\"/>" : "") +
                    "<w:vAlign w:val=\"center\"/></w:tcPr><w:p><w:pPr><w:spacing w:after=\"0\" w:line=\"240\" w:lineRule=\"auto\"/>" +
                    (i > 0 || header ? "<w:jc w:val=\"center\"/>" : "") + "</w:pPr>" + Run(t, 20, bold) + "</w:p></w:tc>");
            }
            sb.Append("</w:tr>");
        }
        Row(head, Fill, true, 792, true);
        if (calc is not null) Row(calc, CalcFill, false, 792, false);
        foreach (var r in rows) Row(r, "", false, 288, false);
        if (total is not null) Row(total, TotalFill, true, 288, false);
        sb.Append("</w:tbl>");
        return sb.ToString();
    }

    /// <summary>
    /// The Word back-up calculation of the berths in the base format: title, introduction, the note on what each berth
    /// leaves out, material properties and wall lengths, a lettered section per ticked item with a table per berth, and
    /// the reinforcement section. Returns notes.
    /// </summary>
    public static List<string> WriteBackup(Project p, IReadOnlyList<Berth> berths, string path, bool example = false)
    {
        var notes = new List<string>();
        var body = new StringBuilder();
        var list = string.Join(" and ", berths.Select(b => b.Title));
        body.Append(Para("BILL OF QUANTITIES BACKUP CALCULATION", 32, align: "center"));
        body.Append(Para($"This document provides the backup calculation supporting every Structural Item quantity in the Bill of Quantities for {list}. " +
            "For each element it shows how the billed quantity is obtained — quantity = geometry × count, or volume × unit weight, as appropriate — so that every figure reconciles with the issued Bill."));
        var excluded = berths.Where(b => b.Exclude.Trim().Length > 0).ToList();
        if (excluded.Count > 0)
        {
            body.Append(Para("Note:"));
            foreach (var b in excluded)
                body.Append(Para($"{b.Title}: the count of elements does not contain the elements in {b.Exclude.Trim()}" +
                    (b.ExcludeNote.Trim().Length > 0 ? $" ({b.ExcludeNote.Trim()})" : "") + "; the count of these elements will be provided within its own submission.", underline: true));
        }
        body.Append(Para("Material Properties & Basis", 22, underline: true));
        body.Append(Para($"Steel unit weight (density)\t{F(p.SteelDensity, 3)} t/m³", tabs: true));
        body.Append(Para($"Reinforced concrete unit weight\t{F(p.ConcreteDensity, 2)} t/m³", tabs: true));
        foreach (var b in berths)
            body.Append(Para($"{b.Title} Wall Length\t{(b.Length is > 0 ? Qty(b.Length) + " m" : "not set")}" + (b.Exclude.Trim().Length > 0 ? $" (excluding {b.Exclude.Trim()})" : ""), tabs: true));
        body.Append(Para(""));

        var kinds = new HashSet<string>();
        string? heading = null;
        foreach (var item in p.Items.Where(i => i.InFiles && i.Backup.Trim().Length > 0).OrderBy(i => i.BackupOrder).ThenBy(i => i.No))
        {
            var kind = ParseRuleSafe(item.Rule);
            if (kind.Length == 0) continue;
            var follows = item.Backup.StartsWith("+", StringComparison.Ordinal);
            var withLines = berths.Where(b => (b.Measured?.Lines ?? []).Any(l => l.No == item.No)).ToList();
            if (withLines.Count == 0 && !example)
            {
                notes.Add($"No {item.Name} measured, no section.");
                if (!follows) heading = item.Backup.Trim();
                continue;
            }
            // A "+" part goes under the section before it; when that section had nothing, its heading still comes first.
            if (!follows) body.Append(Para(item.Backup.Trim(), 28, bold: true, underline: true, numId: 2));
            else if (heading is { Length: > 0 }) body.Append(Para(heading, 28, bold: true, underline: true, numId: 2));
            heading = null;
            if (item.BackupNote.Trim().Length > 0)
                body.Append(Para(item.BackupNote.Trim(), spacing: "<w:spacing w:after=\"120\" w:line=\"276\" w:lineRule=\"auto\"/>", align: "both"));
            foreach (var b in berths)
            {
                var lines = (b.Measured?.Lines ?? []).Where(l => l.No == item.No).ToList();
                if (lines.Count == 0 && !example) continue;
                var label = b.Number.Length > 0 ? "Berth " + b.Number : b.Name;
                body.Append(Para(follows ? $"{label} – {item.Backup.Substring(1).Trim()}" : label));
                var (rows, total) = BerthCostExports.Rows(kind, lines);
                body.Append(TableXml(kind, rows, total, p.SteelDensity));
                kinds.Add(kind);
                body.Append(Para(""));
                if (b.Hand.TryGetValue(item.No, out var hand))
                    body.Append(Para($"Billed quantity {Qty(hand)} {item.Unit} (set by hand; the model gives {Qty(lines.Sum(l => l.Quantity))})."));
            }
        }
        var rebarItems = p.ItemOf(SuperRebar) is not { InFiles: false };
        var names = berths.SelectMany(b => Rebar(p, b)).Select(r => r.Name).Distinct().ToList();
        if (rebarItems && (names.Count > 0 || example))
        {
            body.Append(Para("Reinforcement", 28, bold: true, underline: true, numId: 2));
            body.Append(Para("Reinforcement is billed by weight at a rate per cubic metre of reinforced concrete. The billed tonnage corresponds to the following volume of reinforced concrete:",
                spacing: "<w:spacing w:after=\"120\" w:line=\"276\" w:lineRule=\"auto\"/>", align: "both"));
            foreach (var n in names) body.Append(Para(n, numId: 1));
            foreach (var b in berths)
            {
                var rebar = Rebar(p, b);
                if (rebar.Count == 0 && !example) continue;
                body.Append(Para(b.Number.Length > 0 ? "Berth " + b.Number : b.Name));
                var vol = rebar.Sum(r => r.Volume);
                var wt = rebar.Sum(r => r.Weight);
                body.Append(TableXml("rebar", rebar.Select(r => new[] { r.Name, F(r.Kg, 0), F(r.Volume, 0), F(r.Weight, 0) }).ToList(),
                    ["Total", F(vol > 0 ? wt / vol * 1000 : 0, 0), F(vol, 0), F(wt, 0)], p.SteelDensity));
                kinds.Add("rebar");
                body.Append(Para(""));
            }
        }
        // A template has one table of each kind, so it can be edited and imported back for any item.
        if (example)
            foreach (var kind in new[] { "core", "tube", "pile", "slab" }.Where(k => !kinds.Contains(k)))
            {
                body.Append(Para("Example " + kind + " table", 28, bold: true, underline: true, numId: 2));
                body.Append(Para("Berth 1"));
                body.Append(TableXml(kind, [], ["Total"], p.SteelDensity));
                body.Append(Para(""));
            }
        body.Append("<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\" w:code=\"9\"/><w:pgMar w:top=\"1440\" w:right=\"1800\" w:bottom=\"1440\" w:left=\"1800\" w:header=\"720\" w:footer=\"720\" w:gutter=\"0\"/></w:sectPr>");
        WriteDocx(path, body.ToString(), "Bill of quantities backup calculation");
        return notes;
    }

    private static string ParseRuleSafe(string rule)
    {
        try { return ParseRule(rule).Select(r => r.Table).FirstOrDefault(t => t.Length > 0 && t != "none") ?? ""; }
        catch (FormatException) { return ""; }
    }

    private static void WriteDocx(string path, string body, string title)
    {
        const string W = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";
        OfficeDocs.WriteFile(path, s =>
        {
            using var zip = new OfficeDocs.Package(s);
            zip.Text("[Content_Types].xml", OfficeDocs.XmlHead +
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
                "<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/>" +
                "<Override PartName=\"/word/numbering.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml\"/>" +
                "<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/></Types>");
            zip.Text("_rels/.rels", OfficeDocs.XmlHead +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
                "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/></Relationships>");
            zip.Text("docProps/core.xml", OfficeDocs.Core(title, DateTime.Now));
            zip.Text("word/_rels/document.xml.rels", OfficeDocs.XmlHead +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
                "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/numbering\" Target=\"numbering.xml\"/></Relationships>");
            zip.Text("word/styles.xml", OfficeDocs.XmlHead + $"<w:styles {W}>" +
                "<w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii=\"Arial\" w:hAnsi=\"Arial\" w:cs=\"Arial\"/><w:sz w:val=\"20\"/><w:szCs w:val=\"20\"/></w:rPr></w:rPrDefault>" +
                "<w:pPrDefault><w:pPr><w:spacing w:after=\"160\" w:line=\"259\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault></w:docDefaults>" +
                "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/></w:style>" +
                "<w:style w:type=\"paragraph\" w:styleId=\"ListParagraph\"><w:name w:val=\"List Paragraph\"/><w:basedOn w:val=\"Normal\"/><w:pPr><w:ind w:left=\"720\"/><w:contextualSpacing/></w:pPr></w:style>" +
                "<w:style w:type=\"table\" w:default=\"1\" w:styleId=\"TableNormal\"><w:name w:val=\"Normal Table\"/><w:tblPr><w:tblCellMar><w:left w:w=\"108\" w:type=\"dxa\"/><w:right w:w=\"108\" w:type=\"dxa\"/></w:tblCellMar></w:tblPr></w:style>" +
                "</w:styles>");
            zip.Text("word/numbering.xml", OfficeDocs.XmlHead + $"<w:numbering {W}>" +
                "<w:abstractNum w:abstractNumId=\"0\"><w:multiLevelType w:val=\"hybridMultilevel\"/><w:lvl w:ilvl=\"0\"><w:start w:val=\"1\"/><w:numFmt w:val=\"bullet\"/><w:lvlText w:val=\"•\"/><w:lvlJc w:val=\"left\"/><w:pPr><w:ind w:left=\"720\" w:hanging=\"360\"/></w:pPr></w:lvl></w:abstractNum>" +
                "<w:abstractNum w:abstractNumId=\"1\"><w:multiLevelType w:val=\"hybridMultilevel\"/><w:lvl w:ilvl=\"0\"><w:start w:val=\"1\"/><w:numFmt w:val=\"upperLetter\"/><w:lvlText w:val=\"%1.\"/><w:lvlJc w:val=\"left\"/><w:pPr><w:ind w:left=\"720\" w:hanging=\"360\"/></w:pPr></w:lvl></w:abstractNum>" +
                "<w:num w:numId=\"1\"><w:abstractNumId w:val=\"0\"/></w:num><w:num w:numId=\"2\"><w:abstractNumId w:val=\"1\"/></w:num></w:numbering>");
            zip.Text("word/document.xml", OfficeDocs.XmlHead + $"<w:document {W}><w:body>{body}</w:body></w:document>");
        });
    }
}
