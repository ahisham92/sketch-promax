using SketchProMax.Tools;

/// <summary>
/// Code that worked once is kept and found again (WorkedCode): one entry per shape, found by the words of what it did,
/// named on a failed call and with a typed message. No Revit here.
/// </summary>
internal static class WorkedCodeTests
{
    private static string Code(string category, int n) =>
        "var piles = new FilteredElementCollector(doc, doc.ActiveView.Id).WhereElementIsNotElementType().Where(e => e.Category?.Name == \"" + category + "\").ToList();\n" +
        "var moved = 0;\nforeach (var p in piles) { if (p.Location is LocationPoint lp) { ElementTransformUtils.MoveElement(doc, p.Id, new XYZ(" + n + ", 0, 0)); moved++; } }\n" +
        "return new { moved };";

    public static void Run(Action<string, bool, string> check)
    {
        var now = new DateTime(2026, 11, 17, 8, 0, 0, DateTimeKind.Utc);
        var list = new List<WorkedCode.Entry>();
        var first = WorkedCode.Keep(list, Code("OST_StructuralFoundation", 3), "Move piles to the middle between tie rods", "run_revit_code", false, "Model A", now);
        var again = WorkedCode.Keep(list, Code("OST_StructuralColumns", 7), "Move the selected piles between the tie rods", "run_revit_code", true, "", now.AddDays(1));
        check("kept: the same shape with other names and numbers is one entry, the newest code, counted twice",
            first is not null && again is not null && list.Count == 1 && list[0].Uses == 2 && list[0].Code.Contains("OST_StructuralColumns")
            && list[0].Model == "Model A" && list[0].OwnTransactions && list[0].Id.Length == 8, "");
        check("kept: not a short snippet, code with no description, or code too long to keep",
            WorkedCode.Keep(list, "return doc.Title;", "Model title", "run_revit_query_code", false, "", now) is null
            && WorkedCode.Keep(list, Code("OST_Walls", 1) + "\nreturn 1 + 2 + 3;", "", "run_revit_code", false, "", now) is null
            && WorkedCode.Keep(list, new string('x', WorkedCode.MaxCode + 1), "Too long", "run_revit_code", false, "", now) is null && list.Count == 1, "");

        WorkedCode.Keep(list, Code("OST_StructuralFraming", 2) + "\nvar trimmed = piles.Count;", "Trim framing beams to the slab edge in the active view", "run_revit_code", false, "Model A", now);
        WorkedCode.Keep(list, Code("OST_Floors", 2) + "\nvar tags = 0; tags++;", "Count untagged slabs per level", "run_revit_query_code", false, "Model B", now);
        var found = WorkedCode.Find(list, "trim the selected beam to the slab limit");
        check("find: by the words of what it did, plurals and endings folded, best first",
            found.Count >= 1 && found[0].Entry.Description.StartsWith("Trim framing") && found[0].Shared >= 3, found.Count + "");
        check("find: nothing alike finds nothing; common words don't count",
            WorkedCode.Find(list, "export the sheets to PDF").Count == 0 && WorkedCode.Find(list, "please do this for all the elements").Count == 0, "");
        check("words: lower case, no common words, plural and -ing folded",
            WorkedCode.Words("Trimming the Piles and beams").SetEquals(["trimm", "pile", "beam"]), string.Join(",", WorkedCode.Words("Trimming the Piles and beams")));

        var pointer = WorkedCode.Pointer(list, "Trim beams at slab edge", "return 0;");
        check("failed call: ends with the kept code that did similar work, by id",
            pointer.Contains(found[0].Entry.Id + ": Trim framing beams") && pointer.Contains("worked_code (action=get, id)"), pointer);
        check("failed call: the failing code's own entry is not offered back, and nothing alike gives nothing",
            WorkedCode.Pointer(list, "Move piles between tie rods", Code("OST_Walls", 9)).Length == 0 && WorkedCode.Pointer(list, "Export sheets", "return 0;").Length == 0, "");

        var hint = WorkedCode.Hint(list, "can you move the piles to be in the middle between the tie rods");
        check("message: names the kept code, not shown to the user, only when three words match",
            hint.StartsWith("\n\n[Sketch ProMax, not typed by the user:") && hint.Contains("Move the selected piles between the tie rods") && hint.Contains("worked 2x")
            && WorkedCode.Hint(list, "move the slab").Length == 0, hint);

        // ---- The team's code
        var theirs = new List<WorkedCode.Entry>();
        WorkedCode.Keep(theirs, Code("OST_Ramps", 4) + "\nvar colour = new Color(255, 0, 0);", "Colour the piles clashing with tie rods red in the active view", "run_revit_code", false, "Model C", now);
        WorkedCode.Keep(theirs, Code("OST_StructuralFraming", 2) + "\nvar trimmed = piles.Count;", "Their trim of beams", "run_revit_code", false, "", now);
        var json = System.Text.Json.JsonSerializer.Serialize(theirs);
        var team = WorkedCode.FromTeam([("PC-2", json), ("PC-1", json), ("PC-3", "not json")], "pc-1");
        var merged = WorkedCode.Merge(list, team);
        check("team: a colleague's code is found here, marked with whose PC; this PC's own file and a broken file are left out",
            team.Count == 2 && team.All(e => e.By == "PC-2") && WorkedCode.Find(merged, "colour piles clashing with the tie rods")[0].Entry.By == "PC-2"
            && WorkedCode.Line(team[0]).Contains("kept by PC-2"), team.Count + "");
        check("team: for a shape this PC has, its own code stays", merged.Count == list.Count + 1 && merged.Single(e => e.Description.StartsWith("Trim framing")).By == "", merged.Count + "");
        check("team tools: installed when new, updated while untouched, kept when it is this PC's own, changed here or deleted here",
            WorkedCode.Decide(null, null, "t1") == "install" && WorkedCode.Decide("t1", "t1", "t2") == "update" && WorkedCode.Decide("mine", null, "t1") == "keep"
            && WorkedCode.Decide("edited", "t1", "t2") == "keep" && WorkedCode.Decide(null, "t1", "t2") == "keep" && WorkedCode.Decide("t1", "t0", "t1") == "keep", "");

        var many = new List<WorkedCode.Entry>();
        for (var i = 0; i < WorkedCode.MaxEntries + 5; i++)
            WorkedCode.Keep(many, Code("OST_Walls", 1) + string.Concat(Enumerable.Repeat("\nlog.AppendLine(doc.Title);", i + 1)), "Work number " + i, "run_revit_code", false, "", now.AddMinutes(i));
        check("kept: capped, the oldest go first", many.Count == WorkedCode.MaxEntries && many.All(e => e.Description != "Work number 0") && many.Any(e => e.Description == "Work number " + (WorkedCode.MaxEntries + 4)), many.Count + "");
    }
}
