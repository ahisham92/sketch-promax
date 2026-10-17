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
        int reached = 0;
        var stopped = false;
        foreach (var circuit in circuits)
        {
            if (stopped = CancelPoint.Stop("Placing rooms", reached++, circuits.Count)) { reached--; break; }
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
            stopped = CancelPoint.Note(stopped, reached, circuits.Count),
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
        var folder = OutputPaths.Checked(args.OptString("folder"))
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SketchProMax");
        Directory.CreateDirectory(folder);
        var name = (OutputPaths.Checked(args.OptString("file_name")) ?? schedule.Name) + ".csv";
        foreach (var bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');
        schedule.Export(folder, name, new ViewScheduleExportOptions { FieldDelimiter = ",", TextQualifier = ExportTextQualifier.DoubleQuote });
        return ToolResult.Ok(new { file = Path.Combine(folder, name) });
    }
}

public sealed class WorksetsTool : IRevitTool
{
    public string Name => "manage_worksets";
    public string Description =>
        "In a workshared model: create user worksets that don't exist yet, and/or move elements onto a workset (each element its own " +
        "step; ones another user owns are skipped and named). See also list_worksets, make_worksets_editable, borrow_elements, element_owners.";
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

        var failures = new List<object>();
        Batch.InGroup(doc, "Worksets", () =>
        {
            var names = args.StringList("create").Where(n => WorksetTable.IsWorksetNameUnique(doc, n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var made = ItemBatch.Run(doc, "Create worksets", names, n => n, _ => [], n => { Workset.Create(doc, n); return null; }, borrow: false);
            created.AddRange(made.Items.Where(o => o.Ok).Select(o => o.Item));
            failures.AddRange(Batch.Failures(made));
            if (args.Has("move"))
            {
                var spec = args.Raw["move"];
                var wsName = spec.GetProperty("workset").GetString();
                var ws = UserWorksets().FirstOrDefault(w => string.Equals(w.Name, wsName, StringComparison.OrdinalIgnoreCase))
                         ?? throw new ArgumentException($"No workset named '{wsName}'.");
                var ids = spec.GetProperty("element_ids").EnumerateArray().Select(x => x.GetInt64()).Distinct().ToList();
                failed.AddRange(ids.Where(i => doc.GetElement(new ElementId(i)) is null));
                ids = ids.Where(i => doc.GetElement(new ElementId(i)) is not null).ToList();
                // Each element is its own step: one element someone else owns is skipped (and named), the rest move.
                var batch = ItemBatch.Run(doc, "Move to workset", ids, i => i.ToString(), i => [new ElementId(i)], i =>
                {
                    var p = doc.GetElement(new ElementId(i)).get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                    if (p is null || p.IsReadOnly) throw new ArgumentException("its workset can't be changed (e.g. a view-specific or hosted element follows its owner).");
                    if (!p.Set(ws.Id.IntegerValue)) throw new ArgumentException("Revit refused the workset.");
                    return null;
                });
                moved = batch.Done;
                failed.AddRange(batch.Failed.Select(f => long.Parse(f.Item)));
                failures.AddRange(Batch.Failures(batch));
            }
            return created.Count + moved;
        }, n => n > 0);
        return ToolResult.Ok(new { created, moved, failed, failures = failures.Count > 0 ? failures : null, worksets = UserWorksets().Select(w => w.Name) });
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

        // Steps, each regenerated before the next: duplicate, then the type values (read back, re-applied one by one if they
        // did not stick), then the swaps (one element per step, so a locked element doesn't undo the others).
        ElementType? copy = null;
        var paramResults = new Dictionary<string, string>();
        var swapFailures = new List<object>();
        var swapped = 0;
        Batch.InGroup(doc, $"Duplicate type {source.Name}", () =>
        {
            var dup = ItemBatch.Run(doc, "Duplicate type", [source], s => s.Name, _ => [], s => s.Duplicate(args.String("new_name")).Id, borrow: false);
            if (!dup.Items[0].Ok) throw new InvalidOperationException($"Revit refused to duplicate '{source.Name}': {dup.Items[0].Error}");
            copy = (ElementType)doc.GetElement((ElementId)dup.Items[0].Value!);
            if (args.Has("parameters"))
            {
                var wants = ParamEdits.FromJson(args.Raw["parameters"]);
                var target = new ParamEdits.Target { Id = copy.Id, Label = copy.Name, Wants = wants };
                foreach (var l in ParamEdits.Apply(doc, "Type parameters", [target], borrow: false))
                    paramResults[l.parameter] = l.status + (l.status == "ok" ? "" : (l.now is null ? "" : $" (now {l.now})") + (l.note is null ? "" : $": {l.note}"));
            }
            var ids = args.LongList("apply_to_element_ids").Where(i => doc.GetElement(new ElementId(i)) is not null).Distinct().ToList();
            var swap = ItemBatch.Run(doc, "Swap to new type", ids, i => i.ToString(), i => [new ElementId(i)], i =>
            {
                var e = doc.GetElement(new ElementId(i));
                if (!e.IsValidType(copy.Id)) throw new ArgumentException("the new type doesn't fit this element.");
                e.ChangeTypeId(copy.Id);
                return null;
            });
            swapped = swap.Done;
            swapFailures.AddRange(Batch.Failures(swap));
            return copy;
        }, _ => true);
        return ToolResult.Ok(new { typeId = copy!.Id.Value, name = copy.Name, parameters = paramResults, swapped, swapFailures = swapFailures.Count > 0 ? swapFailures : null });
    }
}

public sealed class ChangeTypeTool : IRevitTool
{
    public string Name => "change_type";
    public string Description => "Swap elements to another type of the same category (e.g. all 'D1' doors on Level 2 to 'D2'). " +
        "Each element is its own step: elements another user owns (workshared) or that Revit refuses are skipped and listed with the reason.";
    public string PropertiesJson => """
    { "element_ids": {"type": "array", "items": {"type": "integer"}}, "type_id": {"type": "integer"} }
    """;
    public string[] Required => ["element_ids", "type_id"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var typeId = new ElementId(args.OptLong("type_id") ?? -1);
        var ids = args.LongList("element_ids").Distinct().ToList();
        var failed = ids.Where(i => doc.GetElement(new ElementId(i)) is null).ToList();
        var failures = failed.Select(i => (object)new { item = i.ToString(), reason = "not found" }).ToList();
        ids = ids.Except(failed).ToList();
        // Each element is its own step: a locked (borrowed by someone else) or refused element is skipped and named.
        var batch = Batch.InGroup(doc, "Change type", () => ItemBatch.Run(doc, "Change type", ids, i => i.ToString(), i => [new ElementId(i)], i =>
        {
            var e = doc.GetElement(new ElementId(i));
            if (!e.IsValidType(typeId)) throw new ArgumentException($"{doc.GetElement(typeId)?.Name ?? typeId.Value.ToString()} is not a valid type for this element.");
            e.ChangeTypeId(typeId);
            return null;
        }), _ => true);
        var changed = batch.Done;
        failed.AddRange(batch.Failed.Select(f => long.Parse(f.Item)));
        failures.AddRange(Batch.Failures(batch));
        return ToolResult.Ok(new { changed, failed, failures });
    }
}

public sealed class DeleteElementsTool : IRevitTool
{
    public string Name => "delete_elements";
    public string Description =>
        "Delete elements (e.g. unused views, empty sheets, duplicate tags). Reports everything Revit removed, " +
        "including dependent elements. Always confirm the list with the user first. Workshared: elements another user owns are left " +
        "and named with their owner; if Revit refuses the deletion as a whole, each element is tried on its own so only the refused ones stay.";
    public string PropertiesJson => """
    { "element_ids": {"type": "array", "items": {"type": "integer"}} }
    """;
    public string[] Required => ["element_ids"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var asked = args.LongList("element_ids").Distinct().ToList();
        var ids = asked.Select(i => new ElementId(i)).Where(id => doc.GetElement(id) != null).ToList();
        var notFound = asked.Where(i => doc.GetElement(new ElementId(i)) is null).ToList();
        // Workshared: elements someone else owns are left alone and named with their owner.
        var locked = Batch.Locked(doc, ids);
        ids = ids.Where(id => !locked.ContainsKey(id.Value)).ToList();
        if (ids.Count == 0)
            return ToolResult.Error(locked.Count > 0 ? $"Nothing deleted: {string.Join("; ", locked.Values.Take(10))}" : "None of those elements exist.");

        // Dry run (rolled back) to learn what Revit will cascade-delete, while the elements still exist to be described.
        var preview = new Dictionary<long, (string category, string name)>();
        using (var dry = new Transaction(doc, "Sketch ProMax: preview delete"))
        {
            dry.Start();
            try
            {
                foreach (var id in doc.Delete(ids))
                {
                    var e = doc.GetElement(id);
                    preview[id.Value] = (e?.Category?.Name ?? e?.GetType().Name ?? "Unknown", SafeName(e));
                }
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { /* described one by one below */ }
            dry.RollBack();
        }

        // All together first (fast); if Revit refuses that, each element on its own so only the refused ones stay.
        var failures = new List<object>();
        var method = "together";
        Batch.InGroup(doc, "Delete elements", () =>
        {
            var all = ItemBatch.Run(doc, "Delete elements", [ids], _ => "all", x => x.Concat(preview.Keys.Select(k => new ElementId(k))), x => doc.Delete(x).Count);
            if (all.Done == 1) return 1;
            method = "one by one";
            var each = ItemBatch.Run(doc, "Delete elements", ids, id => id.Value.ToString(), id => [id], id =>
                doc.GetElement(id) is null ? 0 : doc.Delete(id).Count, borrow: false);
            failures.AddRange(Batch.Failures(each));
            return each.Done;
        }, n => n > 0);

        var requestedSet = new HashSet<long>(ids.Select(i => i.Value));
        var deletedRequested = ids.Where(id => doc.GetElement(id) is null).Select(id => id.Value).ToList();
        if (deletedRequested.Count == 0)
            return ToolResult.Error($"Revit refused every deletion; nothing was deleted. {System.Text.Json.JsonSerializer.Serialize(failures.Take(10))}");
        var dependents = preview.Keys.Where(k => !requestedSet.Contains(k) && doc.GetElement(new ElementId(k)) is null)
            .Select(k => new { id = k, category = preview[k].category, name = preview[k].name }).ToList();
        const int cap = 200;
        return ToolResult.Ok(new
        {
            requested = asked.Count,
            deleted = deletedRequested.Count,
            totalDeletedIncludingDependents = deletedRequested.Count + dependents.Count,
            dependentCount = dependents.Count,
            dependentsByCategory = dependents.GroupBy(d => d.category).ToDictionary(g => g.Key, g => g.Count()),
            dependents = dependents.Take(cap),
            dependentsTruncated = dependents.Count > cap,
            method,
            notFound = notFound.Count > 0 ? notFound : null,
            locked = locked.Count > 0 ? locked.Select(kv => new { id = kv.Key, reason = kv.Value }) : null,
            failures = failures.Count > 0 ? failures : null,
        });
    }

    private static string SafeName(Element? e)
    {
        if (e == null) return "";
        try { return e.Name ?? ""; } catch { return ""; }
    }
}
