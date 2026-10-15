using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

/// <summary>Shared geometry for placing a dimension string across parallel references in a plan.</summary>
internal static class DimensionGeometry
{
    /// <summary>
    /// Builds a line perpendicular to the referenced elements' direction, positioned just beyond
    /// the elements' extent on the given side, at the view's cut height.
    /// </summary>
    public static Line Across(IList<XYZ> points, XYZ alongElements, double offset, double z)
    {
        var across = new XYZ(-alongElements.Y, alongElements.X, 0).Normalize();
        var along = alongElements.Normalize();

        var onAcross = points.Select(p => p.DotProduct(across)).ToList();
        var onAlong = points.Select(p => p.DotProduct(along)).ToList();
        double min = onAcross.Min(), max = onAcross.Max(), position = onAlong.Max() + offset;

        var a = across * (min - 1) + along * position;
        var b = across * (max + 1) + along * position;
        return Line.CreateBound(new XYZ(a.X, a.Y, z), new XYZ(b.X, b.Y, z));
    }

    public static bool IsParallel(XYZ a, XYZ b) => Math.Abs(Math.Abs(a.Normalize().DotProduct(b.Normalize())) - 1) < 1e-6;
}

public sealed class DimensionGridsTool : IRevitTool
{
    public string Name => "dimension_grids";
    public string Description =>
        "Add one continuous dimension string across each set of parallel straight grids visible in a plan view " +
        "(typically one horizontal and one vertical string), placed just outside the grid extents.";
    public string PropertiesJson => """
    {
      "view_id": {"type": "integer", "description": "Plan view (default: active view)."},
      "offset": {"type": "number", "description": "Distance beyond the grid ends, project units (default ~ 1 m / 3 ft)."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var view = ViewKit.ViewOrActive(doc, args.OptLong("view_id"));
        if (view is not ViewPlan) return ToolResult.Error("dimension_grids works in plan views.");
        var offset = args.OptDouble("offset") is { } o ? RevitHelpers.ToInternal(doc, o) : 3.0;

        var grids = new FilteredElementCollector(doc, view.Id).OfClass(typeof(Grid)).Cast<Grid>()
            .Where(g => g.Curve is Line).ToList();
        if (grids.Count < 2)
        {
            var inModel = new FilteredElementCollector(doc).OfClass(typeof(Grid)).GetElementCount();
            return ToolResult.Error(inModel == 0
                ? "The model has no grids."
                : $"Only {grids.Count} of the model's {inModel} grids are visible in '{view.Name}'. They may not reach this level " +
                  "(their vertical extents), be hidden in the view, or lie outside its crop region. Fix that, then try again.");
        }
        var groups = new List<List<Grid>>();
        foreach (var g in grids)
        {
            var dir = ((Line)g.Curve).Direction;
            var group = groups.FirstOrDefault(gr => DimensionGeometry.IsParallel(((Line)gr[0].Curve).Direction, dir));
            if (group is null) groups.Add([g]); else group.Add(g);
        }

        var z = view.GenLevel?.ProjectElevation ?? 0;
        if (groups.All(g => g.Count < 2))
            return ToolResult.Error($"No two grids in '{view.Name}' are parallel, so there is nothing to dimension between.");
        // One string per transaction: a string Revit refuses is reported and the others are kept.
        var specs = new List<DimensionBatch.Spec>();
        foreach (var group in groups.Where(g => g.Count >= 2))
        {
            var refs = new ReferenceArray();
            foreach (var g in group) refs.Append(new Reference(g));
            var dir = ((Line)group[0].Curve).Direction;
            var pts = group.SelectMany(g => new[] { g.Curve.GetEndPoint(0), g.Curve.GetEndPoint(1) }).ToList();
            specs.Add(new DimensionBatch.Spec($"grids {string.Join(", ", group.Select(g => g.Name).Take(6))}{(group.Count > 6 ? "..." : "")}", view,
                DimensionGeometry.Across(pts, dir, offset, z), refs));
        }
        var (created, result) = DimensionBatch.Create(doc, "Dimension grids", specs);
        var failures = result.Failed.Select(f => new { item = f.Item, reason = f.Error }).ToList();
        if (created.Count == 0)
            return ToolResult.Error($"Revit refused every dimension string in '{view.Name}' ({specs.Count} tried): {System.Text.Json.JsonSerializer.Serialize(failures)}. " +
                                    "The grids may be multi-segment, pinned in a way that hides their references, or not cut by this view; try dimension_walls or dimension the grids by hand.");
        return ToolResult.Ok(new { dimensionStrings = created.Count, ids = created, gridsFound = grids.Count, skipped = failures.Count, failures });
    }
}

public sealed class DimensionWallsTool : IRevitTool
{
    public string Name => "dimension_walls";
    public string Description =>
        "Add dimension strings across parallel straight walls in a plan view, measuring between wall faces " +
        "(exterior or interior). Works on the given walls, or all walls in the view. Produces one string per wall direction.";
    public string PropertiesJson => """
    {
      "view_id": {"type": "integer"},
      "wall_ids": {"type": "array", "items": {"type": "integer"}, "description": "Default: all walls in the view."},
      "measure_to": {"type": "string", "enum": ["exterior_face", "interior_face"]},
      "offset": {"type": "number", "description": "Distance beyond the walls, project units."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var view = ViewKit.ViewOrActive(doc, args.OptLong("view_id"));
        if (view is not ViewPlan) return ToolResult.Error("dimension_walls works in plan views.");
        var offset = args.OptDouble("offset") is { } o ? RevitHelpers.ToInternal(doc, o) : 3.0;
        var mode = args.OptString("measure_to") ?? "exterior_face";

        var ids = args.LongList("wall_ids");
        IEnumerable<Wall> walls = ids.Count > 0
            ? ids.Select(i => doc.GetElement(new ElementId(i))).OfType<Wall>()
            : new FilteredElementCollector(doc, view.Id).OfClass(typeof(Wall)).Cast<Wall>();
        var straight = walls.Where(w => w.Location is LocationCurve { Curve: Line }).ToList();

        var groups = new List<List<Wall>>();
        foreach (var w in straight)
        {
            var dir = Dir(w);
            var group = groups.FirstOrDefault(g => DimensionGeometry.IsParallel(Dir(g[0]), dir));
            if (group is null) groups.Add([w]); else group.Add(w);
        }

        var z = view.GenLevel?.ProjectElevation ?? 0;
        var skipped = 0;
        // One string per transaction: a string Revit refuses is reported and the others are kept.
        var specs = new List<DimensionBatch.Spec>();
        foreach (var group in groups.Where(g => g.Count >= 2))
        {
            var refs = new ReferenceArray();
            foreach (var w in group)
            {
                var r = WallReference(w, mode);
                if (r is null) skipped++; else refs.Append(r);
            }
            if (refs.Size < 2) continue;
            var pts = group.SelectMany(w => new[] { ((LocationCurve)w.Location).Curve.GetEndPoint(0), ((LocationCurve)w.Location).Curve.GetEndPoint(1) }).ToList();
            specs.Add(new DimensionBatch.Spec($"walls {string.Join(", ", group.Select(w => w.Id.Value).Take(6))}{(group.Count > 6 ? "..." : "")}", view,
                DimensionGeometry.Across(pts, Dir(group[0]), offset, z), refs));
        }
        var (created, result) = DimensionBatch.Create(doc, "Dimension walls", specs);
        var failures = result.Failed.Select(f => new { item = f.Item, reason = f.Error }).ToList();
        if (created.Count == 0)
        {
            var reason = straight.Count == 0 ? "no straight walls are visible in this view (or among the given ids)"
                : groups.All(g => g.Count < 2) ? "each string needs at least two parallel straight walls, and none of the walls given have a parallel partner"
                : failures.Count > 0 ? $"Revit refused them: {System.Text.Json.JsonSerializer.Serialize(failures)}"
                : "Revit could not find usable faces on these walls (curtain or stacked walls, or walls joined in a way that hides the face)";
            return ToolResult.Error($"No dimension strings were added in '{view.Name}': {reason}.");
        }
        return ToolResult.Ok(new { dimensionStrings = created.Count, ids = created, wallsConsidered = straight.Count, skipped, failures });
    }

    private static XYZ Dir(Wall w) => ((Line)((LocationCurve)w.Location).Curve).Direction;

    internal static Reference? WallReference(Wall w, string mode)
    {
        var side = mode == "interior_face" ? ShellLayerType.Interior : ShellLayerType.Exterior;
        try
        {
            var faces = HostObjectUtils.GetSideFaces(w, side);
            return faces.Count > 0 ? faces[0] : null;
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { return null; } // curtain/stacked walls have no side faces

    }
}
