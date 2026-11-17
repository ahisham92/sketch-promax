using System.Diagnostics;
using System.Text.RegularExpressions;
using SketchProMax.Chat;
using SketchProMax.Tools;

/// <summary>
/// Bug 72: the chat's Claude Code, started from inside Revit, inherited Revit's open handles to the central model's lock
/// files and held Synchronize with Central for 27 minutes. Every program Sketch ProMax starts with redirected input or
/// output goes through ChildProcess (inherits only its own pipes); long-lived ones too.
/// </summary>
internal static class SyncLockTests
{
    public static void Run(Action<string, bool, string> check)
    {
        check("child process: the program is quoted like Process.Start", ChildProcess.CommandLine(@"C:\Program Files\x\claude.exe", "-p --x") == "\"C:\\Program Files\\x\\claude.exe\" -p --x"
            && ChildProcess.CommandLine("\"a b.exe\"", "") == "\"a b.exe\"", ChildProcess.CommandLine(@"C:\Program Files\x\claude.exe", "-p --x"));
        var block = ChildProcess.EnvironmentBlock([new("b", "2"), new("A", "1"), new("", "x"), new("C", null)]);
        check("child process: environment block sorted, \\0 separated, ends in \\0\\0", block == "A=1\0b=2\0C=\0\0", block.Replace("\0", "|"));
        check("code hints: a family that won't load back gets its fix", ScriptSource.RuntimeHint("Family loading failed.").Contains("run_revit_code"), ScriptSource.RuntimeHint("Family loading failed."));

        RunWindows(check);
        // Off Windows ChildProcess is Process.Start: it still hands back working streams.
        var shell = Environment.OSVersion.Platform != PlatformID.Win32NT && File.Exists("/bin/sh") ? "/bin/sh" : null;
        if (shell is not null)
        {
            using var child = ChildProcess.Start(new ProcessStartInfo(shell, "-c \"read x; echo got $x; echo err 1>&2\"")
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            });
            child.StandardInput!.WriteLine("hello");
            child.StandardInput.Close();
            var output = child.StandardOutput!.ReadToEnd().Trim();
            var error = child.StandardError!.ReadToEnd().Trim();
            check("child process: input, output and error streams work", output == "got hello" && error == "err" && child.WaitForExit(5000), output + " / " + error);
        }
    }

    /// <summary>On Windows (CI): the handle-list start itself, through cmd.exe, with all three streams.</summary>
    private static void RunWindows(Action<string, bool, string> check)
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT) return;
        var info = new ProcessStartInfo("cmd.exe", "/d /c \"findstr x & echo err 1>&2\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetTempPath(),
        };
        info.Environment["SKETCHPROMAX_TEST"] = "1";
        using var child = ChildProcess.Start(info);
        child.StandardInput!.WriteLine("hello x");
        child.StandardInput.Close();
        var output = child.StandardOutput!.ReadToEnd().Trim();
        var error = child.StandardError!.ReadToEnd().Trim();
        var ended = child.WaitForExit(10000);
        check("child process (Windows): started with the handle list, streams and exit code work",
            ChildProcess.LastStartIsolated && output == "hello x" && error == "err" && ended && child.Process.ExitCode == 0,
            $"{ChildProcess.LastStartIsolated} '{output}' '{error}' {ended}");
    }

    public static void RunSources(string root, Action<string, bool, string> check)
    {
        // No program started from the add-in with redirected streams outside ChildProcess (it would inherit Revit's files).
        var offenders = new List<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(root, "src", "SketchProMax"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) || file.EndsWith("ChildProcess.cs")) continue;
            var text = File.ReadAllText(file);
            if (!text.Contains("RedirectStandard")) continue;
            if (Regex.IsMatch(text, @"(?<!Child)Process\.Start\((info|start)\)|new Process\s*\{\s*StartInfo|\bprocess\.Start\(\)")) offenders.Add(Path.GetFileName(file));
        }
        check("bug 72: programs with redirected streams start through ChildProcess", offenders.Count == 0, string.Join(", ", offenders));
        var autopilot = File.ReadAllText(Path.Combine(root, "src", "SketchProMax", "Chat", "ChatPane.Autopilot.cs"));
        var freeze = File.ReadAllText(Path.Combine(root, "src", "SketchProMax", "Revit", "FreezeWatch.cs"));
        check("bug 72: the Autopilot watcher and a restarted Revit inherit none of Revit's files",
            autopilot.Contains("ChildProcess.Start(new System.Diagnostics.ProcessStartInfo(exe, $\"--watch-revit") && freeze.Contains("Chat.ChildProcess.Start(new ProcessStartInfo(exe"), "");
        var report = File.ReadAllText(Path.Combine(root, "src", "SketchProMax", "Bugs", "BugReport.cs"));
        check("bug 72: a bug report keeps its whole description", report.Contains("MaxDescription = 8000") && !report.Contains("Substring(0, 500)"), "");
        var save = File.ReadAllText(Path.Combine(root, "src", "SketchProMax", "Tools", "LinkedModelTools.cs"));
        check("speed: save_model saves only when an open model may link this one", save.Contains("MayBeLinked(app, doc)"), "");
    }
}
