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
        var adapted = new List<WorkedCode.Entry>();
        var basis = WorkedCode.Keep(adapted, Code("OST_StructuralColumns", 3), "Berth 9 AZ piles: head down to the cap underside", "run_revit_code", false, "", now);
        WorkedCode.Keep(adapted, Code("OST_StructuralColumns", 3).Replace("var moved = 0;", "var moved = 0; var skipped = 0;"), "Restore 5 Berth 9 AZ piles to their heads", "run_revit_code", false, "", now.AddMinutes(1));
        var third = WorkedCode.Keep(adapted, Code("OST_StructuralColumns", 3) + "\nlog.AppendLine(\"done\");", "Berth 10 AZ piles flush with the soffit", "run_revit_code", false, "", now.AddMinutes(2));
        WorkedCode.Keep(adapted, Code("OST_StructuralColumns", 3) + "\nvar other = 1;", "Tag the doors on level 1", "run_revit_code", false, "", now.AddMinutes(3));
        check("kept: code adapted a little (one more line, a description sharing a word) is another use of the same entry, so it reaches three uses",
            basis is not null && third is not null && third.Id == basis.Id && third.Uses == 3 && third.Code.Contains("done")
            && adapted.Count == 2 && adapted.Any(e => e.Description == "Tag the doors on level 1" && e.Uses == 1), string.Join(" | ", adapted.Select(e => e.Description + " " + e.Uses)));
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

        var redo = new List<WorkedCode.Entry>();
        string Args(string code, string what) => System.Text.Json.JsonSerializer.Serialize(new { code, description = what });
        var past = new List<(string, RedoLog.Step)>
        {
            ("Berth 10", new RedoLog.Step(2, now.AddDays(-14).AddMinutes(5), "run_revit_code", Args(Code("OST_Floors", 4) + "\nvar graded = 1;", "Grade the Berth 10 approach slab tops to the grading CAD"), "{}")),
            ("Berth 10", new RedoLog.Step(1, now.AddDays(-14), "run_revit_code", Args(Code("OST_Floors", 2), "Grade the Berth 10 deck to the grading CAD"), "{}")),
            ("Berth 10", new RedoLog.Step(3, now.AddDays(-13), "create_floors", "{\"level\":\"L1\"}", "{}")),
            ("Berth 9", new RedoLog.Step(4, now.AddDays(-12), "run_revit_code", "not json", "{}")),
        };
        var taken = WorkedCode.FromRedo(redo, past);
        check("from the redo logs: code changes that worked before the library began are kept once, adapted copies as uses; other tools and broken lines skipped",
            taken == 2 && redo.Count == 1 && redo[0].Uses == 2 && redo[0].Model == "Berth 10" && redo[0].Code.Contains("graded")
            && WorkedCode.Find(redo, "grade the approach slabs to the grading").Count == 1, taken + " " + redo.Count);

        string Use(string id, string tool, string code, string what) => System.Text.Json.JsonSerializer.Serialize(new
        {
            timestamp = "2026-10-20T09:00:00Z",
            message = new { content = new object[] { new { type = "tool_use", id, name = "mcp__sketch-promax__" + tool, input = new { code, description = what } } } },
        });
        string Result(string id, bool error) => System.Text.Json.JsonSerializer.Serialize(new
        {
            message = new { content = new object[] { new { type = "tool_result", tool_use_id = id, is_error = error, content = "x" } } },
        });
        var transcript = new[]
        {
            Use("a", "run_revit_query_code", Code("OST_Floors", 1) + "\nvar clashes = 0;", "Check clashes between piles and beams"), Result("a", false),
            Use("b", "run_revit_code", Code("OST_Walls", 1), "Move walls"), Result("b", true),
            Use("c", "capture_view_image", "", "picture"), Result("c", false),
            "not json", "{\"type\":\"summary\"}",
        };
        var fromChats = WorkedCode.FromTranscript(transcript);
        check("from the chat transcripts: every code call that worked, reads too, with its whole code; failed calls and other tools left out",
            fromChats.Count == 1 && fromChats[0].Step.Tool == "run_revit_query_code" && fromChats[0].Step.Arguments.Contains("clashes")
            && fromChats[0].Step.Time == new DateTime(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc), fromChats.Count + "");

        var many = new List<WorkedCode.Entry>();
        for (var i = 0; i < 3010; i++)
            many.Add(new WorkedCode.Entry { Id = "x" + i, Description = "Old work " + i, Tool = "run_revit_code", Code = "", Uses = 1 });
        WorkedCode.Keep(many, Code("OST_StructuralColumns", 1), "Mark columns over 3000", "run_revit_code", false, "", now);
        check("no limit: 3,000 kept and one more is still kept, none dropped", many.Count == 3011 && many[0].Id == "x0", many.Count + "");

        check("topic: the Revit category the code names most", WorkedCode.Topic("new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StructuralColumns)") == "Structural Columns"
            && WorkedCode.Topic("new FilteredElementCollector(doc).OfClass(typeof(Floor))") == "Floors"
            && WorkedCode.Topic("var x = 1;") == "General", WorkedCode.Topic("OST_StructuralColumns"));

        var folder = Path.Combine(Path.GetTempPath(), "spm-worked-" + Guid.NewGuid().ToString("N"));
        try
        {
            var lib = new List<WorkedCode.Entry>();
            var col = WorkedCode.Keep(lib, Code("OST_StructuralColumns", 1), "Mark the columns", "run_revit_code", false, "M", now)!;
            var wal = WorkedCode.Keep(lib, "var walls = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Walls).WhereElementIsNotElementType().ToList();\n" +
                "var tall = walls.Where(w => w.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() > 10).Select(w => w.Id.ToString()).ToList();\nreturn new { count = walls.Count, tall };", "Count the walls", "run_revit_query_code", false, "M", now)!;
            WorkedCode.Save(folder, lib);
            var index = WorkedCode.ReadIndex(folder);
            var colFile = WorkedCode.CodeFile(folder, col);
            check("library: one file per code in its topic's folder; the index has no code, is sorted by topic and keeps the shape numbers",
                File.Exists(colFile) && Path.GetFileName(Path.GetDirectoryName(colFile)) == "Structural Columns" && File.ReadAllText(colFile) == col.Code
                && File.Exists(WorkedCode.CodeFile(folder, wal)) && index.Count == 2 && index.All(e => e.Code.Length == 0 && e.Sig is { Length: WorkedCode.SignatureSize })
                && index.Select(e => e.Topic).SequenceEqual(index.Select(e => e.Topic).OrderBy(t => t, StringComparer.Ordinal)), string.Join(",", index.Select(e => e.Topic)));
            var reused = WorkedCode.Keep(index, Code("OST_StructuralColumns", 1), "Mark the columns", "run_revit_code", false, "M", now.AddHours(1));
            check("library: read back from the index, the same code is one more use, not a new entry", reused?.Id == col.Id && index.Count == 2 && reused.Uses == 2, index.Count + "");
        }
        finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
    }
}
