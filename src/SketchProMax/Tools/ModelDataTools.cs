using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
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
        // Rooms go in the active view's phase, so they show up where the user is looking.
        var phase = doc.GetElement(app.ActiveUIDocument.ActiveView.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId() ?? ElementId.InvalidElementId) as Phase;
        using var t = RevitHelpers.StartTransaction(doc, $"Place rooms on {level.Name}");
        // Walk the level's plan circuits ourselves: NewRooms2 also fills circuits that already hold a room
        // (leaving redundant, zero-area rooms). Only circuits without a room, with a real area, get one.
        var topology = phase != null ? doc.get_PlanTopology(level, phase) : doc.get_PlanTopology(level);
        var circuits = new List<PlanCircuit>();
        foreach (PlanCircuit c in topology.Circuits) circuits.Add(c);
        var created = new List<long>();
        int alreadyHadRoom = 0, tooSmall = 0;
        var failed = new List<string>();
        var minArea = 0.1 * 0.1 / (0.3048 * 0.3048); // 0.01 m2 in square feet
        foreach (var circuit in circuits)
        {
            if (circuit.IsRoomLocated) { alreadyHadRoom++; continue; }
            if (circuit.Area < minArea) { tooSmall++; continue; }
            Room? room = null;
            try
            {
                if (phase != null)
                {
                    // An unplaced room in the view's phase, then placed in the circuit.
                    room = doc.Create.NewRoom(phase);
                    room = doc.Create.NewRoom(room, circuit);
                }
                else room = doc.Create.NewRoom(null, circuit);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                if (room != null && room.IsValidObject && room.Location == null) doc.Delete(room.Id);
                failed.Add(ex.Message.Trim());
                continue;
            }
            if (room != null) created.Add(room.Id.Value);
        }
        var status = t.Commit();
        if (status != TransactionStatus.Committed)
            return ToolResult.Error("Revit rolled the room placement back; no rooms were created.");
        var existing = created.Where(id => doc.GetElement(new ElementId(id)) is Room).ToList();
        return ToolResult.Ok(new
        {
            created = existing,
            circuits = circuits.Count,
            skippedAlreadyHaveRoom = alreadyHadRoom,
            skippedTooSmall = tooSmall,
            failed = failed.Count > 0 ? failed.Distinct().Take(10) : null,
        });
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
        // Dry run in a sub-transaction to learn what Revit will cascade-delete, while the elements still exist
        // to be described; then roll it back and delete for real.
        var preview = new Dictionary<long, (string category, string name)>();
        using (var st = new SubTransaction(doc))
        {
            st.Start();
            var wouldDelete = doc.Delete(ids);
            st.RollBack();
            foreach (var id in wouldDelete)
            {
                var e = doc.GetElement(id);
                preview[id.Value] = (e?.Category?.Name ?? e?.GetType().Name ?? "Unknown", SafeName(e));
            }
        }
        var deleted = doc.Delete(ids);
        if (t.Commit() != TransactionStatus.Committed)
            return ToolResult.Error("Revit rolled the deletion back; nothing was deleted.");
        var requestedSet = new HashSet<long>(ids.Select(i => i.Value));
        var dependents = deleted.Where(id => !requestedSet.Contains(id.Value)).Select(id =>
        {
            var info = preview.TryGetValue(id.Value, out var v) ? v : ("Unknown", "");
            return new { id = id.Value, category = info.Item1, name = info.Item2 };
        }).ToList();
        const int cap = 200;
        return ToolResult.Ok(new
        {
            requested = ids.Count,
            totalDeletedIncludingDependents = deleted.Count,
            dependentCount = dependents.Count,
            dependentsByCategory = dependents.GroupBy(d => d.category).ToDictionary(g => g.Key, g => g.Count()),
            dependents = dependents.Take(cap),
            dependentsTruncated = dependents.Count > cap,
        });
    }

    private static string SafeName(Element? e)
    {
        if (e == null) return "";
        try { return e.Name ?? ""; } catch { return ""; }
    }
}
