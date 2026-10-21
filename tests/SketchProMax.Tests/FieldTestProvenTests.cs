using System.Text.RegularExpressions;
using SketchProMax.Tools;

/// <summary>The field test's "proven earlier" baseline (Data/field-test-proven.json) and the steps it lets the field test skip.</summary>
internal static class FieldTestProvenTests
{
    public static void Run(string repoRoot, Action<string, bool, string> check)
    {
        // The built-in baseline: both Revit years of the last full run, labels only.
        var data = FieldTestProven.Load();
        var y2024 = data.For("2024");
        var y2025 = data.For("2025");
        check("proven: the built-in baseline parses, with Revit 2024 and 2025", y2024 is not null && y2025 is not null && data.Version.Length > 0,
            $"{data.Baselines.Count} baselines, version '{data.Version}'");
        check("proven: a Revit year with no baseline runs everything", data.For("2026") is null && data.For(null) is null && data.For(" 2024 ") == y2024, "");
        check("proven: each baseline proves rows", y2024?.Labels.Count > 20 && y2025?.Labels.Count > 20, $"{y2024?.Labels.Count} / {y2025?.Labels.Count}");
        var label = new Regex("^[a-z0-9_]+( #[0-9]+)?$");
        var odd = data.Baselines.SelectMany(b => b.Labels).Where(l => !label.IsMatch(l)).ToList();
        check("proven: labels are \"tool\" or \"tool #N\" (no bug tags, paths or names)", odd.Count == 0, string.Join(", ", odd.Take(5)));
        check("proven: the row message names the run", y2024?.Message == $"proven earlier ({y2024?.Date}, Revit 2024, {data.Version}); code unchanged", y2024?.Message ?? "");

        // Every proven label is still a self-test case (a renamed or removed case can't stay proven).
        var selfTest = Path.Combine(repoRoot, "src", "SketchProMax", "Tools", "SelfTest.cs");
        if (File.Exists(selfTest))
        {
            var source = File.ReadAllText(selfTest);
            var cases = Regex.Matches(source, @"\bnew\(""([a-z0-9_]+)"",").Select(m => m.Groups[1].Value).GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count());
            var stale = data.Baselines.SelectMany(b => b.Labels).Where(l =>
            {
                var m = Regex.Match(l, "^([a-z0-9_]+)(?: #([0-9]+))?$");
                var nth = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 1;
                return !cases.TryGetValue(m.Groups[1].Value, out var n) || n < nth;
            }).Distinct().ToList();
            check("proven: every proven row is still a self-test case", stale.Count == 0, string.Join(", ", stale));
        }

        // Tools added after the baseline's version are never proven.
        string[] added = ["purge_unused", "delete_unused_views", "cleanup_imports", "cleanup_model", "cost_estimate", "what_if", "drawing_package",
            "get_drawing_package_plan", "issue_check", "redline_read", "redline_plan", "redline_apply"];
        var leaked = data.Baselines.SelectMany(b => b.Labels).Where(l => added.Contains(l.Split(' ')[0])).ToList();
        check("proven: tools new since the baseline are not in it", leaked.Count == 0, string.Join(", ", leaked));
        check("proven: the field test's own steps are not in it", !data.Baselines.SelectMany(b => b.Labels).Any(l => FieldTestCoverage.FieldSteps.Contains(l.Split(' ')[0])), "");

        // Manual features unchanged since the baseline are manual features.
        check("proven: unchanged manual items are manual features",
            data.ManualUnchanged.All(k => FieldTestCoverage.Features.Any(f => f.Key == k && f.How == FieldTestCoverage.How.Manual)), string.Join(", ", data.ManualUnchanged));

        // Parse: the script's format.
        var sample = FieldTestProven.Parse("""
            { "commit": "abc1234", "version": "0.31.0", "manual_unchanged": ["help"],
              "baselines": [ { "revit": "2024", "date": "2026-10-17", "version": "0.31.0", "proven": ["get_model_context", "save_recipe", "list_types #2", "create_levels"] } ] }
            """);
        var b = sample.For("2024")!;
        check("proven: parse", sample.Commit == "abc1234" && b.Labels.Count == 4 && sample.ManualUnchanged.Contains("help") && b.Describe.Contains("Revit 2024"), b.Describe);
        var broken = false;
        try { FieldTestProven.Parse("{ \"version\": \"x\" }"); }
        catch (Exception ex) when (ex is KeyNotFoundException or System.Text.Json.JsonException) { broken = true; }
        check("proven: a file without baselines is refused", broken, "");

        // Labels: a tool's second case is "tool #2", as the report numbers them.
        FieldTestProven.Step S(string tool, int bug = 0) => new(tool, bug);
        check("proven: labels number a tool's cases", FieldTestProven.Labels([S("list_types"), S("query_elements"), S("list_types")]).SequenceEqual(["list_types", "query_elements", "list_types #2"]), "");

        // The plan: proven rows are skipped, the rest run.
        var steps = new List<FieldTestProven.Step> { S("get_model_context"), S("list_types"), S("list_types"), S("create_levels", bug: 7), S("brand_new_tool") };
        var plan = FieldTestProven.PlanFor(steps, b);
        check("proven: a proven row is skipped, its second case only when proven too", plan.Proven.SetEquals([0, 2]), string.Join(",", plan.Proven));
        check("proven: a bug check always runs", !plan.Proven.Contains(3), "");
        check("proven: a new tool always runs", !plan.Proven.Contains(4), "");
        check("proven: no baseline, nothing skipped", FieldTestProven.PlanFor(steps, null).Proven.Count == 0, "");

        // A step that a later step needs still runs: save_recipe is proven, but run_recipe runs and needs the saved recipe.
        var chain = new List<FieldTestProven.Step> { S("save_recipe"), S("get_model_context"), S("run_recipe") };
        var guarded = FieldTestProven.PlanFor(chain, b);
        check("proven: a step a running step depends on is not skipped", !guarded.Proven.Contains(0) && guarded.Forced.SequenceEqual(["save_recipe"]) && guarded.Proven.Contains(1),
            $"proven {string.Join(",", guarded.Proven)}, forced {string.Join(",", guarded.Forced)}");
        var both = FieldTestProven.PlanFor(chain, b with { Labels = new HashSet<string>(b.Labels) { "run_recipe" } });
        check("proven: a bundle proven whole is skipped whole", both.Proven.SetEquals([0, 1, 2]) && both.Forced.Count == 0, "");
        check("proven: every bundle names real cases", FieldTestProven.Bundles.All(g => g.Length >= 2), "");
        // In the built-in baseline: the learned tool is saved and deleted around steps that run, so both still run.
        if (y2024 is not null && y2024.Labels.Contains("save_as_tool"))
        {
            var learned = FieldTestProven.PlanFor([S("save_as_tool"), S("self_test_count_walls"), S("run_learned_tool"), S("delete_learned_tool")], y2024);
            check("proven: save_as_tool runs while the learned tool's calls run", !learned.Proven.Contains(0) && learned.Forced.Contains("save_as_tool"), string.Join(",", learned.Forced));
        }

        // The estimate shrinks with the steps that run.
        check("proven: the estimate follows the steps that run", FieldTestProven.Estimate(400) != FieldTestProven.Estimate(250) && FieldTestProven.Estimate(0).StartsWith("about 2 to 3", StringComparison.Ordinal),
            $"{FieldTestProven.Estimate(400)} / {FieldTestProven.Estimate(250)}");

        // Report rows marked proven count as passed for the bug log.
        check("proven: a proven row passes its bug log check", FieldTestCoverage.RefStatus("tool:ve_register", [("ve_register", "proven")]) == "pass", "");

        // The checklist: unchanged manual items carry the note and come last; the result file says "unchanged" for them.
        var items = FieldTestCoverage.Checklist([new(3, "Raft on GF", "Fixed in 0.30")], ["help"], "unchanged since 0.31.0: not asked again");
        var help = items.FindIndex(i => i.Id == "help");
        check("checklist: an unchanged manual item comes last, with its note", help == items.Count - 1 && items[help].Unchanged.Contains("0.31.0")
            && items.Count(i => i.Unchanged.Length > 0) == 1 && items.Any(i => i.Id == "bug:3"), $"help at {help} of {items.Count}");
        check("checklist: the same items as without the baseline", items.Count == FieldTestCoverage.Checklist([new(3, "Raft on GF", "Fixed in 0.30")]).Count, "");
        var md = FieldTestCoverage.ResultMarkdown(items, new Dictionary<string, FieldTestCoverage.Mark> { ["chat.ui"] = new("chat.ui", "pass", "") },
            "0.35.0", "2025", "", new DateTime(2026, 10, 21, 9, 0, 0));
        check("checklist result: unchanged items aren't counted as not checked", md.Contains($"1 pass, 0 fail, {items.Count - 2} not checked, 1 unchanged (not asked again)")
            && md.Contains("| unchanged | unchanged since 0.31.0"), md.Split('\n').FirstOrDefault(l => l.StartsWith("- Result", StringComparison.Ordinal)) ?? "");
    }
}
