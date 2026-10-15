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
    public string PropertiesJson => """
    {
      "category": {"type": "string"},
      "view_id": {"type": "integer", "description": "Defaults to the active view."},
      "leader": {"type": "boolean"}
    }
    """;
    public string[] Required => ["category"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var view = ViewKit.ViewOrActive(doc, args.OptLong("view_id"));
        var bic = RevitHelpers.ParseCategory(doc, args.String("category"));

        var alreadyTagged = new HashSet<long>();
        foreach (var tag in new FilteredElementCollector(doc, view.Id).OfClass(typeof(IndependentTag)).Cast<IndependentTag>())
            foreach (var id in tag.GetTaggedLocalElementIds()) alreadyTagged.Add(id.Value);
        if (bic == BuiltInCategory.OST_Rooms)
            foreach (var rt in new FilteredElementCollector(doc, view.Id).OfCategory(BuiltInCategory.OST_RoomTags).Cast<RoomTag>())
                if (rt.Room != null) alreadyTagged.Add(rt.Room.Id.Value);

        var targets = new FilteredElementCollector(doc, view.Id).OfCategory(bic).WhereElementIsNotElementType()
            .Where(e => !alreadyTagged.Contains(e.Id.Value)).ToList();

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
                    doc.Create.NewRoomTag(new LinkElementId(room.Id), new UV(lp.Point.X, lp.Point.Y), view.Id);
                }
                else
                {
                    var center = Center(e, view);
                    if (center is null) { Skip("no location to put the tag"); continue; }
                    IndependentTag.Create(doc, view.Id, new Reference(e), args.OptBool("leader"),
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
