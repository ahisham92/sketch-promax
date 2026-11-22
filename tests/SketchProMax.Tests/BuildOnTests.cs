using System.Text.Json;
using SketchProMax.Tools;

/// <summary>
/// Bug 135: the library holds each piece of work once (Merge), a code call that isn't built on kept code is given the
/// closest kept code to change a little (StartFrom, RevitTargets), a job like one done before gets its steps to replay
/// (JobRecipes), and a new job sees every ready-made tool. No Revit here.
/// </summary>
internal static class BuildOnTests
{
    private static string Slab(string extra) =>
        "var floors = new FilteredElementCollector(doc).OfClass(typeof(Floor)).Cast<Floor>().Where(f => f.Name.Contains(\"DECK\")).ToList();\n" +
        "foreach (var f in floors) { var bb = f.get_BoundingBox(null); log.AppendLine(f.Id + \" top \" + (bb.Max.Z * 304.8) + \" bottom \" + (bb.Min.Z * 304.8)); }\n" +
        extra + "return floors.Count;";

    private static string Walls =>
        "var walls = new FilteredElementCollector(doc, doc.ActiveView.Id).OfClass(typeof(Wall)).ToElements();\n" +
        "using (var t = new Transaction(doc, \"tag\")) { t.Start(); foreach (var w in walls) IndependentTag.Create(doc, doc.ActiveView.Id, new Reference(w), false, TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal, XYZ.Zero); t.Commit(); }\n" +
        "return walls.Count;";

    public static void Run(Action<string, bool, string> check)
    {
        var now = new DateTime(2026, 11, 22, 8, 0, 0, DateTimeKind.Utc);
        WorkedCode.Entry Make(string code, string what, int uses, int minutes)
        {
            var shingles = WorkedCode.Shingles(CodeRepeats.Shape(code));
            return new WorkedCode.Entry
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 8), Description = what, Code = code, Tool = "run_revit_query_code", Uses = uses,
                First = now.AddMinutes(minutes), Last = now.AddMinutes(minutes), Sig = WorkedCode.Signature(shingles), Size = shingles.Count,
            };
        }

        // Merge: the same code written again folds into the most used one.
        var a = Make(Slab(""), "Read deck slab tops and bottoms", 4, 0);
        var b = Make(Slab("log.AppendLine(\"x\");\n"), "Read slab top and bottom of the deck", 1, 10);
        var c = Make(Walls, "Tag walls in the view", 2, 5);
        var lib = new List<WorkedCode.Entry> { b, a, c };
        var removed = WorkedCode.Merge(lib);
        check("library (bug 135): the same code kept twice is folded into the most used, with both uses and the latest date",
            lib.Count == 2 && lib.Contains(a) && lib.Contains(c) && removed.Single() == b && a.Uses == 5 && a.Last == b.Last, string.Join(", ", lib.Select(e => e.Description + " " + e.Uses)));
        check("library (bug 135): different code is never folded together",
            WorkedCode.Merge([Make(Slab(""), "Read deck slab tops", 1, 0), Make(Walls, "Read deck walls", 1, 1)]).Count == 0, "");

        // StartFrom: code not built on kept code, while kept code did this work.
        var fresh = "var list = new List<string>();\nforeach (Floor f in new FilteredElementCollector(doc).OfClass(typeof(Floor))) { var box = f.get_BoundingBox(null); list.Add(f.Id + \": \" + box.Max.Z + \"/\" + box.Min.Z); }\nreturn string.Join(\";\", list);";
        var start = WorkedCode.StartFrom(lib, "Read the deck slab tops and bottoms on Berth 9", fresh, "run_revit_query_code");
        check("build on (bug 135): fresh code for work kept code already did is pointed to that code", start == a, start?.Description ?? "none");
        check("build on (bug 135): code built on kept code runs as written",
            WorkedCode.StartFrom(lib, "Read the deck slab tops and bottoms", Slab("log.AppendLine(\"Berth 9\");\n"), "run_revit_query_code") is null, "");
        check("build on (bug 135): work nothing kept did runs as written",
            WorkedCode.StartFrom(lib, "Export the drawing list to Excel", fresh, "run_revit_query_code") is null
            && WorkedCode.StartFrom(lib, "Read the deck slab tops and bottoms", fresh, "run_revit_code") is null, "");
        var text = WorkedCode.StartFromText(a, a.Code);
        check("build on (bug 135): the held answer gives the kept code and says to change only what differs, and that sending again runs",
            text.StartsWith("Not run yet") && text.Contains(a.Code) && text.Contains("change only") && text.Contains("send your own call again unchanged"), text);

        {
            static Dictionary<string, JsonElement> A(string what, string code) => new() { ["description"] = JsonSerializer.SerializeToElement(what), ["code"] = JsonSerializer.SerializeToElement(code) };
            SketchProMax.Bridge.RevitTargets.StartFromFor = (what, code, tool) => code.Contains("list.Add") ? "Not run yet. Kept code: " + what : null;
            var main = new SketchProMax.Bridge.RevitTargets(new SketchProMax.Bridge.ConnectorCaller(true, null, null, null, null));
            var held = main.HoldFirstCode("run_revit_query_code", A("deck tops", fresh));
            var again = main.HoldFirstCode("run_revit_query_code", A("deck tops", fresh));
            var other = main.HoldFirstCode("run_revit_query_code", A("deck tops", Slab("")));
            var job = new SketchProMax.Bridge.RevitTargets(new SketchProMax.Bridge.ConnectorCaller(true, "job-9", null, null, null));
            var jobFirst = job.HoldFirstCode("run_revit_query_code", A("deck tops", fresh));
            var desktop = new SketchProMax.Bridge.RevitTargets(new SketchProMax.Bridge.ConnectorCaller(false, null, null, null, null)).HoldFirstCode("run_revit_query_code", A("deck tops", fresh));
            SketchProMax.Bridge.RevitTargets.StartFromFor = null;
            check("build on (bug 135): every chat and job code call not built on kept code is held once with that code; sent again it runs; the desktop app is never held",
                held is not null && again is null && other is null && jobFirst is not null && jobFirst.StartsWith("Not run yet. Kept code") && desktop is null, held + " | " + jobFirst);
        }

        // Every ready-made tool goes to a new job.
        var tools = Enumerable.Range(1, 45).Select(i => new WorkedCode.ReadyTool("tool_" + i, "does " + i, i)).ToList();
        var all = WorkedCode.AllTools(tools);
        check("tools (bug 135): a new job sees every ready-made tool by name, most used first, up to 40",
            all.Contains("tool_45 (does 45)") && all.Contains("tool_6") && !all.Contains("tool_5 ") && all.Contains("and 5 more") && WorkedCode.AllTools([]) == "", all);

        // A job done before: its steps from its transcript, found by its words.
        string Use(string id, string name, object input) => JsonSerializer.Serialize(new { type = "assistant", message = new { content = new object[] { new { type = "tool_use", id, name, input } } } });
        string Result(string id, bool error, string text = "ok") => JsonSerializer.Serialize(new { type = "user", message = new { content = new object[] { new { type = "tool_result", tool_use_id = id, is_error = error, content = text } } } });
        var lines = new[]
        {
            Use("1", "mcp__sketch-promax__chat_jobs", new { action = "list" }), Result("1", false),
            Use("2", "mcp__sketch-promax__run_revit_query_code", new { description = "Read the pile heads", code = "return 1;" }), Result("2", false),
            Use("3", "mcp__sketch-promax__run_revit_code", new { description = "Lower the pile heads", code = "return 2;" }), Result("3", true),
            Use("4", "mcp__sketch-promax__run_revit_code", new { description = "Lower the pile heads to the soffit", code = "return 3;" }), Result("4", false),
            Use("5", "mcp__sketch-promax__tag_elements_in_view", new { view_id = 7 }), Result("5", false),
            Use("6", "Read", new { file_path = "x" }), Result("6", false),
            Use("7", "mcp__sketch-promax__run_revit_code", new { description = "again", code = "return 4;" }), Result("7", false, "Not run yet. Kept code"),
        };
        var steps = JobRecipes.FromTranscript(lines);
        check("replay (bug 135): a job's steps are its Sketch ProMax calls that worked, in order, without job chatter, failures or held calls",
            steps.Count == 3 && steps[0].What == "Read the pile heads" && steps[1].Input.Contains("return 3;") && steps[2].Tool == "tag_elements_in_view", string.Join(" | ", steps.Select(s => s.Tool + ":" + s.What)));
        var recipes = new List<JobRecipes.Recipe>
        {
            new() { Key = "k1", Title = "Berth 9 pile heads", Task = "Lower the Berth 9 king pile heads to the deck soffit and tag them on the pile plans", Ended = now, Steps = 3, Passed = true },
            new() { Key = "k2", Title = "Sheet list", Task = "Make the drawing list of the issue sheets with revision dates", Ended = now, Steps = 4 },
        };
        var best = JobRecipes.Best(recipes, "Berth 10 pile heads: lower the king pile heads to the deck soffit and tag them on the plans", null);
        check("replay (bug 135): a job worded like one done before finds it, and not an unrelated one, and never itself",
            best.Count == 1 && best[0].Recipe.Key == "k1" && JobRecipes.Best(recipes, "Berth 10 pile heads: lower the king pile heads to the deck soffit and tag them on the plans", "k1").Count == 0
            && JobRecipes.Best(recipes, "Colour the fenders by type in the 3D view of Berth 10", null).Count == 0, string.Join(",", best.Select(x => x.Recipe.Key + " " + x.Score)));
        var hint = JobRecipes.Hint(recipes[0], steps);
        check("replay (bug 135): the hint names the earlier job, each step with its id, and says to replay with only what differs changed",
            hint.Contains("Berth 9 pile heads") && hint.Contains("passed its check") && hint.Contains("2. run_revit_code: Lower the pile heads to the soffit [id k1#2]")
            && hint.Contains("worked_code action=get") && hint.Contains("only what differs changed"), hint);
        check("replay (bug 135): finished jobs are remembered as they end and from every saved chat once, and a new job gets the replay",
            File.ReadAllText(Path.Combine(Root(), "src/SketchProMax/Chat/ChatPane.Jobs.cs")).Contains("SketchProMax.Tools.JobRecipes.Remember(job.Key, job.Title, job.Task, JobSessions.Get(JobsFolder(_conv), job.Key)")
            && File.ReadAllText(Path.Combine(Root(), "src/SketchProMax/Chat/ChatPane.Jobs.cs")).Contains("SketchProMax.Tools.JobRecipes.HintFor(job.Title + \" \" + job.Task, job.Key)")
            && File.ReadAllText(Path.Combine(Root(), "src/SketchProMax/Chat/ChatPane.cs")).Contains("Tools.JobRecipes.BackfillOnce(JobsFolder)")
            && File.ReadAllText(Path.Combine(Root(), "src/SketchProMax/Chat/ChatPane.cs")).Contains("Tools.WorkedCode.MergeDaily()")
            && File.ReadAllText(Path.Combine(Root(), "src/SketchProMax/Bridge/BridgeServer.cs")).Contains("RevitTargets.StartFromFor = WorkedCode.StartFromFor;"), "");
    }

    private static string Root()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src", "SketchProMax"))) dir = Path.GetDirectoryName(dir);
        return dir ?? ".";
    }
}
