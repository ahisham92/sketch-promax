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

    /// <summary>The services checked for openings through the structure.</summary>
    private static readonly List<BuiltInCategory> ServiceCats =
    [
        BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_PipeFitting, BuiltInCategory.OST_FlexPipeCurves, BuiltInCategory.OST_Conduit, BuiltInCategory.OST_ConduitFitting,
        BuiltInCategory.OST_CableTray, BuiltInCategory.OST_CableTrayFitting, BuiltInCategory.OST_DuctCurves, BuiltInCategory.OST_DuctFitting,
    ];

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
        public Transform T = Transform.Identity;
        public string Category = "";
        public BuiltInCategory Bic;
        public string Type = "";
        public string Family = "";
        public XYZ Center = XYZ.Zero;
        public string Workset = "";
        public bool InPlace;
        public bool NoAssembly;
        public bool NoTypeMark;
        public long TypeId;
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
        // A package of several berths is read one berth at a time (its status puts them together).
        var berths = Units(BerthsOf(setup, model)).Where(b => onlyBerths is null || onlyBerths.Contains(b.Key)).ToList();
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
        // Services (pipes, conduits, cable trays, ducts) from this model and every loaded link, for the openings check.
        var tolerance = Math.Max(1, setup.ClashToleranceMm) / 304.8;
        var services = new List<Placed>();
        var serviceSources = new List<string>();
        reading.Once("services", () =>
        {
            var filter = new ElementMulticategoryFilter(ServiceCats);
            void From(Document d, Transform t, string source, bool inHost)
            {
                var before = services.Count;
                foreach (var e in new FilteredElementCollector(d).WherePasses(filter).WhereElementIsNotElementType())
                    if (Place(e, t, source, inHost) is { } p) services.Add(p);
                if (services.Count > before) serviceSources.Add($"{(inHost ? "this model" : source)} ({services.Count - before})");
            }
            From(doc, Transform.Identity, "model", true);
            foreach (var inst in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
                if (inst.GetLinkDocument() is { } ld) From(ld, inst.GetTotalTransform(), ld.Title, false);
        });
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
                    // No planned counts: the structure is measured by what its drawings and schedules need, a Type Mark
                    // on every type (piles and beams are tagged and scheduled by it).
                    var built = mine.Where(i => ClashCats.Contains(i.Bic) || i.Bic == BuiltInCategory.OST_SpecialityEquipment).ToList();
                    r.Total = built.Count;
                    r.Done = built.Count(i => !i.NoTypeMark);
                    foreach (var g in built.Where(i => i.NoTypeMark).GroupBy(i => i.Describe).OrderByDescending(g => g.Count()).Take(200))
                        r.Findings.Add(new Finding(Modelling, g.Key, $"No Type Mark ({g.Count()} element{(g.Count() == 1 ? "" : "s")}): its tags and schedule rows come out empty", g.First().Linked ? null : g.First().Id));
                    r.Percent = built.Count == 0 ? null : Part(r.Done, r.Total);
                    r.Summary = built.Count == 0 ? $"{mine.Count} elements, none structural." :
                        $"{r.Done} of {r.Total} structural elements have a Type Mark. Add planned counts (Planned sheet) to measure against the plan too.";
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
                var bad = new List<(Item Item, string Problems)>();
                foreach (var i in mine)
                {
                    var problems = new List<string>();
                    if (i.Bic == BuiltInCategory.OST_GenericModel) problems.Add("Generic Models: put it on its real category");
                    if (i.InPlace) problems.Add("In-place family: make it a loadable family so it schedules and counts");
                    if (i.NoAssembly) problems.Add("Assembly Code empty: it won't land in the right bill item");
                    if (problems.Count == 0) { r.Done++; continue; }
                    bad.Add((i, string.Join("; ", problems)));
                }
                // One line per type and problem (not one per element): "Pile type: Assembly Code empty (120 elements)".
                foreach (var g in bad.GroupBy(b => (b.Item.Describe, b.Problems)).OrderByDescending(g => g.Count()).Take(300))
                    r.Findings.Add(new Finding(CategoryData, g.Key.Describe, $"{g.Key.Problems} ({g.Count()} element{(g.Count() == 1 ? "" : "s")})",
                        g.First().Item.Linked ? null : g.First().Item.Id, g.Key.Problems.Contains("Assembly Code") ? "uniformat" : null));
                r.Percent = mine.Count == 0 ? null : Part(r.Done, r.Total);
                r.Summary = mine.Count == 0 ? "No elements found for this berth." : $"{r.Total - r.Done} of {r.Total} elements need their category or data fixed.";
                res[CategoryData] = r;
            });

            // The berth's structure as clash shapes (this model only), with a box index for both clash checks.
            List<Placed>? structure = null;
            BoxIndex<Placed>? index = null;
            void Structure()
            {
                if (structure is not null) return;
                // The structure of a documentation model is in its included links: their elements take part, moved into place.
                structure = mine.Where(i => ClashCats.Contains(i.Bic)).OrderBy(i => i.Linked).ThenBy(i => i.Id).Take(ClashCap)
                    .Select(i => Place(i.El, i.T, i.Linked ? i.Link : "model", !i.Linked)).OfType<Placed>().ToList();
                index = new BoxIndex<Placed>(10);
                foreach (var p in structure) index.Add(p, p.Min, p.Max);
            }

            // Structural clashes deeper than the tolerance, a slice of elements at a time.
            var clashFound = new List<(Placed A, Placed B, double Depth)>();
            var clashSeen = new HashSet<string>();
            var clashIndex = 0;
            var clashSkipped = 0;
            reading.Add(Clashes + "|" + key, () =>
            {
                if (inBerth is null) return false;
                Structure();
                var stop = Math.Min(structure!.Count, clashIndex + 20);
                for (; clashIndex < stop; clashIndex++)
                {
                    var a = structure[clashIndex];
                    foreach (var b in index!.Overlapping(a.Min, a.Max, 0))
                    {
                        if (string.CompareOrdinal(a.Key, b.Key) >= 0 || !clashSeen.Add(a.Key + "|" + b.Key) || FindClashesTool.Related(a, b)) continue;
                        if (BoxDepth(a, b) < tolerance) continue;
                        // Revit can't intersect some solids ("unable to complete the centroid calculation"): that pair is
                        // counted as not checked, the rest go on.
                        try
                        {
                            var (hit, depth, _, _) = FindClashesTool.Hard(a, b, tolerance);
                            if (hit) clashFound.Add((a, b, depth));
                        }
                        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or InvalidOperationException or ArgumentException) { clashSkipped++; }
                    }
                }
                if (clashIndex < structure.Count) return true;
                var clashing = new HashSet<string>(clashFound.SelectMany(c => new[] { c.A.Key, c.B.Key }));
                var r = new AreaResult { Total = structure.Count, Done = structure.Count - clashing.Count };
                foreach (var (a, b, depth) in clashFound.Take(500))
                    r.Findings.Add(new Finding(Clashes, a.Describe(), $"runs {Mm(depth)} into {b.Describe()} (not joined)", a.InHost ? a.Element.Id.Value : b.InHost ? b.Element.Id.Value : null,
                        a.InHost && b.InHost ? $"join:{a.Element.Id.Value}:{b.Element.Id.Value}" : null));
                r.Percent = structure.Count == 0 ? null : Part(r.Done, r.Total);
                r.Summary = structure.Count == 0 ? "No structural elements found for this berth." :
                    $"{clashFound.Count} clashes deeper than {setup.ClashToleranceMm:0} mm between {clashing.Count} of {structure.Count} structural elements" +
                    (mine.Count(i => ClashCats.Contains(i.Bic)) > ClashCap ? $" (the first {ClashCap} checked)." : ".") +
                    (clashSkipped > 0 ? $" {clashSkipped} pairs Revit couldn't intersect were skipped." : "");
                res[Clashes] = r;
                return false;
            });

            // Services and openings: pipes, conduits, cable trays and ducts (this model and every loaded link) through the
            // berth's structure with no opening (an opening cuts the structure, so a service through it doesn't overlap).
            var serviceIndex = 0;
            var crossing = new Dictionary<string, Placed>(StringComparer.Ordinal);
            var blocked = new List<(Placed Service, Placed Structure, double Depth)>();
            reading.Add(Coordination + "|" + key, () =>
            {
                Structure();
                var stop = Math.Min(services.Count, serviceIndex + 40);
                for (; serviceIndex < stop; serviceIndex++)
                {
                    var svc = services[serviceIndex];
                    foreach (var st in index!.Overlapping(svc.Min, svc.Max, 0))
                    {
                        if (BoxDepth(svc, st) < tolerance) continue;
                        crossing[svc.Key] = svc;
                        try
                        {
                            var (hit, depth, _, _) = FindClashesTool.Hard(svc, st, tolerance);
                            if (hit) blocked.Add((svc, st, depth));
                        }
                        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or InvalidOperationException or ArgumentException) { }
                    }
                }
                if (serviceIndex < services.Count) return true;
                var r = new AreaResult();
                r.Findings.AddRange(linkProblems);
                var blockedKeys = new HashSet<string>(blocked.Select(b => b.Service.Key));
                foreach (var (svc, st, depth) in blocked.Take(500))
                    r.Findings.Add(new Finding(Coordination, svc.Describe(), $"passes {Mm(depth)} through {st.Describe()} with no opening", st.InHost ? st.Element.Id.Value : svc.InHost ? svc.Element.Id.Value : null));
                r.Total = crossing.Count;
                r.Done = crossing.Count - blockedKeys.Count;
                var parts = new List<double>();
                if (services.Count > 0) parts.Add(Part(r.Done, r.Total));
                var summary = services.Count == 0
                    ? "No pipes, conduits, cable trays or ducts in this model or its loaded links."
                    : $"{blockedKeys.Count} of {crossing.Count} services crossing the structure have no opening (tolerance {setup.ClashToleranceMm:0} mm; {services.Count} services checked from {string.Join(", ", serviceSources)}).";

                // The Navisworks clash report, when one is set.
                var report = model.ClashReport.Trim().Length == 0 ? "" : ChecklistMath.NewestFile(model.ClashReport, ".xml", ".html", ".htm");
                if (model.ClashReport.Trim().Length > 0 && report.Length == 0) summary += " No clash report found at " + model.ClashReport + ".";
                if (report.Length > 0)
                {
                    var tests = ChecklistMath.ClashReport(File.ReadAllText(report));
                    int open = tests.Sum(t => t.Open), closed = tests.Sum(t => t.Closed);
                    var ids = tests.Where(t => t.Ids is not null).SelectMany(t => t.Ids!).Distinct().ToList();
                    if (ids.Count > 0 && inBerth is not null)
                    {
                        // Only the open clashes on this berth's elements count when the report lists the element ids.
                        var mineIds = new HashSet<long>(mine.Where(i => !i.Linked).Select(i => i.Id));
                        var ours = ids.Where(mineIds.Contains).ToList();
                        foreach (var id in ours.Take(500)) r.Findings.Add(new Finding(Coordination, $"Element {id}", "In an open clash in the Navisworks clash report", id));
                        open = ours.Count;
                    }
                    else foreach (var t in tests.Where(t => t.Open > 0)) r.Findings.Add(new Finding(Coordination, t.Name, $"{t.Open} open clashes in the Navisworks clash report"));
                    parts.Add(Part(closed, open + closed));
                    summary += $" Clash report {Path.GetFileName(report)} ({File.GetLastWriteTime(report):dd-MMM HH:mm}): {open} open, {closed} resolved.";
                }
                // A link not loaded isn't checked: it is listed, but doesn't lower the status (an existing-structures link needn't be loaded).
                if (linkProblems.Count > 0) summary += $" {linkProblems.Count} of {linksTotal} links not loaded, so not checked: {string.Join(", ", linkProblems.Select(f => f.Item))}.";
                r.Percent = parts.Count == 0 ? null : parts.Average();
                r.Summary = summary;
                res[Coordination] = r;
                foreach (var svc in services) svc.Release();
                return false;
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
                var text = Values(berth.Sheets).Append(berth.Name.Trim()).Where(t => t.Length > 0).ToList();
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
                    // What this view must tag comes from the Tagging rules (a piles plan tags its piles); other views need dimensions only.
                    var sheetNames = viewSheets.TryGetValue(v.Id.Value, out var on) ? on.Select(x => x.Name) : [];
                    var tagCats = TagCats(doc, TagCategoriesFor(setup, v.Name, sheetNames));
                    var shown = tagCats.Count == 0 ? [] : new FilteredElementCollector(doc, v.Id).WherePasses(new ElementMulticategoryFilter(tagCats)).WhereElementIsNotElementType().Select(e => e.Id.Value).Take(2000).ToList();
                    var tags = new HashSet<long>();
                    foreach (var t in new FilteredElementCollector(doc, v.Id).OfClass(typeof(IndependentTag)).Cast<IndependentTag>())
                        try { foreach (var id in t.GetTaggedLocalElementIds()) tags.Add(id.Value); } catch (Autodesk.Revit.Exceptions.ApplicationException) { }
                    var missing = shown.Where(id => !tags.Contains(id)).ToList();
                    elements += shown.Count;
                    tagged += shown.Count - missing.Count;
                    if (missing.Count > 0)
                        foreach (var cat in tagCats)
                        {
                            var ofCat = missing.Count(id => doc.GetElement(new ElementId(id))?.Category?.Id == cat);
                            if (ofCat == 0) continue;
                            var catName = Category.GetCategory(doc, cat)?.Name ?? "elements";
                            annotation.Findings.Add(new Finding(Annotation, v.Name, $"{ofCat} {catName} without a tag", v.Id.Value, $"tag:{v.Id.Value}:{catName}"));
                        }
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
                annotation.Percent = annViews.Count == 0 ? null : elements == 0 ? Part(withDims, dimViews) : 0.7 * Part(tagged, elements) + 0.3 * Part(withDims, dimViews);
                annotation.Summary = annViews.Count == 0 ? "No plans or sections on this berth's sheets yet." :
                    (elements == 0 ? "No view here needs tags (Tagging sheet); " : $"{tagged} of {elements} elements tagged where the Tagging sheet asks; ") +
                    $"{withDims} of {dimViews} views dimensioned ({annViews.Count} views on the sheets).";
                res[Annotation] = annotation;
                return false;
            });

            reading.Once(Schedules + "|" + key, () =>
            {
                var r = new AreaResult();
                var texts = berth.ScheduleTexts;
                var text = string.Join("\" or \"", texts);
                var whole = berth.FindBy == FindBy.WholeModel && berth.Schedules.Trim().Length == 0;
                var found = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                    .Where(s => !s.IsTemplate && !s.IsTitleblockRevisionSchedule && !s.IsInternalKeynoteSchedule && !s.IsTitleblockRevisionSchedule
                                && (whole || texts.Any(t => s.Name.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0))).ToList();
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
                    El = el, Id = el.Id.Value, Linked = t is not null, Link = link, T = t ?? Transform.Identity, Category = cat.Name, Bic = bic,
                    Type = type?.Name ?? "", Family = type?.FamilyName ?? "", Center = t is null ? center : t.OfPoint(center),
                };
                if (worksets is not null) try { item.Workset = worksets.GetWorkset(el.WorksetId)?.Name ?? ""; } catch (Exception) { }
                if (el is FamilyInstance fi) try { item.InPlace = fi.Symbol?.Family?.IsInPlace == true; } catch (Exception) { }
                var code = type?.LookupParameter("Assembly Code");
                item.NoAssembly = code is not null && (code.AsString() ?? "").Trim().Length == 0;
                var mark = type?.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_MARK);
                item.NoTypeMark = mark is not null && (mark.AsString() ?? "").Trim().Length == 0;
                item.TypeId = type?.Id.Value ?? -1;
                yield return item;
            }
        }
    }

    /// <summary>An element as a clash shape in the model's coordinates (its box moved by the link's transform), or null without a box.</summary>
    private static Placed? Place(Element e, Transform t, string source, bool inHost)
    {
        var bb = e.get_BoundingBox(null);
        if (bb is null) return null;
        var p = new Placed(e, t, source, inHost);
        if (t.IsIdentity) { p.Min = bb.Min; p.Max = bb.Max; return p; }
        var corners = new[] { bb.Min, bb.Max, new XYZ(bb.Min.X, bb.Max.Y, bb.Min.Z), new XYZ(bb.Max.X, bb.Min.Y, bb.Min.Z),
            new XYZ(bb.Min.X, bb.Min.Y, bb.Max.Z), new XYZ(bb.Max.X, bb.Max.Y, bb.Min.Z), new XYZ(bb.Min.X, bb.Max.Y, bb.Max.Z), new XYZ(bb.Max.X, bb.Min.Y, bb.Max.Z) }
            .Select(t.OfPoint).ToList();
        p.Min = new XYZ(corners.Min(c => c.X), corners.Min(c => c.Y), corners.Min(c => c.Z));
        p.Max = new XYZ(corners.Max(c => c.X), corners.Max(c => c.Y), corners.Max(c => c.Z));
        return p;
    }

    /// <summary>How far two boxes overlap (their thinnest side of overlap; negative when apart).</summary>
    private static double BoxDepth(Placed a, Placed b) =>
        Math.Min(Math.Min(a.Max.X, b.Max.X) - Math.Max(a.Min.X, b.Min.X), Math.Min(Math.Min(a.Max.Y, b.Max.Y) - Math.Max(a.Min.Y, b.Min.Y), Math.Min(a.Max.Z, b.Max.Z) - Math.Max(a.Min.Z, b.Min.Z)));

    private static string Mm(double feet) => Math.Round(feet * 304.8).ToString("0", CultureInfo.InvariantCulture) + " mm";

    /// <summary>Whether an element belongs to the berth, or null (with why) when the berth's rule can't be used.</summary>
    private static Func<Item, bool>? Membership(Document doc, Berth berth, out string why)
    {
        why = "";
        var value = berth.Value.Trim();
        switch (berth.FindBy)
        {
            case FindBy.ScopeBox:
            {
                // A package of several berths lists several scope boxes ("BERTH 9; BERTH 10"): an element in any of them belongs.
                var boxes = ScopeBoxes(doc, value, out var missing);
                if (missing.Count > 0) { why = $"No scope box named \"{string.Join("\", \"", missing)}\" in this model (Berths sheet, Value)."; return null; }
                var shapes = boxes.Select(Footprint).ToList();
                if (shapes.Count == 0 || shapes.Any(f => f.Poly.Count < 3)) { why = $"Couldn't read the outline of scope box \"{value}\"."; return null; }
                return i => shapes.Any(f => i.Center.Z >= f.ZMin - 1 && i.Center.Z <= f.ZMax + 1 && Inside(f.Poly, i.Center.X, i.Center.Y));
            }
            case FindBy.Parameter:
                if (berth.Parameter.Trim().Length == 0) { why = "Set the Parameter column for this berth (Berths sheet)."; return null; }
                var name = berth.Parameter.Trim();
                var values = Values(value);
                return i =>
                {
                    var p = i.El.LookupParameter(name) ?? (i.El.Document.GetElement(i.El.GetTypeId()) as ElementType)?.LookupParameter(name);
                    var v = p is null ? "" : p.StorageType == StorageType.String ? p.AsString() ?? "" : p.AsValueString() ?? "";
                    return ValueMatches(v, values);
                };
            case FindBy.Workset:
                if (value.Length == 0) { why = "Set the Value column (text in the workset name) for this berth."; return null; }
                return i => i.Workset.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
            default:
                return _ => true;
        }
    }

    /// <summary>The scope boxes the berth's Value names (one or several), and the names not found.</summary>
    private static List<Element> ScopeBoxes(Document doc, string value, out List<string> missing)
    {
        var all = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_VolumeOfInterest).WhereElementIsNotElementType().ToList();
        var found = new List<Element>();
        missing = [];
        foreach (var name in Values(value))
            if (all.FirstOrDefault(b => string.Equals(b.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)) is { } box) found.Add(box);
            else missing.Add(name);
        if (found.Count == 0 && missing.Count == 0) missing.Add(value.Trim());
        return found;
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
        var texts = Values(berth.Sheets);
        if (texts.Count > 0)
            return sheets.Where(s => texts.Any(text => s.SheetNumber.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0 || s.Name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
        if (berth.FindBy == FindBy.WholeModel) return sheets;
        if (berth.FindBy == FindBy.ScopeBox)
        {
            var ids = ScopeBoxes(doc, berth.Value, out _).Select(b => b.Id).ToHashSet();
            if (ids.Count == 0) return [];
            return sheets.Where(s => s.GetAllPlacedViews().Select(doc.GetElement).OfType<View>()
                .Any(v => v.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP)?.AsElementId() is { } id && ids.Contains(id))).ToList();
        }
        return sheets.Where(s => s.SheetNumber.IndexOf(berth.Name, StringComparison.OrdinalIgnoreCase) >= 0 || s.Name.IndexOf(berth.Name, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
    }

    /// <summary>
    /// Runs a finding's Fix on the active model (Revit API context): "uniformat" fills the Assembly Codes from the project
    /// checklist's UniFormat rules, "tag:view:category" tags the untagged in that view, "join:a:b" joins two clashing elements.
    /// A sentence saying what changed.
    /// </summary>
    public static string RunFix(Autodesk.Revit.UI.UIApplication app, string fix, ChecklistMath.Definition? def)
    {
        var ui = app.ActiveUIDocument;
        var doc = ui.Document;
        var parts = fix.Split([':'], 3);
        switch (parts[0])
        {
            case "uniformat":
                return ChecklistMath.Rule(def, "uniformatmap").Trim().Length == 0
                    ? "Set the UniFormat code for each category in the project's checklist (Pre-submission checklist > Checking rules) first: the Assembly Codes come from there."
                    : ChecklistFixes.Run(ui, "uniformat", def);
            case "tag" when parts.Length == 3 && long.TryParse(parts[1], out var viewId):
            {
                var json = System.Text.Json.JsonSerializer.SerializeToElement(new { category = parts[2], view_id = viewId });
                var result = new TagElementsInViewTool().Execute(app, new ToolArgs(json.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())));
                var view = doc.GetElement(new ElementId(viewId)) as View;
                if (result.IsError) return result.Text;
                using var said = System.Text.Json.JsonDocument.Parse(result.Text);
                var tagged = said.RootElement.TryGetProperty("tagged", out var t) ? t.GetInt32() : 0;
                return $"Tagged {tagged} {parts[2]} in {view?.Name ?? "the view"}.";
            }
            case "join" when parts.Length == 3 && long.TryParse(parts[1], out var a) && long.TryParse(parts[2], out var b):
            {
                var ea = doc.GetElement(new ElementId(a));
                var eb = doc.GetElement(new ElementId(b));
                if (ea is null || eb is null) return "One of the two elements isn't in the model any more.";
                if (JoinGeometryUtils.AreElementsJoined(doc, ea, eb)) return $"{ea.Name} and {eb.Name} are already joined.";
                using var tx = new Transaction(doc, "Sketch ProMax: join clashing elements");
                tx.Start();
                try { JoinGeometryUtils.JoinGeometry(doc, ea, eb); }
                catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or ArgumentException) { tx.RollBack(); return $"Revit couldn't join {ea.Name} and {eb.Name} ({ex.Message}): move or cut one of them."; }
                tx.Commit();
                return $"Joined {ea.Name} and {eb.Name}.";
            }
            default:
                return "Nothing to fix here.";
        }
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
