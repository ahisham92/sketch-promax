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
        "title, wrong scale, missing schedule, empty sheet, different issue date, drawing cited that is not a sheet, and " +
        "typed-over dimension, with the fix: call it BEFORE saying a submission is done; the work is done when it lists " +
        "nothing. Changes nothing.";
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
                    note = issues.Count == 0
                        ? "Every line of the format that can be read from the model holds. Still look at each sheet (capture_view_image): overlapping tags, matchlines past the crop and the key plan show only there."
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
            foreach (var id in s.GetAllPlacedViews())
            {
                if (doc.GetElement(id) is not View view || view.ViewType == ViewType.Legend) continue;
                var title = view.get_Parameter(BuiltInParameter.VIEW_DESCRIPTION)?.AsString();
                sheet.Views.Add((string.IsNullOrWhiteSpace(title) ? view.Name : title!, view.Scale));
                Notes(doc, id, sheet);
                sheet.TypedDimensions += TypedDimensions(doc, id);
            }
            sheet.Cited = sheet.Cited.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        return result;
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
