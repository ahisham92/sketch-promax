using System.IO;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SketchProMax.Bridge;

namespace SketchProMax.Tools;

/// <summary>
/// The drawing package probes (keys package.sentence and package.skip in FieldTestCoverage): a small package made from
/// a sentence on the test project, and Skip step during one stage. Both roll the test project back afterwards.
/// </summary>
internal static partial class FieldTest
{
    private const string PackageSentenceText = "GA plans, sections and column schedule for levels 1-2 on A1 with our title block";

    /// <summary>What was in the model before: every view and sheet by id, with its name (sheets with their number).</summary>
    private static Dictionary<long, string> Existing(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
            .ToDictionary(v => v.Id.Value, v => v is ViewSheet s ? s.SheetNumber + " " + s.Name : v.Name);

    private static string? StageStatus(JsonElement result, string stage) =>
        result.GetProperty("stages").EnumerateArray().FirstOrDefault(s => s.GetProperty("stage").GetString()!.EndsWith(stage, StringComparison.Ordinal)) is { ValueKind: JsonValueKind.Object } row
            ? row.GetProperty("status").GetString() : null;

    private static string? StageCheck(JsonElement result, string stage) =>
        result.GetProperty("stages").EnumerateArray().FirstOrDefault(s => s.GetProperty("stage").GetString()!.EndsWith(stage, StringComparison.Ordinal)) is { ValueKind: JsonValueKind.Object } row
        && row.TryGetProperty("check", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;

    /// <summary>
    /// The sentence is planned (counts), then run: every planned view is made, each stage's check passes, the register
    /// sheet and its Excel copy are there, the time-saved line is given, and nothing that was in the model before changed.
    /// A step only the user can do (no title block or tag family in the test project) is answered Skip and must be named.
    /// </summary>
    private static (string, string) PackageSentence(UIApplication app, State s)
    {
        if (TestDoc(app, s, out var why) is not { } doc) return Fail(why);
        var before = Existing(doc);
        var asked = new List<string>();
        using var group = new TransactionGroup(doc, "Field test: drawing package");
        group.Start();
        try
        {
            Handoff.TestResponder = (step, _) => { asked.Add(step.What); return HandoffChoice.Skip; };
            var planned = Call(app, s.Registry, "get_drawing_package_plan", new { request = PackageSentenceText });
            if (planned.IsError) return Fail("the sentence was not planned: " + Short(planned.Text, 400));
            int views, sheets;
            using (var p = JsonDocument.Parse(planned.Text))
            {
                var counts = p.RootElement.GetProperty("counts");
                views = counts.GetProperty("views").GetInt32() + counts.GetProperty("schedules").GetInt32();
                sheets = counts.GetProperty("sheets").GetInt32();
            }
            var run = Call(app, s.Registry, "drawing_package", new { request = PackageSentenceText, approved = true, folder = Path.Combine(s.Folder, "package") });
            if (run.IsError) return Fail("drawing_package failed: " + Short(run.Text, 400));
            using var json = JsonDocument.Parse(run.Text);
            var r = json.RootElement;
            var problems = new List<string>();
            var made = r.GetProperty("counts").GetProperty("views").GetInt32();
            if (made != views) problems.Add($"{made} of the {views} planned views were made");
            if (StageCheck(r, "Views") is { } vc && !vc.StartsWith("pass", StringComparison.Ordinal)) problems.Add("views check: " + vc);
            foreach (var stage in new[] { "Dimensions", "Tags", "Sheets", "Issue sheet" })
                if (StageCheck(r, stage) is { } c && !c.StartsWith("pass", StringComparison.Ordinal) && !(stage == "Sheets" && c.Contains("handed to you")))
                    problems.Add($"{stage} check: {c}");
            var titleBlockAsked = asked.Any(a => a.IndexOf("title block", StringComparison.OrdinalIgnoreCase) >= 0);
            var sheetsMade = r.GetProperty("counts").GetProperty("sheets").GetInt32();
            if (!titleBlockAsked && sheetsMade != sheets) problems.Add($"{sheetsMade} of the {sheets} planned sheets were made");
            if (titleBlockAsked && StageStatus(r, "Sheets")?.StartsWith("waiting for you", StringComparison.Ordinal) != true) problems.Add("a title block was asked for but the Sheets stage doesn't say it waits for it");
            if (!titleBlockAsked)
            {
                if (r.GetProperty("issue").ValueKind != JsonValueKind.Object) problems.Add("no register sheet");
                else if (r.GetProperty("issue").GetProperty("excel").GetString() is not { } xlsx || !File.Exists(xlsx)) problems.Add("the Excel register is not there");
            }
            var line = r.GetProperty("timeSaved").GetProperty("line").GetString() ?? "";
            if (!line.StartsWith("Done in", StringComparison.Ordinal)) problems.Add($"no time-saved line ({line})");
            var after = Existing(doc);
            var lost = before.Where(b => !after.TryGetValue(b.Key, out var n) || n != b.Value).Select(b => b.Value).ToList();
            if (lost.Count > 0) problems.Add($"views or sheets that were there before changed or went: {string.Join(", ", lost.Take(5))}");
            if (doc.IsModifiable) problems.Add("a transaction was left open");
            var handed = asked.Count > 0 ? $"; handed over and skipped: {string.Join("; ", asked.Distinct().Take(4))}" : "";
            return problems.Count == 0
                ? Pass($"\"{PackageSentenceText}\": planned {views} views and {sheets} sheets; made {made} views and {sheetsMade} sheets, every stage check passed, nothing that was there before changed. {line}{handed}")
                : Fail(string.Join("; ", problems) + handed);
        }
        finally
        {
            Handoff.TestResponder = null;
            RollBack(group);
        }
    }

    /// <summary>Skip step at the second view: the Views stage ends with one view kept, the skip is used up, and the Sheets stage still runs.</summary>
    private static (string, string) PackageSkip(UIApplication app, State s)
    {
        if (TestDoc(app, s, out var why) is not { } doc) return Fail(why);
        var keepControl = JobControl.Current;
        var keepTool = JobControl.Tool;
        var keepLong = JobFeed.LongStep;
        using var group = new TransactionGroup(doc, "Field test: drawing package skip");
        group.Start();
        try
        {
            var control = new JobControl();
            JobControl.Current = control;
            JobControl.Tool = control; // as the bridge sets it for a chat call
            JobFeed.LongStep = null;
            JobFeed.Reset();
            var views = DrawingPackageTool.BatchName(1, "Views");
            var sheetsSeen = false;
            JobControl.TestBoundary = (what, i) =>
            {
                if (what == views && i == 1) control.Apply(JobAction.Skip);
                if (what == DrawingPackageTool.BatchName(4, "Sheets")) sheetsSeen = true;
            };
            Handoff.TestResponder = (_, _) => HandoffChoice.Skip;
            var result = Call(app, s.Registry, "drawing_package", new
            {
                levels = new[] { "all" }, views = new[] { "floor_plan", "section" }, sections = 2, tags = Array.Empty<string>(), dimensions = false,
                issue_sheet = "none", approved = true, folder = Path.Combine(s.Folder, "package-skip"),
            });
            var text = JobControl.AfterStep(control, "drawing_package", result.Text, result.IsError, out var state, notes: false);
            if (result.IsError) return Fail("drawing_package failed: " + Short(result.Text, 400));
            using var json = JsonDocument.Parse(result.Text);
            var r = json.RootElement;
            var problems = new List<string>();
            var made = r.GetProperty("counts").GetProperty("views").GetInt32();
            if (made != 1) problems.Add($"{made} views were kept, not 1");
            if (StageStatus(r, "Views")?.StartsWith("skipped by the user", StringComparison.Ordinal) != true) problems.Add($"the Views stage says \"{StageStatus(r, "Views")}\"");
            var sheets = StageStatus(r, "Sheets") ?? "";
            if (!sheetsSeen && !sheets.StartsWith("waiting for you", StringComparison.Ordinal)) problems.Add($"the Sheets stage did not run after the skip (\"{sheets}\")");
            if (control.State != JobState.Running || state != "done") problems.Add($"after the package the task is {control.State} and the step {state} (the skip should be used up by the stage)");
            return problems.Count == 0
                ? Pass($"Skip at the second view: the Views stage kept 1 view and said skipped, the skip was used up there and the Sheets stage ran ({Short(sheets, 60)}); the step ended {state}")
                : Fail(string.Join("; ", problems) + $" ({Short(text, 200)})");
        }
        finally
        {
            JobControl.TestBoundary = null;
            Handoff.TestResponder = null;
            JobControl.Current = keepControl;
            JobControl.Tool = keepTool;
            JobFeed.LongStep = keepLong;
            RollBack(group);
        }
    }
}
