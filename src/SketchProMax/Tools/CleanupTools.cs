using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SketchProMax.Bridge;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

/// <summary>
/// What the purge and cleanup tools share: a preview by default (the deletes are made in a transaction that is rolled
/// back, so the counts, later passes and what else goes with them are exact), apply to do it as one Undo step, repeat
/// passes until nothing new turns up, keep and only name lists, workshared elements another user owns left and named,
/// Cancel and the job card's Stop between chunks, and a paged result with before/after counts and the file size.
/// </summary>
internal static class Cleanup
{
    /// <summary>One thing to delete: its id, the group it is counted under ("materials", "floor plans"...) and its name.</summary>
    internal sealed record Item(ElementId Id, string Group, string Name);

    /// <summary>
    /// What a tool deletes: <see cref="Collect"/> lists the candidates of a pass (called again after each pass, so what
    /// became unused is found), <see cref="Protect"/> says why one must stay (null: it may go).
    /// </summary>
    internal sealed record Spec(string Title, int MaxPasses, Func<Document, int, IEnumerable<Item>> Collect, Func<Item, string?>? Protect,
        List<string> Keep, List<string> Only);

    /// <summary>Why something stayed: the counted reason ("kept by name"...) and the detail for that element.</summary>
    internal sealed record Left(Item Item, string Reason, string Why);

    internal sealed class Outcome
    {
        public readonly List<Item> Deleted = [];
        public readonly HashSet<long> DeletedIds = [];
        public readonly List<Left> Stayed = [];
        /// <summary>Elements deleted along with the candidates (preview: described after the rollback).</summary>
        public readonly HashSet<long> Also = [];
        public readonly Dictionary<string, int> AlsoByCategory = new(StringComparer.Ordinal);
        public int Passes, Before, After;
        public string? Stopped;
    }

    public const string Kept = "kept by name", Owned = "owned by someone else", Protected = "protected", Refused = "refused by Revit";

    /// <summary>The field test's hook: an owner for an element (as if another user had borrowed it), or null. Null hook: the real check.</summary>
    internal static Func<ElementId, string?>? TestOwner;

    /// <summary>The properties every cleanup tool has, after its own.</summary>
    public const string CommonProperties = """
      "apply": {"type": "boolean", "description": "false (default): a preview, nothing in the model changes. true: delete what the preview lists, as one Undo step (Ctrl+Z)."},
      "keep": {"type": "array", "items": {"type": "string"}, "description": "Names never to delete: text the name contains, or a pattern with * (e.g. \"*Title*\", \"_keep\"). Case-insensitive."},
      "only": {"type": "array", "items": {"type": "string"}, "description": "Only names like these (same patterns as keep); everything else is left out of the run."},
    """ + Paging.Properties;

    public static bool Applying(ToolArgs args) => args.OptBool("apply");

    /// <summary>True when the element (or category, for a line style) is still in the model.</summary>
    public static bool Exists(Document doc, ElementId id) =>
        doc.GetElement(id) is not null || SafeCategory(doc, id) is not null;

    private static Category? SafeCategory(Document doc, ElementId id)
    {
        try { return Category.GetCategory(doc, id); }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or ArgumentException) { return null; }
    }

    public static int Count(Document doc) =>
        new FilteredElementCollector(doc).WhereElementIsNotElementType().GetElementCount() + new FilteredElementCollector(doc).WhereElementIsElementType().GetElementCount();

    private static string? Owner(Document doc, ElementId id)
    {
        if (TestOwner?.Invoke(id) is { } hook) return hook;
        return doc.IsWorkshared && doc.GetElement(id) is not null ? ItemBatch.Locked(doc, [id]) : null;
    }

    /// <summary>A pass's candidates sorted into what goes and what stays; <paramref name="seen"/> keeps an item from being judged twice.</summary>
    private static List<Item> Filter(Document doc, Spec spec, IEnumerable<Item> items, HashSet<long> seen, List<Left>? stayed)
    {
        var go = new List<Item>();
        foreach (var item in items)
        {
            if (!seen.Add(item.Id.Value)) continue;
            if (spec.Only.Count > 0 && CleanupMath.FirstMatch(item.Name, spec.Only) is null) continue;
            if (CleanupMath.FirstMatch(item.Name, spec.Keep) is { } k) { stayed?.Add(new(item, Kept, $"matches \"{k}\"")); continue; }
            if (spec.Protect?.Invoke(item) is { } why) { stayed?.Add(new(item, Protected, why)); continue; }
            if (Owner(doc, item.Id) is { } owner) { stayed?.Add(new(item, Owned, owner)); continue; }
            go.Add(item);
        }
        return go;
    }

    /// <summary>The first pass's candidates (for the approval: what else a delete takes with it). No transaction is opened.</summary>
    public static List<ElementId> FirstPass(Document doc, Spec spec) =>
        Filter(doc, spec, spec.Collect(doc, 1), [], null).Select(i => i.Id).ToList();

    /// <summary>Runs the passes: in a rolled-back transaction for a preview, as one transaction group (one Undo) to apply.</summary>
    public static Outcome Sweep(Document doc, Spec spec, bool apply)
    {
        if (doc.IsModifiable) throw new InvalidOperationException("A transaction is already open; this tool must run on its own.");
        var o = new Outcome { Before = Count(doc) };
        if (apply)
        {
            Batch.InGroup(doc, spec.Title, () => { Passes(doc, spec, o, true); return o.Deleted.Count; }, n => n > 0);
            o.After = Count(doc);
            return o;
        }
        using (var t = new Transaction(doc, $"Sketch ProMax: {spec.Title} (preview)"))
        {
            var options = t.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(new Quiet());
            options.SetClearAfterRollback(true);
            options.SetForcedModalHandling(false);
            t.SetFailureHandlingOptions(options);
            FailureGuard.Muted++;
            try
            {
                t.Start();
                Passes(doc, spec, o, false);
                o.After = Count(doc);
            }
            finally
            {
                if (t.HasStarted() && !t.HasEnded()) t.RollBack();
                FailureGuard.Muted--;
            }
        }
        // Rolled back: what went along with the candidates is there again to be named.
        foreach (var id in o.Also)
            if (doc.GetElement(new ElementId(id)) is { } e && e.Category is { } c && c.CategoryType is CategoryType.Model or CategoryType.Annotation)
                o.AlsoByCategory[c.Name] = o.AlsoByCategory.TryGetValue(c.Name, out var n) ? n + 1 : 1;
        return o;
    }

    private static void Passes(Document doc, Spec spec, Outcome o, bool apply)
    {
        var seen = new HashSet<long>();
        for (var pass = 1; pass <= Math.Max(1, spec.MaxPasses); pass++)
        {
            if (CancelPoint.Requested) { o.Stopped = CancelPoint.Stopped + $" after pass {pass - 1}; what was found so far is shown."; break; }
            var go = Filter(doc, spec, spec.Collect(doc, pass), seen, o.Stayed);
            if (go.Count == 0) break;
            o.Passes = pass;
            var before = o.Deleted.Count;
            if (apply) DeleteApply(doc, spec.Title, go, o);
            else DeletePreview(doc, spec.Title, go, o);
            if (o.Stopped is not null || o.Deleted.Count == before) break;
            if (!apply) doc.Regenerate();
        }
    }

    private const int ChunkSize = 50;

    private static readonly ICollection<ElementId> None = Array.Empty<ElementId>();

    /// <summary>Preview: deletes chunk by chunk in sub-transactions of the open (rolled-back) transaction; a refused chunk one by one.</summary>
    private static void DeletePreview(Document doc, string title, List<Item> go, Outcome o)
    {
        var chunks = CleanupMath.Chunks(go, ChunkSize);
        for (var i = 0; i < chunks.Count; i++)
        {
            if (CancelPoint.Stop(title, i, chunks.Count)) { o.Stopped = CancelPoint.Stopped + "; the preview covers what was looked at so far."; return; }
            if (!TryDelete(doc, chunks[i], o))
                foreach (var item in chunks[i])
                    if (!TryDelete(doc, [item], o, out var why)) o.Stayed.Add(new(item, Refused, why));
        }
    }

    private static bool TryDelete(Document doc, List<Item> items, Outcome o) => TryDelete(doc, items, o, out _);

    private static bool TryDelete(Document doc, List<Item> items, Outcome o, out string why)
    {
        why = "";
        var asked = items.Where(i => Exists(doc, i.Id)).Select(i => i.Id).ToList();
        using var sub = new SubTransaction(doc);
        sub.Start();
        try
        {
            var gone = asked.Count == 0 ? None : doc.Delete(asked);
            sub.Commit();
            Record(items, gone, o);
            return true;
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or ArgumentException or InvalidOperationException)
        {
            if (sub.HasStarted() && !sub.HasEnded()) sub.RollBack();
            why = ex.Message.Trim();
            return false;
        }
    }

    /// <summary>The items count as deleted (one already gone with an earlier one too); anything else Revit removed is "also".</summary>
    private static void Record(List<Item> items, ICollection<ElementId> gone, Outcome o)
    {
        o.Deleted.AddRange(items);
        foreach (var item in items)
        {
            o.DeletedIds.Add(item.Id.Value);
            o.Also.Remove(item.Id.Value);
        }
        foreach (var id in gone)
            if (!o.DeletedIds.Contains(id.Value)) o.Also.Add(id.Value);
    }

    /// <summary>Apply: chunks through ItemBatch (borrowing in a workshared model, progress, Pause/Skip/Stop); a refused chunk one by one.</summary>
    private static void DeleteApply(Document doc, string title, List<Item> go, Outcome o)
    {
        var chunks = CleanupMath.Chunks(go, ChunkSize);
        var batch = ItemBatch.Run(doc, title, chunks, c => c.Count == 1 ? c[0].Name : $"{c.Count} items from {c[0].Name}",
            c => c.Select(i => i.Id).Where(id => doc.GetElement(id) is not null),
            c => { var ids = c.Where(i => Exists(doc, i.Id)).Select(i => i.Id).ToList(); return ids.Count == 0 ? None : doc.Delete(ids); });
        var retry = new List<Item>();
        for (var i = 0; i < chunks.Count; i++)
        {
            var r = batch.Items[i];
            if (r.Ok) { Record(chunks[i], r.Value as ICollection<ElementId> ?? None, o); continue; }
            if (Halted(r.Error)) { o.Stopped = "Stopped part-way: what was deleted is kept (one Undo step); the rest was not done."; continue; }
            retry.AddRange(chunks[i]);
        }
        if (retry.Count == 0 || o.Stopped is not null) return;
        var each = ItemBatch.Run(doc, title, retry, it => it.Name, it => [it.Id],
            it => Exists(doc, it.Id) ? doc.Delete(it.Id) : None, borrow: false);
        for (var i = 0; i < retry.Count; i++)
        {
            var r = each.Items[i];
            if (r.Ok) Record([retry[i]], r.Value as ICollection<ElementId> ?? None, o);
            else if (Halted(r.Error)) o.Stopped = "Stopped part-way: what was deleted is kept (one Undo step); the rest was not done.";
            else o.Stayed.Add(new(retry[i], Refused, r.Error ?? "Revit did not accept it"));
        }
    }

    private static bool Halted(string? error) =>
        error is { } e && (e.StartsWith("not done", StringComparison.Ordinal) || e == JobControl.RolledBack);

    /// <summary>The tool's result: the chat line, counts by group and reason, a page of the list, before/after and the file size.</summary>
    public static ToolResult Result(Document doc, Spec spec, Outcome o, bool apply, ToolArgs args, object? extra = null)
    {
        var byGroup = o.Deleted.GroupBy(i => i.Group).ToDictionary(g => g.Key, g => g.Count());
        var stayed = o.Stayed.GroupBy(s => s.Reason).ToDictionary(g => g.Key, g => g.Count());
        var (page, info) = Paging.Page(Paging.Named(o.Deleted, args, i => i.Name).Select(i => new { id = i.Id.Value, group = i.Group, name = i.Name }), args);
        long? bytes = null;
        try { if (doc.PathName.Length > 0 && File.Exists(doc.PathName)) bytes = new FileInfo(doc.PathName).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        var also = apply ? o.Also.Count : o.AlsoByCategory.Values.Sum();
        return ToolResult.Ok(new
        {
            summary = CleanupMath.Summary(spec.Title, apply, o.Deleted.Count, o.Passes, byGroup, stayed, also, o.Stopped),
            preview = !apply,
            total = o.Deleted.Count,
            passes = o.Passes,
            byGroup,
            items = page,
            paging = info,
            alsoDeletes = !apply && o.AlsoByCategory.Count > 0 ? o.AlsoByCategory : null,
            alsoDeleted = apply && o.Also.Count > 0 ? o.Also.Count : (int?)null,
            left = stayed.Count > 0 ? stayed : null,
            leftItems = o.Stayed.Count > 0 ? o.Stayed.Take(50).Select(s => new { id = s.Item.Id.Value, name = s.Item.Name, reason = s.Reason, why = s.Why }) : null,
            elements = new { before = o.Before, after = o.After },
            file = CleanupMath.FileHint(bytes, apply, o.Deleted.Count),
            stopped = o.Stopped,
            details = extra,
            next = apply || o.Deleted.Count == 0 ? null : "Show the user the summary and the list; when they agree, call again with the same arguments and apply true.",
        });
    }

    /// <summary>The spec of a cleanup tool for these arguments (the approval uses it to see what a delete takes along).</summary>
    public static Spec? SpecFor(UIApplication app, string tool, ToolArgs args) => tool switch
    {
        "delete_unused_views" => DeleteUnusedViewsTool.Spec(app, args),
        "cleanup_imports" => CleanupImportsTool.Spec(app, args),
        "cleanup_model" => CleanupModelTool.Spec(app, args),
        // Purge Unused only lists what nothing uses, so nothing else depends on it.
        _ => null,
    };

    /// <summary>For the approval: "It also deletes 3 Dimensions, 2 Text Notes." when applying takes other elements along. Call with no transaction open.</summary>
    public static string? Takes(UIApplication app, string tool, IReadOnlyDictionary<string, System.Text.Json.JsonElement> raw)
    {
        var args = new ToolArgs(raw);
        if (!Applying(args) || app.ActiveUIDocument?.Document is not { } doc || SpecFor(app, tool, args) is not { } spec) return null;
        try
        {
            var ids = FirstPass(doc, spec);
            return ids.Count == 0 ? null : Redlines.AlsoDeleted(doc, ids);
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or ArgumentException or InvalidOperationException)
        {
            return "What else it deletes could not be checked.";
        }
    }

    // ---- What things are --------------------------------------------------------------------------------------------

    public static string SafeName(Element e)
    {
        try { return e.Name ?? ""; }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or InvalidOperationException) { return ""; }
    }

    /// <summary>An element as a purge item: its group and a name a person recognises ("Doors: Single-Flush").</summary>
    public static Item? Describe(Document doc, ElementId id)
    {
        var e = doc.GetElement(id);
        if (e is null) return null;
        var name = SafeName(e);
        var group = e switch
        {
            Family f => "families",
            FamilySymbol => "family types",
            View { IsTemplate: true } => "view templates",
            View v => ViewGroup(v.ViewType),
            Material => "materials",
            AppearanceAssetElement => "appearance assets",
            LinePatternElement => "line patterns",
            FillPatternElement => "fill patterns",
            GroupType => "group types",
            FilterElement => "view filters",
            ImportInstance i => i.IsLinked ? "linked CAD" : "imported CAD",
            CADLinkType => "CAD types",
            ElementType t => (t.Category?.Name ?? "other") + " types",
            _ => e.Category?.Name ?? e.GetType().Name,
        };
        if (e is Family fam) name = $"{fam.FamilyCategory?.Name ?? "Family"}: {name}";
        else if (e is FamilySymbol s) name = $"{s.FamilyName}: {name}";
        return new Item(id, group, name);
    }

    /// <summary>The kind a purge_unused "kinds" filter names an element by.</summary>
    public static string Kind(Element e) => e switch
    {
        Family or FamilySymbol => "families",
        GroupType => "groups",
        View { IsTemplate: true } => "view_templates",
        Material => "materials",
        AppearanceAssetElement => "appearance_assets",
        LinePatternElement or FillPatternElement => "patterns",
        FilterElement => "filters",
        CADLinkType => "imports",
        ElementType => "types",
        _ => "other",
    };

    public static string ViewGroup(ViewType type) => type switch
    {
        ViewType.FloorPlan => "floor plans",
        ViewType.CeilingPlan => "ceiling plans",
        ViewType.EngineeringPlan => "structural plans",
        ViewType.AreaPlan => "area plans",
        ViewType.Section => "sections",
        ViewType.Elevation => "elevations",
        ViewType.Detail => "detail views",
        ViewType.ThreeD => "3D views",
        ViewType.Rendering => "renderings",
        ViewType.Walkthrough => "walkthroughs",
        ViewType.Legend => "legends",
        ViewType.DraftingView => "drafting views",
        ViewType.Schedule => "schedules",
        ViewType.ColumnSchedule => "column schedules",
        _ => type.ToString(),
    };

    /// <summary>View templates that no view, sheet or view type uses (the active view's is always used).</summary>
    public static IEnumerable<View> UnusedTemplates(Document doc, View? active)
    {
        var used = new HashSet<long>();
        var all = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().ToList();
        foreach (var v in all)
            if (!v.IsTemplate && v.ViewTemplateId != ElementId.InvalidElementId) used.Add(v.ViewTemplateId.Value);
        foreach (var t in new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>())
            if (t.DefaultTemplateId != ElementId.InvalidElementId) used.Add(t.DefaultTemplateId.Value);
        if (active is not null)
        {
            used.Add(active.Id.Value);
            if (active.ViewTemplateId != ElementId.InvalidElementId) used.Add(active.ViewTemplateId.Value);
        }
        return all.Where(v => v.IsTemplate && !used.Contains(v.Id.Value));
    }

    /// <summary>
    /// Rule-based view filters no view or template uses. Selection filters (saved selections) are never listed: people
    /// use them to select, not only in views.
    /// </summary>
    public static IEnumerable<ParameterFilterElement> UnusedFilters(Document doc)
    {
        var used = new HashSet<long>();
        foreach (var v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
        {
            try
            {
                if (!v.AreGraphicsOverridesAllowed()) continue;
                foreach (var id in v.GetFilters()) used.Add(id.Value);
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or InvalidOperationException) { }
        }
        return new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement)).Cast<ParameterFilterElement>().Where(f => !used.Contains(f.Id.Value));
    }

    /// <summary>Lets the preview's deletes go through quietly; it is rolled back anyway.</summary>
    private sealed class Quiet : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            foreach (var f in accessor.GetFailureMessages())
                if (f.GetSeverity() == FailureSeverity.Warning) accessor.DeleteWarning(f);
            return FailureProcessingResult.Continue;
        }
    }
}

public sealed class PurgeUnusedTool : IRevitTool
{
    public string Name => "purge_unused";
    public string Description =>
        "Purge Unused, as Revit's dialog, but previewed first and repeated until nothing new is unused (Revit's dialog needs " +
        "several runs): unused families and types, materials and appearance assets, line and fill patterns, group types not " +
        "placed, imported CAD types, view templates no view uses and view filters no view uses. Without apply it is a preview " +
        "(counts by kind and a page of the list); with apply true it deletes them in one Undo step. Never purges the active " +
        "view's template, anything a view or sheet uses, or title block families unless include_title_blocks; keep lists " +
        "names never to purge. Workshared: elements another user owns are left and named.";
    public string PropertiesJson => """
    {
      "kinds": {"type": "array", "items": {"type": "string", "enum": ["families", "types", "materials", "appearance_assets", "patterns", "groups", "view_templates", "filters", "imports", "other"]}, "description": "What to purge (default all)."},
      "categories": {"type": "array", "items": {"type": "string"}, "description": "Only unused elements of these categories (e.g. Doors, Walls); default every category."},
      "include_title_blocks": {"type": "boolean", "description": "Purge unused title block families too (default false: they are kept)."},
      "max_passes": {"type": "integer", "description": "Repeat until nothing new is unused, at most this many times (default 5)."},
    """ + Cleanup.CommonProperties + "}";
    public string[] Required => [];
    public bool ModifiesModel => true;

    private static readonly string[] AllKinds = ["families", "types", "materials", "appearance_assets", "patterns", "groups", "view_templates", "filters", "imports", "other"];

    internal static Cleanup.Spec Spec(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var kinds = args.StringList("kinds") is { Count: > 0 } k ? new HashSet<string>(k, StringComparer.OrdinalIgnoreCase) : new HashSet<string>(AllKinds);
        var categories = new HashSet<ElementId>();
        foreach (var name in args.StringList("categories")) categories.Add(new ElementId(RevitHelpers.ParseCategory(doc, name)));
        var titleBlocks = args.OptBool("include_title_blocks");
        var active = app.ActiveUIDocument.ActiveView;
        var activeTemplate = active?.ViewTemplateId ?? ElementId.InvalidElementId;

        IEnumerable<Cleanup.Item> Collect(Document d, int pass)
        {
            var ids = new List<ElementId>(d.GetUnusedElements(categories));
            if (categories.Count == 0)
            {
                if (kinds.Contains("view_templates")) ids.AddRange(Cleanup.UnusedTemplates(d, active).Select(v => v.Id));
                if (kinds.Contains("filters")) ids.AddRange(Cleanup.UnusedFilters(d).Select(f => f.Id));
            }
            foreach (var id in ids.Distinct())
            {
                if (d.GetElement(id) is not { } e || !kinds.Contains(Cleanup.Kind(e))) continue;
                if (Cleanup.Describe(d, id) is { } item) yield return item;
            }
        }

        string? Protect(Cleanup.Item item)
        {
            if (item.Id == activeTemplate) return "the active view's template";
            var e = doc.GetElement(item.Id);
            if (!titleBlocks && (e?.Category?.Id.Value == (long)BuiltInCategory.OST_TitleBlocks
                                 || e is Family f && f.FamilyCategory?.Id.Value == (long)BuiltInCategory.OST_TitleBlocks))
                return "a title block (pass include_title_blocks to purge unused ones)";
            return null;
        }

        return new Cleanup.Spec("Purge unused", (int)Math.Max(1, Math.Min(10, args.OptLong("max_passes") ?? 5)), Collect, Protect,
            args.StringList("keep"), args.StringList("only"));
    }

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var spec = Spec(app, args);
        var apply = Cleanup.Applying(args);
        return Cleanup.Result(doc, spec, Cleanup.Sweep(doc, spec, apply), apply, args);
    }
}

public sealed class DeleteUnusedViewsTool : IRevitTool
{
    public string Name => "delete_unused_views";
    public string Description =>
        "Delete views that are on no sheet: plans, sections, elevations, 3D and detail views by default, and legends, " +
        "schedules and drafting views when asked. Never the active or an open view, the starting view, a view with dependent " +
        "views, a view that holds callouts, a view a reference section or callout points to, the last legend, or a name in " +
        "keep (default WORKING and _keep). Without apply it is a preview with counts and what else would go (annotation drawn " +
        "in those views); with apply true it deletes them in one Undo step. Workshared: views another user owns are left and named.";
    public string PropertiesJson => """
    {
      "include_legends": {"type": "boolean", "description": "Also legends on no sheet (default false). The last legend always stays."},
      "include_schedules": {"type": "boolean", "description": "Also schedules on no sheet (default false)."},
      "include_drafting": {"type": "boolean", "description": "Also drafting views on no sheet (default false)."},
      "include_model_views": {"type": "boolean", "description": "Plans, sections, elevations, 3D and detail views (default true)."},
    """ + Cleanup.CommonProperties.Replace("(e.g. \\\"*Title*\\\", \\\"_keep\\\")", "(default WORKING and _keep)") + "}";
    public string[] Required => [];
    public bool ModifiesModel => true;

    private static readonly HashSet<ViewType> ModelViews =
    [
        ViewType.FloorPlan, ViewType.CeilingPlan, ViewType.EngineeringPlan, ViewType.AreaPlan, ViewType.Section, ViewType.Elevation,
        ViewType.Detail, ViewType.ThreeD, ViewType.Rendering, ViewType.Walkthrough,
    ];

    internal static Cleanup.Spec Spec(UIApplication app, ToolArgs args)
    {
        var uidoc = app.ActiveUIDocument;
        var doc = uidoc.Document;
        var types = new HashSet<ViewType>();
        if (args.OptBool("include_model_views", true)) types.UnionWith(ModelViews);
        if (args.OptBool("include_legends")) types.Add(ViewType.Legend);
        if (args.OptBool("include_schedules")) { types.Add(ViewType.Schedule); types.Add(ViewType.ColumnSchedule); }
        if (args.OptBool("include_drafting")) types.Add(ViewType.DraftingView);
        var keep = args.Has("keep") ? args.StringList("keep") : ["WORKING", "_keep"];

        var open = new HashSet<long>();
        try { foreach (var u in uidoc.GetOpenUIViews()) open.Add(u.ViewId.Value); } catch (Exception) { }
        if (uidoc.ActiveView is { } av) open.Add(av.Id.Value);
        long starting = -1;
        try { starting = StartingViewSettings.GetStartingViewSettings(doc).ViewId.Value; } catch (Exception) { }

        var all = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate).ToList();
        var onSheet = new HashSet<long>();
        foreach (var vp in new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>()) onSheet.Add(vp.ViewId.Value);
        foreach (var si in new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>()) onSheet.Add(si.ScheduleId.Value);
        var calloutParents = new HashSet<long>();
        foreach (var v in all)
        {
            try { if (v.IsCallout && v.GetCalloutParentId() is { } p && p != ElementId.InvalidElementId) calloutParents.Add(p.Value); }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or InvalidOperationException) { }
        }
        var referenced = new HashSet<long>();
        foreach (var marker in new FilteredElementCollector(doc).WherePasses(new LogicalOrFilter(
                     new ElementCategoryFilter(BuiltInCategory.OST_ReferenceViewer), new ElementCategoryFilter(BuiltInCategory.OST_Viewers))).WhereElementIsNotElementType())
        {
            try
            {
                var target = ReferenceableViewUtils.GetReferencedViewId(doc, marker.Id);
                if (target != ElementId.InvalidElementId) referenced.Add(target.Value);
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or ArgumentException) { /* not a reference marker */ }
        }
        var legends = all.Count(v => v.ViewType == ViewType.Legend);

        IEnumerable<Cleanup.Item> Collect(Document d, int pass) =>
            all.Where(v => v.IsValidObject && types.Contains(v.ViewType) && v is not ViewSheet && !onSheet.Contains(v.Id.Value)
                           && !(v is ViewSchedule s && s.IsTitleblockRevisionSchedule))
                .Select(v => new Cleanup.Item(v.Id, Cleanup.ViewGroup(v.ViewType), Cleanup.SafeName(v)));

        var legendsLeft = legends;
        string? Protect(Cleanup.Item item)
        {
            var id = item.Id.Value;
            if (open.Contains(id)) return "open in Revit (close it to delete it)";
            if (id == starting) return "the model's starting view";
            var v = doc.GetElement(item.Id) as View;
            if (v is not null && v.GetDependentViewIds().Count > 0) return "it has dependent views";
            if (calloutParents.Contains(id)) return "callouts are drawn in it (deleting it would delete them)";
            if (referenced.Contains(id)) return "a reference section or callout points to it";
            if (v?.ViewType == ViewType.Legend && --legendsLeft < 1) return "the last legend (Revit needs one to make new legends)";
            return null;
        }

        return new Cleanup.Spec("Delete unused views", 1, Collect, Protect, keep, args.StringList("only"));
    }

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var spec = Spec(app, args);
        var apply = Cleanup.Applying(args);
        return Cleanup.Result(doc, spec, Cleanup.Sweep(doc, spec, apply), apply, args);
    }
}

public sealed class CleanupImportsTool : IRevitTool
{
    public string Name => "cleanup_imports";
    public string Description =>
        "Remove imported or linked CAD (DWG, DXF, DGN...): every imported instance by default (scope imported; linked or all " +
        "on request), or only names in only (the file name, e.g. \"*survey*\"), then the CAD types left with no instance, and " +
        "the IMPORT- line styles and line patterns imports leave behind that nothing uses. Without apply it is a preview with " +
        "counts, the views the imports sit in and what else would go (dimensions or tags on them); with apply true it deletes " +
        "them in one Undo step. Workshared: imports another user owns are left and named.";
    public string PropertiesJson => """
    {
      "scope": {"type": "string", "enum": ["imported", "linked", "all"], "description": "imported (default): imported CAD only, links stay. linked: CAD links (removes the link). all: both."},
      "line_styles": {"type": "boolean", "description": "Also unused IMPORT- line styles and line patterns (default true)."},
    """ + Cleanup.CommonProperties + "}";
    public string[] Required => [];
    public bool ModifiesModel => true;

    internal static Cleanup.Spec Spec(UIApplication app, ToolArgs args)
    {
        var scope = args.OptString("scope") ?? "imported";
        var styles = args.OptBool("line_styles", true);
        bool InScope(bool linked) => scope == "all" || (scope == "linked") == linked;

        IEnumerable<Cleanup.Item> Collect(Document d, int pass)
        {
            var instances = new FilteredElementCollector(d).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().ToList();
            foreach (var i in instances.Where(i => InScope(i.IsLinked)))
            {
                var type = Cleanup.SafeName(d.GetElement(i.GetTypeId()) ?? i);
                var where = i.OwnerViewId != ElementId.InvalidElementId && d.GetElement(i.OwnerViewId) is View v ? $" (in view '{Cleanup.SafeName(v)}')" : " (model)";
                yield return new Cleanup.Item(i.Id, i.IsLinked ? "linked CAD" : "imported CAD", type + where);
            }
            // CAD types left with no instance (after the instances went, or from earlier deletes).
            var placed = new HashSet<long>(instances.Select(i => i.GetTypeId().Value));
            foreach (var t in new FilteredElementCollector(d).OfClass(typeof(CADLinkType)).Cast<CADLinkType>())
                if (!placed.Contains(t.Id.Value) && InScope(t.IsExternalFileReference()))
                    yield return new Cleanup.Item(t.Id, "CAD types", Cleanup.SafeName(t));
            if (!styles) yield break;
            var usedStyles = new HashSet<long>();
            foreach (var c in new FilteredElementCollector(d).OfClass(typeof(CurveElement)).Cast<CurveElement>())
            {
                try { if (c.LineStyle is GraphicsStyle gs && gs.GraphicsStyleCategory is { } cat) usedStyles.Add(cat.Id.Value); }
                catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or InvalidOperationException) { }
            }
            var lines = d.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
            foreach (Category sub in lines.SubCategories)
                if (sub.Name.StartsWith("IMPORT", StringComparison.OrdinalIgnoreCase) && !usedStyles.Contains(sub.Id.Value))
                    yield return new Cleanup.Item(sub.Id, "import line styles", sub.Name);
            var unused = new HashSet<long>(d.GetUnusedElements(new HashSet<ElementId> { new((long)BuiltInCategory.INVALID) }).Select(id => id.Value));
            foreach (var p in new FilteredElementCollector(d).OfClass(typeof(LinePatternElement)).Cast<LinePatternElement>())
                if (p.Name.StartsWith("IMPORT", StringComparison.OrdinalIgnoreCase) && unused.Contains(p.Id.Value))
                    yield return new Cleanup.Item(p.Id, "import line patterns", p.Name);
        }

        return new Cleanup.Spec("Clean up imports", 3, Collect, null, args.StringList("keep"), args.StringList("only"));
    }

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var spec = Spec(app, args);
        var apply = Cleanup.Applying(args);
        return Cleanup.Result(doc, spec, Cleanup.Sweep(doc, spec, apply), apply, args);
    }
}

public sealed class CleanupModelTool : IRevitTool
{
    public string Name => "cleanup_model";
    public string Description =>
        "Model health clean in one go, the safe pieces in order: view templates no view uses, unplaced rooms, areas and spaces, " +
        "group types not placed, view filters no view uses, and (when asked) scope boxes nothing uses. Lists Revit's warnings " +
        "by type with counts and a quick-fix hint (read only; warnings_only lists just those). Without apply it is a preview " +
        "with a one-line summary for the chat; with apply true it deletes in one Undo step. Workshared: elements another user owns are left and named.";
    public string PropertiesJson => """
    {
      "steps": {"type": "array", "items": {"type": "string", "enum": ["view_templates", "unplaced_rooms", "unplaced_areas", "unplaced_spaces", "unused_groups", "unused_filters", "scope_boxes"]}, "description": "What to clean (default all but scope_boxes)."},
      "warnings": {"type": "boolean", "description": "List Revit's warnings by type with counts and hints (default true). Nothing is changed for them."},
      "warnings_only": {"type": "boolean", "description": "Only list the warnings; delete nothing."},
    """ + Cleanup.CommonProperties + "}";
    public string[] Required => [];
    public bool ModifiesModel => true;

    private static readonly string[] DefaultSteps = ["view_templates", "unplaced_rooms", "unplaced_areas", "unplaced_spaces", "unused_groups", "unused_filters"];

    internal static Cleanup.Spec Spec(UIApplication app, ToolArgs args)
    {
        var steps = args.OptBool("warnings_only") ? new HashSet<string>()
            : args.StringList("steps") is { Count: > 0 } s ? new HashSet<string>(s, StringComparer.OrdinalIgnoreCase) : new HashSet<string>(DefaultSteps);
        var active = app.ActiveUIDocument.ActiveView;

        IEnumerable<Cleanup.Item> Collect(Document d, int pass)
        {
            if (steps.Contains("view_templates"))
                foreach (var v in Cleanup.UnusedTemplates(d, active)) yield return new Cleanup.Item(v.Id, "unused view templates", Cleanup.SafeName(v));
            foreach (var (step, category, group) in new[] { ("unplaced_rooms", BuiltInCategory.OST_Rooms, "unplaced rooms"), ("unplaced_areas", BuiltInCategory.OST_Areas, "unplaced areas"), ("unplaced_spaces", BuiltInCategory.OST_MEPSpaces, "unplaced spaces") })
            {
                if (!steps.Contains(step)) continue;
                foreach (var e in new FilteredElementCollector(d).OfCategory(category).WhereElementIsNotElementType().OfType<SpatialElement>().Where(r => r.Location is null))
                    yield return new Cleanup.Item(e.Id, group, $"{e.Number} {Cleanup.SafeName(e)}".Trim());
            }
            if (steps.Contains("unused_groups"))
                foreach (var g in new FilteredElementCollector(d).OfClass(typeof(GroupType)).Cast<GroupType>().Where(g => g.Groups.IsEmpty))
                    yield return new Cleanup.Item(g.Id, "group types not placed", Cleanup.SafeName(g));
            if (steps.Contains("unused_filters"))
                foreach (var f in Cleanup.UnusedFilters(d)) yield return new Cleanup.Item(f.Id, "unused view filters", Cleanup.SafeName(f));
            if (steps.Contains("scope_boxes"))
            {
                var used = new HashSet<long>();
                foreach (var e in new FilteredElementCollector(d).WherePasses(new LogicalOrFilter(new ElementClassFilter(typeof(View)), new ElementClassFilter(typeof(DatumPlane)))))
                    foreach (var bip in new[] { BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP, BuiltInParameter.DATUM_VOLUME_OF_INTEREST })
                        if (e.get_Parameter(bip)?.AsElementId() is { } id && id != ElementId.InvalidElementId) used.Add(id.Value);
                foreach (var b in new FilteredElementCollector(d).OfCategory(BuiltInCategory.OST_VolumeOfInterest).WhereElementIsNotElementType().Where(b => !used.Contains(b.Id.Value)))
                    yield return new Cleanup.Item(b.Id, "unused scope boxes", Cleanup.SafeName(b));
            }
        }

        string? Protect(Cleanup.Item item) =>
            active is not null && (item.Id == active.Id || item.Id == active.ViewTemplateId) ? "the active view's template" : null;

        return new Cleanup.Spec("Model health clean", 3, Collect, Protect, args.StringList("keep"), args.StringList("only"));
    }

    /// <summary>Revit's warnings by type, most frequent first, with a hint and a few element ids each.</summary>
    internal static List<object> Warnings(Document doc, out int total)
    {
        var all = doc.GetWarnings();
        total = all.Count;
        return all.GroupBy(w => w.GetDescriptionText()).OrderByDescending(g => g.Count()).Take(30)
            .Select(g => (object)new
            {
                warning = g.Key,
                count = g.Count(),
                hint = CleanupMath.WarningHint(g.Key),
                elementIds = g.Take(3).SelectMany(w => w.GetFailingElements().Concat(w.GetAdditionalElements())).Select(id => id.Value).Distinct().Take(10),
            }).ToList();
    }

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var spec = Spec(app, args);
        var apply = Cleanup.Applying(args) && !args.OptBool("warnings_only");
        object? warnings = null;
        if (args.OptBool("warnings", true) || args.OptBool("warnings_only"))
        {
            var list = Warnings(doc, out var total);
            warnings = new { total, types = list.Count, byType = list };
        }
        return Cleanup.Result(doc, spec, args.OptBool("warnings_only") ? new Cleanup.Outcome { Before = Cleanup.Count(doc), After = Cleanup.Count(doc) } : Cleanup.Sweep(doc, spec, apply),
            apply, args, warnings);
    }
}
