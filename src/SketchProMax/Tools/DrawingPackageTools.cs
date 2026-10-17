using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SketchProMax.Bridge;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

/// <summary>What the two drawing package tools share: their inputs, reading the model, and the plan as shown for approval.</summary>
internal static class PackageInputs
{
    public const string PropertiesJson = """
    {
      "request": {"type": "string", "description": "The package in one sentence, e.g. 'GA plans, sections and column schedule for levels 1-5 on A1 with our title block'. Read best-effort (plain English); any field below wins over what it says."},
      "package": {"type": "string", "enum": ["ga", "structural_ga", "rcp", "custom"], "description": "Picks the defaults: plan kind, tags and sheet prefix (A- or S-)."},
      "levels": {"type": "array", "items": {"type": "string"}, "description": "Level names, numbers ('3'), ranges ('1-5') or 'all' (default)."},
      "views": {"type": "array", "items": {"type": "string", "enum": ["floor_plan", "structural_plan", "ceiling_plan", "section", "elevation"]}},
      "schedules": {"type": "array", "items": {"type": "string"}, "description": "Categories to schedule, e.g. Structural Columns, Structural Framing, Doors."},
      "sections": {"type": "integer", "description": "How many building sections (default 2 when sections are asked for)."},
      "scale": {"type": "integer", "description": "e.g. 100 for 1:100 (default)."},
      "sheet_size": {"type": "string", "enum": ["A0", "A1", "A2", "A3", "A4"], "description": "Default A1."},
      "title_block": {"type": "string", "description": "'our' (default): the title block this model's sheets use for that size; or a title block name."},
      "sheet_prefix": {"type": "string", "description": "Default A- (S- for structural plans)."},
      "first_number": {"type": "integer", "description": "First drawing number (default 101; the register takes the one before). Numbers already taken are stepped over."},
      "tags": {"type": "array", "items": {"type": "string"}, "description": "Categories to tag in every plan; an empty list for none. Default: doors, windows, rooms (GA) or columns and framing (structural)."},
      "dimensions": {"type": "boolean", "description": "Grid dimensions in each GA or structural plan (default true)."},
      "view_template": {"type": "string", "description": "A plan view template applied to the plans."},
      "title_block_fields": {"type": "object", "additionalProperties": {"type": "string"}, "description": "Fields filled on every package sheet, e.g. {\"Drawn By\": \"AM\"}."},
      "issue_sheet": {"type": "string", "enum": ["register", "transmittal", "none"], "description": "The last stage: a drawing register sheet (default) or a transmittal, each with an Excel copy."}
    }
    """;

    public static DrawingPackage.Facts Facts(Document doc)
    {
        var facts = new DrawingPackage.Facts();
        foreach (var l in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.ProjectElevation))
            facts.Levels.Add(new(l.Name, Math.Round(l.ProjectElevation * 304.8, 1)));
        var bySheet = TitleBlocks.BySheet(doc);
        var used = bySheet.Values.SelectMany(l => l).GroupBy(i => i.Symbol.Id.Value).ToDictionary(g => g.Key, g => g.Count());
        foreach (var s in TitleBlocks.Types(doc))
        {
            double Mm(BuiltInParameter p) => s.get_Parameter(p) is { StorageType: StorageType.Double } v ? Math.Round(v.AsDouble() * 304.8, 1) : 0;
            facts.TitleBlocks.Add(new(TypeMatch.Label(s), Mm(BuiltInParameter.SHEET_WIDTH), Mm(BuiltInParameter.SHEET_HEIGHT), used.TryGetValue(s.Id.Value, out var n) ? n : 0));
        }
        foreach (var s in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()) facts.SheetNumbers.Add(s.SheetNumber);
        foreach (var v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()) facts.ViewNames.Add(v.Name);
        foreach (var t in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => v.IsTemplate)) facts.ViewTemplates.Add(t.Name);
        facts.Grids = new FilteredElementCollector(doc).OfClass(typeof(Grid)).GetElementCount();
        var families = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().Select(t => t.ViewFamily).ToHashSet();
        facts.StructuralPlanType = families.Contains(ViewFamily.StructuralPlan);
        facts.CeilingPlanType = families.Contains(ViewFamily.CeilingPlan);
        return facts;
    }

    public static DrawingPackage.Plan Plan(Document doc, ToolArgs args) =>
        DrawingPackage.Resolve(DrawingPackage.FromArgs(args.Raw), Facts(doc));

    public static List<TimeRate> Rates()
    {
        try { return Settings.AgentSettings.Load().TimeRates ?? []; }
        catch (Exception) { return []; }
    }

    public const string UndoText =
        "The whole package is one Undo step: each stage runs in its own transaction group inside one group for the package, " +
        "so Undo (or Undo all on the job card) takes all of it back, and nothing that was in the model before is changed or deleted. " +
        "Steps you do from a hand-off afterwards are Undo steps of their own.";

    /// <summary>The plan as shown for approval: what was read, what will be made, the counts and the stages.</summary>
    public static object Show(DrawingPackage.Plan plan, IEnumerable<TimeRate> rates)
    {
        var s = plan.Spec;
        var names = plan.Views.ToDictionary(v => v.Key, v => v.Name);
        var drawings = plan.Sheets.Where(x => !x.Register).ToList();
        return new
        {
            ok = plan.Ok,
            errors = plan.Errors.Count > 0 ? plan.Errors : null,
            request = s.Request.Length > 0 ? s.Request : null,
            read = s.Read.Count > 0 ? s.Read : null,
            plan = new
            {
                package = s.Package,
                levels = plan.Levels.Select(l => l.Name).ToList(),
                views = s.Views,
                schedules = s.Schedules,
                sections = s.Sections,
                scale = $"1:{s.Scale}",
                sheet_size = s.SheetSize,
                title_block = plan.TitleBlock ?? "(none yet: you are asked to load one, then the sheets are made)",
                numbering = drawings.Count == 0 ? "" : drawings.Count == 1 ? drawings[0].Number : $"{drawings[0].Number} to {drawings[^1].Number}",
                register = plan.Register?.Number,
                tags = plan.Tags.Select(t => t.Category).Distinct().ToList(),
                dimensions = plan.DimensionViews.Count > 0,
                view_template = s.ViewTemplate,
                title_block_fields = s.TitleBlockFields.Count > 0 ? s.TitleBlockFields : null,
                issue_sheet = s.IssueSheet,
            },
            counts = plan.Counts(),
            stages = plan.StageItems().Select((st, i) => new { stage = $"{i + 1}. {st.Stage}", items = st.Items }).ToList(),
            sheets = plan.Sheets.Take(80).Select(x => new { number = x.Number, name = x.Name, views = x.Views.Select(k => names[k]).ToList() }).ToList(),
            byHandEstimate = TimeMath.Duration(DrawingPackage.PlannedByHandMinutes(plan, rates) * 60) + " (estimate)",
            warnings = plan.Warnings.Count > 0 ? plan.Warnings : null,
            undo = UndoText,
            next = plan.Ok
                ? "Show the user this plan with its counts and ask before making it. When they say yes, call drawing_package with the same inputs and approved true."
                : "Fix what errors says (or ask the user), then plan again.",
        };
    }
}

public sealed class GetDrawingPackagePlanTool : IRevitTool
{
    public string Name => "get_drawing_package_plan";
    public string Description =>
        "Plan a drawing package from one sentence or from structured fields, without changing anything: which plans, sections, " +
        "elevations and schedules for which levels, the sheets with their numbers and title block, the tags and grid dimensions, " +
        "the issue sheet, the counts of each, and a by-hand time estimate. Levels, title blocks, sheet numbers and view names are " +
        "checked against the model (taken numbers and names are stepped over). Show it to the user for approval, then run drawing_package.";
    public string PropertiesJson => PackageInputs.PropertiesJson;
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var plan = PackageInputs.Plan(app.ActiveUIDocument.Document, args);
        var shown = ToolResult.Ok(PackageInputs.Show(plan, PackageInputs.Rates()));
        return plan.Ok ? shown : shown with { IsError = true };
    }
}

public sealed class DrawingPackageTool : IRevitTool
{
    public string Name => "drawing_package";
    public string Description =>
        "Make a drawing package from one sentence ('GA plans, sections and column schedule for levels 1-5 on A1 with our title block') " +
        "or structured fields, as one checked pipeline: views, grid dimensions, tags, sheets with title blocks, title block fields, " +
        "then an issue sheet (drawing register or transmittal, plus an Excel copy) and a time-saved summary. Each stage is checked " +
        "(views exist, every view placed, nothing overlaps on a sheet, no orphan tags; check_drawings at the end). Pause, Skip step " +
        "(skips the current stage) and Stop work on the job card; the package is one Undo step and never changes or deletes existing " +
        "views or sheets. What only the user can do (load a title block or a tag family, fit a view too big for the sheet) is handed " +
        "to them. Plan first with get_drawing_package_plan and get the user's yes; without approved true Revit asks them with the counts.";
    public string PropertiesJson => PackageInputs.PropertiesJson.TrimEnd().TrimEnd('}').TrimEnd() + """
      ,
      "approved": {"type": "boolean", "description": "True when the user has seen the plan and its counts and said yes. Otherwise Revit shows them the plan first."},
      "folder": {"type": "string", "description": "Where the Excel register and the drawing check go (default Documents\\SketchProMax)."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => true;

    /// <summary>The job card's name for a stage's batch (the field test watches for it).</summary>
    internal static string BatchName(int stage, string name) => $"Drawing package {stage}/{DrawingPackage.Stages.Length}: {name}";

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var rates = PackageInputs.Rates();
        var plan = PackageInputs.Plan(doc, args);
        if (!plan.Ok)
            return ToolResult.Error("The package can't be made as asked; nothing was changed. " + string.Join(" ", plan.Errors) +
                                    " Plan it with get_drawing_package_plan and fix these first.");
        if (doc.IsModifiable) return ToolResult.Error("A transaction is already open; run drawing_package on its own.");
        if (!args.OptBool("approved") && !Ask(plan, rates)) return ToolResult.Error("The user did not approve the package in Revit; nothing was changed. Ask them what to change.");
        var folder = OutputPaths.Checked(args.OptString("folder")) ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SketchProMax");
        return new PackageRun(app, doc, plan, folder, rates).Run(Name);
    }

    /// <summary>Shows the plan and its counts in Revit and asks to make it (when Claude didn't say the user approved it).</summary>
    private static bool Ask(DrawingPackage.Plan plan, List<TimeRate> rates)
    {
        // Tests and recipes answer for themselves: they always pass approved.
        if (SelfTest.IsRunning || StartupTimings.TestRunning) return true;
        var drawings = plan.Sheets.Where(s => !s.Register).ToList();
        var lines = new List<string>
        {
            $"Levels: {string.Join(", ", plan.Levels.Select(l => l.Name))}",
            $"Views: {plan.Views.Count(v => v.Kind != "schedule")} ({plan.Views.Count(v => v.Kind.EndsWith("_plan", StringComparison.Ordinal))} plans, {plan.Count("section")} sections, {plan.Count("elevation")} elevations), schedules: {plan.Count("schedule")}",
            $"Sheets: {plan.Sheets.Count}" + (drawings.Count > 0 ? $" ({drawings[0].Number} to {drawings[^1].Number}" + (plan.Register is { } r ? $", register {r.Number})" : ")") : ""),
            $"Title block: {plan.TitleBlock ?? "none loaded yet (you will be asked to load it)"}, sheet size {plan.Spec.SheetSize}, scale 1:{plan.Spec.Scale}",
            $"Tags: {(plan.Tags.Count == 0 ? "none" : string.Join(", ", plan.Tags.Select(t => t.Category).Distinct()))}; grid dimensions: {(plan.DimensionViews.Count > 0 ? "yes" : "no")}",
            $"By hand this would take about {TimeMath.Duration(DrawingPackage.PlannedByHandMinutes(plan, rates) * 60)} (estimate).",
        };
        lines.AddRange(plan.Warnings.Select(w => "Note: " + w));
        var dialog = new TaskDialog("Sketch ProMax")
        {
            MainInstruction = "Make this drawing package?",
            MainContent = string.Join("\n", lines) + "\n\nIt is one Undo step, and existing views and sheets are not changed.",
            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
            DefaultButton = TaskDialogResult.No,
        };
        return dialog.Show() == TaskDialogResult.Yes;
    }
}

/// <summary>
/// One run of a drawing package: the stages in order, each an <see cref="ItemBatch.RunGrouped{T}"/> of existing tool calls
/// (create_plan_views, create_section, create_elevations, create_schedule, dimension_grids, tag_elements_in_view,
/// create_sheets, fill_title_blocks, create_sheet_list) and a check after it, all inside one transaction group.
/// Skip step on the job card ends the current stage and the package goes on; Stop ends it after the item in progress.
/// </summary>
internal sealed class PackageRun(UIApplication app, Document doc, DrawingPackage.Plan plan, string folder, List<TimeRate> rates)
{
    private sealed class StageReport(string name, int items)
    {
        public string Name = name;
        public int Items = items, Done, Failed;
        public string Status = "done";
        public List<object> Failures = [];
        public bool? CheckOk;
        public string Check = "";
        public double Seconds, ByHandMinutes;
    }

    private readonly Dictionary<string, long> _views = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _sheets = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<StageReport> _stages = [];
    private readonly List<HandoffItem> _handoffs = [];
    private readonly List<string> _notes = [];
    private bool _stopped;
    private int _tags, _dimensions;
    private List<(double X0, double Y0, double X1, double Y1, double Depth)>? _sectionLines;
    private List<long>? _wrap;
    private const double Ft = 304.8;

    public ToolResult Run(string tool)
    {
        var watch = Stopwatch.StartNew();
        if (plan.TitleBlock is not null)
            TitleBlockArea.Prepare(doc, TitleBlocks.Types(doc).FirstOrDefault(t => TypeMatch.Label(t) == plan.TitleBlock));

        var kept = false;
        using (var group = new TransactionGroup(doc, "Sketch ProMax: Drawing package"))
        {
            group.Start();
            JobUndo.GroupOpened();
            try
            {
                Stage(1, plan.Views, v => v.Name, MakeView, CheckViews, r =>
                    DrawingPackage.ByHandMinutes("create_plan_views", Made(k => k.EndsWith("_plan", StringComparison.Ordinal)), rates)
                    + DrawingPackage.ByHandMinutes("create_section", Made(k => k == "section"), rates)
                    + DrawingPackage.ByHandMinutes("create_elevations", Made(k => k == "elevation"), rates)
                    + DrawingPackage.ByHandMinutes("create_schedule", Made(k => k == "schedule"), rates));
                Stage(2, plan.DimensionViews.Where(_views.ContainsKey).ToList(), Name, Dimension, CheckDimensions,
                    _ => DrawingPackage.ByHandMinutes("dimension_grids", _dimensions, rates));
                Stage(3, plan.Tags.Where(t => _views.ContainsKey(t.View)).ToList(), t => $"{t.Category} in {Name(t.View)}", Tag, CheckTags,
                    _ => DrawingPackage.ByHandMinutes("tag_elements_in_view", _tags, rates));
                // A sheet whose views were all skipped or failed is not made (it would only be an empty sheet).
                var drawingSheets = plan.Sheets.Where(s => !s.Register && s.Views.Any(_views.ContainsKey)).ToList();
                var empty = plan.Sheets.Count(s => !s.Register) - drawingSheets.Count;
                if (empty > 0) _notes.Add($"{empty} sheet(s) not made: none of their views was made.");
                if (plan.TitleBlock is null && drawingSheets.Count > 0 && !_stopped)
                {
                    _stages.Add(new StageReport(DrawingPackage.Stages[3], drawingSheets.Count) { Status = "waiting for you: " + plan.TitleBlockMissing });
                    HandTitleBlock(drawingSheets);
                }
                else
                    Stage(4, drawingSheets, s => $"{s.Number} {s.Name}", MakeSheet, CheckSheets, r =>
                        DrawingPackage.ByHandMinutes("create_sheets", r.Done, rates) + DrawingPackage.ByHandMinutes("place_views_on_sheet", PlacedCount(), rates));
                var fill = plan.Spec.TitleBlockFields.Count > 0 && _sheets.Count > 0 ? new List<int> { 0 } : [];
                Stage(5, fill, _ => $"{plan.Spec.TitleBlockFields.Count} field(s) on {_sheets.Count} sheet(s)", _ => Fill(), null,
                    r => DrawingPackage.ByHandMinutes("fill_title_blocks", r.Done > 0 ? _sheets.Count : 0, rates));
                var register = plan.Register is { } reg && plan.TitleBlock is not null && _sheets.Count > 0 ? new List<DrawingPackage.SheetItem> { reg } : [];
                if (plan.Register is not null && plan.TitleBlock is null && !_stopped)
                    _stages.Add(new StageReport(DrawingPackage.Stages[5], 1) { Status = "waiting for you: made with the sheets once a title block is loaded" });
                else Stage(6, register, s => $"{s.Number} {s.Name}", MakeRegister, CheckRegister, r =>
                    r.Done == 0 ? 0 : DrawingPackage.ByHandMinutes("create_sheet_list", 1, rates) + DrawingPackage.ByHandMinutes("export_drawing_list", 1, rates));
                kept = _views.Count + _sheets.Count > 0 || _stages.Any(s => s.Done > 0);
                if (kept) group.Assimilate();
                else group.RollBack();
            }
            finally
            {
                if (group.HasStarted() && !group.HasEnded()) group.RollBack();
                JobUndo.GroupClosed(kept);
            }
        }
        if (!kept)
        {
            _views.Clear();
            _sheets.Clear();
        }

        // Read-only from here: the drawing check on what was made, the Excel register and the time saved.
        var findings = DrawingCheck();
        string? excel = null;
        if (!_stopped && plan.Spec.IssueSheet != "none" && _sheets.Count > 0)
        {
            try { excel = WriteExcel(findings); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _notes.Add($"The Excel register was not written ({ex.Message.Trim()})."); }
        }
        var took = watch.Elapsed.TotalSeconds;
        var planning = DrawingPackage.ByHandMinutes("get_drawing_package_plan", 1, rates);
        var byHand = planning + _stages.Sum(s => s.ByHandMinutes);
        var problems = _stages.Sum(s => s.Failed) + _stages.Count(s => s.CheckOk == false) + findings.Count(f => f.Problem);
        var names = plan.Views.ToDictionary(v => v.Key, v => v.Name);

        var result = ToolResult.Ok(new
        {
            status = _stopped ? "stopped by the user: what was finished is kept" : problems > 0 || _handoffs.Count > 0 ? "done, with points to look at" : "done",
            counts = new
            {
                views = _views.Count, sheets = _sheets.Count, tags = _tags, dimensionStrings = _dimensions,
                planned = plan.Counts(),
            },
            stages = _stages.Select((s, i) => new
            {
                stage = $"{i + 1}. {s.Name}", status = s.Status, items = s.Items, done = s.Done, failed = s.Failed,
                check = s.CheckOk is null ? null : (s.CheckOk == true ? "pass: " : "FAIL: ") + s.Check,
                failures = s.Failures.Count > 0 ? s.Failures : null,
                seconds = Math.Round(s.Seconds, 1),
            }).ToList(),
            sheets = plan.Sheets.Where(s => _sheets.ContainsKey(s.Number)).Take(100).Select(s => new
            {
                number = s.Number, name = s.Name, id = _sheets[s.Number], views = s.Views.Where(_views.ContainsKey).Select(k => names[k]).ToList(),
            }).ToList(),
            views = plan.Views.Where(v => _views.ContainsKey(v.Key)).Take(150).Select(v => new { id = _views[v.Key], name = v.Name, kind = v.Kind }).ToList(),
            drawingCheck = findings.Count == 0 ? "check_drawings found nothing on the package's views and sheets" : null,
            drawingCheckFindings = findings.Count > 0 ? findings.Take(40).Select(f => new { check = f.Check, where = f.Where, id = f.Id, issue = f.Issue, problem = f.Problem }).ToList() : null,
            issue = plan.Register is { } r && _sheets.TryGetValue(r.Number, out var regId) ? new { sheet = r.Number, id = regId, excel } : null,
            timeSaved = new
            {
                line = TimeMath.Line(took, byHand * 60),
                tookSeconds = Math.Round(took, 1),
                byHandMinutes = Math.Round(byHand, 1),
                savedMinutes = Math.Round(TimeMath.Saved(byHand * 60, took) / 60, 1),
                basis = "By-hand estimate: each stage's tool rate (Settings > time rates) times what it made, plus planning the set; rates are estimates unless set in Settings.",
                byStage = _stages.Where(s => s.ByHandMinutes > 0).Select(s => new { stage = s.Name, byHandMinutes = Math.Round(s.ByHandMinutes, 1), aiSeconds = Math.Round(s.Seconds, 1) }).ToList(),
            },
            handoffSteps = _handoffs.Count > 0 ? _handoffs.Select(h => h.Step.Text).ToList() : null,
            notes = _notes.Count > 0 ? _notes : null,
            warnings = plan.Warnings.Count > 0 ? plan.Warnings : null,
            undo = kept ? PackageInputs.UndoText : "Nothing was made, so nothing needs undoing.",
            next = _stopped ? "Say briefly what was made and what wasn't; don't start more steps."
                : "Report the stages and the time-saved line in a few lines. To issue: issue_drawings for these sheet numbers.",
        });
        // The self-test runs on the user's model and rolls it back: steps are listed, never asked.
        return SelfTest.IsRunning ? result : Handoff.Finish(tool, result, _handoffs);
    }

    // ---- Stages ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Runs one stage as an item batch, then its check. Skip step during it ends this stage only (the skip is used up
    /// here, so the next stage runs); Stop or cancel_job ends the package after it.
    /// </summary>
    private void Stage<T>(int number, List<T> items, Func<T, string> label, Func<T, object?> apply, Func<(bool Ok, string Text)>? check,
        Func<ItemBatch.Result, double> byHand)
    {
        var report = new StageReport(DrawingPackage.Stages[number - 1], items.Count);
        _stages.Add(report);
        if (_stopped) { report.Status = "not run: the package was stopped"; return; }
        if (items.Count == 0) { report.Status = "nothing to do"; return; }
        var watch = Stopwatch.StartNew();
        var result = ItemBatch.RunGrouped(doc, DrawingPackageTool.BatchName(number, report.Name), items, label, apply);
        report.Seconds = watch.Elapsed.TotalSeconds;
        // An item rolled back (Stop while it ran, or a failure after its view was made) leaves an id that is gone.
        foreach (var gone in _views.Where(p => doc.GetElement(new ElementId(p.Value)) is null).Select(p => p.Key).ToList()) _views.Remove(gone);
        foreach (var gone in _sheets.Where(p => doc.GetElement(new ElementId(p.Value)) is null).Select(p => p.Key).ToList()) _sheets.Remove(gone);
        report.Done = result.Done;
        report.Failed = result.Items.Count(i => !i.Ok);
        report.Failures = result.Failed.Take(20).Select(f => (object)new { item = f.Item, reason = f.Error }).ToList();
        var control = JobControl.Tool;
        if (control?.State == JobState.Skipping)
        {
            control.StepEnded();
            report.Status = result.Items.Any(i => i.Error?.StartsWith("not done:", StringComparison.Ordinal) == true)
                ? "skipped by the user (Skip step): what it finished is kept, the rest of this stage was not done"
                : "done (Skip step came as it finished; the next stage ran)";
        }
        else if (control?.State == JobState.Stopping || Jobs.CancelRequested)
        {
            _stopped = true;
            report.Status = "stopped by the user: the item in progress was rolled back, finished items are kept";
        }
        else report.Status = report.Failed > 0 ? $"done, {report.Failed} item(s) not made" : "done";
        report.ByHandMinutes = byHand(result);
        if (check is not null && report.Done > 0)
        {
            var (ok, text) = check();
            report.CheckOk = ok;
            report.Check = text;
        }
    }

    /// <summary>How many views of these kinds were made.</summary>
    private int Made(Func<string, bool> kind) =>
        plan.Views.Where(v => kind(v.Kind)).Count(v => _views.ContainsKey(v.Key));

    private string Name(string key) => plan.Views.FirstOrDefault(v => v.Key == key)?.Name ?? key;

    /// <summary>A tool called as the self-test calls it; an error result fails the item with the tool's message.</summary>
    private JsonElement Call(IRevitTool tool, object args)
    {
        var dict = JsonSerializer.SerializeToElement(args).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        var r = tool.Execute(app, new ToolArgs(dict));
        if (r.IsError) throw new InvalidOperationException(r.Text.Length > 600 ? r.Text.Substring(0, 600) + "…" : r.Text);
        using var json = JsonDocument.Parse(r.Text);
        return json.RootElement.Clone();
    }

    /// <summary>Runs <paramref name="body"/>; a step only the user can do becomes a hand-off that runs it again after.</summary>
    private object? HandingOff(Func<object?> body)
    {
        try { return body(); }
        catch (ManualStepException manual)
        {
            _handoffs.Add(new HandoffItem(manual.Step, body));
            throw;
        }
    }

    // 1. Views
    private object? MakeView(DrawingPackage.ViewItem v) => HandingOff(() =>
    {
        long id;
        var s = plan.Spec;
        switch (v.Kind)
        {
            case "floor_plan" or "structural_plan" or "ceiling_plan":
            {
                var kind = v.Kind switch { "structural_plan" => "structural", "ceiling_plan" => "ceiling", _ => "floor" };
                var r = Call(new CreatePlanViewsTool(), new { levels = new[] { v.Level }, kind, name_pattern = v.Name, scale = s.Scale, view_template = s.ViewTemplate });
                id = r.GetProperty("created")[0].GetProperty("id").GetInt64();
                break;
            }
            case "section":
            {
                var lines = _sectionLines ??= SectionLines();
                if (v.Index >= lines.Count) throw new InvalidOperationException("The model has nothing to cut a section through.");
                var (x0, y0, x1, y1, depth) = lines[v.Index];
                double U(double ft) => RevitHelpers.FromInternal(doc, ft);
                var r = Call(new CreateSectionTool(), new { start = new[] { U(x0), U(y0) }, end = new[] { U(x1), U(y1) }, depth = U(depth), name = v.Name, scale = s.Scale });
                id = r.GetProperty("id").GetInt64();
                break;
            }
            case "elevation":
            {
                var host = plan.Views.Where(p => p.Kind is "floor_plan" or "structural_plan" && _views.ContainsKey(p.Key)).Select(p => (long?)_views[p.Key]).FirstOrDefault();
                var r = Call(new CreateElevationsTool(), new { mode = "building", sides = new[] { v.Side }, plan_view_id = host, element_ids = _wrap ??= WrapIds(), name_pattern = v.Name, scale = s.Scale });
                var made = r.GetProperty("views");
                if (made.GetArrayLength() == 0) throw new InvalidOperationException($"Revit made no {v.Side} elevation.");
                id = made[0].GetProperty("id").GetInt64();
                break;
            }
            default:
            {
                var r = Call(new CreateScheduleTool(), new { category = v.Category, name = v.Name, fields = DrawingPackage.ScheduleFields(v.Category!), sort_by_first_field = true });
                id = r.GetProperty("scheduleId").GetInt64();
                if (r.TryGetProperty("missingFields", out var missing) && missing.GetArrayLength() > 0)
                    _notes.Add($"{v.Name}: fields not in this model left out ({string.Join(", ", missing.EnumerateArray().Select(m => m.GetString()))}).");
                break;
            }
        }
        _views[v.Key] = id;
        return id;
    });

    /// <summary>The elements the building is: walls, columns, framing, floors and foundations (grids when there are none).</summary>
    private List<Element> Building()
    {
        var cats = new ElementMulticategoryFilter(new List<BuiltInCategory>
        {
            BuiltInCategory.OST_Walls, BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_Columns,
            BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_Floors, BuiltInCategory.OST_StructuralFoundation,
        });
        var list = new FilteredElementCollector(doc).WherePasses(cats).WhereElementIsNotElementType().ToList();
        return list.Count > 0 ? list : new FilteredElementCollector(doc).OfClass(typeof(Grid)).ToList();
    }

    private List<(double, double, double, double, double)> SectionLines()
    {
        var box = ViewKit.Extents(doc, Building());
        if (box is null) return [];
        return DrawingPackage.SectionLines(box.Min.X, box.Min.Y, box.Max.X, box.Max.Y, plan.Spec.Sections, 1000 / Ft);
    }

    private List<long> WrapIds() => Building().Take(5000).Select(e => e.Id.Value).ToList();

    private (bool, string) CheckViews()
    {
        var missing = _views.Where(p => doc.GetElement(new ElementId(p.Value)) is not View).Select(p => Name(p.Key)).ToList();
        var notMade = plan.Views.Count(v => !_views.ContainsKey(v.Key));
        return (missing.Count == 0,
            $"{_views.Count - missing.Count} of {plan.Views.Count} views exist" + (notMade > 0 ? $" ({notMade} not made)" : "") +
            (missing.Count > 0 ? $"; missing after the stage: {string.Join(", ", missing.Take(10))}" : ""));
    }

    // 2. Dimensions
    private object? Dimension(string key) => HandingOff(() =>
    {
        var r = Call(new DimensionGridsTool(), new { view_id = _views[key] });
        var n = r.GetProperty("dimensionStrings").GetInt32();
        _dimensions += n;
        return n;
    });

    private (bool, string) CheckDimensions()
    {
        var bare = plan.DimensionViews.Where(_views.ContainsKey)
            .Where(k => new FilteredElementCollector(doc, new ElementId(_views[k])).OfClass(typeof(Dimension)).GetElementCount() == 0).Select(Name).ToList();
        return (bare.Count == 0, $"{_dimensions} grid dimension string(s)" + (bare.Count > 0 ? $"; no dimension in {string.Join(", ", bare.Take(10))}" : " in every plan"));
    }

    // 3. Tags
    private object? Tag(DrawingPackage.TagItem t) => HandingOff(() =>
    {
        try
        {
            var r = Call(new TagElementsInViewTool(), new { category = t.Category, view_id = _views[t.View] });
            var n = r.GetProperty("tagged").GetInt32();
            _tags += n;
            return n;
        }
        catch (InvalidOperationException ex) when (ex.Message.IndexOf("Load a tag family", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            // Only the user can load the office's tag family; Continue tags this view again.
            _handoffs.Add(new HandoffItem(new HandoffStep
            {
                What = $"Load a tag family for {t.Category}",
                Where = "Insert > Load Family (or Load Autodesk Family), pick the office's tag for that category",
                Why = $"the model has no {t.Category} tag type, and a tag family can't be made from nothing",
                Kind = "missing_family",
                Item = $"{t.Category} in {Name(t.View)}",
            }, () =>
            {
                var again = Call(new TagElementsInViewTool(), new { category = t.Category, view_id = _views[t.View] });
                _tags += again.GetProperty("tagged").GetInt32();
                return again.GetProperty("tagged").GetInt32();
            }));
            throw;
        }
    });

    private (bool, string) CheckTags()
    {
        var orphans = new List<string>();
        var total = 0;
        foreach (var key in plan.Tags.Select(t => t.View).Distinct().Where(_views.ContainsKey))
            foreach (var tag in new FilteredElementCollector(doc, new ElementId(_views[key])).OfClass(typeof(IndependentTag)).Cast<IndependentTag>())
            {
                total++;
                if (tag.IsOrphaned) orphans.Add($"{tag.Id.Value} in {Name(key)}");
            }
        return (orphans.Count == 0, $"{_tags} tag(s) placed, {total} in the package's plans" + (orphans.Count > 0 ? $"; orphan tags: {string.Join(", ", orphans.Take(10))}" : ", no orphans"));
    }

    // 4. Sheets
    private object? MakeSheet(DrawingPackage.SheetItem s) => HandingOff(() => CreateSheet(s, plan.TitleBlock!));

    private long CreateSheet(DrawingPackage.SheetItem s, string titleBlock)
    {
        var ids = s.Views.Where(_views.ContainsKey).Select(k => _views[k]).ToArray();
        var r = Call(new CreateSheetsTool(), new { sheets = new[] { new { number = s.Number, name = s.Name, title_block = titleBlock, view_ids = ids } } })[0];
        if (r.TryGetProperty("error", out var error)) throw new InvalidOperationException($"{s.Number}: {error.GetString()}");
        var id = r.GetProperty("sheetId").GetInt64();
        _sheets[s.Number] = id;
        if (r.TryGetProperty("skipped", out var skipped) && skipped.GetArrayLength() > 0)
            _notes.Add($"{s.Number}: {skipped.GetArrayLength()} view(s) not placed ({skipped[0].GetRawText()}).");
        return id;
    }

    /// <summary>No title block is loaded (or the named one isn't): the user loads it, then Continue makes the sheets and the register.</summary>
    private void HandTitleBlock(List<DrawingPackage.SheetItem> sheets)
    {
        string? Found()
        {
            var facts = PackageInputs.Facts(doc);
            return DrawingPackage.PickTitleBlock(plan.Spec.TitleBlock, plan.Spec.SheetSize, facts.TitleBlocks, [], out _);
        }
        _handoffs.Add(new HandoffItem(new HandoffStep
        {
            What = plan.Spec.TitleBlock is { Length: > 0 } named && named != "our" ? $"Load the title block '{named}'" : $"Load your {plan.Spec.SheetSize} title block family",
            Where = "Insert > Load Family, then pick the title block .rfa (Annotations > Title Blocks in the Autodesk library)",
            Why = plan.TitleBlockMissing ?? "no title block is loaded",
            Kind = "missing_family",
            Item = "title block",
            Recheck = () => Found() is not null,
        }, () =>
        {
            var tb = Found() ?? throw new InvalidOperationException("No title block is loaded yet.");
            var made = new List<string>();
            foreach (var s in sheets.Concat(plan.Register is { } r ? [r] : []))
            {
                if (s.Register) MakeRegisterWith(s, tb);
                else CreateSheet(s, tb);
                made.Add(s.Number);
            }
            return new { sheets = made, title_block = tb };
        }));
    }

    private int PlacedCount() =>
        new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>().Count(vp => _views.ContainsValue(vp.ViewId.Value))
        + new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>().Count(si => _views.ContainsValue(si.ScheduleId.Value));

    /// <summary>Every view of the made sheets is placed, nothing overlaps, and nothing was parked beside a sheet.</summary>
    private (bool, string) CheckSheets()
    {
        var placed = new HashSet<long>(new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>().Select(vp => vp.ViewId.Value)
            .Concat(new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>().Select(si => si.ScheduleId.Value)));
        var unplaced = plan.Sheets.Where(s => _sheets.ContainsKey(s.Number)).SelectMany(s => s.Views).Where(_views.ContainsKey)
            .Where(k => !placed.Contains(_views[k])).Select(Name).ToList();
        var overlaps = new List<string>();
        var off = new List<string>();
        foreach (var s in plan.Sheets.Where(s => !s.Register && _sheets.ContainsKey(s.Number)))
        {
            if (doc.GetElement(new ElementId(_sheets[s.Number])) is not ViewSheet sheet) continue;
            var boxes = Boxes(sheet);
            foreach (var (a, b) in DrawingPackage.Overlaps(boxes)) overlaps.Add($"{a} and {b} on {s.Number}");
            if (Extents(sheet) is not { } frame) continue;
            foreach (var (label, box) in boxes.Where(b => DrawingPackage.OffSheet(b.Box, frame)))
            {
                off.Add($"{label} on {s.Number}");
                var number = s.Number;
                var view = label;
                _handoffs.Add(new HandoffItem(new HandoffStep
                {
                    What = $"Make '{view}' fit on sheet {number}",
                    Where = $"Open sheet {number}: change the view's scale or crop region, then drag the viewport inside the frame",
                    Why = $"at 1:{plan.Spec.Scale} it is larger than the free drawing area of the title block, so it was put beside the sheet",
                    Kind = "manual",
                    Item = $"{view} on {number}",
                    RunAgain = false,
                    Recheck = () => doc.GetElement(new ElementId(_sheets[number])) is ViewSheet again && Extents(again) is { } f &&
                                    Boxes(again).Where(x => x.Label == view).All(x => !DrawingPackage.OffSheet(x.Box, f)),
                }, null));
            }
        }
        var problems = new List<string>();
        if (unplaced.Count > 0) problems.Add($"not placed: {string.Join(", ", unplaced.Take(10))}");
        if (overlaps.Count > 0) problems.Add($"overlapping: {string.Join("; ", overlaps.Take(10))}");
        if (off.Count > 0) problems.Add($"too big for the sheet (handed to you): {string.Join(", ", off.Take(10))}");
        return problems.Count == 0
            ? (true, $"{_sheets.Count} sheet(s): every view placed, nothing overlaps, nothing off the sheet")
            : (false, string.Join("; ", problems));
    }

    /// <summary>Each viewport (with its title) and schedule on the sheet, by view name, in sheet coordinates.</summary>
    private List<(string Label, LayoutMath.Rect Box)> Boxes(ViewSheet sheet)
    {
        var list = new List<(string, LayoutMath.Rect)>();
        foreach (var vpId in sheet.GetAllViewports())
        {
            if (doc.GetElement(vpId) is not Viewport vp) continue;
            var o = vp.GetBoxOutline();
            var r = new LayoutMath.Rect(o.MinimumPoint.X, o.MinimumPoint.Y, o.MaximumPoint.X, o.MaximumPoint.Y);
            try
            {
                if (vp.GetLabelOutline() is { IsEmpty: false } l)
                    r = new LayoutMath.Rect(Math.Min(r.X0, l.MinimumPoint.X), Math.Min(r.Y0, l.MinimumPoint.Y), Math.Max(r.X1, l.MaximumPoint.X), Math.Max(r.Y1, l.MaximumPoint.Y));
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { } // no title shown
            list.Add(((doc.GetElement(vp.ViewId) as View)?.Name ?? vp.ViewId.Value.ToString(), r));
        }
        foreach (var si in new FilteredElementCollector(doc, sheet.Id).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>())
            if (!si.IsTitleblockRevisionSchedule && si.get_BoundingBox(sheet) is { } b)
                list.Add(((doc.GetElement(si.ScheduleId) as View)?.Name ?? si.ScheduleId.Value.ToString(), new LayoutMath.Rect(b.Min.X, b.Min.Y, b.Max.X, b.Max.Y)));
        return list;
    }

    /// <summary>The sheet's paper: its title block's extents, else the sheet outline.</summary>
    private LayoutMath.Rect? Extents(ViewSheet sheet)
    {
        var tb = new FilteredElementCollector(doc, sheet.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType().FirstOrDefault();
        if (tb?.get_BoundingBox(sheet) is { } b && b.Max.X - b.Min.X > 0.3) return new LayoutMath.Rect(b.Min.X, b.Min.Y, b.Max.X, b.Max.Y);
        var o = sheet.Outline;
        return o is not null && o.Max.U - o.Min.U > 0.3 ? new LayoutMath.Rect(o.Min.U, o.Min.V, o.Max.U, o.Max.V) : null;
    }

    // 5. Title block fields
    private object? Fill() => HandingOff(() =>
    {
        var numbers = plan.Sheets.Where(s => _sheets.ContainsKey(s.Number)).Select(s => s.Number).ToArray();
        return Call(new FillTitleBlocksTool(), new { values = plan.Spec.TitleBlockFields, sheet_numbers = numbers }).GetRawText();
    });

    // 6. Issue sheet
    private object? MakeRegister(DrawingPackage.SheetItem s) => HandingOff(() => MakeRegisterWith(s, plan.TitleBlock!));

    /// <summary>The register sheet with a sheet list schedule of the package's numbers on it.</summary>
    private long MakeRegisterWith(DrawingPackage.SheetItem s, string titleBlock)
    {
        var id = CreateSheet(s, titleBlock);
        var prefix = PrefixOf(s.Number);
        Call(new CreateSheetListTool(), new
        {
            name = $"{s.Name} {prefix}".Trim(),
            extra_fields = new[] { "Current Revision", "Current Revision Date" },
            number_starts_with = prefix.Length > 0 ? prefix : null,
            place_on_sheet = s.Number,
        });
        if (prefix.Length > 0 && plan.Sheets.Count < new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Count(x => x.SheetNumber.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            _notes.Add($"The register on {s.Number} lists every sheet numbered {prefix}..., including ones that were in the model before.");
        return id;
    }

    private static string PrefixOf(string number)
    {
        var i = number.Length;
        while (i > 0 && char.IsDigit(number[i - 1])) i--;
        return number.Substring(0, i);
    }

    private (bool, string) CheckRegister()
    {
        if (plan.Register is not { } r || !_sheets.TryGetValue(r.Number, out var id)) return (false, "the register sheet was not made");
        var lists = new FilteredElementCollector(doc, new ElementId(id)).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>().Count(si => !si.IsTitleblockRevisionSchedule);
        return (lists > 0, lists > 0 ? $"{r.Number} holds the drawing list" : $"{r.Number} has no drawing list on it");
    }

    // ---- After the package ----------------------------------------------------------------------------------------------

    private sealed record Finding(string Check, string Where, long? Id, string Issue, bool Problem);

    /// <summary>check_drawings on what was made: sections not on a sheet and empty sheets are problems; empty title block fields are notes.</summary>
    private List<Finding> DrawingCheck()
    {
        var mine = new HashSet<long>(_views.Values.Concat(_sheets.Values));
        if (mine.Count == 0) return [];
        try
        {
            Directory.CreateDirectory(folder);
            var r = Call(new CheckDrawingsTool(), new { checks = new[] { "views_not_on_sheets", "empty_sheets", "sheet_fields" }, folder });
            return r.GetProperty("list").EnumerateArray()
                .Where(f => f.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && mine.Contains(id.GetInt64()))
                .Select(f => new Finding(f.GetProperty("check").GetString() ?? "", f.GetProperty("where").GetString() ?? "", f.GetProperty("id").GetInt64(),
                    f.GetProperty("issue").GetString() ?? "", f.GetProperty("check").GetString() != "sheet_fields"))
                .ToList();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException or KeyNotFoundException)
        {
            _notes.Add($"check_drawings did not run ({ex.Message.Trim()}).");
            return [];
        }
    }

    private string WriteExcel(List<Finding> findings)
    {
        Directory.CreateDirectory(folder);
        var stem = new string($"{doc.Title} - Drawing package {DateTime.Now:yyyy-MM-dd HHmm}".Where(ch => Path.GetInvalidFileNameChars().IndexOf(ch) < 0).ToArray());
        var file = Path.Combine(folder, stem + ".xlsx");
        var names = plan.Views.ToDictionary(v => v.Key, v => v.Name);
        string Revision(long id) => doc.GetElement(new ElementId(id)) is ViewSheet s ? s.get_Parameter(BuiltInParameter.SHEET_CURRENT_REVISION)?.AsString() ?? "" : "";
        var register = new List<object?[]> { new object?[] { "Sheet number", "Sheet name", "Views", "Scale", "Size", "Title block", "Revision" } };
        foreach (var s in plan.Sheets.Where(s => _sheets.ContainsKey(s.Number)))
            register.Add([s.Number, s.Name, string.Join(", ", s.Views.Where(_views.ContainsKey).Select(k => names[k])), $"1:{plan.Spec.Scale}", plan.Spec.SheetSize, plan.TitleBlock ?? "", Revision(_sheets[s.Number])]);
        var sheets = new List<(string, IList<object?[]>)> { ("Drawing register", register) };
        if (plan.Spec.IssueSheet == "transmittal")
        {
            var t = new List<object?[]>
            {
                new object?[] { "Transmittal", doc.Title }, new object?[] { "Date", DateTime.Now.ToString("yyyy-MM-dd") },
                new object?[] { "From", app.Application.Username }, new object?[] { "To", "" }, new object?[] { "Purpose", "For information" },
                new object?[] { "" }, new object?[] { "No.", "Sheet number", "Sheet name", "Revision", "Copies", "Format" },
            };
            var i = 1;
            foreach (var s in plan.Sheets.Where(s => !s.Register && _sheets.ContainsKey(s.Number)))
                t.Add([i++, s.Number, s.Name, Revision(_sheets[s.Number]), 1, "PDF"]);
            sheets.Add(("Transmittal", t));
        }
        var checks = new List<object?[]> { new object?[] { "Stage", "Status", "Check", "Result" } };
        foreach (var s in _stages) checks.Add([s.Name, s.Status, s.CheckOk is null ? "" : s.CheckOk == true ? "pass" : "fail", s.Check]);
        foreach (var f in findings) checks.Add(["check_drawings", f.Check, f.Problem ? "fail" : "note", $"{f.Where}: {f.Issue}"]);
        sheets.Add(("Checks", checks));
        var time = new List<object?[]> { new object?[] { "Stage", "AI seconds", "By-hand minutes (estimate)" } };
        foreach (var s in _stages) time.Add([s.Name, Math.Round(s.Seconds, 1), Math.Round(s.ByHandMinutes, 1)]);
        sheets.Add(("Time saved", time));
        Xlsx.Write(file, sheets);
        return file;
    }
}
