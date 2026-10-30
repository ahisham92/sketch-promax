using SketchProMax.Chat;
using SketchProMax.Tools;

/// <summary>Speed: where an answer's time went (answer_time), and code written again and again offered as a tool.</summary>
internal static class SpeedTests
{
    public static void Run(Action<string, bool, string> check)
    {
        var t0 = new DateTime(2026, 10, 30, 20, 0, 0, DateTimeKind.Utc);

        // Claude Code's own thinking time comes with its result.
        var result = new StreamParser().ParseLine("""{"type":"result","subtype":"success","is_error":false,"result":"ok","session_id":"s","duration_ms":42000,"duration_api_ms":30100.5}""").OfType<ResultEvent>().Single();
        check("timing: the result carries Claude Code's thinking time", result.ApiMs == 30100, result.ApiMs.ToString());
        check("timing: a result without it reads 0", new StreamParser().ParseLine("""{"type":"result","subtype":"success","result":"ok"}""").OfType<ResultEvent>().Single().ApiMs == 0, "");

        var timing = new AnswerTiming(t0);
        timing.ToolStarted("a", "create_walls", t0.AddSeconds(5));
        timing.ToolStarted("a", "create_walls", t0.AddSeconds(6)); // the same call seen again: its start stays
        timing.ToolEnded("a", t0.AddSeconds(9));
        timing.ToolStarted("b", "tag_elements_in_view", t0.AddSeconds(20));
        timing.ToolEnded("b", t0.AddSeconds(25.5));
        timing.ToolEnded("zz", t0.AddSeconds(26)); // a result with no start is ignored
        var line = timing.Line("Job 4", t0.AddSeconds(42), 30100);
        check("timing: the line has the whole, thinking, the calls with the longest, and the rest",
            line == "Job 4 answer: 42.0 s in all; thinking 30.1 s; 2 tool calls 9.5 s (longest tag_elements_in_view 5.5 s); other 2.4 s", line);
        check("timing: no thinking figure: no rest either", new AnswerTiming(t0).Line("Chat", t0.AddSeconds(3), 0) == "Chat answer: 3.0 s in all", "");
        timing.Restart(t0.AddMinutes(1));
        check("timing: the next answer starts from nothing", timing.Line("Job 4", t0.AddMinutes(1).AddSeconds(2), 1000) == "Job 4 answer: 2.0 s in all; thinking 1.0 s; other 1.0 s", timing.Line("Job 4", t0.AddMinutes(1).AddSeconds(2), 1000));
        var overlap = new AnswerTiming(t0);
        overlap.ToolStarted("x", "query_elements", t0);
        overlap.ToolEnded("x", t0.AddSeconds(8));
        check("timing: thinking and calls that overlap leave no negative rest", !overlap.Line("Chat", t0.AddSeconds(10), 9000).Contains("other"), overlap.Line("Chat", t0.AddSeconds(10), 9000));

        // Code repeats: the same shape with other names, numbers and comments is the same code.
        var body = string.Join("\n", Enumerable.Range(0, 6).Select(i =>
            $"var walls{i} = new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>().Where(w => w.LevelId == level.Id).ToList();"));
        string Code(string level, double height, string comment) => $"// {comment}\nvar level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().First(l => l.Name == \"{level}\");\n{body}\nreturn walls0.Count * {height};";
        var a = Code("Level 1", 3.5, "first try");
        var b = Code("Level 02 // roof", 12, "again, other level");
        check("repeats: other names, numbers and comments give the same shape", CodeRepeats.Shape(a) == CodeRepeats.Shape(b), CodeRepeats.Shape(b));
        check("repeats: different code has another shape", CodeRepeats.Shape(a) != CodeRepeats.Shape(a.Replace("Wall", "Floor")), "");
        var shapes = new Dictionary<string, CodeRepeats.Entry>();
        check("repeats: the first and second time say nothing", CodeRepeats.Count(shapes, a, "count walls", t0) is null && CodeRepeats.Count(shapes, b, "count walls", t0.AddDays(1)) is null, "");
        var tip = CodeRepeats.Count(shapes, a, "count walls", t0.AddDays(2));
        check("repeats: the third time offers it as a tool", tip is not null && tip.Contains("3 times") && tip.Contains("save_as_tool"), tip ?? "");
        check("repeats: said once per shape", CodeRepeats.Count(shapes, a, "", t0.AddDays(3)) is null && shapes.Single().Value.Count == 4, "");
        check("repeats: a short snippet isn't counted", CodeRepeats.Count(shapes, "return new FilteredElementCollector(doc).OfClass(typeof(Wall)).GetElementCount();", "", t0) is null && shapes.Count == 1, "");
        var later = new Dictionary<string, CodeRepeats.Entry>();
        CodeRepeats.Count(later, a, "", t0);
        CodeRepeats.Count(later, a, "", t0.AddDays(1));
        check("repeats: uses more than 30 days apart start again", CodeRepeats.Count(later, a, "", t0.AddDays(40)) is null && later.Single().Value.Count == 1, "");
    }
}
