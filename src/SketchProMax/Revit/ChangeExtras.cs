using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using M = SketchProMax.Revit.ChangeFileMath;

namespace SketchProMax.Revit;

/// <summary>
/// What the change file keeps besides an element's own values and shape: its joins, cuts and attachments to other
/// elements; a view's graphics (categories hidden or drawn differently, filters, elements hidden or drawn differently);
/// the line styles and object styles of the project; and a short signature of a shape that can't be written out, so a
/// replay knows when it changed.
/// </summary>
internal static class ChangeExtras
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ---- A shape's signature ---------------------------------------------------------------------------------------------

    /// <summary>The element's box, the volume of its solids and how many faces they have: enough to tell that its shape changed.</summary>
    public static string? Signature(Element e)
    {
        try
        {
            if (e.get_BoundingBox(null) is not { } box) return null;
            double volume = 0;
            var faces = 0;
            if (e.get_Geometry(new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Medium }) is { } geometry) Count(geometry, ref volume, ref faces, 0);
            string F(double d) => Math.Round(d, 3).ToString("0.###", Inv);
            return string.Join(" ", F(box.Min.X), F(box.Min.Y), F(box.Min.Z), F(box.Max.X), F(box.Max.Y), F(box.Max.Z), Math.Round(volume, 2).ToString("0.##", Inv), faces.ToString(Inv));
        }
        catch (Exception) { return null; }
    }

    private static void Count(GeometryElement geometry, ref double volume, ref int faces, int depth)
    {
        foreach (var item in geometry)
        {
            if (item is Solid solid) { try { volume += solid.Volume; faces += solid.Faces.Size; } catch (Exception) { } }
            else if (item is GeometryInstance instance && depth < 4) Count(instance.GetInstanceGeometry(), ref volume, ref faces, depth + 1);
        }
    }

    /// <summary>
    /// What a group is made of (<see cref="ChangeFileMath.GroupShape"/>): each member's category and type, and how far
    /// its point or ends are from the group's origin (a member with neither: its volume). Null when it can't be read.
    /// </summary>
    public static string? GroupShape(Group group)
    {
        try
        {
            if (group.Location is not LocationPoint { Point: { } origin }) return null;
            var doc = group.Document;
            var members = new List<(string, double[])>();
            foreach (var id in group.GetMemberIds())
            {
                if (doc.GetElement(id) is not { } member) continue;
                var kind = (member.Category?.Name ?? member.GetType().Name) + "|" + (doc.GetElement(member.GetTypeId())?.Name ?? "");
                double[] far;
                switch (member.Location)
                {
                    case LocationPoint { Point: { } at }: far = [at.DistanceTo(origin)]; break;
                    case LocationCurve { Curve: { IsBound: true } curve }: far = [curve.GetEndPoint(0).DistanceTo(origin), curve.GetEndPoint(1).DistanceTo(origin), curve.Length]; break;
                    default:
                        double volume = 0;
                        var faces = 0;
                        if (!member.ViewSpecific && member.get_Geometry(new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Coarse }) is { } geometry) Count(geometry, ref volume, ref faces, 0);
                        far = [volume, faces];
                        break;
                }
                members.Add((kind, far));
            }
            return members.Count == 0 ? null : M.GroupShape(members);
        }
        catch (Exception) { return null; }
    }

    /// <summary>The tags, dimensions and spots of the model (what Revit takes out when the element they point at goes).</summary>
    public static HashSet<ElementId> Notes(Document doc) =>
        [.. new FilteredElementCollector(doc).WherePasses(new LogicalOrFilter(new ElementClassFilter(typeof(IndependentTag)), new ElementClassFilter(typeof(Dimension)))).ToElementIds()];

    /// <summary>
    /// True when nothing of the user's hangs on the element (a tag, a dimension, a hosted family, bars, an opening), so
    /// it can be taken out and put back from its kept copy without losing anything else.
    /// </summary>
    public static bool SafeToReplace(Document doc, Element e)
    {
        try
        {
            foreach (var id in e.GetDependentElements(null))
            {
                if (id == e.Id || doc.GetElement(id) is not { } d) continue;
                if (d is IndependentTag or Dimension or SpatialElementTag or TextNote or Rebar or Opening or AreaReinforcement or PathReinforcement) return false;
                if (d is FamilyInstance fi && fi.SuperComponent is null && fi.Host?.Id == e.Id) return false;
            }
            return true;
        }
        catch (Exception) { return false; }
    }

    // ---- Joins, cuts and attachments --------------------------------------------------------------------------------------

    private const int MaxRelations = 80;

    /// <summary>Elements that join or cut others: walls, floors, roofs and the like, and structural families.</summary>
    private static bool Relates(Element e) =>
        e is HostObject || (e is FamilyInstance fi && fi.Category is { CategoryType: CategoryType.Model } && fi.SuperComponent is null && !fi.ViewSpecific);

    /// <summary>Adds the element's joins (and which of the two cuts), void cuts, solid cuts and what a wall is attached to, to its line.</summary>
    public static void ReadRelations(Document doc, Element e, M.Line line)
    {
        if (!Relates(e)) return;
        var x = line.Extra ??= [];
        string List(IEnumerable<ElementId> ids, Func<Element, string>? mark = null) =>
            string.Join(";", ids.Take(MaxRelations).Select(doc.GetElement).Where(o => o is not null).Select(o => (mark?.Invoke(o!) ?? "") + o!.UniqueId));
        // Always written for these elements (empty too), so a join taken off is known as well as one added.
        try { x["joins"] = List(JoinGeometryUtils.GetJoinedElements(doc, e), o => JoinGeometryUtils.IsCuttingElementInJoin(doc, e, o) ? "+" : "-"); } catch (Exception) { }
        try { if (InstanceVoidCutUtils.CanBeCutWithVoid(e) && InstanceVoidCutUtils.GetCuttingVoidInstances(e) is { Count: > 0 } voids) x["voids"] = List(voids); } catch (Exception) { }
        try { if (SolidSolidCutUtils.GetCuttingSolids(e) is { Count: > 0 } cutters) x["cuts"] = List(cutters); } catch (Exception) { }
#if !REVIT2024
        if (e is Wall wall)
        {
            try { if (wall.GetAttachmentIds(AttachmentLocation.Top) is { Count: > 0 } top) x["attop"] = List(top); } catch (Exception) { }
            try { if (wall.GetAttachmentIds(AttachmentLocation.Base) is { Count: > 0 } bottom) x["atbase"] = List(bottom); } catch (Exception) { }
        }
#endif
        if (x.Count == 0) line.Extra = null;
    }

    /// <summary>True when the element's joins, cuts and attachments are as its line has them already (read only): nothing to do for it.</summary>
    public static bool Related(Document doc, Element e, M.Line line, Func<string, Element?> find)
    {
        try
        {
            if (line.Extra is not { } x) return true;
            if (x.ContainsKey("joins"))
            {
                var wanted = M.Relations(line, "joins");
                var now = JoinGeometryUtils.GetJoinedElements(doc, e);
                if (now.Count != wanted.Count) return false;
                foreach (var (unique, cuts) in wanted)
                    if (find(unique) is not { } other || !now.Contains(other.Id) || JoinGeometryUtils.IsCuttingElementInJoin(doc, e, other) != cuts) return false;
            }
            foreach (var (unique, _) in M.Relations(line, "voids"))
                if (find(unique) is not { } cutter || !InstanceVoidCutUtils.InstanceVoidCutExists(e, cutter)) return false;
            foreach (var (unique, _) in M.Relations(line, "cuts"))
                if (find(unique) is not { } cutter || !SolidSolidCutUtils.CutExistsBetweenElements(e, cutter, out _)) return false;
#if !REVIT2024
            if (e is Wall wall)
                foreach (var (key, where) in new[] { ("attop", AttachmentLocation.Top), ("atbase", AttachmentLocation.Base) })
                    foreach (var (unique, _) in M.Relations(line, key))
                        if (find(unique) is not { } target || !wall.GetAttachmentIds(where).Contains(target.Id)) return false;
#endif
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Sets the element's joins, cuts and attachments as its line has them; true when anything changed. A join is taken
    /// off only when the other element's line (<paramref name="lineOf"/>) agrees that the two aren't joined.
    /// Call inside a transaction.
    /// </summary>
    public static bool Relate(Document doc, Element e, M.Line line, Func<string, Element?> find, Func<string, M.Line?> lineOf)
    {
        var changed = false;
        if (line.Extra is { } x && x.ContainsKey("joins"))
        {
            var wanted = M.Relations(line, "joins");
            foreach (var (unique, cuts) in wanted)
            {
                if (find(unique) is not { } other) continue;
                try
                {
                    if (!JoinGeometryUtils.AreElementsJoined(doc, e, other)) { JoinGeometryUtils.JoinGeometry(doc, e, other); changed = true; }
                    if (JoinGeometryUtils.IsCuttingElementInJoin(doc, e, other) != cuts) { JoinGeometryUtils.SwitchJoinOrder(doc, e, other); changed = true; }
                }
                catch (Exception) { /* Revit won't join these two as they are now: left */ }
            }
            try
            {
                var keep = wanted.Select(w => find(w.Unique)?.Id).Where(id => id is not null).ToHashSet();
                var mine = e.UniqueId;
                foreach (var id in JoinGeometryUtils.GetJoinedElements(doc, e))
                {
                    if (keep.Contains(id) || doc.GetElement(id) is not { } other) continue;
                    // Only when the other element's line says the same (it has one, and it doesn't list this element).
                    if (lineOf(other.UniqueId) is not { Extra: { } ox } || !ox.ContainsKey("joins")) continue;
                    if (M.Relations(lineOf(other.UniqueId)!, "joins").Any(r => find(r.Unique)?.UniqueId == mine)) continue;
                    JoinGeometryUtils.UnjoinGeometry(doc, e, other);
                    changed = true;
                }
            }
            catch (Exception) { }
        }
        foreach (var (unique, _) in M.Relations(line, "voids"))
            try { if (find(unique) is { } cutter && !InstanceVoidCutUtils.InstanceVoidCutExists(e, cutter)) { InstanceVoidCutUtils.AddInstanceVoidCut(doc, e, cutter); changed = true; } } catch (Exception) { }
        foreach (var (unique, _) in M.Relations(line, "cuts"))
            try { if (find(unique) is { } cutter && !SolidSolidCutUtils.CutExistsBetweenElements(e, cutter, out _)) { SolidSolidCutUtils.AddCutBetweenSolids(doc, e, cutter); changed = true; } } catch (Exception) { }
#if !REVIT2024
        if (e is Wall wall)
            foreach (var (key, where) in new[] { ("attop", AttachmentLocation.Top), ("atbase", AttachmentLocation.Base) })
                foreach (var (unique, _) in M.Relations(line, key))
                    try
                    {
                        if (find(unique) is { } target && !wall.GetAttachmentIds(where).Contains(target.Id) && Wall.IsValidTargetAttachment(doc, target.Id)) { wall.AddAttachment(target.Id, where); changed = true; }
                    }
                    catch (Exception) { }
#endif
        return changed;
    }

    // ---- A view's graphics ------------------------------------------------------------------------------------------------

    private const int MaxCategories = 4000;

    /// <summary>
    /// The view's graphics as JSON for its line: categories hidden and drawn differently, its filters; and for
    /// <paramref name="touched"/> (the elements last selected: what Hide in View and Override Graphics act on) whether
    /// each is hidden and how it is drawn. Null when the view has no graphics of its own (a schedule, a sheet list).
    /// </summary>
    public static string? ReadGraphics(Document doc, View view, IEnumerable<ElementId> touched)
    {
        try
        {
            if (view is ViewSchedule or ViewSheet || !view.AreGraphicsOverridesAllowed()) return null;
            var vg = new M.Vg();
            var n = 0;
            void Look(Category c)
            {
                if (++n > MaxCategories) return;
                try
                {
                    if (view.CanCategoryBeHidden(c.Id) && view.GetCategoryHidden(c.Id)) vg.Hidden.Add(c.Id.Value);
                    var text = Write(view.GetCategoryOverrides(c.Id));
                    if (text.Length > 0) vg.Over[c.Id.Value.ToString(Inv)] = text;
                }
                catch (Exception) { }
            }
            foreach (Category c in doc.Settings.Categories)
            {
                Look(c);
                // A link's or CAD file's layers are its own: only the project's subcategories.
                if (c.Id.Value < 0) foreach (Category sub in c.SubCategories) Look(sub);
            }
            foreach (var id in view.GetOrderedFilters())
                if (doc.GetElement(id) is { } filter)
                    vg.Filters.Add(new M.VgFilter { U = filter.UniqueId, Visible = view.GetFilterVisibility(id), Enabled = view.GetIsFilterEnabled(id), Over = Write(view.GetFilterOverrides(id)) });
            foreach (var id in touched.Take(500))
            {
                if (doc.GetElement(id) is not { } e || e is View or ElementType) continue;
                try
                {
                    if (e.IsHidden(view)) (vg.Hide ??= []).Add(e.UniqueId);
                    else if (e.CanBeHidden(view)) (vg.Show ??= []).Add(e.UniqueId);
                    (vg.Elements ??= [])[e.UniqueId] = Write(view.GetElementOverrides(id));
                }
                catch (Exception) { }
            }
            return JsonSerializer.Serialize(vg);
        }
        catch (Exception) { return null; }
    }

    /// <summary>The same without the elements (they differ with the selection): what tells whether the view's own graphics changed.</summary>
    public static string GraphicsKey(string json)
    {
        try
        {
            var vg = JsonSerializer.Deserialize<M.Vg>(json)!;
            // Elements as the view draws them and not hidden say nothing new.
            var plain = vg.Hide is null && (vg.Elements is null || vg.Elements.Values.All(v => v.Length == 0));
            if (plain) { vg.Show = null; vg.Elements = null; }
            return JsonSerializer.Serialize(vg);
        }
        catch (Exception) { return json; }
    }

    /// <summary>Sets the view's graphics as its line has them; true when anything changed. Call inside a transaction.</summary>
    public static bool SetGraphics(Document doc, View view, string json, Func<string, Element?> find)
    {
        M.Vg vg;
        try { vg = JsonSerializer.Deserialize<M.Vg>(json)!; } catch (Exception) { return false; }
        var changed = false;
        try { if (!view.AreGraphicsOverridesAllowed()) return false; } catch (Exception) { return false; }
        var hidden = vg.Hidden.ToHashSet();
        var n = 0;
        void Put(Category c)
        {
            if (++n > MaxCategories) return;
            try
            {
                if (view.CanCategoryBeHidden(c.Id) && view.GetCategoryHidden(c.Id) != hidden.Contains(c.Id.Value)) { view.SetCategoryHidden(c.Id, hidden.Contains(c.Id.Value)); changed = true; }
                var want = vg.Over.TryGetValue(c.Id.Value.ToString(Inv), out var text) ? text : "";
                if (Write(view.GetCategoryOverrides(c.Id)) != want) { view.SetCategoryOverrides(c.Id, Read(want)); changed = true; }
            }
            catch (Exception) { /* its template holds this one, or the view can't draw the category: left */ }
        }
        foreach (Category c in doc.Settings.Categories)
        {
            Put(c);
            if (c.Id.Value < 0) foreach (Category sub in c.SubCategories) Put(sub);
        }
        try
        {
            var wanted = vg.Filters.Select(f => (Filter: f, Element: find(f.U))).Where(f => f.Element is not null).ToList();
            var keep = wanted.Select(f => f.Element!.Id).ToHashSet();
            foreach (var id in view.GetFilters().Where(id => !keep.Contains(id)).ToList()) { view.RemoveFilter(id); changed = true; }
            foreach (var (filter, element) in wanted)
            {
                var id = element!.Id;
                if (!view.IsFilterApplied(id)) { view.AddFilter(id); changed = true; }
                if (view.GetFilterVisibility(id) != filter.Visible) { view.SetFilterVisibility(id, filter.Visible); changed = true; }
                if (view.GetIsFilterEnabled(id) != filter.Enabled) { view.SetIsFilterEnabled(id, filter.Enabled); changed = true; }
                if (Write(view.GetFilterOverrides(id)) != filter.Over) { view.SetFilterOverrides(id, Read(filter.Over)); changed = true; }
            }
        }
        catch (Exception) { }
        try
        {
            var hide = (vg.Hide ?? []).Select(find).Where(e => e is not null && !e.IsHidden(view) && e.CanBeHidden(view)).Select(e => e!.Id).ToList();
            if (hide.Count > 0) { view.HideElements(hide); changed = true; }
            var show = (vg.Show ?? []).Select(find).Where(e => e is not null && e.IsHidden(view)).Select(e => e!.Id).ToList();
            if (show.Count > 0) { view.UnhideElements(show); changed = true; }
        }
        catch (Exception) { }
        foreach (var over in vg.Elements ?? [])
            try
            {
                if (find(over.Key) is { } e && Write(view.GetElementOverrides(e.Id)) != over.Value) { view.SetElementOverrides(e.Id, Read(over.Value)); changed = true; }
            }
            catch (Exception) { }
        return changed;
    }

    /// <summary>Graphic overrides as "name=value" pairs, only what differs from none ("" when nothing is overridden).</summary>
    internal static string Write(OverrideGraphicSettings o)
    {
        var parts = new List<string>();
        void Colour(string k, Color c) { if (c.IsValid) parts.Add($"{k}={c.Red}.{c.Green}.{c.Blue}"); }
        void Id(string k, ElementId id) { if (id != ElementId.InvalidElementId) parts.Add($"{k}={id.Value.ToString(Inv)}"); }
        void Weight(string k, int w) { if (w > 0) parts.Add($"{k}={w.ToString(Inv)}"); }
        Colour("plc", o.ProjectionLineColor); Id("plp", o.ProjectionLinePatternId); Weight("plw", o.ProjectionLineWeight);
        Colour("clc", o.CutLineColor); Id("clp", o.CutLinePatternId); Weight("clw", o.CutLineWeight);
        Colour("sfc", o.SurfaceForegroundPatternColor); Id("sfp", o.SurfaceForegroundPatternId); if (!o.IsSurfaceForegroundPatternVisible) parts.Add("sfv=0");
        Colour("sbc", o.SurfaceBackgroundPatternColor); Id("sbp", o.SurfaceBackgroundPatternId); if (!o.IsSurfaceBackgroundPatternVisible) parts.Add("sbv=0");
        Colour("cfc", o.CutForegroundPatternColor); Id("cfp", o.CutForegroundPatternId); if (!o.IsCutForegroundPatternVisible) parts.Add("cfv=0");
        Colour("cbc", o.CutBackgroundPatternColor); Id("cbp", o.CutBackgroundPatternId); if (!o.IsCutBackgroundPatternVisible) parts.Add("cbv=0");
        if (o.Transparency > 0) parts.Add($"tr={o.Transparency.ToString(Inv)}");
        if (o.Halftone) parts.Add("ht=1");
        if (o.DetailLevel != ViewDetailLevel.Undefined) parts.Add($"dl={(int)o.DetailLevel}");
        return string.Join(",", parts);
    }

    internal static OverrideGraphicSettings Read(string text)
    {
        var o = new OverrideGraphicSettings();
        foreach (var part in text.Split([','], StringSplitOptions.RemoveEmptyEntries))
        {
            var cut = part.IndexOf('=');
            if (cut <= 0) continue;
            var (k, v) = (part.Substring(0, cut), part.Substring(cut + 1));
            Color C() { var c = v.Split('.'); return new Color(byte.Parse(c[0], Inv), byte.Parse(c[1], Inv), byte.Parse(c[2], Inv)); }
            ElementId E() => new(long.Parse(v, Inv));
            try
            {
                switch (k)
                {
                    case "plc": o.SetProjectionLineColor(C()); break;
                    case "plp": o.SetProjectionLinePatternId(E()); break;
                    case "plw": o.SetProjectionLineWeight(int.Parse(v, Inv)); break;
                    case "clc": o.SetCutLineColor(C()); break;
                    case "clp": o.SetCutLinePatternId(E()); break;
                    case "clw": o.SetCutLineWeight(int.Parse(v, Inv)); break;
                    case "sfc": o.SetSurfaceForegroundPatternColor(C()); break;
                    case "sfp": o.SetSurfaceForegroundPatternId(E()); break;
                    case "sfv": o.SetSurfaceForegroundPatternVisible(false); break;
                    case "sbc": o.SetSurfaceBackgroundPatternColor(C()); break;
                    case "sbp": o.SetSurfaceBackgroundPatternId(E()); break;
                    case "sbv": o.SetSurfaceBackgroundPatternVisible(false); break;
                    case "cfc": o.SetCutForegroundPatternColor(C()); break;
                    case "cfp": o.SetCutForegroundPatternId(E()); break;
                    case "cfv": o.SetCutForegroundPatternVisible(false); break;
                    case "cbc": o.SetCutBackgroundPatternColor(C()); break;
                    case "cbp": o.SetCutBackgroundPatternId(E()); break;
                    case "cbv": o.SetCutBackgroundPatternVisible(false); break;
                    case "tr": o.SetSurfaceTransparency(int.Parse(v, Inv)); break;
                    case "ht": o.SetHalftone(true); break;
                    case "dl": o.SetDetailLevel((ViewDetailLevel)int.Parse(v, Inv)); break;
                }
            }
            catch (Exception) { }
        }
        return o;
    }

    // ---- Line styles and object styles ------------------------------------------------------------------------------------

    /// <summary>
    /// A project's own style: the projection style of a category or subcategory of a built-in category (a line style is
    /// a subcategory of Lines). A link's or CAD file's layers are not the project's.
    /// </summary>
    public static bool IsStyle(Element e)
    {
        try
        {
            if (e is not GraphicsStyle { GraphicsStyleType: GraphicsStyleType.Projection } style || style.GraphicsStyleCategory is not { } c) return false;
            // Revit's own unnamed styles (one comes with a new text type) are not the project's.
            if (c.Name is not { Length: > 0 }) return false;
            return c.Parent is { } parent ? parent.Id.Value < 0 : c.Id.Value < 0;
        }
        catch (Exception) { return false; }
    }

    public static void ReadStyle(Document doc, GraphicsStyle style, M.Line line)
    {
        var c = style.GraphicsStyleCategory;
        line.Name = c.Name;
        var x = line.Extra ??= [];
        if (c.Parent is { } parent) x["parent"] = parent.Id.Value.ToString(Inv);
        else x["category"] = c.Id.Value.ToString(Inv);
        try { if (c.GetLineWeight(GraphicsStyleType.Projection) is { } w) x["weight"] = w.ToString(Inv); } catch (Exception) { }
        try { if (c.GetLineWeight(GraphicsStyleType.Cut) is { } w) x["cutweight"] = w.ToString(Inv); } catch (Exception) { }
        try { if (c.LineColor is { IsValid: true } colour) x["color"] = $"{colour.Red}.{colour.Green}.{colour.Blue}"; } catch (Exception) { }
        try { if (doc.GetElement(c.GetLinePatternId(GraphicsStyleType.Projection)) is { } pattern) x["pattern"] = pattern.UniqueId; else x["pattern"] = c.GetLinePatternId(GraphicsStyleType.Projection).Value.ToString(Inv); } catch (Exception) { }
        try { if (c.Material is { } material) x["material"] = material.UniqueId; } catch (Exception) { }
    }

    // ---- Sheet/view sets ---------------------------------------------------------------------------------------------------

    public static string? CanMakeSheetSet(M.Line line) =>
        line.Name is not { Length: > 0 } ? "a sheet set with no name in the file: make it again by hand"
        : M.SplitViews(line).Count == 0 ? "a sheet set with no views or sheets in the file: make it again by hand" : null;

    private static ViewSet ViewsOf(M.Line line, Func<string?, Element?> known, out int missing)
    {
        var set = new ViewSet();
        missing = 0;
        foreach (var unique in M.SplitViews(line))
            if (known(unique) is View view) set.Insert(view);
            else missing++;
        return set;
    }

    /// <summary>Makes the sheet/view set of the line again, through Revit's print manager (the only way Revit gives). Call inside a transaction.</summary>
    public static Element MakeSheetSet(Document doc, M.Line line, Func<string?, Element?> known)
    {
        var name = line.Name!;
        var views = ViewsOf(line, known, out var missing);
        if (views.Size == 0) throw new InvalidOperationException("none of the sheet set's views and sheets is in this model: make it again by hand");
        ViewSheetSet? Named() => new FilteredElementCollector(doc).OfClass(typeof(ViewSheetSet)).Cast<ViewSheetSet>().FirstOrDefault(s => s.Name == name);
        if (Named() is { } there) { GiveViews(there, views); return there; }
        try
        {
            var print = doc.PrintManager;
            print.PrintRange = PrintRange.Select;
            var setting = print.ViewSheetSetting;
            setting.CurrentViewSheetSet.Views = views;
            setting.SaveAs(name);
        }
        catch (Exception ex) { throw new InvalidOperationException($"Revit couldn't save the sheet set \"{name}\" ({ex.Message}): make it again by hand"); }
        return Named() ?? throw new InvalidOperationException($"Revit didn't keep the sheet set \"{name}\": make it again by hand");
    }

    /// <summary>Gives a sheet/view set that is in the model the views of its line; true when they differed. Call inside a transaction.</summary>
    public static bool SetSheetSet(ViewSheetSet set, M.Line line, Func<string?, Element?> known)
    {
        var want = ViewsOf(line, known, out _);
        if (want.Size == 0) return false;
        var have = new HashSet<ElementId>(set.Views.Cast<View>().Select(v => v.Id));
        var wanted = new HashSet<ElementId>(want.Cast<View>().Select(v => v.Id));
        if (have.SetEquals(wanted)) return false;
        GiveViews(set, want);
        return true;
    }

    /// <summary>
    /// A saved set takes its views only through the print manager (set on the element itself, Revit keeps the old
    /// ones), and through one settings object: each read of PrintManager.ViewSheetSetting is a new one.
    /// </summary>
    private static void GiveViews(ViewSheetSet set, ViewSet views)
    {
        var print = set.Document.PrintManager;
        print.PrintRange = PrintRange.Select;
        var setting = print.ViewSheetSetting;
        setting.CurrentViewSheetSet = set;
        setting.CurrentViewSheetSet.Views = views;
        setting.Save();
    }

    public static string? CanMakeStyle(M.Line line) =>
        line.Name is { Length: > 0 } && line.Extra is { } x && x.ContainsKey("parent") ? null : "a style made after the last save that the file can't make: make it again by hand";

    /// <summary>Makes the subcategory again (a line style under Lines, an object style's subcategory) and hands back its style.</summary>
    public static Element MakeStyle(Document doc, M.Line line)
    {
        var parentId = long.Parse(line.Extra!["parent"], Inv);
        var parent = doc.Settings.Categories.Cast<Category>().FirstOrDefault(c => c.Id.Value == parentId) ?? throw new InvalidOperationException("its category isn't in this model");
        var made = parent.SubCategories.Cast<Category>().FirstOrDefault(c => c.Name == line.Name) ?? doc.Settings.Categories.NewSubcategory(parent, line.Name);
        return made.GetGraphicsStyle(GraphicsStyleType.Projection) ?? throw new InvalidOperationException("Revit made the subcategory without a style");
    }

    /// <summary>Sets the style's weights, colour, pattern and material as its line has them; true when anything changed.</summary>
    public static bool SetStyle(Document doc, GraphicsStyle style, M.Line line, Func<string, Element?> find)
    {
        if (line.Extra is not { } x || style.GraphicsStyleCategory is not { } c) return false;
        var changed = false;
        try { if (x.TryGetValue("weight", out var w) && int.Parse(w, Inv) is var weight && c.GetLineWeight(GraphicsStyleType.Projection) != weight) { c.SetLineWeight(weight, GraphicsStyleType.Projection); changed = true; } } catch (Exception) { }
        try { if (x.TryGetValue("cutweight", out var w) && int.Parse(w, Inv) is var weight && c.GetLineWeight(GraphicsStyleType.Cut) != weight) { c.SetLineWeight(weight, GraphicsStyleType.Cut); changed = true; } } catch (Exception) { }
        try
        {
            if (x.TryGetValue("color", out var text))
            {
                var p = text.Split('.');
                var colour = new Color(byte.Parse(p[0], Inv), byte.Parse(p[1], Inv), byte.Parse(p[2], Inv));
                var now = c.LineColor;
                if (!now.IsValid || now.Red != colour.Red || now.Green != colour.Green || now.Blue != colour.Blue) { c.LineColor = colour; changed = true; }
            }
        }
        catch (Exception) { }
        try
        {
            if (x.TryGetValue("pattern", out var pattern))
            {
                var id = find(pattern)?.Id ?? (long.TryParse(pattern, NumberStyles.Integer, Inv, out var raw) ? new ElementId(raw) : null);
                if (id is not null && c.GetLinePatternId(GraphicsStyleType.Projection) != id) { c.SetLinePatternId(id, GraphicsStyleType.Projection); changed = true; }
            }
        }
        catch (Exception) { }
        try { if (x.TryGetValue("material", out var m) && find(m) is Material material && c.Material?.Id != material.Id) { c.Material = material; changed = true; } } catch (Exception) { }
        return changed;
    }
}
