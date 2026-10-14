using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.UI;

namespace SketchProMax.Tools;

/// <summary>
/// Calls every tool once on the open model with inputs picked from that model, and reports which ones fail.
/// Read-only tools run first. Tools that change the model then run inside one transaction group that is rolled back
/// at the end, so the model is left exactly as it was. Files go to a temporary folder that is deleted afterwards.
/// </summary>
internal static class SelfTest
{
    public sealed record Outcome(string Tool, string Status, long Ms, string Message);

    public sealed class Report
    {
        public string Model { get; init; } = "";
        public string RevitVersion { get; init; } = "";
        public string AddinVersion { get; init; } = "";
        public DateTime Ran { get; init; } = DateTime.Now;
        public List<Outcome> Results { get; } = [];
        public int Passed => Results.Count(r => r.Status == "pass");
        public int Failed => Results.Count(r => r.Status == "fail");
        public int Skipped => Results.Count(r => r.Status == "skip");
        public string? File { get; set; }

        public string Summary()
        {
            var sb = new StringBuilder($"{Passed} passed, {Failed} failed, {Skipped} skipped (of {Results.Count} tools).");
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

    private sealed record Case(string Tool, Func<Ctx, object> Args);

    public static Report Run(UIApplication app, ToolRegistry registry, bool includeChanges, IProgress<string>? progress = null)
    {
        var doc = app.ActiveUIDocument?.Document ?? throw new InvalidOperationException("Open a model first.");
        var report = new Report
        {
            Model = doc.Title,
            RevitVersion = app.Application.VersionNumber,
            AddinVersion = typeof(SelfTest).Assembly.GetName().Version?.ToString() ?? "",
        };
        var temp = Path.Combine(Path.GetTempPath(), "SketchProMax-self-test-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(temp);
        var ctx = Collect(app, doc, registry, temp);

        var tested = new HashSet<string>();
        void RunCase(Case c)
        {
            tested.Add(c.Tool);
            progress?.Report(c.Tool);
            var tool = registry.Find(c.Tool);
            if (tool is null) { report.Results.Add(new(c.Tool, "fail", 0, "Tool not found in the registry.")); return; }
            var watch = Stopwatch.StartNew();
            try
            {
                var raw = JsonSerializer.SerializeToElement(c.Args(ctx));
                var args = raw.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
                var result = tool.Execute(app, new ToolArgs(args));
                if (!result.IsError && result.Text.Length > 24000)
                    report.Results.Add(new(c.Tool, "fail", watch.ElapsedMilliseconds, $"Output too large for a chat ({result.Text.Length:N0} characters)."));
                else
                    report.Results.Add(new(c.Tool, result.IsError ? "fail" : "pass", watch.ElapsedMilliseconds, Short(result.Text, 600)));
            }
            catch (Skip s) { report.Results.Add(new(c.Tool, "skip", 0, s.Message)); }
            catch (Exception ex)
            {
                if (ex is System.Reflection.TargetInvocationException { InnerException: { } inner }) ex = inner;
                report.Results.Add(new(c.Tool, "fail", watch.ElapsedMilliseconds, $"{ex.GetType().Name}: {ex.Message}"));
            }
        }

        foreach (var c in ReadOnlyCases()) RunCase(c);

        if (includeChanges)
        {
            using var group = new TransactionGroup(doc, "Sketch ProMax: self-test (rolled back)");
            group.Start();
            try
            {
                foreach (var c in ChangeCases()) RunCase(c);
            }
            finally
            {
                // Close anything a failed tool left open, then undo the whole run.
                if (group.HasStarted() && !group.HasEnded()) group.RollBack();
            }
        }

        foreach (var c in FileCases()) RunCase(c);

        foreach (var tool in registry.All.Where(t => !tested.Contains(t.Name)).OrderBy(t => t.Name))
        {
            var reason = tool.Name switch
            {
                "batch_run_recipe" => "opens other model files; run it by hand on a folder of copies",
                "load_family" => "needs a family file on this PC",
                "run_learned_tool" => "tested through save_as_tool",
                "run_self_test" => "this test",
                _ when !includeChanges && tool.ModifiesModel => "changes the model; run the self-test with changes",
                _ => "no test written yet",
            };
            report.Results.Add(new(tool.Name, "skip", 0, reason));
        }

        try { Directory.Delete(temp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }

        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax", "self-test");
            Directory.CreateDirectory(folder);
            var safe = string.Concat(doc.Title.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
            report.File = Path.Combine(folder, $"{DateTime.Now:yyyyMMdd-HHmmss} {safe} Revit {report.RevitVersion}.json");
            System.IO.File.WriteAllText(report.File, JsonSerializer.Serialize(new
            {
                report.Model, report.RevitVersion, report.AddinVersion, report.Ran, report.Passed, report.Failed, report.Skipped,
                results = report.Results.OrderBy(r => r.Status == "fail" ? 0 : r.Status == "skip" ? 2 : 1).ThenBy(r => r.Tool),
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException) { report.File = null; }
        return report;
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
        new("read_schedule", c => new { schedule_id = c.Id(c.Schedule ?? throw new Skip("the model has no schedules")), max_rows = 20 }),
        new("select_elements", c => new { element_ids = new[] { c.Id(c.Wall ?? throw new Skip("the model has no walls")) }, zoom = false }),
        new("run_model_audit", _ => new { }),
        new("capture_view_image", c => new { view_id = c.PlanId, pixel_width = 800 }),
        new("list_view_filters", _ => new { }),
        new("query_linked_models", _ => new { }),
        new("collect_text", _ => new { limit = 50 }),
        new("get_coordinates", _ => new { }),
        new("convert_coordinates", _ => new { points = new[] { new[] { 0.0, 0.0, 0.0 } }, from = "internal" }),
        new("get_activity_log", _ => new { limit = 5 }),
        new("list_recipes", _ => new { }),
        new("list_learned_tools", _ => new { }),
        new("run_revit_query_code", _ => new { code = "return new { walls = new FilteredElementCollector(doc).OfClass(typeof(Wall)).GetElementCount(), title = doc.Title };", description = "self-test query" }),
        new("create_mep_openings", _ => new { method = "report", limit = 50 }),
    ];

    /// <summary>Exports: they write files to the temporary folder and cannot run inside a transaction group.</summary>
    private static IEnumerable<Case> FileCases() =>
    [
        new("export_schedule", c => new { schedule_id = c.Id(c.Schedule ?? throw new Skip("the model has no schedules")), folder = c.Temp, file_name = "schedule" }),
        new("export_pdf", c => new { view_ids = new[] { c.Sheet?.Id.Value ?? c.PlanId }, folder = c.Temp, file_name = "sheets" }),
        new("export_files", c => new { format = "dwg", view_ids = new[] { c.PlanId }, folder = c.Temp, file_name = "plan" }),
        new("export_bcf", c => new { issues = new[] { new { title = "Self-test issue", element_ids = c.Wall is null ? Array.Empty<long>() : [c.Wall.Id.Value] } }, folder = c.Temp, file_name = "issues" }),
        new("save_recipe", _ => new { name = "sketch-promax-self-test", description = "Self-test", steps = new[] { new { tool = "get_model_context", arguments = new { } } } }),
        new("save_as_tool", _ => new
        {
            name = "self_test_count_walls", description = "Self-test: counts walls.", changes_model = false,
            code = "return new FilteredElementCollector(doc).OfClass(typeof(Wall)).GetElementCount() + Convert.ToInt32(args[\"add\"]);",
            parameters = new { add = new { type = "integer" } }, required = new[] { "add" },
        }),
        new("self_test_count_walls", _ => new { add = 0 }),
        new("delete_learned_tool", _ => new { name = "self_test_count_walls" }),
    ];

    private static IEnumerable<Case> ChangeCases() =>
    [
        // Levels, grids and new geometry go in an empty area east of the model.
        new("create_levels", c => new { levels = new[] { new { name = "Self-test level", elevation = c.P(new FilteredElementCollector(c.Doc).OfClass(typeof(Level)).Cast<Level>().Select(l => l.Elevation).DefaultIfEmpty(0).Max() * 304.8 + 3000) } }, create_plans = true }),
        new("create_grids", c => new { lines = new[] { new { name = "ST1", start = c.At(0, -2000), end = c.At(0, 12000) }, new { name = "ST2", start = c.At(6000, -2000), end = c.At(6000, 12000) } } }),
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
        new("place_rooms", c => new { level = c.LevelName }),

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
        new("hollow_core_layout", c => new { floor_id = c.Id(c.Floor ?? throw new Skip("the model has no floors")), original = "keep" }),
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
        }),
        new("create_schedule", _ => new { category = "Walls", name = "Self-test wall schedule", fields = new[] { "Type", "Length" }, sort_by_first_field = true }),
        new("format_schedule", _ => new { schedule = "Self-test wall schedule", grand_totals = true }),
        new("create_sheet_list", _ => new { name = "Self-test drawing list" }),
        new("create_revision", _ => new { description = "Self-test revision" }),
        new("cloud_elements", c =>
        {
            var revision = c.First<Revision>() ?? throw new Skip("no revision");
            return new { revision_id = revision.Id.Value, element_ids = new[] { c.Id(c.Wall ?? throw new Skip("the model has no walls")) }, view_id = c.PlanId };
        }),
        new("create_section", c => new { element_ids = new[] { c.Id(c.Wall ?? throw new Skip("the model has no walls")) }, name = "Self-test section" }),
        new("create_elevations", c => new { mode = "building", plan_view_id = c.PlanId, sides = new[] { "North" }, name_pattern = "{side} self-test" }),
        new("create_callout", c => new { parent_view_id = c.PlanId, element_ids = new[] { c.Id(c.Wall ?? throw new Skip("the model has no walls")) }, name = "Self-test callout" }),
        new("create_3d_view", c => new { name = "Self-test 3D", level = c.LevelName }),
        new("set_view_graphics", c => new { view_ids = new[] { c.PlanId }, detail_level = "medium" }),
        new("create_view_filter", _ => new { name = "Self-test filter", categories = new[] { "Walls" } }),
        new("apply_view_filter", c => new { filter = "Self-test filter", view_ids = new[] { c.PlanId }, halftone = true }),
        new("color_by_parameter", c => c.Room is not null
            ? new { categories = new[] { "Rooms" }, parameter = "Name", view_ids = new[] { c.PlanId }, name_prefix = "Self-test" }
            : (object)new { categories = new[] { "Walls" }, parameter = "Type", view_ids = new[] { c.PlanId }, name_prefix = "Self-test" }),

        // Annotation.
        new("add_annotations", c => new { view_id = c.PlanId,
            text_notes = new[] { new { text = "Self-test note", at = c.At(0, -3000) } },
            detail_lines = new[] { new { points = new[] { c.At(0, -3500), c.At(3000, -3500) } } } }),
        new("update_text_notes", c => new { create = new[] { new { view_id = c.PlanId, x = c.At(0, -4000)[0], y = c.At(0, -4000)[1], text = "Self-test text" } } }),
        new("tag_elements_in_view", c => new { category = "Doors", view_id = c.PlanId }),
        new("arrange_tags", c => new { view_id = c.PlanId, action = "report_overlaps" }),
        new("dimension_grids", c => new { view_id = c.PlanId }),
        new("dimension_walls", c => new { view_id = c.PlanId, wall_ids = new[] { c.Id(c.Wall ?? throw new Skip("the model has no walls")) } }),
        new("dimension_openings", c => new { view_id = c.PlanId }),
        new("dimension_rooms", c => { Need(c.Room is not null, "the model has no placed rooms"); return new { view_id = c.PlanId, room_ids = new[] { c.Room!.Id.Value } }; }),
        new("add_spot_elevations", c => new { view_id = c.PlanId }),
        new("import_image", c => new { path = WriteBitmap(c.Temp), view_id = c.PlanId, center = c.At(0, -6000), width = c.P(2000) }),

        // Text, CAD and families.
        new("apply_text", c =>
        {
            var plan = c.Plan ?? throw new Skip("the model has no floor plan");
            return new { items = new[] { new { key = $"{plan.Id.Value}|name", text = plan.Name + " (translated)" } }, mode = "replace" };
        }),
        new("import_cad", c => new { path = WriteDxf(c.Temp), view_id = c.PlanId, link = false, placement = "origin", units = "millimeter" }),
        new("list_cad_layers", c => new { import_id = c.Id(LatestImport(c.Doc) ?? throw new Skip("import_cad did not import the test drawing")) }),
        new("convert_cad", c => new { import_id = c.Id(LatestImport(c.Doc) ?? throw new Skip("import_cad did not import the test drawing")), level = c.LevelName,
            conversions = new[] { new { layer = "*", @as = "model_lines" } } }),
        new("create_family_from_shapes", c => new { name = "Self-test box " + Guid.NewGuid().ToString("N").Substring(0, 4),
            shapes = new[] { new { kind = "box", origin = new[] { 0.0, 0.0, 0.0 }, size = new[] { c.P(600), c.P(600), c.P(750) } } } }),
        new("run_revit_code", _ => new { code = "var n = new FilteredElementCollector(doc).OfClass(typeof(Level)).GetElementCount();\nreturn n;", description = "self-test change" }),
        new("run_recipe", _ =>
        {
            Need(System.IO.File.Exists(Recipes.PathFor("sketch-promax-self-test")), "the self-test recipe was not saved");
            return new { name = "sketch-promax-self-test" };
        }),

        new("delete_elements", c => new { element_ids = new[] { c.Id(c.Wall ?? throw new Skip("the model has no walls")) } }),
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
        "Check that Sketch ProMax works in this Revit version and model: calls every tool once with inputs taken from the " +
        "open model and reports pass, fail or skip for each. With include_changes, tools that change the model run too, " +
        "inside one transaction group that is rolled back at the end, so the model is left unchanged. Use it when the " +
        "user asks whether everything works, or after an update. Takes a minute or two on a large model.";
    public string PropertiesJson => """{"include_changes": {"type": "boolean", "description": "Default true."}}""";
    public string[] Required => [];
    // Everything it changes is rolled back, so it needs no approval.
    public bool ModifiesModel => false;

    private readonly ToolRegistry _registry;
    public RunSelfTestTool(ToolRegistry registry) => _registry = registry;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var report = SelfTest.Run(app, _registry, args.OptBool("include_changes", true));
        return ToolResult.Ok(new
        {
            summary = report.Summary(),
            passed = report.Passed, failed = report.Failed, skipped = report.Skipped,
            failures = report.Results.Where(r => r.Status == "fail"),
            skips = report.Results.Where(r => r.Status == "skip").Select(r => $"{r.Tool}: {r.Message}"),
            report_file = report.File,
        });
    }
}
