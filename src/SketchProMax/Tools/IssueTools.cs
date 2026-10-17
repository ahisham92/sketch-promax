using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

/// <summary>Drafting views with notes and tables, and putting them on sheets.</summary>
internal static class Drafting
{
    public static double Mm(double mm) => mm / 304.8;

    public static ViewDrafting Create(Document doc, string name, int scale)
    {
        var vft = ViewKit.TypeFor(doc, ViewFamily.Drafting);
        var view = ViewDrafting.Create(doc, vft.Id);
        ViewKit.Finish(doc, view, name, scale, null);
        return view;
    }

    public static ViewSheet? Sheet(Document doc, ToolArgs args)
    {
        if (args.OptLong("sheet_id") is { } id) return doc.GetElement(new ElementId(id)) as ViewSheet ?? throw new ArgumentException($"{id} is not a sheet.");
        if (args.OptString("sheet_number") is { } number)
            return new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().FirstOrDefault(s => s.SheetNumber == number)
                   ?? throw new ArgumentException($"No sheet numbered '{number}'.");
        return null;
    }

    /// <summary>Places the view on the sheet: the first goes top left, later ones to its right, wrapping down.</summary>
    public static long? Place(Document doc, ViewSheet? sheet, View view, int index)
    {
        if (sheet is null || !Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id)) return null;
        var o = sheet.Outline;
        double w = o.Max.U - o.Min.U, h = o.Max.V - o.Min.V;
        var col = index % 3; var row = index / 3;
        var at = new XYZ(o.Min.U + w * (0.2 + 0.28 * col), o.Max.V - h * (0.3 + 0.35 * row), 0);
        var vp = Viewport.Create(doc, sheet.Id, view.Id, at);
        // Keep a tall view (long notes) inside the sheet: move it so its top sits near the sheet's top.
        var box = vp.GetBoxOutline();
        var dy = o.Max.V - h * 0.05 - box.MaximumPoint.Y;
        if (dy < 0) ElementTransformUtils.MoveElement(doc, vp.Id, new XYZ(0, dy, 0));
        return vp.Id.Value;
    }

    /// <summary>Draws a table of detail lines and text at 1:1 (paper sizes), top left at the origin. Returns its height.</summary>
    public static double Table(Document doc, View view, XYZ origin, IList<string> headers, IList<IList<string>> rows, IList<double> colMm, string? title = null)
    {
        var textType = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
        var rowH = Mm(8);
        var y = origin.Y;
        if (title != null) { TextNote.Create(doc, view.Id, new XYZ(origin.X, y, 0), title, textType); y -= Mm(10); }
        var widths = colMm.Select(Mm).ToList();
        var total = widths.Sum();
        var all = new List<IList<string>> { headers };
        all.AddRange(rows);
        var top = y;
        for (var r = 0; r <= all.Count; r++)
            doc.Create.NewDetailCurve(view, Line.CreateBound(new XYZ(origin.X, top - r * rowH, 0), new XYZ(origin.X + total, top - r * rowH, 0)));
        var x = origin.X;
        for (var c = 0; c <= widths.Count; c++)
        {
            doc.Create.NewDetailCurve(view, Line.CreateBound(new XYZ(x, top, 0), new XYZ(x, top - all.Count * rowH, 0)));
            if (c < widths.Count) x += widths[c];
        }
        for (var r = 0; r < all.Count; r++)
        {
            x = origin.X;
            for (var c = 0; c < widths.Count; c++)
            {
                var text = c < all[r].Count ? all[r][c] : "";
                if (text.Length > 0) TextNote.Create(doc, view.Id, new XYZ(x + Mm(1.5), top - r * rowH - Mm(1.5), 0), text, textType);
                x += widths[c];
            }
        }
        return origin.Y - top + all.Count * rowH;
    }

    public static ToolResult Call(UIApplication app, IRevitTool tool, object args)
    {
        var raw = JsonSerializer.SerializeToElement(args);
        try { return tool.Execute(app, new ToolArgs(raw.EnumerateObject().Where(p => p.Value.ValueKind != JsonValueKind.Null).ToDictionary(p => p.Name, p => p.Value.Clone()))); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Autodesk.Revit.Exceptions.ApplicationException or System.IO.IOException or UnauthorizedAccessException)
        { return ToolResult.Error($"{tool.Name}: {ex.Message}"); }
    }
}

public sealed class GeneralNotesTool : IRevitTool
{
    public string Name => "general_notes";
    public string Description =>
        "Write the general notes sheet content: a 'General Notes' drafting view with numbered notes under headings. The " +
        "structural template covers codes, materials (concrete grades by element, reinforcement, structural steel, bolts, " +
        "welds), covers, loads, foundations, workmanship and drawing conventions, filled from what you give and leaving " +
        "[TO CONFIRM] where you don't; or give your own sections. Optionally placed on a sheet.";
    public string PropertiesJson => """
    {
      "template": {"type": "string", "enum": ["structural", "none"], "description": "Default structural."},
      "values": {"type": "object", "description": "Fills the template, e.g. {codes: 'BS EN 1992-1-1 + UK NA', concrete: 'C32/40 slabs and beams, C40/50 columns', rebar: 'B500B to BS 4449', steel: 'S355JR', bolts: 'Grade 8.8', cover: 'slabs 25, beams 30, columns 35, foundations 50 (mm)', live_load: '3.0 kPa offices', soil: '150 kPa allowable'}."},
      "sections": {"type": "array", "items": {"type": "object", "properties": {"heading": {"type": "string"}, "notes": {"type": "array", "items": {"type": "string"}}}, "required": ["heading", "notes"]}, "description": "Your own sections, added after the template's."},
      "name": {"type": "string", "description": "View name (default 'General Notes')."},
      "sheet_number": {"type": "string"}, "sheet_id": {"type": "integer"}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (args.Has("values"))
            foreach (var p in args.Raw["values"].EnumerateObject()) values[p.Name] = p.Value.ToString();
        string V(string key) => values.TryGetValue(key, out var v) && v.Length > 0 ? v : "[TO CONFIRM]";
        var sections = new List<(string heading, List<string> notes)>();
        if ((args.OptString("template") ?? "structural") == "structural")
        {
            sections.Add(("GENERAL", [
                "These drawings are to be read with all relevant architectural, MEP and specialist drawings and the specification.",
                "Do not scale from the drawings. All dimensions are in millimetres and levels in metres unless noted otherwise.",
                "Any discrepancy shall be reported to the engineer before work proceeds.",
                $"Design codes: {V("codes")}."]));
            sections.Add(("MATERIALS", [
                $"Concrete: {V("concrete")}.", $"Reinforcement: {V("rebar")}.", $"Structural steel: {V("steel")}.",
                $"Bolts: {V("bolts")}. Welds: {V("welds")}.", $"Blockwork: {V("masonry")}."]));
            sections.Add(("COVER TO REINFORCEMENT", [$"Nominal cover: {V("cover")}.", "Cover is to the outermost bar including links."]));
            sections.Add(("DESIGN LOADS", [$"Imposed loads: {V("live_load")}.", $"Superimposed dead loads: {V("sdl")}.", $"Wind / seismic: {V("lateral")}."]));
            sections.Add(("FOUNDATIONS", [$"Allowable bearing pressure: {V("soil")}.", "Founding levels to be inspected and approved before blinding.", $"Piles: {V("piles")}."]));
            sections.Add(("REINFORCEMENT", [
                $"Laps: {V("laps")}. Laps to be staggered where possible.",
                "Bar shapes to BS 8666 / ACI 315 as applicable; bending schedules govern bar shapes.",
                "Spacers and chairs to maintain cover at not more than 1.0 m centres."]));
            sections.Add(("WORKMANSHIP", [
                "Construction joints only where shown or approved by the engineer.",
                "Openings, sleeves and embedded items are to be coordinated with the sleeve schedule before casting.",
                $"Formwork striking times: {V("striking")}."]));
        }
        foreach (var s in args.Array("sections"))
            sections.Add((s.TryGetProperty("heading", out var hd) ? hd.GetString() ?? "" : throw new ArgumentException("Each section needs a heading and notes."),
                s.TryGetProperty("notes", out var nt) && nt.ValueKind == JsonValueKind.Array ? nt.EnumerateArray().Select(n => n.GetString() ?? "").ToList() : throw new ArgumentException("Each section needs a heading and notes.")));
        if (sections.Count == 0) return ToolResult.Error("Give sections, or use the structural template.");

        var sheet = Drafting.Sheet(doc, args);
        using var t = RevitHelpers.StartTransaction(doc, "General notes");
        var view = Drafting.Create(doc, args.OptString("name") ?? "General Notes", 1);
        var textType = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
        var y = 0.0; var n = 0;
        foreach (var (heading, notes) in sections)
        {
            n++;
            var body = $"{n}. {heading}\n" + string.Join("\n", notes.Select((x, i) => $"{n}.{i + 1}  {x}"));
            var note = TextNote.Create(doc, view.Id, new XYZ(0, y, 0), Drafting.Mm(180), body, textType);
            doc.Regenerate();
            y -= (note.get_BoundingBox(view) is { } bb ? bb.Max.Y - bb.Min.Y : Drafting.Mm(6) * (notes.Count + 1)) + Drafting.Mm(6);
        }
        var vp = Drafting.Place(doc, sheet, view, 0);
        if (t.Commit() != TransactionStatus.Committed) return ToolResult.Error("Revit refused the notes when saving them.");
        return ToolResult.Ok(new
        {
            viewId = view.Id.Value, view = view.Name, sections = sections.Count, placedOn = vp is null ? null : sheet?.SheetNumber,
            toConfirm = sections.Sum(s => s.notes.Count(x => x.Contains("[TO CONFIRM]"))),
        });
    }
}

public sealed class TypicalDetailsTool : IRevitTool
{
    public string Name => "typical_details";
    public string Description =>
        "Draw typical detail tables in drafting views: lap and anchorage lengths per bar size, cover per element, and " +
        "standard bend and hook dimensions, from multiples of the bar diameter you give (typical values when you don't). " +
        "Optionally placed on a sheet. The values must be checked against the project's code.";
    public string PropertiesJson => """
    {
      "kinds": {"type": "array", "items": {"type": "string", "enum": ["lap_schedule", "cover_schedule", "hook_schedule"]}, "description": "Default all."},
      "bar_sizes": {"type": "array", "items": {"type": "number"}, "description": "Bar diameters mm (default 8, 10, 12, 16, 20, 25, 32, 40)."},
      "lap_factor": {"type": "number", "description": "Tension lap = factor x d (default 50)."},
      "anchorage_factor": {"type": "number", "description": "Anchorage = factor x d (default 40)."},
      "covers": {"type": "object", "description": "Element -> cover mm, e.g. {Slabs: 25, Beams: 30, Columns: 35, Walls: 30, Foundations: 50, 'Retaining walls (earth face)': 50}."},
      "sheet_number": {"type": "string"}, "sheet_id": {"type": "integer"}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var kinds = args.StringList("kinds");
        if (kinds.Count == 0) kinds = ["lap_schedule", "cover_schedule", "hook_schedule"];
        var sizes = args.Has("bar_sizes") ? args.Raw["bar_sizes"].EnumerateArray().Select(x => x.GetDouble()).ToList() : [8, 10, 12, 16, 20, 25, 32, 40];
        var lap = args.OptDouble("lap_factor") ?? 50;
        var anchor = args.OptDouble("anchorage_factor") ?? 40;
        var covers = new List<(string, double)>();
        if (args.Has("covers")) foreach (var p in args.Raw["covers"].EnumerateObject()) covers.Add((p.Name, p.Value.GetDouble()));
        if (covers.Count == 0) covers = [("Slabs", 25), ("Beams", 30), ("Columns", 35), ("Walls", 30), ("Foundations (cast against blinding)", 50), ("Cast against earth", 75)];
        string N(double v) => Math.Round(v).ToString(CultureInfo.InvariantCulture);

        var sheet = Drafting.Sheet(doc, args);
        var views = new List<object>();
        using var t = RevitHelpers.StartTransaction(doc, "Typical details");
        var i = 0;
        foreach (var kind in kinds)
        {
            ViewDrafting view;
            switch (kind)
            {
                case "lap_schedule":
                    view = Drafting.Create(doc, "Typical - Lap and anchorage lengths", 1);
                    Drafting.Table(doc, view, XYZ.Zero, ["Bar (mm)", $"Lap {N(lap)}d (mm)", $"Anchorage {N(anchor)}d (mm)"],
                        sizes.Select(d => (IList<string>)[N(d), N(lap * d), N(anchor * d)]).ToList(), [25, 40, 45],
                        $"LAP AND ANCHORAGE LENGTHS (typical, confirm against the design code)");
                    break;
                case "cover_schedule":
                    view = Drafting.Create(doc, "Typical - Cover to reinforcement", 1);
                    Drafting.Table(doc, view, XYZ.Zero, ["Element", "Nominal cover (mm)"], covers.Select(c => (IList<string>)[c.Item1, N(c.Item2)]).ToList(), [70, 40],
                        "NOMINAL COVER TO REINFORCEMENT");
                    break;
                case "hook_schedule":
                    view = Drafting.Create(doc, "Typical - Bends and hooks", 1);
                    Drafting.Table(doc, view, XYZ.Zero, ["Bar (mm)", "Min. bend dia. (mm)", "90° hook tail (mm)", "135° link hook (mm)"],
                        sizes.Select(d => (IList<string>)[N(d), N((d <= 16 ? 4 : 7) * d), N(Math.Max(12 * d, 100)), N(Math.Max(10 * d, 75))]).ToList(), [25, 40, 40, 45],
                        "STANDARD BENDS AND HOOKS (typical: 4d up to 16 mm, 7d above; confirm against BS 8666 / ACI 318)");
                    break;
                default: throw new ArgumentException($"Unknown kind '{kind}'.");
            }
            views.Add(new { kind, viewId = view.Id.Value, view = view.Name, viewport = Drafting.Place(doc, sheet, view, i++) });
        }
        if (t.Commit() != TransactionStatus.Committed) return ToolResult.Error("Revit refused the details when saving them.");
        return ToolResult.Ok(new { views, sheet = sheet?.SheetNumber });
    }
}

public sealed class IssueDrawingsTool : IRevitTool
{
    public string Name => "issue_drawings";
    public string Description =>
        "Issue a set of drawings end to end: first the issue check (issue_check: check_drawings, spec_check, boq_check and " +
        "check_model, pass/fail per sheet), and nothing is issued while it has open items, unless the user turned on " +
        "'Allow issue with open items' in Settings and asks for it with a reason (recorded). Then pick the sheets, add a " +
        "new (or existing) revision and mark it issued, set the sheet issue date, export a PDF per sheet (and DWG if asked) " +
        "named by your pattern, write the list of drawings and check every expected file, and write the transmittal (Excel: " +
        "drawings, the issue record and the pass/fail report).";
    public string PropertiesJson => """
    {
      "sheet_numbers": {"type": "array", "items": {"type": "string"}, "description": "Default: every sheet."},
      "number_starts_with": {"type": "string"},
      "revision_description": {"type": "string", "description": "Create a new revision with this description (e.g. 'Issued for construction')."},
      "revision": {"type": "string", "description": "Or use an existing revision (number, description or id)."},
      "date": {"type": "string", "description": "Revision and issue date (default today, yyyy-MM-dd)."},
      "issued_by": {"type": "string"}, "issued_to": {"type": "string"},
      "name_pattern": {"type": "string", "description": "File name per sheet, e.g. 'PRJ-{number}-{revision}' (default '{number} - {name}')."},
      "dwg": {"type": "boolean", "description": "Also export DWG per sheet (default false)."},
      "coordinates": {"type": "string", "enum": ["internal", "shared"], "description": "DWG coordinates."},
      "units": {"type": "string", "enum": ["mm", "m"], "description": "DWG units."},
      "folder": {"type": "string", "description": "Issue folder (default Documents\\SketchProMax\\Issue <date>)."},
      "spec_checklist": {"type": "string", "description": "For the issue check: spec_check's saved checklist (remembered per model)."},
      "boq_file": {"type": "string", "description": "For the issue check: the BOQ to compare (remembered per model)."},
      "allow_open_items": {"type": "boolean", "description": "Issue even though the issue check has open items. Only when the user asks and 'Allow issue with open items' is on in Settings (off by default); recorded in the issue record."},
      "open_items_reason": {"type": "string", "description": "With allow_open_items: the user's reason, recorded."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var numbers = args.StringList("sheet_numbers");
        var sheets = IssueGate.Sheets(doc, numbers, args.OptString("number_starts_with"));
        if (sheets.Count == 0) return ToolResult.Error("No sheets match.");
        var missing = numbers.Where(n => sheets.All(s => s.SheetNumber != n)).ToList();
        var date = args.OptString("date") ?? DateTime.Now.ToString("yyyy-MM-dd");
        var folder = OutputPaths.Checked(args.OptString("folder")) ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SketchProMax", $"Issue {date}");
        var pattern = args.OptString("name_pattern") ?? "{number} - {name}";
        var steps = new List<object>();
        string Short(ToolResult r) => (r.IsError ? "ERROR: " : "") + (r.Text.Length > 600 ? r.Text.Substring(0, 600) + "..." : r.Text);
        var sheetNumbers = sheets.Select(s => s.SheetNumber).ToArray();

        // 1. The issue check first, so a block leaves the model untouched. A run the ribbon window just cleared for these
        // sheets is used as it is (with its override reason, if the user let open items through there).
        string? overrideReason = null;
        var cleared = IssueGate.TakeCleared(doc, sheetNumbers);
        var gate = cleared?.Run ?? IssueGate.Check(app, sheets, args.OptString("spec_checklist"), args.OptString("boq_file"), System.IO.Path.Combine(folder, "Issue check"));
        if (gate.Blocked)
        {
            var asked = cleared?.OverrideReason is not null || args.OptBool("allow_open_items");
            var (allowed, used, why) = IssueGateMath.CanIssue(gate.Groups, IssueGate.OverrideAllowed, asked, cleared?.OverrideReason ?? args.OptString("open_items_reason"));
            if (!allowed)
                return ToolResult.Ok(new { stopped = $"Not issued: {why} Nothing in the model was changed.", sheets = sheets.Count, issueCheck = IssueGate.Chat(gate) });
            if (used) overrideReason = cleared?.OverrideReason ?? args.OptString("open_items_reason");
        }
        steps.Add(new { step = "issue check", result = IssueGateMath.Verdict(gate.Groups), overrideUsed = overrideReason is not null, report = gate.Report });

        // 2. Revision on the sheets, and the issue date.
        string? revision = args.OptString("revision");
        var revisionText = "";
        using (var t = RevitHelpers.StartTransaction(doc, "Issue: revision and dates"))
        {
            Revision? rev = null;
            if (args.OptString("revision_description") is { } description)
            {
                rev = Revision.Create(doc);
                rev.Description = description;
                rev.RevisionDate = date;
                revision = rev.Id.Value.ToString(CultureInfo.InvariantCulture);
                steps.Add(new { step = "revision created", revision = $"{rev.RevisionNumber} {description}" });
            }
            else if (revision != null)
                rev = new FilteredElementCollector(doc).OfClass(typeof(Revision)).Cast<Revision>().FirstOrDefault(r =>
                    r.Id.Value.ToString(CultureInfo.InvariantCulture) == revision || r.RevisionNumber == revision || r.Description.Equals(revision, StringComparison.OrdinalIgnoreCase));
            if (rev is { Issued: false })
            {
                if (args.OptString("issued_by") is { } by) rev.IssuedBy = by;
                if (args.OptString("issued_to") is { } to) rev.IssuedTo = to;
            }
            if (rev is not null) revisionText = $"{rev.RevisionNumber} {rev.Description}".Trim();
            foreach (var s in sheets)
                if (s.get_Parameter(BuiltInParameter.SHEET_ISSUE_DATE) is { IsReadOnly: false } p) p.Set(date);
            if (t.Commit() != TransactionStatus.Committed) return ToolResult.Error("Revit refused the revision and issue dates.");
        }
        if (revision != null)
        {
            var r = Drafting.Call(app, new SetSheetRevisionsTool(), new { revision, sheet_numbers = sheetNumbers, issue = true });
            steps.Add(new { step = "revision on sheets", result = Short(r) });
            if (r.IsError) return ToolResult.Error($"Stopped: the revision couldn't be added to the sheets. {r.Text}");
        }

        // 3. Exports.
        var ids = sheets.Select(s => s.Id.Value).ToArray();
        var pdf = Drafting.Call(app, new ExportPdfTool(), new { view_ids = ids, combine = false, folder, name_pattern = pattern });
        steps.Add(new { step = "pdf", result = Short(pdf) });
        var failed = new List<string>();
        if (pdf.IsError) failed.Add("pdf");
        var extensions = new List<string> { "pdf" };
        if (args.OptBool("dwg"))
        {
            var dwg = Drafting.Call(app, new ExportFilesTool(), new { format = "dwg", view_ids = ids, folder, name_pattern = pattern, coordinates = args.OptString("coordinates"), units = args.OptString("units") });
            steps.Add(new { step = "dwg", result = Short(dwg) });
            if (dwg.IsError) failed.Add("dwg");
            extensions.Add("dwg");
        }

        // 4. List of drawings, checked against the files.
        var list = Drafting.Call(app, new ExportDrawingListTool(), new
        {
            sheet_numbers = sheetNumbers, folder, check_folder = folder, extensions,
            name_pattern = pattern.Replace("{date}", DateTime.Now.ToString("yyyy-MM-dd")),
            fields = new[] { "Sheet Issue Date", "Current Revision", "Current Revision Description" },
        });
        steps.Add(new { step = "list of drawings", result = Short(list) });

        // 5. The issue record and the transmittal (drawings, the record and the pass/fail report), kept with the model too.
        var record = new IssueGateMath.IssueRecord
        {
            Date = date, Model = doc.Title, Revision = revisionText, Sheets = sheets.Count, Gate = IssueGateMath.Verdict(gate.Groups),
            OpenItems = gate.Groups.Sum(g => g.Open), AcceptedItems = gate.Groups.Sum(g => g.AcceptedCount),
            OverrideUsed = overrideReason is not null, OverrideReason = overrideReason ?? "", IssuedBy = Environment.UserName, Folder = folder,
        };
        string? transmittal = null;
        try
        {
            var files = System.IO.Directory.Exists(folder) ? System.IO.Directory.GetFiles(folder).Select(System.IO.Path.GetFileName).ToList() : [];
            var drawings = sheets.Select(s => (s.SheetNumber, s.Name,
                s.get_Parameter(BuiltInParameter.SHEET_CURRENT_REVISION)?.AsString() ?? "",
                string.Join(", ", files.Where(f => f!.IndexOf(s.SheetNumber, StringComparison.OrdinalIgnoreCase) >= 0 && extensions.Any(e => f.EndsWith("." + e, StringComparison.OrdinalIgnoreCase))))));
            transmittal = System.IO.Path.Combine(folder, $"Transmittal {date}{(revisionText.Length > 0 ? " rev " + string.Concat(revisionText.Split(System.IO.Path.GetInvalidFileNameChars())) : "")}.xlsx");
            Xlsx.Write(transmittal, IssueGateMath.TransmittalSheets(record, drawings, gate.Groups, gate.Checks));
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            steps.Add(new { step = "transmittal", result = "ERROR: not written: " + ex.Message });
            transmittal = null;
        }
        IssueGate.Record(doc, record);
        steps.Add(new { step = "transmittal", result = transmittal ?? "not written", issueRecord = record });

        if (failed.Count > 0)
            return ToolResult.Error($"The {string.Join(" and ", failed)} export failed, so the issue is incomplete (the revision is already on the sheets). Steps: {JsonSerializer.Serialize(steps)}");
        return ToolResult.Ok(new { sheets = sheets.Count, folder, transmittal, issuedWithOpenItems = overrideReason is not null ? record.OpenItems : (int?)null, missingSheetNumbers = missing, steps });
    }
}
