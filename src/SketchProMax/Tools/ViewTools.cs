using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

public sealed class CreatePlanViewsTool : IRevitTool
{
    public string Name => "create_plan_views";
    public string Description =>
        "Create floor, ceiling or structural plans for one or more levels, optionally applying a view template, " +
        "a scale and a name pattern where {level} is replaced by the level name (e.g. 'A - {level} - Furniture Plan').";
    public string PropertiesJson => """
    {
      "levels": {"type": "array", "items": {"type": "string"}, "description": "Level names. Empty = all levels."},
      "kind": {"type": "string", "enum": ["floor", "ceiling", "structural"]},
      "name_pattern": {"type": "string"},
      "view_template": {"type": "string", "description": "View template name to apply."},
      "scale": {"type": "integer", "description": "e.g. 100 for 1:100. Ignored when the template controls scale."}
    }
    """;
    public string[] Required => ["kind"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var family = args.String("kind") switch
        {
            "ceiling" => ViewFamily.CeilingPlan,
            "structural" => ViewFamily.StructuralPlan,
            _ => ViewFamily.FloorPlan,
        };
        var vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
            .FirstOrDefault(t => t.ViewFamily == family)
            ?? throw new InvalidOperationException($"No view type for {family} in this model.");

        var names = args.StringList("levels");
        var allLevels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
        var levels = allLevels.Where(l => names.Count == 0 || names.Contains(l.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        if (names.Count > 0 && levels.Count == 0)
            return ToolResult.Error($"None of the levels {string.Join(", ", names.Select(n => $"'{n}'"))} exist. Available levels: {string.Join(", ", allLevels.Select(l => l.Name))}.");
        var template = ViewTemplates.Find(doc, args.OptString("view_template"));
        var pattern = args.OptString("name_pattern");

        var created = new List<object>();
        using var t = RevitHelpers.StartTransaction(doc, "Create plan views");
        foreach (var level in levels)
        {
            var view = ViewPlan.Create(doc, vft.Id, level.Id);
            if (pattern != null)
            {
                try { view.Name = pattern.Replace("{level}", level.Name); }
                catch (Autodesk.Revit.Exceptions.ArgumentException) { /* duplicate name: keep Revit's default */ }
            }
            if (args.OptLong("scale") is { } scale) view.Scale = (int)scale;
            if (template != null) view.ViewTemplateId = template.Id;
            created.Add(new { id = view.Id.Value, name = view.Name, level = level.Name });
        }
        t.Commit();
        return ToolResult.Ok(new { created });
    }
}

public sealed class DuplicateViewsTool : IRevitTool
{
    public string Name => "duplicate_views";
    public string Description =>
        "Duplicate views: 'duplicate' (model only), 'with_detailing' (model + annotation) or 'dependent'. " +
        "New names are the original name plus the suffix.";
    public string PropertiesJson => """
    {
      "view_ids": {"type": "array", "items": {"type": "integer"}},
      "mode": {"type": "string", "enum": ["duplicate", "with_detailing", "dependent"]},
      "suffix": {"type": "string", "description": "Appended to the name, e.g. ' - Furniture'."}
    }
    """;
    public string[] Required => ["view_ids", "mode"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var option = args.String("mode") switch
        {
            "with_detailing" => ViewDuplicateOption.WithDetailing,
            "dependent" => ViewDuplicateOption.AsDependent,
            _ => ViewDuplicateOption.Duplicate,
        };
        var suffix = args.OptString("suffix");
        var results = new List<object>();
        using var t = RevitHelpers.StartTransaction(doc, "Duplicate views");
        foreach (var id in args.LongList("view_ids"))
        {
            if (doc.GetElement(new ElementId(id)) is not View v || !v.CanViewBeDuplicated(option))
            {
                results.Add(new { id, error = "cannot be duplicated this way" });
                continue;
            }
            var copy = (View)doc.GetElement(v.Duplicate(option));
            if (suffix != null)
            {
                try { copy.Name = v.Name + suffix; } catch (Autodesk.Revit.Exceptions.ArgumentException) { }
            }
            results.Add(new { source = id, id = copy.Id.Value, name = copy.Name });
        }
        t.Commit();
        return ToolResult.Ok(results);
    }
}

public sealed class ApplyViewTemplateTool : IRevitTool
{
    public string Name => "apply_view_template";
    public string Description => "Assign a view template to views (or remove it with template = '').";
    public string PropertiesJson => """
    { "view_ids": {"type": "array", "items": {"type": "integer"}}, "template": {"type": "string"} }
    """;
    public string[] Required => ["view_ids", "template"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var name = args.String("template");
        var template = name.Length == 0 ? null : ViewTemplates.Find(doc, name);
        int applied = 0;
        var failed = new List<long>();
        using var t = RevitHelpers.StartTransaction(doc, "Apply view template");
        foreach (var id in args.LongList("view_ids"))
        {
            if (doc.GetElement(new ElementId(id)) is View v &&
                (template is null || v.IsValidViewTemplate(template.Id)))
            {
                v.ViewTemplateId = template?.Id ?? ElementId.InvalidElementId;
                applied++;
            }
            else failed.Add(id);
        }
        t.Commit();
        return ToolResult.Ok(new { applied, failed });
    }
}

public sealed class ListViewTemplatesTool : IRevitTool
{
    public string Name => "list_view_templates";
    public string Description => "List view templates (id, name, view type) available in the model.";
    public string PropertiesJson => "{}";
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        return ToolResult.Ok(new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
            .Where(v => v.IsTemplate).OrderBy(v => v.Name)
            .Select(v => new { id = v.Id.Value, name = v.Name, type = v.ViewType.ToString() }));
    }
}

internal static class ViewTemplates
{
    public static View? Find(Document doc, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                   .FirstOrDefault(v => v.IsTemplate && string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase))
               ?? throw new ArgumentException($"No view template named '{name}'.");
    }
}
