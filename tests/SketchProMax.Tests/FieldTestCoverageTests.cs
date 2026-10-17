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
            check($"checklist: the bug log's fixed rows are read ({fixedRows.Count})", fixedRows.Count >= 20, "");
        }
    }
}
