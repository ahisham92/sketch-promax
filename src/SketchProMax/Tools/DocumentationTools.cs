using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

public sealed class TagElementsInViewTool : IRevitTool
{
    public string Name => "tag_elements_in_view";
    public string Description =>
        "Tag every untagged element of a category (Doors, Windows, Rooms, Walls, Furniture, Structural Columns, Floors, ...) in a plan view. " +
        "Uses the category's default tag type; elements that already have a tag in that view are skipped. " +
        "Columns get one tag per stack (the column seen from below and the one above share a location: the one cut by the view is tagged), " +
        "with the tag head outside the column, clear of other columns and tags. Floors, foundations, roofs and ceilings are tagged at a point " +
        "well inside each element's own outline, moved apart where tags would overlap.";
    public string PropertiesJson => DependentGuard.AddTo("""
    {
      "category": {"type": "string"},
      "view_id": {"type": "integer", "description": "Defaults to the active view."},
      "leader": {"type": "boolean", "description": "Draw a leader from the tag to its element (default false)."}
    }
    """);
    public string[] Required => ["category"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var view = ViewKit.ViewOrActive(doc, args.OptLong("view_id"));
        var bic = RevitHelpers.ParseCategory(doc, args.String("category"));
        var guard = DependentGuard.For(doc, view, args.OptString("dependent_view"));
        var target = guard.Target;

        // Everything already tagged in this view: element tags (IndependentTag) and spatial tags (room, space and
        // area tags are SpatialElementTag, not IndependentTag). Tag categories are collected too, in case a tag
        // class is not reachable through the class filter.
        var taggedInView = new HashSet<long>();
        var tagElements = new Dictionary<long, Element>();
        foreach (var e in new FilteredElementCollector(doc, view.Id)
                     .WherePasses(new ElementMulticlassFilter([typeof(IndependentTag), typeof(SpatialElementTag)])))
            tagElements[e.Id.Value] = e;
        foreach (var tagCat in new[] { BuiltInCategory.OST_RoomTags, BuiltInCategory.OST_MEPSpaceTags, BuiltInCategory.OST_AreaTags })
            foreach (var e in new FilteredElementCollector(doc, view.Id).OfCategory(tagCat).WhereElementIsNotElementType())
                tagElements[e.Id.Value] = e;
        foreach (var tagEl in tagElements.Values)
        {
            try
            {
                switch (tagEl)
                {
                    case IndependentTag it:
                        foreach (var id in it.GetTaggedLocalElementIds()) taggedInView.Add(id.Value);
                        break;
                    case RoomTag rt:
                        // TaggedLocalRoomId is set even when Room resolves to null (e.g. phase differences).
                        if (rt.TaggedLocalRoomId != ElementId.InvalidElementId) taggedInView.Add(rt.TaggedLocalRoomId.Value);
                        else if (rt.Room != null) taggedInView.Add(rt.Room.Id.Value);
                        break;
                    case Autodesk.Revit.DB.Mechanical.SpaceTag st:
                        if (st.Space != null) taggedInView.Add(st.Space.Id.Value);
                        break;
                    case AreaTag at:
                        if (at.Area != null) taggedInView.Add(at.Area.Id.Value);
                        break;
                }
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { /* orphaned tag: nothing to record */ }
        }

        var inCategory = new FilteredElementCollector(doc, view.Id).OfCategory(bic).WhereElementIsNotElementType().ToList();
        var alreadyTagged = inCategory.Where(e => taggedInView.Contains(e.Id.Value)).Select(e => e.Id.Value).ToList();

        var targets = inCategory.Where(e => !taggedInView.Contains(e.Id.Value)).ToList();

        // Columns: one tag per stack. A plan shows the column it cuts and, through its view depth, the one below; both
        // stand at the same spot, so only the one the view cuts (else the one on the view's level) is tagged.
        var isColumn = bic is BuiltInCategory.OST_StructuralColumns or BuiltInCategory.OST_Columns;
        var isSlab = bic is BuiltInCategory.OST_Floors or BuiltInCategory.OST_StructuralFoundation or BuiltInCategory.OST_Roofs or BuiltInCategory.OST_Ceilings;
        var sameStack = 0;
        if (isColumn && view is ViewPlan plan)
        {
            var cut = CutElevation(doc, plan);
            var located = inCategory.Select(e => (e, p: Center(e, view))).Where(x => x.p is not null).ToList();
            var stacks = LayoutMath.Stacks(located.Select(x => (x.p!.X, x.p!.Y)).ToList(), 50 / 304.8);
            var keep = new HashSet<long>();
            foreach (var stack in stacks)
            {
                var members = stack.Select(i => located[i].e).ToList();
                if (members.Any(m => taggedInView.Contains(m.Id.Value))) { sameStack += members.Count(m => !taggedInView.Contains(m.Id.Value)); continue; }
                var pick = members.FirstOrDefault(m => m.get_BoundingBox(null) is { } b && b.Min.Z <= cut + 1e-3 && b.Max.Z >= cut - 1e-3)
                           ?? members.FirstOrDefault(m => m.LevelId == plan.GenLevel?.Id)
                           ?? members.OrderBy(m => Math.Abs((m.get_BoundingBox(null)?.Min.Z ?? 0) - (plan.GenLevel?.ProjectElevation ?? 0))).First();
                keep.Add(pick.Id.Value);
                sameStack += members.Count - 1;
            }
            targets = targets.Where(e => keep.Contains(e.Id.Value) || Center(e, view) is null).ToList();
        }
        var gap = 2.5 / 304.8 * Math.Max(1, view.Scale);
        var placedTags = new List<(IndependentTag tag, Element host, List<XYZ> spots)>();

        int tagged = 0;
        var reasons = new Dictionary<string, int>();
        void Skip(string why) => reasons[why] = reasons.TryGetValue(why, out var n) ? n + 1 : 1;
        using var t = RevitHelpers.StartTransaction(doc, $"Tag {bic.ToString().Replace("OST_", "")}");
        foreach (var e in targets)
        {
            try
            {
                if (e is Room room)
                {
                    if (room.Location is not LocationPoint lp) { Skip("room is not placed"); continue; }
                    if (room.Area <= 0) { Skip("room is not enclosed or is redundant (zero area)"); continue; }
                    if (!guard.Keep(lp.Point)) { Skip("outside this dependent view's crop"); continue; }
                    doc.Create.NewRoomTag(new LinkElementId(room.Id), new UV(lp.Point.X, lp.Point.Y), target.Id);
                }
                else
                {
                    var center = Center(e, view);
                    if (center is null) { Skip("no location to put the tag"); continue; }
                    if (!guard.Keep(center)) { Skip("outside this dependent view's crop"); continue; }
                    // Slabs: points well inside the element's own outline (its bounding box centre can be off the slab, or shared).
                    var spots = isSlab ? InsidePoints(e, center.Z) : [];
                    var at = spots.Count > 0 ? spots[0] : center;
                    var tag = IndependentTag.Create(doc, target.Id, new Reference(e), args.OptBool("leader"),
                        TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal, at);
                    if (isColumn || isSlab) placedTags.Add((tag, e, spots));
                }
                tagged++;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex) { Skip(ex.Message.Trim()); }
        }
        var skipped = reasons.Values.Sum();
        if (tagged == 0 && skipped > 0)
        {
            t.RollBack();
            var main = reasons.OrderByDescending(r => r.Value).First().Key;
            var hint = main.IndexOf("tag", StringComparison.OrdinalIgnoreCase) >= 0 && main.IndexOf("load", StringComparison.OrdinalIgnoreCase) >= 0
                ? " Load a tag family for this category (load_family, e.g. search 'wall tag'), then try again."
                : "";
            return ToolResult.Error($"Tagged nothing in '{view.Name}': {skipped} element(s) could not be tagged. Revit said: {main}.{hint}");
        }
        var moved = placedTags.Count > 0 ? Spread(doc, view, placedTags, tagElements.Values, inCategory, isColumn, gap) : 0;
        t.Commit();
        return ToolResult.Ok(new
        {
            view = view.Name, tagged, skipped, alreadyTagged = alreadyTagged.Count,
            sameColumnStack = sameStack > 0 ? sameStack : (int?)null,
            tagsMovedApart = moved > 0 ? moved : (int?)null,
            skippedBecause = reasons.Select(r => $"{r.Value}: {r.Key}"),
            dependentView = guard.Report(),
            note = targets.Count == 0 ? $"No untagged {args.String("category")} are visible in this view." : null,
        });
    }

    /// <summary>
    /// Settles the new column and slab tags: reads each tag's real size, then puts column tags just outside their
    /// column (clear of other columns and tags) and moves slab tags to the next point inside their slab when they
    /// would overlap a tag already placed. Returns how many tags were moved from where they were created.
    /// </summary>
    private static int Spread(Document doc, View view, List<(IndependentTag tag, Element host, List<XYZ> spots)> placed,
        IEnumerable<Element> existingTags, List<Element> hosts, bool columns, double gap)
    {
        doc.Regenerate();
        static LayoutMath.Rect R(BoundingBoxXYZ b) => new(b.Min.X, b.Min.Y, b.Max.X, b.Max.Y);
        var obstacles = new List<LayoutMath.Rect>();
        foreach (var e in existingTags)
            if (e.IsValidObject && e.get_BoundingBox(view) is { } b) obstacles.Add(R(b));
        if (columns)
            foreach (var h in hosts)
                if (h.get_BoundingBox(null) is { } b) obstacles.Add(R(b));
        var moved = 0;
        foreach (var (tag, host, spots) in placed)
        {
            if (tag.get_BoundingBox(view) is not { } tb) continue;
            var box = R(tb);
            var head = tag.TagHeadPosition;
            double offX = head.X - box.Cx, offY = head.Y - box.Cy;
            (double x, double y) centre;
            if (columns && host.get_BoundingBox(null) is { } hb)
                centre = LayoutMath.TagBeside(R(hb), box.W, box.H, gap, obstacles);
            else
            {
                centre = (box.Cx, box.Cy);
                foreach (var p in spots.Take(40))
                {
                    var c = (p.X - offX, p.Y - offY);
                    var cand = new LayoutMath.Rect(c.Item1 - box.W / 2, c.Item2 - box.H / 2, c.Item1 + box.W / 2, c.Item2 + box.H / 2);
                    if (!obstacles.Any(o => o.Overlaps(cand))) { centre = c; break; }
                }
            }
            var final = new LayoutMath.Rect(centre.x - box.W / 2, centre.y - box.H / 2, centre.x + box.W / 2, centre.y + box.H / 2);
            obstacles.Add(final);
            if (Math.Abs(centre.x - box.Cx) < 1e-6 && Math.Abs(centre.y - box.Cy) < 1e-6) continue;
            try
            {
                tag.TagHeadPosition = new XYZ(centre.x + offX, centre.y + offY, head.Z);
                moved++;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { }
        }
        return moved;
    }

    /// <summary>The height the plan cuts at: its cut plane level plus offset (the level plus 1.2 m when unreadable).</summary>
    private static double CutElevation(Document doc, ViewPlan plan)
    {
        var level = plan.GenLevel?.ProjectElevation ?? 0;
        try
        {
            var range = plan.GetViewRange();
            var lv = doc.GetElement(range.GetLevelId(PlanViewPlane.CutPlane)) as Level;
            return (lv?.ProjectElevation ?? level) + range.GetOffset(PlanViewPlane.CutPlane);
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { return level + 1200 / 304.8; }
    }

    /// <summary>Points well inside a floor-like element's top outline, best first (empty when it has no top face).</summary>
    private static List<XYZ> InsidePoints(Element e, double z)
    {
        if (e is not HostObject host) return [];
        try
        {
            Face? top = null;
            foreach (var r in HostObjectUtils.GetTopFaces(host))
                if (e.GetGeometryObjectFromReference(r) is Face f && (top is null || f.Area > top.Area)) top = f;
            if (top is null) return [];
            var loops = top.GetEdgesAsCurveLoops()
                .Select(l => l.SelectMany(c => c.Tessellate().Take(Math.Max(1, c.Tessellate().Count - 1))).Select(p => (p.X, p.Y)).ToList())
                .Where(l => l.Count >= 3).OrderByDescending(l => Math.Abs(SignedArea(l))).ToList();
            if (loops.Count == 0) return [];
            var holes = loops.Skip(1).Cast<IReadOnlyList<(double x, double y)>>().ToList();
            return LayoutMath.InteriorPoints(loops[0], holes).Select(p => new XYZ(p.x, p.y, z)).ToList();
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { return []; }
    }

    private static double SignedArea(List<(double X, double Y)> ring)
    {
        double a = 0;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++) a += (ring[j].X + ring[i].X) * (ring[j].Y - ring[i].Y);
        return a / 2;
    }

    private static XYZ? Center(Element e, View view)
    {
        if (e.Location is LocationPoint lp) return lp.Point;
        if (e.Location is LocationCurve lc) return lc.Curve.Evaluate(0.5, true);
        var bb = e.get_BoundingBox(view);
        return bb is null ? null : (bb.Min + bb.Max) / 2;
    }
}

public sealed class CreateScheduleTool : IRevitTool
{
    public string Name => "create_schedule";
    public string Description =>
        "Create a schedule for a category with the given fields (parameter names, in order), optionally sorted " +
        "by the first field. Unknown field names are reported back.";
    public string PropertiesJson => """
    {
      "category": {"type": "string"},
      "name": {"type": "string"},
      "fields": {"type": "array", "items": {"type": "string"}},
      "sort_by_first_field": {"type": "boolean"}
    }
    """;
    public string[] Required => ["category", "name", "fields"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var bic = RevitHelpers.ParseCategory(doc, args.String("category"));

        using var t = RevitHelpers.StartTransaction(doc, $"Create schedule {args.String("name")}");
        var schedule = ViewSchedule.CreateSchedule(doc, new ElementId(bic));
        var name = ViewKit.Finish(doc, schedule, args.String("name"), null, null); // made unique if the name is taken

        var def = schedule.Definition;
        var schedulable = def.GetSchedulableFields();
        var missing = new List<string>();
        ScheduleField? first = null;
        foreach (var fieldName in args.StringList("fields"))
        {
            var sf = schedulable.FirstOrDefault(f => string.Equals(f.GetName(doc), fieldName, StringComparison.OrdinalIgnoreCase));
            if (sf is null) { missing.Add(fieldName); continue; }
            var added = def.AddField(sf);
            first ??= added;
        }
        if (first != null && args.OptBool("sort_by_first_field", true))
            def.AddSortGroupField(new ScheduleSortGroupField(first.FieldId));
        t.Commit();

        return ToolResult.Ok(new
        {
            scheduleId = schedule.Id.Value,
            name,
            missingFields = missing,
            availableFieldsHint = missing.Count > 0 ? schedulable.Select(f => f.GetName(doc)).Take(80) : null,
        });
    }
}
