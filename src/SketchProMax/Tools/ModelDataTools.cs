using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

public sealed class PlaceRoomsTool : IRevitTool
{
    public string Name => "place_rooms";
    public string Description =>
        "Automatically place rooms in every enclosed, room-bounded area on a level that has no room yet. " +
        "Then use rename_elements to name and number them.";
    public string PropertiesJson => """
    { "level": {"type": "string"} }
    """;
    public string[] Required => ["level"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var level = RevitHelpers.FindLevel(doc, args.String("level"));
        using var t = RevitHelpers.StartTransaction(doc, $"Place rooms on {level.Name}");
        var ids = doc.Create.NewRooms2(level);
        t.Commit();
        return ToolResult.Ok(new { created = ids.Select(i => i.Value) });
    }
}

public sealed class ReadScheduleTool : IRevitTool
{
    public string Name => "read_schedule";
    public string Description => "Read a schedule's table (header and body rows as text) so you can check or summarize it.";
    public string PropertiesJson => """
    { "schedule_id": {"type": "integer"}, "max_rows": {"type": "integer"} }
    """;
    public string[] Required => ["schedule_id"];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        if (doc.GetElement(new ElementId(args.OptLong("schedule_id") ?? -1)) is not ViewSchedule schedule)
            return ToolResult.Error("schedule_id is not a schedule.");
        var body = schedule.GetTableData().GetSectionData(SectionType.Body);
        var max = (int)(args.OptLong("max_rows") ?? 500);
        var rows = new List<List<string>>();
        for (int r = body.FirstRowNumber; r <= body.LastRowNumber && rows.Count < max; r++)
        {
            var row = new List<string>();
            for (int c = body.FirstColumnNumber; c <= body.LastColumnNumber; c++)
                row.Add(schedule.GetCellText(SectionType.Body, r, c));
            rows.Add(row);
        }
        return ToolResult.Ok(new { name = schedule.Name, totalRows = body.NumberOfRows, rows });
    }
}

public sealed class ExportScheduleTool : IRevitTool
{
    public string Name => "export_schedule";
    public string Description => "Export a schedule to a CSV file (opens in Excel). Defaults to Documents\\SketchProMax.";
    public string PropertiesJson => """
    { "schedule_id": {"type": "integer"}, "folder": {"type": "string"}, "file_name": {"type": "string"} }
    """;
    public string[] Required => ["schedule_id"];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        if (doc.GetElement(new ElementId(args.OptLong("schedule_id") ?? -1)) is not ViewSchedule schedule)
            return ToolResult.Error("schedule_id is not a schedule.");
        var folder = args.OptString("folder")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SketchProMax");
        Directory.CreateDirectory(folder);
        var name = (args.OptString("file_name") ?? schedule.Name) + ".csv";
        foreach (var bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');
        schedule.Export(folder, name, new ViewScheduleExportOptions { FieldDelimiter = ",", TextQualifier = ExportTextQualifier.DoubleQuote });
        return ToolResult.Ok(new { file = Path.Combine(folder, name) });
    }
}

public sealed class WorksetsTool : IRevitTool
{
    public string Name => "manage_worksets";
    public string Description =>
        "In a workshared model: create user worksets that don't exist yet, and/or move elements onto a workset.";
    public string PropertiesJson => """
    {
      "create": {"type": "array", "items": {"type": "string"}},
      "move": {"type": "object", "properties": {
        "workset": {"type": "string"}, "element_ids": {"type": "array", "items": {"type": "integer"}}},
        "required": ["workset", "element_ids"]}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        if (!doc.IsWorkshared) return ToolResult.Error("This model is not workshared.");

        List<Workset> UserWorksets() => new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToWorksets().ToList();
        var created = new List<string>();
        int moved = 0;
        var failed = new List<long>();

        using var t = RevitHelpers.StartTransaction(doc, "Worksets");
        foreach (var name in args.StringList("create"))
        {
            if (WorksetTable.IsWorksetNameUnique(doc, name)) { Workset.Create(doc, name); created.Add(name); }
        }
        if (args.Has("move"))
        {
            var spec = args.Raw["move"];
            var wsName = spec.GetProperty("workset").GetString();
            var ws = UserWorksets().FirstOrDefault(w => string.Equals(w.Name, wsName, StringComparison.OrdinalIgnoreCase))
                     ?? throw new ArgumentException($"No workset named '{wsName}'.");
            foreach (var idEl in spec.GetProperty("element_ids").EnumerateArray())
            {
                var e = doc.GetElement(new ElementId(idEl.GetInt64()));
                var p = e?.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                if (p != null && !p.IsReadOnly && p.Set(ws.Id.IntegerValue)) moved++;
                else failed.Add(idEl.GetInt64());
            }
        }
        t.Commit();
        return ToolResult.Ok(new { created, moved, failed, worksets = UserWorksets().Select(w => w.Name) });
    }
}

public sealed class DuplicateTypeTool : IRevitTool
{
    public string Name => "duplicate_type";
    public string Description =>
        "Duplicate a family/system type under a new name, set type parameters on the copy (e.g. Width '1000 mm'), " +
        "and optionally swap given instances to the new type.";
    public string PropertiesJson => """
    {
      "type_id": {"type": "integer"},
      "new_name": {"type": "string"},
      "parameters": {"type": "object", "additionalProperties": {"type": "string"}, "description": "Parameter name -> value as typed in Revit."},
      "apply_to_element_ids": {"type": "array", "items": {"type": "integer"}}
    }
    """;
    public string[] Required => ["type_id", "new_name"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        if (doc.GetElement(new ElementId(args.OptLong("type_id") ?? -1)) is not ElementType source)
            return ToolResult.Error("type_id is not a type.");

        using var t = RevitHelpers.StartTransaction(doc, $"Duplicate type {source.Name}");
        var copy = source.Duplicate(args.String("new_name"));
        var paramResults = new Dictionary<string, string>();
        if (args.Has("parameters"))
        {
            foreach (var kv in args.Raw["parameters"].EnumerateObject())
            {
                var p = copy.LookupParameter(kv.Name);
                paramResults[kv.Name] = p is null ? "not found"
                    : p.IsReadOnly ? "read-only"
                    : (p.StorageType == StorageType.String ? p.Set(kv.Value.GetString()) : p.SetValueString(kv.Value.GetString())) ? "ok" : "rejected";
            }
        }
        int swapped = 0;
        foreach (var id in args.LongList("apply_to_element_ids"))
        {
            var e = doc.GetElement(new ElementId(id));
            if (e != null && e.IsValidType(copy.Id)) { e.ChangeTypeId(copy.Id); swapped++; }
        }
        t.Commit();
        return ToolResult.Ok(new { typeId = copy.Id.Value, name = copy.Name, parameters = paramResults, swapped });
    }
}

public sealed class ChangeTypeTool : IRevitTool
{
    public string Name => "change_type";
    public string Description => "Swap elements to another type of the same category (e.g. all 'D1' doors on Level 2 to 'D2').";
    public string PropertiesJson => """
    { "element_ids": {"type": "array", "items": {"type": "integer"}}, "type_id": {"type": "integer"} }
    """;
    public string[] Required => ["element_ids", "type_id"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var typeId = new ElementId(args.OptLong("type_id") ?? -1);
        int changed = 0;
        var failed = new List<long>();
        using var t = RevitHelpers.StartTransaction(doc, "Change type");
        foreach (var id in args.LongList("element_ids"))
        {
            var e = doc.GetElement(new ElementId(id));
            if (e != null && e.IsValidType(typeId)) { e.ChangeTypeId(typeId); changed++; } else failed.Add(id);
        }
        t.Commit();
        return ToolResult.Ok(new { changed, failed });
    }
}

public sealed class DeleteElementsTool : IRevitTool
{
    public string Name => "delete_elements";
    public string Description =>
        "Delete elements (e.g. unused views, empty sheets, duplicate tags). Reports everything Revit removed, " +
        "including dependent elements. Always confirm the list with the user first.";
    public string PropertiesJson => """
    { "element_ids": {"type": "array", "items": {"type": "integer"}} }
    """;
    public string[] Required => ["element_ids"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var ids = args.LongList("element_ids").Select(i => new ElementId(i)).Where(id => doc.GetElement(id) != null).ToList();
        using var t = RevitHelpers.StartTransaction(doc, "Delete elements");
        var deleted = doc.Delete(ids);
        t.Commit();
        return ToolResult.Ok(new { requested = ids.Count, totalDeletedIncludingDependents = deleted.Count });
    }
}
