using SketchProMax.Tools;

/// <summary>The pure parts of purge and cleanup: name patterns, chunks, the summary line, the file hint and warning hints.</summary>
internal static class CleanupTests
{
    public static void Run(Action<string, bool, string> check)
    {
        // Patterns: plain text is "contains", * and ? match the whole name; case-insensitive; empty matches nothing.
        check("cleanup: a plain pattern is contained", CleanupMath.Matches("Level 1 - WORKING", "working") && !CleanupMath.Matches("Level 1", "working"), "");
        check("cleanup: wildcards match the whole name", CleanupMath.Matches("Self-test type 3", "Self-test type*") && !CleanupMath.Matches("Old Self-test type 3", "Self-test type*")
            && CleanupMath.Matches("A1", "A?") && !CleanupMath.Matches("A12", "A?"), "");
        check("cleanup: regex characters are literal", CleanupMath.Matches("Wall (old)", "Wall (old)") && CleanupMath.Matches("x.y", "x.y") && !CleanupMath.Matches("xzy", "x.y*"), "");
        check("cleanup: an empty pattern matches nothing", !CleanupMath.Matches("anything", "") && !CleanupMath.Matches("anything", "  ") && CleanupMath.FirstMatch("x", null) is null, "");
        check("cleanup: the first matching pattern", CleanupMath.FirstMatch("Plan _keep", ["WORKING", "_keep"]) == "_keep", "");

        var chunks = CleanupMath.Chunks(Enumerable.Range(1, 120), 50);
        check("cleanup: chunks of 50", chunks.Select(c => c.Count).SequenceEqual([50, 50, 20]) && chunks.SelectMany(c => c).SequenceEqual(Enumerable.Range(1, 120)), "");
        check("cleanup: no chunks for nothing, size at least 1", CleanupMath.Chunks(Array.Empty<int>(), 50).Count == 0 && CleanupMath.Chunks([1, 2], 0).Count == 2, "");

        var groups = new Dictionary<string, int> { ["families and types"] = 148, ["materials"] = 40, ["patterns"] = 3, ["groups"] = 2, ["filters"] = 1, ["empty"] = 0 };
        var text = CleanupMath.Groups(groups);
        check("cleanup: groups, biggest first, the rest counted", text == "148 families and types, 40 materials, 3 patterns, 2 groups and 1 more kind", text);
        check("cleanup: one group, no groups", CleanupMath.Groups(new Dictionary<string, int> { ["views"] = 2 }) == "2 views" && CleanupMath.Groups(new Dictionary<string, int>()) == "nothing", "");

        var none = new Dictionary<string, int>();
        var preview = CleanupMath.Summary("Purge unused", false, 12, 3, new Dictionary<string, int> { ["materials"] = 12 }, new Dictionary<string, int> { ["kept by name"] = 2 }, 0);
        check("cleanup: the preview says nothing changed and how to apply", preview.Contains("preview: 12 to delete") && preview.Contains("found in 3 passes") && preview.Contains("Nothing has changed yet")
            && preview.Contains("Left alone: 2 kept by name") && preview.Contains("apply true"), preview);
        var applied = CleanupMath.Summary("Purge unused", true, 12, 1, new Dictionary<string, int> { ["materials"] = 12 }, none, 4);
        check("cleanup: applied says one Undo step and what went along", applied.Contains("deleted 12") && applied.Contains("Ctrl+Z") && applied.Contains("4 other element(s) went with them")
            && !applied.Contains("passes") && !applied.Contains("apply true"), applied);
        check("cleanup: nothing to delete", CleanupMath.Summary("Clean up imports", false, 0, 1, none, none, 0) == "Clean up imports: nothing to delete.", "");
        var stopped = CleanupMath.Summary("Purge unused", true, 5, 1, new Dictionary<string, int> { ["types"] = 5 }, none, 0, "Stopped by the user; what was deleted stays.");
        check("cleanup: a stop is said instead of the next step", stopped.EndsWith("Stopped by the user; what was deleted stays.", StringComparison.Ordinal), stopped);

        check("cleanup: file hint before saving", CleanupMath.FileHint(null, true, 3).Contains("not been saved"), "");
        check("cleanup: file hint for a preview", CleanupMath.FileHint(52_428_800, false, 3) == "The file is 50 MB on disk now.", CleanupMath.FileHint(52_428_800, false, 3));
        check("cleanup: file hint after deleting says save with Compact", CleanupMath.FileHint(52_428_800, true, 3).Contains("Compact"), "");

        check("cleanup: warning hints", CleanupMath.WarningHint("There are identical instances in the same place.").StartsWith("Duplicates", StringComparison.Ordinal)
            && CleanupMath.WarningHint("Highlighted walls overlap.").StartsWith("Overlapping walls", StringComparison.Ordinal)
            && CleanupMath.WarningHint("Room is not in a properly enclosed region").StartsWith("Unbounded", StringComparison.Ordinal)
            && CleanupMath.WarningHint("Something new").Contains("Manage > Warnings"), "");
        check("cleanup: the four cleanup tools", CleanupMath.Tools.SetEquals(["purge_unused", "delete_unused_views", "cleanup_imports", "cleanup_model"]), "");
    }
}
