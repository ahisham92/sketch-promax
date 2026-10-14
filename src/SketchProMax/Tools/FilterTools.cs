using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

internal static class Filters
{
    public static List<ElementId> CategoryIds(Document doc, IEnumerable<string> names) =>
        names.Select(n => new ElementId(RevitHelpers.ParseCategory(doc, n))).ToList();

    /// <summary>Finds a filterable parameter (instance or type) shared by the categories, by its visible name.</summary>
    public static ElementId ParameterId(Document doc, ICollection<ElementId> categories, string name)
    {
        foreach (var id in ParameterFilterUtilities.GetFilterableParametersInCommon(doc, categories))
        {
            if (string.Equals(ParameterName(doc, id), name, StringComparison.OrdinalIgnoreCase)) return id;
        }
        throw new ArgumentException($"'{name}' is not a filterable parameter common to those categories.");
    }

    public static string ParameterName(Document doc, ElementId id) =>
        id.Value < 0 ? LabelUtils.GetLabelFor((BuiltInParameter)(int)id.Value) : doc.GetElement(id)?.Name ?? "";

    /// <summary>A sample parameter instance, to learn its storage and data type.</summary>
    public static Parameter? Sample(Document doc, ICollection<ElementId> categories, ElementId paramId)
    {
        foreach (var cat in categories)
        {
            foreach (var e in new FilteredElementCollector(doc).OfCategoryId(cat).WhereElementIsNotElementType().Take(50))
            {
                var p = Find(e, paramId) ?? (doc.GetElement(e.GetTypeId()) is { } t ? Find(t, paramId) : null);
                if (p != null) return p;
            }
        }
        return null;
    }

    private static Parameter? Find(Element e, ElementId paramId) =>
        e.Parameters.Cast<Parameter>().FirstOrDefault(p => p.Id == paramId);

    public static FilterRule Rule(Document doc, ElementId paramId, Parameter? sample, string op, string value)
    {
        var storage = sample?.StorageType ?? StorageType.String;
        if (storage == StorageType.Double && sample != null &&
            UnitFormatUtils.TryParse(doc.GetUnits(), sample.Definition.GetDataType(), value, out double d))
        {
            const double eps = 1e-6;
            return op switch
            {
                "not_equals" => ParameterFilterRuleFactory.CreateNotEqualsRule(paramId, d, eps),
                "greater" => ParameterFilterRuleFactory.CreateGreaterRule(paramId, d, eps),
                "greater_or_equal" => ParameterFilterRuleFactory.CreateGreaterOrEqualRule(paramId, d, eps),
                "less" => ParameterFilterRuleFactory.CreateLessRule(paramId, d, eps),
                "less_or_equal" => ParameterFilterRuleFactory.CreateLessOrEqualRule(paramId, d, eps),
                _ => ParameterFilterRuleFactory.CreateEqualsRule(paramId, d, eps),
            };
        }
        if (storage == StorageType.Integer && TryInt(value, out int i))
        {
            return op switch
            {
                "not_equals" => ParameterFilterRuleFactory.CreateNotEqualsRule(paramId, i),
                "greater" => ParameterFilterRuleFactory.CreateGreaterRule(paramId, i),
                "greater_or_equal" => ParameterFilterRuleFactory.CreateGreaterOrEqualRule(paramId, i),
                "less" => ParameterFilterRuleFactory.CreateLessRule(paramId, i),
                "less_or_equal" => ParameterFilterRuleFactory.CreateLessOrEqualRule(paramId, i),
                _ => ParameterFilterRuleFactory.CreateEqualsRule(paramId, i),
            };
        }
        return op switch
        {
            "not_equals" => ParameterFilterRuleFactory.CreateNotEqualsRule(paramId, value),
            "contains" => ParameterFilterRuleFactory.CreateContainsRule(paramId, value),
            "not_contains" => ParameterFilterRuleFactory.CreateNotContainsRule(paramId, value),
            "begins_with" => ParameterFilterRuleFactory.CreateBeginsWithRule(paramId, value),
            "ends_with" => ParameterFilterRuleFactory.CreateEndsWithRule(paramId, value),
            "greater" => ParameterFilterRuleFactory.CreateGreaterRule(paramId, value),
            "less" => ParameterFilterRuleFactory.CreateLessRule(paramId, value),
            _ => ParameterFilterRuleFactory.CreateEqualsRule(paramId, value),
        };
    }

    private static bool TryInt(string value, out int i)
    {
        if (value.Equals("yes", StringComparison.OrdinalIgnoreCase)) { i = 1; return true; }
        if (value.Equals("no", StringComparison.OrdinalIgnoreCase)) { i = 0; return true; }
        return int.TryParse(value, out i);
    }

    public static Color ParseColor(string hexOrName)
    {
        var s = hexOrName.Trim().TrimStart('#');
        if (s.Length == 6 && int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
            return new Color((byte)(rgb >> 16), (byte)(rgb >> 8 & 0xFF), (byte)(rgb & 0xFF));
        var named = System.Drawing.Color.FromName(hexOrName.Trim());
        if (named.A == 0 && !named.IsKnownColor) throw new ArgumentException($"Unknown color '{hexOrName}'. Use hex like #FF8800.");
        return new Color(named.R, named.G, named.B);
    }

    public static OverrideGraphicSettings Overrides(Document doc, JsonElement spec)
    {
        var ogs = new OverrideGraphicSettings();
        if (spec.TryGetProperty("color", out var c) && c.GetString() is { Length: > 0 } colorText)
        {
            var color = ParseColor(colorText);
            var solid = FillPatternElement.GetFillPatternElementByName(doc, FillPatternTarget.Drafting, "<Solid fill>")
                        ?? new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                            .FirstOrDefault(f => f.GetFillPattern().IsSolidFill);
            ogs.SetProjectionLineColor(color);
            ogs.SetCutLineColor(color);
            if (solid != null)
            {
                ogs.SetSurfaceForegroundPatternId(solid.Id);
                ogs.SetSurfaceForegroundPatternColor(color);
                ogs.SetCutForegroundPatternId(solid.Id);
                ogs.SetCutForegroundPatternColor(color);
            }
        }
        if (spec.TryGetProperty("halftone", out var h)) ogs.SetHalftone(h.GetBoolean());
        if (spec.TryGetProperty("transparency", out var tr)) ogs.SetSurfaceTransparency(Math.Max(0, Math.Min(100, tr.GetInt32())));
        if (spec.TryGetProperty("line_weight", out var lw)) ogs.SetProjectionLineWeight(lw.GetInt32());
        return ogs;
    }

    public static void Apply(View view, ElementId filterId, OverrideGraphicSettings ogs, bool visible)
    {
        if (!view.GetFilters().Contains(filterId)) view.AddFilter(filterId);
        view.SetFilterOverrides(filterId, ogs);
        view.SetFilterVisibility(filterId, visible);
    }

    // A palette that stays distinguishable on white drawings.
    public static readonly string[] Palette =
    [
        "#E6194B", "#3CB44B", "#4363D8", "#F58231", "#911EB4", "#42D4F4", "#F032E6", "#BFEF45",
        "#9A6324", "#469990", "#DCBEFF", "#800000", "#AAFFC3", "#808000", "#FFD8B1", "#000075",
    ];
}

public sealed class ListViewFiltersTool : IRevitTool
{
    public string Name => "list_view_filters";
    public string Description => "List view filters in the model: name, categories, and which views/templates use them.";
    public string PropertiesJson => "{}";
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
            .Where(v => v.AreGraphicsOverridesAllowed()).ToList();
        return ToolResult.Ok(new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement))
            .Cast<ParameterFilterElement>().Select(f => new
            {
                id = f.Id.Value,
                name = f.Name,
                categories = f.GetCategories().Select(c => Category.GetCategory(doc, c)?.Name),
                usedIn = views.Where(v => v.GetFilters().Contains(f.Id)).Select(v => v.Name),
            }));
    }
}

public sealed class CreateViewFilterTool : IRevitTool
{
    public string Name => "create_view_filter";
    public string Description =>
        "Create a rule-based view filter (e.g. 'Doors with Fire Rating = 60 min', 'Walls whose Type Name contains EXT'). " +
        "All rules must match (AND). Operators: equals, not_equals, contains, not_contains, begins_with, ends_with, " +
        "greater, greater_or_equal, less, less_or_equal. Values are typed as in Revit (e.g. '2100 mm', 'Yes'). " +
        "Optionally apply it straight away to views or view templates with a color, halftone, transparency or hide.";
    public string PropertiesJson => """
    {
      "name": {"type": "string"},
      "categories": {"type": "array", "items": {"type": "string"}},
      "rules": {"type": "array", "items": {"type": "object", "properties": {
        "parameter": {"type": "string"}, "operator": {"type": "string"}, "value": {"type": "string"}},
        "required": ["parameter", "operator", "value"]}},
      "apply_to_view_ids": {"type": "array", "items": {"type": "integer"}, "description": "Views or view templates."},
      "color": {"type": "string", "description": "Hex like #FF0000 or a color name."},
      "halftone": {"type": "boolean"},
      "transparency": {"type": "integer"},
      "visible": {"type": "boolean", "description": "false hides matching elements (default true)."}
    }
    """;
    public string[] Required => ["name", "categories"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var cats = Filters.CategoryIds(doc, args.StringList("categories"));
        if (!ParameterFilterElement.IsNameUnique(doc, args.String("name")))
            return ToolResult.Error($"A filter named '{args.String("name")}' already exists.");

        var rules = new List<FilterRule>();
        foreach (var r in args.Array("rules"))
        {
            var pid = Filters.ParameterId(doc, cats, r.GetProperty("parameter").GetString()!);
            rules.Add(Filters.Rule(doc, pid, Filters.Sample(doc, cats, pid),
                r.GetProperty("operator").GetString() ?? "equals", r.GetProperty("value").GetString() ?? ""));
        }

        using var t = RevitHelpers.StartTransaction(doc, $"Create filter {args.String("name")}");
        var filter = rules.Count == 0
            ? ParameterFilterElement.Create(doc, args.String("name"), cats)
            : ParameterFilterElement.Create(doc, args.String("name"), cats, new ElementParameterFilter(rules));

        var ogs = Filters.Overrides(doc, JsonDocument.Parse(JsonSerializer.Serialize(args.Raw)).RootElement);
        var applied = new List<string>();
        foreach (var id in args.LongList("apply_to_view_ids"))
        {
            if (doc.GetElement(new ElementId(id)) is View v && v.AreGraphicsOverridesAllowed())
            {
                Filters.Apply(v, filter.Id, ogs, args.OptBool("visible", true));
                applied.Add(v.Name);
            }
        }
        t.Commit();
        return ToolResult.Ok(new { filterId = filter.Id.Value, appliedTo = applied });
    }
}

public sealed class ApplyViewFilterTool : IRevitTool
{
    public string Name => "apply_view_filter";
    public string Description =>
        "Apply an existing view filter to views or view templates with graphics: color (solid fill + lines), " +
        "halftone, transparency, line weight, or hide matching elements.";
    public string PropertiesJson => """
    {
      "filter": {"type": "string", "description": "Filter name."},
      "view_ids": {"type": "array", "items": {"type": "integer"}},
      "color": {"type": "string"}, "halftone": {"type": "boolean"}, "transparency": {"type": "integer"},
      "line_weight": {"type": "integer"}, "visible": {"type": "boolean"}
    }
    """;
    public string[] Required => ["filter", "view_ids"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var filter = new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement))
            .FirstOrDefault(f => string.Equals(f.Name, args.String("filter"), StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"No filter named '{args.String("filter")}'.");
        var ogs = Filters.Overrides(doc, JsonDocument.Parse(JsonSerializer.Serialize(args.Raw)).RootElement);
        var applied = new List<string>();
        using var t = RevitHelpers.StartTransaction(doc, $"Apply filter {filter.Name}");
        foreach (var id in args.LongList("view_ids"))
        {
            if (doc.GetElement(new ElementId(id)) is View v && v.AreGraphicsOverridesAllowed())
            {
                Filters.Apply(v, filter.Id, ogs, args.OptBool("visible", true));
                applied.Add(v.Name);
            }
        }
        t.Commit();
        return ToolResult.Ok(new { appliedTo = applied });
    }
}

public sealed class ColorByParameterTool : IRevitTool
{
    public string Name => "color_by_parameter";
    public string Description =>
        "Color-code a view by a parameter's values: creates one filter per distinct value (e.g. doors by Fire Rating, " +
        "walls by Type, rooms by Department), each with its own color, and applies them to the given views. " +
        "Returns the value-to-color legend.";
    public string PropertiesJson => """
    {
      "categories": {"type": "array", "items": {"type": "string"}},
      "parameter": {"type": "string"},
      "view_ids": {"type": "array", "items": {"type": "integer"}, "description": "Default: active view."},
      "name_prefix": {"type": "string", "description": "Filter name prefix (default: the parameter name)."},
      "colors": {"type": "object", "additionalProperties": {"type": "string"}, "description": "Optional value -> color map."}
    }
    """;
    public string[] Required => ["categories", "parameter"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var cats = Filters.CategoryIds(doc, args.StringList("categories"));
        var pname = args.String("parameter");
        var pid = Filters.ParameterId(doc, cats, pname);

        // Collect distinct display values from instances (falling back to their types).
        var values = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cat in cats)
            foreach (var e in new FilteredElementCollector(doc).OfCategoryId(cat).WhereElementIsNotElementType())
            {
                var p = e.Parameters.Cast<Parameter>().FirstOrDefault(x => x.Id == pid)
                        ?? doc.GetElement(e.GetTypeId())?.Parameters.Cast<Parameter>().FirstOrDefault(x => x.Id == pid);
                var v = p is null ? null : (p.StorageType == StorageType.String ? p.AsString() : p.AsValueString());
                if (!string.IsNullOrWhiteSpace(v)) values.Add(v!);
            }
        if (values.Count == 0) return ToolResult.Error($"No elements have a value for '{pname}'.");
        if (values.Count > 40) return ToolResult.Error($"'{pname}' has {values.Count} distinct values; too many to color. Pick a coarser parameter.");

        var sample = Filters.Sample(doc, cats, pid);
        var views = args.LongList("view_ids").Select(i => doc.GetElement(new ElementId(i))).OfType<View>().ToList();
        if (views.Count == 0) views.Add(doc.ActiveView);
        var prefix = args.OptString("name_prefix") ?? pname;
        var custom = args.Has("colors") ? args.Raw["colors"].EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.OrdinalIgnoreCase) : [];

        var legend = new List<object>();
        int index = 0;
        using var t = RevitHelpers.StartTransaction(doc, $"Color by {pname}");
        foreach (var value in values)
        {
            var name = $"{prefix} = {value}";
            var existing = new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement))
                .Cast<ParameterFilterElement>().FirstOrDefault(f => f.Name == name);
            var rule = Filters.Rule(doc, pid, sample, "equals", value);
            var filter = existing ?? ParameterFilterElement.Create(doc, name, cats, new ElementParameterFilter(rule));

            var color = custom.TryGetValue(value, out var c) ? c : Filters.Palette[index++ % Filters.Palette.Length];
            var ogs = Filters.Overrides(doc, JsonDocument.Parse(JsonSerializer.Serialize(new { color })).RootElement);
            foreach (var v in views.Where(v => v.AreGraphicsOverridesAllowed())) Filters.Apply(v, filter.Id, ogs, true);
            legend.Add(new { value, color, filter = name });
        }
        t.Commit();
        return ToolResult.Ok(new { views = views.Select(v => v.Name), legend });
    }
}
