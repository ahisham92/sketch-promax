using System.Text.RegularExpressions;
using SketchProMax.Tools;

/// <summary>
/// Revit closing during the field test: the breadcrumb journal (FieldTestJournal) and the rules that keep Sketch ProMax
/// from closing Revit (previews that never delete, nothing escaping a background thread or an async void handler).
/// </summary>
internal static class CrashSafetyTests
{
    public static void Run(string repoRoot, Action<string, bool, string> check)
    {
        // The whole round: write, Revit "closes", read back at the next start, interrupted rows, Continue, crash memory.
        var folder = Path.Combine(Path.GetTempPath(), "spm-journal-" + Guid.NewGuid().ToString("N"));
        try
        {
            var problem = FieldTestJournal.SelfCheck(folder);
            check("journal: a run Revit closed during is found, reported and continued after", problem is null, problem ?? "");
        }
        finally { try { Directory.Delete(folder, true); } catch (IOException) { } }

        // A journal cut off mid-line by the crash still reads.
        var header = new FieldTestJournal.Header(42, "2024", "0.34.40", new DateTime(2026, 10, 22, 9, 0, 0), "field test", null, null, ["steel"], true, true, true, false, 120, false);
        var text = System.Text.Json.JsonSerializer.Serialize(new { kind = "run", header }) + "\n" +
                   """{"kind":"start","family":"steel","step":"place_bracing","index":1,"at":"2026-10-22T09:01:00"}""" + "\n" +
                   """{"kind":"row","family":"steel","tool":"place_bracing","status":"pa""";
        var c = FieldTestJournal.Parse(text);
        check("journal: a last line cut short is ignored", c is not null && c.Open?.Step == "place_bracing" && c.Rows.Count == 0 && c.Header.Families?.Single() == "steel", "");
        check("journal: no header, no journal", FieldTestJournal.Parse("""{"kind":"start","step":"x"}""") is null, "");

        // Which Revit is gone: the same check as the running-call file (RunningCall.IsRevitRunning) decides.
        check("journal: a journal of a Revit that is gone is left over", FieldTestJournal.IsLeftover(header, 7, _ => false), "");
        check("journal: a running Revit's journal is not", !FieldTestJournal.IsLeftover(header, 7, pid => pid == 42), "");
        check("journal: this Revit's own journal is not", !FieldTestJournal.IsLeftover(header, 42, _ => false), "");

        // What the user is told, and what Continue says.
        var tell = FieldTestJournal.Tell("purge_unused", "Everything else", @"C:\Reports");
        check("journal: the user is told which step closed Revit and where the report is",
            tell == @"Revit closed during the field test, while running purge_unused (Everything else). A report of what ran is in C:\Reports.", tell);
        var setup = new FieldTestJournal.Contents { Header = header, Open = new("setup", FieldTestJournal.SetupPrefix + "building the test project", 1, DateTime.Now) };
        check("journal: a crash while building the test project can't be continued after", !FieldTestJournal.CanContinue(setup), "");
        var resume = FieldTestJournal.ResumeFrom(new FieldTestJournal.Contents { Header = header, Open = new("other", "purge_unused", 9, DateTime.Now) }, []);
        check("journal: the crashed step is a failure, not run again", resume.Skips("purge_unused") && resume.CrashedMessage("purge_unused").Contains("not run again"), "");

        // Previews never delete: Revit 2024 closed inside purge_unused's preview, which deleted families in a rolled-back transaction.
        string Source(params string[] parts) => File.ReadAllText(Path.Combine(new[] { repoRoot, "src", "SketchProMax" }.Concat(parts).ToArray()));
        var cleanup = Source("Tools", "CleanupTools.cs");
        var preview = Body(cleanup, "private static void Preview(");
        check("purge: the cleanup preview opens no transaction and deletes nothing", preview.Length > 0 && !preview.Contains("Transaction") && !preview.Contains(".Delete(")
            && preview.Contains("DeleteSafety.Dependents"), preview.Length == 0 ? "Preview not found" : "");
        var sweep = Body(cleanup, "public static Outcome Sweep(");
        check("purge: Sweep only deletes when applying", !sweep.Contains("new Transaction(") && !sweep.Contains("SubTransaction"), "");
        check("purge: families and family types are deleted one at a time", Body(cleanup, "private static void DeleteApply(").Contains("DeleteSafety.IsFamilyContent"), "");
        check("purge: the rolled-back trial delete is gone", !cleanup.Contains("DeletePreview") && !cleanup.Contains("new SubTransaction"), "");
        var also = Body(Source("Tools", "RedlineTools.cs"), "public static string? AlsoDeleted(Document doc, ICollection<ElementId> ids)");
        check("purge: \"it also deletes\" is read, not tried", also.Contains("DeleteSafety.Dependents") && !also.Contains("Transaction"), "");
        check("delete_elements: what goes along is read, not tried", !Source("Tools", "ModelDataTools.cs").Contains("preview delete"), "");
        check("purge: the preview says applying finds more", CleanupMath.PreviewPassesNote.Contains("deletes nothing"), "");

        // Nothing escapes a background thread or an async void handler (either closes Revit).
        var engine = Source("Chat", "ChatEngine.cs");
        check("threads: the chat's output and error readers catch everything", Body(engine, "private void ReadOutput(").Contains("catch (Exception) { }")
            && Body(engine, "private void ReadErrors(").Contains("catch (Exception)"), "");
        check("threads: the process's Exited handler can't throw", engine.Contains("process.Exited += (_, _) => { try { OnExited(); } catch (Exception) { } };"), "");
        var http = Source("Bridge", "McpHttp.cs");
        check("threads: cancelling a chat call never throws out (a failed callback is an AggregateException)", !http.Contains("Cancel(); } catch (ObjectDisposedException)"), "");
        var asyncVoid = Directory.GetFiles(Path.Combine(repoRoot, "src", "SketchProMax"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"async void (\w+)\(").Select(m => (File: f, Name: m.Groups[1].Value, Text: File.ReadAllText(f))))
            .Where(x => !Body(x.Text, $"async void {x.Name}(").Contains("catch (Exception"))
            .Select(x => x.Name).ToList();
        check("threads: every async void handler catches everything", asyncVoid.Count == 0, string.Join(", ", asyncVoid));
        var app = Source("App.cs");
        check("threads: the crash guard is on with the core and off with it", app.Contains("CrashGuard.Start(Ui)") && app.Contains("CrashGuard.Stop()"), "");
    }

    /// <summary>The text of the member whose declaration starts with <paramref name="start"/>, to its closing brace.</summary>
    private static string Body(string source, string start)
    {
        var at = source.IndexOf(start, StringComparison.Ordinal);
        if (at < 0) return "";
        var open = source.IndexOf('{', at);
        if (open < 0) return "";
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source.Substring(at, i - at + 1);
        }
        return "";
    }
}
