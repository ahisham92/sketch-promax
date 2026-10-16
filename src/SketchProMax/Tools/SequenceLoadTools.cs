using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

public sealed class ConstructionSequenceTool : IRevitTool
{
    public string Name => "construction_sequence";
    public string Description =>
        "Plan the construction sequence: number every structural element by storey, bottom up, and within a storey by " +
        "trade order (foundations, columns, walls, beams, slabs, stairs, or an order you give), writing the step into a " +
        "'Construction Sequence' parameter (created if missing). Optionally makes one 3D view per step showing what is built " +
        "so far with the current step highlighted, and a CSV of the steps.";
    public string PropertiesJson => """
    {
      "categories": {"type": "array", "items": {"type": "string"}, "description": "In build order within a storey. Default: Structural Foundations, Structural Columns, Walls, Structural Framing, Floors, Stairs."},
      "parameter": {"type": "string", "description": "Integer parameter to write the step to (default 'Construction Sequence', created if missing)."},
      "views": {"type": "boolean", "description": "Make a 3D view per step (default false; at most 40)."},
      "shared_parameter_file": {"type": "string", "description": "Shared parameter file for the new parameter (default the firm's, else Sketch ProMax's own)."},
      "folder": {"type": "string"}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var names = args.StringList("categories");
        if (names.Count == 0) names = ["Structural Foundations", "Structural Columns", "Walls", "Structural Framing", "Floors", "Stairs"];
        var cats = names.Select(n => RevitHelpers.ParseCategory(doc, n)).ToList();
        var paramName = args.OptString("parameter") ?? "Construction Sequence";
        // Storeys only: reference levels (T/O steel, setting-out) would split steps.
        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
            .Where(l => l.get_Parameter(BuiltInParameter.LEVEL_IS_BUILDING_STORY)?.AsInteger() != 0).OrderBy(l => l.ProjectElevation).ToList();
        if (levels.Count == 0) return ToolResult.Error("The model has no storey levels.");

        // Group: storey (the level at or just below the element's bottom), then trade order.
        var groups = new SortedDictionary<(int storey, int trade), List<Element>>();
        for (var c = 0; c < cats.Count; c++)
            foreach (var e in new FilteredElementCollector(doc).OfCategory(cats[c]).WhereElementIsNotElementType())
            {
                if (e.get_BoundingBox(null) is not { } bb) continue;
                if (cats[c] == BuiltInCategory.OST_Walls && e.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT)?.AsInteger() != 1) continue;
                if (cats[c] == BuiltInCategory.OST_Floors && e.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL)?.AsInteger() != 1) continue;
                // Slabs and beams hang below their level: judge them by their top.
                var z = cats[c] is BuiltInCategory.OST_Floors or BuiltInCategory.OST_StructuralFraming ? bb.Max.Z - 0.5 : bb.Min.Z + 0.1;
                var storey = Math.Max(0, levels.FindLastIndex(l => l.ProjectElevation <= z));
                if (cats[c] is BuiltInCategory.OST_Floors or BuiltInCategory.OST_StructuralFraming)
                    storey = Math.Max(0, levels.FindLastIndex(l => l.ProjectElevation <= z + 1)) - 1; // slab on level N closes storey N-1
                storey = Math.Max(0, storey);
                if (!groups.TryGetValue((storey, c), out var list)) groups[(storey, c)] = list = [];
                list.Add(e);
            }
        if (groups.Count == 0) return ToolResult.Error("No structural elements of those categories found.");

        // Make sure the parameter exists and is bound to all these categories (the tool reuses and rebinds an existing one).
        {
            var catNames = cats.Select(b => Category.GetCategory(doc, b)?.Name).Where(n => n != null).ToArray();
            var raw = JsonSerializer.SerializeToElement(new { parameters = new[] { new { name = paramName, type = "integer", categories = catNames, group = "constraints" } },
                shared_parameter_file = args.OptString("shared_parameter_file") });
            var made = new CreateProjectParametersTool().Execute(app, new ToolArgs(raw.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())));
            if (made.IsError) return ToolResult.Error($"Couldn't create the '{paramName}' parameter: {made.Text}");
            if (made.Text.Contains("\"error\"")) return ToolResult.Error($"Couldn't set up the '{paramName}' parameter: {made.Text}");
        }

        var steps = new List<(int step, string storey, string trade, int count)>();
        var notWritten = 0;
        using var t = RevitHelpers.StartTransaction(doc, "Construction sequence");
        var n = 0;
        foreach (var kv in groups)
        {
            n++;
            foreach (var e in kv.Value)
                if (e.LookupParameter(paramName) is { IsReadOnly: false, StorageType: StorageType.Integer } p) p.Set(n);
                else notWritten++;
            steps.Add((n, levels[kv.Key.storey].Name, Category.GetCategory(doc, cats[kv.Key.trade])?.Name ?? names[kv.Key.trade], kv.Value.Count));
        }

        var views = new List<string>();
        string? viewNote = null;
        if (args.OptBool("views"))
        {
            var param = new FilteredElementCollector(doc).OfClass(typeof(SharedParameterElement)).Cast<SharedParameterElement>().FirstOrDefault(p => p.Name == paramName);
            if (param is null) viewNote = "Views skipped: the parameter isn't a shared parameter, so view filters can't use it.";
            else
            {
                var catIds = cats.Select(b => new ElementId(b)).Where(id => ParameterFilterUtilities.GetFilterableParametersInCommon(doc, [id]).Contains(param.Id)).ToList();
                var solid = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>().FirstOrDefault(f => f.GetFillPattern().IsSolidFill);
                var vft = ViewKit.TypeFor(doc, ViewFamily.ThreeDimensional);
                if (catIds.Count == 0) viewNote = "Views skipped: view filters can't use the parameter on these categories.";
                ParameterFilterElement Filter(string name, FilterRule rule)
                {
                    // Reuse a filter of this name only when it covers the same categories.
                    var existing = new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement)).Cast<ParameterFilterElement>().FirstOrDefault(f => f.Name == name);
                    if (existing != null && catIds.All(c => existing.GetCategories().Contains(c))) return existing;
                    var unique = existing is null ? name : $"{name} ({Guid.NewGuid().ToString("N").Substring(0, 4)})";
                    return ParameterFilterElement.Create(doc, unique, catIds, new ElementParameterFilter(rule));
                }
                foreach (var s in catIds.Count == 0 ? [] : steps.Take(40))
                {
                    var view = View3D.CreateIsometric(doc, vft.Id);
                    view.ViewTemplateId = ElementId.InvalidElementId;
                    var name = ViewKit.Finish(doc, view, $"Sequence {s.step:00} - {s.storey} - {s.trade}", null, null);
                    var later = Filter($"Sequence after {s.step:00}", ParameterFilterRuleFactory.CreateGreaterRule(param.Id, s.step));
                    var now = Filter($"Sequence step {s.step:00}", ParameterFilterRuleFactory.CreateEqualsRule(param.Id, s.step));
                    view.AddFilter(later.Id); view.SetFilterVisibility(later.Id, false);
                    view.AddFilter(now.Id);
                    var highlight = new OverrideGraphicSettings().SetProjectionLineColor(new Color(230, 120, 0));
                    if (solid != null) highlight = highlight.SetSurfaceForegroundPatternId(solid.Id).SetSurfaceForegroundPatternColor(new Color(255, 170, 60));
                    view.SetFilterOverrides(now.Id, highlight);
                    view.DisplayStyle = DisplayStyle.ShadingWithEdges;
                    views.Add(name);
                }
                if (steps.Count > 40) viewNote = $"Views made for the first 40 of {steps.Count} steps.";
            }
        }
        if (notWritten == groups.Values.Sum(g => g.Count)) { t.RollBack(); return ToolResult.Error($"No element has a writable whole-number '{paramName}' parameter; pick another parameter name."); }
        if (t.Commit() != TransactionStatus.Committed) return ToolResult.Error("Revit refused the sequence when saving it.");

        var folder = OutputPaths.Checked(args.OptString("folder")) ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SketchProMax");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"{doc.Title} - Construction sequence {DateTime.Now:yyyy-MM-dd}.csv");
        File.WriteAllText(file, "Step,Storey,Trade,Elements\n" + string.Join("\n", steps.Select(s => $"{s.step},\"{s.storey}\",\"{s.trade}\",{s.count}")), new UTF8Encoding(true));
        return ToolResult.Ok(new
        {
            steps = steps.Count, parameter = paramName, file, notWritten, views = views.Count, viewNames = views.Take(10), viewNote,
            sequence = steps.Take(60).Select(s => new { s.step, s.storey, s.trade, elements = s.count }),
        });
    }
}

/// <summary>Which link element a copied element follows, kept on the element itself.</summary>
internal static class MonitorLink
{
    private static readonly Guid SchemaGuid = new("4b1c7d0e-5a2f-4e8b-9c3d-6f1a2b3c4d5e");

    public static Schema Schema() => Autodesk.Revit.DB.ExtensibleStorage.Schema.Lookup(SchemaGuid) ?? Build();

    private static Schema Build()
    {
        var b = new SchemaBuilder(SchemaGuid);
        b.SetSchemaName("SketchProMaxMonitor");
        b.SetVendorId("SPMX");
        b.SetReadAccessLevel(AccessLevel.Public);
        b.SetWriteAccessLevel(AccessLevel.Vendor);
        b.AddSimpleField("Link", typeof(string));
        b.AddSimpleField("Source", typeof(string));
        return b.Finish();
    }

    public static void Set(Element e, RevitLinkInstance link, Element source)
    {
        var entity = new Entity(Schema());
        entity.Set("Link", link.UniqueId);
        entity.Set("Source", source.UniqueId);
        e.SetEntity(entity);
    }

    public static (string link, string source)? Get(Element e)
    {
        var schema = Autodesk.Revit.DB.ExtensibleStorage.Schema.Lookup(SchemaGuid);
        if (schema is null) return null;
        var entity = e.GetEntity(schema);
        return entity.IsValid() ? (entity.Get<string>("Link"), entity.Get<string>("Source")) : null;
    }
}

internal sealed class KeepOwnTypes : IDuplicateTypeNamesHandler
{
    public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args) => DuplicateTypeAction.UseDestinationTypes;
}

public sealed class CopyMonitorTool : IRevitTool
{
    public string Name => "copy_monitor";
    public string Description =>
        "Copy/monitor from a linked model: 'copy' brings levels, grids and/or columns from the link into this model (or " +
        "pairs up ones that already exist by name) and remembers which link element each follows; 'check' lists what moved, " +
        "was renamed or deleted in the link since; 'update' moves and renames the copies to match. Works like Revit's " +
        "Copy/Monitor for levels, grids and columns.";
    public string PropertiesJson => """
    {
      "mode": {"type": "string", "enum": ["copy", "check", "update"]},
      "link": {"type": "string", "description": "Link name (copy). check/update use every monitored link when omitted."},
      "what": {"type": "array", "items": {"type": "string", "enum": ["levels", "grids", "columns"]}, "description": "copy: default levels and grids."},
      "tolerance": {"type": "number", "description": "check/update: ignore moves smaller than this, project units (default 1 mm)."}
    }
    """;
    public string[] Required => ["mode"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().ToList();
        return args.String("mode") switch
        {
            "copy" => Copy(doc, args, LinkMatch.Find(doc, links, args.OptString("link") ?? throw new ArgumentException("Give the link to copy from."))),
            "check" => Check(doc, args, false),
            "update" => Check(doc, args, true),
            var m => throw new ArgumentException($"Unknown mode '{m}'."),
        };
    }

    private static ToolResult Copy(Document doc, ToolArgs args, RevitLinkInstance link)
    {
        var linkDoc = link.GetLinkDocument() ?? throw new ArgumentException($"Link '{link.Name}' is not loaded.");
        var tr = link.GetTotalTransform();
        var what = args.StringList("what");
        if (what.Count == 0) what = ["levels", "grids"];
        var copied = new List<string>(); var paired = new List<string>(); var skipped = new List<string>();
        using var t = RevitHelpers.StartTransaction(doc, "Copy/monitor from link");
        void Run<T>(IEnumerable<T> source, Func<T, Element?> existing, string label) where T : Element
        {
            foreach (var s in source)
            {
                if (existing(s) is { } mine)
                {
                    if (mine == (Element)s) { skipped.Add($"{label} {s.Name}: slanted, copy it by hand"); continue; }
                    if (MonitorLink.Get(mine) is null) { MonitorLink.Set(mine, link, s); paired.Add($"{label} {s.Name}"); }
                    else skipped.Add($"{label} {s.Name} (already monitored)");
                    continue;
                }
                try
                {
                    var options = new CopyPasteOptions();
                    options.SetDuplicateTypeNamesHandler(new KeepOwnTypes());
                    var ids = ElementTransformUtils.CopyElements(linkDoc, [s.Id], doc, tr, options);
                    foreach (var id in ids) if (doc.GetElement(id) is { } e) MonitorLink.Set(e, link, s);
                    copied.Add($"{label} {s.Name}");
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex) { skipped.Add($"{label} {s.Name}: {ex.Message.Trim()}"); }
            }
        }
        var myLevels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
        var myGrids = new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>().ToList();
        if (what.Contains("levels"))
            Run(new FilteredElementCollector(linkDoc).OfClass(typeof(Level)).Cast<Level>(),
                l => myLevels.FirstOrDefault(m => m.Name == l.Name) ?? myLevels.FirstOrDefault(m => Math.Abs(m.ProjectElevation - (l.ProjectElevation + tr.Origin.Z)) < 1 / 304.8), "Level");
        if (what.Contains("grids"))
            Run(new FilteredElementCollector(linkDoc).OfClass(typeof(Grid)).Cast<Grid>(), g => myGrids.FirstOrDefault(m => m.Name == g.Name), "Grid");
        if (what.Contains("columns"))
        {
            var mine = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StructuralColumns).WhereElementIsNotElementType().Cast<Element>()
                .Select(e => (e, p: (e.Location as LocationPoint)?.Point)).Where(x => x.p != null).ToList();
            Run(new FilteredElementCollector(linkDoc).OfCategory(BuiltInCategory.OST_StructuralColumns).WhereElementIsNotElementType().OfType<FamilyInstance>(),
                c => c.Location is LocationPoint lp ? mine.FirstOrDefault(x => x.p!.DistanceTo(tr.OfPoint(lp.Point)) < 10 / 304.8).e : c, "Column");
        }
        if (t.Commit() != TransactionStatus.Committed) return ToolResult.Error("Revit refused the copy when saving it.");
        return ToolResult.Ok(new { link = link.Name, copied = copied.Count, pairedWithExisting = paired.Count, copiedList = copied.Take(60), paired = paired.Take(60), skipped });
    }

    private static ToolResult Check(Document doc, ToolArgs args, bool update)
    {
        var tol = args.OptDouble("tolerance") is { } tv ? RevitHelpers.ToInternal(doc, tv) : 1 / 304.8;
        var links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().ToDictionary(l => l.UniqueId);
        var monitored = new FilteredElementCollector(doc).WherePasses(new LogicalOrFilter(
                new ElementClassFilter(typeof(Level)), new LogicalOrFilter(new ElementClassFilter(typeof(Grid)), new ElementCategoryFilter(BuiltInCategory.OST_StructuralColumns))))
            .WhereElementIsNotElementType().Select(e => (e, m: MonitorLink.Get(e))).Where(x => x.m != null).ToList();
        if (monitored.Count == 0) return ToolResult.Error("Nothing is monitored yet; run copy_monitor with mode 'copy' first.");
        if (args.OptString("link") is { } wanted)
        {
            var link = LinkMatch.Find(doc, links.Values.ToList(), wanted);
            monitored = monitored.Where(x => x.m!.Value.link == link.UniqueId).ToList();
        }
        string L(double ft) => $"{Math.Round(RevitHelpers.FromInternal(doc, ft), 1)} {RevitHelpers.LengthUnitLabel(doc)}";
        var changes = new List<object>(); var fixedCount = 0; var cannotFix = new List<string>();
        using var t = update ? RevitHelpers.StartTransaction(doc, "Update monitored elements") : null;
        foreach (var (e, m) in monitored)
        {
            if (!links.TryGetValue(m!.Value.link, out var link) || link.GetLinkDocument() is not { } linkDoc)
            { changes.Add(new { element = e.Name, id = e.Id.Value, change = "its link is missing or unloaded" }); continue; }
            var tr = link.GetTotalTransform();
            var source = linkDoc.GetElement(m.Value.source);
            if (source is null) { changes.Add(new { element = e.Name, id = e.Id.Value, change = "deleted in the link" }); continue; }
            var notes = new List<string>();
            XYZ? move = null;
            if (source.Name != e.Name && e is Level or Grid)
            {
                notes.Add($"renamed '{e.Name}' to '{source.Name}'");
                if (update) { try { e.Name = source.Name; fixedCount++; } catch (Autodesk.Revit.Exceptions.ArgumentException) { cannotFix.Add($"{e.Name}: the name '{source.Name}' is taken"); } }
            }
            switch (e, source)
            {
                case (Level mine, Level theirs):
                    var dz = theirs.ProjectElevation + tr.Origin.Z - mine.ProjectElevation;
                    if (Math.Abs(dz) > tol) { notes.Add($"moved {L(dz)} vertically"); move = new XYZ(0, 0, dz); }
                    break;
                case (Grid mine, Grid theirs) when mine.Curve is Line ml && theirs.Curve.CreateTransformed(tr) is Line tl:
                    var flat = Line.CreateUnbound(new XYZ(ml.Origin.X, ml.Origin.Y, 0), new XYZ(ml.Direction.X, ml.Direction.Y, 0));
                    var target = new XYZ(tl.Origin.X, tl.Origin.Y, 0);
                    if (Math.Abs(Math.Abs(ml.Direction.DotProduct(tl.Direction)) - 1) > 1e-4) { notes.Add("rotated in the link"); cannotFix.Add($"Grid {e.Name}: rotated, move it by hand"); }
                    else if (flat.Project(target) is { Distance: var dist } r && dist > tol) { notes.Add($"moved {L(dist)}"); move = target - r.XYZPoint; }
                    break;
                case (FamilyInstance { Location: LocationPoint mp }, FamilyInstance { Location: LocationPoint tp } fi):
                    var delta = tr.OfPoint(tp.Point) - mp.Point;
                    if (new XYZ(delta.X, delta.Y, 0).GetLength() > tol) { notes.Add($"moved {L(new XYZ(delta.X, delta.Y, 0).GetLength())} in plan"); move = new XYZ(delta.X, delta.Y, 0); }
                    if (e is FamilyInstance mfi && mfi.Symbol.Name != fi.Symbol.Name) notes.Add($"type changed to {fi.Symbol.FamilyName}: {fi.Symbol.Name} (not updated)");
                    break;
            }
            if (update && move != null)
            {
                try { ElementTransformUtils.MoveElement(doc, e.Id, move); fixedCount++; }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex) { cannotFix.Add($"{e.Name}: {ex.Message.Trim()}"); }
            }
            if (notes.Count > 0) changes.Add(new { element = $"{e.Category?.Name} {e.Name}", id = e.Id.Value, change = string.Join("; ", notes) });
        }
        if (t != null && t.Commit() != TransactionStatus.Committed) return ToolResult.Error("Revit refused the updates when saving them.");
        return ToolResult.Ok(new { monitored = monitored.Count, changed = changes.Count, changes = changes.Take(150), updated = update ? fixedCount : (int?)null, cannotFix });
    }
}

public sealed class LoadingPlanTool : IRevitTool
{
    public string Name => "loading_plan";
    public string Description =>
        "Draw a loading plan from the architectural link: each room in the link on this view's level is classed by its name " +
        "(office, residential, corridor, stairs, storage, plant, parking, assembly, retail, balcony, roof...) and given live " +
        "and superimposed dead loads from EN 1991, ASCE 7 or your own table; the plan gets a coloured filled region and a " +
        "load label per room, plus a CSV. Rooms the link leaves unbounded are covered by zones made from the slab outlines on that " +
        "level (slab minus the rooms that could be drawn), each taking the heaviest class of the rooms inside it. The legend is a " +
        "table in its own drafting view (colour, class, loads, area) with a list of what could not be loaded; place it with " +
        "place_views_on_sheet. Loads are typical code values to confirm, not a design.";
    public string PropertiesJson => """
    {
      "link": {"type": "string", "description": "Architectural link name."},
      "view_id": {"type": "integer", "description": "Plan view in this model to draw on (its level picks the rooms)."},
      "standard": {"type": "string", "enum": ["EN1991", "ASCE7"], "description": "Default EN1991."},
      "sdl": {"type": "number", "description": "Superimposed dead load kPa for every room (finishes, services, partitions), default 1.5."},
      "overrides": {"type": "array", "items": {"type": "object", "properties": {"contains": {"type": "string"}, "label": {"type": "string"}, "live": {"type": "number"}, "sdl": {"type": "number"}}, "required": ["contains", "live"]}, "description": "Your own classes, checked first: room name contains this text -> loads in kPa."},
      "label_rooms": {"type": "boolean", "description": "Write the loads in each room and zone (default true)."},
      "fallback_zones": {"type": "boolean", "description": "Cover unbounded rooms with zones from the slab outlines (default true)."},
      "zone_default": {"type": "object", "properties": {"label": {"type": "string"}, "live": {"type": "number"}, "sdl": {"type": "number"}}, "required": ["live"], "description": "Loads for a slab zone with no room inside it (default: Unclassified, the office value)."},
      "folder": {"type": "string"}
    }
    """;
    public string[] Required => ["link", "view_id"];
    public bool ModifiesModel => true;

    // Keyword -> class, then class -> live load kPa per standard. Typical values; projects must confirm them.
    private static readonly (string[] words, string cls)[] Classes =
    [
        (["corridor", "lobby", "circulation", "entrance", "foyer", "passage"], "Corridor"),
        (["stair"], "Stairs"),
        (["retail", "shop", "showroom", "market"], "Retail"),
        (["balcony", "terrace", "veranda"], "Balcony"),
        (["store", "storage", "archive", "file", "warehouse", "stock"], "Storage"),
        (["plant", "mech", "electrical", "elec", "pump", "generator", "transformer", "switch", "ahu", "chiller", "tank", "mep"], "Plant"),
        (["parking", "car", "garage", "vehicle"], "Parking"),
        (["assembly", "auditorium", "hall", "restaurant", "cafe", "café", "gym", "class", "lecture", "prayer", "mosque", "majlis", "theatre", "cinema"], "Assembly"),
        (["office", "meeting", "conference", "work", "study", "admin", "reception"], "Office"),
        (["bed", "living", "kitchen", "bath", "toilet", "wc", "dining", "apartment", "flat", "dwelling", "maid", "laundry", "dressing", "family", "guest"], "Residential"),
        (["roof"], "Roof"),
    ];

    private static readonly Dictionary<string, (double en, double asce)> Live = new()
    {
        ["Residential"] = (2.0, 1.92), ["Office"] = (3.0, 2.40), ["Corridor"] = (4.0, 4.79), ["Stairs"] = (4.0, 4.79),
        ["Balcony"] = (4.0, 2.87), ["Storage"] = (7.5, 6.00), ["Plant"] = (7.5, 7.18), ["Parking"] = (2.5, 1.92),
        ["Assembly"] = (5.0, 4.79), ["Retail"] = (5.0, 4.79), ["Roof"] = (0.75, 0.96), ["Unclassified"] = (3.0, 2.40),
    };

    private static readonly Color[] Palette =
    [
        new(255, 214, 153), new(153, 204, 255), new(255, 153, 153), new(204, 153, 255), new(153, 230, 179), new(255, 255, 153),
        new(255, 179, 217), new(179, 179, 179), new(153, 255, 255), new(230, 204, 153), new(204, 255, 153), new(240, 240, 240),
    ];

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var view = doc.GetElement(new ElementId(args.OptLong("view_id") ?? -1)) as ViewPlan ?? throw new ArgumentException("view_id must be a plan view.");
        var level = view.GenLevel ?? throw new ArgumentException("That view has no level.");
        var z = level.ProjectElevation;
        var links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().ToList();
        var link = LinkMatch.Find(doc, links, args.String("link"));
        var linkDoc = link.GetLinkDocument() ?? throw new ArgumentException($"Link '{link.Name}' is not loaded.");
        var tr = link.GetTotalTransform();
        var asce = args.OptString("standard") == "ASCE7";
        var sdl = args.OptDouble("sdl") ?? 1.5;
        var overrides = args.Array("overrides").Select(o => (contains: o.GetProperty("contains").GetString() ?? "", label: o.TryGetProperty("label", out var l) ? l.GetString() : null,
            live: o.GetProperty("live").GetDouble(), sdl: o.TryGetProperty("sdl", out var s) ? s.GetDouble() : (double?)null)).ToList();
        var fallback = args.OptBool("fallback_zones", true);
        var zoneDefault = (cls: "Unclassified", live: asce ? Live["Unclassified"].asce : Live["Unclassified"].en, sdl);
        if (args.Has("zone_default"))
        {
            var zd = args.Raw["zone_default"];
            zoneDefault = (zd.TryGetProperty("label", out var zl) ? zl.GetString() ?? "Default zone" : "Default zone", zd.GetProperty("live").GetDouble(),
                zd.TryGetProperty("sdl", out var zs) ? zs.GetDouble() : sdl);
        }
        (string cls, double live, double sdl) Classify(string name)
        {
            var o = overrides.FirstOrDefault(x => name.IndexOf(x.contains, StringComparison.OrdinalIgnoreCase) >= 0);
            if (o.contains is { Length: > 0 }) return (o.label ?? o.contains, o.live, o.sdl ?? sdl);
            var cls = Classes.FirstOrDefault(c => c.words.Any(w => name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0)).cls ?? "Unclassified";
            return (cls, asce ? Live[cls].asce : Live[cls].en, sdl);
        }

        // Placed rooms on this level, bounded or not: an unbounded room still says what its area is used for.
        var rooms = new FilteredElementCollector(linkDoc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().OfType<Room>()
            .Where(r => r.Location is LocationPoint && r.Level is { } rl && Math.Abs(rl.ProjectElevation + tr.Origin.Z - z) < 300 / 304.8).ToList();
        if (rooms.Count == 0 && !fallback) return ToolResult.Error($"No placed rooms in '{link.Name}' at the level of '{view.Name}'.");

        var rows = new List<RoomRow>();
        foreach (var r in rooms)
        {
            var name = r.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "";
            var (cls, live, roomSdl) = Classify(name);
            var row = new RoomRow(r, name, cls, live, roomSdl, tr.OfPoint(((LocationPoint)r.Location).Point));
            if (r.Area <= 0) row.Why = "not enclosed (no boundary in the link)";
            else
                foreach (var segs in r.GetBoundarySegments(new SpatialElementBoundaryOptions()) ?? [])
                {
                    try { row.Loops.Add(CurveLoop.Create(segs.Select(sg => Flatten(sg.GetCurve().CreateTransformed(tr), z)).ToList())); }
                    catch (Autodesk.Revit.Exceptions.ArgumentException)
                    {
                        if (row.Loops.Count == 0) { row.Why = "its boundary is not a closed loop"; break; } // without its outer loop the room can't be drawn
                    }
                }
            if (row.Loops.Count == 0) row.Why ??= "no usable boundary";
            rows.Add(row);
        }

        using var t = RevitHelpers.StartTransaction(doc, "Loading plan");
        var solid = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>().FirstOrDefault(f => f.GetFillPattern().IsSolidFill && f.GetFillPattern().Target == FillPatternTarget.Drafting);
        var baseType = new FilteredElementCollector(doc).OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>().FirstOrDefault()
                       ?? throw new InvalidOperationException("The model has no filled region types.");
        var types = new Dictionary<(string, double, double), FilledRegionType>();
        FilledRegionType TypeFor((string cls, double live, double sdl) c)
        {
            if (types.TryGetValue(c, out var known)) return known;
            var typeName = $"Load - {c.cls} LL {c.live.ToString("0.0#", CultureInfo.InvariantCulture)} SDL {c.sdl.ToString("0.0#", CultureInfo.InvariantCulture)}";
            var type = new FilteredElementCollector(doc).OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>().FirstOrDefault(x => x.Name == typeName)
                       ?? (FilledRegionType)baseType.Duplicate(typeName);
            if (solid != null) type.ForegroundPatternId = solid.Id;
            type.ForegroundPatternColor = Palette[types.Count % Palette.Length];
            type.BackgroundPatternId = ElementId.InvalidElementId;
            return types[c] = type;
        }
        var textType = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
        var labels = args.OptBool("label_rooms", true);
        var made = 0;
        foreach (var r in rows.Where(r => r.Loops.Count > 0).OrderBy(r => r.Live))
        {
            try
            {
                FilledRegion.Create(doc, TypeFor((r.Cls, r.Live, r.Sdl)).Id, view.Id, r.Loops);
                r.Drawn = true;
                made++;
                if (labels) TextNote.Create(doc, view.Id, new XYZ(r.At.X, r.At.Y, z), $"{r.Cls.ToUpperInvariant()}\nLL {r.Live:0.0#} kPa\nSDL {r.Sdl:0.0#} kPa", textType);
            }
            catch (Exception ex) when (ex is ArgumentException or Autodesk.Revit.Exceptions.ApplicationException) { r.Why = ex.Message.Trim(); }
        }

        // Zones: the slabs on this level minus the rooms drawn above, for everything the rooms could not cover.
        var zones = new List<ZoneRow>();
        string? zoneNote = null;
        if (fallback && rows.Any(r => !r.Drawn) || fallback && rows.Count == 0)
        {
            var pieces = SlabZones.Pieces(doc, linkDoc, tr, z, rows.Where(r => r.Drawn).Select(r => r.Loops).ToList(), out zoneNote);
            foreach (var piece in pieces)
            {
                var inside = rows.Where(r => !r.Drawn && piece.Contains(r.At.X, r.At.Y)).ToList();
                var gov = inside.OrderByDescending(r => r.Live).ThenByDescending(r => r.Sdl).FirstOrDefault();
                (string cls, double live, double sdl) c = gov is null ? zoneDefault : (gov.Cls, gov.Live, gov.Sdl);
                var zone = new ZoneRow(c.cls, c.live, c.sdl, piece.AreaM2, inside.Select(r => $"{r.Name} {r.Room.Number}".Trim()).ToList());
                try
                {
                    FilledRegion.Create(doc, TypeFor(c).Id, view.Id, piece.Loops);
                    zone.Drawn = true;
                    foreach (var r in inside) r.Zone = zones.Count + 1;
                    if (labels && piece.Label is { } at)
                        TextNote.Create(doc, view.Id, new XYZ(at.x, at.y, z), $"{c.cls.ToUpperInvariant()} (ZONE {zones.Count + 1})\nLL {c.live:0.0#} kPa\nSDL {c.sdl:0.0#} kPa", textType);
                }
                catch (Exception ex) when (ex is ArgumentException or Autodesk.Revit.Exceptions.ApplicationException) { zone.Error = ex.Message.Trim(); }
                zones.Add(zone);
            }
        }
        var notLoaded = rows.Where(r => !r.Drawn && r.Zone is null).ToList();

        // Legend: a table in its own drafting view, with a colour swatch per class, then what was not loaded.
        var classes = rows.Where(r => r.Drawn).Select(r => (r.Cls, r.Live, r.Sdl)).Concat(zones.Where(zn => zn.Drawn).Select(zn => (zn.Cls, zn.Live, zn.Sdl)))
            .Distinct().OrderBy(c => c.Item2).ThenBy(c => c.Item1).ToList();
        ViewDrafting? legend = null;
        if (classes.Count > 0)
        {
            legend = Drafting.Create(doc, $"Loading legend - {level.Name}", 1);
            string N(double v) => v.ToString("0.0#", CultureInfo.InvariantCulture);
            var table = classes.Select(c =>
            {
                var roomArea = rows.Where(r => r.Drawn && (r.Cls, r.Live, r.Sdl) == c).Sum(r => UnitUtils.ConvertFromInternalUnits(r.Room.Area, UnitTypeId.SquareMeters));
                var zoneArea = zones.Where(zn => zn.Drawn && (zn.Cls, zn.Live, zn.Sdl) == c).Sum(zn => zn.AreaM2);
                var source = roomArea > 0 && zoneArea > 0 ? "rooms + slab zones" : zoneArea > 0 ? "slab zones" : "rooms";
                return (IList<string>)["", c.Item1, N(c.Item2), N(c.Item3), (roomArea + zoneArea).ToString("0.#", CultureInfo.InvariantCulture), source];
            }).ToList();
            var title = $"LOADING PLAN - {level.Name} ({(asce ? "ASCE 7" : "EN 1991")}, kPa). Typical code values; confirm with the project's design criteria.";
            var height = Drafting.Table(doc, legend, XYZ.Zero, ["Colour", "Use", "LL (kPa)", "SDL (kPa)", "Area (m²)", "From"], table, [16, 50, 22, 22, 24, 36], title);
            // Swatches: the table puts the title 10 mm down, then 8 mm rows with the header first.
            for (var i = 0; i < classes.Count; i++)
            {
                var top = -Drafting.Mm(10) - Drafting.Mm(8) * (i + 1);
                var loop = CurveLoop.Create(new List<Curve>
                {
                    Line.CreateBound(new XYZ(Drafting.Mm(2), top - Drafting.Mm(1.5), 0), new XYZ(Drafting.Mm(14), top - Drafting.Mm(1.5), 0)),
                    Line.CreateBound(new XYZ(Drafting.Mm(14), top - Drafting.Mm(1.5), 0), new XYZ(Drafting.Mm(14), top - Drafting.Mm(6.5), 0)),
                    Line.CreateBound(new XYZ(Drafting.Mm(14), top - Drafting.Mm(6.5), 0), new XYZ(Drafting.Mm(2), top - Drafting.Mm(6.5), 0)),
                    Line.CreateBound(new XYZ(Drafting.Mm(2), top - Drafting.Mm(6.5), 0), new XYZ(Drafting.Mm(2), top - Drafting.Mm(1.5), 0)),
                });
                try { FilledRegion.Create(doc, TypeFor(classes[i]).Id, legend.Id, [loop]); }
                catch (Autodesk.Revit.Exceptions.ApplicationException) { }
            }
            if (notLoaded.Count > 0)
                Drafting.Table(doc, legend, new XYZ(0, -height - Drafting.Mm(12), 0), ["Room", "Name", "Not loaded because"],
                    notLoaded.Take(60).Select(r => (IList<string>)[r.Room.Number ?? "", Cut(r.Name, 30), Cut(r.Why ?? "", 60)]).ToList(), [20, 60, 110],
                    $"NOT LOADED ({notLoaded.Count}{(notLoaded.Count > 60 ? ", first 60 listed" : "")})");
        }
        if (t.Commit() != TransactionStatus.Committed) return ToolResult.Error("Revit refused the loading plan when saving it.");

        var folder = OutputPaths.Checked(args.OptString("folder")) ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SketchProMax");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"{doc.Title} - Loading plan {level.Name} {DateTime.Now:yyyy-MM-dd}.csv");
        string Csv(string v) => v.IndexOfAny([',', '"', '\n']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        string F(double v) => v.ToString(CultureInfo.InvariantCulture);
        var csv = new StringBuilder("Room number,Room name,Class,Area (m²),Live (kPa),SDL (kPa),Loaded as\n");
        foreach (var r in rows)
            csv.Append($"{Csv(r.Room.Number ?? "")},{Csv(r.Name)},{Csv(r.Cls)},{UnitUtils.ConvertFromInternalUnits(r.Room.Area, UnitTypeId.SquareMeters).ToString("0.##", CultureInfo.InvariantCulture)},{F(r.Live)},{F(r.Sdl)}," +
                       $"{Csv(r.Drawn ? "room" : r.Zone is { } zi ? $"zone {zi}" : "not loaded: " + r.Why)}\n");
        for (var i = 0; i < zones.Count; i++)
            csv.Append($",{Csv($"Zone {i + 1}")},{Csv(zones[i].Cls)},{zones[i].AreaM2.ToString("0.##", CultureInfo.InvariantCulture)},{F(zones[i].Live)},{F(zones[i].Sdl)},{Csv(zones[i].Drawn ? "slab zone" : "not drawn: " + zones[i].Error)}\n");
        File.WriteAllText(file, csv.ToString(), new UTF8Encoding(true));

        if (made == 0 && zones.All(zn => !zn.Drawn))
            return ToolResult.Error($"Nothing could be loaded on '{view.Name}': {rows.Count} room(s), none with a usable boundary, and no slab zones. {zoneNote}".Trim());
        return ToolResult.Ok(new
        {
            rooms = rows.Count, roomRegions = made,
            zones = zones.Select((zn, i) => new { zone = i + 1, zn.Cls, liveKpa = zn.Live, sdlKpa = zn.Sdl, areaM2 = Math.Round(zn.AreaM2, 1), rooms = zn.Rooms.Take(20), error = zn.Error,
                note = zn.Rooms.Count == 0 ? "no room inside: default loads" : null }),
            zoneNote,
            notLoaded = notLoaded.Select(r => new { number = r.Room.Number, name = r.Name, why = r.Why }).Take(100),
            legendViewId = legend?.Id.Value, legendView = legend?.Name,
            file,
            classes = classes.Select(c => new { cls = c.Item1, liveKpa = c.Item2, sdlKpa = c.Item3 }),
            unclassified = rows.Where(r => r.Cls == "Unclassified").Select(r => r.Name).Distinct().Take(30),
            note = "Unclassified rooms got the office value; add overrides for them. Values are typical code loads to confirm, not a design. " +
                   (legend is null ? "" : "Put the legend on the sheet with place_views_on_sheet (legendViewId)."),
        });
    }

    private static string Cut(string s, int n) => s.Length <= n ? s : s.Substring(0, n - 1) + "…";

    private sealed class RoomRow(Room room, string name, string cls, double live, double sdl, XYZ at)
    {
        public Room Room { get; } = room;
        public string Name { get; } = name;
        public string Cls { get; } = cls;
        public double Live { get; } = live;
        public double Sdl { get; } = sdl;
        public XYZ At { get; } = at;
        public List<CurveLoop> Loops { get; } = [];
        public bool Drawn;
        public int? Zone;
        public string? Why;
    }

    private sealed class ZoneRow(string cls, double live, double sdl, double areaM2, List<string> rooms)
    {
        public string Cls { get; } = cls;
        public double Live { get; } = live;
        public double Sdl { get; } = sdl;
        public double AreaM2 { get; } = areaM2;
        public List<string> Rooms { get; } = rooms;
        public bool Drawn;
        public string? Error;
    }

    private static Curve Flatten(Curve c, double z)
    {
        var tr = Transform.CreateTranslation(new XYZ(0, 0, z - c.GetEndPoint(0).Z));
        return c.CreateTransformed(tr);
    }
}

/// <summary>
/// Load zones for areas the architectural rooms don't cover: the outlines of the slabs on a level (this model's, else
/// the link's), minus the rooms already drawn (grown a little so the wall strips between them go too), split into
/// separate pieces. Thin leftovers (strips under walls and along slab edges) are dropped.
/// </summary>
internal static class SlabZones
{
    public sealed class Piece
    {
        public List<CurveLoop> Loops = [];
        public List<(double x, double y)> Outer = [];
        public List<IReadOnlyList<(double x, double y)>> Holes = [];
        public double AreaM2;
        public (double x, double y)? Label;
        public bool Contains(double x, double y) => LayoutMath.InPolygon(Outer, x, y) && !Holes.Any(h => LayoutMath.InPolygon(h, x, y));
    }

    private const double Height = 1.0;          // extrusion height for the 2D booleans, feet
    private static readonly double Grow = 350 / 304.8;   // drawn rooms are grown by this before subtracting
    private static readonly double MinArea = 2 / 0.09290304;   // 2 m², in square feet
    private static readonly double MinWidth = 0.9 / 0.3048;    // pieces narrower than this (area / half perimeter) are slivers

    public static List<Piece> Pieces(Document doc, Document linkDoc, Transform tr, double z, List<List<CurveLoop>> drawnRooms, out string? note)
    {
        note = null;
        var tol = 600 / 304.8;
        var slabs = Slabs(doc, Transform.Identity, z, tol);
        var from = "this model's";
        if (slabs.Count == 0) { slabs = Slabs(linkDoc, tr, z, tol); from = "the link's"; }
        if (slabs.Count == 0) { note = "No slabs at this level in this model or the link, so unbounded rooms could not be zoned."; return []; }

        Solid? union = null;
        var separate = new List<Solid>();
        foreach (var s in slabs)
        {
            if (union is null) { union = s; continue; }
            try { union = BooleanOperationsUtils.ExecuteBooleanOperation(union, s, BooleanOperationsType.Union); }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { separate.Add(s); }
        }
        var solids = new List<Solid>();
        if (union is not null) solids.Add(union);
        solids.AddRange(separate);

        var failedRooms = 0;
        foreach (var loops in drawnRooms)
        {
            var cutter = RoomCutter(loops, z);
            if (cutter is null) { failedRooms++; continue; }
            for (var i = 0; i < solids.Count; i++)
            {
                try { solids[i] = BooleanOperationsUtils.ExecuteBooleanOperation(solids[i], cutter, BooleanOperationsType.Difference); }
                catch (Autodesk.Revit.Exceptions.ApplicationException) { failedRooms++; }
            }
        }

        var pieces = new List<Piece>();
        var slivers = 0;
        foreach (var solid in solids)
            foreach (Face f in solid.Faces)
            {
                if (f is not PlanarFace pf || pf.FaceNormal.Z > -0.99) continue;
                var loops = pf.GetEdgesAsCurveLoops().Select(l => CurveLoop.CreateViaTransform(l, Transform.CreateTranslation(new XYZ(0, 0, z - pf.Origin.Z)))).ToList();
                var rings = loops.Select(Ring).Where(r => r.Count >= 3).OrderByDescending(r => Math.Abs(Signed(r))).ToList();
                if (rings.Count == 0) continue;
                var perimeter = loops.Sum(l => l.GetExactLength());
                if (pf.Area < MinArea || pf.Area / (perimeter / 2) < MinWidth / 2) { slivers++; continue; }
                var piece = new Piece { Loops = loops, Outer = rings[0], Holes = rings.Skip(1).Cast<IReadOnlyList<(double x, double y)>>().ToList(), AreaM2 = pf.Area * 0.09290304 };
                var inside = LayoutMath.InteriorPoints(piece.Outer, piece.Holes);
                if (inside.Count > 0) piece.Label = (inside[0].x, inside[0].y);
                pieces.Add(piece);
            }
        note = $"Zones made from {from} {slabs.Count} slab outline(s) on this level, minus the rooms drawn" +
               (slivers > 0 ? $"; {slivers} thin strip(s) under walls or along edges were left out" : "") +
               (failedRooms > 0 ? $"; {failedRooms} room cut(s) failed, so a zone may overlap a room" : "") + ".";
        return pieces;
    }

    /// <summary>Each floor near the level as a flat extrusion of its top outline at the level.</summary>
    private static List<Solid> Slabs(Document source, Transform tr, double z, double tol)
    {
        var result = new List<Solid>();
        foreach (var floor in new FilteredElementCollector(source).OfCategory(BuiltInCategory.OST_Floors).WhereElementIsNotElementType().OfType<Floor>())
        {
            if (floor.get_BoundingBox(null) is not { } bb || Math.Abs(bb.Max.Z + tr.Origin.Z - z) > tol) continue;
            try
            {
                PlanarFace? top = null;
                foreach (var r in HostObjectUtils.GetTopFaces(floor))
                    if (floor.GetGeometryObjectFromReference(r) is PlanarFace f && (top is null || f.Area > top.Area)) top = f;
                if (top is null) continue;
                var loops = top.GetEdgesAsCurveLoops().Select(l => CurveLoop.CreateViaTransform(CurveLoop.CreateViaTransform(l, tr),
                    Transform.CreateTranslation(new XYZ(0, 0, z - tr.OfPoint(top.Origin).Z)))).ToList();
                result.Add(GeometryCreationUtilities.CreateExtrusionGeometry(loops, XYZ.BasisZ, Height));
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { }
        }
        return result;
    }

    /// <summary>The room's outer loop grown outward, extruded a little taller than the slabs so the cut is clean.</summary>
    private static Solid? RoomCutter(List<CurveLoop> loops, double z)
    {
        var outer = loops.OrderByDescending(l => Math.Abs(Signed(Ring(l)))).FirstOrDefault();
        if (outer is null) return null;
        var area = Math.Abs(Signed(Ring(outer)));
        var shape = outer;
        foreach (var d in new[] { Grow, -Grow })
        {
            try
            {
                var grown = CurveLoop.CreateViaOffset(outer, d, XYZ.BasisZ);
                if (Math.Abs(Signed(Ring(grown))) > area) { shape = grown; break; }
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { }
        }
        try
        {
            var low = CurveLoop.CreateViaTransform(shape, Transform.CreateTranslation(new XYZ(0, 0, -0.1)));
            if (!low.IsCounterclockwise(XYZ.BasisZ)) low.Flip();
            return GeometryCreationUtilities.CreateExtrusionGeometry([low], XYZ.BasisZ, Height + 0.2);
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { return null; }
    }

    private static List<(double x, double y)> Ring(CurveLoop loop)
    {
        var pts = new List<(double x, double y)>();
        foreach (var c in loop)
        {
            var t = c.Tessellate();
            for (var i = 0; i < t.Count - 1; i++) pts.Add((t[i].X, t[i].Y));
        }
        return pts;
    }

    private static double Signed(List<(double x, double y)> ring)
    {
        double a = 0;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++) a += (ring[j].x + ring[i].x) * (ring[j].y - ring[i].y);
        return a / 2;
    }
}
