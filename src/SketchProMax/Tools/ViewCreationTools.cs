using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

internal static class ViewKit
{
    public static ViewFamilyType TypeFor(Document doc, ViewFamily family) =>
        new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
            .FirstOrDefault(t => t.ViewFamily == family)
        ?? throw new InvalidOperationException($"This model has no {family} view type.");

    public static View ViewOrActive(Document doc, long? id) =>
        id is { } v ? doc.GetElement(new ElementId(v)) as View ?? throw new ArgumentException($"{v} is not a view.") : doc.ActiveView;

    /// <summary>Applies the shared optional settings: name, scale, view template. Returns the final name.</summary>
    public static string Finish(Document doc, View view, string? name, long? scale, string? template)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            try { view.Name = name; }
            catch (Autodesk.Revit.Exceptions.ArgumentException)
            {
                // Either a forbidden character or a duplicate: drop the characters Revit refuses, then make it unique.
                var clean = new string(name!.Where(ch => "\\:{}[]|;<>?`~".IndexOf(ch) < 0).ToArray()).Trim();
                if (clean.Length == 0) clean = "View";
                try { view.Name = clean; }
                catch (Autodesk.Revit.Exceptions.ArgumentException) { view.Name = Unique(doc, clean); }
            }
        }
        if (scale is { } s && view.ViewTemplateId == ElementId.InvalidElementId) view.Scale = (int)s;
        if (ViewTemplates.Find(doc, template) is { } t) view.ViewTemplateId = t.Id;
        return view.Name;
    }

    private static string Unique(Document doc, string name)
    {
        var taken = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(View)).Select(v => v.Name), StringComparer.OrdinalIgnoreCase);
        for (var i = 2; ; i++)
            if (!taken.Contains($"{name} ({i})")) return $"{name} ({i})";
    }

    /// <summary>Facade name from the direction the viewer stands (a view's ViewDirection points toward the viewer).</summary>
    public static string Compass(XYZ towardViewer)
    {
        if (Math.Abs(towardViewer.X) >= Math.Abs(towardViewer.Y)) return towardViewer.X >= 0 ? "East" : "West";
        return towardViewer.Y >= 0 ? "North" : "South";
    }

    public static XYZ Point(Document doc, System.Text.Json.JsonElement p, double z = 0) =>
        new(RevitHelpers.ToInternal(doc, p[0].GetDouble()), RevitHelpers.ToInternal(doc, p[1].GetDouble()),
            p.GetArrayLength() > 2 ? RevitHelpers.ToInternal(doc, p[2].GetDouble()) : z);

    /// <summary>
    /// A point given in a view's own coordinates: plans take model x, y (at the level); sections, elevations and
    /// drafting views take x along the view's right direction and y up from the view origin. Project units.
    /// </summary>
    public static XYZ ToModel(Document doc, View view, System.Text.Json.JsonElement p) =>
        ToModel(doc, view, p[0].GetDouble(), p[1].GetDouble());

    public static XYZ ToModel(Document doc, View view, double xProject, double yProject)
    {
        double x = RevitHelpers.ToInternal(doc, xProject), y = RevitHelpers.ToInternal(doc, yProject);
        if (view is ViewPlan plan) return new XYZ(x, y, plan.GenLevel?.Elevation ?? 0);
        return view.Origin + view.RightDirection * x + view.UpDirection * y;
    }

    /// <summary>All eight corners of a box, in model coordinates (its own transform applied).</summary>
    public static IEnumerable<XYZ> Corners(BoundingBoxXYZ b)
    {
        var tr = b.Transform ?? Transform.Identity;
        foreach (var x in new[] { b.Min.X, b.Max.X })
            foreach (var y in new[] { b.Min.Y, b.Max.Y })
                foreach (var z in new[] { b.Min.Z, b.Max.Z })
                    yield return tr.OfPoint(new XYZ(x, y, z));
    }

    public static BoundingBoxXYZ? Extents(Document doc, IEnumerable<Element> elements, View? view = null)
    {
        BoundingBoxXYZ? box = null;
        foreach (var e in elements)
        {
            var b = e.get_BoundingBox(view);
            if (b is null) continue;
            if (box is null) { box = new BoundingBoxXYZ { Min = b.Min, Max = b.Max }; continue; }
            box.Min = new XYZ(Math.Min(box.Min.X, b.Min.X), Math.Min(box.Min.Y, b.Min.Y), Math.Min(box.Min.Z, b.Min.Z));
            box.Max = new XYZ(Math.Max(box.Max.X, b.Max.X), Math.Max(box.Max.Y, b.Max.Y), Math.Max(box.Max.Z, b.Max.Z));
        }
        return box;
    }

    public static BoundingBoxXYZ Grow(BoundingBoxXYZ b, double margin) =>
        new() { Min = b.Min - new XYZ(margin, margin, margin), Max = b.Max + new XYZ(margin, margin, margin) };
}

public sealed class CreateSectionTool : IRevitTool
{
    public string Name => "create_section";
    public string Description =>
        "Create a section view along a line drawn in plan (start and end points in project units). Looking direction: " +
        "to the LEFT of the line when walking from start to end; swap the points to look the other way. " +
        "Set the depth (how far it sees) and the bottom/top heights, or give element ids to cut through their middle.";
    public string PropertiesJson => """
    {
      "start": {"type": "array", "items": {"type": "number"}, "description": "[x, y] in project units."},
      "end": {"type": "array", "items": {"type": "number"}},
      "element_ids": {"type": "array", "items": {"type": "integer"}, "description": "Alternative to start/end: a section through these elements (e.g. a wall, or a whole building's walls), cut along their longer side."},
      "depth": {"type": "number", "description": "How far the section looks, project units. Default: 10 m equivalent."},
      "bottom": {"type": "number", "description": "Lowest elevation shown. Default: lowest level minus a little."},
      "top": {"type": "number", "description": "Highest elevation shown. Default: highest level plus a little."},
      "name": {"type": "string"}, "scale": {"type": "integer"}, "view_template": {"type": "string"}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().Select(l => l.Elevation).ToList();
        double meter = UnitUtils.ConvertToInternalUnits(1, UnitTypeId.Meters);
        XYZ p0, p1;
        double depth;
        if (args.LongList("element_ids") is { Count: > 0 } ids)
        {
            var box = ViewKit.Extents(doc, ids.Select(i => doc.GetElement(new ElementId(i))).Where(e => e != null))
                      ?? throw new ArgumentException("Those elements have no geometry to cut through.");
            var mid = (box.Min + box.Max) / 2;
            var alongX = box.Max.X - box.Min.X >= box.Max.Y - box.Min.Y;
            p0 = alongX ? new XYZ(box.Min.X - meter, mid.Y, 0) : new XYZ(mid.X, box.Max.Y + meter, 0);
            p1 = alongX ? new XYZ(box.Max.X + meter, mid.Y, 0) : new XYZ(mid.X, box.Min.Y - meter, 0);
            depth = (alongX ? box.Max.Y - box.Min.Y : box.Max.X - box.Min.X) / 2 + meter;
        }
        else
        {
            if (!args.Has("start") || !args.Has("end")) throw new ArgumentException("Give start and end points, or element_ids.");
            p0 = ViewKit.Point(doc, args.Raw["start"]);
            p1 = ViewKit.Point(doc, args.Raw["end"]);
            depth = 10 * meter;
        }
        if (args.OptDouble("depth") is { } d) depth = RevitHelpers.ToInternal(doc, d);
        p0 = new XYZ(p0.X, p0.Y, 0);
        p1 = new XYZ(p1.X, p1.Y, 0);
        if (p0.DistanceTo(p1) < 0.1) throw new ArgumentException("Start and end are the same point.");

        var bottom = args.OptDouble("bottom") is { } b ? RevitHelpers.ToInternal(doc, b) : (levels.Count > 0 ? levels.Min() : 0) - meter;
        var top = args.OptDouble("top") is { } tp ? RevitHelpers.ToInternal(doc, tp) : (levels.Count > 1 ? levels.Max() : bottom + 4 * meter) + 2 * meter;

        var dir = (p1 - p0).Normalize();
        var up = XYZ.BasisZ;
        var transform = Transform.Identity;
        transform.Origin = (p0 + p1) / 2;
        transform.BasisX = dir;
        transform.BasisY = up;
        transform.BasisZ = dir.CrossProduct(up);
        var half = p0.DistanceTo(p1) / 2;
        var sectionBox = new BoundingBoxXYZ
        {
            Transform = transform,
            Min = new XYZ(-half, bottom, -depth),
            Max = new XYZ(half, top, 0),
        };

        using var t = RevitHelpers.StartTransaction(doc, "Create section");
        var view = ViewSection.CreateSection(doc, ViewKit.TypeFor(doc, ViewFamily.Section).Id, sectionBox);
        var name = ViewKit.Finish(doc, view, args.OptString("name"), args.OptLong("scale"), args.OptString("view_template"));
        t.Commit();
        return ToolResult.Ok(new { id = view.Id.Value, name, looking = ViewKit.Compass(-view.ViewDirection) + "-ish" });
    }
}

public sealed class CreateElevationsTool : IRevitTool
{
    public string Name => "create_elevations";
    public string Description =>
        "Create elevation views. mode 'building': North/South/East/West exterior elevations around the model (or around " +
        "given elements), placed in a plan view. mode 'interior': four interior elevations for each room given (or every " +
        "placed room on a level), named '{number} {name} - North' etc.";
    public string PropertiesJson => """
    {
      "mode": {"type": "string", "enum": ["building", "interior"]},
      "plan_view_id": {"type": "integer", "description": "Plan view to host the elevation markers (default: active view, or the level's first floor plan for interior mode)."},
      "sides": {"type": "array", "items": {"type": "string", "enum": ["North", "South", "East", "West"]}, "description": "Default: all four."},
      "element_ids": {"type": "array", "items": {"type": "integer"}, "description": "building: elements to wrap (default: all walls). interior: room ids."},
      "level": {"type": "string", "description": "interior: rooms on this level when no room ids are given."},
      "offset": {"type": "number", "description": "building: distance from the building to each marker, project units. Default 3 m."},
      "name_pattern": {"type": "string", "description": "building: e.g. '{side} Elevation' (default). interior: e.g. '{number} {name} - {side}' (default)."},
      "scale": {"type": "integer"}, "view_template": {"type": "string"}
    }
    """;
    public string[] Required => ["mode"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var vft = ViewKit.TypeFor(doc, ViewFamily.Elevation);
        var sides = args.StringList("sides");
        if (sides.Count == 0) sides = ["North", "South", "East", "West"];
        var scale = args.OptLong("scale") ?? 100;
        var created = new List<object>();
        var skipped = new List<string>();

        using var t = RevitHelpers.StartTransaction(doc, "Create elevations");
        if (args.String("mode") == "interior")
        {
            var rooms = args.LongList("element_ids").Select(i => doc.GetElement(new ElementId(i))).OfType<Room>().ToList();
            if (rooms.Count == 0)
            {
                var level = RevitHelpers.FindLevel(doc, args.OptString("level"));
                rooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).OfType<Room>()
                    .Where(r => r.LevelId == level.Id && r.Area > 0).ToList();
            }
            var pattern = args.OptString("name_pattern") ?? "{number} {name} - {side}";
            foreach (var room in rooms)
            {
                if (room.Location is not LocationPoint lp) { skipped.Add($"{room.Number}: not placed"); continue; }
                var plan = HostPlan(doc, args.OptLong("plan_view_id"), room.LevelId);
                var marker = ElevationMarker.CreateElevationMarker(doc, vft.Id, lp.Point, (int)scale);
                for (var i = 0; i < 4; i++)
                {
                    if (!marker.IsAvailableIndex(i)) continue;
                    var view = marker.CreateElevation(doc, plan.Id, i);
                    var side = ViewKit.Compass(-view.ViewDirection); // interior: the wall you look at
                    if (!sides.Contains(side, StringComparer.OrdinalIgnoreCase)) { doc.Delete(view.Id); continue; }
                    var name = ViewKit.Finish(doc, view,
                        pattern.Replace("{number}", room.Number).Replace("{name}", room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "").Replace("{side}", side),
                        scale, args.OptString("view_template"));
                    created.Add(new { id = view.Id.Value, name, room = room.Number });
                }
            }
        }
        else
        {
            var plan = HostPlan(doc, args.OptLong("plan_view_id"), null);
            var elements = args.LongList("element_ids").Select(i => doc.GetElement(new ElementId(i))).Where(e => e != null).ToList();
            if (elements.Count == 0)
                elements = new FilteredElementCollector(doc).OfClass(typeof(Wall)).WhereElementIsNotElementType().ToList();
            var box = ViewKit.Extents(doc, elements) ?? throw new InvalidOperationException("No walls found to place elevations around.");
            var offset = RevitHelpers.ToInternal(doc, args.OptDouble("offset") ?? FromMeters(doc, 3));
            var mid = (box.Min + box.Max) / 2;
            var z = plan.GenLevel?.Elevation ?? 0;
            var pattern = args.OptString("name_pattern") ?? "{side} Elevation";
            foreach (var side in sides)
            {
                var (point, outward) = side.ToLowerInvariant() switch
                {
                    "north" => (new XYZ(mid.X, box.Max.Y + offset, z), XYZ.BasisY),
                    "south" => (new XYZ(mid.X, box.Min.Y - offset, z), -XYZ.BasisY),
                    "east" => (new XYZ(box.Max.X + offset, mid.Y, z), XYZ.BasisX),
                    _ => (new XYZ(box.Min.X - offset, mid.Y, z), -XYZ.BasisX),
                };
                var marker = ElevationMarker.CreateElevationMarker(doc, vft.Id, point, (int)scale);
                ViewSection? made = null;
                for (var i = 0; i < 4 && made is null; i++)
                {
                    var view = marker.CreateElevation(doc, plan.Id, i);
                    if (view.ViewDirection.DotProduct(outward) > 0.9) made = view;
                    else doc.Delete(view.Id);
                }
                if (made is null) { doc.Delete(marker.Id); skipped.Add(side); continue; }
                var name = ViewKit.Finish(doc, made, pattern.Replace("{side}", side), scale, args.OptString("view_template"));
                created.Add(new { id = made.Id.Value, name });
            }
        }
        t.Commit();
        return ToolResult.Ok(new { created = created.Count, views = created, skipped });
    }

    private static double FromMeters(Document doc, double m) =>
        RevitHelpers.FromInternal(doc, UnitUtils.ConvertToInternalUnits(m, UnitTypeId.Meters));

    private static ViewPlan HostPlan(Document doc, long? id, ElementId? levelId)
    {
        if (id is { } v) return doc.GetElement(new ElementId(v)) as ViewPlan ?? throw new ArgumentException("plan_view_id is not a plan view.");
        if (levelId is null && doc.ActiveView is ViewPlan active) return active;
        return new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                   .Where(p => !p.IsTemplate && p.ViewType == ViewType.FloorPlan)
                   .FirstOrDefault(p => levelId is null || p.GenLevel?.Id == levelId)
               ?? throw new InvalidOperationException("No floor plan found to host the elevation markers. Create one first.");
    }
}

public sealed class CreateCalloutTool : IRevitTool
{
    public string Name => "create_callout";
    public string Description =>
        "Create a callout (enlarged plan or detail) in a plan, section or elevation, from two opposite corners in the " +
        "parent view's coordinates (plans: model x, y in project units). Or give element ids and the callout wraps them.";
    public string PropertiesJson => """
    {
      "parent_view_id": {"type": "integer", "description": "Default: active view."},
      "corner1": {"type": "array", "items": {"type": "number"}},
      "corner2": {"type": "array", "items": {"type": "number"}},
      "element_ids": {"type": "array", "items": {"type": "integer"}},
      "margin": {"type": "number", "description": "Around element_ids, project units. Default 0.5 m."},
      "name": {"type": "string"}, "scale": {"type": "integer", "description": "e.g. 50 or 20."}, "view_template": {"type": "string"}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var parent = ViewKit.ViewOrActive(doc, args.OptLong("parent_view_id"));
        XYZ a, b;
        if (args.LongList("element_ids") is { Count: > 0 } ids)
        {
            var margin = RevitHelpers.ToInternal(doc, args.OptDouble("margin") ?? RevitHelpers.FromInternal(doc, UnitUtils.ConvertToInternalUnits(0.5, UnitTypeId.Meters)));
            var box = ViewKit.Extents(doc, ids.Select(i => doc.GetElement(new ElementId(i))).Where(e => e != null), parent)
                      ?? throw new ArgumentException("Those elements are not visible in the parent view.");
            box = ViewKit.Grow(box, margin);
            a = box.Min;
            b = box.Max;
        }
        else
        {
            // Plans: model x, y at the level; other views: [u, v] along the view's right and up directions.
            a = ViewKit.ToModel(doc, parent, args.Raw["corner1"]);
            b = ViewKit.ToModel(doc, parent, args.Raw["corner2"]);
        }

        var family = parent.ViewType switch
        {
            ViewType.FloorPlan or ViewType.CeilingPlan or ViewType.EngineeringPlan or ViewType.AreaPlan => ((ViewFamilyType)doc.GetElement(parent.GetTypeId())).ViewFamily,
            _ => ViewFamily.Detail,
        };
        using var t = RevitHelpers.StartTransaction(doc, "Create callout");
        var view = ViewSection.CreateCallout(doc, parent.Id, ViewKit.TypeFor(doc, family).Id, a, b);
        var name = ViewKit.Finish(doc, view, args.OptString("name"), args.OptLong("scale"), args.OptString("view_template"));
        t.Commit();
        return ToolResult.Ok(new { id = view.Id.Value, name, parent = parent.Name });
    }
}

public sealed class Create3DViewTool : IRevitTool
{
    public string Name => "create_3d_view";
    public string Description =>
        "Create an isometric 3D view, optionally with a section box around given elements or around one level " +
        "(from that level to the next one up).";
    public string PropertiesJson => """
    {
      "element_ids": {"type": "array", "items": {"type": "integer"}},
      "level": {"type": "string"},
      "margin": {"type": "number", "description": "Section box margin, project units. Default 1 m."},
      "name": {"type": "string"}, "view_template": {"type": "string"}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var margin = args.OptDouble("margin") is { } m ? RevitHelpers.ToInternal(doc, m) : UnitUtils.ConvertToInternalUnits(1, UnitTypeId.Meters);
        BoundingBoxXYZ? box = null;
        if (args.LongList("element_ids") is { Count: > 0 } ids)
            box = ViewKit.Extents(doc, ids.Select(i => doc.GetElement(new ElementId(i))).Where(e => e != null));
        else if (args.OptString("level") is { } levelName)
        {
            var level = RevitHelpers.FindLevel(doc, levelName);
            var above = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .Where(l => l.Elevation > level.Elevation + 0.01).OrderBy(l => l.Elevation).FirstOrDefault();
            var all = ViewKit.Extents(doc, new FilteredElementCollector(doc).OfClass(typeof(Wall)).WhereElementIsNotElementType());
            if (all != null)
                box = new BoundingBoxXYZ
                {
                    Min = new XYZ(all.Min.X, all.Min.Y, level.Elevation),
                    Max = new XYZ(all.Max.X, all.Max.Y, above?.Elevation - 0.1 ?? level.Elevation + UnitUtils.ConvertToInternalUnits(4, UnitTypeId.Meters)),
                };
        }

        using var t = RevitHelpers.StartTransaction(doc, "Create 3D view");
        var view = View3D.CreateIsometric(doc, ViewKit.TypeFor(doc, ViewFamily.ThreeDimensional).Id);
        if (box != null) view.SetSectionBox(ViewKit.Grow(box, margin));
        var name = ViewKit.Finish(doc, view, args.OptString("name"), null, args.OptString("view_template"));
        t.Commit();
        return ToolResult.Ok(new { id = view.Id.Value, name, sectionBox = box != null });
    }
}
