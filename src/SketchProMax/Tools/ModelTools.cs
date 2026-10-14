using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

public sealed class SetParametersTool : IRevitTool
{
    public string Name => "set_parameters";
    public string Description =>
        "Set parameter values on elements in one transaction. Values are written as the user would type them " +
        "in Revit (e.g. '2100 mm', '60 min', 'Yes'). Set type=true to write the element's type parameter " +
        "(this affects every instance of that type).";
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
      }
    }
    """;
    public string[] Required => ["changes"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var results = new List<object>();
        using var t = RevitHelpers.StartTransaction(doc, "Set parameters");

        foreach (var c in args.Array("changes"))
        {
            var id = c.GetProperty("element_id").GetInt64();
            var name = c.GetProperty("parameter").GetString()!;
            var value = c.GetProperty("value").GetString() ?? "";
            var onType = c.TryGetProperty("type", out var tp) && tp.GetBoolean();

            var e = doc.GetElement(new ElementId(id));
            if (onType && e != null) e = doc.GetElement(e.GetTypeId());
            var p = e?.LookupParameter(name);

            string status;
            if (e is null) status = "element not found";
            else if (p is null) status = "parameter not found";
            else if (p.IsReadOnly) status = "parameter is read-only";
            else status = Write(p, value) ? "ok" : "value rejected";

            results.Add(new { element_id = id, parameter = name, status });
        }

        t.Commit();
        return ToolResult.Ok(results);
    }

    private static bool Write(Parameter p, string value) => p.StorageType switch
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
        "(see get_model_context). Use list_types with category 'Walls' to pick a wall type.";
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
      "structural": {"type": "boolean"}
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

        var created = new List<long>();
        using var t = RevitHelpers.StartTransaction(doc, "Create walls");
        foreach (var seg in args.Array("segments"))
        {
            var a = Point(doc, seg.GetProperty("start"), level.Elevation);
            var b = Point(doc, seg.GetProperty("end"), level.Elevation);
            if (a.DistanceTo(b) < doc.Application.ShortCurveTolerance) continue;
            var wall = Wall.Create(doc, Line.CreateBound(a, b), typeId, level.Id, height, 0, false, args.OptBool("structural"));
            created.Add(wall.Id.Value);
        }
        t.Commit();
        return ToolResult.Ok(new { created });
    }

    private static XYZ Point(Document doc, System.Text.Json.JsonElement arr, double z)
    {
        var xy = arr.EnumerateArray().Select(v => RevitHelpers.ToInternal(doc, v.GetDouble())).ToArray();
        return new XYZ(xy[0], xy[1], z);
    }
}
