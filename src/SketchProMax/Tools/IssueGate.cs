using System.IO;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SketchProMax.Settings;

namespace SketchProMax.Tools;

/// <summary>
/// The zero-mistake issue gate on Revit's side: runs check_drawings, spec_check, boq_check and check_model (the tools
/// themselves, through <see cref="Drafting.Call"/>) one after the other with progress and Stop between them
/// (<see cref="CancelPoint"/>), maps each finding to the sheets of the issue set through the views on them, and keeps
/// acceptances per model. The rules (grouping, keys, blocking) are in <see cref="IssueGateMath"/>.
///
/// Used by issue_check (the chat), issue_drawings (it runs the gate first) and the ribbon (UI/IssueGateWindow), which
/// hands a run it cleared to the issue_drawings call that follows (<see cref="Clear"/> / <see cref="TakeCleared"/>),
/// so the checks don't run twice.
/// </summary>
internal static class IssueGate
{
    /// <summary>The field test's folder for the per-model files (null: %APPDATA%\SketchProMax\issue-gate).</summary>
    internal static string? FolderOverride;

    /// <summary>The self-test lets issue_drawings go ahead with open items (it issues a throwaway sheet); off otherwise.</summary>
    internal static bool TestOverride;

    /// <summary>Whether "allow issue with open items" is on (Settings; off by default).</summary>
    public static bool OverrideAllowed => TestOverride || AgentSettings.Load().AllowIssueWithOpenItems;

    public static string StorePath(Document doc) => Path.Combine(
        FolderOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax", "issue-gate"),
        Checks.ModelKey(doc) + ".json");

    /// <summary>One run of the gate: the pass/fail list and how each check ran.</summary>
    internal sealed class Run
    {
        public required string ModelKey;
        public required string Model;
        public required List<IssueGateMath.Sheet> Sheets;
        public List<IssueGateMath.GroupResult> Groups = [];
        public List<(string Tool, string Outcome)> Checks = [];
        public List<string> Notes = [];
        public string? Report;
        public DateTime At = DateTime.Now;
        public bool Blocked => IssueGateMath.Blocked(Groups);
        public IEnumerable<IssueGateMath.Item> Open => Groups.SelectMany(g => g.Items).Where(i => i.Status == IssueGateMath.Status.Open);
        public bool Covers(IEnumerable<string> numbers) => new HashSet<string>(numbers, StringComparer.Ordinal).SetEquals(Sheets.Select(s => s.Number));
    }

    /// <summary>The last run (the ribbon window reads it after running issue_check as a job).</summary>
    internal static Run? Last;

    /// <summary>The sheets of an issue set: the numbers given (or every sheet), optionally only those starting with a prefix.</summary>
    public static List<ViewSheet> Sheets(Document doc, IReadOnlyCollection<string> numbers, string? prefix) =>
        new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(s => !s.IsPlaceholder)
            .Where(s => (numbers.Count == 0 || numbers.Contains(s.SheetNumber)) && (prefix is null || s.SheetNumber.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(s => s.SheetNumber).ToList();

    /// <summary>
    /// Runs the four checks for <paramref name="sheets"/>. <paramref name="spec"/> and <paramref name="boq"/> are
    /// remembered for the model when given; otherwise the remembered ones are used. CSVs and the Excel report go to
    /// <paramref name="folder"/>. Throws the bridge's cancel exception when a stop is asked for between checks.
    /// </summary>
    public static Run Check(UIApplication app, IReadOnlyList<ViewSheet> sheets, string? spec, string? boq, string folder)
    {
        var doc = app.ActiveUIDocument.Document;
        var path = StorePath(doc);
        var store = IssueGateStore.Load(path);
        if ((spec is not null && spec != store.SpecChecklist) || (boq is not null && boq != store.BoqFile))
        {
            store.SpecChecklist = spec ?? store.SpecChecklist;
            store.BoqFile = boq ?? store.BoqFile;
            TrySave(store, path);
        }
        spec ??= store.SpecChecklist;
        boq ??= store.BoqFile;
        Directory.CreateDirectory(folder);

        var run = new Run { ModelKey = Checks.ModelKey(doc), Model = doc.Title, Sheets = sheets.Select(s => new IssueGateMath.Sheet(s.SheetNumber, s.Name, s.Id.Value)).ToList() };
        var findings = new List<IssueGateMath.Finding>();
        void Ran(string tool, ToolResult r, List<IssueGateMath.Finding>? found, string? note = null)
        {
            if (r.IsError)
            {
                run.Checks.Add((tool, "could not run: " + r.Text));
                findings.Add(new(tool, "not_run", 0, tool, "Could not run: " + r.Text));
                return;
            }
            findings.AddRange(found ?? []);
            run.Checks.Add((tool, $"ran: {found?.Count ?? 0} finding(s)" + (note is null ? "" : "; " + note)));
        }

        var steps = new Action[]
        {
            () => { var t = new CheckDrawingsTool(); Ran(t.Name, Drafting.Call(app, t, new { folder }), t.Found); },
            () =>
            {
                var t = new SpecCheckTool();
                if (string.IsNullOrWhiteSpace(spec) || !File.Exists(spec))
                {
                    var why = string.IsNullOrWhiteSpace(spec) ? "no spec checklist is set for this model" : $"the spec checklist {spec} is not there";
                    run.Checks.Add((t.Name, "not run: " + why));
                    findings.Add(new(t.Name, "not_run", 0, "spec checklist",
                        $"Not run: {why}. Give spec_checklist (a JSON checklist saved by spec_check save_as), or accept this item with a reason."));
                }
                else Ran(t.Name, Drafting.Call(app, t, new { checklist_file = spec, folder }), t.Found);
            },
            () =>
            {
                var t = new BoqCheckTool();
                var file = !string.IsNullOrWhiteSpace(boq) && File.Exists(boq) ? boq : null;
                if (file is null) run.Notes.Add("BOQ items not compared: no BOQ file is set for this model (boq_file); double counting was checked.");
                Ran(t.Name, Drafting.Call(app, t, file is null ? new { folder } : (object)new { boq_file = file, folder }), t.Found,
                    file is null ? "double counting only (no BOQ file)" : null);
            },
            () => { var t = new CheckModelTool(); Ran(t.Name, Drafting.Call(app, t, new { folder }), t.Found); },
        };
        for (var i = 0; i < steps.Length; i++)
        {
            // Read-only: progress per check on the job card; a stop ends the run between checks (nothing to undo).
            if (CancelPoint.Stop("Issue check", i, steps.Length)) CancelPoint.ThrowIfRequested("The issue check did not finish; nothing was issued.");
            Bridge.Jobs.Report($"Issue check: {IssueGateMath.Tools[i]} ({i + 1} of {steps.Length})");
            steps[i]();
        }
        Bridge.JobFeed.Item("Issue check", steps.Length, steps.Length, "");

        var where = SheetsByView(doc);
        var mapped = findings.Select(f => f with { Sheets = SheetsOf(doc, where, f.ElementId) }).ToList();
        run.Groups = IssueGateMath.Group(mapped, run.Sheets, store.Accepted);
        run.Report = WriteReport(run, folder);
        Last = run;
        return run;
    }

    /// <summary>Accepts items of a run with a reason (kept with the model) and updates the run's list. Returns how many.</summary>
    public static int Accept(Document doc, Run run, IEnumerable<IssueGateMath.Item> items, string reason)
    {
        var path = StorePath(doc);
        var store = IssueGateStore.Load(path);
        var n = IssueGateMath.Accept(store.Accepted, items, reason, Environment.UserName, DateTime.Now);
        store.Save(path);
        run.Groups = IssueGateMath.Regroup(run.Groups, store.Accepted);
        return n;
    }

    /// <summary>Puts accepted items back to open.</summary>
    public static int Unaccept(Document doc, Run run, IEnumerable<string> keys)
    {
        var path = StorePath(doc);
        var store = IssueGateStore.Load(path);
        var n = IssueGateMath.Unaccept(store.Accepted, keys);
        if (n > 0) store.Save(path);
        run.Groups = IssueGateMath.Regroup(run.Groups, store.Accepted);
        return n;
    }

    /// <summary>Adds a line to the model's issue history.</summary>
    public static void Record(Document doc, IssueGateMath.IssueRecord record)
    {
        var path = StorePath(doc);
        var store = IssueGateStore.Load(path);
        store.Issues.Add(record);
        TrySave(store, path);
    }

    private static void TrySave(IssueGateStore store, string path)
    {
        try { store.Save(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* remembered inputs are a convenience */ }
    }

    // ---- Handing a cleared run from the ribbon window to issue_drawings ------------------------------------------------

    private static (Run Run, string? OverrideReason, DateTime At)? _cleared;

    /// <summary>The ribbon window cleared this run (or let it through with a reason): the next issue_drawings on the same sheets uses it.</summary>
    public static void Clear(Run run, string? overrideReason) => _cleared = (run, overrideReason, DateTime.Now);

    /// <summary>The cleared run for these sheets of this model, once, when it is less than ten minutes old.</summary>
    public static (Run Run, string? OverrideReason)? TakeCleared(Document doc, IEnumerable<string> numbers)
    {
        var c = _cleared;
        _cleared = null;
        if (c is not { } v || v.Run.ModelKey != Checks.ModelKey(doc) || !v.Run.Covers(numbers) || DateTime.Now - v.At > TimeSpan.FromMinutes(10)) return null;
        return (v.Run, v.OverrideReason);
    }

    // ---- Mapping and the report ----------------------------------------------------------------------------------------

    /// <summary>Each sheet and each view placed on a sheet, with the sheet numbers it is on (a legend can be on several).</summary>
    private static Dictionary<long, List<string>> SheetsByView(Document doc)
    {
        var map = new Dictionary<long, List<string>>();
        void Add(long id, string number)
        {
            if (!map.TryGetValue(id, out var list)) map[id] = list = [];
            if (!list.Contains(number)) list.Add(number);
        }
        foreach (var s in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(s => !s.IsPlaceholder))
        {
            Add(s.Id.Value, s.SheetNumber);
            foreach (var v in s.GetAllPlacedViews()) Add(v.Value, s.SheetNumber);
        }
        return map;
    }

    /// <summary>
    /// The sheets a finding is on: a sheet itself; a view on sheets; an element drawn in such a view (a dimension, a
    /// text note). Model elements, views on no sheet and findings with no element are model-wide (none).
    /// </summary>
    private static IReadOnlyList<string> SheetsOf(Document doc, Dictionary<long, List<string>> where, long id)
    {
        if (id <= 0) return [];
        if (where.TryGetValue(id, out var direct)) return direct;
        var e = doc.GetElement(new ElementId(id));
        if (e is null || e.OwnerViewId == ElementId.InvalidElementId) return [];
        return where.TryGetValue(e.OwnerViewId.Value, out var owner) ? owner : [];
    }

    private static string? WriteReport(Run run, string folder)
    {
        var file = Path.Combine(folder, $"{run.Model} - Issue check {run.At:yyyy-MM-dd HHmm}.xlsx");
        try
        {
            Xlsx.Write(file, IssueGateMath.ReportSheets(run.Groups, run.Checks));
            return file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            run.Notes.Add($"The Excel report was not written: {ex.Message}");
            return null;
        }
    }

    /// <summary>The chat's result for a run: verdict, sheets with their open items and keys, how the checks ran, the report.</summary>
    public static object Chat(Run run, object? extra = null) => new
    {
        gate = IssueGateMath.Chat(run.Groups),
        checks = run.Checks.Select(c => new { tool = c.Tool, outcome = c.Outcome }),
        notes = run.Notes.Count > 0 ? run.Notes : null,
        report = run.Report,
        extra,
        next = run.Blocked
            ? "Show the user the open items per sheet. For each: fix it in the model and run issue_check again, or (only when the user gives a reason) " +
              "accept it with issue_check accept [{key, reason}]. issue_drawings stays blocked until none is open."
            : "Clear: issue_drawings can go ahead.",
    };
}

public sealed class IssueCheckTool : IRevitTool
{
    public string Name => "issue_check";
    public string Description =>
        "The zero-mistake check before an issue: runs check_drawings, spec_check, boq_check and check_model together and " +
        "gives one pass/fail list per sheet of the issue set (each finding mapped to its sheet through the views on it; " +
        "model-wide findings under 'Model'). The issue is blocked until every open item is fixed (run this again) or accepted " +
        "with the user's reason (accept: key + reason). Acceptances are kept with this model and come back when the finding " +
        "changes. issue_drawings runs this first. The spec checklist and BOQ file are remembered per model. Writes the " +
        "pass/fail report to Excel. Reads the model only.";
    public string PropertiesJson => """
    {
      "sheet_numbers": {"type": "array", "items": {"type": "string"}, "description": "The issue set (default: every sheet)."},
      "number_starts_with": {"type": "string"},
      "spec_checklist": {"type": "string", "description": "spec_check's saved checklist (JSON); remembered for this model."},
      "boq_file": {"type": "string", "description": "BOQ (CSV or .xlsx) for boq_check; remembered for this model. Without one, only double counting is checked."},
      "accept": {"type": "array", "items": {"type": "object", "properties": {"key": {"type": "string"}, "reason": {"type": "string"}}, "required": ["key", "reason"]}, "description": "Open items the user accepts, by key, each with the user's reason."},
      "unaccept": {"type": "array", "items": {"type": "string"}, "description": "Keys of accepted items to open again."},
      "folder": {"type": "string", "description": "Where the report and the checks' CSVs go (default Documents\\SketchProMax\\Issue check)."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var sheets = IssueGate.Sheets(doc, args.StringList("sheet_numbers"), args.OptString("number_starts_with"));
        if (sheets.Count == 0) return ToolResult.Error("No sheets match. Give sheet_numbers or number_starts_with of the issue set.");
        var folder = OutputPaths.Checked(args.OptString("folder")) ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SketchProMax", "Issue check");
        var run = IssueGate.Check(app, sheets, args.OptString("spec_checklist"), args.OptString("boq_file"), folder);

        var unknown = new List<string>();
        var accepted = 0;
        if (args.StringList("unaccept") is { Count: > 0 } reopen) IssueGate.Unaccept(doc, run, reopen);
        var byKey = run.Groups.SelectMany(g => g.Items).GroupBy(i => i.Key).ToDictionary(g => g.Key, g => g.First());
        foreach (var a in args.Array("accept"))
        {
            var key = a.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "";
            var reason = a.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
            if (!byKey.TryGetValue(key, out var item)) { unknown.Add(key); continue; }
            if (string.IsNullOrWhiteSpace(reason)) return ToolResult.Error($"Item {key} needs the user's reason to be accepted. Nothing was accepted.");
            accepted += IssueGate.Accept(doc, run, [item], reason);
        }
        if (accepted > 0 && run.Report is { } report)
        {
            try { Xlsx.Write(report, IssueGateMath.ReportSheets(run.Groups, run.Checks)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { run.Notes.Add($"The report was not updated: {ex.Message}"); }
        }
        return ToolResult.Ok(IssueGate.Chat(run, accepted > 0 || unknown.Count > 0
            ? new { acceptedNow = accepted, unknownKeys = unknown.Count > 0 ? unknown : null }
            : null));
    }
}
