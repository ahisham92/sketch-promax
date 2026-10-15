using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

public sealed class SetParametersTool : IRevitTool
{
    public string Name => "set_parameters";
    public string Description =>
        "Set parameter values on elements. Values are written as the user would type them " +
        "in Revit (e.g. '2100 mm', '60 min', 'Yes'). Set type=true to write the element's type parameter " +
        "(this affects every instance of that type). Each element is its own step, so one refused or locked element " +
        "(workshared: borrowed by someone else, named in the result) undoes nothing else. Values are read back after Revit " +
        "regenerates; any that did not stick are re-applied one parameter per step, and what still differs is reported " +
        "with the current value and why (formula, reporting/read-only, constraint). separate_steps=true writes every value as its own step.";
    public string PropertiesJson => """
    {
      "changes": {
        "type": "array",
        "items": {
          "type": "object",
          "properties": {
            "element_id": {"type": "integer"},
            "parameter": {"type": "string"},
            "value": {"type": "string"},
            "type": {"type": "boolean"}
          },
          "required": ["element_id", "parameter", "value"]
        }
      },
      "separate_steps": {"type": "boolean", "description": "Write each value in its own transaction, regenerating in between (default: per element, splitting only when needed)."}
    }
    """;
    public string[] Required => ["changes"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var rows = new List<(long id, string name, string value, ElementId? target)>();
        foreach (var c in args.Array("changes"))
        {
            var id = c.GetProperty("element_id").GetInt64();
            var name = c.GetProperty("parameter").GetString()!;
            var value = c.TryGetProperty("value", out var v) ? (v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText()) : "";
            var onType = c.TryGetProperty("type", out var tp) && tp.ValueKind == System.Text.Json.JsonValueKind.True;
            var e = doc.GetElement(new ElementId(id));
            var target = e is null ? null : onType ? e.GetTypeId() : e.Id;
            if (target == ElementId.InvalidElementId) target = null;
            rows.Add((id, name, value, target));
        }

        // One target per element (or type), keeping the order the changes were given in.
        var targets = rows.Where(r => r.target is not null).GroupBy(r => r.target!.Value)
            .Select(g => new ParamEdits.Target
            {
                Id = g.First().target!, Label = g.First().target!.Value.ToString(),
                // The same parameter given twice: the last value wins (writing both would undo each other's read-back).
                Wants = g.GroupBy(r => r.name, StringComparer.OrdinalIgnoreCase).Select(n => new ParamEdits.Want(n.Last().name, n.Last().value)).ToList(),
            }).ToList();
        var lines = targets.Count == 0 ? []
            : Batch.InGroup(doc, "Set parameters",
                () => ParamEdits.Apply(doc, "Set parameters", targets, args.OptBool("separate_steps")),
                l => l.Any(x => x.Ok));

        // Each change's own line (the same parameter given twice on one element reports the final state twice).
        var results = new List<object>();
        foreach (var r in rows)
        {
            if (r.target is null)
            {
                results.Add(new { element_id = r.id, parameter = r.name, status = doc.GetElement(new ElementId(r.id)) is null ? "element not found" : "element has no type" });
                continue;
            }
            var l = lines.LastOrDefault(x => x.element_id == r.target.Value && string.Equals(x.parameter, r.name, StringComparison.OrdinalIgnoreCase));
            results.Add(new
            {
                element_id = r.id,
                parameter = r.name,
                status = l?.status ?? "not applied",
                now = l is null || l.status == "ok" ? null : l.now,
                note = l?.note,
            });
        }
        return ToolResult.Ok(results);
    }

    internal static bool Write(Parameter p, string value) => p.StorageType switch
    {
        StorageType.String => p.Set(value),
        StorageType.Integer when value.Equals("yes", StringComparison.OrdinalIgnoreCase) => p.Set(1),
        StorageType.Integer when value.Equals("no", StringComparison.OrdinalIgnoreCase) => p.Set(0),
        StorageType.Integer when int.TryParse(value, out var i) => p.Set(i),
        StorageType.ElementId when long.TryParse(value, out var l) => p.Set(new ElementId(l)),
        _ => p.SetValueString(value),
    };
}

public sealed class CreateWallsTool : IRevitTool
{
    public string Name => "create_walls";
    public string Description =>
        "Create straight walls from 2D segments on a level. Coordinates and height are in the project's length unit " +
        "(see get_model_context). Use list_types with category 'Walls' to pick a wall type. Segments that form a closed " +
        "loop are oriented so the walls' exterior faces point out of the loop, whichever way the loop is drawn " +
        "(orient_loops false keeps them as given).";
    public string PropertiesJson => """
    {
      "segments": {
        "type": "array",
        "items": {
          "type": "object",
          "properties": {
            "start": {"type": "array", "items": {"type": "number"}, "minItems": 2, "maxItems": 2},
            "end": {"type": "array", "items": {"type": "number"}, "minItems": 2, "maxItems": 2}
          },
          "required": ["start", "end"]
        }
      },
      "level": {"type": "string", "description": "Base level name (default: lowest level)."},
      "height": {"type": "number", "description": "Unconnected height in project units."},
      "wall_type": {"type": "string", "description": "Exact wall type name (default: model default)."},
      "structural": {"type": "boolean"},
      "orient_loops": {"type": "boolean", "description": "Turn closed loops so exterior faces point outwards (default true)."}
    }
    """;
    public string[] Required => ["segments", "height"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var level = RevitHelpers.FindLevel(doc, args.OptString("level"));
        var height = RevitHelpers.ToInternal(doc, args.Double("height"));

        ElementId typeId = doc.GetDefaultElementTypeId(ElementTypeGroup.WallType);
        if (args.OptString("wall_type") is { } typeName)
        {
            typeId = new FilteredElementCollector(doc).OfClass(typeof(WallType))
                .FirstOrDefault(w => string.Equals(w.Name, typeName, StringComparison.OrdinalIgnoreCase))?.Id
                ?? throw new ArgumentException($"No wall type named '{typeName}'.");
        }

        var z = level.ProjectElevation;
        var segments = new List<(XYZ a, XYZ b)>();
        foreach (var seg in args.Array("segments"))
        {
            var a = Point(doc, seg.GetProperty("start"), z);
            var b = Point(doc, seg.GetProperty("end"), z);
            if (a.DistanceTo(b) < doc.Application.ShortCurveTolerance) continue;
            segments.Add((a, b));
        }
        var reversed = args.OptBool("orient_loops", true) ? LoopReversals(segments) : new HashSet<int>();

        var created = new List<long>();
        using var t = RevitHelpers.StartTransaction(doc, "Create walls");
        for (int i = 0; i < segments.Count; i++)
        {
            var (a, b) = segments[i];
            var line = reversed.Contains(i) ? Line.CreateBound(b, a) : Line.CreateBound(a, b);
            var wall = Wall.Create(doc, line, typeId, level.Id, height, 0, false, args.OptBool("structural"));
            created.Add(wall.Id.Value);
        }
        t.Commit();
        return ToolResult.Ok(new { created, reversedForExterior = reversed.Count });
    }

    /// <summary>
    /// Segments (by index) to draw reversed so every closed loop runs clockwise in plan. Revit puts a wall's
    /// exterior face on the left of its direction, so clockwise loops face outwards. Only components where every
    /// corner joins exactly two segments (simple closed loops) are touched; open runs keep their direction.
    /// </summary>
    internal static HashSet<int> LoopReversals(IReadOnlyList<(XYZ a, XYZ b)> segments)
    {
        const double tol = 1e-3; // feet, ~0.3 mm
        var vertices = new List<XYZ>();
        int Vertex(XYZ p)
        {
            for (int k = 0; k < vertices.Count; k++)
                if (Math.Abs(vertices[k].X - p.X) < tol && Math.Abs(vertices[k].Y - p.Y) < tol) return k;
            vertices.Add(p);
            return vertices.Count - 1;
        }
        var va = segments.Select(s => Vertex(s.a)).ToArray();
        var vb = segments.Select(s => Vertex(s.b)).ToArray();
        var at = new Dictionary<int, List<int>>();
        for (int i = 0; i < segments.Count; i++)
            foreach (var v in new[] { va[i], vb[i] })
            {
                if (!at.TryGetValue(v, out var list)) at[v] = list = new List<int>();
                list.Add(i);
            }

        var result = new HashSet<int>();
        var seen = new bool[segments.Count];
        for (int start = 0; start < segments.Count; start++)
        {
            if (seen[start] || va[start] == vb[start]) continue;
            // Walk the loop from this segment; give up when a corner does not join exactly two segments.
            var order = new List<(int seg, bool forward)>();
            var cur = start;
            var forward = true;
            var closed = false;
            while (true)
            {
                order.Add((cur, forward));
                seen[cur] = true;
                var end = forward ? vb[cur] : va[cur];
                if (at[end].Count != 2) break;
                var next = at[end][0] == cur ? at[end][1] : at[end][0];
                if (next == start) { closed = at[va[start]].Count == 2; break; }
                if (seen[next]) break;
                forward = va[next] == end;
                cur = next;
            }
            if (!closed || order.Count < 3) continue;

            double area = 0; // shoelace over the walk, > 0 = counter-clockwise
            foreach (var (seg, fwd) in order)
            {
                XYZ p = fwd ? segments[seg].a : segments[seg].b, q = fwd ? segments[seg].b : segments[seg].a;
                area += p.X * q.Y - q.X * p.Y;
            }
            if (Math.Abs(area) < 1e-9) continue;
            // Draw each segment in the clockwise sense of the loop.
            foreach (var (seg, fwd) in order)
                if ((area > 0) == fwd) result.Add(seg);
        }
        return result;
    }

    private static XYZ Point(Document doc, System.Text.Json.JsonElement arr, double z)
    {
        var xy = arr.EnumerateArray().Select(v => RevitHelpers.ToInternal(doc, v.GetDouble())).ToArray();
        return new XYZ(xy[0], xy[1], z);
    }
}
