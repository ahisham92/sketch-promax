using System.Globalization;
using System.IO;
using Autodesk.Revit.DB;
using static SketchProMax.Tools.BerthStatusMath;

namespace SketchProMax.Tools;

/// <summary>
/// Reads the open model for the berth status tracker (read only, nothing changes): for each berth of this model, the
/// areas BerthStatusMath scores (modelling against the plan, categories and data, coordination from the clash report,
/// structural clashes, drawings, details, annotation, schedules). It runs only when the user presses Update status, cut
/// into short steps (<see cref="Reading.RunFor"/>): the window gives Revit back between them, so a big model never
/// freezes. Each area is read on its own, so one that fails says so and the others still come.
/// </summary>
internal static class BerthStatusFacts
{
    /// <summary>The structural categories checked for clashes between each other.</summary>
    private static readonly BuiltInCategory[] ClashCats =
        [BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_StructuralFoundation, BuiltInCategory.OST_Floors, BuiltInCategory.OST_Walls];

    /// <summary>Model categories that are not built things (left out of the counts).</summary>
    private static readonly HashSet<BuiltInCategory> NotBuilt =
    [
        BuiltInCategory.OST_Lines, BuiltInCategory.OST_Rooms, BuiltInCategory.OST_Areas, BuiltInCategory.OST_MEPSpaces, BuiltInCategory.OST_RvtLinks,
        BuiltInCategory.OST_Cameras, BuiltInCategory.OST_VolumeOfInterest, BuiltInCategory.OST_SectionBox, BuiltInCategory.OST_IOSModelGroups,
        BuiltInCategory.OST_Grids, BuiltInCategory.OST_Levels, BuiltInCategory.OST_Topography, BuiltInCategory.OST_Viewers, BuiltInCategory.OST_Mass,
        BuiltInCategory.OST_ProjectBasePoint, BuiltInCategory.OST_SharedBasePoint, BuiltInCategory.OST_ReferenceLines, BuiltInCategory.OST_CLines,
    ];

    /// <summary>At most this many elements per berth are checked for structural clashes (the summary says when it stopped).</summary>
    private const int ClashCap = 3000;

    /// <summary>A built element found in the model (or a linked model), with what the areas need.</summary>
    private sealed class Item
    {
        public Element El = null!;
        public long Id;
        public bool Linked;
        public string Link = "";
        public string Category = "";
        public BuiltInCategory Bic;
        public string Type = "";
        public string Family = "";
        public XYZ Center = XYZ.Zero;
        public string Workset = "";
        public bool InPlace;
        public bool NoAssembly;
        public string Describe => $"{Category}: {(Family.Length > 0 && Family != Type ? Family + " : " : "")}{Type}{(Linked ? " (link " + Link + ")" : "")}";
    }

    public sealed class Reading
    {
        private readonly Document _doc;
        private readonly Queue<(string Name, Func<bool> Step)> _steps = new();
        internal Reading(Document doc) => _doc = doc;

        /// <summary>Per berth key, per area: what was read.</summary>
        public Dictionary<string, Dictionary<string, AreaResult>> Results { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<Berth> Berths { get; } = [];
        public string Model { get; set; } = "";
        public int Total { get; private set; }
        public int Left => _steps.Count;
        public string NextName => _steps.Count > 0 ? _steps.Peek().Name : "";

        /// <summary>A step: it returns true while it has more to do (it is called again), false when done.</summary>
        internal void Add(string name, Func<bool> step) { _steps.Enqueue((name, step)); Total++; }
        internal void Once(string name, Action step) => Add(name, () => { step(); return false; });

        /// <summary>Runs the next step, or a slice of it (Revit API context). False when nothing is left or the model was closed.</summary>
        public bool Next()
        {
            if (_steps.Count == 0) return false;
            if (!_doc.IsValidObject) { _steps.Clear(); return false; }
            var (name, step) = _steps.Peek();
            bool more;
            try { more = step(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Fail?.Invoke(name, ex); more = false; }
            if (!more) _steps.Dequeue();
            return true;
        }

        internal Action<string, Exception>? Fail;

        /// <summary>Runs steps for about <paramref name="budget"/>, at least one. False when done.</summary>
        public bool RunFor(TimeSpan budget)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            do { if (!Next()) return false; } while (clock.Elapsed < budget);
            return _steps.Count > 0;
        }
    }

    /// <summary>Reads everything in one call (field test and tests only: the window uses <see cref="Plan"/> and steps).</summary>
    public static Reading Read(Document doc, Setup setup, Model model)
    {
        var r = Plan(doc, setup, model);
        while (r.Next()) { }
        return r;
    }

    /// <summary>The reading of <paramref name="model"/>'s berths in <paramref name="doc"/>, as steps. Revit API context for this call and each step.</summary>
    public static Reading Plan(Document doc, Setup setup, Model model, IReadOnlyCollection<string>? onlyBerths = null)
    {
        var reading = new Reading(doc) { Model = doc.Title };
        var berths = BerthsOf(setup, model).Where(b => onlyBerths is null || onlyBerths.Contains(b.Key)).ToList();
        reading.Berths.AddRange(berths);
        foreach (var b in berths) reading.Results[b.Key] = new Dictionary<string, AreaResult>(StringComparer.Ordinal);
        reading.Fail = (name, ex) =>
        {
            // A step that fails marks its area for its berth (named "area|berth key"), or every berth for a shared step.
            var parts = name.Split(['|'], 2);
            foreach (var b in berths)
                if (parts.Length < 2 || string.Equals(parts[1], b.Key, StringComparison.OrdinalIgnoreCase))
                {
                    var area = parts.Length == 2 ? parts[0] : "";
                    if (Areas.Any(x => x.Key == area)) reading.Results[b.Key][area] = new AreaResult { Summary = "Couldn't read this from the model (" + ex.Message + ")." };
                }
        };
        if (berths.Count == 0) return reading;

        // ---- Shared: the built elements of the model and its included links, read in slices.
        var items = new List<Item>();
        var sources = new List<(Document Doc, Transform? T, string Link)> { (doc, null, "") };
        var linkProblems = new List<Finding>();
        var linksTotal = 0;
        foreach (var inst in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
        {
            linksTotal++;
            var linkDoc = inst.GetLinkDocument();
            var name = linkDoc?.Title ?? inst.Name;
            if (linkDoc is null) { linkProblems.Add(new Finding(Coordination, inst.Name, "Link not loaded")); continue; }
            var wanted = model.Links.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0);
            if (wanted.Any(w => name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0)) sources.Add((linkDoc, inst.GetTotalTransform(), name));
        }
        IEnumerator<Item>? walk = null;
        reading.Add("elements", () =>
        {
            walk ??= Walk(sources).GetEnumerator();
            for (var i = 0; i < 1500; i++)
            {
                if (!walk.MoveNext()) { walk.Dispose(); return false; }
                items.Add(walk.Current);
            }
            return true;
        });

        // Sheets, views on sheets and schedules: read once.
        List<ViewSheet> sheets = [];
        Dictionary<long, List<ViewSheet>> viewSheets = new();
        reading.Once("sheets", () =>
        {
            sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(s => !s.IsTemplate).ToList();
            foreach (var s in sheets)
                foreach (var id in s.GetAllPlacedViews())
                {
                    if (!viewSheets.TryGetValue(id.Value, out var on)) viewSheets[id.Value] = on = [];
                    on.Add(s);
                }
        });

        foreach (var berth in berths)
        {
            var key = berth.Key;
            var res = reading.Results[key];
            List<Item> mine = [];
            Func<Item, bool>? inBerth = null;
            reading.Once("find|" + key, () =>
            {
                inBerth = Membership(doc, berth, out var why);
                if (inBerth is null)
                {
                    foreach (var a in new[] { Modelling, CategoryData, Clashes })
                        res[a] = new AreaResult { Summary = why, Findings = [new Finding(a, berth.Name, why)] };
                    return;
                }
                mine = items.Where(inBerth).ToList();
            });

            reading.Once(Modelling + "|" + key, () =>
            {
                if (inBerth is null) return;
                var plan = PlannedElements(setup, berth);
                var r = new AreaResult();
                if (plan.Count == 0)
                {
                    r.Summary = $"{mine.Count} elements; no planned counts (add them on the setup's Planned sheet).";
                    r.Done = mine.Count;
                }
                else
                {
                    var parts = new List<double>();
                    foreach (var p in plan)
                    {
                        var n = mine.Count(i => string.Equals(i.Category, p.Category, StringComparison.OrdinalIgnoreCase)
                                                && (p.Type.Length == 0 || i.Type.IndexOf(p.Type, StringComparison.OrdinalIgnoreCase) >= 0 || i.Family.IndexOf(p.Type, StringComparison.OrdinalIgnoreCase) >= 0));
                        parts.Add(Part(Math.Min(n, p.Count), p.Count));
                        r.Done += Math.Min(n, p.Count);
                        r.Total += p.Count;
                        if (n < p.Count) r.Findings.Add(new Finding(Modelling, p.What, $"{n} of {p.Count} modelled ({p.Count - n} to go)"));
                        else if (n > p.Count) r.Findings.Add(new Finding(Modelling, p.What, $"{n} modelled, {n - p.Count} more than the {p.Count} planned: check the plan or duplicates"));
                    }
                    r.Percent = parts.Average();
                    r.Summary = $"{r.Done} of {r.Total} planned elements modelled.";
                }
                res[Modelling] = r;
            });

            reading.Once(CategoryData + "|" + key, () =>
            {
                if (inBerth is null) return;
                var r = new AreaResult { Total = mine.Count };
                foreach (var i in mine)
                {
                    var problems = new List<string>();
                    if (i.Bic == BuiltInCategory.OST_GenericModel) problems.Add("Generic Models: put it on its real category");
                    if (i.InPlace) problems.Add("In-place family: make it a loadable family so it schedules and counts");
                    if (i.NoAssembly) problems.Add("Assembly Code empty: it won't land in the right bill item");
                    if (problems.Count == 0) { r.Done++; continue; }
                    r.Findings.Add(new Finding(CategoryData, i.Describe, string.Join("; ", problems), i.Linked ? null : i.Id));
                }
                r.Percent = mine.Count == 0 ? null : Part(r.Done, r.Total);
                r.Summary = mine.Count == 0 ? "No elements found for this berth." : $"{r.Total - r.Done} of {r.Total} elements need their category or data fixed.";
                res[CategoryData] = r;
            });

            // Structural clashes, a slice of elements at a time.
            var clashPairs = new HashSet<(long, long)>();
            var clashing = new HashSet<long>();
            var clashIndex = 0;
            List<Item>? candidates = null;
            ICollection<ElementId>? candidateIds = null;
            reading.Add(Clashes + "|" + key, () =>
            {
                if (inBerth is null) return false;
                if (candidates is null)
                {
                    candidates = mine.Where(i => !i.Linked && ClashCats.Contains(i.Bic)).OrderBy(i => i.Id).Take(ClashCap).ToList();
                    candidateIds = candidates.Select(i => i.El.Id).ToList();
                }
                var stop = Math.Min(candidates.Count, clashIndex + 25);
                for (; clashIndex < stop; clashIndex++)
                {
                    var a = candidates[clashIndex];
                    var box = a.El.get_BoundingBox(null);
                    if (box is null || candidateIds!.Count < 2) continue;
                    var near = new FilteredElementCollector(doc, candidateIds).WherePasses(new BoundingBoxIntersectsFilter(new Outline(box.Min, box.Max)))
                        .WherePasses(new ElementIntersectsElementFilter(a.El));
                    foreach (var b in near)
                    {
                        var bId = b.Id.Value;
                        if (bId == a.Id) continue;
                        var pair = a.Id < bId ? (a.Id, bId) : (bId, a.Id);
                        if (clashPairs.Contains(pair)) continue;
                        try { if (JoinGeometryUtils.AreElementsJoined(doc, a.El, b)) continue; } catch (Autodesk.Revit.Exceptions.ApplicationException) { }
                        clashPairs.Add(pair);
                        clashing.Add(a.Id);
                        clashing.Add(bId);
                    }
                }
                if (clashIndex < candidates.Count) return true;
                var r = new AreaResult { Total = candidates.Count, Done = candidates.Count - clashing.Count };
                var byId = candidates.ToDictionary(i => i.Id);
                foreach (var (x, y) in clashPairs.Take(500))
                    r.Findings.Add(new Finding(Clashes, $"{byId[x].Describe} (id {x})", $"runs into {byId[y].Describe} (id {y}), not joined", x));
                r.Percent = candidates.Count == 0 ? null : Part(r.Done, r.Total);
                r.Summary = candidates.Count == 0 ? "No structural elements found for this berth." :
                    $"{clashPairs.Count} clashes between {clashing.Count} of {candidates.Count} structural elements" +
                    (mine.Count(i => !i.Linked && ClashCats.Contains(i.Bic)) > ClashCap ? $" (the first {ClashCap} checked)." : ".");
                res[Clashes] = r;
                return false;
            });

            reading.Once(Coordination + "|" + key, () =>
            {
                var r = new AreaResult();
                r.Findings.AddRange(linkProblems);
                var report = model.ClashReport.Trim().Length == 0 ? "" : ChecklistMath.NewestFile(model.ClashReport, ".xml", ".html", ".htm");
                if (report.Length == 0)
                {
                    r.Summary = (model.ClashReport.Trim().Length == 0 ? "No clash report set for this model (Models sheet, Clash report)." : "No clash report found at " + model.ClashReport + ".") +
                                (linkProblems.Count > 0 ? $" {linkProblems.Count} of {linksTotal} links not loaded." : "");
                    res[Coordination] = r;
                    return;
                }
                var tests = ChecklistMath.ClashReport(File.ReadAllText(report));
                int open = tests.Sum(t => t.Open), closed = tests.Sum(t => t.Closed);
                var ids = tests.Where(t => t.Ids is not null).SelectMany(t => t.Ids!).Distinct().ToList();
                if (ids.Count > 0 && inBerth is not null)
                {
                    // Only the open clashes on this berth's elements count when the report lists the element ids.
                    var mineIds = new HashSet<long>(mine.Where(i => !i.Linked).Select(i => i.Id));
                    var ours = ids.Where(mineIds.Contains).ToList();
                    foreach (var id in ours.Take(500)) r.Findings.Add(new Finding(Coordination, $"Element {id}", "In an open clash in the clash report", id));
                    open = ours.Count;
                }
                else foreach (var t in tests.Where(t => t.Open > 0)) r.Findings.Add(new Finding(Coordination, t.Name, $"{t.Open} open clashes"));
                r.Done = closed;
                r.Total = open + closed;
                var loaded = linksTotal == 0 ? 1 : (linksTotal - linkProblems.Count) / (double)linksTotal;
                r.Percent = Part(closed, open + closed) * loaded;
                r.Summary = $"{open} open and {closed} resolved clashes in {Path.GetFileName(report)} ({File.GetLastWriteTime(report):dd-MMM HH:mm})" +
                            (linkProblems.Count > 0 ? $"; {linkProblems.Count} of {linksTotal} links not loaded." : ".");
                res[Coordination] = r;
            });

            // Drawings: the berth's sheets.
            List<ViewSheet> mySheets = [];
            reading.Once(Drawings + "|" + key, () =>
            {
                mySheets = SheetsOf(doc, berth, sheets);
                var r = new AreaResult();
                double score = 0;
                foreach (var s in mySheets.OrderBy(s => s.SheetNumber, StringComparer.OrdinalIgnoreCase))
                {
                    var name = $"{s.SheetNumber} - {s.Name}";
                    if (s.IsPlaceholder) { score += 0.25; r.Findings.Add(new Finding(Drawings, name, "Placeholder sheet: not made yet", s.Id.Value)); continue; }
                    var views = s.GetAllPlacedViews().Count;
                    var titleBlock = new FilteredElementCollector(doc, s.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType().GetElementCount() > 0;
                    if (views == 0) r.Findings.Add(new Finding(Drawings, name, "No views placed on it", s.Id.Value));
                    if (!titleBlock) r.Findings.Add(new Finding(Drawings, name, "No title block", s.Id.Value));
                    var part = (views > 0 ? 0.75 : 0.4) + (titleBlock ? 0.25 : 0);
                    if (part >= 1) r.Done++;
                    score += part;
                }
                var planned = PlannedCount(setup, berth, "Drawings");
                r.Total = Math.Max(planned ?? 0, mySheets.Count);
                if (planned is { } p && mySheets.Count < p) r.Findings.Insert(0, new Finding(Drawings, berth.Name, $"{mySheets.Count} of {p} planned drawings exist ({p - mySheets.Count} to make)"));
                r.Percent = r.Total == 0 ? null : Part(score, r.Total);
                r.Summary = mySheets.Count == 0 && planned is null
                    ? "No sheets found for this berth (set Sheets contain on the Berths sheet, and the planned Drawings count)."
                    : $"{r.Done} of {r.Total} drawings complete" + (planned is null ? " (no planned count)." : $" against {planned} planned.");
                res[Drawings] = r;
            });

            // Details: detail, section and drafting views on the berth's sheets; the berth's details on no sheet.
            reading.Once(Details + "|" + key, () =>
            {
                var r = new AreaResult();
                var onSheets = new HashSet<long>(mySheets.SelectMany(s => s.GetAllPlacedViews()).Select(i => i.Value));
                var text = new[] { berth.Sheets, berth.Name }.Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
                var detailViews = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .Where(v => !v.IsTemplate && v.ViewType is ViewType.Detail or ViewType.DraftingView or ViewType.Section)
                    .Where(v => onSheets.Contains(v.Id.Value) || (!viewSheets.ContainsKey(v.Id.Value) && text.Any(t => v.Name.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0)))
                    .ToList();
                foreach (var v in detailViews)
                {
                    var placed = onSheets.Contains(v.Id.Value);
                    var empty = v.ViewType == ViewType.DraftingView && new FilteredElementCollector(doc, v.Id).WhereElementIsNotElementType().GetElementCount() == 0;
                    if (!placed) r.Findings.Add(new Finding(Details, v.Name, "Detail not on a sheet", v.Id.Value));
                    else if (empty) r.Findings.Add(new Finding(Details, v.Name, "Empty drafting view on a sheet: the detail is missing", v.Id.Value));
                    else r.Done++;
                }
                var planned = PlannedCount(setup, berth, "Details");
                r.Total = Math.Max(planned ?? 0, detailViews.Count);
                if (planned is { } p && r.Done < p) r.Findings.Insert(0, new Finding(Details, berth.Name, $"{r.Done} of {p} planned details on sheets ({p - r.Done} to do)"));
                r.Percent = r.Total == 0 ? null : Part(r.Done, r.Total);
                r.Summary = r.Total == 0 ? "No details found for this berth (add the planned Details count)." : $"{r.Done} of {r.Total} details done and on a sheet.";
                res[Details] = r;
            });

            // Annotation: one view per slice.
            var tagCats = TagCats(doc, setup.TagCategories);
            var annViews = new List<View>();
            var annIndex = 0;
            int elements = 0, tagged = 0, withDims = 0, dimViews = 0;
            var annotation = new AreaResult();
            reading.Add(Annotation + "|" + key, () =>
            {
                if (annIndex == 0 && annViews.Count == 0)
                {
                    annViews = mySheets.SelectMany(s => s.GetAllPlacedViews()).Distinct().Select(id => doc.GetElement(id)).OfType<View>()
                        .Where(v => v.ViewType is ViewType.FloorPlan or ViewType.EngineeringPlan or ViewType.CeilingPlan or ViewType.Section or ViewType.Elevation or ViewType.Detail)
                        .ToList();
                    if (annViews.Count == 0) annIndex = -1;
                }
                if (annIndex >= 0 && annIndex < annViews.Count)
                {
                    var v = annViews[annIndex++];
                    var shown = tagCats.Count == 0 ? [] : new FilteredElementCollector(doc, v.Id).WherePasses(new ElementMulticategoryFilter(tagCats)).WhereElementIsNotElementType().Select(e => e.Id.Value).Take(2000).ToList();
                    var tags = new HashSet<long>();
                    foreach (var t in new FilteredElementCollector(doc, v.Id).OfClass(typeof(IndependentTag)).Cast<IndependentTag>())
                        try { foreach (var id in t.GetTaggedLocalElementIds()) tags.Add(id.Value); } catch (Autodesk.Revit.Exceptions.ApplicationException) { }
                    var missing = shown.Where(id => !tags.Contains(id)).ToList();
                    elements += shown.Count;
                    tagged += shown.Count - missing.Count;
                    if (missing.Count > 0) annotation.Findings.Add(new Finding(Annotation, v.Name, $"{missing.Count} of {shown.Count} elements without a tag", v.Id.Value));
                    if (v.ViewType is not ViewType.Detail)
                    {
                        dimViews++;
                        if (new FilteredElementCollector(doc, v.Id).OfClass(typeof(Dimension)).GetElementCount() > 0) withDims++;
                        else annotation.Findings.Add(new Finding(Annotation, v.Name, "No dimensions in this view", v.Id.Value));
                    }
                    if (annIndex < annViews.Count) return true;
                }
                annotation.Total = annViews.Count;
                annotation.Done = annViews.Count - annotation.Findings.Select(f => f.Id).Distinct().Count();
                annotation.Percent = annViews.Count == 0 ? null : 0.7 * Part(tagged, elements) + 0.3 * Part(withDims, dimViews);
                annotation.Summary = annViews.Count == 0 ? "No plans or sections on this berth's sheets yet." :
                    $"{tagged} of {elements} elements tagged; {withDims} of {dimViews} views dimensioned ({annViews.Count} views on the sheets).";
                res[Annotation] = annotation;
                return false;
            });

            reading.Once(Schedules + "|" + key, () =>
            {
                var r = new AreaResult();
                var text = berth.ScheduleText;
                var found = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                    .Where(s => !s.IsTemplate && !s.IsTitleblockRevisionSchedule && s.Name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                foreach (var s in found)
                {
                    int rows;
                    try { rows = s.GetTableData().GetSectionData(SectionType.Body).NumberOfRows; } catch (Exception) { rows = 0; }
                    if (rows <= 1) r.Findings.Add(new Finding(Schedules, s.Name, "Empty schedule", s.Id.Value));
                    else r.Done++;
                    if (!viewSheets.ContainsKey(s.Id.Value)) r.Findings.Add(new Finding(Schedules, s.Name, "Not on a sheet (fine for a working schedule)", s.Id.Value));
                }
                var planned = PlannedCount(setup, berth, "Schedules");
                r.Total = Math.Max(planned ?? 0, found.Count);
                if (found.Count == 0) r.Findings.Add(new Finding(Schedules, berth.Name, $"No schedule with \"{text}\" in its name"));
                else if (planned is { } p && found.Count < p) r.Findings.Insert(0, new Finding(Schedules, berth.Name, $"{found.Count} of {p} planned schedules exist"));
                r.Percent = r.Total == 0 ? 0 : Part(r.Done, r.Total);
                r.Summary = found.Count == 0 ? $"No schedules with \"{text}\" in the name." : $"{r.Done} of {r.Total} schedules filled.";
                res[Schedules] = r;
            });
        }
        return reading;
    }

    /// <summary>The built elements of each source (the model, then its included links), with link elements moved into the model's coordinates.</summary>
    private static IEnumerable<Item> Walk(List<(Document Doc, Transform? T, string Link)> sources)
    {
        foreach (var (doc, t, link) in sources)
        {
            var worksets = doc.IsWorkshared ? doc.GetWorksetTable() : null;
            foreach (var el in new FilteredElementCollector(doc).WhereElementIsNotElementType().WhereElementIsViewIndependent())
            {
                var cat = el.Category;
                if (cat is null || cat.CategoryType != CategoryType.Model) continue;
                var bic = cat.BuiltInCategory;
                if (NotBuilt.Contains(bic) || el.GetTypeId() == ElementId.InvalidElementId) continue;
                var box = el.get_BoundingBox(null);
                if (box is null) continue;
                var type = el.Document.GetElement(el.GetTypeId()) as ElementType;
                var center = (box.Min + box.Max) / 2;
                var item = new Item
                {
                    El = el, Id = el.Id.Value, Linked = t is not null, Link = link, Category = cat.Name, Bic = bic,
                    Type = type?.Name ?? "", Family = type?.FamilyName ?? "", Center = t is null ? center : t.OfPoint(center),
                };
                if (worksets is not null) try { item.Workset = worksets.GetWorkset(el.WorksetId)?.Name ?? ""; } catch (Exception) { }
                if (el is FamilyInstance fi) try { item.InPlace = fi.Symbol?.Family?.IsInPlace == true; } catch (Exception) { }
                var code = type?.LookupParameter("Assembly Code");
                item.NoAssembly = code is not null && (code.AsString() ?? "").Trim().Length == 0;
                yield return item;
            }
        }
    }

    /// <summary>Whether an element belongs to the berth, or null (with why) when the berth's rule can't be used.</summary>
    private static Func<Item, bool>? Membership(Document doc, Berth berth, out string why)
    {
        why = "";
        var value = berth.Value.Trim();
        switch (berth.FindBy)
        {
            case FindBy.ScopeBox:
                var box = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_VolumeOfInterest).WhereElementIsNotElementType()
                    .FirstOrDefault(b => string.Equals(b.Name.Trim(), value, StringComparison.OrdinalIgnoreCase));
                if (box is null) { why = $"No scope box named \"{value}\" in this model (Berths sheet, Value)."; return null; }
                var (poly, zMin, zMax) = Footprint(box);
                if (poly.Count < 3) { why = $"Couldn't read the outline of scope box \"{value}\"."; return null; }
                return i => i.Center.Z >= zMin - 1 && i.Center.Z <= zMax + 1 && Inside(poly, i.Center.X, i.Center.Y);
            case FindBy.Parameter:
                if (berth.Parameter.Trim().Length == 0) { why = "Set the Parameter column for this berth (Berths sheet)."; return null; }
                var name = berth.Parameter.Trim();
                return i =>
                {
                    var p = i.El.LookupParameter(name) ?? (i.El.Document.GetElement(i.El.GetTypeId()) as ElementType)?.LookupParameter(name);
                    var v = p is null ? "" : p.StorageType == StorageType.String ? p.AsString() ?? "" : p.AsValueString() ?? "";
                    return string.Equals(v.Trim(), value, StringComparison.OrdinalIgnoreCase);
                };
            case FindBy.Workset:
                if (value.Length == 0) { why = "Set the Value column (text in the workset name) for this berth."; return null; }
                return i => i.Workset.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
            default:
                return _ => true;
        }
    }

    /// <summary>The scope box's outline in plan (its corners, in order) and its height range.</summary>
    private static (List<(double X, double Y)> Poly, double ZMin, double ZMax) Footprint(Element box)
    {
        var points = new List<XYZ>();
        var geo = box.get_Geometry(new Options());
        if (geo is not null)
            foreach (var g in geo)
                if (g is Curve c) { points.Add(c.GetEndPoint(0)); points.Add(c.GetEndPoint(1)); }
        var bb = box.get_BoundingBox(null);
        if (points.Count == 0 && bb is not null) points.AddRange([bb.Min, new XYZ(bb.Max.X, bb.Min.Y, 0), bb.Max, new XYZ(bb.Min.X, bb.Max.Y, 0)]);
        var zMin = points.Count > 0 ? points.Min(p => p.Z) : 0;
        var zMax = points.Count > 0 ? points.Max(p => p.Z) : 0;
        if (bb is not null) { zMin = Math.Min(zMin, bb.Min.Z); zMax = Math.Max(zMax, bb.Max.Z); }
        return (Hull(points.Select(p => (p.X, p.Y))), zMin, zMax);
    }

    /// <summary>The convex hull of points (counter-clockwise).</summary>
    internal static List<(double X, double Y)> Hull(IEnumerable<(double X, double Y)> input)
    {
        var pts = input.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        if (pts.Count < 3) return pts;
        static double Cross((double X, double Y) o, (double X, double Y) a, (double X, double Y) b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var hull = new List<(double X, double Y)>();
        foreach (var pass in new[] { pts, Enumerable.Reverse(pts).ToList() })
        {
            var start = hull.Count;
            foreach (var p in pass)
            {
                while (hull.Count >= start + 2 && Cross(hull[hull.Count - 2], hull[hull.Count - 1], p) <= 1e-9) hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            hull.RemoveAt(hull.Count - 1);
        }
        return hull;
    }

    /// <summary>Whether (x, y) is inside the convex polygon (counter-clockwise), edges included.</summary>
    internal static bool Inside(List<(double X, double Y)> poly, double x, double y)
    {
        for (var i = 0; i < poly.Count; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Count];
            if ((b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X) < -1e-6) return false;
        }
        return true;
    }

    /// <summary>
    /// The berth's sheets: those with its text (Sheets contain) in the number or name; without the text, those showing a
    /// view cropped by the berth's scope box, or every sheet when the berth is the whole model.
    /// </summary>
    private static List<ViewSheet> SheetsOf(Document doc, Berth berth, List<ViewSheet> sheets)
    {
        var text = berth.Sheets.Trim();
        if (text.Length > 0)
            return sheets.Where(s => s.SheetNumber.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0 || s.Name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        if (berth.FindBy == FindBy.WholeModel) return sheets;
        if (berth.FindBy == FindBy.ScopeBox)
        {
            var box = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_VolumeOfInterest).WhereElementIsNotElementType()
                .FirstOrDefault(b => string.Equals(b.Name.Trim(), berth.Value.Trim(), StringComparison.OrdinalIgnoreCase));
            if (box is null) return [];
            return sheets.Where(s => s.GetAllPlacedViews().Select(doc.GetElement).OfType<View>()
                .Any(v => v.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP)?.AsElementId() == box.Id)).ToList();
        }
        return sheets.Where(s => s.SheetNumber.IndexOf(berth.Name, StringComparison.OrdinalIgnoreCase) >= 0 || s.Name.IndexOf(berth.Name, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
    }

    private static List<ElementId> TagCats(Document doc, IEnumerable<string> names)
    {
        var ids = new List<ElementId>();
        foreach (var n in names)
            try { ids.Add(new ElementId(Revit.RevitHelpers.ParseCategory(doc, n))); } catch (ArgumentException) { }
        return ids;
    }

    /// <summary>A short line on the reading for the activity log.</summary>
    public static string Describe(Reading r) => string.Join("; ", r.Results.Select(b => b.Key + ": " +
        string.Join(", ", b.Value.Select(a => a.Key + " " + (a.Value.Percent is { } p ? p.ToString("0", CultureInfo.InvariantCulture) + "%" : "-")))));
}
