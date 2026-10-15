using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

public sealed class TagElementsInViewTool : IRevitTool
{
    public string Name => "tag_elements_in_view";
    public string Description =>
        "Tag every untagged element of a category (Doors, Windows, Rooms, Walls, Furniture, ...) in a plan view. " +
        "Uses the category's default tag type; elements that already have a tag in that view are skipped.";
    public string PropertiesJson => DependentGuard.AddTo("""
    {
      "category": {"type": "string"},
      "view_id": {"type": "integer", "description": "Defaults to the active view."},
      "leader": {"type": "boolean"}
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
                    IndependentTag.Create(doc, target.Id, new Reference(e), args.OptBool("leader"),
                        TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal, center);
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
        t.Commit();
        return ToolResult.Ok(new
        {
            view = view.Name, tagged, skipped, alreadyTagged = alreadyTagged.Count,
            skippedBecause = reasons.Select(r => $"{r.Value}: {r.Key}"),
            dependentView = guard.Report(),
            note = targets.Count == 0 ? $"No untagged {args.String("category")} are visible in this view." : null,
        });
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
