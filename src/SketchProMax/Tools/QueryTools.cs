using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

public sealed class GetModelContextTool : IRevitTool
{
    public string Name => "get_model_context";
    public string Description =>
        "Summarize the open Revit model: title, length unit, active view, levels with elevations, " +
        "current selection, and element counts for the main categories. Call this first in a new conversation.";
    public string PropertiesJson => "{}";
    public string[] Required => [];
    public bool ModifiesModel => false;

    private static readonly BuiltInCategory[] Counted =
    [
        BuiltInCategory.OST_Walls, BuiltInCategory.OST_Doors, BuiltInCategory.OST_Windows,
        BuiltInCategory.OST_Rooms, BuiltInCategory.OST_Floors, BuiltInCategory.OST_Ceilings,
        BuiltInCategory.OST_Roofs, BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_StructuralFraming,
        BuiltInCategory.OST_MechanicalEquipment, BuiltInCategory.OST_DuctCurves, BuiltInCategory.OST_PipeCurves,
        BuiltInCategory.OST_Sheets, BuiltInCategory.OST_RvtLinks,
    ];

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var uidoc = app.ActiveUIDocument ?? throw new InvalidOperationException("No model is open.");
        var doc = uidoc.Document;
        var view = doc.ActiveView;

        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
            .OrderBy(l => l.Elevation)
            .Select(l => new { id = l.Id.Value, name = l.Name, elevation = RevitHelpers.FromInternal(doc, l.Elevation) });

        var counts = Counted.ToDictionary(
            c => c.ToString().Replace("OST_", ""),
            c => new FilteredElementCollector(doc).OfCategory(c).WhereElementIsNotElementType().GetElementCount());

        var selection = uidoc.Selection.GetElementIds().Take(200)
            .Select(id => RevitHelpers.ElementSummary(doc, doc.GetElement(id)));

        return ToolResult.Ok(new
        {
            title = doc.Title,
            path = doc.PathName,
            isWorkshared = doc.IsWorkshared,
            lengthUnit = RevitHelpers.LengthUnitLabel(doc),
            activeView = new { id = view.Id.Value, name = view.Name, type = view.ViewType.ToString(), level = view.GenLevel?.Name },
            levels,
            selection,
            counts,
        });
    }
}

public sealed class QueryElementsTool : IRevitTool
{
    public string Name => "query_elements";
    public string Description =>
        "Find elements by category, optionally filtered by level, by a parameter value, or to the active view. " +
        "Returns id, family, type and level, plus any extra parameters you name. Use for questions like " +
        "'how many fire-rated doors are on Level 2'.";
    public string PropertiesJson => """
    {
      "category": {"type": "string", "description": "Revit category, e.g. 'Doors', 'Walls', 'OST_Rooms'."},
      "level": {"type": "string", "description": "Only elements on this level name."},
      "in_active_view": {"type": "boolean", "description": "Only elements visible in the active view."},
      "parameter_filter": {
        "type": "object",
        "description": "Keep elements whose parameter (instance or type) display value contains this text, case-insensitive.",
        "properties": {"name": {"type": "string"}, "contains": {"type": "string"}},
        "required": ["name", "contains"]
      },
      "include_parameters": {"type": "array", "items": {"type": "string"}, "description": "Parameter names to return for each element."},
      "limit": {"type": "integer", "description": "Max elements to return (default 200)."}
    }
    """;
    public string[] Required => ["category"];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var bic = RevitHelpers.ParseCategory(doc, args.String("category"));

        var collector = args.OptBool("in_active_view")
            ? new FilteredElementCollector(doc, doc.ActiveView.Id)
            : new FilteredElementCollector(doc);
        IEnumerable<Element> elements = collector.OfCategory(bic).WhereElementIsNotElementType().ToElements();

        if (args.OptString("level") is { } levelName)
        {
            var level = RevitHelpers.FindLevel(doc, levelName);
            elements = elements.Where(e => e.LevelId == level.Id);
        }

        if (args.Has("parameter_filter"))
        {
            var f = args.Raw["parameter_filter"];
            var pname = f.GetProperty("name").GetString()!;
            var needle = f.GetProperty("contains").GetString()!;
            elements = elements.Where(e =>
            {
                var p = e.LookupParameter(pname) ?? doc.GetElement(e.GetTypeId())?.LookupParameter(pname);
                var v = p is null ? null : RevitHelpers.ParamValue(p);
                return v != null && v.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
            });
        }

        var extra = args.StringList("include_parameters");
        var all = elements.ToList();
        var limit = (int)(args.OptLong("limit") ?? 200);

        var rows = all.Take(limit).Select(e => new
        {
            summary = RevitHelpers.ElementSummary(doc, e),
            parameters = extra.ToDictionary(n => n, n =>
            {
                var p = e.LookupParameter(n) ?? doc.GetElement(e.GetTypeId())?.LookupParameter(n);
                return p is null ? null : RevitHelpers.ParamValue(p);
            }),
        });

        return ToolResult.Ok(new { total = all.Count, returned = Math.Min(limit, all.Count), elements = rows });
    }
}

public sealed class GetElementParametersTool : IRevitTool
{
    public string Name => "get_element_parameters";
    public string Description =>
        "Return every instance and type parameter (name, value, storage type, read-only flag) for up to 20 elements.";
    public string PropertiesJson => """
    { "element_ids": {"type": "array", "items": {"type": "integer"}, "maxItems": 20} }
    """;
    public string[] Required => ["element_ids"];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var result = args.LongList("element_ids").Take(20).Select(id =>
        {
            var e = doc.GetElement(new ElementId(id));
            if (e is null) return (object)new { id, error = "not found" };
            var type = doc.GetElement(e.GetTypeId());
            return new
            {
                summary = RevitHelpers.ElementSummary(doc, e),
                instance = Dump(e),
                type = type is null ? null : Dump(type),
            };
        });
        return ToolResult.Ok(result);
    }

    private static IEnumerable<object> Dump(Element e) =>
        e.Parameters.Cast<Parameter>()
            .Where(p => p.Definition != null)
            .OrderBy(p => p.Definition.Name)
            .Select(p => new
            {
                name = p.Definition.Name,
                value = RevitHelpers.ParamValue(p),
                storage = p.StorageType.ToString(),
                readOnly = p.IsReadOnly,
            });
}

public sealed class ListViewsAndSheetsTool : IRevitTool
{
    public string Name => "list_views_and_sheets";
    public string Description =>
        "List sheets (number, name, placed views) and non-template views (name, type, level, whether placed on a sheet).";
    public string PropertiesJson => """
    { "view_type": {"type": "string", "description": "Optional filter, e.g. 'FloorPlan', 'Section', 'Elevation', 'ThreeD', 'Schedule'."} }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
            .OrderBy(s => s.SheetNumber).ToList();
        var placed = sheets.SelectMany(s => s.GetAllPlacedViews()).Select(id => id.Value).ToHashSet();

        var filter = args.OptString("view_type");
        var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
            .Where(v => !v.IsTemplate && v is not ViewSheet && v.ViewType != ViewType.ProjectBrowser && v.ViewType != ViewType.SystemBrowser)
            .Where(v => filter is null || string.Equals(v.ViewType.ToString(), filter, StringComparison.OrdinalIgnoreCase))
            .Select(v => new { id = v.Id.Value, name = v.Name, type = v.ViewType.ToString(), level = v.GenLevel?.Name, onSheet = placed.Contains(v.Id.Value) });

        return ToolResult.Ok(new
        {
            sheets = sheets.Select(s => new { id = s.Id.Value, number = s.SheetNumber, name = s.Name, views = s.GetAllPlacedViews().Select(v => doc.GetElement(v)?.Name) }),
            views,
        });
    }
}

public sealed class ListTypesTool : IRevitTool
{
    public string Name => "list_types";
    public string Description =>
        "List the available family types / system types for a category (e.g. Walls, Doors, Title Blocks) " +
        "so you can pick an exact type name before creating elements.";
    public string PropertiesJson => """
    { "category": {"type": "string"} }
    """;
    public string[] Required => ["category"];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var bic = RevitHelpers.ParseCategory(doc, args.String("category"));
        var types = new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsElementType()
            .Cast<ElementType>()
            .Select(t => new { id = t.Id.Value, family = t.FamilyName, type = t.Name })
            .OrderBy(t => t.family).ThenBy(t => t.type);
        return ToolResult.Ok(types);
    }
}

public sealed class SelectElementsTool : IRevitTool
{
    public string Name => "select_elements";
    public string Description => "Select (highlight) elements in the Revit UI so the user can see them. Does not change the model.";
    public string PropertiesJson => """
    { "element_ids": {"type": "array", "items": {"type": "integer"}},
      "zoom": {"type": "boolean", "description": "Also zoom the active view to them."} }
    """;
    public string[] Required => ["element_ids"];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var uidoc = app.ActiveUIDocument;
        var ids = args.LongList("element_ids").Select(i => new ElementId(i))
            .Where(id => uidoc.Document.GetElement(id) != null).ToList();
        uidoc.Selection.SetElementIds(ids);
        if (args.OptBool("zoom") && ids.Count > 0) uidoc.ShowElements(ids);
        return ToolResult.Ok(new { selected = ids.Count });
    }
}
