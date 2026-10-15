using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

internal static class TypeKit
{
    public static T Pick<T>(Document doc, string? name, string what) where T : ElementType
    {
        var all = new FilteredElementCollector(doc).OfClass(typeof(T)).Cast<T>().ToList();
        if (all.Count == 0) throw new InvalidOperationException($"This model has no {what} types. Load one first.");
        if (string.IsNullOrWhiteSpace(name)) return all[0];
        return all.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
               ?? all.FirstOrDefault(t => t.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
               ?? throw new ArgumentException($"No {what} type '{name}'. Available: {string.Join(", ", all.Select(t => t.Name).Take(40))}");
    }

    public static List<XYZ> Points(Document doc, JsonElement array, double z) =>
        array.EnumerateArray().Select(p => ViewKit.Point(doc, p, z)).ToList();

    /// <summary>
    /// Failure handling for batch commits. Warnings are dismissed. An error caused only by elements this transaction
    /// created deletes those elements, and an error Revit can fix without deleting anything is resolved, so one bad
    /// item doesn't undo a whole batch. Anything else falls through to FailureGuard, which rolls the transaction back.
    /// (This preprocessor runs before the global FailuresProcessing event; when it handles something it asks Revit to
    /// commit again, and the new pass is checked from the start.) Construct it with the document right after the
    /// transaction starts so it can tell new elements (ids above every existing id) from the user's; without the
    /// document it never deletes anything.
    /// </summary>
    public sealed class SwallowWarnings : IFailuresPreprocessor
    {
        private readonly long _lastExistingId = long.MaxValue;
        private int _passes;

        /// <summary>Ids of new elements deleted to settle errors.</summary>
        public List<long> Deleted { get; } = [];

        public SwallowWarnings() { }

        public SwallowWarnings(Document doc) =>
            _lastExistingId = new FilteredElementCollector(doc).WhereElementIsNotElementType().ToElementIds()
                .Concat(new FilteredElementCollector(doc).WhereElementIsElementType().ToElementIds())
                .Select(id => id.Value).DefaultIfEmpty(0).Max();

        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            var handled = false;
            var deleting = new HashSet<long>();
            foreach (var f in accessor.GetFailureMessages())
            {
                if (f.GetSeverity() == FailureSeverity.Warning) { accessor.DeleteWarning(f); continue; }
                if (f.GetSeverity() != FailureSeverity.Error || _passes >= 5) continue; // still failing after 5 tries: let FailureGuard roll back
                var failing = f.GetFailingElementIds().Select(id => id.Value).ToList();
                try
                {
                    if (failing.Count > 0 && failing.All(id => id > _lastExistingId))
                    {
                        var fresh = failing.Where(id => !deleting.Contains(id)).ToList();
                        if (fresh.Count > 0) accessor.DeleteElements(fresh.Select(id => new ElementId(id)).ToList());
                        deleting.UnionWith(fresh);
                        handled = true;
                    }
                    else if (f.HasResolutions() && f.GetCurrentResolutionType() != FailureResolutionType.DeleteElements)
                    {
                        accessor.ResolveFailure(f);
                        handled = true;
                    }
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException) { }
            }
            if (!handled) return FailureProcessingResult.Continue;
            Deleted.AddRange(deleting);
            _passes++;
            return FailureProcessingResult.ProceedWithCommit;
        }
    }
}

public sealed class CreateStairsTool : IRevitTool
{
    public string Name => "create_stairs";
    public string Description =>
        "Create a stair between two levels, starting at a plan point and climbing in a direction: straight, L-shaped " +
        "(turn left or right at a landing) or U-shaped (switchback), with railings. Riser count and tread depth follow " +
        "the stair type's rules.";
    public string PropertiesJson => """
    {
      "base_level": {"type": "string"}, "top_level": {"type": "string"},
      "start": {"type": "array", "items": {"type": "number"}, "description": "[x, y] of the first riser's center, project units."},
      "direction": {"type": "array", "items": {"type": "number"}, "description": "[dx, dy] climbing direction, e.g. [0, 1] for north."},
      "width": {"type": "number", "description": "Run width, project units."},
      "shape": {"type": "string", "enum": ["straight", "l_left", "l_right", "u_left", "u_right"], "description": "Default straight. L and U turn at a landing halfway up."},
      "first_run_risers": {"type": "integer", "description": "Risers before the landing (default: half)."},
      "stair_type": {"type": "string"},
      "railings": {"type": "boolean", "description": "Add railings (default true)."},
      "railing_type": {"type": "string"}
    }
    """;
    public string[] Required => ["base_level", "top_level", "start", "direction"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var baseLevel = RevitHelpers.FindLevel(doc, args.String("base_level"));
        var topLevel = RevitHelpers.FindLevel(doc, args.String("top_level"));
        if (topLevel.ProjectElevation <= baseLevel.ProjectElevation) return ToolResult.Error("The top level must be above the base level.");
        // Run location paths are drawn at the run's base elevation measured from the stairs' base level, not in model Z.
        var start = ViewKit.Point(doc, args.Raw["start"], 0);
        start = new XYZ(start.X, start.Y, 0);
        var d = args.Raw["direction"];
        var dir = new XYZ(d[0].GetDouble(), d[1].GetDouble(), 0).Normalize();

        ElementId stairsId;
        int risers;
        using (var scope = new StairsEditScope(doc, "Sketch ProMax: Create stairs"))
        {
            stairsId = scope.Start(baseLevel.Id, topLevel.Id);
            using var t = new Transaction(doc, "Sketch ProMax: stair run");
            t.Start();
            var stairs = (Stairs)doc.GetElement(stairsId);
            if (args.OptString("stair_type") is { } typeName)
                stairs.ChangeTypeId(TypeKit.Pick<StairsType>(doc, typeName, "stair").Id);
            risers = stairs.DesiredRisersNumber;
            var tread = stairs.ActualTreadDepth;
            var shape = args.OptString("shape") ?? "straight";
            var width = args.OptDouble("width") is { } w ? RevitHelpers.ToInternal(doc, w) : (double?)null;
            if (shape == "straight")
            {
                var run = StairsRun.CreateStraightRun(doc, stairsId,
                    Line.CreateBound(start, start + dir * Math.Max(1, risers - 1) * tread), StairsRunJustification.Center);
                if (width is { } rw) run.ActualRunWidth = rw;
            }
            else
            {
                var r1 = (int)(args.OptLong("first_run_risers") ?? risers / 2);
                var r2 = risers - r1;
                if (r1 < 2 || r2 < 2) throw new ArgumentException("Each run needs at least 2 risers.");
                var riserHeight = stairs.ActualRiserHeight > 1e-9
                    ? stairs.ActualRiserHeight
                    : (topLevel.ProjectElevation - baseLevel.ProjectElevation) / Math.Max(1, risers);
                var run1 = StairsRun.CreateStraightRun(doc, stairsId,
                    Line.CreateBound(start, start + dir * (r1 - 1) * tread), StairsRunJustification.Center);
                if (width is { } rw1) run1.ActualRunWidth = rw1;
                var runWidth = run1.ActualRunWidth;
                var end1 = start + dir * (r1 - 1) * tread;
                var side = shape.EndsWith("left", StringComparison.Ordinal) ? new XYZ(-dir.Y, dir.X, 0) : new XYZ(dir.Y, -dir.X, 0);
                XYZ start2, dir2;
                if (shape.StartsWith("l", StringComparison.Ordinal))
                {
                    start2 = end1 + dir * (tread / 2 + runWidth / 2) + side * (runWidth / 2 + tread / 2);
                    dir2 = side;
                }
                else
                {
                    start2 = end1 + side * runWidth;
                    dir2 = -dir;
                }
                // The second run starts where the first one ends: r1 risers above the stairs' base.
                start2 = new XYZ(start2.X, start2.Y, r1 * riserHeight);
                var run2 = StairsRun.CreateStraightRun(doc, stairsId,
                    Line.CreateBound(start2, start2 + dir2 * (r2 - 1) * tread), StairsRunJustification.Center);
                run2.ActualRunWidth = runWidth;
                if (StairsLanding.CanCreateAutomaticLanding(doc, run1.Id, run2.Id))
                    StairsLanding.CreateAutomaticLanding(doc, run1.Id, run2.Id);
            }
            t.Commit();
            scope.Commit(new TypeKit.SwallowWarnings());
        }

        var railingIds = new List<long>();
        string? railingNote = null;
        if (args.OptBool("railings", true))
        {
            railingNote = CreateRailingsTool.NoRailingTypes(doc);
            if (railingNote is null)
            {
                var created = new List<ElementId>();
                using (var t = RevitHelpers.StartTransaction(doc, "Stair railings"))
                {
                    try
                    {
                        var type = TypeKit.Pick<RailingType>(doc, args.OptString("railing_type"), "railing");
                        created.AddRange(Railing.Create(doc, stairsId, type.Id, RailingPlacementPosition.Treads));
                    }
                    catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or ArgumentException or InvalidOperationException)
                    {
                        railingNote = "The stair was made, but its railings weren't: " + ex.Message.Trim();
                    }
                    if (t.GetStatus() == TransactionStatus.Started) t.Commit();
                }
                // Revit can roll railings back at commit (e.g. a railing type whose rail or baluster families are missing).
                railingIds = CreateRailingsTool.Surviving(doc, created);
                if (railingNote is null && created.Count > 0 && railingIds.Count < created.Count)
                    railingNote = railingIds.Count == 0
                        ? "Revit removed the railings when committing, so the stair has none. Check that the railing type's rail and baluster families are loaded."
                        : $"Revit removed {created.Count - railingIds.Count} of {created.Count} railings when committing.";
            }
        }
        return ToolResult.Ok(new { stairsId = stairsId.Value, risers, railings = railingIds, railingNote });
    }
}

public sealed class CreateRailingsTool : IRevitTool
{
    public string Name => "create_railings";
    public string Description => "Create a railing along a path of plan points on a level (balconies, ramps, voids), or on an existing stair/ramp.";
    public string PropertiesJson => """
    {
      "level": {"type": "string"},
      "points": {"type": "array", "items": {"type": "array", "items": {"type": "number"}}, "description": "[[x, y], ...] in project units."},
      "host_id": {"type": "integer", "description": "Stair or ramp id instead of a path."},
      "railing_type": {"type": "string"}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        if (NoRailingTypes(doc) is { } none) return ToolResult.Error(none);
        if (args.OptLong("host_id") is null && !args.Has("points")) return ToolResult.Error("Give points or host_id.");
        var type = TypeKit.Pick<RailingType>(doc, args.OptString("railing_type"), "railing");
        var created = new List<ElementId>();
        using (var t = RevitHelpers.StartTransaction(doc, "Create railing"))
        {
            if (args.OptLong("host_id") is { } host)
                created.AddRange(Railing.Create(doc, new ElementId(host), type.Id, RailingPlacementPosition.Treads));
            else
            {
                var level = RevitHelpers.FindLevel(doc, args.OptString("level"));
                var pts = TypeKit.Points(doc, args.Raw["points"], level.ProjectElevation);
                var loop = new CurveLoop();
                for (var i = 0; i + 1 < pts.Count; i++) loop.Append(Line.CreateBound(pts[i], pts[i + 1]));
                created.Add(Railing.Create(doc, loop, type.Id, level.Id).Id);
            }
            t.Commit();
        }
        // Only report railings that are still there after the commit: Revit can roll them back.
        var ids = Surviving(doc, created);
        if (created.Count > 0 && ids.Count == 0)
            return ToolResult.Error("Revit removed the railing when committing, so nothing was created. Check that the railing type's rail and baluster families are loaded.");
        return ToolResult.Ok(new { railings = ids, removedByRevit = created.Count - ids.Count });
    }

    /// <summary>A message when the model has no railing types to use, else null.</summary>
    internal static string? NoRailingTypes(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(RailingType)).GetElementCount() == 0
            ? "No railing type loaded, so no railings were added. Load a railing type (e.g. from a template or another project with Transfer Project Standards) and run create_railings."
            : null;

    internal static List<long> Surviving(Document doc, IEnumerable<ElementId> ids) =>
        ids.Where(id => doc.GetElement(id) is { IsValidObject: true }).Select(id => id.Value).ToList();
}

public sealed class CreateRoomSeparationLinesTool : IRevitTool
{
    public string Name => "create_room_separation_lines";
    public string Description => "Draw room separation lines in a plan view (to split open spaces into rooms), from a list of plan points.";
    public string PropertiesJson => """
    {
      "view_id": {"type": "integer", "description": "Plan view, default active."},
      "points": {"type": "array", "items": {"type": "array", "items": {"type": "number"}}},
      "closed": {"type": "boolean"}
    }
    """;
    public string[] Required => ["points"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        if (ViewKit.ViewOrActive(doc, args.OptLong("view_id")) is not ViewPlan view) return ToolResult.Error("Use a plan view.");
        var z = view.GenLevel?.ProjectElevation ?? 0;
        var pts = TypeKit.Points(doc, args.Raw["points"], z);
        if (args.OptBool("closed") && pts.Count > 2) pts.Add(pts[0]);
        var curves = new CurveArray();
        for (var i = 0; i + 1 < pts.Count; i++) curves.Append(Line.CreateBound(pts[i], pts[i + 1]));
        using var t = RevitHelpers.StartTransaction(doc, "Room separation lines");
        var plane = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, z)));
        var made = doc.Create.NewRoomBoundaryLines(plane, curves, view);
        t.Commit();
        return ToolResult.Ok(new { lines = made.Size });
    }
}

public sealed class SetMaterialTool : IRevitTool
{
    public string Name => "set_material";
    public string Description =>
        "Create or edit a material (color, transparency, surface pattern color) and optionally assign it: to element " +
        "instances or types through a material parameter (default: the first material parameter found, e.g. " +
        "'Structural Material' or 'Material'), or paint it on all faces of elements.";
    public string PropertiesJson => """
    {
      "name": {"type": "string", "description": "Material name; created if it doesn't exist."},
      "based_on": {"type": "string", "description": "Existing material to duplicate when creating."},
      "color": {"type": "string", "description": "Shading color, hex like #C0A080."},
      "transparency": {"type": "integer"},
      "assign_to_ids": {"type": "array", "items": {"type": "integer"}, "description": "Elements or element types."},
      "parameter": {"type": "string", "description": "Material parameter name to set on those elements."},
      "paint_faces": {"type": "boolean", "description": "Paint the material on every face instead of setting a parameter."}
    }
    """;
    public string[] Required => ["name"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var name = args.String("name");
        var materials = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().ToList();
        using var t = RevitHelpers.StartTransaction(doc, "Set material");
        var material = materials.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
        var created = false;
        if (material is null)
        {
            var source = args.OptString("based_on") is { } b
                ? materials.FirstOrDefault(m => string.Equals(m.Name, b, StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException($"No material '{b}'.")
                : null;
            material = source != null ? source.Duplicate(name) : (Material)doc.GetElement(Material.Create(doc, name));
            created = true;
        }
        if (args.OptString("color") is { } c)
        {
            var color = Filters.ParseColor(c);
            material.Color = color;
            material.SurfaceForegroundPatternColor = color;
        }
        if (args.OptLong("transparency") is { } tr) material.Transparency = (int)Math.Max(0, Math.Min(100, tr));

        var assigned = 0;
        var problems = new List<string>();
        foreach (var id in args.LongList("assign_to_ids").Select(i => new ElementId(i)))
        {
            var e = doc.GetElement(id);
            if (e is null) { problems.Add($"{id.Value}: not found"); continue; }
            if (args.OptBool("paint_faces"))
            {
                var faces = 0;
                foreach (var solid in (e.get_Geometry(new Options()) ?? Enumerable.Empty<GeometryObject>()).OfType<Solid>())
                    foreach (Face f in solid.Faces)
                    {
                        try { doc.Paint(e.Id, f, material.Id); faces++; }
                        catch (Autodesk.Revit.Exceptions.ArgumentException) { }
                    }
                if (faces > 0) assigned++; else problems.Add($"{id.Value}: no paintable faces");
                continue;
            }
            var p = args.OptString("parameter") is { } pn
                ? e.LookupParameter(pn)
                : e.Parameters.Cast<Parameter>().FirstOrDefault(x => x.Definition.GetDataType() == SpecTypeId.Reference.Material && !x.IsReadOnly);
            if (p is null || p.IsReadOnly) { problems.Add($"{id.Value}: no editable material parameter (try its type id, or paint_faces)"); continue; }
            p.Set(material.Id);
            assigned++;
        }
        t.Commit();
        return ToolResult.Ok(new { material = material.Name, id = material.Id.Value, created, assigned, problems });
    }
}

public sealed class CreateMepRunsTool : IRevitTool
{
    public string Name => "create_mep_runs";
    public string Description =>
        "Create ducts, pipes, cable trays or conduits along a path of 3D points (x, y, z in project units; z is the " +
        "center height above the level), with elbows at the turns. Sizes in project units.";
    public string PropertiesJson => """
    {
      "kind": {"type": "string", "enum": ["duct", "pipe", "cable_tray", "conduit"]},
      "level": {"type": "string"},
      "points": {"type": "array", "items": {"type": "array", "items": {"type": "number"}}, "description": "[[x, y, z], ...]; z above the level."},
      "type": {"type": "string", "description": "Duct/pipe/tray/conduit type name."},
      "system_type": {"type": "string", "description": "Duct or piping system type, e.g. 'Supply Air', 'Domestic Cold Water'."},
      "diameter": {"type": "number", "description": "Round duct, pipe or conduit."},
      "width": {"type": "number"}, "height": {"type": "number"}
    }
    """;
    public string[] Required => ["kind", "points"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var level = RevitHelpers.FindLevel(doc, args.OptString("level"));
        var pts = args.Raw["points"].EnumerateArray()
            .Select(p => ViewKit.Point(doc, p) + new XYZ(0, 0, level.ProjectElevation)).ToList();
        if (pts.Count < 2) return ToolResult.Error("Give at least two points.");
        var kind = args.String("kind");

        using var t = RevitHelpers.StartTransaction(doc, $"Create {kind} run");
        var segments = new List<MEPCurve>();
        for (var i = 0; i + 1 < pts.Count; i++)
        {
            MEPCurve seg = kind switch
            {
                "duct" => Duct.Create(doc,
                    TypeKit.Pick<MechanicalSystemType>(doc, args.OptString("system_type"), "duct system").Id,
                    TypeKit.Pick<DuctType>(doc, args.OptString("type"), "duct").Id, level.Id, pts[i], pts[i + 1]),
                "pipe" => Pipe.Create(doc,
                    TypeKit.Pick<PipingSystemType>(doc, args.OptString("system_type"), "piping system").Id,
                    TypeKit.Pick<PipeType>(doc, args.OptString("type"), "pipe").Id, level.Id, pts[i], pts[i + 1]),
                "cable_tray" => CableTray.Create(doc, TypeKit.Pick<CableTrayType>(doc, args.OptString("type"), "cable tray").Id, pts[i], pts[i + 1], level.Id),
                _ => Conduit.Create(doc, TypeKit.Pick<ConduitType>(doc, args.OptString("type"), "conduit").Id, pts[i], pts[i + 1], level.Id),
            };
            SetSize(doc, seg, args);
            segments.Add(seg);
        }

        var elbows = 0;
        var problems = new List<string>();
        var missingElbow = segments.Count > 1 ? MissingElbow(doc, segments[0]) : null;
        for (var i = 0; i + 1 < segments.Count; i++)
        {
            var (a, b) = Closest(segments[i], segments[i + 1]);
            if (a is null || b is null) continue;
            try { doc.Create.NewElbowFitting(a, b); elbows++; }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                problems.Add(missingElbow != null ? $"turn {i + 1}: no elbow made. {missingElbow}" : $"turn {i + 1}: {ex.Message.Trim()}");
            }
        }
        t.Commit();
        return ToolResult.Ok(new { segments = segments.Select(s => s.Id.Value), elbows, problems });
    }

    /// <summary>
    /// Why Revit can't make an elbow for this segment's type, when the reason is that no elbow fitting is set:
    /// ducts and pipes take elbows from the type's routing preferences, cable trays and conduits from the type's
    /// Horizontal Bend / Bend fitting. Null when an elbow is set (or it can't be told).
    /// </summary>
    private static string? MissingElbow(Document doc, MEPCurve seg)
    {
        try
        {
            if (doc.GetElement(seg.GetTypeId()) is not MEPCurveType type) return null;
            if (seg is Duct or Pipe)
            {
                var rpm = type.RoutingPreferenceManager;
                if (rpm is null) return null;
                var rules = rpm.GetNumberOfRules(RoutingPreferenceRuleGroupType.Elbows);
                var hasPart = false;
                for (var i = 0; i < rules; i++)
                {
                    var partId = rpm.GetRule(RoutingPreferenceRuleGroupType.Elbows, i).MEPPartId;
                    if (partId != ElementId.InvalidElementId && doc.GetElement(partId) != null) { hasPart = true; break; }
                }
                return hasPart ? null
                    : $"The {(seg is Duct ? "duct" : "pipe")} type '{type.Name}' has no elbow fitting in its routing preferences. " +
                      "Load an elbow family and set it under Edit Type > Routing Preferences > Elbows, then run again.";
            }
            var bend = type.get_Parameter(BuiltInParameter.RBS_CURVETYPE_DEFAULT_HORIZONTAL_BEND_PARAM)
                       ?? type.get_Parameter(BuiltInParameter.RBS_CURVETYPE_DEFAULT_BEND_PARAM);
            if (bend is null) return null;
            var bendId = bend.AsElementId();
            return bendId != ElementId.InvalidElementId && doc.GetElement(bendId) != null ? null
                : $"The {(seg is CableTray ? "cable tray" : "conduit")} type '{type.Name}' has no bend fitting set. " +
                  "Load a bend family and set it in the type's Fittings (Horizontal Bend / Bend), then run again.";
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { return null; }
    }

    private static void SetSize(Document doc, MEPCurve seg, ToolArgs args)
    {
        void Set(BuiltInParameter bip, string key)
        {
            if (args.OptDouble(key) is { } v && seg.get_Parameter(bip) is { IsReadOnly: false } p) p.Set(RevitHelpers.ToInternal(doc, v));
        }
        Set(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM, "diameter");
        Set(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM, "diameter");
        Set(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM, "diameter");
        Set(BuiltInParameter.RBS_CURVE_WIDTH_PARAM, "width");
        Set(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM, "height");
        Set(BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM, "width");
        Set(BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM, "height");
    }

    private static (Connector?, Connector?) Closest(MEPCurve a, MEPCurve b)
    {
        Connector? bestA = null, bestB = null;
        var best = double.MaxValue;
        foreach (Connector ca in a.ConnectorManager.Connectors)
            foreach (Connector cb in b.ConnectorManager.Connectors)
            {
                var d = ca.Origin.DistanceTo(cb.Origin);
                if (d < best) { best = d; bestA = ca; bestB = cb; }
            }
        return (bestA, bestB);
    }
}

public sealed class DimensionOpeningsTool : IRevitTool
{
    public string Name => "dimension_openings";
    public string Description =>
        "In a plan view, add one dimension string per wall locating its doors and windows (opening edges, or centers), " +
        "offset to the outside of the wall.";
    public string PropertiesJson => """
    {
      "view_id": {"type": "integer"},
      "wall_ids": {"type": "array", "items": {"type": "integer"}, "description": "Default: every wall in the view with doors or windows."},
      "measure": {"type": "string", "enum": ["edges", "centers"], "description": "Default edges (rough opening sides)."},
      "offset": {"type": "number", "description": "Distance from the wall, project units. Default 1 m."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        if (ViewKit.ViewOrActive(doc, args.OptLong("view_id")) is not ViewPlan view) return ToolResult.Error("Use a plan view.");
        var offset = args.OptDouble("offset") is { } o ? RevitHelpers.ToInternal(doc, o) : UnitUtils.ConvertToInternalUnits(1, UnitTypeId.Meters);
        var centers = args.OptString("measure") == "centers";
        var wallFilter = new HashSet<long>(args.LongList("wall_ids"));

        var openings = new FilteredElementCollector(doc, view.Id).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
            .Where(f => f.Host is Wall && f.Category != null &&
                        (f.Category.Id.Value == (long)BuiltInCategory.OST_Doors || f.Category.Id.Value == (long)BuiltInCategory.OST_Windows))
            .Where(f => wallFilter.Count == 0 || wallFilter.Contains(f.Host.Id.Value))
            .GroupBy(f => f.Host.Id.Value);

        var created = new List<long>();
        var skipped = new List<string>();
        var z = view.GenLevel?.ProjectElevation ?? 0;
        using var t = RevitHelpers.StartTransaction(doc, "Dimension openings");
        foreach (var group in openings)
        {
            var wall = (Wall)doc.GetElement(new ElementId(group.Key));
            if (wall.Location is not LocationCurve { Curve: Line wallLine }) { skipped.Add($"wall {group.Key}: curved"); continue; }
            var refs = new ReferenceArray();
            foreach (var opening in group)
            {
                var kinds = centers
                    ? new[] { FamilyInstanceReferenceType.CenterLeftRight }
                    : new[] { FamilyInstanceReferenceType.Left, FamilyInstanceReferenceType.Right };
                foreach (var kind in kinds)
                    foreach (var r in opening.GetReferences(kind).Take(1)) refs.Append(r);
            }
            // Measure from the wall's ends too, so the openings are located.
            foreach (var r in WallEnds(wall)) refs.Append(r);
            if (refs.Size < 2) { skipped.Add($"wall {group.Key}: families have no left/right references"); continue; }

            var outward = wall.Orientation.Normalize() * (wall.Width / 2 + offset);
            var p0 = wallLine.GetEndPoint(0) + outward;
            var p1 = wallLine.GetEndPoint(1) + outward;
            try { created.Add(doc.Create.NewDimension(view, Line.CreateBound(new XYZ(p0.X, p0.Y, z), new XYZ(p1.X, p1.Y, z)), refs).Id.Value); }
            catch (Autodesk.Revit.Exceptions.ArgumentException ex) { skipped.Add($"wall {group.Key}: {ex.Message}"); }
        }
        t.Commit();
        return ToolResult.Ok(new { dimensionStrings = created.Count, ids = created, skipped });
    }

    /// <summary>The two end faces of a straight wall, found from its geometry (not the jambs of openings or recesses).</summary>
    private static IEnumerable<Reference> WallEnds(Wall wall)
    {
        var line = (Line)((LocationCurve)wall.Location).Curve;
        var dir = line.Direction;
        var start = line.GetEndPoint(0);
        var geometry = wall.get_Geometry(new Options { ComputeReferences = true });
        if (geometry is null) yield break;
        foreach (var solid in geometry.OfType<Solid>())
            foreach (Face face in solid.Faces)
            {
                if (face is not PlanarFace pf || Math.Abs(pf.FaceNormal.DotProduct(dir)) <= 0.99 || pf.Reference is null) continue;
                var along = (pf.Origin - start).DotProduct(dir);
                if (Math.Abs(along) < 0.05 || Math.Abs(along - line.Length) < 0.05) yield return pf.Reference;
            }
    }
}

public sealed class DimensionRoomsTool : IRevitTool
{
    public string Name => "dimension_rooms";
    public string Description =>
        "In a plan view, dimension each room's clear width and length (wall finish face to finish face) through the " +
        "room's center. Works on rectangular-ish rooms bounded by walls.";
    public string PropertiesJson => """
    {
      "view_id": {"type": "integer"},
      "room_ids": {"type": "array", "items": {"type": "integer"}, "description": "Default: every room in the view."},
      "directions": {"type": "string", "enum": ["both", "x", "y"]}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        if (ViewKit.ViewOrActive(doc, args.OptLong("view_id")) is not ViewPlan view) return ToolResult.Error("Use a plan view.");
        var ids = args.LongList("room_ids");
        // Unplaced or unenclosed rooms (area 0) have no boundary to dimension.
        var rooms = (ids.Count > 0
                ? ids.Select(i => doc.GetElement(new ElementId(i))).OfType<Room>()
                : new FilteredElementCollector(doc, view.Id).OfCategory(BuiltInCategory.OST_Rooms).OfType<Room>())
            .Where(r => r.Area > 0).ToList();
        var which = args.OptString("directions") ?? "both";
        var z = view.GenLevel?.ProjectElevation ?? 0;
        var created = new List<long>();
        var skipped = new List<string>();

        using var t = RevitHelpers.StartTransaction(doc, "Dimension rooms");
        foreach (var room in rooms)
        {
            if (room.Location is not LocationPoint lp) continue;
            var c = lp.Point;
            var faces = new List<PlanarFace>();
            var refs = new Dictionary<PlanarFace, Reference>();
            var boundary = room.GetBoundarySegments(new SpatialElementBoundaryOptions());
            if (boundary is null) { skipped.Add($"{room.Number}: no boundary"); continue; }
            var walls = boundary.Where(l => l != null).SelectMany(l => l).Where(seg => seg != null)
                .Select(seg => doc.GetElement(seg.ElementId)).OfType<Wall>().Distinct();
            foreach (var wall in walls)
                foreach (var shell in new[] { ShellLayerType.Interior, ShellLayerType.Exterior })
                    foreach (var r in HostObjectUtils.GetSideFaces(wall, shell))
                        if (wall.GetGeometryObjectFromReference(r) is PlanarFace pf && pf.FaceNormal.DotProduct(c - pf.Origin) > 0)
                        {
                            faces.Add(pf);
                            refs[pf] = r;
                        }

            foreach (var axis in new[] { XYZ.BasisX, XYZ.BasisY })
            {
                if (which == "x" && axis.IsAlmostEqualTo(XYZ.BasisY) || which == "y" && axis.IsAlmostEqualTo(XYZ.BasisX)) continue;
                double At(XYZ p) => p.DotProduct(axis);
                PlanarFace? Crossing(PlanarFace f)
                {
                    var hit = f.Project(new XYZ(c.X, c.Y, f.Origin.Z));
                    if (hit is null) return null;
                    var other = hit.XYZPoint - new XYZ(c.X, c.Y, hit.XYZPoint.Z);
                    return Math.Abs(other.DotProduct(new XYZ(-axis.Y, axis.X, 0))) < 0.05 ? f : null;
                }
                var low = faces.Where(f => f.FaceNormal.DotProduct(axis) > 0.99 && At(f.Origin) < At(c)).Select(Crossing).OfType<PlanarFace>()
                    .OrderByDescending(f => At(f.Origin)).FirstOrDefault();
                var high = faces.Where(f => f.FaceNormal.DotProduct(axis) < -0.99 && At(f.Origin) > At(c)).Select(Crossing).OfType<PlanarFace>()
                    .OrderBy(f => At(f.Origin)).FirstOrDefault();
                if (low is null || high is null) { skipped.Add($"{room.Number} ({(axis.X > 0 ? "x" : "y")}): no facing walls"); continue; }
                var array = new ReferenceArray();
                array.Append(refs[low]);
                array.Append(refs[high]);
                var a = new XYZ(c.X, c.Y, z) + axis * (At(low.Origin) - At(c));
                var b = new XYZ(c.X, c.Y, z) + axis * (At(high.Origin) - At(c));
                try { created.Add(doc.Create.NewDimension(view, Line.CreateBound(a, b), array).Id.Value); }
                catch (Autodesk.Revit.Exceptions.ArgumentException ex) { skipped.Add($"{room.Number}: {ex.Message}"); }
            }
        }
        t.Commit();
        return ToolResult.Ok(new { rooms = rooms.Count, dimensions = created.Count, skipped });
    }
}
