using SketchProMax.Tools;

/// <summary>The coverage table's own logic (the whole-catalogue check is in CatalogTests) and the guided checklist.</summary>
internal static class FieldTestCoverageTests
{
    public static void Run(string repoRoot, Action<string, bool, string> check)
    {
        // Gaps sees what is missing.
        var gaps = FieldTestCoverage.Gaps(["new_tool", "run_field_test"], ["other"], ["button\tx\tNewThing\tNew\tnew-command"], "");
        check("coverage: a tool with no case is a gap", gaps.Any(g => g.StartsWith("tool new_tool")), string.Join("; ", gaps));
        check("coverage: a field-test step needs no case", !gaps.Any(g => g.StartsWith("tool run_field_test")), "");
        check("coverage: an unmapped ribbon command is a gap", gaps.Any(g => g.StartsWith("ribbon new-command")), string.Join("; ", gaps));
        check("coverage: a probe missing from the source is a gap", gaps.Any(g => g.Contains("no probe")), "");
        check("coverage: ribbon keys", FieldTestCoverage.RibbonKey("item\tx\tFav3\tY\tfav:3") == "fav:*"
            && FieldTestCoverage.RibbonKey("item\tx\tA\tY\ttool:place_rooms") is null
            && FieldTestCoverage.RibbonKey("tab\tx\tnotes") == "tab:notes", "");

        check("coverage: a tool skipped for want of a hand-made element points at a manual checklist step",
            FieldTestCoverage.ManualWhenSkipped.All(p => FieldTestCoverage.Features.Any(f => f.Key == p.Value && f.How == FieldTestCoverage.How.Manual && f.Steps.Length > 0))
            && FieldTestCoverage.ManualWhenSkipped.ContainsKey("add_legend_components"), "");

        // The checklist: manual items first, then one per fixed bug.
        const string log = """
            | # | Date | What | Version | Status |
            |---|---|---|---|---|
            | 1 | 2026-09-01 | Chat froze | 0.20 | Fixed in 0.21 (please check) |
            | 2 | 2026-09-02 | Slow open | 0.20 | Open |
            | 3 | 2026-09-03 | Wrong | label | 0.20 | Fixed in 0.22 |
            """;
        var bugs = FieldTestCoverage.FixedBugs(log);
        check("checklist: only fixed rows", bugs.Select(b => b.Number).SequenceEqual([1]), string.Join(",", bugs.Select(b => b.Number)));
        var items = FieldTestCoverage.Checklist(bugs);
        check("checklist: manual features and the fixed bug", items.Any(i => i.Id == "chat.ui") && items[^1].Id == "bug:1" && items[^1].Title.Contains("Chat froze"), "");
        // 0.32: hand-off, time saved and the prompt enhancer each have a manual step, after Chat and the ribbon.
        check("checklist: 0.32 manual steps", new[] { "handoff.ui", "time.ui", "brief.ui" }.All(k => items.Any(i => i.Id == k && i.Steps.Length > 0)), "");
        var areas = items.Select(i => i.Area).Distinct().ToList();
        check("checklist: hand-off and time areas come after the ribbon", areas.IndexOf(FieldTestCoverage.Tasks) > areas.IndexOf(FieldTestCoverage.RibbonArea)
            && areas.IndexOf(FieldTestCoverage.Time) > areas.IndexOf(FieldTestCoverage.Tasks), string.Join(", ", areas));
        check("coverage: the time-saved button maps to a feature", FieldTestCoverage.Ribbon.TryGetValue("time-saved", out var timeKey)
            && FieldTestCoverage.Features.Any(f => f.Key == timeKey), "");
        var md = FieldTestCoverage.ResultMarkdown(items, new Dictionary<string, FieldTestCoverage.Mark>
        {
            ["bug:1"] = new("bug:1", "fail", "still | freezes\nafter send"),
            ["chat.ui"] = new("chat.ui", "pass", ""),
        }, "0.32.0", "2025", "", new DateTime(2026, 10, 17, 9, 5, 0));
        check("checklist result: counts", md.Contains($"1 pass, 1 fail, {items.Count - 2} not checked"), md);
        check("checklist result: a note stays on one table row", md.Contains("still / freezes after send") && md.Contains("**fail**"), "");
        check("checklist result: no model line when none", !md.Contains("- Model:"), "");

        var file = Path.Combine(repoRoot, "docs", "bug-log.md");
        if (File.Exists(file))
        {
            var fixedRows = FieldTestCoverage.FixedBugs(File.ReadAllText(file));
            check($"checklist: the bug log's fixed rows are read ({fixedRows.Count})", fixedRows.Count >= 33, "");
            check("bug log: every fixed row has a BugChecks row", fixedRows.All(b => FieldTestCoverage.BugChecks.Any(c => c.Row == b.Number)),
                string.Join(", ", fixedRows.Where(b => !FieldTestCoverage.BugChecks.Any(c => c.Row == b.Number)).Select(b => b.Number)));
            var text = File.ReadAllText(file);
            check("bug log: no rows numbered twice", text.Split('\n').Select(l => System.Text.RegularExpressions.Regex.Match(l, @"^\|\s*(\d+)\s*\|")).Where(m => m.Success)
                .GroupBy(m => m.Groups[1].Value).All(g => g.Count() == 1), "");
        }

        // ---- The report's Bug log section
        check("bug log: the case tag", FieldTestCoverage.BugTag(6) == "bug#6", "");
        (string, string)[] rows =
        [
            ("create_raft bug#3", "pass"), ("place_views_on_sheet bug#4", "fail"), ("arch_to_structure #2 bug#2", "pass"), ("arch_to_structure #3 bug#20", "skip"),
            ("ribbon.registered", "pass"), ("startup.timings", "pass"), ("update.loader_fixed", "pass"), ("ve_opportunities", "pass"), ("ve_apply #2", "pass"),
        ];
        check("bug log: a tagged case is found by its tag only", FieldTestCoverage.RefStatus("bug#2", rows) == "pass" && FieldTestCoverage.RefStatus("bug#20", rows) == "not run"
            && FieldTestCoverage.RefStatus("bug#4", rows) == "fail" && FieldTestCoverage.RefStatus("bug#5", rows) == "not run", "");
        check("bug log: tools and features", FieldTestCoverage.RefStatus("tool:ve_apply", rows) == "pass" && FieldTestCoverage.RefStatus("chat.update_reopen", rows) == "manual"
            && FieldTestCoverage.RefStatus("ribbon.registered", rows) == "pass" && FieldTestCoverage.RefStatus("no.such", rows) == "not run", "");
        var section = FieldTestCoverage.BugLogMarkdown(
        [
            new(1, "Chat button missing", "Fixed in 0.30"), new(3, "Raft on GF", "Fixed in 0.30"), new(4, "Views over the title", "Fixed in 0.30"),
            new(17, "Always Load", "Fixed in 0.30"), new(18, "Slow open", "Fixed in 0.31"), new(20, "Nothing made", "Fixed in 0.31"), new(99, "New | thing", "Fixed in 0.40"),
        ], rows);
        string Line(int n) => section.Split('\n').First(l => l.StartsWith($"| {n} |", StringComparison.Ordinal));
        check("bug log: passed rows can be confirmed", Line(3).Contains("| pass | mark Confirmed |") && Line(18).Contains("| pass | mark Confirmed |"), section);
        check("bug log: a failing case keeps the row open", Line(4).Contains("**fail**") && Line(4).Contains("keep open"), Line(4));
        check("bug log: passed with a checklist step waits for it", Line(1).Contains("once the checklist step passes") && Line(17).Contains("once the checklist step passes"), Line(1));
        check("bug log: a case not run says so", Line(20).Contains("| not run |"), Line(20));
        check("bug log: a row with no check is listed as not checked, on one table row", Line(99).Contains("**not checked**") && Line(99).Contains("New / thing"), Line(99));

        // Gaps: a fixed row without a check, a tag no case has, a feature that isn't one.
        var bugGaps = FieldTestCoverage.BugGaps([new(99, "x", "Fixed in 1"), new(3, "y", "Fixed in 1")], [3], ["ve_apply"]);
        check("bug log gaps: a row without a check", bugGaps.Any(g => g.StartsWith("bug #99")), string.Join("; ", bugGaps));
        check("bug log gaps: a tag no case carries", bugGaps.Any(g => g.EndsWith("Bug = 2", StringComparison.Ordinal)) && !bugGaps.Any(g => g.EndsWith("Bug = 3", StringComparison.Ordinal)), string.Join("; ", bugGaps));
        check("bug log gaps: a tool with no case", bugGaps.Any(g => g.Contains("tool:ve_register")) && !bugGaps.Any(g => g.Contains("tool:ve_apply")), "");
        check("bug log gaps: every feature reference is a feature", !bugGaps.Any(g => g.Contains("is not a feature")), string.Join("; ", bugGaps.Where(g => g.Contains("is not a feature"))));

        // The checklist: the cleanup and bug-fix steps a person does, with the field test's check named on a bug row.
        var withBugs = FieldTestCoverage.Checklist([new(3, "Raft on GF", "Fixed in 0.30")]);
        check("checklist: cleanup and bug manual steps", new[] { "cleanup.ui", "bug.titleblock_params", "bug.newer_link", "bug.always_load" }.All(k => withBugs.Any(i => i.Id == k && i.Steps.Length > 0)), "");
        check("checklist: a bug row names its field-test check", withBugs[^1].Id == "bug:3" && withBugs[^1].Steps.Contains("field test checks it too"), withBugs[^1].Steps);
    }
}
