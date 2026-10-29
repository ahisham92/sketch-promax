using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

/// <summary>
/// Working on a model and a model that links it (a documentation model, open in another Revit window): save_model puts
/// the changes in the file (or synchronizes a local copy with central), then reload_links in the linking model shows them.
/// </summary>
public sealed class SaveModelTool : IRevitTool
{
    public string Name => "save_model";
    public string Description =>
        "Save the open model, or synchronize it with central when it is a local copy of a central model, so that another model " +
        "linking it (for example the documentation model, open in another Revit window) can reload_links and see the changes. " +
        "check_only=true says what it would do. Use it when a change must reach the models that link this one, not after every step.";
    public string PropertiesJson => """
    {
      "check_only": {"type": "boolean", "description": "true: only say whether it would save or synchronize, and why not when it can't."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var shared = AutoSync.Shared(doc);
        string? problem = doc.IsFamilyDocument ? "This is a family, not a project."
            : doc.PathName.Length == 0 ? "This model has never been saved to a file: ask the user to Save As first."
            : doc.IsReadOnly ? "This model is open read-only."
            : doc.IsModifiable ? "A change is still open in this model; try again when it has finished."
            : null;
        var does = shared ? "synchronize with central" : "save";
        if (args.OptBool("check_only")) return ToolResult.Ok(new { model = doc.Title, would = does, can = problem is null, why_not = problem });
        if (problem is not null) return ToolResult.Error(problem);
        if (shared)
        {
            if (AutoSync.SyncNow(doc) is { } failed) return ToolResult.Error($"\"{doc.Title}\" couldn't be synchronized with central: {failed}");
            return ToolResult.Ok(new { model = doc.Title, synchronized = true, next = "Models that link this one can now reload_links to see the changes." });
        }
        if (!doc.IsModified) return ToolResult.Ok(new { model = doc.Title, saved = false, note = "Nothing to save: the file already has every change." });
        doc.Save();
        return ToolResult.Ok(new { model = doc.Title, saved = true, next = "Models that link this one can now reload_links to see the changes." });
    }
}

public sealed class ReloadLinksTool : IRevitTool
{
    public string Name => "reload_links";
    public string Description =>
        "Reload the Revit models linked into the open model from their files, so changes saved in a linked model (for example the " +
        "working model, open in another Revit window, after save_model there) show here: in views, tags, schedules and sheets. " +
        "link: only links whose name contains it. check_only=true lists what would reload. A link loads what was last saved " +
        "(or synchronized), so save the linked model first.";
    public string PropertiesJson => """
    {
      "link": {"type": "string", "description": "Part of the link's name; omit for every Revit link."},
      "check_only": {"type": "boolean", "description": "true: only list the links that would reload."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var wanted = args.OptString("link")?.Trim() ?? "";
        var types = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>().Where(t => !t.IsNestedLink).ToList();
        if (types.Count == 0) return ToolResult.Ok(new { reloaded = Array.Empty<object>(), note = "This model links no Revit models." });
        var picked = wanted.Length == 0 ? types : types.Where(t => t.Name.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        if (picked.Count == 0) return ToolResult.Error($"No link's name contains '{wanted}'. Links: {string.Join(", ", types.Select(t => t.Name))}.");
        if (args.OptBool("check_only")) return ToolResult.Ok(new { would_reload = picked.Select(t => t.Name) });
        var done = new List<object>();
        foreach (var type in picked)
        {
            try
            {
                var result = type.Reload();
                done.Add(new { link = type.Name, result = result.LoadResult.ToString() });
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or InvalidOperationException)
            {
                done.Add(new { link = type.Name, result = "not reloaded: " + ex.Message });
            }
        }
        return ToolResult.Ok(new { model = doc.Title, reloaded = done });
    }
}
