using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

/// <summary>
/// Join Geometry in bulk, the way the chat was asked again and again ("join the blinding together, the floors together
/// and the cement stabilised sand together, but never the piles", "unjoin all piles from the floors"). Written as code,
/// those requests ran every join in one transaction, so one pair Revit refused ("Can't cut joined element") undid all
/// of them: 231 refused code calls between 2026-10-15 and 10-29, most of them the chat probing pair by pair. Here each
/// pair is its own step (ItemBatch): a refused pair is tried once more with the other element first, and if Revit still
/// refuses it is listed with Revit's reason while every other pair is kept. Only pairs that really touch are joined, so
/// no "joined but do not intersect" joins are made.
/// </summary>
public sealed class JoinElementsTool : IRevitTool
{
    public string Name => "join_elements";
    public string Description =>
        "Join or unjoin touching elements in bulk, one pair at a time, so a pair Revit refuses never undoes the others " +
        "(use this instead of code for joins). Pick the elements with element_ids, or view_id (what that view shows, inside its " +
        "crop) and/or categories and type_contains. Pairs: with with_ids/with_categories/with_type_contains, every element " +
        "against that second set; otherwise pairs inside the set, kept within groups (group_by type, default; category; " +
        "none; or groups = lists of type-name words, e.g. [[\"blinding\"],[\"stabil\"],[\"slab\",\"deck\"]]). Only elements " +
        "that touch or overlap are paired (tolerance). never_categories are never joined (default Structural Columns: piles). " +
        "cut_order: type-name words or category names, earliest wins the join (e.g. [\"beam\",\"slab\"] beams cut slabs). " +
        "A refused pair is tried again with the other element first, else listed with Revit's reason. action check lists the " +
        "touching pairs and whether they are joined, changing nothing; unjoin undoes the joins between the two sets (or " +
        "inside the set).";
    public string PropertiesJson => """
    {
      "action": {"type": "string", "enum": ["join", "unjoin", "check"], "description": "Default join."},
      "element_ids": {"type": "array", "items": {"type": "integer"}, "description": "The elements. Or view_id and/or categories."},
      "view_id": {"type": "integer", "description": "Take the elements this view shows (inside its crop), e.g. a plan cropped to one berth."},
      "categories": {"type": "array", "items": {"type": "string"}, "description": "Categories of the elements (default Floors, Structural Framing, Structural Foundations, Walls, Generic Models when only view_id is given)."},
      "type_contains": {"type": "array", "items": {"type": "string"}, "description": "Keep elements whose 'Family: Type' name contains any of these words."},
      "exclude_ids": {"type": "array", "items": {"type": "integer"}, "description": "Elements to leave out (an area the user excluded)."},
      "with_ids": {"type": "array", "items": {"type": "integer"}, "description": "Second set: pair every element with these."},
      "with_categories": {"type": "array", "items": {"type": "string"}, "description": "Second set by category (same view_id scope)."},
      "with_type_contains": {"type": "array", "items": {"type": "string"}, "description": "Second set by type-name words (same scope)."},
      "group_by": {"type": "string", "enum": ["type", "category", "none"], "description": "Pairs inside one set: only within the same type (default), category, or any."},
      "groups": {"type": "array", "items": {"type": "array", "items": {"type": "string"}}, "description": "Pairs inside one set: only within each group of type-name words."},
      "never_categories": {"type": "array", "items": {"type": "string"}, "description": "Never joined. Default ['Structural Columns'] (piles); [] to allow."},
      "cut_order": {"type": "array", "items": {"type": "string"}, "description": "Type-name words or category names, earliest wins (cuts) the join."},
      "tolerance": {"type": "number", "description": "How close counts as touching, project units (default 1 mm)."},
      "limit": {"type": "integer", "description": "Most pairs in one call (default 600)."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => true;

    private static readonly string[] DefaultCategories = ["Floors", "Structural Framing", "Structural Foundations", "Walls", "Generic Models"];

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var action = (args.OptString("action") ?? "join").Trim().ToLowerInvariant();
        if (action is not ("join" or "unjoin" or "check")) throw new ArgumentException("action is join, unjoin or check.");
        var view = args.OptLong("view_id") is { } v
            ? doc.GetElement(new ElementId(v)) as View ?? throw new ArgumentException($"{v} is not a view.")
            : null;
        var exclude = args.LongList("exclude_ids").ToHashSet();

        var set = Pick(doc, view, args.LongList("element_ids"), args.StringList("categories"), args.StringList("type_contains"), exclude, true);
        var secondGiven = args.Has("with_ids") || args.Has("with_categories") || args.Has("with_type_contains");
        var with = secondGiven
            ? Pick(doc, view, args.LongList("with_ids"), args.StringList("with_categories"), args.StringList("with_type_contains"), exclude, false)
            : null;
        if (set.Count == 0) return ToolResult.Error("No elements match: give element_ids, or a view_id / categories / type_contains that find some.");
        if (with is { Count: 0 }) return ToolResult.Error("The second set (with_ids / with_categories / with_type_contains) found no elements.");

        var never = (args.Has("never_categories") ? args.StringList("never_categories") : ["Structural Columns"])
            .Select(c => (long)RevitHelpers.ParseCategory(doc, c)).ToHashSet();
        var groupWords = args.Has("groups")
            ? args.Array("groups").Select(g => g.EnumerateArray().Select(w => w.GetString() ?? "").Where(w => w.Length > 0).ToList()).Where(g => g.Count > 0).ToList()
            : null;
        var groupBy = args.OptString("group_by") ?? "type";
        var order = args.StringList("cut_order");
        var tol = args.OptDouble("tolerance") is { } t ? RevitHelpers.ToInternal(doc, t) : 1 / 304.8;
        var limit = (int)(args.OptLong("limit") ?? 600);

        // ---- The pairs
        var pairs = new List<(Element A, Element B)>();
        var skippedNever = 0;
        if (action == "unjoin")
        {
            var others = with is null ? set.Select(e => e.Id).ToHashSet() : with.Select(e => e.Id).ToHashSet();
            var seen = new HashSet<(long, long)>();
            foreach (var a in set)
                foreach (var id in JoinGeometryUtils.GetJoinedElements(doc, a))
                {
                    if (!others.Contains(id) || doc.GetElement(id) is not { } b) continue;
                    if (with is null && !SameGroup(doc, a, b, groupBy, groupWords)) continue;
                    var key = a.Id.Value < id.Value ? (a.Id.Value, id.Value) : (id.Value, a.Id.Value);
                    if (seen.Add(key)) pairs.Add((a, b));
                }
        }
        else
        {
            var boxes = (with ?? set).Select(e => (E: e, Box: e.get_BoundingBox(null))).Where(x => x.Box is not null).ToList();
            var seen = new HashSet<(long, long)>();
            foreach (var a in set)
            {
                if (a.get_BoundingBox(null) is not { } ba) continue;
                foreach (var (b, bb) in boxes)
                {
                    if (a.Id == b.Id || !Overlap(ba, bb!, tol)) continue;
                    var key = a.Id.Value < b.Id.Value ? (a.Id.Value, b.Id.Value) : (b.Id.Value, a.Id.Value);
                    if (!seen.Add(key)) continue;
                    if (with is null && !SameGroup(doc, a, b, groupBy, groupWords)) continue;
                    if (action == "join" && (never.Contains(a.Category?.Id.Value ?? 0) || never.Contains(b.Category?.Id.Value ?? 0))) { skippedNever++; continue; }
                    pairs.Add((a, b));
                }
            }
            // Boxes overlap; keep the pairs whose solids really touch (or that Revit already joined).
            pairs = pairs.Where(p => JoinGeometryUtils.AreElementsJoined(doc, p.A, p.B) || Touch(p.A, p.B, tol)).ToList();
        }

        var truncated = pairs.Count > limit;
        if (truncated) pairs = pairs.Take(limit).ToList();
        var label = (Func<(Element A, Element B), string>)(p => $"{Geo.Describe(p.A)} / {Geo.Describe(p.B)}");

        if (action == "check")
        {
            var rows = pairs.Select(p => new
            {
                a = p.A.Id.Value, b = p.B.Id.Value, types = $"{TypeName(doc, p.A)} / {TypeName(doc, p.B)}",
                joined = JoinGeometryUtils.AreElementsJoined(doc, p.A, p.B),
            }).ToList();
            return ToolResult.Ok(new
            {
                elements = set.Count, pairs = rows.Count, joined = rows.Count(r => r.joined), notJoined = rows.Count(r => !r.joined),
                skippedNever = skippedNever > 0 ? skippedNever : (int?)null, truncated = truncated ? true : (bool?)null,
                list = rows.Take(300),
            });
        }

        if (pairs.Count == 0)
            return ToolResult.Ok(new
            {
                elements = set.Count, pairs = 0,
                note = action == "unjoin" ? "None of these elements are joined to each other." : "No two of these elements touch (within the tolerance), so there was nothing to join.",
                skippedNever = skippedNever > 0 ? skippedNever : (int?)null,
            });

        // ---- Join or unjoin, one pair per step; then the refused joins once more the other way round.
        var first = ItemBatch.Run(doc, action == "join" ? "Join elements" : "Unjoin elements", pairs, label,
            p => [p.A.Id, p.B.Id], p => action == "join" ? Join(doc, p.A, p.B, order, false) : Unjoin(doc, p.A, p.B));
        var refused = pairs.Zip(first.Items, (p, i) => (p, i)).Where(x => !x.i.Ok && !Skipped(x.i.Error)).Select(x => x.p).ToList();
        ItemBatch.Result? again = null;
        if (action == "join" && refused.Count > 0)
            again = ItemBatch.Run(doc, "Join elements (other order)", refused, label, p => [p.A.Id, p.B.Id], p => Join(doc, p.A, p.B, order, true));

        var failed = first.Items.Where(i => !i.Ok && Skipped(i.Error)).Concat(again?.Failed ?? (action == "join" ? [] : first.Failed)).ToList();
        var done = first.Done + (again?.Done ?? 0);
        return ToolResult.Ok(new
        {
            action, elements = set.Count, pairs = pairs.Count, done,
            doneOtherOrder = again is { Done: > 0 } ? again.Done : (int?)null,
            refused = failed.Count,
            refusedPairs = failed.Count > 0 ? failed.Take(60).Select(f => new { pair = f.Item, reason = f.Error }) : null,
            skippedNever = skippedNever > 0 ? skippedNever : (int?)null,
            truncated = truncated ? $"Only the first {limit} pairs were done; call again for the rest." : null,
            next = failed.Count > 0 && action == "join"
                ? "Report the refused pairs in a line (Revit can't join them as they are: often a third element already joined to both, or faces that only meet at an edge); don't unjoin or move other elements to force them unless the user asks."
                : null,
        });
    }

    /// <summary>A reason that means the pair wasn't tried (owned by someone else, stopped): not worth the other order.</summary>
    private static bool Skipped(string? error) => error is not null &&
        (error.StartsWith("not done", StringComparison.OrdinalIgnoreCase) || error.IndexOf("owned by", StringComparison.OrdinalIgnoreCase) >= 0);

    private static object Join(Document doc, Element a, Element b, List<string> order, bool otherOrder)
    {
        var win = Winner(doc, a, b, order);
        var (x, y) = win ?? (a, b);
        if (otherOrder) (x, y) = (y, x);
        var was = JoinGeometryUtils.AreElementsJoined(doc, x, y);
        if (!was) JoinGeometryUtils.JoinGeometry(doc, x, y);
        if (win is { } w && !JoinGeometryUtils.IsCuttingElementInJoin(doc, w.Win, w.Lose)) JoinGeometryUtils.SwitchJoinOrder(doc, w.Win, w.Lose);
        return was ? "already joined" : "joined";
    }

    private static object Unjoin(Document doc, Element a, Element b)
    {
        if (!JoinGeometryUtils.AreElementsJoined(doc, a, b)) return "not joined";
        JoinGeometryUtils.UnjoinGeometry(doc, a, b);
        return "unjoined";
    }

    /// <summary>Which of the two cuts the other by cut_order (earliest word wins), or null when the order doesn't say.</summary>
    private static (Element Win, Element Lose)? Winner(Document doc, Element a, Element b, List<string> order)
    {
        if (order.Count == 0) return null;
        int Rank(Element e)
        {
            var name = TypeName(doc, e);
            var cat = e.Category?.Name ?? "";
            for (var i = 0; i < order.Count; i++)
                if (string.Equals(cat, order[i], StringComparison.OrdinalIgnoreCase) || name.IndexOf(order[i], StringComparison.OrdinalIgnoreCase) >= 0) return i;
            return int.MaxValue;
        }
        int ra = Rank(a), rb = Rank(b);
        return ra == rb ? null : ra < rb ? (a, b) : (b, a);
    }

    // ---- Picking the elements -------------------------------------------------------------------------------------

    private static List<Element> Pick(Document doc, View? view, List<long> ids, List<string> categories, List<string> words, HashSet<long> exclude, bool main)
    {
        IEnumerable<Element> found;
        if (ids.Count > 0) found = ids.Select(i => doc.GetElement(new ElementId(i))).OfType<Element>();
        else
        {
            if (view is null && categories.Count == 0 && words.Count == 0) return [];
            var cats = categories.Count > 0 ? categories : main || words.Count > 0 ? DefaultCategories.ToList() : [];
            if (cats.Count == 0) return [];
            var filter = new LogicalOrFilter(cats.Select(c => (ElementFilter)new ElementCategoryFilter(RevitHelpers.ParseCategory(doc, c))).ToList());
            var collector = view is null ? new FilteredElementCollector(doc) : new FilteredElementCollector(doc, view.Id);
            found = collector.WherePasses(filter).WhereElementIsNotElementType();
        }
        if (ids.Count > 0 && categories.Count > 0)
        {
            var keep = categories.Select(c => (long)RevitHelpers.ParseCategory(doc, c)).ToHashSet();
            found = found.Where(e => keep.Contains(e.Category?.Id.Value ?? 0));
        }
        if (words.Count > 0) found = found.Where(e => words.Any(w => TypeName(doc, e).IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0));
        return found.Where(e => !exclude.Contains(e.Id.Value) && e.Category is not null && e.Category.CategoryType == CategoryType.Model && !e.ViewSpecific)
            .GroupBy(e => e.Id.Value).Select(g => g.First()).ToList();
    }

    internal static string TypeName(Document doc, Element e) =>
        doc.GetElement(e.GetTypeId()) is ElementType t ? $"{t.FamilyName}: {t.Name}" : e.Name;

    private static bool SameGroup(Document doc, Element a, Element b, string groupBy, List<List<string>>? groups)
    {
        if (groups is not null)
        {
            int Of(Element e)
            {
                var n = TypeName(doc, e);
                for (var i = 0; i < groups.Count; i++)
                    if (groups[i].Any(w => n.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0)) return i;
                return -1;
            }
            var ga = Of(a);
            return ga >= 0 && ga == Of(b);
        }
        return groupBy switch
        {
            "none" => true,
            "category" => a.Category?.Id == b.Category?.Id,
            _ => a.GetTypeId() == b.GetTypeId(),
        };
    }

    // ---- Touching ---------------------------------------------------------------------------------------------------

    private static bool Overlap(BoundingBoxXYZ x, BoundingBoxXYZ y, double tol) =>
        x.Min.X <= y.Max.X + tol && x.Max.X >= y.Min.X - tol && x.Min.Y <= y.Max.Y + tol && x.Max.Y >= y.Min.Y - tol
        && x.Min.Z <= y.Max.Z + tol && x.Max.Z >= y.Min.Z - tol;

    /// <summary>
    /// True when the two solids overlap, or a corner or edge point of one lies on (within tol of) a face of the other:
    /// side-by-side slabs that only share a face count, which a solid intersection test misses.
    /// </summary>
    private static bool Touch(Element a, Element b, double tol)
    {
        try { if (new ElementIntersectsElementFilter(a).PassesFilter(b)) return true; }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { }
        var sa = Geo.Solids(a);
        var sb = Geo.Solids(b);
        if (sa.Count == 0 || sb.Count == 0) return false;
        return Near(sa, sb, tol) || Near(sb, sa, tol);
    }

    private static bool Near(List<Solid> from, List<Solid> to, double tol)
    {
        var points = new List<XYZ>();
        foreach (var s in from)
            foreach (Edge e in s.Edges)
            {
                try { points.AddRange(e.Tessellate()); }
                catch (Autodesk.Revit.Exceptions.ApplicationException) { }
                if (points.Count > 400) break;
            }
        foreach (var t in to)
            foreach (Face f in t.Faces)
                foreach (var p in points)
                {
                    IntersectionResult? r;
                    try { r = f.Project(p); }
                    catch (Autodesk.Revit.Exceptions.ApplicationException) { continue; }
                    if (r is not null && r.Distance <= tol) return true;
                }
        return false;
    }
}
