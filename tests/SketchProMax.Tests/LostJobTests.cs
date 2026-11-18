/// <summary>
/// Bug 87: a call cut off before it reached Revit (an update switch) must never hold Revit busy. Jobs need Revit's API,
/// so their behaviour is checked by the field test (job.lost); these check the source keeps the guards.
/// </summary>
internal static class LostJobTests
{
    public static void RunSources(string root, Action<string, bool, string> check)
    {
        string Read(string path) => File.ReadAllText(Path.Combine(root, path)).Replace("\r\n", "\n");
        var app = Read("src/SketchProMax/App.cs");
        var stop = app.Substring(app.IndexOf("internal static void StopCore", StringComparison.Ordinal));
        check("lost job: stopping the core drops queued calls before its dispatcher stops",
            stop.IndexOf("Jobs.DropQueued", StringComparison.Ordinal) is var d and > 0 && d < stop.IndexOf("Dispatcher.Shutdown", StringComparison.Ordinal), "");
        var bridge = Read("src/SketchProMax/Bridge/BridgeServer.cs");
        var guard = bridge.IndexOf("return job.FailBeforeStart(", StringComparison.Ordinal);
        check("lost job: a call the dispatcher refuses ends its job, before its task would be set",
            guard > 0 && guard < bridge.IndexOf("job.Task = Jobs.Follow(job, firstCall);", StringComparison.Ordinal), "");
        var jobs = Read("src/SketchProMax/Bridge/Jobs.cs");
        check("lost job: only InTheWay decides busy, and a dropped job never is",
            jobs.Contains("busyWith = Recent.LastOrDefault(InTheWay);") && jobs.Contains("if (j.Abandoned) return false;"), "");
        check("lost job: cancel_job answers for a dropped job", jobs.Contains("job.Task is { IsCompleted: true } || job.Abandoned"), "");
        var features = Read("src/SketchProMax/Tools/FieldTest.Features.cs");
        check("lost job: the field test runs the job.lost probe", features.Contains("\"job.lost\", LostJobProbe"), "");
    }
}
