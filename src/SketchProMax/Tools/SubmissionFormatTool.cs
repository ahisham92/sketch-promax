using System.Globalization;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SketchProMax.Tools;

/// <summary>
/// The built-in format of a berth's drawing submission (SubmissionFormat) as a tool: action=show gives the sheets a
/// berth gets with their numbers, names, views, scales and schedules, so a submission is planned from the list and not
/// from zero; action=check reads the model's sheets and says, line by line, what is missing or differs. Reads only.
/// </summary>
public sealed class SubmissionFormatTool : IRevitTool
{
    public const string ToolName = "submission_format";

    public string Name => ToolName;
    public string Description =>
        "The built-in format of a berth's drawing submission, taken from the final design submissions already issued: which " +
        "sheets a berth gets (overall layout, piles plan and framing plan with elevation per part, a cross section per design " +
        "section, pile details), their numbers and names, the views on each with their titles and scales, the schedules, and " +
        "what the QC sheet checks on every sheet. action=show gives the format (give berth for its exact titles): call it " +
        "BEFORE planning or starting a berth's sheets, and build exactly that list. action=check reads the model's sheets " +
        "against it (berths: which ones; none = every berth with sheets) and lists every sheet missing, wrong name or view " +
        "title, scale that is not standard or differs between a berth's parts, view running off the sheet, missing " +
        "schedule, empty sheet, empty or overlapping tag, pile inside the matchlines with no tag, tag on a pile beyond the matchline, key plan highlighting another berth, " +
        "issue date written another way, drawing cited that is not a sheet, and typed-over dimension, with the fix: call it BEFORE " +
        "saying a submission is done; the work is done when it lists nothing. In a big model one call reads the tags of " +
        "only as many views as fit in about a minute: while its note says NOT COMPLETE, call check again. Scales are usual ones, not fixed: a view too " +
        "long for the sheet takes the next standard scale. 'look' in the result is what to see by eye. Changes nothing.";
    public string PropertiesJson => """
    {
      "action": {"type": "string", "enum": ["show", "check"], "description": "Default show."},
      "berth": {"type": "integer", "description": "show: the berth number, for its exact titles."},
      "berths": {"type": "array", "items": {"type": "integer"}, "description": "check: the berths to check. Default: every berth that has sheets."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var action = (args.OptString("action") ?? "show").Trim().ToLowerInvariant();
        switch (action)
        {
            case "show":
                return ToolResult.Ok(new
                {
                    format = SubmissionFormat.Show((int)(args.OptLong("berth") ?? 0)),
                    note = "Build exactly this list for the berth; then run action=check and fix what it lists before saying the submission is done.",
                });
            case "check":
            {
                var doc = app.ActiveUIDocument?.Document;
                if (doc is null) return ToolResult.Error("No model is open.");
                var berths = args.LongList("berths").Select(b => (int)b).Where(b => b > 0).Distinct().ToList();
                if (berths.Count == 0 && args.OptLong("berth") is > 0 and var one) berths.Add((int)one);
                var sheets = Read(doc, berths);
                var numbered = sheets.Count(s => SubmissionFormat.Parse(s.Number) is { Berth: > 0 });
                if (numbered == 0)
                    return ToolResult.Ok(new
                    {
                        sheets = sheets.Count, berthSheets = 0,
                        note = "No sheet here is numbered <prefix>-<berth>-<discipline>-<series>, so there is nothing to compare. Call action=show for the list to build.",
                    });
                var issues = SubmissionFormat.Check(sheets, berths);
                var pending = sheets.Sum(s => s.Views.Count(v => v.NotJudged));
                var matrix = SubmissionFormat.Matrix(issues);
                var checkedBerths = (berths.Count > 0 ? berths : sheets.Select(s => SubmissionFormat.Parse(s.Number)?.Berth ?? 0).Where(b => b > 0).Distinct().ToList()).OrderBy(b => b).ToList();
                return ToolResult.Ok(new
                {
                    berths = checkedBerths,
                    sheetsChecked = sheets.Count(s => SubmissionFormat.Parse(s.Number) is { } at && (at.Berth == 0 || checkedBerths.Contains(at.Berth))),
                    open = issues.Count,
                    viewsNotReadYet = pending,
                    issues = issues.Select(i => new { sheet = i.Sheet, check = i.Check, problem = i.Problem, fix = i.Fix }).ToList(),
                    sheetsWithOpenItems = matrix.Select(m => new { sheet = m.Key, checks = m.Value }).ToList(),
                    look = SubmissionFormat.Look(sheets, checkedBerths),
                    note = pending > 0
                        ? $"NOT COMPLETE: {pending} view(s) are not read yet (Revit takes long to draw a full plan the first time). Call check again with the same berths until this note goes; each call carries on from where the last stopped."
                        : issues.Count == 0
                        ? "Every line of the format that can be read from the model holds. Go through 'look', and see each sheet once (capture_view_image) before saying it is done."
                        : "The submission is not done while this lists anything: fix each line, then check again.",
                });
            }
            default:
                return ToolResult.Error("action is show or check.");
        }
    }

    /// <summary>
    /// Every sheet with its number and name; the sheets of <paramref name="berths"/> (all numbered sheets when empty) also
    /// with their views (title on sheet, scale), schedules, issue date, the drawing numbers their notes cite and their
    /// typed-over dimensions.
    /// </summary>
    /// <remarks>
    /// Tags and matchlines need Revit to draw the view, which takes up to a minute the first time for a full plan of a
    /// big model (measured on a berths model, 2026-11-17: 13 minutes for two berths; a viewport's box alone about 2 s
    /// per view not drawn yet). So everything read inside a view (its box on the sheet, notes, dimensions, tags,
    /// matchlines) is read while <paramref name="budgetMs"/> lasts; the views after that are marked not judged, and the
    /// next call carries on quickly through the ones Revit has drawn already. The view being read when the time runs
    /// out is finished, so a call can take up to a minute longer than the budget.
    /// </remarks>
    internal static List<SubmissionFormat.Sheet> Read(Document doc, IReadOnlyCollection<int> berths, int budgetMs = 40000)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var all = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(s => !s.IsPlaceholder).ToList();
        var index = new Index(doc);
        var result = new List<SubmissionFormat.Sheet>();
        foreach (var s in all)
        {
            var sheet = new SubmissionFormat.Sheet { Number = s.SheetNumber ?? "", Name = s.Name ?? "" };
            result.Add(sheet);
            if (SubmissionFormat.Parse(sheet.Number) is not { } at || (at.Berth > 0 && berths.Count > 0 && !berths.Contains(at.Berth))) continue;
            sheet.IssueDate = s.get_Parameter(BuiltInParameter.SHEET_ISSUE_DATE)?.AsString() ?? "";
            sheet.Schedules = index.Schedules[s.Id.Value].Select(i => doc.GetElement(i.ScheduleId)?.Name ?? "").Where(n => n.Length > 0).ToList();
            Notes(index.Notes[s.Id.Value], sheet);
            KeyPlan(doc, index, s, sheet);
            // The general sheets (berth 00) are read for their notes and dimensions only: the format has no rule for their views.
            var kind = at.Berth > 0 ? SubmissionFormat.KindOf(at.Series, sheet.Name)?.Key : null;
            // Past the time for drawing views, a sheet's views keep their title and scale only: asking a sheet for its
            // outline or a viewport for its box also makes Revit draw what it has not drawn yet.
            var frame = clock.ElapsedMilliseconds > budgetMs ? null : s.Outline;
            foreach (var port in index.Ports[s.Id.Value])
            {
                if (doc.GetElement(port.ViewId) is not View view || view.ViewType == ViewType.Legend) continue;
                var title = view.get_Parameter(BuiltInParameter.VIEW_DESCRIPTION)?.AsString();
                var placed = new SubmissionFormat.PlacedView { Title = string.IsNullOrWhiteSpace(title) ? view.Name : title!, Scale = view.Scale };
                sheet.Views.Add(placed);
                // A key plan placed as a view is not a drawing of the sheet: nothing more is read in it.
                if (IsKeyPlan(view)) continue;
                if (frame is null || clock.ElapsedMilliseconds > budgetMs) { placed.NotJudged = true; continue; }
                try
                {
                    var box = port.GetBoxOutline();
                    const double slack = 0.02; // about 6 mm on paper
                    placed.OffSheet = (box.MinimumPoint.X < frame.Min.U - slack || box.MinimumPoint.Y < frame.Min.V - slack
                                                         || box.MaximumPoint.X > frame.Max.U + slack || box.MaximumPoint.Y > frame.Max.V + slack);
                    if (placed.OffSheet) placed.OffSheetAroundOnly = CropOnSheet(view, port, frame, slack);
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException) { /* a viewport with no box: not judged */ }
                // A dependent view (one part of a plan) owns no annotation: its tags, notes and dimensions belong to its
                // primary view, and the ones inside its crop are its own.
                var owner = view.GetPrimaryViewId() is { } primary && primary != ElementId.InvalidElementId ? primary : view.Id;
                var here = owner == view.Id ? _ => true : InCrop(view);
                Notes(index.Notes[owner.Value].Where(x => here(x)), sheet);
                sheet.TypedDimensions += TypedDimensions(index.Dimensions[owner.Value].Where(x => here(x)));
                if (at.Berth == 0) continue;
                // Asking a view what it shows is slow in a big model: once per plan for its matchlines, and once per tagged
                // category on a piles plan. Tags, notes and dimensions come from the model's lists by their view.
                var plan = view.ViewType is ViewType.FloorPlan or ViewType.EngineeringPlan or ViewType.CeilingPlan or ViewType.AreaPlan;
                var shown = plan && index.Matchlines.Count > 0
                    ? new FilteredElementCollector(doc, view.Id).OfCategory(BuiltInCategory.OST_Matchline).WhereElementIsNotElementType().ToElementIds().Select(i => i.Value).ToHashSet()
                    : [];
                Tags(doc, index, index.Tags[owner.Value].Where(x => here(x)), view, placed, kind == "piles", shown);
                if (plan) placed.MatchlinesPastCrop = MatchlinesPastCrop(doc, index, view, shown);
            }
            sheet.Cited = sheet.Cited.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        return result;
    }

    /// <summary>The model's annotations by the view that owns them, read once: asking each view for them is far slower.</summary>
    private sealed class Index(Document doc)
    {
        public readonly ILookup<long, ScheduleSheetInstance> Schedules = new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>()
            .Where(i => !i.IsTitleblockRevisionSchedule).ToLookup(i => i.OwnerViewId.Value);
        public readonly ILookup<long, Viewport> Ports = new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>().ToLookup(v => v.SheetId.Value);
        public readonly ILookup<long, Element> Blocks = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType().ToLookup(e => e.OwnerViewId.Value);
        public readonly ILookup<long, IndependentTag> Tags = new FilteredElementCollector(doc).OfClass(typeof(IndependentTag)).Cast<IndependentTag>().ToLookup(t => t.OwnerViewId.Value);
        public readonly ILookup<long, TextNote> Notes = new FilteredElementCollector(doc).OfClass(typeof(TextNote)).Cast<TextNote>().ToLookup(t => t.OwnerViewId.Value);
        public readonly ILookup<long, Dimension> Dimensions = new FilteredElementCollector(doc).OfClass(typeof(Dimension)).Cast<Dimension>().ToLookup(t => t.OwnerViewId.Value);
        public readonly List<Element> Matchlines = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Matchline).WhereElementIsNotElementType().ToList();
    }

    private static bool IsKeyPlan(View view) =>
        Regex.IsMatch(view.Name + " " + (view.get_Parameter(BuiltInParameter.VIEW_DESCRIPTION)?.AsString() ?? ""), @"KEY\s*PLAN", RegexOptions.IgnoreCase);

    /// <summary>
    /// The key plan, as far as the model describes it: a view on the sheet named KEY PLAN (its name says the berth), or
    /// yes/no parameters of the title block named for a berth or the key plan (the ones switched on).
    /// </summary>
    private static void KeyPlan(Document doc, Index index, ViewSheet s, SubmissionFormat.Sheet sheet)
    {
        foreach (var port in index.Ports[s.Id.Value])
            if (doc.GetElement(port.ViewId) is View view && IsKeyPlan(view))
            {
                sheet.KeyPlanFound = true;
                sheet.KeyPlanOn.AddRange(SubmissionFormat.SwitchBerths(view.Name + " " + (view.get_Parameter(BuiltInParameter.VIEW_DESCRIPTION)?.AsString() ?? "")).Select(b => (b, 0)));
            }
        // The key plan is drawn in the title block, one yes/no switch per berth: on the sheet's title block or on its type.
        foreach (var block in index.Blocks[s.Id.Value])
            foreach (var owner in new[] { block, doc.GetElement(block.GetTypeId()) })
            {
                if (owner is null) continue;
                foreach (Parameter p in owner.Parameters)
                {
                    if (p.StorageType != StorageType.Integer || p.Definition is null || p.Definition.GetDataType() != SpecTypeId.Boolean.YesNo) continue;
                    var berths = SubmissionFormat.SwitchBerths(p.Definition.Name);
                    if (berths.Count == 0) { sheet.OtherSwitches.Add(p.Definition.Name); continue; }
                    sheet.KeyPlanFound = sheet.KeyPlanSwitches = true;
                    var part = SubmissionFormat.SwitchPart(p.Definition.Name);
                    if (part > 0) sheet.KeyPlanParts = true;
                    if (p.AsInteger() == 1) sheet.KeyPlanOn.AddRange(berths.Select(b => (b, part)));
                }
            }
    }

    /// <summary>The view's tags: empty ones, ones lying on another tag, and how many elements of the tagged categories have none.</summary>
    /// <summary>
    /// A viewport's box takes in what is drawn around the view (level heads, grid heads, tags, notes). True when the view's
    /// own crop lies inside the sheet, so only something around it runs past the paper. False when that cannot be told
    /// (no crop, or a view broken into several regions).
    /// </summary>
    private static bool CropOnSheet(View view, Viewport port, BoundingBoxUV frame, double slack)
    {
        try
        {
            if (!view.CropBoxActive || view.CropBox is not { } crop) return false;
            var regions = view.GetModelToProjectionTransforms();
            if (regions.Count != 1) return false;
            var toSheet = port.GetProjectionToSheetTransform().Multiply(regions[0].GetModelToProjectionTransform());
            foreach (var x in new[] { crop.Min.X, crop.Max.X })
                foreach (var y in new[] { crop.Min.Y, crop.Max.Y })
                {
                    var at = toSheet.OfPoint(crop.Transform.OfPoint(new XYZ(x, y, crop.Max.Z)));
                    if (at.X < frame.Min.U - slack || at.Y < frame.Min.V - slack || at.X > frame.Max.U + slack || at.Y > frame.Max.V + slack) return false;
                }
            return true;
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { return false; }
    }

    private static void Tags(Document doc, Index index, IEnumerable<IndependentTag> tags, View view, SubmissionFormat.PlacedView placed, bool piles, HashSet<long> shownMatchlines)
    {
        XYZ right = view.RightDirection, up = view.UpDirection;
        var boxes = new List<(double U0, double V0, double U1, double V1)>();
        var tagged = new Dictionary<long, HashSet<long>>();
        Func<Element, bool?> inside = piles ? InsideMatchlines(doc, index, view, shownMatchlines) : _ => true;
        foreach (var tag in tags)
        {
            if (tag.IsHidden(view)) continue;
            try
            {
                var text = (tag.TagText ?? "").Trim();
                if (text.Length == 0 || text == "?") placed.EmptyTags++;
                foreach (var link in tag.GetTaggedElementIds())
                {
                    if (!piles || link.LinkInstanceId != ElementId.InvalidElementId || doc.GetElement(link.HostElementId) is not { Category: { } cat } host) continue;
                    if (!tagged.TryGetValue(cat.Id.Value, out var set)) tagged[cat.Id.Value] = set = [];
                    if (set.Add(host.Id.Value) && inside(host) == false) placed.TaggedOutside++;
                }
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { /* a tag whose host is gone */ }
            if (tag.get_BoundingBox(view) is not { } box) continue;
            // Revit gives the box along the model's axes. In a plan turned on the sheet that is bigger than the tag, and
            // taking its corners along the view's axes would grow it again: the tag's own size is worked back from it.
            var middle = (box.Min + box.Max) / 2;
            var (w, h) = SubmissionFormat.Unturned(box.Max.X - box.Min.X, box.Max.Y - box.Min.Y, Math.Abs(right.X), Math.Abs(right.Y));
            if (Math.Abs(right.Z) > 1e-6 || Math.Abs(up.Z) > 1e-6)
            {
                // A section or elevation: the box's corners along the view's axes.
                var corners = from x in new[] { box.Min.X, box.Max.X } from y in new[] { box.Min.Y, box.Max.Y } from z in new[] { box.Min.Z, box.Max.Z } select new XYZ(x, y, z);
                w = corners.Max(c => c.DotProduct(right)) - corners.Min(c => c.DotProduct(right));
                h = corners.Max(c => c.DotProduct(up)) - corners.Min(c => c.DotProduct(up));
            }
            double u0 = middle.DotProduct(right) - w / 2, u1 = middle.DotProduct(right) + w / 2, v0 = middle.DotProduct(up) - h / 2, v1 = middle.DotProduct(up) + h / 2;
            // A tag's box takes in its leader, which may cross another tag freely: only the text counts.
            try { if (tag.HasLeader) (u0, v0, u1, v1) = SubmissionFormat.WithoutLeader(u0, v0, u1, v1, tag.TagHeadPosition.DotProduct(right), tag.TagHeadPosition.DotProduct(up)); }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { /* a tag with no head position: its whole box */ }
            // Shrunk: Revit's box is a little bigger than the text, so tags that touch or sit close are not on top of each
            // other (in the real model, slivers of 0.05 mm on paper between neighbours were reported at 10%).
            double du = (u1 - u0) * 0.15, dv = (v1 - v0) * 0.15;
            if (u1 > u0 && v1 > v0) boxes.Add((u0 + du, v0 + dv, u1 - du, v1 - dv));
        }
        for (var i = 0; i < boxes.Count; i++)
            for (var j = 0; j < boxes.Count; j++)
                if (i != j && boxes[i].U0 < boxes[j].U1 && boxes[j].U0 < boxes[i].U1 && boxes[i].V0 < boxes[j].V1 && boxes[j].V0 < boxes[i].V1) { placed.OverlappingTags++; break; }
        foreach (var pair in tagged)
        {
            foreach (var e in new FilteredElementCollector(doc, view.Id).OfCategoryId(new ElementId(pair.Key)).WhereElementIsNotElementType())
            {
                // On a matchline (null) an element is neither required to have a tag nor wrong to have one.
                if (inside(e) != true) continue;
                placed.Taggable++;
                if (!pair.Value.Contains(e.Id.Value)) placed.UntaggedInside++;
            }
        }
    }

    /// <summary>
    /// Says whether an element lies inside the view's matchlines: on the same side of every matchline as the middle of the
    /// crop (the part the view is for). False beyond one; null on one (within about 100 mm). All true when the view has
    /// no matchline or no crop. Matchlines hidden in the view (redrawn by trim_matchlines) count too.
    /// </summary>
    private static Func<Element, bool?> InsideMatchlines(Document doc, Index index, View view, HashSet<long> shownMatchlines)
    {
        var lines = new List<(double U, double V, double Du, double Dv, double Side)>();
        try
        {
            if (view.ViewType is ViewType.FloorPlan or ViewType.EngineeringPlan or ViewType.CeilingPlan or ViewType.AreaPlan && view.CropBoxActive && view.CropBox is { } crop)
            {
                XYZ right = view.RightDirection, up = view.UpDirection;
                var middle = crop.Transform.OfPoint((crop.Min + crop.Max) / 2);
                double mu = middle.DotProduct(right), mv = middle.DotProduct(up);
                foreach (var ml in index.Matchlines)
                {
                    if (ml.OwnerViewId != ElementId.InvalidElementId && ml.OwnerViewId != view.Id) continue;
                    // The ones the view shows, and the ones hidden in it by hand (trim_matchlines hides and redraws them).
                    if (!shownMatchlines.Contains(ml.Id.Value) && !ml.IsHidden(view)) continue;
                    // The matchline's longest run is the cut; short jogs are not.
                    if (TrimMatchlinesTool.Curves(doc, ml, view).OrderByDescending(c => c.Length).FirstOrDefault() is not { } run) continue;
                    XYZ a = run.GetEndPoint(0), b = run.GetEndPoint(1);
                    double u = a.DotProduct(right), v = a.DotProduct(up), du = b.DotProduct(right) - u, dv = b.DotProduct(up) - v;
                    var length = Math.Sqrt(du * du + dv * dv);
                    if (length < 1) continue;
                    du /= length; dv /= length;
                    var side = du * (mv - v) - dv * (mu - u);
                    // A matchline through the middle of the crop cuts nothing off for this view.
                    if (Math.Abs(side) > 1) lines.Add((u, v, du, dv, Math.Sign(side)));
                }
            }
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { lines.Clear(); }
        if (lines.Count == 0) return _ => true;
        var r = view.RightDirection;
        var t = view.UpDirection;
        return e =>
        {
            XYZ? at = e.Location is LocationPoint p ? p.Point : e.get_BoundingBox(view) is { } box ? (box.Min + box.Max) / 2 : null;
            if (at is null) return null;
            double pu = at.DotProduct(r), pv = at.DotProduct(t);
            bool? result = true;
            foreach (var l in lines)
            {
                var d = (l.Du * (pv - l.V) - l.Dv * (pu - l.U)) * l.Side;
                if (d < -0.33) return false;
                if (d < 0.33) result = null;
            }
            return result;
        };
    }

    /// <summary>The matchlines the view shows that run past its crop (ones hidden and redrawn by trim_matchlines are not shown).</summary>
    private static int MatchlinesPastCrop(Document doc, Index index, View view, HashSet<long> shownMatchlines)
    {
        var count = 0;
        try
        {
            if (CropClip.For(view) is not { } crop) return 0;
            if (shownMatchlines.Count == 0) return 0;
            foreach (var ml in index.Matchlines.Where(m => shownMatchlines.Contains(m.Id.Value)))
            {
                var curves = TrimMatchlinesTool.Curves(doc, ml, view);
                var total = curves.Sum(c => c.Length);
                var inside = curves.SelectMany(c => crop.Clip(c, 0)).Sum(c => c.Length);
                if (total - inside > 0.05) count++; // more than about 15 mm in the model
            }
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { /* a view whose crop can't be read: not judged */ }
        return count;
    }

    /// <summary>
    /// Says whether an annotation of a primary view lies in this dependent view: inside its crop (by the tag's head, the
    /// note's origin, the dimension's middle). Everything when the view has no crop.
    /// </summary>
    private static Func<Element, bool> InCrop(View view)
    {
        try
        {
            if (!view.CropBoxActive || view.CropBox is not { } crop) return _ => true;
            var back = crop.Transform.Inverse;
            return e =>
            {
                try
                {
                    // Never the element's box in the view: that makes Revit draw the view, which is slow.
                    XYZ? at = e switch
                    {
                        IndependentTag t => t.TagHeadPosition,
                        TextNote n => n.Coord,
                        Dimension { NumberOfSegments: > 1 } d => d.Segments.get_Item(d.NumberOfSegments / 2).Origin,
                        Dimension d => d.Origin,
                        _ => null,
                    };
                    if (at is null) return false;
                    var p = back.OfPoint(at);
                    return p.X >= crop.Min.X && p.X <= crop.Max.X && p.Y >= crop.Min.Y && p.Y <= crop.Max.Y;
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException) { return false; }
            };
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { return _ => true; }
    }

    private static void Notes(IEnumerable<TextNote> notes, SubmissionFormat.Sheet sheet)
    {
        foreach (var note in notes)
            sheet.Cited.AddRange(SubmissionFormat.CitedIn(note.Text ?? ""));
    }

    /// <summary>The dimensions of a view whose typed number differs from what the model measures ('EQ', 'TYP' are labels, not numbers).</summary>
    private static int TypedDimensions(IEnumerable<Dimension> dimensions)
    {
        var count = 0;
        foreach (var d in dimensions)
        {
            IEnumerable<(string? over, string? shown)> Parts() => d.NumberOfSegments > 1
                ? d.Segments.Cast<DimensionSegment>().Select(s => (s.ValueOverride, s.ValueString))
                : [(d.ValueOverride, d.ValueString)];
            foreach (var (over, shown) in Parts())
            {
                if (string.IsNullOrWhiteSpace(over) || shown is null) continue;
                var number = Regex.Match(over!, @"-?\d+(\.\d+)?");
                var actual = Regex.Match(shown.Replace(",", ""), @"-?\d+(\.\d+)?");
                if (!number.Success || !actual.Success) continue;
                var a = double.Parse(number.Value, CultureInfo.InvariantCulture);
                var m = double.Parse(actual.Value, CultureInfo.InvariantCulture);
                if (Math.Abs(a - m) > Math.Max(0.5, Math.Abs(m) * 0.005)) count++;
            }
        }
        return count;
    }
}
