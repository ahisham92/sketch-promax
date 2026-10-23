using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using static SketchProMax.Tools.BerthCostMath;

namespace SketchProMax.Tools;

/// <summary>
/// The three files of a berth costing, each made from the user's own file so it comes out in exactly that format:
/// the cost comparison workbook (its Quantities columns and Unit Costs filled; every other sheet follows by its own
/// formulas), the BOQ Excel (quantities and the numbers in the descriptions of a berth's bill), and the Word back-up
/// calculation (sections, tables and notes cloned from the template's own paragraphs and tables). No Revit here.
/// </summary>
internal static class BerthCostExports
{
    private static string F(double v, int dp) => v.ToString(dp == 0 ? "0" : "0." + new string('0', dp), CultureInfo.InvariantCulture);

    // =====================================================================================================================
    // The comparison workbook
    // =====================================================================================================================

    /// <summary>Where things are on the comparison workbook's Quantities sheet, found by their labels.</summary>
    internal sealed class Layout
    {
        public const string Sheet = "Quantities";
        public int Stage, Site, Name, Source, Length, Remarks, BillNo;
        public int FirstCol = 5, LastCol = -1;
        public Dictionary<string, int> Params = new(StringComparer.Ordinal);
        public Dictionary<int, int> Items = [];
        public int RebarExclPiles, RebarPiles;

        public static Layout Of(XlsxBook book)
        {
            if (!book.HasSheet(Sheet)) throw new InvalidDataException("it has no Quantities sheet (pick the cost comparison workbook).");
            var l = new Layout();
            var cells = book.Cells(Sheet).ToList();
            int Row(string label) => cells.FirstOrDefault(c => c.Col == 1 && string.Equals(c.Text.Trim(), label, StringComparison.OrdinalIgnoreCase)).Row;
            l.BillNo = Row("Bill No."); l.Stage = Row("Stage"); l.Site = Row("Site"); l.Name = Row("Bill / Berth");
            l.Source = Row("Source document"); l.Length = Row("Quay length (m)"); l.Remarks = Row("Remarks");
            if (l.Stage == 0 || l.Site == 0 || l.Name == 0 || l.Length == 0) throw new InvalidDataException("its Quantities sheet has no Stage, Site, Bill / Berth and Quay length (m) rows in column B.");
            foreach (var (key, label, _) in BerthCostMath.Params) if (Row(label) is > 0 and var r) l.Params[key] = r;
            foreach (var c in cells.Where(c => c.Col == 0 && c.Row > l.Remarks))
                if (int.TryParse(c.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var no) && !l.Items.ContainsKey(no)) l.Items[no] = c.Row;
            l.RebarExclPiles = Row("Bill-stated reinforcement excl. piles");
            l.RebarPiles = Row("Bill-stated pile reinforcement");
            var header = l.BillNo > 0 ? l.BillNo : l.Stage;
            var end = cells.FirstOrDefault(c => c.Row == header && string.Equals(c.Text.Trim(), "END", StringComparison.OrdinalIgnoreCase));
            l.LastCol = end.Text is null ? cells.Where(c => c.Row == header || c.Row == l.Name).Select(c => c.Col).DefaultIfEmpty(l.FirstCol).Max() : end.Col - 1;
            var first = cells.Where(c => c.Row == l.Name && c.Col >= 2 && c.Text.Trim().Length > 0).Select(c => c.Col).DefaultIfEmpty(5).Min();
            l.FirstCol = Math.Max(2, first);
            return l;
        }

        /// <summary>The column of a berth (same site and stage, same number), else the first spare one; -1 when none is left.</summary>
        public int ColumnFor(XlsxBook book, Berth b, string stage, out bool isNew)
        {
            isNew = false;
            for (var c = FirstCol; c <= LastCol; c++)
                if (Same(book.Text(Sheet, c, Site), b.Site) && Same(book.Text(Sheet, c, Stage), stage) && NumberOf(book.Text(Sheet, c, Name)) is { Length: > 0 } n && n == b.Number) return c;
            isNew = true;
            for (var c = FirstCol; c <= LastCol; c++)
            {
                var name = book.Text(Sheet, c, Name).Trim();
                if ((name.Length == 0 || name.StartsWith("Spare", StringComparison.OrdinalIgnoreCase)) && book.Text(Sheet, c, Stage).Trim().Length == 0) return c;
            }
            return -1;
        }

        /// <summary>The newest column of the stage on the site (to start a new berth's parameters from).</summary>
        public int LastOf(XlsxBook book, string site, string stage)
        {
            for (var c = LastCol; c >= FirstCol; c--)
                if (Same(book.Text(Sheet, c, Site), site) && Same(book.Text(Sheet, c, Stage), stage)) return c;
            return -1;
        }
    }

    private static bool Same(string a, string b) => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    private static string NumberOf(string name) => Regex.Match(name ?? "", @"\d+") is { Success: true } m ? m.Value.TrimStart('0') is { Length: > 0 } n ? n : "0" : "";

    /// <summary>
    /// Takes the unit costs, the pile rate basis and, for berths not set yet, the quay length and design parameters from
    /// the comparison workbook (the berth's own column, else the site's newest one). Returns what was taken, in words.
    /// </summary>
    public static string Import(Project p, string path, IEnumerable<Berth> berths)
    {
        using var book = XlsxBook.Read(path);
        var names = book.Names();
        var rates = 0;
        foreach (var named in names)
        {
            var (name, at) = (named.Key, named.Value);
            if (!name.StartsWith("R_", StringComparison.OrdinalIgnoreCase) || !book.HasSheet(at.Sheet)) continue;
            if (book.Number(at.Sheet, at.Col, at.Row) is not { } v) continue;
            var rate = p.RateOf(name);
            if (rate is null)
            {
                // A unit cost the defaults don't have: its name and unit from the cells before it (Item, Unit, Rate).
                rate = new Rate { Code = name, Name = at.Col >= 2 ? book.Text(at.Sheet, at.Col - 2, at.Row) : name, Unit = at.Col >= 1 ? book.Text(at.Sheet, at.Col - 1, at.Row) : "" };
                if (rate.Name.Trim().Length == 0) continue;
                p.Rates.Add(rate);
            }
            rate.Value = v;
            rates++;
        }
        double? Named(string n) => names.TryGetValue(n, out var at) && book.HasSheet(at.Sheet) ? book.Number(at.Sheet, at.Col, at.Row) : null;
        if (Named("PILE_REF_D") is > 0 and var d) p.Pile.RefDiameter = d;
        if (Named("PILE_REF_KG") is > 0 and var kg) p.Pile.RefKg = kg;
        if (names.TryGetValue("PILE_SCALING", out var sc) && book.HasSheet(sc.Sheet)) p.Pile.ByVolume = !book.Text(sc.Sheet, sc.Col, sc.Row).Trim().StartsWith("Diam", StringComparison.OrdinalIgnoreCase);

        var filled = new List<string>();
        if (book.HasSheet(Layout.Sheet))
        {
            var l = Layout.Of(book);
            foreach (var b in berths)
            {
                var col = l.ColumnFor(book, b, p.Stage, out var isNew);
                if (isNew) col = l.LastOf(book, b.Site, p.Stage);
                if (col < 0) continue;
                var any = false;
                if (!isNew && b.Length is null && book.Number(Layout.Sheet, col, l.Length) is > 0 and var len) { b.Length = len; any = true; }
                foreach (var kv in l.Params)
                    if (!b.Params.ContainsKey(kv.Key) && book.Number(Layout.Sheet, col, kv.Value) is { } v) { b.Params[kv.Key] = v; any = true; }
                if (!isNew && b.ExcludeNote.Trim().Length == 0 && Regex.Match(book.Text(Layout.Sheet, col, l.Name), @"\(([^)]+)\)") is { Success: true } m) { b.ExcludeNote = m.Groups[1].Value.Trim(); any = true; }
                if (any) filled.Add(b.Name);
            }
        }
        return $"{rates} unit costs" + (filled.Count > 0 ? $"; quay length and design parameters for {string.Join(", ", filled)}" : "");
    }

    /// <summary>
    /// The comparison workbook with the berths in it: each in its own column on Quantities (found by site, stage and
    /// number; a new berth takes the first spare column) with its stage, site, name, BOQ, quay length, remarks, design
    /// parameters and quantities, and the Unit Costs (and pile rate basis) as set in Sketch ProMax. Returns notes.
    /// </summary>
    public static List<string> WriteComparison(Project p, IReadOnlyList<Berth> berths, string template, string output, Func<Berth, string> source)
    {
        var notes = new List<string>();
        using var book = XlsxBook.Copy(template, output);
        var l = Layout.Of(book);
        foreach (var b in berths)
        {
            var col = l.ColumnFor(book, b, p.Stage, out var isNew);
            if (col < 0) { notes.Add($"{b.Title}: no spare column left on Quantities (insert columns before END, then make it again)."); continue; }
            const string S = Layout.Sheet;
            book.SetText(S, col, l.Stage, p.Stage);
            book.SetText(S, col, l.Site, b.Site);
            book.SetText(S, col, l.Name, b.Title);
            if (l.Source > 0) book.SetText(S, col, l.Source, source(b));
            if (b.Length is > 0) book.SetNumber(S, col, l.Length, b.Length.Value); else notes.Add($"{b.Title}: no quay length, so no US$/lm.");
            if (l.Remarks > 0 && b.ExcludeNote.Trim().Length > 0) book.SetText(S, col, l.Remarks, $"Excludes {b.Exclude.Trim()} ({b.ExcludeNote.Trim()}) - to follow");
            foreach (var kv in l.Params)
                if (b.Params.TryGetValue(kv.Key, out var v)) book.SetNumber(S, col, kv.Value, v);
            var written = 0;
            foreach (var item in p.Items)
            {
                if (item.No is InfillRebar or SuperRebar) continue; // formulas on the sheet (m³ × kg/m³)
                var (q, from) = Quantity(p, b, item.No);
                if (!l.Items.TryGetValue(item.No, out var row)) { if (q is > 0) notes.Add($"{b.Title}: {item.Name} has no row {item.No} on Quantities."); continue; }
                if (q is { } value && from is Source.Model or Source.Hand) { book.SetNumber(S, col, row, Math.Round(value, 3)); written++; }
                else if (isNew) book.Clear(S, col, row);
            }
            var rebar = Rebar(p, b);
            if (l.RebarExclPiles > 0 && rebar.Count > 0) book.SetNumber(S, col, l.RebarExclPiles, Math.Round(rebar.Where(r => r.No is not (5 or 6)).Sum(r => r.Weight)));
            if (l.RebarPiles > 0 && rebar.Any(r => r.No is 5 or 6)) book.SetNumber(S, col, l.RebarPiles, Math.Round(rebar.Where(r => r.No is 5 or 6).Sum(r => r.Weight)));
            notes.Add($"{b.Title}: column {OfficeXlsx.Column(col)}{(isNew ? " (new)" : "")}, {written} quantities.");
        }
        // Unit costs and the pile rate basis.
        var names = book.Names();
        foreach (var rate in p.Rates)
        {
            if (rate.Value is not { } v || !names.TryGetValue(rate.Code, out var at) || !book.HasSheet(at.Sheet)) continue;
            var old = book.Number(at.Sheet, at.Col, at.Row);
            // A rate the workbook takes from another (=R_PIPE) stays a formula while it is the same.
            if (old is { } o && Math.Abs(o - v) < 1e-9) continue;
            book.SetNumber(at.Sheet, at.Col, at.Row, v);
        }
        void Basis(string name, double value) { if (names.TryGetValue(name, out var at) && book.HasSheet(at.Sheet) && book.Number(at.Sheet, at.Col, at.Row) != value) book.SetNumber(at.Sheet, at.Col, at.Row, value); }
        Basis("PILE_REF_D", p.Pile.RefDiameter);
        Basis("PILE_REF_KG", p.Pile.RefKg);
        if (names.TryGetValue("PILE_SCALING", out var scaling) && book.HasSheet(scaling.Sheet)) book.SetText(scaling.Sheet, scaling.Col, scaling.Row, p.Pile.ByVolume ? "Volume" : "Diameter");
        book.Save();
        return notes;
    }

    // =====================================================================================================================
    // The BOQ
    // =====================================================================================================================

    /// <summary>Whether a BOQ description is the item's: words with ";" must all be in it, "|" gives choices, "^" starts it, "=" is the whole text.</summary>
    public static bool BoqMatches(string description, string pattern)
    {
        var d = Regex.Replace(description ?? "", @"\s+", " ").Trim();
        if (d.Length == 0) return false;
        foreach (var choice in (pattern ?? "").Split('|'))
        {
            var words = choice.Split(';').Select(w => w.Trim()).Where(w => w.Length > 0).ToList();
            if (words.Count == 0) continue;
            if (words.All(w => w.StartsWith("=", StringComparison.Ordinal) ? string.Equals(d, w.Substring(1).Trim(), StringComparison.OrdinalIgnoreCase)
                    : w.StartsWith("^", StringComparison.Ordinal) ? d.StartsWith(w.Substring(1).Trim(), StringComparison.OrdinalIgnoreCase)
                    : d.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0)) return true;
        }
        return false;
    }

    /// <summary>The berth number a BOQ template is for ("BERTH 10" in its texts), or "".</summary>
    public static string TemplateBerth(IEnumerable<string> texts) =>
        texts.Select(t => Regex.Match(t ?? "", @"\bBERTH\s*\(?0*(\d+)\)?", RegexOptions.IgnoreCase)).FirstOrDefault(m => m.Success)?.Groups[1].Value ?? "";

    /// <summary>A template's text for another berth: "BERTH 10" → "BERTH 9", "Berth (10)" → "Berth (9)", "-10-" → "-09-".</summary>
    public static string ForBerth(string text, string from, string to, double? length = null)
    {
        if (from.Length == 0 || to.Length == 0 || from == to) return LengthText(text, length);
        var t = Regex.Replace(text, @"(\bBerth\s*\(?)0*" + from + @"(?!\d)", m => m.Groups[1].Value + (Regex.IsMatch(m.Value, @"\(?0\d") ? to.PadLeft(2, '0') : to), RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"-0*" + from + @"-", "-" + to.PadLeft(2, '0') + "-");
        return LengthText(t, length);
    }

    private static string LengthText(string text, double? length) =>
        length is > 0 ? Regex.Replace(text, @"(LENGTH\s+)[\d.,]+(\s+METE?RE?S?)", m => m.Groups[1].Value + F(Math.Round(length.Value, 1), length.Value % 1 == 0 ? 0 : 1) + m.Groups[2].Value, RegexOptions.IgnoreCase) : text;

    /// <summary>A BOQ description with the berth's numbers: reinforcement tonnage, element count and length, and a single tip level.</summary>
    public static string Describe(string text, Item item, IReadOnlyList<Line> lines, IReadOnlyList<RebarRow> rebar, List<string> notes, string berth)
    {
        var t = text;
        // The reinforcement items say their tonnage: the infill's, the superstructure's, or an element's own.
        var of = item.No switch
        {
            InfillRebar => rebar.Where(r => r.No == 3).ToList(),
            SuperRebar => rebar.Where(r => RebarOf.Any(x => x.No == r.No)).ToList(),
            _ => rebar.Where(r => r.No == item.No).ToList(),
        };
        if (of.Count > 0)
            t = Regex.Replace(t, @"\(\s*[\d,.]+\s*(Tons?|T|tonnes?)\s*\)", m => $"({F(Math.Round(of.Sum(r => r.Weight)), 0)} {m.Groups[1].Value})", RegexOptions.IgnoreCase);
        var mine = lines.Where(l => l.No == item.No).ToList();
        if (mine.Count > 0)
        {
            var count = mine.Sum(l => l.Count);
            var typical = mine.GroupBy(l => Math.Round(l.Height, 1)).OrderByDescending(g => g.Sum(l => l.Count)).First().Key;
            t = Regex.Replace(t, @"for\s+\d+\s+(piles|steel sheets|sheets|elements)\s+of\s+(height|length)\s+[\d.]+\s*m", m => $"for {count} {m.Groups[1].Value} of {m.Groups[2].Value} {F(typical, typical % 1 == 0 ? 0 : 1)} m", RegexOptions.IgnoreCase);
            var tips = mine.Select(l => Math.Round(l.Bottom, 2)).Distinct().ToList();
            var tipMatches = Regex.Matches(t, @"Tip Level\s*\(\s*-?[\d.]+\s*\)", RegexOptions.IgnoreCase).Count;
            if (tips.Count == 1 && tipMatches == 1)
                t = Regex.Replace(t, @"(Tip Level\s*\(\s*)-?[\d.]+(\s*\))", m => m.Groups[1].Value + F(tips[0], 2) + m.Groups[2].Value, RegexOptions.IgnoreCase);
            else if (tipMatches > 0 && tips.Count > 0)
            {
                var said = Regex.Matches(t, @"Tip Level\s*\(\s*(-?[\d.]+)\s*\)", RegexOptions.IgnoreCase).Cast<Match>().Select(m => Math.Round(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), 2)).Distinct().OrderBy(x => x);
                if (!said.SequenceEqual(tips.OrderBy(x => x)))
                    notes.Add($"{berth}: check the tip levels in \"{item.Name}\" (the model has {string.Join(", ", tips.Select(x => F(x, 2)))}).");
            }
        }
        return t;
    }

    /// <summary>
    /// The BOQ of one berth from the template: quantities into the rows the items match, the numbers in their
    /// descriptions, and the berth's number and length in its titles, footer and file name. Returns notes.
    /// </summary>
    public static List<string> WriteBoq(Project p, Berth b, string template, string output)
    {
        var notes = new List<string>();
        using var book = XlsxBook.Copy(template, output);
        var sheet = book.SheetNames.FirstOrDefault() ?? throw new InvalidDataException("the BOQ template has no sheet");
        var cells = book.Cells(sheet).ToList();
        var from = TemplateBerth(cells.Where(c => c.Col == 1).Select(c => c.Text).Concat([Path.GetFileNameWithoutExtension(template)]));
        var lines = b.Measured?.Lines ?? [];
        var rebar = Rebar(p, b);
        // Bill rows: a description in B and a unit in D (headings have none).
        var rows = cells.Where(c => c.Col == 1 && c.Text.Trim().Length > 0)
            .Select(c => (c.Row, Text: c.Text, Unit: book.Text(sheet, 3, c.Row).Trim())).Where(r => r.Unit.Length > 0 && !Same(r.Text, "Description")).ToList();
        var used = new HashSet<int>();
        foreach (var item in p.Items)
        {
            if (item.Boq.Trim().Length == 0) continue;
            var (q, _) = Quantity(p, b, item.No);
            var row = rows.FirstOrDefault(r => !used.Contains(r.Row) && BoqMatches(r.Text, item.Boq));
            if (row.Text is null) { if (q is > 0) notes.Add($"{b.Title}: {item.Name} ({Qty(q)} {item.Unit}) has no row in the BOQ template; add one."); continue; }
            used.Add(row.Row);
            if (q is { } value) book.SetNumber(sheet, 2, row.Row, Math.Round(value));
            else { book.Clear(sheet, 2, row.Row); notes.Add($"{b.Title}: {item.Name} has no quantity; its row is left empty."); }
            var text = Describe(row.Text, item, lines, rebar, notes, b.Title);
            text = ForBerth(text, from, b.Number);
            if (text != row.Text) book.SetText(sheet, 1, row.Row, text);
        }
        foreach (var r in rows.Where(r => !used.Contains(r.Row)))
            notes.Add($"{b.Title}: row {r.Row} \"{Short(r.Text)}\" isn't one of the items; it keeps the template's quantity.");
        book.ReplaceTexts(t => ForBerth(t, from, b.Number, b.Length));
        book.Save();
        return notes;
    }

    /// <summary>The BOQ file name for a berth from the template's ("BOQ-FD-Berth 10-ST" → "BOQ-FD-Berth 9-ST").</summary>
    public static string BoqName(string template, Berth b)
    {
        var name = Path.GetFileNameWithoutExtension(template);
        var from = TemplateBerth([name]);
        return from.Length > 0 && b.Number.Length > 0 ? ForBerth(name, from, b.Number) : OfficeDocs.SafeName(b.Title + " BOQ");
    }

    private static string Short(string s) { var t = Regex.Replace(s, @"\s+", " ").Trim(); return t.Length > 60 ? t.Substring(0, 57) + "…" : t; }

    // =====================================================================================================================
    // The Word back-up calculation
    // =====================================================================================================================

    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace W14 = "http://schemas.microsoft.com/office/word/2010/wordml";

    private static string PText(XElement p) => string.Concat(p.Descendants(W + "t").Select(t => t.Value));

    private static bool IsList(XElement p) => p.Element(W + "pPr")?.Element(W + "numPr") is not null;

    private static string? NumId(XElement p) => (string?)p.Element(W + "pPr")?.Element(W + "numPr")?.Element(W + "numId")?.Attribute(W + "val");

    /// <summary>A copy of a template paragraph (or table) with new text in it: the first run's formatting, tabs from "\t".</summary>
    private static XElement Para(XElement proto, string text)
    {
        var p = new XElement(proto);
        Fresh(p);
        SetText(p, text);
        return p;
    }

    /// <summary>Drops Word's paragraph ids from a copy (a document must not repeat them).</summary>
    private static void Fresh(XElement e)
    {
        foreach (var x in e.DescendantsAndSelf()) { x.SetAttributeValue(W14 + "paraId", null); x.SetAttributeValue(W14 + "textId", null); }
    }

    private static void SetText(XElement p, string text)
    {
        var rPr = p.Elements(W + "r").Select(r => r.Element(W + "rPr")).FirstOrDefault(x => x is not null) ?? p.Element(W + "pPr")?.Element(W + "rPr");
        var keep = p.Element(W + "pPr");
        p.Nodes().Remove();
        if (keep is not null) p.Add(keep);
        if (text.Length == 0) return;
        var run = new XElement(W + "r");
        if (rPr is not null) run.Add(new XElement(W + "rPr", rPr.Elements()));
        var parts = text.Split('\t');
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0) run.Add(new XElement(W + "tab"));
            if (parts[i].Length > 0) run.Add(new XElement(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), parts[i]));
        }
        p.Add(run);
    }

    private static void SetCell(XElement tc, string text)
    {
        var ps = tc.Elements(W + "p").ToList();
        if (ps.Count == 0) { tc.Add(new XElement(W + "p")); ps = tc.Elements(W + "p").ToList(); }
        foreach (var extra in ps.Skip(1)) extra.Remove();
        SetText(ps[0], text);
    }

    /// <summary>The template's paragraphs and tables the document is built from.</summary>
    internal sealed class Protos
    {
        public XElement Title = null!, Body = null!, Plain = null!, Heading = null!, Label = null!, Empty = null!;
        public XElement? Note, NoteLabel, Basis, Underline, ListItem;
        public string IntroText = "";
        public Dictionary<string, XElement> Tables = new(StringComparer.Ordinal);
        public XElement? SectPr;

        public static Protos Of(XElement body)
        {
            var ps = body.Elements(W + "p").ToList();
            if (ps.Count < 3) throw new InvalidDataException("the back-up calculation template has too little in it to copy its format.");
            var pr = new Protos { Title = ps[0] };
            var intro = ps.FirstOrDefault(p => PText(p).Length > 60 && !IsList(p)) ?? ps[1];
            pr.Body = intro; pr.IntroText = PText(intro);
            pr.NoteLabel = ps.FirstOrDefault(p => PText(p).Trim().StartsWith("Note", StringComparison.OrdinalIgnoreCase));
            pr.Note = pr.NoteLabel?.ElementsAfterSelf(W + "p").FirstOrDefault(p => PText(p).Trim().Length > 0);
            pr.Underline = ps.FirstOrDefault(p => PText(p).IndexOf("Basis", StringComparison.OrdinalIgnoreCase) >= 0 && !IsList(p));
            pr.Basis = ps.FirstOrDefault(p => p.Descendants(W + "tab").Any() && PText(p).Trim().Length > 0);
            pr.Heading = ps.FirstOrDefault(IsList) ?? pr.Body;
            var headingNum = NumId(pr.Heading);
            pr.ListItem = ps.FirstOrDefault(p => IsList(p) && NumId(p) != headingNum);
            var table = body.Elements(W + "tbl").FirstOrDefault();
            pr.Label = table?.ElementsBeforeSelf(W + "p").LastOrDefault(p => PText(p).Trim().Length > 0) ?? pr.Body;
            pr.Plain = pr.Heading.ElementsAfterSelf(W + "p").FirstOrDefault(p => PText(p).Trim().Length > 0 && !IsList(p)) ?? pr.Body;
            pr.Empty = ps.FirstOrDefault(p => PText(p).Trim().Length == 0 && !IsList(p)) ?? new XElement(W + "p");
            foreach (var t in body.Elements(W + "tbl"))
            {
                var kind = Kind(t);
                if (kind.Length > 0 && !pr.Tables.ContainsKey(kind)) pr.Tables[kind] = t;
            }
            pr.SectPr = body.Element(W + "sectPr");
            return pr;
        }
    }

    /// <summary>A template table's kind from its first row: core, tube, pile, slab or rebar.</summary>
    internal static string Kind(XElement table)
    {
        var head = string.Join("|", table.Elements(W + "tr").FirstOrDefault()?.Elements(W + "tc").Select(c => string.Concat(c.Descendants(W + "t").Select(t => t.Value)).Trim()) ?? []);
        if (head.IndexOf("Reinforcement Weight", StringComparison.OrdinalIgnoreCase) >= 0 || head.IndexOf("Rate (kg", StringComparison.OrdinalIgnoreCase) >= 0) return "rebar";
        if (head.IndexOf("Inner Diameter", StringComparison.OrdinalIgnoreCase) >= 0) return "tube";
        if (head.IndexOf("Quantity (m)", StringComparison.OrdinalIgnoreCase) >= 0) return "pile";
        if (head.IndexOf("Pile Diameter", StringComparison.OrdinalIgnoreCase) >= 0) return "core";
        if (head.IndexOf("Thickness", StringComparison.OrdinalIgnoreCase) >= 0) return "slab";
        return "";
    }

    /// <summary>A table like the template's: its heading rows, a row per entry from its first data row, and its Total row.</summary>
    internal static XElement Table(XElement proto, IReadOnlyList<string[]> rows, string[]? total)
    {
        var t = new XElement(proto);
        Fresh(t);
        var trs = t.Elements(W + "tr").ToList();
        string First(XElement tr) => string.Concat(tr.Elements(W + "tc").FirstOrDefault()?.Descendants(W + "t").Select(x => x.Value) ?? []).Trim();
        var keepHead = 1 + (trs.Count > 2 && First(trs[1]).StartsWith("Calc", StringComparison.OrdinalIgnoreCase) ? 1 : 0);
        var dataProto = trs.Count > keepHead ? trs[keepHead] : trs.Last();
        var totalProto = trs.Count > keepHead + 1 && First(trs.Last()).StartsWith("Total", StringComparison.OrdinalIgnoreCase) ? trs.Last() : null;
        foreach (var tr in trs.Skip(keepHead)) tr.Remove();
        void Add(XElement proto, string[] cells)
        {
            var tr = new XElement(proto);
            var tcs = tr.Elements(W + "tc").ToList();
            for (var i = 0; i < tcs.Count; i++) SetCell(tcs[i], i < cells.Length ? cells[i] : "");
            t.Add(tr);
        }
        foreach (var r in rows) Add(dataProto, r);
        if (total is not null) Add(totalProto ?? dataProto, total);
        return t;
    }

    /// <summary>The rows of an item's back-up table for a berth, and its total row.</summary>
    internal static (List<string[]> Rows, string[]? Total) Rows(string kind, IReadOnlyList<Line> lines)
    {
        string V(double v) => F(v, v >= 100 ? 0 : v >= 10 ? 1 : 2);
        var rows = new List<string[]>();
        switch (kind)
        {
            case "core":
                foreach (var l in lines) rows.Add([l.Label, F(l.Diameter, 3).TrimEnd('0').TrimEnd('.'), F(l.Area, 3), F(l.Top, 3), F(l.Bottom, 3), F(l.Height, 3), l.Count.ToString(CultureInfo.InvariantCulture), F(l.Volume, 0)]);
                return (rows, ["Total", "", "", "", "", "", lines.Sum(l => l.Count).ToString(CultureInfo.InvariantCulture), F(lines.Sum(l => l.Volume), 0)]);
            case "tube":
                foreach (var l in lines) rows.Add([l.Label, F(l.Diameter, 3).TrimEnd('0').TrimEnd('.'), F(l.Inner, 3).TrimEnd('0').TrimEnd('.'), F(l.Area, 3), F(l.Top, 3), F(l.Bottom, 3), F(l.Height, 3), l.Count.ToString(CultureInfo.InvariantCulture), V(l.Volume), F(l.Weight, 0)]);
                return (rows, lines.Count > 1 ? ["Total", "", "", "", "", "", "", lines.Sum(l => l.Count).ToString(CultureInfo.InvariantCulture), V(lines.Sum(l => l.Volume)), F(lines.Sum(l => l.Weight), 0)] : null);
            case "pile":
                foreach (var l in lines) rows.Add([l.Label, F(l.Diameter, 3).TrimEnd('0').TrimEnd('.'), F(l.Top, 3), F(l.Bottom, 3), F(l.Height, 3), l.Count.ToString(CultureInfo.InvariantCulture), F(l.Quantity, 0)]);
                return (rows, ["Total", "", "", "", "", lines.Sum(l => l.Count).ToString(CultureInfo.InvariantCulture), F(lines.Sum(l => l.Quantity), 0)]);
            case "slab":
                foreach (var l in lines) rows.Add([l.Label, l.Area > 0 ? F(l.Area, 2) : "—", l.Thickness > 0 ? F(l.Thickness, l.Thickness >= 1 ? 2 : 3) : "—", V(l.Volume)]);
                return (rows, lines.Count > 1 ? ["Total", "", "", F(lines.Sum(l => l.Volume), 0)] : null);
            default:
                return (rows, null);
        }
    }

    /// <summary>
    /// The Word back-up calculation of the berths, built from the template: its title and introduction, the note on
    /// what is left out, the basis (unit weights, each berth's wall length), a numbered section per element with a table
    /// per berth, and the reinforcement tables. Returns notes.
    /// </summary>
    public static List<string> WriteBackup(Project p, IReadOnlyList<Berth> berths, string template, string output)
    {
        var notes = new List<string>();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.Copy(template, output, overwrite: true);
        File.SetAttributes(output, FileAttributes.Normal);
        using (var zip = ZipFile.Open(output, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException("the back-up template isn't a Word document (.docx).");
            XDocument doc;
            using (var s = entry.Open()) doc = XDocument.Load(s);
            var body = doc.Root!.Element(W + "body") ?? throw new InvalidDataException("the back-up template has no body.");
            var pr = Protos.Of(body);
            var list = string.Join(" and ", berths.Select(b => b.Title));
            var parts = new List<XElement>
            {
                new XElement(pr.Title).Also(Fresh),
            };
            var intro = Regex.IsMatch(pr.IntroText, @"\bfor Berth[^.]*\.", RegexOptions.IgnoreCase)
                ? Regex.Replace(pr.IntroText, @"\bfor Berth[^.]*\.", "for " + list + ".", RegexOptions.IgnoreCase)
                : $"This document provides the backup calculation supporting every Structural Item quantity in the Bill of Quantities for {list}. For each element it shows how the billed quantity is obtained — quantity = geometry × count, or volume × unit weight, as appropriate — so that every figure reconciles with the issued Bill.";
            parts.Add(Para(pr.Body, intro));
            var excluded = berths.Where(b => b.Exclude.Trim().Length > 0).ToList();
            if (excluded.Count > 0)
            {
                parts.Add(Para(pr.NoteLabel ?? pr.Body, "Note:"));
                foreach (var b in excluded)
                    parts.Add(Para(pr.Note ?? pr.Body, $"{b.Title}: the count of elements does not include the elements in {b.Exclude.Trim()}" +
                        (b.ExcludeNote.Trim().Length > 0 ? $" ({b.ExcludeNote.Trim()}); they will be given in its own submission." : "; they will be given in its own submission.")));
            }
            parts.Add(Para(pr.Underline ?? pr.Body, "Material Properties & Basis"));
            parts.Add(Para(pr.Basis ?? pr.Body, $"Steel unit weight (density)\t\t{F(p.SteelDensity, 3)} t/m³"));
            parts.Add(Para(pr.Basis ?? pr.Body, $"Reinforced concrete unit weight\t\t{F(p.ConcreteDensity, 2)} t/m³"));
            foreach (var b in berths)
                parts.Add(Para(pr.Basis ?? pr.Body, $"{b.Title} Wall Length\t\t\t{(b.Length is > 0 ? Qty(b.Length) + " m" : "not set")}" + (b.Exclude.Trim().Length > 0 ? $" (excluding {b.Exclude.Trim()})" : "")));
            parts.Add(new XElement(pr.Empty).Also(Fresh));

            foreach (var item in p.Items.Where(i => i.Backup.Trim().Length > 0).OrderBy(i => i.BackupOrder).ThenBy(i => i.No))
            {
                var kind = ParseRule(item.Rule).Select(r => r.Table).FirstOrDefault(t => t.Length > 0) ?? "";
                if (kind.Length == 0 || kind == "none") continue;
                var follows = item.Backup.StartsWith("+", StringComparison.Ordinal);
                if (!follows)
                {
                    parts.Add(Para(pr.Heading, item.Backup.Trim()));
                    if (item.BackupNote.Trim().Length > 0) parts.Add(Para(pr.Plain, item.BackupNote.Trim()));
                }
                else if (item.BackupNote.Trim().Length > 0) parts.Add(Para(pr.Plain, item.BackupNote.Trim()));
                if (!pr.Tables.TryGetValue(kind, out var proto)) { notes.Add($"The back-up template has no {kind} table to copy; {item.Name} is left out."); continue; }
                foreach (var b in berths)
                {
                    var lines = (b.Measured?.Lines ?? []).Where(l => l.No == item.No).ToList();
                    if (lines.Count == 0) { notes.Add($"{b.Title}: no {item.Name} measured, no table."); continue; }
                    var label = b.Number.Length > 0 ? "Berth " + b.Number : b.Name;
                    parts.Add(Para(pr.Label, follows ? $"{label} – {item.Backup.Substring(1).Trim()}" : label));
                    var (rows, total) = Rows(kind, lines);
                    parts.Add(Table(proto, rows, total));
                    parts.Add(new XElement(pr.Empty).Also(Fresh));
                    // A hand quantity replaces the model's: say so under the table.
                    if (b.Hand.TryGetValue(item.No, out var hand))
                        parts.Add(Para(pr.Plain, $"Billed quantity {Qty(hand)} {item.Unit} (set by hand; the model gives {Qty(lines.Sum(l => l.Quantity))})."));
                }
            }

            if (pr.Tables.TryGetValue("rebar", out var rebarProto))
            {
                parts.Add(Para(pr.Heading, "Reinforcement"));
                parts.Add(Para(pr.Plain, "Reinforcement is billed by weight at a rate per cubic metre of reinforced concrete. The billed tonnage corresponds to the following volume of reinforced concrete:"));
                var names = berths.SelectMany(b => Rebar(p, b)).Select(r => r.Name).Distinct().ToList();
                foreach (var n in names) parts.Add(Para(pr.ListItem ?? pr.Plain, n));
                foreach (var b in berths)
                {
                    var rebar = Rebar(p, b);
                    if (rebar.Count == 0) continue;
                    parts.Add(Para(pr.Label, b.Number.Length > 0 ? "Berth " + b.Number : b.Name));
                    var rows = rebar.Select(r => new[] { r.Name, F(r.Kg, 0), F(r.Volume, 0), F(r.Weight, 0) }).ToList();
                    var vol = rebar.Sum(r => r.Volume);
                    var wt = rebar.Sum(r => r.Weight);
                    parts.Add(Table(rebarProto, rows, ["Total", F(vol > 0 ? wt / vol * 1000 : 0, 0), F(vol, 0), F(wt, 0)]));
                    parts.Add(new XElement(pr.Empty).Also(Fresh));
                }
            }
            else notes.Add("The back-up template has no reinforcement table to copy; the reinforcement section is left out.");

            if (pr.SectPr is { } sect) parts.Add(new XElement(sect));
            body.Nodes().Remove();
            body.Add(parts);
            entry.Delete();
            var fresh = zip.CreateEntry("word/document.xml", CompressionLevel.Optimal);
            using var w = fresh.Open();
            doc.Save(w, SaveOptions.DisableFormatting);
        }
        return notes;
    }

    private static XElement Also(this XElement e, Action<XElement> act) { act(e); return e; }
}
