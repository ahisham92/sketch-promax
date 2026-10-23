using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SketchProMax.Tools;

/// <summary>
/// Changes cells of an existing Excel workbook (.xlsx) and keeps everything else as it is: styles, formulas, merged
/// cells, comments, drawings, print setup, headers and footers. Used to fill the user's own templates (the cost
/// comparison workbook, the BOQ) so the files come out in exactly their format. Values are written as numbers or as
/// text in the cell itself; formulas the writer doesn't touch stay, and Excel recalculates everything when the file
/// is opened. No Revit here.
/// </summary>
internal sealed class XlsxBook : IDisposable
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PkgRel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace Types = "http://schemas.openxmlformats.org/package/2006/content-types";

    private readonly ZipArchive _zip;
    private readonly Stream _stream;
    private readonly XDocument _workbook;
    private readonly Dictionary<string, string> _parts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, XDocument> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _changed = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _shared = [];
    private XDocument? _sst;
    private bool _formulaDropped;

    public List<string> SheetNames { get; } = [];

    private XlsxBook(Stream stream, bool write)
    {
        _stream = stream;
        _zip = new ZipArchive(stream, write ? ZipArchiveMode.Update : ZipArchiveMode.Read, leaveOpen: false);
        _workbook = Load("xl/workbook.xml") ?? throw new InvalidDataException("it has no workbook part");
        var rels = Load("xl/_rels/workbook.xml.rels")?.Root?.Elements(PkgRel + "Relationship")
            .ToDictionary(r => (string?)r.Attribute("Id") ?? "", r => (string?)r.Attribute("Target") ?? "") ?? [];
        foreach (var sheet in _workbook.Root!.Element(Main + "sheets")?.Elements(Main + "sheet") ?? [])
        {
            var name = (string?)sheet.Attribute("name") ?? "";
            if (!rels.TryGetValue((string?)sheet.Attribute(RelNs + "id") ?? "", out var target)) continue;
            _parts[name] = target.StartsWith("/", StringComparison.Ordinal) ? target.TrimStart('/') : "xl/" + target;
            SheetNames.Add(name);
        }
        _sst = Load("xl/sharedStrings.xml");
        if (_sst is not null)
            foreach (var si in _sst.Root!.Elements(Main + "si")) _shared.Add(string.Concat(si.Descendants(Main + "t").Select(t => t.Value)));
    }

    /// <summary>Opens a workbook to read. Throws InvalidDataException when it isn't one.</summary>
    public static XlsxBook Read(string path)
    {
        var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        try { return new XlsxBook(stream, false); }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException) { stream.Dispose(); throw new InvalidDataException(Path.GetFileName(path) + " isn't an Excel workbook (.xlsx).", ex); }
    }

    /// <summary>Copies the template to <paramref name="output"/> and opens the copy to change. <see cref="Save"/> writes it.</summary>
    public static XlsxBook Copy(string template, string output)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.Copy(template, output, overwrite: true);
        File.SetAttributes(output, FileAttributes.Normal);
        var stream = File.Open(output, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        try { return new XlsxBook(stream, true); }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException) { stream.Dispose(); throw new InvalidDataException(Path.GetFileName(template) + " isn't an Excel workbook (.xlsx).", ex); }
    }

    private XDocument? Load(string part)
    {
        if (_loaded.TryGetValue(part, out var doc)) return doc;
        var entry = _zip.GetEntry(part);
        if (entry is null) return null;
        using var s = entry.Open();
        doc = XDocument.Load(s, LoadOptions.PreserveWhitespace);
        _loaded[part] = doc;
        return doc;
    }

    private XDocument Sheet(string name) =>
        _parts.TryGetValue(name, out var part) && Load(part) is { } doc ? doc : throw new InvalidDataException($"the workbook has no sheet \"{name}\"");

    public bool HasSheet(string name) => _parts.ContainsKey(name);

    // ---- Cell references ---------------------------------------------------------------------------------------------

    /// <summary>"C12" from column 2 (zero-based) and row 12.</summary>
    public static string Ref(int col, int row) => OfficeXlsx.Column(col) + row.ToString(CultureInfo.InvariantCulture);

    public static (int Col, int Row) Parse(string reference)
    {
        var m = Regex.Match(reference.Replace("$", ""), @"^([A-Za-z]+)(\d+)$");
        if (!m.Success) return (-1, -1);
        return (ChecklistMath.Column(m.Groups[1].Value), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    // ---- Reading -----------------------------------------------------------------------------------------------------

    private string CellText(XElement c)
    {
        var type = (string?)c.Attribute("t") ?? "";
        return type switch
        {
            "s" => int.TryParse(c.Element(Main + "v")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && i >= 0 && i < _shared.Count ? _shared[i] : "",
            "inlineStr" => string.Concat(c.Descendants(Main + "t").Select(t => t.Value)),
            _ => c.Element(Main + "v")?.Value ?? "",
        };
    }

    /// <summary>Every cell with a value or formula: its column, row, text (a formula's last value) and formula.</summary>
    public IEnumerable<(int Col, int Row, string Text, string Formula)> Cells(string sheet)
    {
        foreach (var c in Sheet(sheet).Descendants(Main + "c"))
        {
            var (col, row) = Parse((string?)c.Attribute("r") ?? "");
            if (col < 0) continue;
            var text = CellText(c);
            var f = c.Element(Main + "f")?.Value ?? "";
            if (text.Length > 0 || f.Length > 0) yield return (col, row, text, f);
        }
    }

    public string Text(string sheet, int col, int row) => FindCell(sheet, col, row) is { } c ? CellText(c) : "";

    public string Formula(string sheet, int col, int row) => FindCell(sheet, col, row)?.Element(Main + "f")?.Value ?? "";

    public double? Number(string sheet, int col, int row) => BerthCostMath.Num(Text(sheet, col, row));

    /// <summary>The workbook's defined names (R_PIPE) with the sheet and cell they point at.</summary>
    public Dictionary<string, (string Sheet, int Col, int Row)> Names()
    {
        var names = new Dictionary<string, (string, int, int)>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in _workbook.Root!.Element(Main + "definedNames")?.Elements(Main + "definedName") ?? [])
        {
            if (d.Attribute("localSheetId") is not null) continue;
            var m = Regex.Match(d.Value.Trim(), @"^'?(.+?)'?!\$?([A-Z]+)\$?(\d+)$");
            if (!m.Success) continue;
            var (col, row) = Parse(m.Groups[2].Value + m.Groups[3].Value);
            names[(string?)d.Attribute("name") ?? ""] = (m.Groups[1].Value.Replace("''", "'"), col, row);
        }
        return names;
    }

    private XElement? FindCell(string sheet, int col, int row)
    {
        var data = Sheet(sheet).Root!.Element(Main + "sheetData");
        var r = data?.Elements(Main + "row").FirstOrDefault(x => (int?)x.Attribute("r") == row);
        var reference = Ref(col, row);
        return r?.Elements(Main + "c").FirstOrDefault(c => string.Equals((string?)c.Attribute("r"), reference, StringComparison.OrdinalIgnoreCase));
    }

    // ---- Writing -----------------------------------------------------------------------------------------------------

    private XElement Cell(string sheet, int col, int row)
    {
        var doc = Sheet(sheet);
        _changed.Add(_parts[sheet]);
        var data = doc.Root!.Element(Main + "sheetData") ?? throw new InvalidDataException($"sheet \"{sheet}\" has no cells");
        var r = data.Elements(Main + "row").FirstOrDefault(x => (int?)x.Attribute("r") == row);
        if (r is null)
        {
            r = new XElement(Main + "row", new XAttribute("r", row));
            var after = data.Elements(Main + "row").LastOrDefault(x => ((int?)x.Attribute("r") ?? 0) < row);
            if (after is null) data.AddFirst(r); else after.AddAfterSelf(r);
        }
        var reference = Ref(col, row);
        var c = r.Elements(Main + "c").FirstOrDefault(x => string.Equals((string?)x.Attribute("r"), reference, StringComparison.OrdinalIgnoreCase));
        if (c is null)
        {
            c = new XElement(Main + "c", new XAttribute("r", reference));
            // A new cell takes the style of the row (or of the cell before it in the row).
            if ((string?)r.Attribute("s") is { } rs && (string?)r.Attribute("customFormat") == "1") c.SetAttributeValue("s", rs);
            var before = r.Elements(Main + "c").LastOrDefault(x => Parse((string?)x.Attribute("r") ?? "").Col < col);
            if (before is null) r.AddFirst(c); else before.AddAfterSelf(c);
            r.SetAttributeValue("spans", null);
        }
        if (c.Element(Main + "f") is not null) _formulaDropped = true;
        c.Elements().Remove();
        c.SetAttributeValue("t", null);
        return c;
    }

    public void SetNumber(string sheet, int col, int row, double value)
    {
        var c = Cell(sheet, col, row);
        c.Add(new XElement(Main + "v", value.ToString("R", CultureInfo.InvariantCulture)));
    }

    public void SetText(string sheet, int col, int row, string text)
    {
        var c = Cell(sheet, col, row);
        if (text.Length == 0) return;
        c.SetAttributeValue("t", "inlineStr");
        var t = new XElement(Main + "t", text);
        if (text != text.Trim() || text.Contains('\n')) t.SetAttributeValue(XNamespace.Xml + "space", "preserve");
        c.Add(new XElement(Main + "is", t));
    }

    public void Clear(string sheet, int col, int row)
    {
        if (FindCell(sheet, col, row) is null) return;
        Cell(sheet, col, row);
    }

    /// <summary>Changes every text in the workbook (shared texts, texts in cells, sheet headers and footers) through <paramref name="change"/>.</summary>
    public void ReplaceTexts(Func<string, string> change)
    {
        if (_sst is not null)
        {
            var any = false;
            foreach (var t in _sst.Descendants(Main + "t"))
            {
                var v = change(t.Value);
                if (v != t.Value) { t.Value = v; any = true; }
            }
            if (any)
            {
                _changed.Add("xl/sharedStrings.xml");
                _shared.Clear();
                foreach (var si in _sst.Root!.Elements(Main + "si")) _shared.Add(string.Concat(si.Descendants(Main + "t").Select(t => t.Value)));
            }
        }
        foreach (var name in SheetNames)
        {
            var doc = Sheet(name);
            var any = false;
            foreach (var t in doc.Descendants(Main + "is").Descendants(Main + "t").Concat(doc.Descendants(Main + "headerFooter").Elements()))
            {
                var v = change(t.Value);
                if (v != t.Value) { t.Value = v; any = true; }
            }
            if (any) _changed.Add(_parts[name]);
        }
    }

    /// <summary>Writes the changed parts, asks Excel to recalculate on open, and drops the calculation chain (Excel rebuilds it).</summary>
    public void Save()
    {
        var calc = _workbook.Root!.Element(Main + "calcPr");
        if (calc is null)
        {
            calc = new XElement(Main + "calcPr");
            var anchor = _workbook.Root.Elements().LastOrDefault(e => e.Name.LocalName is "sheets" or "functionGroups" or "externalReferences" or "definedNames");
            if (anchor is null) _workbook.Root.Add(calc); else anchor.AddAfterSelf(calc);
        }
        calc.SetAttributeValue("fullCalcOnLoad", "1");
        _changed.Add("xl/workbook.xml");
        if (_formulaDropped && _zip.GetEntry("xl/calcChain.xml") is { } chain)
        {
            chain.Delete();
            if (Load("xl/_rels/workbook.xml.rels") is { } rels)
            {
                rels.Root!.Elements(PkgRel + "Relationship").Where(r => ((string?)r.Attribute("Target") ?? "").EndsWith("calcChain.xml", StringComparison.OrdinalIgnoreCase)).Remove();
                _changed.Add("xl/_rels/workbook.xml.rels");
            }
            if (Load("[Content_Types].xml") is { } types)
            {
                types.Root!.Elements(Types + "Override").Where(o => ((string?)o.Attribute("PartName") ?? "").EndsWith("calcChain.xml", StringComparison.OrdinalIgnoreCase)).Remove();
                _changed.Add("[Content_Types].xml");
            }
        }
        foreach (var part in _changed)
        {
            if (!_loaded.TryGetValue(part, out var doc)) continue;
            _zip.GetEntry(part)?.Delete();
            var entry = _zip.CreateEntry(part, CompressionLevel.Optimal);
            using var s = entry.Open();
            doc.Save(s, SaveOptions.DisableFormatting);
        }
        _changed.Clear();
    }

    public void Dispose()
    {
        _zip.Dispose();
        _stream.Dispose();
    }
}
