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
        "schedule, empty sheet, empty or overlapping tag, pile inside the matchlines with no tag, tag on a pile beyond the matchline, matchline past the crop, key plan highlighting another berth, " +
        "different issue date, drawing cited that is not a sheet, and typed-over dimension, with the fix: call it BEFORE " +
        "saying a submission is done; the work is done when it lists nothing. Scales are usual ones, not fixed: a view too " +
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
                var matrix = SubmissionFormat.Matrix(issues);
                var checkedBerths = (berths.Count > 0 ? berths : sheets.Select(s => SubmissionFormat.Parse(s.Number)?.Berth ?? 0).Where(b => b > 0).Distinct().ToList()).OrderBy(b => b).ToList();
                return ToolResult.Ok(new
                {
                    berths = checkedBerths,
                    sheetsChecked = sheets.Count(s => SubmissionFormat.Parse(s.Number) is { } at && (at.Berth == 0 || checkedBerths.Contains(at.Berth))),
                    open = issues.Count,
                    issues = issues.Select(i => new { sheet = i.Sheet, check = i.Check, problem = i.Problem, fix = i.Fix }).ToList(),
                    sheetsWithOpenItems = matrix.Select(m => new { sheet = m.Key, checks = m.Value }).ToList(),
                    look = SubmissionFormat.Look(sheets, checkedBerths),
                    note = issues.Count == 0
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
    internal static List<SubmissionFormat.Sheet> Read(Document doc, IReadOnlyCollection<int> berths)
    {
        var all = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(s => !s.IsPlaceholder).ToList();
        var schedules = new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>()
            .Where(i => !i.IsTitleblockRevisionSchedule).ToLookup(i => i.OwnerViewId.Value);
        var result = new List<SubmissionFormat.Sheet>();
        foreach (var s in all)
        {
            var sheet = new SubmissionFormat.Sheet { Number = s.SheetNumber ?? "", Name = s.Name ?? "" };
            result.Add(sheet);
            if (SubmissionFormat.Parse(sheet.Number) is not { } at || (at.Berth > 0 && berths.Count > 0 && !berths.Contains(at.Berth))) continue;
            sheet.IssueDate = s.get_Parameter(BuiltInParameter.SHEET_ISSUE_DATE)?.AsString() ?? "";
            sheet.Schedules = schedules[s.Id.Value].Select(i => doc.GetElement(i.ScheduleId)?.Name ?? "").Where(n => n.Length > 0).ToList();
            Notes(doc, s.Id, sheet);
            KeyPlan(doc, s, sheet);
            var frame = s.Outline;
            foreach (var port in new FilteredElementCollector(doc, s.Id).OfClass(typeof(Viewport)).Cast<Viewport>())
            {
                if (doc.GetElement(port.ViewId) is not View view || view.ViewType == ViewType.Legend) continue;
                var title = view.get_Parameter(BuiltInParameter.VIEW_DESCRIPTION)?.AsString();
                var placed = new SubmissionFormat.PlacedView { Title = string.IsNullOrWhiteSpace(title) ? view.Name : title!, Scale = view.Scale };
                sheet.Views.Add(placed);
                // A key plan placed as a view is not a drawing of the sheet: nothing more is read in it.
                if (IsKeyPlan(view)) continue;
                try
                {
                    var box = port.GetBoxOutline();
                    const double slack = 0.02; // about 6 mm on paper
                    placed.OffSheet = frame is not null && (box.MinimumPoint.X < frame.Min.U - slack || box.MinimumPoint.Y < frame.Min.V - slack
                                                         || box.MaximumPoint.X > frame.Max.U + slack || box.MaximumPoint.Y > frame.Max.V + slack);
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException) { /* a viewport with no box: not judged */ }
                Tags(doc, view, placed);
                placed.MatchlinesPastCrop = MatchlinesPastCrop(doc, view);
                Notes(doc, view.Id, sheet);
                sheet.TypedDimensions += TypedDimensions(doc, view.Id);
            }
            sheet.Cited = sheet.Cited.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        return result;
    }

    private static bool IsKeyPlan(View view) =>
        Regex.IsMatch(view.Name + " " + (view.get_Parameter(BuiltInParameter.VIEW_DESCRIPTION)?.AsString() ?? ""), @"KEY\s*PLAN", RegexOptions.IgnoreCase);

    /// <summary>
    /// The key plan, as far as the model describes it: a view on the sheet named KEY PLAN (its name says the berth), or
    /// yes/no parameters of the title block named for a berth or the key plan (the ones switched on).
    /// </summary>
    private static void KeyPlan(Document doc, ViewSheet s, SubmissionFormat.Sheet sheet)
    {
        static IEnumerable<int> Berths(string text) =>
            Regex.Matches(text ?? "", @"BERTH\s*0?(\d{1,2})", RegexOptions.IgnoreCase).Cast<Match>().Select(m => int.Parse(m.Groups[1].Value));
        foreach (var id in s.GetAllPlacedViews())
            if (doc.GetElement(id) is View view && IsKeyPlan(view))
            {
                sheet.KeyPlanFound = true;
                sheet.KeyPlanBerths.AddRange(Berths(view.Name + " " + (view.get_Parameter(BuiltInParameter.VIEW_DESCRIPTION)?.AsString() ?? "")));
            }
        // The key plan is drawn in the title block, one yes/no switch per berth: on the sheet's title block or on its type.
        foreach (var block in new FilteredElementCollector(doc, s.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType())
            foreach (var owner in new[] { block, doc.GetElement(block.GetTypeId()) })
            {
                if (owner is null) continue;
                foreach (Parameter p in owner.Parameters)
                {
                    if (p.StorageType != StorageType.Integer || p.Definition is null || p.Definition.GetDataType() != SpecTypeId.Boolean.YesNo) continue;
                    var berths = SubmissionFormat.SwitchBerths(p.Definition.Name);
                    if (berths.Count == 0) { sheet.OtherSwitches.Add(p.Definition.Name); continue; }
                    sheet.KeyPlanFound = sheet.KeyPlanSwitches = true;
                    if (p.AsInteger() == 1) sheet.KeyPlanBerths.AddRange(berths);
                }
            }
    }

    /// <summary>The view's tags: empty ones, ones lying on another tag, and how many elements of the tagged categories have none.</summary>
    private static void Tags(Document doc, View view, SubmissionFormat.PlacedView placed)
    {
        XYZ right = view.RightDirection, up = view.UpDirection;
        var boxes = new List<(double U0, double V0, double U1, double V1)>();
        var tagged = new Dictionary<long, HashSet<long>>();
        var inside = InsideMatchlines(doc, view);
        foreach (var tag in new FilteredElementCollector(doc, view.Id).OfClass(typeof(IndependentTag)).Cast<IndependentTag>())
        {
            try
            {
                var text = (tag.TagText ?? "").Trim();
                if (text.Length == 0 || text == "?") placed.EmptyTags++;
                foreach (var link in tag.GetTaggedElementIds())
                {
                    if (link.LinkInstanceId != ElementId.InvalidElementId || doc.GetElement(link.HostElementId) is not { Category: { } cat } host) continue;
                    if (!tagged.TryGetValue(cat.Id.Value, out var set)) tagged[cat.Id.Value] = set = [];
                    if (set.Add(host.Id.Value) && inside(host) == false) placed.TaggedOutside++;
                }
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { /* a tag whose host is gone */ }
            if (tag.get_BoundingBox(view) is not { } box) continue;
            var corners = new[] { box.Min, box.Max, new XYZ(box.Min.X, box.Max.Y, box.Min.Z), new XYZ(box.Max.X, box.Min.Y, box.Max.Z) };
            double u0 = corners.Min(c => c.DotProduct(right)), u1 = corners.Max(c => c.DotProduct(right)), v0 = corners.Min(c => c.DotProduct(up)), v1 = corners.Max(c => c.DotProduct(up));
            // Shrunk a little: tags that only touch are not on top of each other.
            double du = (u1 - u0) * 0.1, dv = (v1 - v0) * 0.1;
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
    private static Func<Element, bool?> InsideMatchlines(Document doc, View view)
    {
        var lines = new List<(double U, double V, double Du, double Dv, double Side)>();
        try
        {
            if (view.ViewType is ViewType.FloorPlan or ViewType.EngineeringPlan or ViewType.CeilingPlan or ViewType.AreaPlan && view.CropBoxActive && view.CropBox is { } crop)
            {
                XYZ right = view.RightDirection, up = view.UpDirection;
                var middle = crop.Transform.OfPoint((crop.Min + crop.Max) / 2);
                double mu = middle.DotProduct(right), mv = middle.DotProduct(up);
                foreach (var ml in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Matchline).WhereElementIsNotElementType())
                {
                    if (ml.OwnerViewId != ElementId.InvalidElementId && ml.OwnerViewId != view.Id) continue;
                    var shown = !ml.IsHidden(view);
                    if (shown && !new FilteredElementCollector(doc, view.Id).OfCategory(BuiltInCategory.OST_Matchline).WhereElementIsNotElementType().Any(e => e.Id == ml.Id)) continue;
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
    private static int MatchlinesPastCrop(Document doc, View view)
    {
        if (view.ViewType is not (ViewType.FloorPlan or ViewType.EngineeringPlan or ViewType.CeilingPlan or ViewType.AreaPlan)) return 0;
        var count = 0;
        try
        {
            if (CropClip.For(view) is not { } crop) return 0;
            foreach (var ml in new FilteredElementCollector(doc, view.Id).OfCategory(BuiltInCategory.OST_Matchline).WhereElementIsNotElementType())
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

    private static void Notes(Document doc, ElementId view, SubmissionFormat.Sheet sheet)
    {
        foreach (var note in new FilteredElementCollector(doc, view).OfClass(typeof(TextNote)).Cast<TextNote>())
            sheet.Cited.AddRange(SubmissionFormat.CitedIn(note.Text ?? ""));
    }

    /// <summary>The dimensions of a view whose typed number differs from what the model measures ('EQ', 'TYP' are labels, not numbers).</summary>
    private static int TypedDimensions(Document doc, ElementId view)
    {
        var count = 0;
        foreach (var d in new FilteredElementCollector(doc, view).OfClass(typeof(Dimension)).Cast<Dimension>())
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
