using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

public sealed class CreatePlanViewsTool : IRevitTool
{
    public string Name => "create_plan_views";
    public string Description =>
        "Create floor, ceiling or structural plans for one or more levels, optionally applying a view template, " +
        "a scale and a name pattern where {level} is replaced by the level name (e.g. 'A - {level} - Furniture Plan'). " +
        "Structural plans are engineering plans looking down, with a view range set from top_offset, cut_offset and " +
        "view_depth (defaults +2300, +1200 and -1500 mm from the level, so beams under the slab show); the result says " +
        "when a view template controls the view range instead.";
    public string PropertiesJson => """
    {
      "levels": {"type": "array", "items": {"type": "string"}, "description": "Level names. Empty = all levels."},
      "kind": {"type": "string", "enum": ["floor", "ceiling", "structural"]},
      "name_pattern": {"type": "string"},
      "view_template": {"type": "string", "description": "View template name to apply."},
      "scale": {"type": "integer", "description": "e.g. 100 for 1:100. Ignored when the template controls scale."},
      "top_offset": {"type": "number", "description": "View range top above the level, project units (structural default 2300 mm)."},
      "cut_offset": {"type": "number", "description": "Cut plane above the level, project units (structural default 1200 mm)."},
      "view_depth": {"type": "number", "description": "Bottom and view depth relative to the level, project units, usually negative (structural default -1500 mm)."}
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
        // A structural plan type whose name says it looks up (reflected) is used only when there is no other.
        var vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
            .Where(t => t.ViewFamily == family)
            .OrderBy(t => family == ViewFamily.StructuralPlan && new[] { "Reflected", "Ceiling", "Up", "RCP" }.Any(w => ModelRules.FirstWord(t.Name, [w]) != null) ? 1 : 0)
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"No view type for {family} in this model.");
        var structural = family == ViewFamily.StructuralPlan;
        double? Offset(string key, double defaultMm) =>
            args.OptDouble(key) is { } v ? RevitHelpers.ToInternal(doc, v) : structural ? defaultMm / 304.8 : null;
        var top = Offset("top_offset", 2300);
        var cut = Offset("cut_offset", 1200);
        var depth = Offset("view_depth", -1500);
        if (top is { } tv && cut is { } cv && depth is { } dv && !(tv >= cv && cv >= dv))
            return ToolResult.Error("The view range must run top_offset >= cut_offset >= view_depth (all from the level).");

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
            var rangeNote = top is null && cut is null && depth is null ? null : SetRange(view, level, top, cut, depth, structural);
            created.Add(new { id = view.Id.Value, name = view.Name, level = level.Name, viewType = view.ViewType.ToString(), viewRange = rangeNote });
        }
        t.Commit();
        return ToolResult.Ok(new { created });
    }

    /// <summary>
    /// Structural plans look down (some types default to looking up), then the view range is set from the level.
    /// Returns what was set, or why it could not be.
    /// </summary>
    private static string SetRange(ViewPlan view, Level level, double? top, double? cut, double? depth, bool structural)
    {
        var doc = view.Document;
        var notes = new List<string>();
        if (structural)
        {
            if (view.get_Parameter(BuiltInParameter.PLAN_VIEW_VIEW_DIR) is { IsReadOnly: false } dir && dir.AsInteger() != (int)PlanViewDirection.Down)
                if (!dir.Set((int)PlanViewDirection.Down)) notes.Add("couldn't set the view direction to Down");
            if (view.ViewType != ViewType.EngineeringPlan) notes.Add($"Revit made a {view.ViewType} view, not an engineering plan; check the structural plan view type");
        }
        // A template that includes the view range owns it: say so rather than fight it.
        if (view.ViewTemplateId != ElementId.InvalidElementId && doc.GetElement(view.ViewTemplateId) is View template
            && template.GetNonControlledTemplateParameterIds().All(id => id.Value != (long)BuiltInParameter.PLAN_VIEW_RANGE))
        {
            notes.Add($"view range not set: the view template '{template.Name}' controls it");
            return string.Join("; ", notes);
        }
        try
        {
            var range = view.GetViewRange();
            var c = cut ?? range.GetOffset(PlanViewPlane.CutPlane);
            var tp = Math.Max(top ?? range.GetOffset(PlanViewPlane.TopClipPlane), c);
            var d = Math.Min(depth ?? range.GetOffset(PlanViewPlane.ViewDepthPlane), c);
            foreach (var plane in new[] { PlanViewPlane.TopClipPlane, PlanViewPlane.CutPlane, PlanViewPlane.BottomClipPlane, PlanViewPlane.ViewDepthPlane })
                range.SetLevelId(plane, level.Id);
            range.SetOffset(PlanViewPlane.TopClipPlane, tp);
            range.SetOffset(PlanViewPlane.CutPlane, c);
            range.SetOffset(PlanViewPlane.BottomClipPlane, d);
            range.SetOffset(PlanViewPlane.ViewDepthPlane, d);
            view.SetViewRange(range);
            string Mm(double v) => $"{(v >= 0 ? "+" : "")}{Math.Round(v * 304.8)}";
            notes.Insert(0, $"top {Mm(tp)}, cut {Mm(c)}, bottom and depth {Mm(d)} mm from {level.Name}");
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or ArgumentException or InvalidOperationException)
        {
            notes.Add($"view range not set: {ex.Message.Trim()}");
        }
        return string.Join("; ", notes);
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
    public string Description => "Assign a view template to views (or remove it with template = ''). Each view is its own step: views another user owns (workshared) or that can't take the template are listed with the reason.";
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
        var ids = args.LongList("view_ids").Distinct().ToList();
        var failed = ids.Where(i => doc.GetElement(new ElementId(i)) is not View).ToList();
        var failures = failed.Select(i => (object)new { item = i.ToString(), reason = "not a view" }).ToList();
        ids = ids.Except(failed).ToList();
        // Each view is its own step: a view another user owns (workshared) is skipped and named, the rest get the template.
        var batch = Batch.InGroup(doc, "Apply view template", () => ItemBatch.Run(doc, "Apply view template", ids, i => i.ToString(), i => [new ElementId(i)], i =>
        {
            var v = (View)doc.GetElement(new ElementId(i));
            if (template is not null && !v.IsValidViewTemplate(template.Id))
                throw new ArgumentException($"'{template.Name}' is a {template.ViewType} template and can't control '{v.Name}' ({v.ViewType}).");
            v.ViewTemplateId = template?.Id ?? ElementId.InvalidElementId;
            return null;
        }), _ => true);
        var applied = batch.Done;
        failed.AddRange(batch.Failed.Select(f => long.Parse(f.Item)));
        failures.AddRange(Batch.Failures(batch));
        return ToolResult.Ok(new { applied, failed, failures });
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
