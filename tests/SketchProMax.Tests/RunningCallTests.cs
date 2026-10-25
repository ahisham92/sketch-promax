using System.Text.Json;
using SketchProMax.Revit;

/// <summary>The call running in Revit, kept on disk so a Revit that closes by itself mid-call can be explained at the next start.</summary>
internal static class RunningCallTests
{
    public static void Run(Action<string, bool, string> check)
    {
        var folder = Path.Combine(Path.GetTempPath(), "spm-running-" + Guid.NewGuid().ToString("N"));
        RunningCall.FolderOverride = folder;
        var was = RunningCall.Enabled;
        try
        {
            var self = System.Diagnostics.Process.GetCurrentProcess().Id;
            var mine = RunningCall.FileOf(self);
            var args = new Dictionary<string, JsonElement> { ["code"] = JsonSerializer.SerializeToElement("var floors = new FilteredElementCollector(doc);") };

            RunningCall.Enabled = false;
            RunningCall.Begin("the chat", "run_revit_query_code", "Model A", args);
            check("running call: nothing is written while off (TritoRevit)", !File.Exists(mine), "");

            RunningCall.Enabled = true;
            RunningCall.Version = "9.9.9";
            RunningCall.Begin("the field test", "run_field_test", "FieldTest-local", null);
            RunningCall.Begin("the field test", "purge_unused", "FieldTest-local", new Dictionary<string, JsonElement>());
            var inner = File.Exists(mine) ? File.ReadAllText(mine) : "";
            check("running call: the innermost call is on disk while it runs", inner.Contains("purge_unused") && inner.Contains("9.9.9"), inner);
            RunningCall.End();
            var outer = File.Exists(mine) ? File.ReadAllText(mine) : "";
            check("running call: when it ends, the call around it is the running one again", outer.Contains("run_field_test") && !outer.Contains("purge_unused"), outer);
            RunningCall.End();
            check("running call: nothing running leaves no file", !File.Exists(mine), "");

            // A crashed Revit (pid 999999, not running) left its call behind; a running one (pid 424242) keeps its own.
            RunningCall.Begin("the chat", "run_revit_query_code", "Model A", args);
            File.Copy(mine, RunningCall.FileOf(999999));
            File.Copy(mine, RunningCall.FileOf(424242));
            var lines = RunningCall.Recover(self, pid => pid == 424242);
            check("running call: the next start names the call Revit closed during, with its arguments",
                lines.Count == 1 && lines[0].StartsWith("Revit closed by itself while run_revit_query_code was running (from the chat) on 'Model A'")
                && lines[0].Contains("FilteredElementCollector") && lines[0].Contains("Sketch ProMax 9.9.9"), string.Join(" | ", lines));
            check("running call: its file is removed; this Revit's own and another running Revit's stay",
                !File.Exists(RunningCall.FileOf(999999)) && File.Exists(RunningCall.FileOf(424242)) && File.Exists(mine), "");
            RunningCall.Clear();
            check("running call: the core stopping clears its own", !File.Exists(mine), "");

            var longArgs = new Dictionary<string, JsonElement> { ["code"] = JsonSerializer.SerializeToElement(new string('x', 9000)) };
            RunningCall.Begin("the chat", "run_revit_code", "", longArgs);
            var saved = JsonSerializer.Deserialize<RunningCall.Entry>(File.ReadAllText(mine))!;
            check("running call: long arguments are cut as the activity log cuts them", saved.Arguments.Length == RunningCall.MaxArgs + 1, saved.Arguments.Length.ToString());
            RunningCall.Clear();
        }
        finally
        {
            RunningCall.Enabled = was;
            RunningCall.FolderOverride = null;
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }
}
