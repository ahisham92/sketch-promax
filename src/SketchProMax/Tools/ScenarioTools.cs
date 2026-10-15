using System.IO;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

public sealed class LinkModelTool : IRevitTool
{
    public string Name => "link_model";
    public string Description =>
        "Link a Revit model (.rvt) into this one, e.g. the architectural model before building the structure from it. " +
        "Placement by shared coordinates (default, falls back to origin when the models share none) or origin to origin. " +
        "If the file is already linked, returns that link instead of linking it twice. Returns the link name to use as " +
        "'source' or 'link' in other tools.";
    public string PropertiesJson => """
    {
      "path": {"type": "string", "description": "Full path of the .rvt file."},
      "placement": {"type": "string", "enum": ["shared", "origin"], "description": "Default shared."}
    }
    """;
    public string[] Required => ["path"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var path = args.String("path").Trim().Trim('"');
        if (!path.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Give the full path of a .rvt file.");
        if (!File.Exists(path)) throw new ArgumentException($"File not found: {path}");
        path = Path.GetFullPath(path);
        string Norm(string p) { try { return Path.GetFullPath(p); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return p; } }
        var existing = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>()
            .FirstOrDefault(t => !t.IsNestedLink && t.GetExternalFileReference() is { } r
                                 && string.Equals(Norm(ModelPathUtils.ConvertModelPathToUserVisiblePath(r.GetAbsolutePath())), path, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            var inst = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().FirstOrDefault(i => i.GetTypeId() == existing.Id);
            if (inst != null) return ToolResult.Ok(new { alreadyLinked = true, link = existing.Name, instanceId = inst.Id.Value, loaded = inst.GetLinkDocument() != null });
        }

        using var t = RevitHelpers.StartTransaction(doc, "Link model");
        var typeId = existing?.Id;
        if (typeId is null)
        {
            var load = RevitLinkType.Create(doc, ModelPathUtils.ConvertUserVisiblePathToModelPath(path), new RevitLinkOptions(false));
            typeId = load.ElementId;
            if (typeId is null || typeId == ElementId.InvalidElementId) throw new InvalidOperationException($"Revit couldn't load the link ({load.LoadResult}).");
        }
        var placement = args.OptString("placement") ?? "shared";
        string used;
        RevitLinkInstance instance;
        if (placement == "shared")
        {
            // Revit may refuse or warn when the models share no coordinates; fall back to origin to origin.
            using var sub = new SubTransaction(doc);
            sub.Start();
            try { instance = RevitLinkInstance.Create(doc, typeId, ImportPlacement.Shared); sub.Commit(); used = "shared coordinates (if Revit warns the models share none, it placed it origin to origin)"; }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                if (!sub.HasEnded()) sub.RollBack();
                instance = RevitLinkInstance.Create(doc, typeId, ImportPlacement.Origin); used = "origin to origin (the models share no coordinates)";
            }
        }
        else { instance = RevitLinkInstance.Create(doc, typeId, ImportPlacement.Origin); used = "origin to origin"; }
        if (t.Commit() != TransactionStatus.Committed) return ToolResult.Error("Revit refused the link when saving it.");
        var linkType = (RevitLinkType)doc.GetElement(typeId);
        return ToolResult.Ok(new { link = linkType.Name, instanceId = instance.Id.Value, placement = used, loaded = instance.GetLinkDocument() != null });
    }
}

public sealed class CreateRaftTool : IRevitTool
{
    public string Name => "create_raft";
    public string Description =>
        "Model a raft (mat) foundation under a floor: its outline follows the floor's outer edge (plus an optional projection), " +
        "its top a given depth below the floor's top (e.g. 1500 mm under the slab on grade), at a thickness. The floor is " +
        "named by id, by Mark, or as the lowest floor on a level. Uses a foundation slab type made by thickness (Raft 1000mm).";
    public string PropertiesJson => """
    {
      "floor_id": {"type": "integer", "description": "The floor (e.g. slab on grade) the raft sits under."},
      "floor_mark": {"type": "string", "description": "Or the floor's Mark."},
      "level": {"type": "string", "description": "Or the level: its lowest, largest floor."},
      "depth_below": {"type": "number", "description": "Raft top below the floor's top, project units (default 1500 mm)."},
      "thickness": {"type": "number", "description": "Raft thickness, project units (default 1000 mm)."},
      "projection": {"type": "number", "description": "Raft edge past the floor's edge, project units (default 0)."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var floors = new FilteredElementCollector(doc).OfClass(typeof(Floor)).Cast<Floor>().ToList();
        Floor floor;
        if (args.OptLong("floor_id") is { } id)
            floor = doc.GetElement(new ElementId(id)) as Floor ?? throw new ArgumentException($"{id} is not a floor.");
        else if (args.OptString("floor_mark") is { } mark)
            floor = floors.FirstOrDefault(f => f.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() == mark) ?? throw new ArgumentException($"No floor marked '{mark}'.");
        else if (args.OptString("level") is { } ln)
        {
            var level = RevitHelpers.FindLevel(doc, ln);
            floor = floors.Where(f => f.LevelId == level.Id && !f.FloorType.IsFoundationSlab)
                        .OrderBy(f => f.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.AsDouble() ?? 0)
                        .ThenByDescending(f => f.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED)?.AsDouble() ?? 0).FirstOrDefault()
                    ?? throw new ArgumentException($"No floor on level '{level.Name}'.");
        }
        else throw new ArgumentException("Give floor_id, floor_mark or level.");

        var depth = args.OptDouble("depth_below") is { } d ? RevitHelpers.ToInternal(doc, d) : 1500 / 304.8;
        var thickness = args.OptDouble("thickness") is { } th ? RevitHelpers.ToInternal(doc, th) : 1000 / 304.8;
        var projection = args.OptDouble("projection") is { } pr ? RevitHelpers.ToInternal(doc, pr) : 0;
        if (thickness <= 0 || depth < 0 || projection < 0) throw new ArgumentException("thickness must be positive, and depth_below and projection not negative.");
        var floorThickness = floor.FloorType.GetCompoundStructure()?.GetWidth() ?? 0;
        if (depth < floorThickness - 1e-6) throw new ArgumentException($"depth_below must be at least the floor's thickness ({Math.Round(floorThickness * 304.8)} mm), or the raft overlaps the floor.");
        var floorLevel = (Level)doc.GetElement(floor.LevelId);
        var floorTop = floorLevel.ProjectElevation + (floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.AsDouble() ?? 0);
        var raftTop = floorTop - depth;

        // Outer edge only (the raft has no openings), flattened and pushed out by the projection.
        var profile = Outlines.FloorProfile(doc, floor).OrderByDescending(Area).ToList();
        var outer = profile.First();
        var flat = CurveLoop.CreateViaTransform(outer, Transform.CreateTranslation(new XYZ(0, 0, floorLevel.ProjectElevation - outer.First().GetEndPoint(0).Z)));
        if (projection > 1e-6)
        {
            var a = CurveLoop.CreateViaOffset(flat, projection, XYZ.BasisZ);
            flat = Area(a) > Area(flat) ? a : CurveLoop.CreateViaOffset(flat, -projection, XYZ.BasisZ);
        }

        using var t = RevitHelpers.StartTransaction(doc, "Raft");
        var type = ConcreteTypes.Slab(doc, thickness, "Raft", foundation: true);
        Floor raft;
        try { raft = Floor.Create(doc, [flat], type.Id, floorLevel.Id, true, null, 0); }
        catch (ArgumentException) when (type.IsFoundationSlab)
        {
            // Some versions refuse foundation slab types here: fall back to a structural floor type named Raft.
            type = ConcreteTypes.Slab(doc, thickness, "Raft (structural floor)");
            raft = Floor.Create(doc, [flat], type.Id, floorLevel.Id, true, null, 0);
        }
        if (raft.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM) is not { IsReadOnly: false } offset || !offset.Set(raftTop - floorLevel.ProjectElevation))
        {
            t.RollBack();
            return ToolResult.Error("Couldn't set the raft's height offset from the level.");
        }
        raft.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set("RAFT");
        if (t.Commit() != TransactionStatus.Committed) return ToolResult.Error("Revit refused the raft when saving it.");
        return ToolResult.Ok(new
        {
            id = raft.Id.Value, type = type.Name, foundationSlab = type.IsFoundationSlab, under = floor.Id.Value,
            topElevation = Elevations.FromInternalZ(doc, raftTop), bottomElevation = Elevations.FromInternalZ(doc, raftTop - thickness),
            otherAreas = profile.Count > 1 && profile.Skip(1).Any(l => Area(l) > Area(outer) * 0.05) ? "The floor's sketch has more than one loop; only the largest outline was used (holes are ignored for a raft). Check separate floor areas." : null,
            next = "Reinforce it with reinforce_slab_wall (cover 75 mm) and show it with rebar_section / a foundation plan.",
        });
    }

    private static double Area(CurveLoop loop)
    {
        // Shoelace area of the loop's tessellated points in plan.
        var pts = loop.SelectMany(c => { var t = c.Tessellate(); return t.Take(t.Count - 1); }).ToList();
        var a = 0.0;
        for (var i = 0; i < pts.Count; i++) { var p = pts[i]; var q = pts[(i + 1) % pts.Count]; a += p.X * q.Y - q.X * p.Y; }
        return Math.Abs(a) / 2;
    }
}

public sealed class WorkProgressTool : IRevitTool
{
    public string Name => "work_progress";
    public string Description =>
        "Keep the plan and progress of a long job (e.g. architecture to an issued structural package) saved with this model, " +
        "so it can be picked up after 'continue' or in a new chat. 'set' saves the goal and steps, 'update' marks a step " +
        "done/failed/skipped with a note (ids, sheet numbers), 'get' reads it back. Read it first when the user says continue.";
    public string PropertiesJson => """
    {
      "action": {"type": "string", "enum": ["get", "set", "update", "clear"]},
      "goal": {"type": "string", "description": "set: the job in one line."},
      "steps": {"type": "array", "items": {"type": "string"}, "description": "set: the steps in order."},
      "step": {"type": "integer", "description": "update: step number (1-based)."},
      "status": {"type": "string", "enum": ["pending", "in_progress", "done", "failed", "skipped"]},
      "note": {"type": "string", "description": "update: what was made or what went wrong (ids, names), for the next steps."}
    }
    """;
    public string[] Required => ["action"];
    public bool ModifiesModel => false;

    private sealed class Step { public string Text { get; set; } = ""; public string Status { get; set; } = "pending"; public string? Note { get; set; } }
    private sealed class Plan { public string Goal { get; set; } = ""; public List<Step> Steps { get; set; } = []; public string Updated { get; set; } = ""; }

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax", "progress");
        var file = Path.Combine(folder, $"{Checks.ModelKey(doc)}.json");
        var options = new JsonSerializerOptions { WriteIndented = true };
        Plan? Load()
        {
            try { return File.Exists(file) ? JsonSerializer.Deserialize<Plan>(File.ReadAllText(file)) : null; }
            catch (JsonException) { return null; }
        }
        void Save(Plan p) { Directory.CreateDirectory(folder); p.Updated = Checks.Now; File.WriteAllText(file, JsonSerializer.Serialize(p, options)); }
        object View(Plan p) => new
        {
            goal = p.Goal, updated = p.Updated,
            next = p.Steps.FirstOrDefault(s => s.Status is "pending" or "in_progress" or "failed") is { } n ? $"{p.Steps.IndexOf(n) + 1}. {n.Text}" : "all steps finished",
            steps = p.Steps.Select((s, i) => new { n = i + 1, step = s.Text, status = s.Status, note = s.Note }),
        };

        switch (args.String("action"))
        {
            case "get":
                return Load() is { } saved ? ToolResult.Ok(View(saved)) : ToolResult.Ok(new { goal = (string?)null, note = "No saved plan for this model." });
            case "set":
            {
                var steps = args.StringList("steps");
                if (steps.Count == 0) throw new ArgumentException("Give the steps.");
                var plan = new Plan { Goal = args.OptString("goal") ?? "", Steps = steps.Select(s => new Step { Text = s }).ToList() };
                Save(plan);
                return ToolResult.Ok(View(plan));
            }
            case "update":
            {
                var plan = Load() ?? throw new ArgumentException("No saved plan; use action 'set' first.");
                var n = (int)(args.OptLong("step") ?? throw new ArgumentException("Give step."));
                if (n < 1 || n > plan.Steps.Count) throw new ArgumentException($"step must be 1 to {plan.Steps.Count}.");
                plan.Steps[n - 1].Status = args.OptString("status") ?? "done";
                if (args.OptString("note") is { } note) plan.Steps[n - 1].Note = note.Length > 2000 ? note.Substring(0, 2000) : note;
                Save(plan);
                return ToolResult.Ok(View(plan));
            }
            case "clear":
                if (File.Exists(file)) File.Delete(file);
                return ToolResult.Ok(new { cleared = true });
            default:
                throw new ArgumentException("action is get, set, update or clear.");
        }
    }
}
