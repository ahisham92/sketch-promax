using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

/// <summary>
/// A view's 2D drawing frame in model space: origin, right, up and the normal toward the viewer (right x up).
/// Plans use model X/Y at their level, so plan-to-plan copies keep model positions.
/// </summary>
internal sealed record ViewFrame(XYZ Origin, XYZ Right, XYZ Up, XYZ Normal)
{
    public static ViewFrame Of(View v) => v is ViewPlan p
        ? new(new XYZ(0, 0, p.GenLevel?.ProjectElevation ?? v.Origin.Z), XYZ.BasisX, XYZ.BasisY, XYZ.BasisZ)
        : new(v.Origin, v.RightDirection, v.UpDirection, v.RightDirection.CrossProduct(v.UpDirection).Normalize());

    public (double X, double Y) Local(XYZ p)
    {
        var d = p - Origin;
        return (d.DotProduct(Right), d.DotProduct(Up));
    }

    public XYZ Model(double x, double y) => Origin + Right * x + Up * y;
    public XYZ Vector(double x, double y) => Right * x + Up * y;

    /// <summary>A rigid 2D motion in this frame's coordinates as a model transform (rotation about the normal, in-plane move).</summary>
    public Transform ToTransform(Rigid2 r) =>
        Transform.CreateTranslation(Vector(r.Tx, r.Ty)).Multiply(Transform.CreateRotationAtPoint(Normal, r.Angle, Origin));

    /// <summary>Moves (and turns) one element by a rigid motion given in this frame. Rotation is about the view normal.</summary>
    public void Apply(Document doc, ElementId id, Rigid2 r)
    {
        if (Math.Abs(r.Angle) > 1e-9)
            ElementTransformUtils.RotateElement(doc, id, Line.CreateBound(Origin, Origin + Normal), r.Angle);
        var v = Vector(r.Tx, r.Ty);
        if (!v.IsZeroLength()) ElementTransformUtils.MoveElement(doc, id, v);
    }
}

/// <summary>Rigid motion in a view's 2D frame: p' = Rot(Angle) p + (Tx, Ty). Lengths in internal feet.</summary>
internal readonly record struct Rigid2(double Angle, double Tx, double Ty)
{
    public static Rigid2 Identity => new(0, 0, 0);

    public (double X, double Y) Apply((double X, double Y) p)
    {
        double c = Math.Cos(Angle), s = Math.Sin(Angle);
        return (c * p.X - s * p.Y + Tx, s * p.X + c * p.Y + Ty);
    }

    /// <summary>This motion, then <paramref name="next"/>.</summary>
    public Rigid2 Then(Rigid2 next)
    {
        var t = next.Apply((Tx, Ty));
        return new(Normalize(Angle + next.Angle), t.X, t.Y);
    }

    public Rigid2 Inverse()
    {
        double c = Math.Cos(-Angle), s = Math.Sin(-Angle);
        return new(-Angle, -(c * Tx - s * Ty), -(s * Tx + c * Ty));
    }

    public static Rigid2 RotationAbout((double X, double Y) center, double angle)
    {
        double c = Math.Cos(angle), s = Math.Sin(angle);
        return new(angle, center.X - (c * center.X - s * center.Y), center.Y - (s * center.X + c * center.Y));
    }

    public bool IsIdentity(double length = 1e-6, double angle = 1e-9) =>
        Math.Abs(Normalize(Angle)) < angle && Math.Abs(Tx) < length && Math.Abs(Ty) < length;

    /// <summary>Least-squares rigid fit (2D Kabsch) taking <paramref name="from"/> onto <paramref name="to"/>.</summary>
    public static Rigid2 Fit(IList<(double X, double Y)> from, IList<(double X, double Y)> to, bool rotate = true)
    {
        if (from.Count == 0 || from.Count != to.Count) throw new ArgumentException("Point pairs must be given in equal, non-zero numbers.");
        double fx = from.Average(p => p.X), fy = from.Average(p => p.Y), tx = to.Average(p => p.X), ty = to.Average(p => p.Y);
        double angle = 0;
        if (rotate && from.Count > 1)
        {
            double sCross = 0, sDot = 0;
            for (var i = 0; i < from.Count; i++)
            {
                double ax = from[i].X - fx, ay = from[i].Y - fy, bx = to[i].X - tx, by = to[i].Y - ty;
                sCross += ax * by - ay * bx;
                sDot += ax * bx + ay * by;
            }
            if (Math.Abs(sCross) + Math.Abs(sDot) > 1e-12) angle = Math.Atan2(sCross, sDot);
        }
        double c = Math.Cos(angle), s = Math.Sin(angle);
        return new(angle, tx - (c * fx - s * fy), ty - (s * fx + c * fy));
    }

    public double Rms(IList<(double X, double Y)> from, IList<(double X, double Y)> to)
    {
        if (from.Count == 0) return 0;
        double sum = 0;
        for (var i = 0; i < from.Count; i++)
        {
            var p = Apply(from[i]);
            sum += (p.X - to[i].X) * (p.X - to[i].X) + (p.Y - to[i].Y) * (p.Y - to[i].Y);
        }
        return Math.Sqrt(sum / from.Count);
    }

    public static double Normalize(double a)
    {
        while (a > Math.PI) a -= 2 * Math.PI;
        while (a <= -Math.PI) a += 2 * Math.PI;
        return a;
    }
}

/// <summary>
/// Moving section / elevation views and keeping their view-specific detailing with them. Revit moves a section
/// through its viewer element (the marker drawn in other views): ElementTransformUtils on the view's own id is
/// silently ignored. Some view-specific elements (generic annotation symbols in particular) keep their model position
/// instead of following the view, so the tools measure every element in the view's own 2D frame before and after the
/// move and put back the ones that stayed behind.
/// </summary>
internal static class ViewRelocation
{
    public const double Tolerance = 1.0 / 304.8; // 1 mm

    public const string PlacementProperties = """
      "offset": {"type": "array", "items": {"type": "number"}, "description": "Move by [dx, dy] (or [dx, dy, dz]) in project units, model axes."},
      "rotation_degrees": {"type": "number", "description": "Turn the view about a vertical axis, counter-clockwise in plan. Applied before offset."},
      "rotation_center": {"type": "array", "items": {"type": "number"}, "description": "[x, y] of the rotation axis, project units. Default: the middle of the section line."},
      "target_point": {"type": "array", "items": {"type": "number"}, "description": "Move the middle of the section line to this [x, y] point (no rotation). Alternative to offset."},
      "target_line": {"type": "object", "description": "Put the section line on this plan line (its middle on the line's middle, turned to the line's direction). The section keeps its depth, width and height.",
        "properties": {"start": {"type": "array", "items": {"type": "number"}}, "end": {"type": "array", "items": {"type": "number"}}}, "required": ["start", "end"]},
      "keep_direction": {"type": "boolean", "description": "With target_line: keep looking the same general way as the source view (default true). false = look to the LEFT of start->end, as create_section does."},
      "fix_detailing": {"type": "boolean", "description": "Put back view-specific elements that did not follow the view (default true)."}
    """;

    /// <summary>A model-space placement: rotate about a vertical axis through Center, then translate.</summary>
    public sealed record Placement(XYZ Translation, double Angle, XYZ Center)
    {
        public Transform Transform =>
            Transform.CreateTranslation(Translation).Multiply(Transform.CreateRotationAtPoint(XYZ.BasisZ, Angle, Center));

        public bool IsIdentity => Translation.GetLength() < 1e-9 && Math.Abs(Angle) < 1e-12;
    }

    /// <summary>The section / elevation / detail view to relocate, with a clear message for anything else.</summary>
    public static ViewSection SectionView(Document doc, long id)
    {
        var e = doc.GetElement(new ElementId(id)) ?? throw new ArgumentException($"No element {id} in this model.");
        if (e is not ViewSection v || v.IsTemplate || v.ViewType is not (ViewType.Section or ViewType.Elevation or ViewType.Detail))
            throw new ArgumentException($"{id} ('{e.Name}') is not a section, elevation or detail view. Only views drawn by a marker in other views can be moved this way.");
        if (v.GetPrimaryViewId() != ElementId.InvalidElementId)
            throw new ArgumentException($"'{v.Name}' is a dependent view; it moves with its primary view {v.GetPrimaryViewId().Value}. Relocate the primary view instead.");
        return v;
    }

    /// <summary>
    /// The element Revit actually moves to move this view: the section/detail viewer, or the elevation marker when it
    /// carries only this elevation.
    /// </summary>
    public static Element Marker(Document doc, View view, out string kind)
    {
        if (view.ViewType == ViewType.Elevation)
        {
            foreach (var m in new FilteredElementCollector(doc).OfClass(typeof(ElevationMarker)).Cast<ElevationMarker>())
            {
                var hosts = false;
                for (var i = 0; i < m.MaximumViewCount && !hosts; i++) hosts = m.GetViewId(i) == view.Id;
                if (!hosts) continue;
                if (m.CurrentViewCount > 1)
                    throw new InvalidOperationException(
                        $"The elevation marker {m.Id.Value} also carries {m.CurrentViewCount - 1} other elevation(s); moving it would move those too. " +
                        "Use duplicate_view_to_location (a duplicated elevation gets its own marker), or move the marker by hand.");
                kind = "elevation marker";
                return m;
            }
        }

        var viewersCat = new ElementId(BuiltInCategory.OST_Viewers);
        var dependents = view.GetDependentElements(new ElementCategoryFilter(BuiltInCategory.OST_Viewers))
            .Select(i => doc.GetElement(i)).Where(e => e != null).ToList();
        var viewer = dependents.FirstOrDefault(e => e.Name == view.Name) ?? (dependents.Count == 1 ? dependents[0] : null);
        if (viewer is null)
        {
            // Older files: the viewer is not always reported as a dependent. It carries the view's name, and is
            // normally created just before the view.
            viewer = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Viewers).WhereElementIsNotElementType()
                .FirstOrDefault(e => e.Name == view.Name && !(e is View));
            if (viewer is null && doc.GetElement(new ElementId(view.Id.Value - 1)) is { } prev && prev.Category?.Id == viewersCat && !(prev is View))
                viewer = prev;
        }
        kind = "viewer";
        return viewer ?? throw new InvalidOperationException(
            $"Could not find the marker (viewer element) of '{view.Name}'. Move its section line by hand, or use run_revit_code with the viewer's id.");
    }

    /// <summary>The middle of the section line, on the cut plane (model coordinates).</summary>
    public static XYZ LineMiddle(View view)
    {
        var cb = view.CropBox;
        var p = cb.Transform.OfPoint(new XYZ((cb.Min.X + cb.Max.X) / 2, (cb.Min.Y + cb.Max.Y) / 2, 0));
        return view.Origin + view.RightDirection * (p - view.Origin).DotProduct(view.RightDirection);
    }

    public static bool LooksVertically(View view) => Math.Abs(view.ViewDirection.Z) > 0.9;

    /// <summary>Reads offset / rotation / target_point / target_line into a placement.</summary>
    public static Placement ReadPlacement(Document doc, View view, ToolArgs args, List<string> notes)
    {
        var mid = LineMiddle(view);
        var hasTarget = args.Has("target_line") || args.Has("target_point");
        if (hasTarget && (args.Has("offset") || args.Has("rotation_degrees")))
            throw new ArgumentException("Give either target_line / target_point, or offset / rotation_degrees, not both.");
        if (args.Has("target_line") && args.Has("target_point")) throw new ArgumentException("Give target_line or target_point, not both.");

        if (args.Has("target_point"))
        {
            var t = Modeling.Point(doc, args.Raw["target_point"], mid.Z);
            return new(new XYZ(t.X - mid.X, t.Y - mid.Y, 0), 0, mid);
        }
        if (args.Has("target_line"))
        {
            if (LooksVertically(view))
                throw new ArgumentException("This view looks up or down (a horizontal section); a plan line does not define where it goes. Use offset / rotation_degrees.");
            var line = args.Raw["target_line"];
            if (!line.TryGetProperty("start", out var s) || !line.TryGetProperty("end", out var e))
                throw new ArgumentException("target_line needs start and end.");
            var a = Modeling.Point(doc, s, 0);
            var b = Modeling.Point(doc, e, 0);
            var d = new XYZ(b.X - a.X, b.Y - a.Y, 0);
            if (d.GetLength() < 1e-6) throw new ArgumentException("target_line start and end are the same point.");
            d = d.Normalize();
            var lookNow = new XYZ(-view.ViewDirection.X, -view.ViewDirection.Y, 0).Normalize();
            var lookNew = XYZ.BasisZ.CrossProduct(d); // to the left of start -> end
            var angle = Math.Atan2(lookNow.CrossProduct(lookNew).Z, lookNow.DotProduct(lookNew));
            if (args.OptBool("keep_direction", true) && Math.Abs(angle) > Math.PI / 2 + 1e-9)
            {
                angle -= Math.Sign(angle) * Math.PI;
                notes.Add("keep_direction: the view looks to the RIGHT of start->end, so it keeps its general direction. Pass keep_direction=false to look left.");
            }
            var target = new XYZ((a.X + b.X) / 2, (a.Y + b.Y) / 2, mid.Z);
            return new(target - mid, angle, mid);
        }

        var offset = args.Has("offset") ? Modeling.Point(doc, args.Raw["offset"]) : XYZ.Zero;
        var deg = args.OptDouble("rotation_degrees") ?? 0;
        var center = args.Has("rotation_center") ? Modeling.Point(doc, args.Raw["rotation_center"], mid.Z) : mid;
        return new(offset, deg * Math.PI / 180, center);
    }

    // ---- view-specific detailing -------------------------------------------------------------------------------

    public sealed class Snap
    {
        public required ElementId Id;
        public required string Category;
        public required List<(double X, double Y)> Local;
    }

    /// <summary>The view's own detailing: view-owned elements other than markers, group members and the view's crop.</summary>
    public static List<Element> Detailing(Document doc, View view)
    {
        var viewers = new ElementId(BuiltInCategory.OST_Viewers);
        return new FilteredElementCollector(doc, view.Id).OwnedByView(view.Id).WhereElementIsNotElementType()
            .Where(e => e.Category != null && e.Category.Id != viewers && e is not View && e is not ElevationMarker
                        && e.GroupId == ElementId.InvalidElementId)
            .ToList();
    }

    /// <summary>Points that pin down where an element sits (and which way it faces) in the view.</summary>
    public static List<XYZ> Probe(Element e, View view)
    {
        switch (e)
        {
            case Dimension: return [];
            case TextNote tn: return [tn.Coord, tn.Coord + tn.BaseDirection];
            case IndependentTag tag: return [tag.TagHeadPosition];
        }
        if (e.Location is LocationCurve { Curve: { IsBound: true } c }) return [c.GetEndPoint(0), c.GetEndPoint(1)];
        if (e.Location is LocationPoint lp)
        {
            if (e is FamilyInstance fi && fi.HandOrientation is { } h && !h.IsZeroLength()) return [lp.Point, lp.Point + h];
            return [lp.Point];
        }
        var box = e.get_BoundingBox(view);
        return box is null ? [] : [(box.Min + box.Max) / 2];
    }

    public static List<Snap> Take(Document doc, View view, ViewFrame frame) =>
        Detailing(doc, view).Select(e => new Snap
        {
            Id = e.Id,
            Category = e.Category?.Name ?? "",
            Local = Probe(e, view).Select(frame.Local).ToList(),
        }).ToList();

    /// <summary>
    /// After the view moved: finds the elements whose position in the view changed (they stayed in the model instead
    /// of following) and moves them back to where they were in the view. Call inside the transaction, after a regenerate.
    /// </summary>
    public static object Follow(Document doc, View view, ViewFrame frame, List<Snap> snaps, bool fix)
    {
        int followed = 0;
        var fixedIds = new List<object>();
        var failed = new List<object>();
        var dims = new List<long>();
        var unknown = 0;
        var toCheck = new List<(Snap Snap, Element E)>();
        foreach (var s in snaps)
        {
            var e = doc.GetElement(s.Id);
            if (e is null) continue;
            if (e is Dimension) { dims.Add(s.Id.Value); continue; }
            if (s.Local.Count == 0) { unknown++; continue; }
            var now = Probe(e, view).Select(frame.Local).ToList();
            if (now.Count != s.Local.Count) { unknown++; continue; }
            if (InPlace(now, s.Local)) { followed++; continue; }
            if (!fix) { failed.Add(new { id = s.Id.Value, category = s.Category, reason = "stayed behind (fix_detailing=false)" }); continue; }
            if (doc.IsWorkshared && ItemBatch.Locked(doc, [s.Id]) is { } locked)
            {
                failed.Add(new { id = s.Id.Value, category = s.Category, reason = locked });
                continue;
            }
            try
            {
                frame.Apply(doc, s.Id, Back(now, s.Local));
                toCheck.Add((s, e));
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                failed.Add(new { id = s.Id.Value, category = s.Category, reason = ex.Message });
            }
        }
        if (toCheck.Count > 0)
        {
            doc.Regenerate();
            foreach (var (s, e) in toCheck)
            {
                var now = Probe(e, view).Select(frame.Local).ToList();
                if (InPlace(now, s.Local)) fixedIds.Add(new { id = s.Id.Value, category = s.Category });
                else failed.Add(new { id = s.Id.Value, category = s.Category, reason = $"moved back but still {RevitHelpers.FromInternal(doc, Dist(now[0], s.Local[0]))} off" });
            }
        }
        return new
        {
            followedTheView = followed,
            putBack = fixedIds.Count,
            putBackElements = fixedIds.Take(100).ToList(),
            notFixed = failed.Take(100).ToList(),
            dimensions = dims.Count == 0 ? null : new { count = dims.Count, note = "Dimensions stay on their model references; check them in the new location.", ids = dims.Take(50).ToList() },
            notMeasured = unknown == 0 ? (int?)null : unknown,
        };
    }

    private static double Dist((double X, double Y) a, (double X, double Y) b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>
    /// The turn from <paramref name="now"/>'s direction (first to second probe point) to <paramref name="want"/>'s,
    /// or 0 when either has no usable direction. A direction that lies mostly out of the view plane (an annotation
    /// left facing the old plane of a section turned in plan) projects short and is ignored: it can't be turned
    /// within the view anyway.
    /// </summary>
    private static double Turn(IList<(double X, double Y)> now, IList<(double X, double Y)> want)
    {
        if (now.Count < 2 || want.Count < 2) return 0;
        double ln = Dist(now[0], now[1]), lw = Dist(want[0], want[1]);
        if (lw < 1e-6 || ln < 0.2 * lw) return 0;
        var a = Math.Atan2(now[1].Y - now[0].Y, now[1].X - now[0].X);
        var b = Math.Atan2(want[1].Y - want[0].Y, want[1].X - want[0].X);
        return Rigid2.Normalize(b - a);
    }

    /// <summary>Same place (anchor point within tolerance) and same facing (within 0.05 degrees).</summary>
    private static bool InPlace(IList<(double X, double Y)> now, IList<(double X, double Y)> want) =>
        now.Count > 0 && want.Count > 0 && Dist(now[0], want[0]) < Tolerance && Math.Abs(Turn(now, want)) < 0.05 * Math.PI / 180;

    /// <summary>The in-view motion that puts the anchor point back exactly and restores the facing.</summary>
    private static Rigid2 Back(IList<(double X, double Y)> now, IList<(double X, double Y)> want) =>
        Rigid2.RotationAbout(now[0], Turn(now, want)).Then(new Rigid2(0, want[0].X - now[0].X, want[0].Y - now[0].Y));

    /// <summary>
    /// Moves the view through its marker and checks Revit really moved it. Returns a description of the new location.
    /// </summary>
    public static object Move(Document doc, View view, Placement place, List<string> notes)
    {
        var marker = Marker(doc, view, out var kind);
        var origin = view.Origin;
        var dir = view.ViewDirection;
        if (place.IsIdentity) throw new ArgumentException("Nothing to do: give offset, rotation_degrees, target_point or target_line.");
        if (marker.Pinned) throw new InvalidOperationException($"The {kind} of '{view.Name}' ({marker.Id.Value}) is pinned; unpin it first.");

        if (Math.Abs(place.Angle) > 1e-12)
            ElementTransformUtils.RotateElement(doc, marker.Id, Line.CreateBound(place.Center, place.Center + XYZ.BasisZ), place.Angle);
        if (place.Translation.GetLength() > 1e-9)
            ElementTransformUtils.MoveElement(doc, marker.Id, place.Translation);
        doc.Regenerate();

        var expected = place.Transform.OfPoint(origin);
        var expectedDir = place.Transform.OfVector(dir);
        if (view.Origin.DistanceTo(origin) < 1e-6 && view.ViewDirection.IsAlmostEqualTo(dir) && expected.DistanceTo(origin) > 1e-6)
            throw new InvalidOperationException(
                $"Revit did not move '{view.Name}' when its {kind} ({marker.Id.Value}) was moved. Nothing was changed.");
        var off = view.Origin.DistanceTo(expected);
        if (off > ViewRelocation.Tolerance)
        {
            // The part along the view's up direction (a vertical offset for an ordinary section) is the usual culprit:
            // a section marker lives in plan and Revit drops the Z of the move.
            var along = (expected - view.Origin);
            notes.Add($"The view ended up {RevitHelpers.FromInternal(doc, off)} from where it was asked to go " +
                      $"(off by {RevitHelpers.FromInternal(doc, along.DotProduct(view.RightDirection))} across, " +
                      $"{RevitHelpers.FromInternal(doc, along.DotProduct(view.UpDirection))} up, " +
                      $"{RevitHelpers.FromInternal(doc, along.DotProduct(view.ViewDirection))} in depth). " +
                      "A section's height is set by its crop region, not by moving its marker.");
        }
        if (!view.ViewDirection.IsAlmostEqualTo(expectedDir)) notes.Add("The view direction differs from the requested rotation; check the result.");
        return new
        {
            marker = new { id = marker.Id.Value, kind },
            origin = Coordinates.Out(doc, view.Origin),
            lineMiddle = Coordinates.Out(doc, LineMiddle(view)),
            looking = LooksVertically(view) ? (view.ViewDirection.Z > 0 ? "down" : "up") : ViewKit.Compass(-view.ViewDirection) + "-ish",
            viewDirection = new[] { Math.Round(view.ViewDirection.X, 6), Math.Round(view.ViewDirection.Y, 6), Math.Round(view.ViewDirection.Z, 6) },
            rotatedDegrees = Math.Round(place.Angle * 180 / Math.PI, 6),
        };
    }

    /// <summary>In a workshared model: borrow what the move edits, or say who has it.</summary>
    public static void EnsureEditable(Document doc, IEnumerable<ElementId> ids)
    {
        if (!doc.IsWorkshared) return;
        var list = ids.Where(i => i != ElementId.InvalidElementId).Distinct().ToList();
        if (CentralAccess.Borrow(doc, list) is { } central) throw new InvalidOperationException("Can't move this view: Sketch ProMax " + central);
        if (ItemBatch.Locked(doc, list) is { } why) throw new InvalidOperationException("Can't move this view: " + why);
    }
}

public sealed class DuplicateViewToLocationTool : IRevitTool
{
    public string Name => "duplicate_view_to_location";
    public string Description =>
        "Duplicate a section, elevation or detail view and put the copy somewhere else: by an offset / rotation, onto a " +
        "target point, or onto a plan line. The copy keeps the source view's look direction, depth, crop, scale and " +
        "template; with_detailing (default) also copies its view-specific detailing, and elements that would not " +
        "follow the moved view (generic annotations) are put back in place. Moves the view's marker (viewer), which " +
        "is what Revit needs; move_copy_rotate on a view id does nothing. Returns the new view.";
    public string PropertiesJson => "{" + """
      "view_id": {"type": "integer", "description": "The section / elevation / detail view to duplicate."},
      "mode": {"type": "string", "enum": ["with_detailing", "duplicate"], "description": "with_detailing (default) copies annotation and detailing too; duplicate copies the view only."},
      "name": {"type": "string", "description": "Name for the new view."},
    """ + ViewRelocation.PlacementProperties + "}";
    public string[] Required => ["view_id"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var source = ViewRelocation.SectionView(doc, args.OptLong("view_id") ?? throw new ArgumentException("Missing 'view_id'."));
        var option = args.OptString("mode") == "duplicate" ? ViewDuplicateOption.Duplicate : ViewDuplicateOption.WithDetailing;
        if (!source.CanViewBeDuplicated(option)) return ToolResult.Error($"'{source.Name}' can't be duplicated that way.");
        var notes = new List<string>();
        var place = ViewRelocation.ReadPlacement(doc, source, args, notes);

        // A copied dimension whose references don't show in the moved copy made Revit refuse the whole copy at commit
        // ("One or more dimension references are or have become invalid", five times in one chat): those dimensions are
        // dropped and listed instead.
        var dims = new HashSet<ElementId>();
        var dropped = new HashSet<ElementId>();
        var failures = new List<string>();
        using var t = new Transaction(doc, "Sketch ProMax: Duplicate view to location");
        var handling = t.GetFailureHandlingOptions();
        handling.SetFailuresPreprocessor(new CopyViewDetailingTool.DropFailedCopies(dims, dropped, failures));
        handling.SetForcedModalHandling(false);
        t.SetFailureHandlingOptions(handling);
        t.Start();
        var copy = (View)doc.GetElement(source.Duplicate(option));
        doc.Regenerate();
        if (copy.ViewDirection.DotProduct(source.ViewDirection) < 0.999)
            notes.Add("Revit flipped the copy's direction on duplication; check it.");
        var frame = ViewFrame.Of(copy);
        var snaps = option == ViewDuplicateOption.WithDetailing ? ViewRelocation.Take(doc, copy, frame) : new List<ViewRelocation.Snap>();
        var where = ViewRelocation.Move(doc, copy, place, notes);
        var detailing = snaps.Count > 0 ? ViewRelocation.Follow(doc, copy, ViewFrame.Of(copy), snaps, args.OptBool("fix_detailing", true)) : null;
        var name = ViewKit.Finish(doc, copy, args.OptString("name"), null, null);
        foreach (var id in new FilteredElementCollector(doc, copy.Id).OfClass(typeof(Dimension)).ToElementIds()) dims.Add(id);
        if (t.Commit() != TransactionStatus.Committed)
            return ToolResult.Error("Revit did not accept the copy, so nothing was changed: " + (failures.Count > 0 ? string.Join("; ", failures) : "no reason given"));
        if (dropped.Count > 0)
            notes.Add($"{dropped.Count} copied dimension(s) lost their references in the new place and were left out (Revit: {string.Join("; ", failures)}). Add them again with add_dimensions.");
        return ToolResult.Ok(new
        {
            id = copy.Id.Value,
            name,
            source = new { id = source.Id.Value, name = source.Name },
            location = where,
            detailing,
            notes = notes.Count == 0 ? null : notes,
        });
    }
}

public sealed class RelocateViewWithDetailingTool : IRevitTool
{
    public string Name => "relocate_view_with_detailing";
    public string Description =>
        "Move an existing section, elevation or detail view (through its marker) by an offset / rotation, onto a " +
        "target point or onto a plan line, and keep ALL its view-specific detailing in place on the drawing: detail " +
        "lines and items follow the marker by themselves, while generic annotations, text and tags that stay behind " +
        "in the model are measured in the view's own frame and moved back to where they were on the drawing. Reports " +
        "what followed, what was put back, and what could not be (dimensions stay on their references).";
    public string PropertiesJson => "{" + """
      "view_id": {"type": "integer", "description": "The section / elevation / detail view to move."},
    """ + ViewRelocation.PlacementProperties + "}";
    public string[] Required => ["view_id"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var view = ViewRelocation.SectionView(doc, args.OptLong("view_id") ?? throw new ArgumentException("Missing 'view_id'."));
        var notes = new List<string>();
        var place = ViewRelocation.ReadPlacement(doc, view, args, notes);
        var marker = ViewRelocation.Marker(doc, view, out _);
        ViewRelocation.EnsureEditable(doc, [view.Id, marker.Id]);
        if (view.GetDependentViewIds().Count > 0)
            notes.Add($"Its {view.GetDependentViewIds().Count} dependent view(s) move with it; their own detailing belongs to this view and is handled here.");

        using var t = RevitHelpers.StartTransaction(doc, "Relocate view with detailing");
        var snaps = ViewRelocation.Take(doc, view, ViewFrame.Of(view));
        var where = ViewRelocation.Move(doc, view, place, notes);
        var detailing = ViewRelocation.Follow(doc, view, ViewFrame.Of(view), snaps, args.OptBool("fix_detailing", true));
        t.Commit();
        return ToolResult.Ok(new
        {
            id = view.Id.Value,
            name = view.Name,
            location = where,
            detailing,
            notes = notes.Count == 0 ? null : notes,
        });
    }
}

public sealed class CopyViewDetailingTool : IRevitTool
{
    public string Name => "copy_view_detailing";
    public string Description =>
        "Copy view-specific detailing (detail lines and items, filled regions, generic annotations, text, tags, " +
        "dimensions) from one view to another, placed by the two views' own drawing frames so the copy is not turned " +
        "when the views face different ways (e.g. sections along a skewed quay). align: 'view_frame' (default: same " +
        "position relative to each view's origin, right and up), 'model' (same model position; parallel views only), " +
        "or 'points' (best fit from paired points, e.g. the same pile corner picked in both views; reports the fit " +
        "error). Extra offset / rotation in the target view. Revit's own view-to-view mapping is measured with a " +
        "throw-away probe and corrected, then the result is checked.";
    public string PropertiesJson => """
    {
      "source_view_id": {"type": "integer"},
      "target_view_id": {"type": "integer"},
      "element_ids": {"type": "array", "items": {"type": "integer"}, "description": "What to copy (view-specific elements of the source view). Default: all its detailing."},
      "categories": {"type": "array", "items": {"type": "string"}, "description": "With no element_ids: only these categories, e.g. ['Generic Annotations', 'Detail Items']."},
      "include_dimensions": {"type": "boolean", "description": "Default true. Dimensions whose references are not in the target view are dropped and listed."},
      "align": {"type": "string", "enum": ["view_frame", "model", "points"]},
      "source_points": {"type": "array", "items": {"type": "array", "items": {"type": "number"}}, "description": "align=points: [x, y] in the source view (along its right / up from its origin, project units; plans: model x, y) or [x, y, z] model points."},
      "target_points": {"type": "array", "items": {"type": "array", "items": {"type": "number"}}, "description": "align=points: the matching points in the target view, same forms."},
      "allow_rotation": {"type": "boolean", "description": "align=points: fit a rotation as well as a shift (default true; needs 2+ pairs)."},
      "offset": {"type": "array", "items": {"type": "number"}, "description": "Extra [dx, dy] in the target view (along its right / up), project units."},
      "rotation_degrees": {"type": "number", "description": "Extra rotation in the target view, counter-clockwise as seen, about the copied detailing's centre."}
    }
    """;
    public string[] Required => ["source_view_id", "target_view_id"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var src = doc.GetElement(new ElementId(args.OptLong("source_view_id") ?? throw new ArgumentException("Missing 'source_view_id'."))) as View
                  ?? throw new ArgumentException("source_view_id is not a view.");
        var dst = doc.GetElement(new ElementId(args.OptLong("target_view_id") ?? throw new ArgumentException("Missing 'target_view_id'."))) as View
                  ?? throw new ArgumentException("target_view_id is not a view.");
        if (src.IsTemplate || dst.IsTemplate) throw new ArgumentException("View templates hold no detailing.");
        var notes = new List<string>();
        if (dst.GetPrimaryViewId() != ElementId.InvalidElementId)
            notes.Add($"'{dst.Name}' is a dependent view: what is copied belongs to its primary view and shows in every dependent view whose crop covers it.");

        // What to copy.
        List<ElementId> ids;
        if (args.LongList("element_ids") is { Count: > 0 } given)
        {
            ids = given.Select(i => new ElementId(i)).ToList();
            var foreign = ids.Where(i => doc.GetElement(i) is not { } e || e.OwnerViewId != src.Id).Select(i => i.Value).ToList();
            if (foreign.Count > 0)
                throw new ArgumentException($"These are not view-specific elements of '{src.Name}': {string.Join(", ", foreign.Take(20))}.");
        }
        else
        {
            var cats = args.StringList("categories");
            ids = ViewRelocation.Detailing(doc, src)
                .Where(e => cats.Count == 0 || cats.Any(c => string.Equals(c, e.Category.Name, StringComparison.OrdinalIgnoreCase)
                                                            || string.Equals(c, ((BuiltInCategory)e.Category.Id.Value).ToString(), StringComparison.OrdinalIgnoreCase)))
                .Where(e => args.OptBool("include_dimensions", true) || e is not Dimension)
                .Select(e => e.Id).ToList();
        }
        if (ids.Count == 0) return ToolResult.Error($"Nothing to copy: '{src.Name}' has no matching view-specific elements.");

        var fs = ViewFrame.Of(src);
        var ft = ViewFrame.Of(dst);
        var desired = Desired(doc, args, src, fs, ft, notes, out var fit);

        // Extra rotation / offset, in the target frame, about the centre of what is copied.
        var box = ViewKit.Extents(doc, ids.Select(i => doc.GetElement(i)).Where(e => e != null), src);
        var centre = box is null ? (0.0, 0.0) : desired.Apply(fs.Local((box.Min + box.Max) / 2));
        if (args.OptDouble("rotation_degrees") is { } deg && deg != 0)
            desired = desired.Then(Rigid2.RotationAbout(centre, deg * Math.PI / 180));
        if (args.Has("offset"))
        {
            var o = args.Raw["offset"];
            desired = desired.Then(new Rigid2(0, RevitHelpers.ToInternal(doc, o[0].GetDouble()), o.GetArrayLength() > 1 ? RevitHelpers.ToInternal(doc, o[1].GetDouble()) : 0));
        }

        var removed = new HashSet<ElementId>();
        var failures = new List<string>();
        var copies = new HashSet<ElementId>();
        using var t = new Transaction(doc, "Sketch ProMax: Copy view detailing");
        var handling = t.GetFailureHandlingOptions();
        handling.SetFailuresPreprocessor(new DropFailedCopies(copies, removed, failures));
        handling.SetForcedModalHandling(false);
        t.SetFailureHandlingOptions(handling);
        t.Start();

        // 1. Measure how Revit maps the source frame onto the target (it differs by view type and orientation).
        var meter = UnitUtils.ConvertToInternalUnits(1, UnitTypeId.Meters);
        (double X, double Y) s0 = (0, 0), s1 = (meter, 0), s2 = (0, meter);
        Probe(doc, src, dst, fs, ft, null, s0, s1, s2, out var a0, out var a1, out var a2);
        var mapped = Rigid2.Fit([s0, s1, s2], [a0, a1, a2]);
        var crossS = (s1.X - s0.X) * (s2.Y - s0.Y) - (s1.Y - s0.Y) * (s2.X - s0.X);
        var crossA = (a1.X - a0.X) * (a2.Y - a0.Y) - (a1.Y - a0.Y) * (a2.X - a0.X);
        if (crossS * crossA < 0 || mapped.Rms([s0, s1, s2], [a0, a1, a2]) > ViewRelocation.Tolerance)
        {
            t.RollBack();
            return ToolResult.Error("Revit maps these two views onto each other with a mirror or a scale, which a rigid correction can't undo. Copy with run_revit_code or by hand.");
        }

        // 2. The correction that turns Revit's mapping into the wanted one, in the target frame.
        var correction = mapped.Inverse().Then(desired);
        string method;
        Transform? extra = null;
        bool moveAfter = false;
        if (correction.IsIdentity(ViewRelocation.Tolerance / 10, 1e-7)) method = "revit_mapping_already_matches";
        else
        {
            extra = ft.ToTransform(correction);
            // Revit applies additionalTransform "in addition to" its own mapping; check it composes the way we assume.
            Probe(doc, src, dst, fs, ft, extra, s0, s1, s2, out var b0, out var b1, out var b2);
            var wanted = new[] { desired.Apply(s0), desired.Apply(s1), desired.Apply(s2) };
            var err = Rigid2.Identity.Rms([b0, b1, b2], wanted);
            if (err < ViewRelocation.Tolerance) method = "copy_with_transform";
            else { method = "copy_then_move"; moveAfter = true; }
        }

        // 3. The real copy.
        ICollection<ElementId> made;
        try
        {
            var options = new CopyPasteOptions();
            made = ElementTransformUtils.CopyElements(src, ids, dst, moveAfter ? null : extra, options);
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException ex)
        {
            t.RollBack();
            return ToolResult.Error($"Revit refused the copy: {ex.Message}");
        }
        foreach (var id in made) copies.Add(id);
        if (moveAfter && made.Count > 0)
        {
            var axis = Line.CreateBound(ft.Origin, ft.Origin + ft.Normal);
            var top = made.Where(i => doc.GetElement(i) is { } e && e.GroupId == ElementId.InvalidElementId).ToList();
            if (Math.Abs(correction.Angle) > 1e-9) ElementTransformUtils.RotateElements(doc, top, axis, correction.Angle);
            var v = ft.Vector(correction.Tx, correction.Ty);
            if (!v.IsZeroLength()) ElementTransformUtils.MoveElements(doc, top, v);
        }
        doc.Regenerate();

        // 4. Check: the copied detailing's centre against where it should be.
        double? centreOff = null;
        if (box != null)
        {
            var got = ViewKit.Extents(doc, made.Select(i => doc.GetElement(i)).Where(e => e != null), dst);
            if (got != null)
            {
                var c = ft.Local((got.Min + got.Max) / 2);
                var want = desired.Apply(fs.Local((box.Min + box.Max) / 2));
                centreOff = RevitHelpers.FromInternal(doc, Math.Sqrt((c.X - want.X) * (c.X - want.X) + (c.Y - want.Y) * (c.Y - want.Y)));
                if (Math.Abs(desired.Angle) > 1e-6)
                    notes.Add("With a rotation the bounding-box centre check is approximate.");
            }
        }

        var status = t.Commit();
        if (status != TransactionStatus.Committed)
            return ToolResult.Error("Revit rolled the copy back: " + (failures.Count > 0 ? string.Join("; ", failures) : status.ToString()));
        var kept = made.Where(i => !removed.Contains(i) && doc.GetElement(i) != null).Select(i => i.Value).ToList();
        return ToolResult.Ok(new
        {
            copied = kept.Count,
            ids = kept.Take(300).ToList(),
            idsTruncated = kept.Count > 300 ? true : (bool?)null,
            droppedByRevit = removed.Count == 0 ? null : new { count = removed.Count, why = failures.Take(20).ToList() },
            method,
            placement = new
            {
                align = args.OptString("align") ?? "view_frame",
                rotationInTargetDegrees = Math.Round(desired.Angle * 180 / Math.PI, 4),
                revitWouldHaveRotatedDegrees = Math.Round(mapped.Angle * 180 / Math.PI, 4),
                correctionDegrees = Math.Round(correction.Angle * 180 / Math.PI, 4),
                correctionShift = new[] { RevitHelpers.FromInternal(doc, correction.Tx), RevitHelpers.FromInternal(doc, correction.Ty) },
                pointFit = fit,
            },
            centreCheckOffset = centreOff,
            notes = notes.Count == 0 ? null : notes,
        });
    }

    /// <summary>The wanted map from source view coordinates to target view coordinates.</summary>
    private static Rigid2 Desired(Document doc, ToolArgs args, View src, ViewFrame fs, ViewFrame ft, List<string> notes, out object? fit)
    {
        fit = null;
        switch (args.OptString("align") ?? "view_frame")
        {
            case "view_frame":
                if (Math.Abs(fs.Normal.DotProduct(ft.Normal)) < 0.999 && src is not ViewPlan)
                    notes.Add("The views face different ways; detailing is placed at the same position relative to each view's origin, right and up.");
                return Rigid2.Identity;
            case "model":
            {
                if (Math.Abs(fs.Normal.DotProduct(ft.Normal)) < 0.9999)
                    throw new ArgumentException("align=model needs parallel views (same or opposite facing). Use view_frame or points.");
                (double, double)[] from = [(0, 0), (1, 0), (0, 1)];
                var to = from.Select(p => ft.Local(fs.Model(p.Item1, p.Item2))).ToList();
                var r = Rigid2.Fit(from, to);
                if (r.Rms(from, to) > 1e-6)
                    throw new ArgumentException("The views face opposite ways, so model positions appear mirrored; use view_frame or points.");
                return r;
            }
            case "points":
            {
                var sp = args.Array("source_points").Select(p => Local(doc, fs, p)).ToList();
                var tp = args.Array("target_points").Select(p => Local(doc, ft, p)).ToList();
                if (sp.Count == 0 || sp.Count != tp.Count) throw new ArgumentException("align=points needs source_points and target_points in matching pairs.");
                var rotate = args.OptBool("allow_rotation", true) && sp.Count > 1;
                var r = Rigid2.Fit(sp, tp, rotate);
                var rms = r.Rms(sp, tp);
                var worst = sp.Select((p, i) => { var q = r.Apply(p); return Math.Sqrt((q.X - tp[i].X) * (q.X - tp[i].X) + (q.Y - tp[i].Y) * (q.Y - tp[i].Y)); }).Max();
                fit = new { pairs = sp.Count, rotated = rotate, rms = RevitHelpers.FromInternal(doc, rms), worst = RevitHelpers.FromInternal(doc, worst) };
                if (worst > 10 * ViewRelocation.Tolerance) notes.Add("The paired points do not fit a rigid move well (see pointFit.worst); check the pairs.");
                return r;
            }
            default: throw new ArgumentException("align must be view_frame, model or points.");
        }
    }

    private static (double X, double Y) Local(Document doc, ViewFrame frame, JsonElement p) =>
        p.GetArrayLength() >= 3
            ? frame.Local(Modeling.Point(doc, p))
            : (RevitHelpers.ToInternal(doc, p[0].GetDouble()), RevitHelpers.ToInternal(doc, p[1].GetDouble())); // plans: model x, y; others: along right / up from the origin

    /// <summary>
    /// Copies two throw-away detail lines (along the source frame's x and y from a common point) into the target and
    /// reads where they land, in target coordinates. Everything it creates is deleted again.
    /// </summary>
    private static List<ElementId> Probe(Document doc, View src, View dst, ViewFrame fs, ViewFrame ft, Transform? extra,
        (double X, double Y) s0, (double X, double Y) s1, (double X, double Y) s2,
        out (double X, double Y) a0, out (double X, double Y) a1, out (double X, double Y) a2)
    {
        var made = new List<ElementId>();
        try
        {
            var lx = doc.Create.NewDetailCurve(src, Line.CreateBound(fs.Model(s0.X, s0.Y), fs.Model(s1.X, s1.Y)));
            var ly = doc.Create.NewDetailCurve(src, Line.CreateBound(fs.Model(s0.X, s0.Y), fs.Model(s2.X, s2.Y)));
            made.Add(lx.Id);
            made.Add(ly.Id);
            var cx = ElementTransformUtils.CopyElements(src, [lx.Id], dst, extra, new CopyPasteOptions()).Single();
            var cy = ElementTransformUtils.CopyElements(src, [ly.Id], dst, extra, new CopyPasteOptions()).Single();
            made.Add(cx);
            made.Add(cy);
            doc.Regenerate();
            var gx = ((DetailCurve)doc.GetElement(cx)).GeometryCurve;
            var gy = ((DetailCurve)doc.GetElement(cy)).GeometryCurve;
            // The copies of the shared start point coincide; the far ends tell x and y apart.
            a0 = ft.Local(gx.GetEndPoint(0));
            a1 = ft.Local(gx.GetEndPoint(1));
            a2 = ft.Local(gy.GetEndPoint(1));
            return made;
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or InvalidCastException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"Could not measure how Revit maps '{src.Name}' onto '{dst.Name}' ({ex.Message}). Both views must accept detail lines.");
        }
        finally
        {
            var left = made.Where(i => doc.GetElement(i) != null).ToList();
            if (left.Count > 0) doc.Delete(left);
        }
    }

    /// <summary>Warnings are dismissed; errors caused only by copied elements (e.g. a dimension whose references are not
    /// in the target view) delete those copies instead of rolling the whole copy back.</summary>
    internal sealed class DropFailedCopies(HashSet<ElementId> copies, HashSet<ElementId> removed, List<string> messages) : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            var resolved = false;
            foreach (var f in accessor.GetFailureMessages())
            {
                var text = f.GetDescriptionText();
                if (f.GetSeverity() == FailureSeverity.Warning) { accessor.DeleteWarning(f); continue; }
                var failing = f.GetFailingElementIds();
                if (failing.Count > 0 && failing.All(copies.Contains) && f.HasResolutionOfType(FailureResolutionType.DeleteElements))
                {
                    f.SetCurrentResolutionType(FailureResolutionType.DeleteElements);
                    accessor.ResolveFailure(f);
                    foreach (var id in failing) removed.Add(id);
                    if (!messages.Contains(text)) messages.Add(text);
                    resolved = true;
                }
                else if (!messages.Contains(text)) messages.Add(text);
            }
            return resolved ? FailureProcessingResult.ProceedWithCommit : FailureProcessingResult.Continue;
        }
    }
}
