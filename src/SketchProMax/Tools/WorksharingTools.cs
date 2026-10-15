using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

/// <summary>
/// Shared worksharing checks. Ownership calls (checking out worksets, borrowing, relinquishing) talk to the central
/// model and Revit refuses them while a transaction is open, so every caller must run outside one.
/// </summary>
internal static class Workshare
{
    public static string User(Document doc) => doc.Application.Username;

    /// <summary>Throws a clear error when ownership can't be changed from here.</summary>
    public static void RequireCentralAccess(Document doc, string what)
    {
        if (!doc.IsWorkshared)
            throw new InvalidOperationException($"This model is not workshared, so there is nothing to {what}: every element is already editable.");
        if (doc.IsDetached)
            throw new InvalidOperationException($"This model was opened detached from central, so there is no ownership to {what}: every element is editable in this copy (and the copy can't sync back).");
        if (doc.IsLinked)
            throw new InvalidOperationException("This is a linked model; ownership can only be changed in the open host model's own worksets.");
        if (doc.IsModifiable)
            throw new InvalidOperationException(
                $"A transaction is open, and Revit refuses to {what} inside one (this is why run_revit_code can't do it). " +
                "Call this tool on its own, not from inside run_revit_code or a running edit.");
        if (doc.IsReadOnly)
            throw new InvalidOperationException("The model is read-only right now (another command or edit mode is active). Finish it in Revit, then try again.");
    }

    public static string Status(CheckoutStatus s) => s switch
    {
        CheckoutStatus.OwnedByCurrentUser => "mine",
        CheckoutStatus.OwnedByOtherUser => "other user",
        _ => "free",
    };

    public static string Updates(ModelUpdatesStatus s) => s switch
    {
        ModelUpdatesStatus.CurrentWithCentral => "current",
        ModelUpdatesStatus.UpdatedInCentral => "changed in central since your last sync (reload latest / synchronize first)",
        ModelUpdatesStatus.DeletedInCentral => "deleted in central",
        ModelUpdatesStatus.NotYetInCentral => "new, not yet in central",
        _ => s.ToString(),
    };

    public static string WorksetName(Document doc, Element e)
    {
        try { return doc.GetWorksetTable().GetWorkset(e.WorksetId)?.Name ?? ""; }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { return ""; }
    }

    /// <summary>Revit's message for a central-model error, with what the user can do about it.</summary>
    public static string Explain(Exception ex)
    {
        var msg = ex.Message.Trim();
        return ex switch
        {
            Autodesk.Revit.Exceptions.CentralFileCommunicationException => $"The central model could not be reached ({msg}). Check the network / BIM 360 connection and try again.",
            Autodesk.Revit.Exceptions.CentralModelContentionException => $"The central model is busy (someone is synchronizing): {msg} Try again in a minute.",
            Autodesk.Revit.Exceptions.CentralModelAccessDeniedException => $"Access to the central model was denied: {msg}",
            Autodesk.Revit.Exceptions.CentralModelException => $"Central model problem: {msg}",
            Autodesk.Revit.Exceptions.InternalException => $"Revit raised an internal error ({msg}). This happens when ownership is changed during another command; " +
                                                           "close any open dialog or edit mode and call the tool again. If it persists use Collaborate > Worksets in Revit.",
            _ => msg,
        };
    }

    public static List<Workset> Worksets(Document doc, bool allKinds) =>
        (allKinds ? new FilteredWorksetCollector(doc) : new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset))
        .ToWorksets().OrderBy(w => w.Kind).ThenBy(w => w.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>One element's ownership, for reports.</summary>
    public static object Describe(Document doc, Element e)
    {
        var status = WorksharingUtils.GetCheckoutStatus(doc, e.Id, out var owner);
        var updates = WorksharingUtils.GetModelUpdatesStatus(doc, e.Id);
        WorksharingTooltipInfo? tip = null;
        try { tip = WorksharingUtils.GetWorksharingTooltipInfo(doc, e.Id); }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { }
        Workset? ws = null;
        try { ws = doc.GetWorksetTable().GetWorkset(e.WorksetId); } catch (Autodesk.Revit.Exceptions.ApplicationException) { }
        var editable = status != CheckoutStatus.OwnedByOtherUser && updates is not (ModelUpdatesStatus.UpdatedInCentral or ModelUpdatesStatus.DeletedInCentral);
        string how = status switch
        {
            CheckoutStatus.OwnedByOtherUser when ws?.Owner == owner => $"in workset '{ws?.Name}', which {owner} has made editable",
            CheckoutStatus.OwnedByOtherUser => $"borrowed by {owner}",
            CheckoutStatus.OwnedByCurrentUser when ws?.IsEditable == true => "yours (your editable workset)",
            CheckoutStatus.OwnedByCurrentUser => "borrowed by you",
            _ => "free: Revit borrows it for you on the first edit",
        };
        return new
        {
            id = e.Id.Value,
            category = e.Category?.Name,
            name = SafeName(e),
            workset = ws?.Name,
            worksetOwner = string.IsNullOrEmpty(ws?.Owner) ? null : ws!.Owner,
            ownership = Status(status),
            owner = string.IsNullOrEmpty(owner) ? null : owner,
            how,
            updates = Updates(updates),
            editableNow = editable,
            creator = tip?.Creator,
            lastChangedBy = tip?.LastChangedBy,
            requestedBy = tip?.GetRequesters()?.ToList() is { Count: > 0 } r ? r : null,
            todo = editable ? null
                : status == CheckoutStatus.OwnedByOtherUser
                    ? $"Ask {owner} to Synchronize with Central with 'relinquish' ticked (or send an editing request from Revit: the API cannot place requests)."
                    : "Reload Latest or Synchronize with Central, then edit.",
        };
    }

    public static string SafeName(Element e)
    {
        try { return e.Name ?? ""; } catch { return ""; }
    }
}

public sealed class ListWorksetsTool : IRevitTool
{
    public string Name => "list_worksets";
    public string Description =>
        "Workshared models: list worksets with whether each is editable by you, who owns it, open/closed, default visibility " +
        "and (optionally) how many elements it holds. Also says who you are in Revit and which workset new elements go on. " +
        "Use before editing in a shared model to see what is locked.";
    public string PropertiesJson => $$"""
    {
      "all_kinds": {"type": "boolean", "description": "Also list view, family and standard worksets (default: user worksets only)."},
      "count_elements": {"type": "boolean", "description": "Count the elements on each workset (default false; a bit slower in big models)."},
      {{Paging.Properties}}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        if (!doc.IsWorkshared) return ToolResult.Ok(new { workshared = false, note = "This model is not workshared: everything is editable." });
        var me = Workshare.User(doc);
        var active = doc.GetWorksetTable().GetActiveWorksetId();
        var count = args.OptBool("count_elements");
        var list = Paging.Named(Workshare.Worksets(doc, args.OptBool("all_kinds")), args, w => w.Name);
        var (page, info) = Paging.Page(list, args);
        string? central = null;
        try { central = ModelPathUtils.ConvertModelPathToUserVisiblePath(doc.GetWorksharingCentralModelPath()); }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { }

        var rows = page.Select(w => new
        {
            id = w.Id.IntegerValue,
            name = w.Name,
            kind = w.Kind.ToString(),
            editable = w.IsEditable,
            owner = string.IsNullOrEmpty(w.Owner) ? null : w.Owner,
            ownedByOther = !string.IsNullOrEmpty(w.Owner) && !string.Equals(w.Owner, me, StringComparison.OrdinalIgnoreCase),
            open = w.IsOpen,
            isDefault = w.IsDefaultWorkset,
            active = w.Id == active,
            visibleByDefault = w.IsVisibleByDefault,
            elements = count
                ? new FilteredElementCollector(doc).WherePasses(new ElementWorksetFilter(w.Id)).WhereElementIsNotElementType().GetElementCount()
                : (int?)null,
        }).ToList();
        return ToolResult.Ok(new
        {
            workshared = true,
            detached = doc.IsDetached,
            you = me,
            central,
            activeWorkset = doc.GetWorksetTable().GetWorkset(active)?.Name,
            worksets = rows,
            page = info,
            note = "editable=true means you own the whole workset. Elements on worksets you don't own are still editable one by one " +
                   "(Revit borrows them) unless someone else owns them: check with element_owners, or take them with borrow_elements / make_worksets_editable.",
        });
    }
}

public sealed class MakeWorksetsEditableTool : IRevitTool
{
    public string Name => "make_worksets_editable";
    public string Description =>
        "Workshared models: make worksets editable by you (take ownership of the whole workset, like Collaborate > Worksets > Editable = Yes). " +
        "Worksets another user owns are reported with that user's name; Revit can't take them until they relinquish. Runs outside any transaction.";
    public string PropertiesJson => """
    {
      "worksets": {"type": "array", "items": {"type": "string"}, "description": "Workset names (exact, case-insensitive) or ids."},
      "name_contains": {"type": "string", "description": "Every user workset whose name contains this text."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        Workshare.RequireCentralAccess(doc, "make worksets editable");
        var me = Workshare.User(doc);
        var all = Workshare.Worksets(doc, allKinds: true);
        var wanted = new List<Workset>();
        var notFound = new List<string>();
        foreach (var name in args.StringList("worksets"))
        {
            var w = all.FirstOrDefault(x => string.Equals(x.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
                    ?? (int.TryParse(name, out var n) ? all.FirstOrDefault(x => x.Id.IntegerValue == n) : null);
            if (w is null) notFound.Add(name); else wanted.Add(w);
        }
        if (args.OptString("name_contains") is { Length: > 0 } part)
            wanted.AddRange(all.Where(w => w.Kind == WorksetKind.UserWorkset && w.Name.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0));
        wanted = wanted.GroupBy(w => w.Id.IntegerValue).Select(g => g.First()).ToList();
        if (wanted.Count == 0)
            return ToolResult.Error($"No workset to make editable{(notFound.Count > 0 ? $" ('{string.Join("', '", notFound)}' not found)" : "")}. " +
                                    $"User worksets: {string.Join(", ", all.Where(w => w.Kind == WorksetKind.UserWorkset).Select(w => w.Name))}");

        var already = wanted.Where(w => w.IsEditable).Select(w => w.Name).ToList();
        var others = wanted.Where(w => !w.IsEditable && !string.IsNullOrEmpty(w.Owner) && !string.Equals(w.Owner, me, StringComparison.OrdinalIgnoreCase))
            .Select(w => new { workset = w.Name, owner = w.Owner }).ToList();
        var ask = wanted.Where(w => !w.IsEditable && (string.IsNullOrEmpty(w.Owner) || string.Equals(w.Owner, me, StringComparison.OrdinalIgnoreCase))).ToList();

        var made = new List<string>();
        var refused = new List<object>();
        string? error = null;
        if (ask.Count > 0)
        {
            try
            {
                var owned = WorksharingUtils.CheckoutWorksets(doc, ask.Select(w => w.Id).ToList());
                var after = Workshare.Worksets(doc, allKinds: true).ToDictionary(w => w.Id.IntegerValue);
                foreach (var w in ask)
                {
                    if (owned.Contains(w.Id)) made.Add(w.Name);
                    else
                    {
                        var now = after.TryGetValue(w.Id.IntegerValue, out var a) ? a.Owner : "";
                        refused.Add(new { workset = w.Name, owner = string.IsNullOrEmpty(now) ? null : now,
                            reason = string.IsNullOrEmpty(now) ? "Revit did not grant it (central may be out of date: synchronize, then try again)" : $"{now} took it first" });
                    }
                }
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex) { error = Workshare.Explain(ex); }
        }
        var result = new
        {
            madeEditable = made,
            alreadyEditable = already,
            ownedByOthers = others,
            refused,
            notFound,
            error,
            note = others.Count > 0 || refused.Count > 0
                ? "A workset someone else owns can only be released by them (Synchronize with Central, relinquish worksets). You can still edit single free elements on it: Revit borrows them."
                : null,
        };
        return error is null ? ToolResult.Ok(result) : new ToolResult(System.Text.Json.JsonSerializer.Serialize(result), IsError: true);
    }
}

public sealed class BorrowElementsTool : IRevitTool
{
    public const int MaxIds = 5000;

    public string Name => "borrow_elements";
    public string Description =>
        "Workshared models: borrow (check out) elements before a batch edit, so nobody takes them mid-edit. Reports which you now own, " +
        "which another user owns (with their name), and which changed in central since your last sync (reload latest first). " +
        "Revit may borrow extra elements it needs (e.g. a whole group). Runs outside any transaction.";
    public string PropertiesJson => """
    {
      "element_ids": {"type": "array", "items": {"type": "integer"}},
      "view_id": {"type": "integer", "description": "Borrow the elements visible in this view (with category to narrow)."},
      "category": {"type": "string", "description": "With view_id: only this category."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        Workshare.RequireCentralAccess(doc, "borrow elements");
        var ids = OwnerQuery.Ids(doc, args, MaxIds, out var notFound, out var truncated);
        if (ids.Count == 0) return ToolResult.Error("No existing elements given: pass element_ids, or view_id (and category).");

        var others = new List<(long id, string owner)>();
        var stale = new List<long>();
        var ask = new List<ElementId>();
        var alreadyMine = 0;
        foreach (var id in ids)
        {
            var status = WorksharingUtils.GetCheckoutStatus(doc, id, out var owner);
            var updates = WorksharingUtils.GetModelUpdatesStatus(doc, id);
            if (status == CheckoutStatus.OwnedByOtherUser) { others.Add((id.Value, owner)); continue; }
            if (updates is ModelUpdatesStatus.UpdatedInCentral or ModelUpdatesStatus.DeletedInCentral) { stale.Add(id.Value); continue; }
            if (status == CheckoutStatus.OwnedByCurrentUser) alreadyMine++;
            else ask.Add(id);
        }

        var borrowed = 0;
        var refused = new List<object>();
        string? error = null;
        if (ask.Count > 0)
        {
            try
            {
                var owned = WorksharingUtils.CheckoutElements(doc, ask);
                foreach (var id in ask)
                {
                    if (owned.Contains(id)) { borrowed++; continue; }
                    var status = WorksharingUtils.GetCheckoutStatus(doc, id, out var owner);
                    refused.Add(new { id = id.Value, owner = status == CheckoutStatus.OwnedByOtherUser ? owner : null,
                        reason = status == CheckoutStatus.OwnedByOtherUser ? $"{owner} took it first" : "Revit did not grant it (synchronize and try again)" });
                }
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex) { error = Workshare.Explain(ex); }
        }

        var owners = others.Count > 0
            ? others.GroupBy(o => o.owner).Select(g => new { owner = g.Key, count = g.Count() }).ToList()
            : null;
        var result = new
        {
            requested = ids.Count,
            borrowed,
            alreadyYours = alreadyMine,
            ownedByOthers = others.Take(200).Select(o => new { o.id, o.owner }),
            ownedByOthersCount = others.Count,
            byOwner = owners,
            changedInCentral = stale.Take(200),
            changedInCentralCount = stale.Count,
            refused = refused.Take(200),
            notFound,
            truncated = truncated ? $"Only the first {MaxIds} elements were handled." : null,
            error,
            note = others.Count > 0
                ? "Elements another user owns can't be taken through the API (it has no 'place editing request'). Ask the owner to synchronize and relinquish, or send a request from Revit's UI."
                : stale.Count > 0 ? "Reload Latest (or Synchronize with Central) to get the newer versions, then borrow again." : null,
        };
        return error is null ? ToolResult.Ok(result) : new ToolResult(System.Text.Json.JsonSerializer.Serialize(result), IsError: true);
    }
}

public sealed class ElementOwnersTool : IRevitTool
{
    public const int MaxIds = 2000;

    public string Name => "element_owners";
    public string Description =>
        "Workshared models: who owns given elements and whether you can edit them now: workset and its owner, borrowed by whom, " +
        "created / last changed by, pending editing requests, and whether central has a newer version. Give element_ids, " +
        "or a view_id (optionally a category) to check what is visible there. Summarised by owner.";
    public string PropertiesJson => """
    {
      "element_ids": {"type": "array", "items": {"type": "integer"}},
      "view_id": {"type": "integer", "description": "Check the elements visible in this view (and the view itself)."},
      "category": {"type": "string", "description": "With view_id: only this category."},
      "only_locked": {"type": "boolean", "description": "List only elements you can't edit now (default false)."},
      "limit": {"type": "integer", "description": "Max element rows to return (default 200)."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        if (!doc.IsWorkshared) return ToolResult.Ok(new { workshared = false, note = "This model is not workshared: everything is editable." });
        var ids = OwnerQuery.Ids(doc, args, MaxIds, out var notFound, out var truncated);
        if (args.OptLong("view_id") is { } vid && doc.GetElement(new ElementId(vid)) is View && !ids.Contains(new ElementId(vid)))
            ids.Insert(0, new ElementId(vid));
        if (ids.Count == 0) return ToolResult.Error("No existing elements given: pass element_ids, or view_id (and category).");

        var me = Workshare.User(doc);
        var lockedOnly = args.OptBool("only_locked");
        var rows = new List<object>();
        var byOwner = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int locked = 0, stale = 0;
        foreach (var id in ids)
        {
            var e = doc.GetElement(id);
            if (e is null) continue;
            var status = WorksharingUtils.GetCheckoutStatus(doc, id, out var owner);
            var updates = WorksharingUtils.GetModelUpdatesStatus(doc, id);
            var isLocked = status == CheckoutStatus.OwnedByOtherUser;
            var isStale = updates is ModelUpdatesStatus.UpdatedInCentral or ModelUpdatesStatus.DeletedInCentral;
            if (isLocked) { locked++; byOwner[owner] = byOwner.TryGetValue(owner, out var n) ? n + 1 : 1; }
            if (isStale) stale++;
            if (lockedOnly && !isLocked && !isStale) continue;
            rows.Add(Workshare.Describe(doc, e));
        }
        var limit = (int)Math.Max(1, args.OptLong("limit") ?? 200);
        return ToolResult.Ok(new
        {
            you = me,
            checkedCount = ids.Count,
            editableNow = ids.Count - locked - stale,
            ownedByOthers = locked,
            changedInCentral = stale,
            byOwner = byOwner.Select(kv => new { owner = kv.Key, elements = kv.Value }),
            elements = rows.Take(limit),
            rowsTruncated = rows.Count > limit ? $"{rows.Count - limit} more rows; raise limit or set only_locked." : null,
            notFound,
            truncated = truncated ? $"Only the first {MaxIds} elements were checked." : null,
        });
    }
}

public sealed class RelinquishMineTool : IRevitTool
{
    public string Name => "relinquish_mine";
    public string Description =>
        "Workshared models: give back what you hold without synchronizing (like Collaborate > Relinquish All Mine): borrowed elements and/or " +
        "editable worksets. Revit only releases items you have not changed since the last sync; changed or new elements stay yours " +
        "until you Synchronize with Central (the result says so). Pending editing requests from others are granted. Runs outside any transaction.";
    public string PropertiesJson => """
    {
      "borrowed_elements": {"type": "boolean", "description": "Default true."},
      "user_worksets": {"type": "boolean", "description": "Default true."},
      "view_worksets": {"type": "boolean", "description": "Default true."},
      "family_worksets": {"type": "boolean", "description": "Default true."},
      "standard_worksets": {"type": "boolean", "description": "Default true."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        Workshare.RequireCentralAccess(doc, "relinquish ownership");
        var mineBefore = Workshare.Worksets(doc, allKinds: true).Where(w => w.IsEditable).Select(w => w.Name).ToList();
        var options = new RelinquishOptions(false)
        {
            CheckedOutElements = args.OptBool("borrowed_elements", true),
            UserWorksets = args.OptBool("user_worksets", true),
            ViewWorksets = args.OptBool("view_worksets", true),
            FamilyWorksets = args.OptBool("family_worksets", true),
            StandardWorksets = args.OptBool("standard_worksets", true),
        };
        RelinquishedItems items;
        try { items = WorksharingUtils.RelinquishOwnership(doc, options, null); }
        catch (Autodesk.Revit.Exceptions.ApplicationException ex) { return ToolResult.Error(Workshare.Explain(ex)); }

        var table = doc.GetWorksetTable();
        var worksets = items.GetRelinquishedWorksets().Select(id => table.GetWorkset(id)?.Name ?? id.IntegerValue.ToString()).ToList();
        var elements = items.GetRelinquishedElements().Count;
        var stillMine = Workshare.Worksets(doc, allKinds: true).Where(w => w.IsEditable).Select(w => w.Name).ToList();
        return ToolResult.Ok(new
        {
            relinquishedElements = elements,
            relinquishedWorksets = worksets,
            worksetsStillYours = stillMine,
            hadEditableWorksets = mineBefore.Count,
            unsavedChanges = doc.IsModified,
            note = stillMine.Count > 0 || doc.IsModified
                ? "What you changed (or created) since the last sync stays yours: Revit can only release it by Synchronize with Central " +
                  "(with relinquish ticked). The API can't relinquish modified elements without syncing."
                : "Everything you held was released.",
        });
    }
}

/// <summary>Element ids from element_ids, or from a view (optionally one category), capped.</summary>
internal static class OwnerQuery
{
    public static List<ElementId> Ids(Document doc, ToolArgs args, int max, out List<long> notFound, out bool truncated)
    {
        notFound = [];
        var ids = new List<ElementId>();
        foreach (var raw in args.LongList("element_ids"))
        {
            var id = new ElementId(raw);
            if (doc.GetElement(id) is null) notFound.Add(raw); else ids.Add(id);
        }
        if (args.OptLong("view_id") is { } vid)
        {
            if (doc.GetElement(new ElementId(vid)) is not View view) throw new ArgumentException($"{vid} is not a view.");
            if (view.IsTemplate || view.ViewType is ViewType.Schedule or ViewType.Internal or ViewType.ProjectBrowser or ViewType.SystemBrowser)
                throw new ArgumentException($"'{view.Name}' doesn't show model elements; give element_ids instead.");
            var collector = new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType();
            if (args.OptString("category") is { Length: > 0 } cat) collector = collector.OfCategory(RevitHelpers.ParseCategory(doc, cat));
            ids.AddRange(collector.ToElementIds().Where(i => i.Value > 0));
        }
        ids = ids.Distinct().ToList();
        truncated = ids.Count > max;
        return truncated ? ids.Take(max).ToList() : ids;
    }
}

/// <summary>Helpers for tools that run their edits through <see cref="ItemBatch"/>.</summary>
internal static class Batch
{
    /// <summary>Elements another user owns (or that changed in central), with the reason; checked before any edit.</summary>
    public static Dictionary<long, string> Locked(Document doc, IEnumerable<ElementId> ids)
    {
        var locked = new Dictionary<long, string>();
        if (!doc.IsWorkshared) return locked;
        foreach (var id in ids.Distinct())
            if (ItemBatch.Locked(doc, [id]) is { } why) locked[id.Value] = why;
        return locked;
    }

    /// <summary>Runs <paramref name="body"/> in one undo step (a transaction group); it must start no transaction itself.</summary>
    public static T InGroup<T>(Document doc, string name, Func<T> body, Func<T, bool> keep)
    {
        if (doc.IsModifiable) throw new InvalidOperationException("A transaction is already open; this tool must run on its own.");
        using var group = new TransactionGroup(doc, $"Sketch ProMax: {name}");
        group.Start();
        T result;
        try { result = body(); }
        catch
        {
            if (group.HasStarted() && !group.HasEnded()) group.RollBack();
            throw;
        }
        if (keep(result)) group.Assimilate(); else group.RollBack();
        return result;
    }

    /// <summary>A short list of failures for a result.</summary>
    public static List<object> Failures(ItemBatch.Result r, int max = 50) =>
        r.Failed.Take(max).Select(f => (object)new { item = f.Item, reason = f.Error }).ToList();
}
