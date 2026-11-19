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
      "check_only": {"type": "boolean", "description": "true: only say whether it would save or synchronize, and why not when it can't."},
      "force": {"type": "boolean", "description": "true: save even when no open model links this one (only when the user asked for a save)."}
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
        // Saving a big model holds Revit for a minute or two, and jobs called this at the end of every run although no model
        // linked theirs (22 saves of ~100 s in two days, the other jobs' calls waiting behind each): only when a model open in
        // this Revit links it, or another Revit window is open (its links can't be seen from here).
        if (!args.OptBool("force") && !MayBeLinked(app, doc))
            return ToolResult.Ok(new
            {
                model = doc.Title, saved = false,
                note = "Not needed: no other open model links this one, so nothing would reload it. Not saved (that takes a minute or two); " +
                       "saving and synchronizing stay with the user. Pass force=true only if the user asked for a save.",
            });
        if (shared)
        {
            if (AutoSync.SyncNow(doc) is { } failed) return ToolResult.Error($"\"{doc.Title}\" couldn't be synchronized with central: {failed}");
            return ToolResult.Ok(new { model = doc.Title, synchronized = true, prompt = (AutoSync.LastPrompt ?? "").Trim(), next = "Models that link this one can now reload_links to see the changes." });
        }
        if (!doc.IsModified) return ToolResult.Ok(new { model = doc.Title, saved = false, note = "Nothing to save: the file already has every change." });
        // Revit's "Location Position Changed" prompt is answered "Do not save" during our save (PositionPrompt).
        PositionPrompt.During(() => { doc.Save(); return 0; }, out var asked);
        return ToolResult.Ok(new { model = doc.Title, saved = true, prompt = PositionPrompt.Said(asked).Trim(), next = "Models that link this one can now reload_links to see the changes." });
    }

    /// <summary>
    /// True when a model that links <paramref name="doc"/> may be open: one open in this Revit links a file of the same name,
    /// or another Revit window is running (what it links can't be seen from here, so it may).
    /// </summary>
    internal static bool MayBeLinked(UIApplication app, Document doc)
    {
        try
        {
            var me = System.Diagnostics.Process.GetCurrentProcess();
            var others = System.Diagnostics.Process.GetProcessesByName(me.ProcessName).Count(p => { using (p) return p.Id != me.Id; });
            if (others > 0) return true;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { System.IO.Path.GetFileNameWithoutExtension(doc.PathName) };
            if (doc.IsWorkshared)
            {
                try { names.Add(System.IO.Path.GetFileNameWithoutExtension(ModelPathUtils.ConvertModelPathToUserVisiblePath(doc.GetWorksharingCentralModelPath()))); }
                catch (Exception) { }
            }
            foreach (Document other in app.Application.Documents)
            {
                if (other.Equals(doc) || other.IsLinked || other.IsFamilyDocument) continue;
                foreach (var type in new FilteredElementCollector(other).OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>())
                {
                    try
                    {
                        var path = type.GetExternalFileReference()?.GetAbsolutePath() is { } mp ? ModelPathUtils.ConvertModelPathToUserVisiblePath(mp) : type.Name;
                        if (names.Contains(System.IO.Path.GetFileNameWithoutExtension(path))) return true;
                    }
                    catch (Exception) { return true; }
                }
            }
            return false;
        }
        catch (Exception) { return true; } // unsure: save, as asked
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
