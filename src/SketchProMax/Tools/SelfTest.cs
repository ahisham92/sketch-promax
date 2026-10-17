using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace SketchProMax.Tools;

/// <summary>
/// Calls every tool once on the open model with inputs picked from that model, and reports which ones fail.
/// Read-only tools run first. Tools that change the model then run inside one transaction group that is rolled back
/// at the end, so the model is left exactly as it was. Files go to a temporary folder that is deleted afterwards.
/// </summary>
internal static partial class SelfTest
{
    public sealed record Outcome(string Tool, string Status, long Ms, string Message);

    public sealed class Report
    {
        public string Model { get; init; } = "";
        public string RevitVersion { get; init; } = "";
        public string AddinVersion { get; init; } = "";
        public DateTime Ran { get; init; } = DateTime.Now;
        public string Mode { get; init; } = "";
        public bool LargeModel { get; init; }
        public List<Outcome> Results { get; } = [];
        public int Passed => Results.Count(r => r.Status == "pass");
        public int Failed => Results.Count(r => r.Status == "fail");
        public int Skipped => Results.Count(r => r.Status == "skip");
        public string? File { get; set; }
        /// <summary>Which copies of the shared libraries (System.Memory, Roslyn, System.Text.Json) this Revit uses.</summary>
        public string Libraries { get; } = SafeLibraries();

        private static string SafeLibraries()
        {
            try { return SketchProMax.Revit.AssemblyReport.Describe(); }
            catch (Exception ex) { return "Could not be read: " + ex.Message; }
        }

        public string Summary()
        {
            var sb = new StringBuilder($"{Passed} passed, {Failed} failed, {Skipped} skipped (of {Results.Count} steps).");
            foreach (var r in Results.Where(r => r.Status == "fail"))
                sb.Append($"\n• {r.Tool}: {Short(r.Message, 220)}");
            return sb.ToString();
        }
    }

    private sealed class Skip(string reason) : Exception(reason);

    private static void Need(bool condition, string reason)
    {
        if (!condition) throw new Skip(reason);
    }

    /// <summary>Everything the inputs are built from, read from the open model.</summary>
    private sealed class Ctx
    {
        public required UIApplication App;
        public required Document Doc;
        public required ToolRegistry Registry;
        public required string Temp;
        /// <summary>The field test's own throwaway project: steps that can't be rolled back may run.</summary>
        public bool Throwaway;
        public ForgeTypeId Unit = UnitTypeId.Millimeters;

        public Level? Level;
        public Level? Upper;
        public ViewPlan? Plan;
        public Wall? Wall;
        public Floor? Floor;
        public Room? Room;
        public ViewSchedule? Schedule;
        public ViewSheet? Sheet;
        public FamilySymbol? Door;
        public double OriginX, OriginY; // internal feet, clear of the model

        /// <summary>Millimetres to project units.</summary>
        public double P(double mm) => Math.Round(UnitUtils.ConvertFromInternalUnits(mm / 304.8, Unit), 4);

        /// <summary>A point in the empty test area, in project units, offset by mm.</summary>
        public double[] At(double xMm, double yMm) =>
            [Math.Round(UnitUtils.ConvertFromInternalUnits(OriginX, Unit), 4) + P(xMm),
             Math.Round(UnitUtils.ConvertFromInternalUnits(OriginY, Unit), 4) + P(yMm)];

        public double[] Rect(double x, double y) => At(x, y);
        public double[][] Square(double x, double y, double size) =>
            [At(x, y), At(x + size, y), At(x + size, y + size), At(x, y + size)];

        public T? First<T>(Func<T, bool>? where = null) where T : Element =>
            new FilteredElementCollector(Doc).OfClass(typeof(T)).Cast<T>().FirstOrDefault(e => where is null || where(e));

        public FamilySymbol? Symbol(BuiltInCategory category, Func<FamilySymbol, bool>? where = null) =>
            new FilteredElementCollector(Doc).OfClass(typeof(FamilySymbol)).OfCategory(category).Cast<FamilySymbol>()
                .FirstOrDefault(s => where is null || where(s));

        public long Id(Element? e) => e?.Id.Value ?? throw new Skip("missing element");

        public string LevelName => Level?.Name ?? throw new Skip("the model has no levels");
        public long PlanId => Plan?.Id.Value ?? throw new Skip("the model has no floor plan");
        public string UpperName => Upper?.Name ?? throw new Skip("the model needs two levels");
    }

    /// <summary>The newest element of a category that can hold rebar (the one a setup step just placed, when it is concrete).</summary>
    private static long Host(Ctx c, BuiltInCategory category, string what, Func<Element, bool>? where = null) =>
        new FilteredElementCollector(c.Doc).OfCategory(category).WhereElementIsNotElementType()
            .Where(e => (where is null || where(e)) && RebarHostData.IsValidHost(e)).OrderByDescending(e => e.Id.Value).FirstOrDefault()?.Id.Value
        ?? throw new Skip($"no {what} in the model (the loaded family may be steel)");

    private static long Newest<T>(Ctx c) where T : Element =>
        new FilteredElementCollector(c.Doc).OfClass(typeof(T)).WhereElementIsNotElementType().Max(e => (long?)e.Id.Value) ?? throw new Skip($"no {typeof(T).Name} in the model");

    private static string FirstLink(Ctx c, Func<Document, bool>? where = null) =>
        new FilteredElementCollector(c.Doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>()
            .Where(l => l.GetLinkDocument() is { } d && (where is null || where(d))).Select(l => c.Doc.GetElement(l.GetTypeId())?.Name).FirstOrDefault(n => n != null)
        ?? throw new Skip(where is null ? "the model has no loaded Revit links" : "no loaded link has what this test needs");

    /// <summary>NoGroup: run outside the rolled-back transaction group (tools such as relinquish_mine that Revit refuses
    /// to run inside one, and that change ownership, not model content).</summary>
    private sealed record Case(string Tool, Func<Ctx, object> Args, string[]? Setup = null, bool Heavy = false, bool NoGroup = false);

    public enum Mode { Safe, Full }

    /// <summary>One run in progress. Each tool runs in its own Revit API call, so Revit stays responsive in between.</summary>
    private sealed class Run
    {
        public required Report Report;
        public required Ctx Ctx;
        public required Mode Mode;
        public required bool Big;
        public required List<Case> Steps;
        public int Done;
        public string? Current;
        public string? StoppedBecause;
        public bool Finished;
        public readonly CancellationTokenSource Cancel = new();
    }

    private static volatile Run? _run;

    public static bool IsRunning => _run is { Finished: false };

    private const int SlowStepMs = 60000;

    /// <summary>
    /// Starts a self-test on the active model and returns at once. Call on Revit's main thread.
    /// Safe mode runs the read-only tools; full mode also runs the editing tools, each inside its own
    /// transaction group that is rolled back straight after, so the model is left as it was.
    /// </summary>
    public static string Start(UIApplication app, ToolRegistry registry, Mode mode, Action<Report>? onDone = null)
    {
        if (IsRunning) return "A self-test is already running. " + Progress();
        var doc = app.ActiveUIDocument?.Document ?? throw new InvalidOperationException("Open a model first.");
        if (doc.IsReadOnly) throw new InvalidOperationException("This model is read-only.");
        var temp = Path.Combine(Path.GetTempPath(), "SketchProMax-self-test-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(temp);
        var ctx = Collect(app, doc, registry, temp);
        LearnedTools.MarkApproved(LearnedToolCode);

        // Large models: tools that touch every element in a view or the model are skipped, so nothing runs for minutes.
        var size = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).GetElementCount()
                   + new FilteredElementCollector(doc).OfClass(typeof(Wall)).GetElementCount()
                   + new FilteredElementCollector(doc).OfClass(typeof(Floor)).GetElementCount();
        var big = size > 5000;

        var steps = ReadOnlyCases().Concat(FileCases()).ToList();
        if (mode == Mode.Full) steps.AddRange(ChangeCases().Concat(RegressionCases()));

        var run = new Run
        {
            Report = new Report
            {
                Model = doc.Title,
                RevitVersion = app.Application.VersionNumber,
                AddinVersion = typeof(SelfTest).Assembly.GetName().Version?.ToString() ?? "",
                Mode = mode.ToString().ToLowerInvariant(),
                LargeModel = big,
            },
            Ctx = ctx, Mode = mode, Big = big, Steps = steps,
        };
        _run = run;
        _ = Task.Run(() => Loop(run, registry, onDone));
        return $"Self-test started on '{doc.Title}' in {run.Report.Mode} mode ({steps.Count} steps" +
               (big ? $", large model with {size:N0} elements: heavy tools are skipped" : "") +
               "). Revit stays usable between steps; don't switch to another model until it finishes.";
    }

    public static void Stop() => _run?.Cancel.Cancel();

    public static string Progress()
    {
        var r = _run;
        if (r is null) return "No self-test has run since Revit started.";
        return r.Finished
            ? $"Finished: {r.Report.Summary()}" + (r.StoppedBecause is null ? "" : $" Stopped early: {r.StoppedBecause}") +
              (r.Report.File is null ? "" : $" Report: {r.Report.File}")
            : $"Running: {r.Done} of {r.Steps.Count} steps done{(r.Current is null ? "" : $", now {r.Current}")}.";
    }

    public static Report? LastReport => _run?.Report;

    private static async Task Loop(Run run, ToolRegistry registry, Action<Report>? onDone)
    {
        try
        {
            foreach (var step in run.Steps)
            {
                if (run.Cancel.IsCancellationRequested) { run.StoppedBecause ??= "stopped by the user"; break; }
                run.Current = step.Tool;
                var watch = Stopwatch.StartNew();
                Outcome outcome;
                try
                {
                    outcome = await App.Dispatcher.InvokeAsync(app => RunStep(app, run, registry, step)).ConfigureAwait(false);
                }
                catch (Exception ex) { outcome = new(step.Tool, "fail", watch.ElapsedMilliseconds, $"{ex.GetType().Name}: {ex.Message}"); }
                lock (run.Report.Results) run.Report.Results.Add(outcome);
                run.Done++;
                if (outcome.Status == "stop") { run.StoppedBecause = outcome.Message; break; }
                if (outcome.Ms > SlowStepMs) { run.StoppedBecause = $"{step.Tool} took {outcome.Ms / 1000} s, so the test stopped to keep Revit responsive"; break; }
                // Give Revit room to redraw and handle the user between steps.
                await Task.Delay(150).ConfigureAwait(false);
            }
            lock (run.Report.Results) run.Report.Results.RemoveAll(r => r.Status == "stop");
            await App.Dispatcher.InvokeAsync(app => { Finish(run, registry); return true; }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            run.StoppedBecause ??= $"the test could not finish ({ex.GetType().Name}: {ex.Message})";
        }
        finally
        {
            run.Current = null;
            run.Finished = true;
        }
        if (onDone is not null)
        {
            try { await App.Dispatcher.InvokeAsync(_ => { onDone(run.Report); return true; }).ConfigureAwait(false); }
            catch (Exception) { /* the result is still available through self_test_status */ }
        }
    }

    /// <summary>Asks the user before a full self-test, which makes (and rolls back) changes in the open model.</summary>
    public static bool ConfirmFull(Document doc) => new TaskDialog("Sketch ProMax self-test")
    {
        MainInstruction = "Run the full self-test?",
        MainContent = FullWarning(doc),
        CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
        DefaultButton = TaskDialogResult.No,
    }.Show() == TaskDialogResult.Yes;

    public static string FullWarning(Document doc) =>
        $"Full check makes and rolls back test changes on '{doc.Title}'. Run it on a copy, not a working model. Continue?";

    private static Outcome RunStep(UIApplication app, Run run, ToolRegistry registry, Case step)
    {
        // Only ever touch the model the test started on.
        var doc = app.ActiveUIDocument?.Document;
        if (doc is null || !doc.Equals(run.Ctx.Doc))
            return new(step.Tool, "stop", 0, $"the active model changed from '{run.Report.Model}', so the test stopped without touching the other model");
        if (step.Heavy && run.Big) return new(step.Tool, "skip", 0, "skipped on large models to keep Revit responsive");
        if (step.Heavy && run.Mode == Mode.Safe) return new(step.Tool, "skip", 0, "slow on big models; runs in the full self-test");

        var tool = registry.Find(step.Tool);
        if (tool is null) return new(step.Tool, "fail", 0, "Tool not found in the registry.");
        var watch = Stopwatch.StartNew();
        TransactionGroup? group = null;
        try
        {
            if ((tool.ModifiesModel && !step.NoGroup) || step.Setup is not null)
            {
                group = new TransactionGroup(doc, $"Sketch ProMax: self-test {step.Tool} (rolled back)");
                group.Start();
                foreach (var name in step.Setup ?? [])
                {
                    // "tool" is the tool's first case; "tool#2" its second, and so on.
                    var hash = name.IndexOf('#');
                    var setupTool = hash < 0 ? name : name.Substring(0, hash);
                    var nth = hash < 0 ? 1 : int.Parse(name.Substring(hash + 1), System.Globalization.CultureInfo.InvariantCulture);
                    var setup = AllCases().Where(c => c.Tool == setupTool).Skip(nth - 1).First();
                    var setupResult = Execute(app, registry.Find(setupTool)!, setup, run.Ctx);
                    if (setupResult.IsError)
                        return new(step.Tool, "skip", watch.ElapsedMilliseconds, $"setup step {name} failed: {Short(setupResult.Text, 300)}");
                }
            }
            var result = Execute(app, tool, step, run.Ctx);
            if (!result.IsError && result.Text.Length > 24000)
                return new(step.Tool, "fail", watch.ElapsedMilliseconds, $"Output too large for a chat ({result.Text.Length:N0} characters).");
            return new(step.Tool, result.IsError ? "fail" : "pass", watch.ElapsedMilliseconds, Short(result.Text, 600));
        }
        catch (Skip s) { return new(step.Tool, "skip", 0, s.Message); }
        catch (Exception ex) { return new(step.Tool, "fail", watch.ElapsedMilliseconds, Bridge.BridgeServer.Describe(tool, ex)); }
        finally
        {
            if (group is not null)
            {
                if (group.HasStarted() && !group.HasEnded()) group.RollBack();
                group.Dispose();
            }
        }
    }

    private static ToolResult Execute(UIApplication app, IRevitTool tool, Case c, Ctx ctx)
    {
        var raw = JsonSerializer.SerializeToElement(c.Args(ctx));
        var args = raw.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        return Revit.FailureGuard.Run(() => tool.Execute(app, new ToolArgs(args)));
    }

    private static IEnumerable<Case> AllCases() => ReadOnlyCases().Concat(FileCases()).Concat(ChangeCases()).Concat(RegressionCases());

    private static void Finish(Run run, ToolRegistry registry)
    {
        var report = run.Report;
        HashSet<string> tested;
        lock (report.Results) tested = report.Results.Select(r => r.Tool).ToHashSet();
        foreach (var tool in registry.All.Where(t => !tested.Contains(t.Name)).OrderBy(t => t.Name))
        {
            var reason = tool.Name switch
            {
                _ when run.StoppedBecause is not null => "not reached: " + run.StoppedBecause,
                "batch_run_recipe" => "opens other model files; run it by hand on a folder of copies",
                "load_family" => "needs a family file on this PC",
                "copy_from_file" => "needs another Revit file on this PC",
                "load_steel_sections" => "depends on the content library installed on this PC",
                "title_block_from_cad" => "needs a DWG title block on this PC",
                "run_learned_tool" => "tested through save_as_tool",
                "run_self_test" or "self_test_status" => "this test",
                "run_field_test" => "opens its own throwaway project; run it from the ribbon or on its own",
                "report_bug" => "writes a bug report zip to the user's bug folder; run it by hand",
                "export_chats_and_bugs" => "writes a zip to the user's bug folder; run it by hand",
                _ when run.Mode == Mode.Safe && tool.ModifiesModel => "changes the model; runs in the full self-test",
                _ => "no test written yet",
            };
            lock (report.Results) report.Results.Add(new(tool.Name, "skip", 0, reason));
        }

        try { Directory.Delete(run.Ctx.Temp, true); } catch (Exception) { }
        try { System.IO.File.Delete(Recipes.PathFor("sketch-promax-self-test")); } catch (Exception) { }
        try { System.IO.File.Delete(Recipes.PathFor("sketch-promax-self-test-typed")); } catch (Exception) { }
        try { System.IO.File.Delete(LearnedTools.PathFor("self_test_count_walls")); } catch (Exception) { }

        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax", "self-test");
            Directory.CreateDirectory(folder);
            var safe = string.Concat(report.Model.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
            report.File = Path.Combine(folder, $"{DateTime.Now:yyyyMMdd-HHmmss} {safe} Revit {report.RevitVersion}.json");
            List<Outcome> results;
            lock (report.Results) results = report.Results.ToList();
            System.IO.File.WriteAllText(report.File, JsonSerializer.Serialize(new
            {
                report.Model, report.RevitVersion, report.AddinVersion, report.Mode, report.LargeModel, report.Ran,
                report.Passed, report.Failed, report.Skipped, stoppedEarly = run.StoppedBecause,
                startup = new { start = SketchProMax.Revit.StartupTimings.StartLine(), opens = SketchProMax.Revit.StartupTimings.OpenLines() },
                results = results.OrderBy(r => r.Status == "fail" ? 0 : r.Status == "skip" ? 2 : 1).ThenBy(r => r.Tool),
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception) { report.File = null; }
    }

    private static Ctx Collect(UIApplication app, Document doc, ToolRegistry registry, string temp)
    {
        var ctx = new Ctx { App = app, Doc = doc, Registry = registry, Temp = temp };
        try { ctx.Unit = doc.GetUnits().GetFormatOptions(SpecTypeId.Length).GetUnitTypeId(); } catch (Exception) { }

        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
        ctx.Level = levels.FirstOrDefault();
        ctx.Upper = levels.Skip(1).FirstOrDefault();
        ctx.Plan = doc.ActiveView is ViewPlan { IsTemplate: false, ViewType: ViewType.FloorPlan } active ? active
            : new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan)
                .OrderBy(v => v.GenLevel?.Id == ctx.Level?.Id ? 0 : 1).FirstOrDefault();
        if (ctx.Plan?.GenLevel is { } planLevel) ctx.Level = planLevel;
        ctx.Upper = levels.FirstOrDefault(l => l.Elevation > (ctx.Level?.Elevation ?? double.MinValue) + 1);

        ctx.Wall = ctx.First<Wall>(w => w.Location is LocationCurve lc && lc.Curve is Line && w.WallType.Kind == WallKind.Basic
                                         && (ctx.Level is null || w.LevelId == ctx.Level.Id))
                   ?? ctx.First<Wall>(w => w.Location is LocationCurve lc && lc.Curve is Line && w.WallType.Kind == WallKind.Basic);
        ctx.Floor = ctx.First<Floor>(f => f.FloorType is not null);
        ctx.Room = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
            .Cast<Room>().FirstOrDefault(r => r.Area > 0);
        ctx.Schedule = ctx.First<ViewSchedule>(s => !s.IsTemplate && !s.IsTitleblockRevisionSchedule && s.Definition is not null);
        ctx.Sheet = ctx.First<ViewSheet>(s => !s.IsPlaceholder);
        ctx.Door = ctx.Symbol(BuiltInCategory.OST_Doors);

        // A clear area 20 m east of everything that has a location, for new geometry.
        double maxX = 0, minY = 0;
        var any = false;
        foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType()
                     .WherePasses(new LogicalOrFilter(new ElementClassFilter(typeof(Wall)), new ElementClassFilter(typeof(Floor)))))
        {
            var box = e.get_BoundingBox(null);
            if (box is null) continue;
            maxX = any ? Math.Max(maxX, box.Max.X) : box.Max.X;
            minY = any ? Math.Min(minY, box.Min.Y) : box.Min.Y;
            any = true;
        }
        ctx.OriginX = maxX + 20000 / 304.8;
        ctx.OriginY = minY;
        return ctx;
    }

    private static IEnumerable<Case> ReadOnlyCases() =>
    [
        new("get_model_context", _ => new { }),
        new("query_elements", _ => new { category = "Walls", limit = 5 }),
        new("get_element_parameters", c => new { element_ids = new[] { c.Id(c.Wall ?? throw new Skip("the model has no walls")) } }),
        new("list_views_and_sheets", _ => new { }),
        new("list_view_templates", _ => new { }),
        new("list_types", _ => new { category = "Walls" }),
        new("list_revisions", _ => new { }),
        new("list_text_notes", _ => new { limit = 20 }),
        new("rft_detail_families", _ => new { parameters = false }),
        new("check_schedule", c => new { schedule = c.Id(c.Schedule ?? throw new Skip("the model has no schedules")).ToString() }),
        new("read_schedule", c => new { schedule_id = c.Id(c.Schedule ?? throw new Skip("the model has no schedules")), max_rows = 20 }),
        new("select_elements", c => new { element_ids = new[] { c.Id(c.Wall ?? throw new Skip("the model has no walls")) }, zoom = false }),
        new("run_model_audit", _ => new { }),
        new("export_project_pack", c => new { folder = c.Temp, include_chat = false, max_schedules = 3, max_schedule_rows = 50, max_elements = 20000 }),
        new("capture_view_image", c => new { view_id = c.PlanId, pixel_width = 800 }) { Heavy = true },
        new("create_legend", c => new { name = "SPM Selftest Legend", scale = 50 }),
        new("save_legend_seed", c => new { path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "spm-legend-seed-test.rvt"), overwrite = true }) { Heavy = true },
        new("in_place_guide", c => new { action = "convert" }),
        new("list_view_filters", _ => new { }),
        new("query_linked_models", _ => new { }),
        new("collect_text", _ => new { limit = 50 }),
        new("wait_for_job", _ => new { wait_seconds = 0 }),
        new("cancel_job", _ => new { job_id = 999999 }),
        new("get_coordinates", _ => new { }),
        new("convert_coordinates", _ => new { points = new[] { new[] { 0.0, 0.0, 0.0 } }, from = "internal" }),
        new("get_activity_log", _ => new { limit = 5 }),
        new("memory_note", _ => new { action = "list" }),
        new("list_recipes", _ => new { }),
        new("list_learned_tools", _ => new { }),
        new("list_steel_connection_types", _ => new { }),
        new("list_title_blocks", c => new { sheet_number = c.Sheet?.SheetNumber }),
        new("run_revit_query_code", _ => new { code = "Level.Create(doc, 1234.5);\nreturn new { walls = new FilteredElementCollector(doc).OfClass(typeof(Wall)).GetElementCount(), title = doc.Title };", description = "self-test query that makes a level (rolled back)" }),
        new("create_mep_openings", _ => new { method = "report", limit = 50 }) { Heavy = true },
    ];

    /// <summary>Exports: they write files to the temporary folder and cannot run inside a transaction group.</summary>
    private static IEnumerable<Case> FileCases() =>
    [
        new("export_schedule", c => new { schedule_id = c.Id(c.Schedule ?? throw new Skip("the model has no schedules")), folder = c.Temp, file_name = "schedule" }),
        new("export_drawing_list", c => new { folder = c.Temp, fields = new[] { "Drawn By" } }),
        new("export_steel_takeoff", c => new { folder = c.Temp }) { Heavy = true },
        new("export_quantities", c => new { folder = c.Temp, categories = new[] { "Walls", "Floors" }, group_by = new[] { "level" } }) { Heavy = true },
        new("embodied_carbon", c => new { folder = c.Temp, stages = new[] { "A1-A3", "A4", "A5" }, categories = new[] { "Walls", "Floors", "Structural Columns", "Structural Framing" } }) { Heavy = true },
        new("carbon_compare", c => new { folder = c.Temp, cement_replacement = "ggbs50", cement_replacement_all = true, categories = new[] { "Walls", "Floors" }, excel = false }) { Heavy = true },
        new("ve_opportunities", c => new { folder = c.Temp, span_m = 7.5, update_register = false, excel = false }) { Heavy = true },
        new("ve_compare_options", c => new { folder = c.Temp, span_x = 8.0, span_y = 6.0, floors = 5, options = new[] { "flat_slab", "beam_and_slab", "pt_flat_slab", "hollow_core", "steel_composite", "raft", "piles", "model" }, excel = false }),
        new("ve_register", c => new { folder = c.Temp, excel = false }),
        new("export_pdf", c => new { view_ids = new[] { c.Sheet?.Id.Value ?? c.PlanId }, folder = c.Temp, file_name = "sheets" }) { Heavy = true },
        new("export_files", c => new { format = "dwg", view_ids = new[] { c.PlanId }, folder = c.Temp, file_name = "plan" }) { Heavy = true },
        new("export_bcf", c => new { issues = new[] { new { title = "Self-test issue", element_ids = c.Wall is null ? Array.Empty<long>() : [c.Wall.Id.Value] } }, folder = c.Temp, file_name = "issues" }),
        new("save_recipe", _ => new { name = "sketch-promax-self-test", description = "Self-test", steps = new[] { new { tool = "get_model_context", arguments = new { } } } }),
        new("save_as_tool", _ => new
        {
            name = "self_test_count_walls", description = "Self-test: counts walls.", changes_model = false,
            code = LearnedToolCode,
            parameters = new { add = new { type = "integer" } }, required = new[] { "add" },
        }),
        new("self_test_count_walls", _ => new { add = 0 }),
        new("delete_learned_tool", _ => new { name = "self_test_count_walls" }),
    ];

    // The learned tool the test saves and runs. Its code is the test's own, so it is approved up front instead of asking.
    private const string LearnedToolCode =
        "return new FilteredElementCollector(doc).OfClass(typeof(Wall)).GetElementCount() + Convert.ToInt32(args[\"add\"]);";

    private static IEnumerable<Case> ChangeCases() =>
    [
        // Levels, grids and new geometry go in an empty area east of the model.
        new("create_levels", c => new { levels = new[] { new { name = "Self-test level", elevation = c.P(new FilteredElementCollector(c.Doc).OfClass(typeof(Level)).Cast<Level>().Select(l => l.Elevation).DefaultIfEmpty(0).Max() * 304.8 + 3000) } }, create_plans = true }),
        new("create_grids", c => new { lines = new[] { new { name = "ST1", start = c.At(0, -2000), end = c.At(0, 12000) }, new { name = "ST2", start = c.At(6000, -2000), end = c.At(6000, 12000) }, new { name = "STA", start = c.At(-1000, 5000), end = c.At(7000, 5000) } } }),
        new("create_walls", c => new { segments = new[] { new { start = c.At(0, 0), end = c.At(6000, 0) }, new { start = c.At(6000, 0), end = c.At(6000, 4000) } }, level = c.LevelName, height = c.P(3000) }),
        new("create_floors", c => new { floors = new[] { new { points = c.Square(0, 0, 4000), level = c.LevelName } } }),
        new("create_ceilings", c => new { ceilings = new[] { new { points = c.Square(0, 0, 4000), level = c.LevelName, height = c.P(2700) } } }),
        new("create_roof", c => new { points = c.Square(0, 6000, 4000), level = c.Upper?.Name ?? c.LevelName, slope_degrees = 20 }),
        new("place_doors_windows", c =>
        {
            Need(c.Wall is not null, "the model has no walls");
            Need(c.Door is not null, "the model has no door types loaded");
            var length = ((LocationCurve)c.Wall!.Location).Curve.Length * 304.8;
            return new { items = new[] { new { wall_id = c.Wall.Id.Value, type_id = c.Door!.Id.Value, distance_along_wall = c.P(length / 2) } } };
        }),
        new("place_family_instances", c =>
        {
            var symbol = c.Symbol(BuiltInCategory.OST_Furniture, s => s.Family.FamilyPlacementType == FamilyPlacementType.OneLevelBased)
                ?? c.Symbol(BuiltInCategory.OST_GenericModel, s => s.Family.FamilyPlacementType == FamilyPlacementType.OneLevelBased)
                ?? throw new Skip("no furniture or generic model family loaded");
            return new { type_id = symbol.Id.Value, level = c.LevelName, placements = new[] { new { point = c.At(1000, 1000) } } };
        }),
        new("place_many", c =>
        {
            var symbol = c.Symbol(BuiltInCategory.OST_Furniture, s => s.Family.FamilyPlacementType == FamilyPlacementType.OneLevelBased)
                ?? c.Symbol(BuiltInCategory.OST_GenericModel, s => s.Family.FamilyPlacementType == FamilyPlacementType.OneLevelBased)
                ?? throw new Skip("no furniture or generic model family loaded");
            return new { kind = "family", source = $"{symbol.FamilyName}: {symbol.Name}", default_level = c.LevelName,
                placements = new[] { new { x = c.At(2000, 2000)[0], y = c.At(2000, 2000)[1] }, new { x = c.At(3000, 2000)[0], y = c.At(3000, 2000)[1] } } };
        }),
        new("place_columns", c =>
        {
            var type = c.Symbol(BuiltInCategory.OST_StructuralColumns) ?? c.Symbol(BuiltInCategory.OST_Columns) ?? throw new Skip("no column family loaded");
            return new { type_id = type.Id.Value, base_level = c.LevelName, top_level = c.UpperName, points = new[] { c.At(0, 8000) },
                architectural = type.Category?.Id.Value == (long)BuiltInCategory.OST_Columns };
        }),
        new("place_beams", c =>
        {
            var type = c.Symbol(BuiltInCategory.OST_StructuralFraming) ?? throw new Skip("no structural framing family loaded");
            return new { type_id = type.Id.Value, level = c.UpperName, lines = new[] { new { start = c.At(0, 8000), end = c.At(6000, 8000) } } };
        }),
        new("create_stairs", c => new { base_level = c.LevelName, top_level = c.UpperName, start = c.At(8000, 0), direction = new[] { 0, 1 }, width = c.P(1200) }),
        new("create_railings", c => new { level = c.LevelName, points = new[] { c.At(10000, 0), c.At(10000, 3000) } }),
        new("create_room_separation_lines", c => new { view_id = c.PlanId, points = new[] { c.At(12000, 0), c.At(12000, 3000) } }),
        new("create_mep_runs", c =>
        {
            Need(c.First<DuctType>() is not null, "the model has no duct types");
            return new { kind = "duct", level = c.LevelName, points = new[] { c.At(0, 10000).Append(c.P(2800)).ToArray(), c.At(6000, 10000).Append(c.P(2800)).ToArray() }, width = c.P(400), height = c.P(300) };
        }),
        new("place_rooms", c => new { level = c.LevelName }) { Heavy = true },

        // Edits to existing elements.
        new("move_copy_rotate", c => new { element_ids = new[] { c.Id(c.Wall ?? throw new Skip("the model has no walls")) }, offset = new[] { c.P(1000), 0 }, copy = true }),
        new("set_parameters", c => new { changes = new[] { new { element_id = c.Id(c.Wall ?? throw new Skip("the model has no walls")), parameter = "Comments", value = "Sketch ProMax self-test" } } }),
        new("duplicate_type", c => new { type_id = c.Id(c.Wall?.WallType ?? throw new Skip("the model has no walls")), new_name = "Self-test type " + Guid.NewGuid().ToString("N").Substring(0, 4) }),
        new("change_type", c => new { element_ids = new[] { c.Id(c.Wall ?? throw new Skip("the model has no walls")) }, type_id = c.Id(c.Wall!.WallType) }),
        new("rename_elements", c => new { items = new[] { new { element_id = c.PlanId, name = c.Plan!.Name + " (self-test)" } } }),
        new("set_material", c => new { name = "Self-test material", color = "#C0A080", assign_to_ids = c.Wall is null ? Array.Empty<long>() : [c.Wall.Id.Value], paint_faces = true }),
        new("cut_slab", c =>
        {
            var box = (c.Floor ?? throw new Skip("the model has no floors")).get_BoundingBox(null) ?? throw new Skip("the floor has no extents");
            double U(double ft) => Math.Round(UnitUtils.ConvertFromInternalUnits(ft, c.Unit), 4);
            var x = box.Min.X; var y = box.Min.Y; var d = 300 / 304.8;
            return new { floor_id = c.Floor!.Id.Value, points = new[] { new[] { U(x - d), U(y - d) }, new[] { U(x + d), U(y - d) }, new[] { U(x + d), U(y + d) }, new[] { U(x - d), U(y + d) } } };
        }),
        new("add_slab_edges", c =>
        {
            Need(c.First<SlabEdgeType>() is not null, "the model has no slab edge types");
            return new { floor_ids = new[] { c.Id(c.Floor ?? throw new Skip("the model has no floors")) } };
        }),
        new("hollow_core_layout", c => new { points = c.Square(0, 14000, 6000), level = c.LevelName, plank_width = c.P(1200) }),
        new("export_precast_schedule", c => new { folder = c.Temp, mark_prefix = "HC-" }, Setup: ["hollow_core_layout"]),
        new("place_pt_tendons", c => new { floor_id = Newest<Floor>(c), uniform = new { direction = "x", spacing = c.P(1000) } }, Setup: ["create_floors"]),
        new("find_clashes", c => new { set_a = new { categories = new[] { "Walls", "Floors" } }, level = c.LevelName, limit = 50, folder = c.Temp }) { Heavy = true },
        new("check_drawings", c => new { folder = c.Temp }) { Heavy = true },
        new("check_code_rules", c => new { pack = "example_residential", folder = c.Temp }),
        new("construction_sequence", c => new { categories = new[] { "Structural Columns", "Floors" }, shared_parameter_file = Path.Combine(c.Temp, "SharedParameters.txt"), folder = c.Temp }) { Heavy = true },
        new("copy_monitor", c => new { mode = "copy", link = FirstLink(c), what = new[] { "levels" } }),
        new("loading_plan", c => new
        {
            link = FirstLink(c, d => new FilteredElementCollector(d).OfCategory(BuiltInCategory.OST_Rooms).OfType<Room>()
                .Any(r => r.Area > 0 && r.Level is { } rl && Math.Abs(rl.ProjectElevation - (c.Plan?.GenLevel?.ProjectElevation ?? double.NaN)) < 1)),
            view_id = c.PlanId, folder = c.Temp,
        }) { Heavy = true },
        new("arch_to_structure", c => new { source = "model", what = new[] { "floors" }, level = c.LevelName }, Setup: ["create_floors"]) { Heavy = true },
        new("create_ramp", c => new { start = c.At(0, 20000), end = c.At(12000, 20000), width = c.P(3000), base_level = c.LevelName, rise = c.P(1500), landing = c.P(1500) }),
        new("equipment_pads", c =>
        {
            Need(new FilteredElementCollector(c.Doc).OfCategory(BuiltInCategory.OST_MechanicalEquipment).WhereElementIsNotElementType().Any(), "the model has no mechanical equipment");
            return new { source = "model" };
        }) { Heavy = true },
        new("sleeve_schedule", c =>
        {
            Need(new FilteredElementCollector(c.Doc).OfClass(typeof(Opening)).Any(), "the model has no openings");
            return new { folder = c.Temp };
        }),
        new("general_notes", _ => new { values = new { codes = "Self-test code" } }),
        new("typical_details", _ => new { kinds = new[] { "lap_schedule", "cover_schedule" } }),
        new("issue_drawings", c => new { sheet_numbers = new[] { "ST-999" }, revision_description = "Self-test issue", folder = Path.Combine(c.Temp, "issue") }, Setup: ["create_sheets"]) { Heavy = true },
        new("check_model", c => new { folder = c.Temp }) { Heavy = true },
        new("find_missing_items", c => new { checks = new[] { "rooms_without_door", "columns_without_footing", "untagged_structure", "sheets_without_views" }, folder = c.Temp }),
        new("model_snapshot", c => new { action = "take", name = "self-test" }) { Heavy = true },
        new("coordination_progress", c => new { action = "record", test = "Self-test walls/floors", clash_args = new { set_a = new { categories = new[] { "Walls", "Floors" } }, level = c.LevelName, folder = c.Temp } }) { Heavy = true },
        new("boq_check", c => new { items = new[] { new { @ref = "1", description = "Concrete in suspended slabs", unit = "m3", quantity = 100.0 } }, folder = c.Temp }, Setup: ["create_floors"]) { Heavy = true },
        new("boq_item_breakdown", c => new { item = new { @ref = "1", description = "Concrete in suspended slabs", unit = "m3" }, method = "NRM2", folder = c.Temp }, Setup: ["create_floors"]),
        new("cost_estimate", c => new { rates = new[] { new { description = "Concrete in slabs", unit = "m3", rate = 150.0 } }, folder = c.Temp }, Setup: ["create_floors"]),
        new("carbon_view", c => new { name = "Self-test carbon view", categories = new[] { "Walls", "Floors" }, open = false }, Setup: ["create_floors"]) { Heavy = true },
        new("ve_view", c => new { name = "Self-test VE view", rescan = true, span_m = 4.0, open = false }, Setup: ["create_floors"]) { Heavy = true },
        new("ve_apply", c => new { target = "main", confirm_main_model = true, changes = new[] { new { kind = "change_type", element_ids = new[] { c.Id(c.Wall) }, to_type = c.Wall!.Name } }, folder = c.Temp }),
        new("spec_check", c => new { requirements = new[] { new { id = "S1", text = "Notes state the concrete grade", expect_text = new[] { "C" } } }, folder = c.Temp }),
        new("analytical_check", c =>
        {
            Need(new FilteredElementCollector(c.Doc).OfClass(typeof(AnalyticalMember)).Any(), "the model has no analytical members");
            return new { folder = c.Temp };
        }),
        new("export_analytical", c =>
        {
            Need(new FilteredElementCollector(c.Doc).OfClass(typeof(AnalyticalMember)).Any(), "the model has no analytical members");
            return new { folder = c.Temp };
        }),
        new("link_model", c =>
        {
            var lt = new FilteredElementCollector(c.Doc).OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>().FirstOrDefault(t => t.GetExternalFileReference() != null)
                     ?? throw new Skip("the model has no Revit links");
            return new { path = ModelPathUtils.ConvertModelPathToUserVisiblePath(lt.GetExternalFileReference().GetAbsolutePath()) };
        }),
        new("create_raft", c => new { level = c.LevelName, depth_below = c.P(1500), thickness = c.P(1000), projection = c.P(500) }, Setup: ["create_floors"]),
        new("work_progress", _ => new { action = "get" }),
        new("create_piles", c => new { points = new[] { c.At(0, 40000), c.At(3000, 40000) }, shape = "round", size = c.P(600), cutoff_level = c.P(0), toe_level = c.P(-12000), rake = 5 }),
        new("marine_structure", c => new { kind = "revetment", alignment = new[] { c.At(0, 50000), c.At(20000, 50000) }, @params = new { crest_level = c.P(3000), toe_level = c.P(-4000), crest_width = c.P(4000), slope = 2 } }),
        new("port_furniture", c => new { item = "bollard", edge = new[] { c.At(0, 60000), c.At(40000, 60000) }, cope_level = c.P(3000) }),
        new("design_vessel", c => new { preset = "hopper_barge", position = c.At(20000, 80000) }),
        new("vessel_collision_check", c => new { channel = new[] { c.At(0, 80000), c.At(40000, 80000) }, dwt = 5000, velocity = 2.5 }),
        new("progressive_collapse_check", c =>
        {
            Need(new FilteredElementCollector(c.Doc).OfCategory(BuiltInCategory.OST_StructuralColumns).WhereElementIsNotElementType().Any(), "the model has no structural columns");
            return new { scope = "perimeter" };
        }),
        new("export_tendon_schedule", c => new { folder = c.Temp }, Setup: ["create_floors", "place_pt_tendons"]),
        new("set_curtain_grid", c =>
        {
            var curtain = c.First<Wall>(w => w.WallType.Kind == WallKind.Curtain) ?? throw new Skip("the model has no curtain walls");
            return new { wall_ids = new[] { curtain.Id.Value }, vertical_spacing = c.P(1500) };
        }),
        new("manage_worksets", c =>
        {
            Need(c.Doc.IsWorkshared, "the model is not workshared");
            return new { create = new[] { "Self-test workset" } };
        }),
        new("set_coordinates", _ => new { latitude = 25.2, longitude = 55.27, place_name = "Self-test" }),

        // Views and sheets.
        new("create_plan_views", c => new { levels = new[] { c.LevelName }, kind = "floor", name_pattern = "{level} - self-test" }),
        new("duplicate_views", c => new { view_ids = new[] { c.PlanId }, mode = "duplicate", suffix = " - self-test" }),
        new("apply_view_template", c =>
        {
            var template = c.First<View>(v => v.IsTemplate && v.ViewType == ViewType.FloorPlan) ?? throw new Skip("the model has no floor plan view templates");
            return new { view_ids = new[] { c.PlanId }, template = template.Name };
        }),
        new("create_drafting_view", c => new { name = "Self-test drafting " + Guid.NewGuid().ToString("N").Substring(0, 4), scale = 20,
            lines = new[] { new { start = new[] { 0.0, 0.0 }, end = new[] { c.P(1000), 0.0 } } },
            texts = new[] { new { point = new[] { 0.0, c.P(200) }, text = "Self-test" } } }),
        new("create_sheets", _ => new { sheets = new[] { new { number = "ST-999", name = "Self-test" } } }),
        new("place_views_on_sheet", c =>
        {
            var sheet = c.First<ViewSheet>(s => s.SheetNumber == "ST-999") ?? throw new Skip("create_sheets did not create the test sheet");
            var view = c.First<ViewDrafting>(v => v.Name.StartsWith("Self-test drafting", StringComparison.Ordinal)) ?? throw new Skip("create_drafting_view did not create the test view");
            return new { sheet_id = sheet.Id.Value, view_ids = new[] { view.Id.Value } };
        }, Setup: ["create_sheets", "create_drafting_view"]),
        new("create_schedule", _ => new { category = "Walls", name = "Self-test wall schedule", fields = new[] { "Type", "Length" }, sort_by_first_field = true }),
        new("format_schedule", _ => new { schedule = "Self-test wall schedule", grand_totals = true }, Setup: ["create_schedule"]),
        new("create_sheet_list", _ => new { name = "Self-test drawing list" }),
        new("create_revision", _ => new { description = "Self-test revision" }),
        new("cloud_elements", c =>
        {
            var revision = c.First<Revision>() ?? throw new Skip("no revision");
            return new { revision_id = revision.Id.Value, element_ids = new[] { c.Id(c.Wall ?? throw new Skip("the model has no walls")) }, view_id = c.PlanId };
        }, Setup: ["create_revision"]),
        new("create_section", c => new { element_ids = new[] { c.Id(c.Wall ?? throw new Skip("the model has no walls")) }, name = "Self-test section" }),
        new("create_elevations", c => new { mode = "building", plan_view_id = c.PlanId, element_ids = new[] { c.Id(c.Wall ?? throw new Skip("the model has no walls")) }, sides = new[] { "North" }, name_pattern = "{side} self-test" }) { Heavy = true },
        new("create_callout", c => new { parent_view_id = c.PlanId, element_ids = new[] { c.Id(c.Wall ?? throw new Skip("the model has no walls")) }, name = "Self-test callout" }),
        new("create_3d_view", c => new { name = "Self-test 3D", level = c.LevelName }),
        new("set_view_graphics", c => new { view_ids = new[] { c.PlanId }, detail_level = "medium" }),
        new("create_view_filter", _ => new { name = "Self-test filter", categories = new[] { "Walls" } }),
        new("apply_view_filter", c => new { filter = "Self-test filter", view_ids = new[] { c.PlanId }, halftone = true }, Setup: ["create_view_filter"]),
        new("color_by_parameter", c => c.Room is not null
            ? new { categories = new[] { "Rooms" }, parameter = "Name", view_ids = new[] { c.PlanId }, name_prefix = "Self-test" }
            : (object)new { categories = new[] { "Walls" }, parameter = "Type", view_ids = new[] { c.PlanId }, name_prefix = "Self-test" }) { Heavy = true },

        // Annotation.
        new("add_annotations", c => new { view_id = c.PlanId,
            text_notes = new[] { new { text = "Self-test note", at = c.At(0, -3000) } },
            detail_lines = new[] { new { points = new[] { c.At(0, -3500), c.At(3000, -3500) } } } }),
        new("update_text_notes", c => new { create = new[] { new { view_id = c.PlanId, x = c.At(0, -4000)[0], y = c.At(0, -4000)[1], text = "Self-test text" } } }),
        new("tag_elements_in_view", c => new { category = "Doors", view_id = c.PlanId }) { Heavy = true },
        new("arrange_tags", c => new { view_id = c.PlanId, action = "report_overlaps" }, Setup: ["tag_elements_in_view"]) { Heavy = true },
        new("dimension_grids", c => new { view_id = c.PlanId }, Setup: ["create_grids"]),
        new("dimension_walls", c => new { view_id = c.PlanId, wall_ids = new[] { c.Id(c.Wall ?? throw new Skip("the model has no walls")) } }),

        // Title blocks and detailing.
        new("set_sheet_title_block", c =>
        {
            var tb = c.Symbol(BuiltInCategory.OST_TitleBlocks) ?? throw new Skip("no title block family loaded");
            return new { sheet_numbers = new[] { "ST-999" }, title_block = $"{tb.FamilyName}: {tb.Name}", move_to_origin = true };
        }, Setup: ["create_sheets"]),
        new("fill_title_blocks", _ => new { sheet_numbers = new[] { "ST-999" }, values = new Dictionary<string, string> { ["Sheet Name"] = "Self-test {number}" } },
            Setup: ["create_sheets", "set_sheet_title_block"]),
        new("add_keynote_tags", c =>
        {
            Need(c.Symbol(BuiltInCategory.OST_KeynoteTags) is not null, "no keynote tag family loaded");
            return new { view_id = c.PlanId, element_ids = new[] { c.Id(c.Wall ?? throw new Skip("the model has no walls")) } };
        }),
        new("place_detail_components", c =>
        {
            var s = c.Symbol(BuiltInCategory.OST_DetailComponents) ?? throw new Skip("no detail item family loaded");
            var family = $"{s.FamilyName}: {s.Name}";
            return s.Family.FamilyPlacementType == FamilyPlacementType.CurveBasedDetail
                ? new { view_id = c.PlanId, family, placements = new object[] { new { start = c.At(0, -6000), end = c.At(2000, -6000) } } }
                : new { view_id = c.PlanId, family, placements = new object[] { new { at = c.At(0, -6000) } } };
        }),
        new("dimension_elements", c =>
        {
            var grids = new FilteredElementCollector(c.Doc).OfClass(typeof(Grid)).Cast<Grid>().Where(g => g.Name is "ST1" or "ST2").Select(g => g.Id.Value).ToArray();
            Need(grids.Length == 2, "create_grids did not create the test grids");
            return new { view_id = c.PlanId, element_ids = grids };
        }, Setup: ["create_grids"]),
        new("create_toposolid", c =>
        {
            double[] P(double x, double y, double z) => c.At(x, y).Append(c.P(z) + Math.Round(UnitUtils.ConvertFromInternalUnits(c.Level?.Elevation ?? 0, c.Unit), 4)).ToArray();
            return new { level = c.LevelName, points = new[] { P(0, 22000, 0), P(8000, 22000, 500), P(8000, 28000, 1200), P(0, 28000, 300), P(4000, 25000, 800) } };
        }),
        new("create_project_parameters", c => new
        {
            shared_parameter_file = Path.Combine(c.Temp, "SharedParameters.txt"),
            parameters = new[] { new { name = "Self-test param", type = "text", categories = new[] { "Sheets" } } },
        }),
        new("edit_family_text", c =>
        {
            var tb = c.Symbol(BuiltInCategory.OST_TitleBlocks) ?? throw new Skip("no title block family loaded");
            return new { family = tb.FamilyName };
        }) { Heavy = true },
        new("place_legend_on_sheets", c =>
        {
            var legend = c.First<View>(v => !v.IsTemplate && v.ViewType == ViewType.Legend) ?? throw new Skip("the model has no legends");
            return new { legend = legend.Name, sheet_numbers = new[] { "ST-999" } };
        }, Setup: ["create_sheets", "set_sheet_title_block"]),
        new("place_bracing", c =>
        {
            var type = c.Symbol(BuiltInCategory.OST_StructuralFraming) ?? throw new Skip("no structural framing family loaded");
            Need(c.Upper is not null, "the model needs two levels");
            double Z(Level l) => Math.Round(UnitUtils.ConvertFromInternalUnits(l.ProjectElevation, c.Unit), 4);
            return new { type_id = type.Id.Value, lines = new[] { new { start = c.At(0, 9000).Append(Z(c.Level!)).ToArray(), end = c.At(3000, 9000).Append(Z(c.Upper!)).ToArray(), level = c.LevelName } } };
        }),
        new("create_steel_connections", _ => new { kind = "beam_column", dry_run = true }) { Heavy = true },
        new("reinforce_beam", c => new { element_ids = new[] { Host(c, BuiltInCategory.OST_StructuralFraming, "concrete beam") }, bottom = new { bar = "16", count = 3 }, top = new { bar = "12", count = 2 }, stirrups = new { bar = "10", spacing = c.P(150) } },
            Setup: ["place_beams"]),
        new("reinforce_column", c => new { element_ids = new[] { Host(c, BuiltInCategory.OST_StructuralColumns, "concrete column") }, bar = "20", bars_x = 3, bars_y = 3, ties = new { bar = "10", spacing = c.P(200) } },
            Setup: ["place_columns"]),
        new("reinforce_footing", c => new { element_ids = new[] { Host(c, BuiltInCategory.OST_StructuralFoundation, "isolated concrete footing", f => f is FamilyInstance) }, bottom = new { bar = "16", spacing = c.P(150) } }),
        new("reinforce_slab_wall", c => new { element_ids = new[] { Host(c, BuiltInCategory.OST_Floors, "structural concrete floor") }, top = new { bar = "12", spacing = c.P(200) }, bottom = new { bar = "12", spacing = c.P(200) } }) { Heavy = true },
        new("rebar_section", c => new { element_ids = new[] { Host(c, BuiltInCategory.OST_StructuralFraming, "concrete beam") }, cut = "cross" }, Setup: ["place_beams", "reinforce_beam"]),
        new("rebar_3d_view", c => new { element_ids = new[] { Host(c, BuiltInCategory.OST_StructuralFraming, "concrete beam") } }, Setup: ["place_beams", "reinforce_beam"]),
        new("export_bar_schedule", c => new { folder = c.Temp }, Setup: ["place_beams", "reinforce_beam"]),
        new("set_sheet_revisions", _ => new { revision = "Self-test revision", sheet_numbers = new[] { "ST-999" } }, Setup: ["create_sheets", "create_revision"]),
        new("dimension_openings", c => new { view_id = c.PlanId, wall_ids = new[] { c.Id(c.Wall ?? throw new Skip("the model has no walls")) } }, Setup: ["place_doors_windows"]),
        new("dimension_rooms", c => { Need(c.Room is not null, "the model has no placed rooms"); return new { view_id = c.PlanId, room_ids = new[] { c.Room!.Id.Value } }; }),
        new("add_spot_elevations", c => new { view_id = c.PlanId, element_ids = new[] { c.Id(c.Floor ?? throw new Skip("the model has no floors")) } }),
        new("import_image", c => new { path = WriteBitmap(c.Temp), view_id = c.PlanId, center = c.At(0, -6000), width = c.P(2000) }),

        // Text, CAD and families.
        new("apply_text", c =>
        {
            var plan = c.Plan ?? throw new Skip("the model has no floor plan");
            return new { items = new[] { new { key = $"{plan.Id.Value}|name", text = plan.Name + " (translated)" } }, mode = "replace" };
        }),
        new("import_cad", c => new { path = WriteDxf(c.Temp), view_id = c.PlanId, link = false, placement = "origin", units = "millimeter" }) { Heavy = true },
        new("list_cad_layers", c => new { import_id = c.Id(LatestImport(c.Doc) ?? throw new Skip("import_cad did not import the test drawing")) }, Setup: ["import_cad"]) { Heavy = true },
        new("convert_cad", c => new { import_id = c.Id(LatestImport(c.Doc) ?? throw new Skip("import_cad did not import the test drawing")), level = c.LevelName,
            conversions = new[] { new { layer = "*", @as = "model_lines" } } }, Setup: ["import_cad"]) { Heavy = true },
        new("create_family_from_shapes", c => new { name = "Self-test box " + Guid.NewGuid().ToString("N").Substring(0, 4),
            shapes = new[] { new { kind = "box", origin = new[] { 0.0, 0.0, 0.0 }, size = new[] { c.P(600), c.P(600), c.P(750) } } } }) { Heavy = true },
        new("run_revit_code", _ => new { code = "var n = new FilteredElementCollector(doc).OfClass(typeof(Level)).GetElementCount();\nreturn n;", description = "self-test change" }),
        new("run_recipe", _ =>
        {
            Need(System.IO.File.Exists(Recipes.PathFor("sketch-promax-self-test")), "the self-test recipe was not saved");
            return new { name = "sketch-promax-self-test" };
        }),

        // 0.26 tools: worksharing, detailing and view repair, dimensions, cuts and voids.
        new("list_worksets", _ => new { count_elements = true }),
        new("element_owners", c => c.Doc.IsWorkshared ? new { view_id = c.PlanId, only_locked = true, limit = 20 } : throw new Skip("the model is not workshared")),
        new("make_worksets_editable", c => c.Doc.IsWorkshared ? new { worksets = new[] { new FilteredWorksetCollector(c.Doc).OfKind(WorksetKind.UserWorkset).ToWorksets().First().Name } } : throw new Skip("the model is not workshared")) { Heavy = true },
        new("borrow_elements", c => c.Doc.IsWorkshared ? new { element_ids = new[] { c.Id(c.Wall ?? throw new Skip("the model has no walls")) } } : throw new Skip("the model is not workshared")) { Heavy = true },
        new("relinquish_mine", c => !c.Throwaway ? throw new Skip("releasing ownership can't be rolled back; runs only in the field test's own project") : c.Doc.IsWorkshared ? new { borrowed_elements = true } : throw new Skip("the model is not workshared")) { Heavy = true, NoGroup = true },
        new("place_detail_item_copy", c => new { view_id = c.PlanId, source_id = new FilteredElementCollector(c.Doc).OfCategory(BuiltInCategory.OST_DetailComponents).OfClass(typeof(FamilyInstance)).Max(e => (long?)e.Id.Value) ?? throw new Skip("no detail item to copy"), placements = new[] { new { at = c.At(3000, 3000), rotation_degrees = 90.0 } } }, Setup: ["place_detail_components"]),
        new("place_additional_reinforcement", c =>
        {
            var s = c.Symbol(BuiltInCategory.OST_DetailComponents, x => x.Family.FamilyPlacementType == FamilyPlacementType.CurveBasedDetail) ?? throw new Skip("no line-based detail item family loaded");
            return new { view_id = c.PlanId, family = $"{s.FamilyName}: {s.Name}", bars = new[] { new { start = c.At(0, -7000), end = c.At(3000, -7000), group = "Self-test RFT" } } };
        }),
        new("place_mesh_symbols", c =>
        {
            var s = c.Symbol(BuiltInCategory.OST_DetailComponents, x => x.Family.FamilyPlacementType != FamilyPlacementType.CurveBasedDetail) ?? throw new Skip("no point-based detail item family loaded");
            return new { view_id = c.PlanId, family = $"{s.FamilyName}: {s.Name}", slab_ids = new[] { Newest<Floor>(c) } };
        }, Setup: ["create_floors"]),
        new("place_column_section_details", c =>
        {
            var s = c.Symbol(BuiltInCategory.OST_DetailComponents, x => x.Family.FamilyPlacementType != FamilyPlacementType.CurveBasedDetail) ?? throw new Skip("no point-based detail item family loaded");
            return new { view_id = c.PlanId, family = $"{s.FamilyName}: {s.Name}", cells = new[] { new { label = "C1" }, new { label = "C2" } },
                grid = new { origin = c.At(0, -9000), spacing = new[] { c.P(1500), c.P(1500) }, per_row = 2 } };
        }),
        new("duplicate_view_to_location", c => new { view_id = Newest<ViewSection>(c), offset = new[] { c.P(2000), 0 }, name = "Self-test moved section" }, Setup: ["create_section"]),
        new("relocate_view_with_detailing", c => new { view_id = Newest<ViewSection>(c), offset = new[] { 0, c.P(1000) } }, Setup: ["create_section"]),
        new("copy_view_detailing", c => new { source_view_id = c.PlanId, target_view_id = c.PlanId, offset = new[] { c.P(500), 0 } }, Setup: ["place_detail_components"]),
        new("add_dimensions", c =>
        {
            var grids = new FilteredElementCollector(c.Doc).OfClass(typeof(Grid)).Cast<Grid>().Where(g => g.Name is "ST1" or "ST2").Select(g => g.Id.Value).ToArray();
            Need(grids.Length == 2, "create_grids did not create the test grids");
            return new { view_id = c.PlanId, items = new object[] { new { element_ids = grids, label = "grids" }, new { element_ids = new[] { grids[0] }, label = "one ref (should fail alone)" } } };
        }, Setup: ["create_grids"]),
        new("copy_dimensions", c =>
        {
            var dim = c.First<Dimension>(d => d.OwnerViewId == c.Plan!.Id) ?? throw new Skip("no dimension in the plan");
            var target = c.First<ViewPlan>(v => !v.IsTemplate && v.Id != c.Plan!.Id && v.GenLevel?.Id == c.Plan!.GenLevel?.Id) ?? throw new Skip("no second plan of the same level");
            return new { source_view_id = c.PlanId, target_view_ids = new[] { target.Id.Value }, dimension_ids = new[] { dim.Id.Value }, list_only = true };
        }, Setup: ["create_grids", "dimension_grids"]),
        new("reflow_annotations", c => new { view_id = c.PlanId, dry_run = true }, Setup: ["tag_elements_in_view"]) { Heavy = true },
        new("trim_matchlines", c =>
        {
            // The API can't create matchlines (Revit has no method for it), so this only runs where the plan has one.
            Need(new FilteredElementCollector(c.Doc, c.Plan?.Id ?? throw new Skip("the model has no floor plan")).OfCategory(BuiltInCategory.OST_Matchline)
                .WhereElementIsNotElementType().GetElementCount() > 0, "the plan has no matchlines (the API can't add one)");
            return new { view_ids = new[] { c.PlanId }, mode = "report" };
        }),
        new("place_void_cuts", c =>
        {
            var sym = c.Symbol(BuiltInCategory.OST_GenericModel, s => s.Family.FamilyPlacementType == FamilyPlacementType.WorkPlaneBased && s.Family.get_Parameter(BuiltInParameter.FAMILY_ALLOW_CUT_WITH_VOIDS)?.AsInteger() == 1) ?? throw new Skip("no face-based cutting void family loaded");
            return new { family = $"{sym.FamilyName}: {sym.Name}", voids = new[] { new { host_id = Newest<Floor>(c), face = "top", point = c.At(2000, 2000) } } };
        }, Setup: ["create_floors"]) { Heavy = true },
        new("verify_void_cut", c => new { element_ids = new[] { Newest<Floor>(c) } }, Setup: ["create_floors"]),
        new("cut_openings_in_joined", c => new { openings = new[] { new { host_id = Newest<Floor>(c), outline = c.Square(1000, 1000, 500) } } }, Setup: ["create_floors"]),
        new("create_floors_from_faces", c => new { element_ids = new[] { Newest<Floor>(c) }, face = "bottom", level = c.LevelName }, Setup: ["create_floors"]),
        new("cut_geometry", c => new { action = "join", pairs = new[] { new { cutting = c.Id(c.Wall ?? throw new Skip("the model has no walls")), cut = Newest<Floor>(c) } }, only_overlapping = false }, Setup: ["create_floors"]),

        new("delete_elements", c => new { element_ids = new[] { c.Id(c.Wall ?? throw new Skip("the model has no walls")) } }),
    ];


    /// <summary>
    /// Regression cases for bugs found in use (0.25: the 0.24 field test). A tool may appear here again with the
    /// arguments that broke it; a setup step "tool" uses the tool's first case, "tool#2" its second. Run in full mode only.
    /// </summary>
    private static IEnumerable<Case> RegressionCases() =>
    [
        // 0.27: with no source given, an open active view is captured from the screen.
        new("capture_view_image", c => new { pixel_width = 800 }) { Heavy = true },
        // 0.26 regressions from the field bug log.
        new("set_parameters", c => new { changes = new[] { new { element_id = c.Id(c.Wall ?? throw new Skip("the model has no walls")), parameter = "Comments", value = "Sketch ProMax self-test" }, new { element_id = c.Id(c.Wall!), parameter = "Mark", value = "ST-1" } }, separate_steps = true }),
        new("query_elements", c =>
        {
            Need(c.Symbol(BuiltInCategory.OST_DetailComponents, s => s.FamilyName.IndexOf("Break Line", StringComparison.OrdinalIgnoreCase) >= 0) is not null,
                "no 'Break Line' detail item family is loaded (the Imperial templates don't have one)");
            return new { category = "Detail Items", family = "Break Line", limit = 5 };
        }),
        new("move_copy_rotate", c => new { element_ids = new[] { c.Id(c.First<Group>() ?? throw new Skip("the model has no model groups")) }, offset = new[] { 0, 0, c.P(500) }, copy = true }),
        new("capture_view_image", c => new { view_id = c.PlanId, pixel_width = 800, resolution_dpi = 300 }) { Heavy = true },
        new("find_clashes", c => new { set_a = new { categories = new[] { "Walls", "Floors", "Generic Models" } }, level = c.LevelName, limit = 50, folder = c.Temp, voids = "rebuild" }) { Heavy = true },
        new("add_spot_elevations", c => new { view_id = c.PlanId, element_ids = new[] { Host(c, BuiltInCategory.OST_StructuralFoundation, "isolated concrete footing", f => f is FamilyInstance) } }),
        new("dimension_elements", c => new { view_id = c.PlanId, element_ids = new[] { Host(c, BuiltInCategory.OST_StructuralColumns, "concrete column") }, direction = "x", family_refs = "faces" }, Setup: ["place_columns"]),
        new("add_annotations", c => new { view_id = c.PlanId, dependent_view = "clip_to_crop", detail_lines = new[] { new { points = new[] { c.At(0, -5000), c.At(3000, -5000) } } } }),
        // 0.26: long snippets can report progress and be stopped (CheckCancel compiles and returns normally when not cancelled).
        new("run_revit_query_code", _ => new { code = "var n = 0; foreach (var id in new FilteredElementCollector(doc).OfClass(typeof(Level)).ToElementIds()) { CheckCancel(); n++; log.AppendLine(\"level \" + n); }\nreturn n;", description = "self-test cancel probe" }),
        new("list_types", _ => new { category = "Slab Edges" }),
        new("collect_text", c => new { view_ids = new[] { c.PlanId }, include = new[] { "view_names" }, text_contains = "Level", limit = 50 }),
        // A placeholder standing for a whole value must reach the tool as the schema's type (here an integer).
        new("save_recipe", _ => new { name = "sketch-promax-self-test-typed", description = "Self-test typed placeholder", steps = new[] { new { tool = "collect_text", arguments = new { limit = "{n}" } } } }),
        new("create_roof", c => new { points = c.Square(5000, 6000, 4000), level = c.Upper?.Name ?? c.LevelName, slope_degrees = 25, overhang = c.P(500) }),
        new("create_roof", c => new { points = c.Square(10000, 6000, 4000), level = c.Upper?.Name ?? c.LevelName, slope_degrees = 25, gable_edges = new[] { 1, 3 } }),
        new("create_levels", c => new { levels = new[] { new { name = "Self-test level displayed", elevation = c.P(new FilteredElementCollector(c.Doc).OfClass(typeof(Level)).Cast<Level>().Select(l => l.Elevation).DefaultIfEmpty(0).Max() * 304.8 + 4000) } }, create_plans = false }),
        new("place_columns", c =>
        {
            var type = c.Symbol(BuiltInCategory.OST_StructuralColumns) ?? c.Symbol(BuiltInCategory.OST_Columns) ?? throw new Skip("no column family loaded");
            return new { type_id = type.Id.Value, base_level = c.LevelName, top_level = c.UpperName, at_grid_intersections = true, grids = new[] { "ST1", "ST2", "STA" },
                area = new { min = c.At(-1000, -3000), max = c.At(7000, 13000) }, architectural = type.Category?.Id.Value == (long)BuiltInCategory.OST_Columns };
        }, Setup: ["create_grids"]),
        new("create_walls", c => new { segments = new[] {
            new { start = c.At(0, 20000), end = c.At(4000, 20000) }, new { start = c.At(4000, 20000), end = c.At(4000, 23000) },
            new { start = c.At(4000, 23000), end = c.At(0, 23000) }, new { start = c.At(0, 23000), end = c.At(0, 20000) } },
            level = c.LevelName, height = c.P(3000) }),
        new("add_slab_edges", c =>
        {
            Need(c.First<SlabEdgeType>() is not null, "the model has no slab edge types");
            return new { floor_ids = new[] { Newest<Floor>(c) } };
        }, Setup: ["create_floors"]),
        new("create_stairs", c => new { base_level = c.LevelName, top_level = c.UpperName, start = c.At(8000, 4000), direction = new[] { 0, 1 }, width = c.P(1000), shape = "u_left", railings = false }),
        new("create_stairs", c => new { base_level = c.LevelName, top_level = c.UpperName, start = c.At(12000, 4000), direction = new[] { 1, 0 }, width = c.P(1000), shape = "l_right", railings = false }),
        new("hollow_core_layout", c => new { points = c.Square(0, 14000, 6000), level = c.LevelName, plank_width = c.P(1200), topping = c.P(75) }),
        new("create_railings", c =>
        {
            Need(c.First<RailingType>() is not null, "the model has no railing types");
            return new { host_id = Newest<Stairs>(c) };
        }, Setup: ["create_stairs"]),
        new("create_mep_runs", c =>
        {
            Need(c.First<Autodesk.Revit.DB.Plumbing.PipeType>() is not null, "the model has no pipe types");
            return new { kind = "pipe", level = c.LevelName, points = new[] { c.At(0, 12000).Append(c.P(2800)).ToArray(), c.At(4000, 12000).Append(c.P(2800)).ToArray(), c.At(4000, 16000).Append(c.P(2800)).ToArray() }, diameter = c.P(50) };
        }),
        new("create_sheets", c => new { sheets = new[] { new { number = "ST-998", name = "Self-test TB match",
            title_block = c.Symbol(BuiltInCategory.OST_TitleBlocks) is { } tb ? $"{tb.FamilyName}: {tb.Name}" : throw new Skip("no title block loaded") } } }),
        new("run_model_audit", _ => new { }, Setup: ["create_schedule", "create_sheets"]),
        new("create_drafting_view", c => new { name = "Self-test styles " + Guid.NewGuid().ToString("N").Substring(0, 4),
            lines = new[] { new { start = new[] { 0.0, 0.0 }, end = new[] { c.P(1000), 0.0 }, style = "Wide Lines" } } }),
        new("import_image", c => new { path = WriteBitmap(c.Temp), view_id = c.First<ViewSheet>(s => s.IsPlaceholder)?.Id.Value ?? throw new Skip("no placeholder sheet") }),
        new("convert_cad", c => new { import_id = c.Id(LatestImport(c.Doc) ?? throw new Skip("import_cad did not import the test drawing")), level = c.LevelName,
            conversions = new[] { new { layer = "SELFTEST", @as = "grids", auto_name = true } } }, Setup: ["import_cad"]) { Heavy = true },
        new("convert_cad", c => new { import_id = c.Id(LatestImport(c.Doc) ?? throw new Skip("import_cad did not import the test drawing")), level = c.LevelName,
            conversions = new[] { new { layer = "SELFTEST", @as = "walls" } } }, Setup: ["import_cad"]) { Heavy = true },
        new("create_section", c => new { start = c.At(-1500, 2500), end = c.At(13500, 2500), depth = c.P(4000), name = "Self-test section line" }),
        new("create_3d_view", c => new { name = "Self-test 3D box", element_ids = new[] { c.Id(c.Wall ?? throw new Skip("the model has no walls")) }, show_all = true }),
        new("run_recipe", _ =>
        {
            Need(System.IO.File.Exists(Recipes.PathFor("sketch-promax-self-test-typed")), "the typed self-test recipe was not saved");
            return new { name = "sketch-promax-self-test-typed", values = new Dictionary<string, string> { ["n"] = "5" } };
        }),
        new("tag_elements_in_view", c => new { category = "Rooms", view_id = c.PlanId }, Setup: ["place_rooms"]) { Heavy = true },
        // 0.27.2 field test (Revit 2024): a stair that already has railings is reported (or its railings replaced), not an error.
        new("create_railings", c =>
        {
            Need(c.First<RailingType>() is not null, "the model has no railing types");
            return new { host_id = Newest<Stairs>(c), replace_existing = true };
        }, Setup: ["create_stairs", "create_railings#2"]),
        // A notch at the corner of a plain new floor (the case above cuts the model's floor, which may carry slab edges).
        new("cut_slab", c =>
        {
            var floor = Newest<Floor>(c);
            var box = c.Doc.GetElement(new ElementId(floor)).get_BoundingBox(null) ?? throw new Skip("the floor has no extents");
            double U(double ft) => Math.Round(UnitUtils.ConvertFromInternalUnits(ft, c.Unit), 4);
            var x = box.Max.X; var y = box.Max.Y; var d = 500 / 304.8;
            return new { floor_id = floor, points = new[] { new[] { U(x - d), U(y - d) }, new[] { U(x + d), U(y - d) }, new[] { U(x + d), U(y + d) }, new[] { U(x - d), U(y + d) } } };
        }, Setup: ["create_floors"]),
        // A second door asked for exactly where one already is: slid along the wall instead of "not cutting anything".
        new("place_doors_windows", c =>
        {
            Need(c.Wall is not null, "the model has no walls");
            Need(c.Door is not null, "the model has no door types loaded");
            var length = ((LocationCurve)c.Wall!.Location).Curve.Length * 304.8;
            var item = new { wall_id = c.Wall.Id.Value, type_id = c.Door!.Id.Value, distance_along_wall = c.P(length / 2) };
            return new { items = new[] { item, item } };
        }),
        // dimension_walls on the four walls of a room (the case above gives one wall and relies on its facing partner).
        new("dimension_walls", c =>
        {
            var walls = new FilteredElementCollector(c.Doc, c.Plan?.Id ?? throw new Skip("the model has no floor plan")).OfClass(typeof(Wall)).Cast<Wall>()
                .Where(w => w.Location is LocationCurve { Curve: Line }).Take(8).Select(w => w.Id.Value).ToArray();
            Need(walls.Length >= 2, "the plan shows fewer than two straight walls");
            return new { view_id = c.PlanId, wall_ids = walls };
        }),
        new("create_elevations", c => new { mode = "building", plan_view_id = c.PlanId, sides = new[] { "North", "East", "South", "West" }, name_pattern = "{side} self-test 2" }) { Heavy = true },
        new("in_place_guide", _ => new { action = "list", limit = 5, offset = 0 }),
        new("arrange_tags", c => new { view_id = c.PlanId, tag_category = "Room Tags", action = "report_overlaps" }, Setup: ["place_rooms", "tag_elements_in_view#2"]) { Heavy = true },
        new("place_rooms", c => new { level = c.LevelName }, Setup: ["place_rooms"]) { Heavy = true },
        new("set_coordinates", _ => new { latitude = 25.2, longitude = 55.27, place_name = "Self-test place" }),
        new("format_schedule", _ => new { schedule = "Self-test wall schedule", sort_by = new[] { new { field = "Type" } } }, Setup: ["create_schedule"]),
        new("export_files", c => new { format = "ifc", ifc_version = "IFC4", folder = c.Temp, file_name = "selftest-ifc4" }) { Heavy = true },
        new("run_revit_code", _ => new { code = "using (var t = new Transaction(doc, \"st\")) { t.Start(); var n = new FilteredElementCollector(doc).OfClass(typeof(Level)).GetElementCount(); t.Commit(); return n; }", description = "self-test own transactions", own_transactions = true }),
    ];

    private static ImportInstance? LatestImport(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>()
            .OrderByDescending(i => i.Id.Value).FirstOrDefault();

    /// <summary>A 2x2 pixel 24-bit bitmap.</summary>
    private static string WriteBitmap(string folder)
    {
        var path = Path.Combine(folder, "self-test.bmp");
        var pixels = new byte[] { 0, 0, 255, 0, 255, 0, 0, 0, 255, 0, 0, 255, 255, 255, 0, 0 }; // 2 rows of 2 BGR pixels, padded to 4 bytes
        using var w = new BinaryWriter(System.IO.File.Create(path));
        w.Write((byte)'B'); w.Write((byte)'M'); w.Write(54 + pixels.Length); w.Write(0); w.Write(54);
        w.Write(40); w.Write(2); w.Write(2); w.Write((short)1); w.Write((short)24); w.Write(0); w.Write(pixels.Length);
        w.Write(2835); w.Write(2835); w.Write(0); w.Write(0);
        w.Write(pixels);
        return path;
    }

    /// <summary>A minimal DXF with two lines on layer SELFTEST.</summary>
    private static string WriteDxf(string folder)
    {
        var path = Path.Combine(folder, "self-test.dxf");
        var lines = new[]
        {
            "0", "SECTION", "2", "HEADER", "9", "$ACADVER", "1", "AC1009", "9", "$INSUNITS", "70", "4", "0", "ENDSEC",
            "0", "SECTION", "2", "ENTITIES",
            "0", "LINE", "8", "SELFTEST", "10", "0", "20", "0", "30", "0", "11", "5000", "21", "0", "31", "0",
            "0", "LINE", "8", "SELFTEST", "10", "0", "20", "0", "30", "0", "11", "0", "21", "3000", "31", "0",
            "0", "ENDSEC", "0", "EOF",
        };
        System.IO.File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n");
        return path;
    }

    private static string Short(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";
}

public sealed class RunSelfTestTool : IRevitTool
{
    public string Name => "run_self_test";
    public string Description =>
        "Check that Sketch ProMax works in this Revit version and model. Starts in the background and returns at once; " +
        "call self_test_status for progress and the result. Each tool runs once, one at a time, so Revit stays usable. " +
        "mode 'safe' (default) runs only read-only tools. mode 'full' also runs the editing tools, each inside a " +
        "transaction group rolled back straight away, so the model is left unchanged; only use full on a copy of a " +
        "model and only when the user asks for it. Slow tools are skipped on large models.";
    public string PropertiesJson => """
    {
      "mode": {"type": "string", "enum": ["safe", "full"]},
      "stop": {"type": "boolean", "description": "Stop a running self-test."}
    }
    """;
    public string[] Required => [];
    // Full mode undoes everything it changes, so the usual change approval is not asked; Execute asks its own question.
    public bool ModifiesModel => false;

    private readonly ToolRegistry _registry;
    public RunSelfTestTool(ToolRegistry registry) => _registry = registry;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        if (args.OptBool("stop"))
        {
            SelfTest.Stop();
            return ToolResult.Ok(new { status = "Stopping after the current step." });
        }
        var mode = args.OptString("mode") == "full" ? SelfTest.Mode.Full : SelfTest.Mode.Safe;
        if (mode == SelfTest.Mode.Full && !SelfTest.IsRunning && app.ActiveUIDocument?.Document is { } doc && !SelfTest.ConfirmFull(doc))
            return ToolResult.Error("The user declined the full self-test on this model. Offer the safe mode, or ask them to open a copy of the model first.");
        return ToolResult.Ok(new { status = SelfTest.Start(app, _registry, mode) });
    }
}

public sealed class SelfTestStatusTool : IRevitTool
{
    public string Name => "self_test_status";
    public string Description => "Progress of the running self-test, or the result of the last one: counts, every failure, and the report file.";
    public string PropertiesJson => "{}";
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var report = SelfTest.LastReport;
        List<SelfTest.Outcome> results;
        lock (report?.Results ?? new List<SelfTest.Outcome>()) results = report?.Results.ToList() ?? [];
        return ToolResult.Ok(new
        {
            status = SelfTest.Progress(),
            running = SelfTest.IsRunning,
            failures = results.Where(r => r.Status == "fail"),
            skipped = results.Where(r => r.Status == "skip").Select(r => $"{r.Tool}: {r.Message}"),
            passed = results.Where(r => r.Status == "pass").Select(r => $"{r.Tool} ({r.Ms} ms)"),
            report_file = report?.File,
        });
    }
}
