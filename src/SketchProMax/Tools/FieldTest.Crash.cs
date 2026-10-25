using System.Diagnostics;
using System.IO;
using Autodesk.Revit.UI;
using SketchProMax.Bridge;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

/// <summary>
/// The field test when Revit closes in the middle of it: the breadcrumbs each step leaves (<see cref="FieldTestJournal"/>),
/// the "(interrupted)" report written from them at the next start, telling the user once (with a bug report), and
/// Continue, which starts a new run after the step Revit closed during. The running-call file (RunningCall, logs\running-PID.json)
/// says which tool call Revit closed during for every caller; the journal adds what the field test needs on top: the rows
/// so far and where to continue. Both use the same "is that Revit still running" check, and the interrupted report quotes
/// the running-call line (with the call's arguments) when the start found one for the same Revit.
/// </summary>
internal static partial class FieldTest
{
    private const string ContinueLabel = "Continue the field test after that step";

    // ---- Breadcrumbs while a run goes --------------------------------------------------------------------------------------

    /// <summary>The breadcrumbs of the run in progress, for <see cref="NoteFatal"/>.</summary>
    private static volatile FieldTestJournal.Writer? _journal;

    /// <summary>Revit is about to close on an unhandled exception (CrashGuard): the reason goes into the running field test's breadcrumbs.</summary>
    internal static void NoteFatal(string line)
    {
        try { _journal?.Note("fatal", line); }
        catch (Exception) { }
    }

    private static FieldTestJournal.Writer? OpenJournal(Options o)
    {
        try
        {
            var header = new FieldTestJournal.Header(Process.GetCurrentProcess().Id, App.RevitYear, Update.Updater.CurrentText, DateTime.Now, o.Title,
                o.ReportFolder, null, o.Families?.ToList(), o.SkipProven, o.IncludeHeavy, o.Workshared, o.ChatRoundTrip, o.ToolCapSeconds, o.Resume is not null);
            var writer = new FieldTestJournal.Writer(header);
            return _journal = writer.Path is null ? null : writer;
        }
        catch (Exception) { return null; /* the run goes on without breadcrumbs */ }
    }

    private static void CloseJournal(State s, Options o)
    {
        try
        {
            if (s.Journal is not { } journal) return;
            _journal = null;
            journal.Complete();
            var year = s.Result.RevitVersion.Length > 0 ? s.Result.RevitVersion : App.RevitYear;
            FieldTestJournal.Forget(year, journal.Ended);
            // A continued run, or a whole one, takes the place of the run Revit closed during.
            if (o.Resume is not null || o.Families is null) FieldTestJournal.ClearCrashed(year);
        }
        catch (Exception) { /* best effort */ }
    }

    private static FieldTestJournal.Entry Crumb(Row r) => new(r.Family, r.Tool, r.Status, r.Ms, r.Message, null);

    /// <summary>Before a step: on disk at once, so a Revit that closes during it leaves its name behind.</summary>
    private static void Begin(State s, string family, string step) =>
        s.Journal?.Begin(family, step, ++s.StepIndex, s.Result.Rows.Count, i => Crumb(s.Result.Rows[i]));

    private static void End(State s, string step, string status) =>
        s.Journal?.End(step, status, s.Result.Rows.Count, i => Crumb(s.Result.Rows[i]));

    /// <summary>A building or closing step, with its breadcrumbs (never skipped by Continue: the test project is needed).</summary>
    private static async Task Setup(State s, string what, Func<Task> body)
    {
        var step = FieldTestJournal.SetupPrefix + what;
        Begin(s, "setup", step);
        var status = "fail";
        try
        {
            await body().ConfigureAwait(false);
            status = "done";
        }
        finally { End(s, step, status); }
    }

    /// <summary>
    /// Continuing after a crash: a step done before Revit closed gets its earlier rows (not run again); the step that closed
    /// Revit, or one that did in an earlier run, gets a "crash" row (a failure) and is not run. False: run it.
    /// </summary>
    private static bool FromEarlier(State s, string family, string step)
    {
        if (s.O.Resume is not { } resume || !resume.Skips(step)) return false;
        Begin(s, family, step);
        if (resume.Carried.TryGetValue(step, out var rows))
        {
            foreach (var r in rows) s.Result.Rows.Add(new(r.Family, r.Tool, r.Status, r.Ms, resume.CarriedMessage(r.Message)));
            End(s, step, "carried");
        }
        else
        {
            s.Result.Rows.Add(new(family, step, "crash", 0, resume.CrashedMessage(step)));
            End(s, step, "crash");
        }
        return true;
    }

    // ---- After Revit closed ------------------------------------------------------------------------------------------------

    private static readonly object CrashGate = new();

    /// <summary>
    /// Journals left by a Revit of this year that is gone: each becomes an "(interrupted)" report next to where its report
    /// was going, its crashed step is remembered, and a line goes to the activity log. Files only; any thread; never throws.
    /// </summary>
    internal static void ReportCrashes()
    {
        try
        {
            lock (CrashGate)
            {
                var year = App.RevitYear;
                foreach (var c in FieldTestJournal.Leftovers(year, Process.GetCurrentProcess().Id, RunningCall.IsRevitRunning))
                {
                    var report = WriteInterrupted(c);
                    if (c.Open is { } open)
                        FieldTestJournal.RememberCrash(new(year, open.Step, open.Family, c.Header.Started.ToString("yyyy-MM-dd HH:mm"), c.Header.Addin));
                    FieldTestJournal.MarkReported(c, report);
                    ActivityLog.Note("field_test_interrupted", FieldTestJournal.Tell(c.Open?.Step ?? "an unknown step", Title(c.Open?.Family ?? "other"),
                        Path.GetDirectoryName(report) ?? c.Header.ReportFolder ?? DefaultReportFolder));
                }
            }
        }
        catch (Exception) { /* the next start tries again */ }
    }

    /// <summary>The crash in this Revit year the user hasn't been told about, or null.</summary>
    private static FieldTestJournal.Contents? Untold()
    {
        try { return FieldTestJournal.LastCrash(App.RevitYear) is { Shown: false } c ? c : null; }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// At the core's start, in the background after it settled (App.BackgroundStartAsync): report a crashed run, then tell
    /// the user from Revit's external event (a valid API context, never inside Idling), unless something runs meanwhile;
    /// then the next click on Field test tells them.
    /// </summary>
    internal static void AfterStart()
    {
        ReportCrashes();
        if (Untold() is null) return;
        try
        {
            _ = App.Dispatcher.InvokeFromEventAsync(app =>
            {
                if (Jobs.Running is null && !SelfTest.IsRunning && Untold() is { } c) ShowCrash(app, c);
                return true;
            }).ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
        }
        catch (Exception) { /* told at the next click on Field test */ }
    }

    private static string StepOf(FieldTestJournal.Contents c) => c.Open?.Step ?? "an unknown step";

    private static string FamilyTitle(FieldTestJournal.Contents c) => Title(c.Open?.Family ?? "other");

    private static string ContinueText(FieldTestJournal.Contents c)
    {
        var resume = FieldTestJournal.ResumeFrom(c, FieldTestJournal.CrashesFor(c.Header.Revit));
        return $"The {resume.Carried.Count} steps that ran before Revit closed are carried into the new report and not run again; " +
               $"{StepOf(c)} is not run again either (it counts as a failure){(resume.Crashed.Count > 1 ? $", nor are {resume.Crashed.Count - 1} other step(s) that closed Revit before" : "")}. " +
               "The test project is built again first.";
    }

    /// <summary>The options of the run Revit closed during, continuing after the step it closed during.</summary>
    internal static Options Continuing(FieldTestJournal.Contents c, string year)
    {
        var h = c.Header;
        return new Options
        {
            Families = h.Families is { Count: > 0 } f ? new HashSet<string>(f) : null,
            SkipProven = h.SkipProven, IncludeHeavy = h.IncludeHeavy, Workshared = h.Workshared, ChatRoundTrip = h.ChatRoundTrip,
            ReportFolder = h.ReportFolder, ToolCapSeconds = h.ToolCapSeconds, Title = h.Title,
            Resume = FieldTestJournal.ResumeFrom(c, FieldTestJournal.CrashesFor(year)),
        };
    }

    /// <summary>Tells the user once that Revit closed during the field test, saves a bug report, and offers what to do next.</summary>
    private static Autodesk.Revit.UI.Result ShowCrash(UIApplication app, FieldTestJournal.Contents c)
    {
        FieldTestJournal.MarkShown(c);
        var folder = c.Report is { Length: > 0 } r ? Path.GetDirectoryName(r)! : c.Header.ReportFolder ?? DefaultReportFolder;
        var tell = FieldTestJournal.Tell(StepOf(c), FamilyTitle(c), folder);
        string? bug = null;
        try
        {
            bug = Bugs.BugReport.Create(app, $"Revit closed during the field test, while running {StepOf(c)} ({FamilyTitle(c)})",
                (null, "Revit had closed; there is no screenshot of that moment"), "the field test, after Revit closed", CrashDetails(c));
        }
        catch (Exception) { /* the report still says it */ }
        var year = app.Application.VersionNumber;
        var baseline = FieldTestProven.Load().For(year);
        var (steps, proven) = StepCount(true, year);
        var dialog = new TaskDialog("Sketch ProMax field test")
        {
            MainInstruction = "Revit closed during the field test",
            MainContent = tell,
            FooterText = bug is null ? "" : $"A bug report was saved too: {Path.GetFileName(bug)} (Report bug's folder).",
            CommonButtons = TaskDialogCommonButtons.Close,
        };
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Open the report folder", c.Report ?? folder);
        if (FieldTestJournal.CanContinue(c)) dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, ContinueLabel, ContinueText(c));
        if (baseline is not null && proven > 0)
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "Run only what's new or changed", $"About {steps - proven} steps, {FieldTestProven.Estimate(steps - proven)}.");
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink4, "Run everything", $"About {steps} steps, {FieldTestProven.Estimate(steps)}. Every step runs, the one Revit closed during too.");
        var choice = dialog.Show();
        switch (choice)
        {
            case TaskDialogResult.CommandLink1:
                try
                {
                    var open = c.Report is { Length: > 0 } report && File.Exists(report) ? $"/select,\"{report}\"" : $"\"{folder}\"";
                    Process.Start(new ProcessStartInfo("explorer.exe", open) { UseShellExecute = true })?.Dispose();
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    TaskDialog.Show("Sketch ProMax field test", $"Couldn't open {folder}: {ex.Message}");
                }
                return Autodesk.Revit.UI.Result.Succeeded;
            case TaskDialogResult.CommandLink2:
                return StartRibbonRun(Continuing(c, year));
            case TaskDialogResult.CommandLink3:
                return StartRibbonRun(new Options { SkipProven = true });
            case TaskDialogResult.CommandLink4:
                return StartRibbonRun(new Options { SkipProven = false });
            default:
                return Autodesk.Revit.UI.Result.Cancelled;
        }
    }

    /// <summary>For the bug report: the crashed step, the running call the start recovered, and the journal's last lines.</summary>
    private static string CrashDetails(FieldTestJournal.Contents c)
    {
        var lines = new List<string>
        {
            $"Field test of {c.Header.Started:yyyy-MM-dd HH:mm}, Revit {c.Header.Revit}, Sketch ProMax {c.Header.Addin}.",
            $"Running when Revit closed: {StepOf(c)} ({FamilyTitle(c)}), step {c.Open?.Index}, started {c.Open?.At:HH:mm:ss}.",
            $"Rows before it: {c.Rows.Count} ({c.Rows.Count(r => IsFail(r.Status))} failed).",
        };
        if (RunningCall.Recovered.TryGetValue(c.Header.Pid, out var call)) lines.Add(call);
        lines.Add("Last rows:");
        lines.AddRange(c.Rows.Skip(Math.Max(0, c.Rows.Count - 10)).Select(r => $"  {r.Tool}: {r.Status} {Short(r.Message, 200)}"));
        return string.Join("\n", lines);
    }

    /// <summary>The "(interrupted)" report of a run Revit closed during: what ran, and the crash row. Returns its path.</summary>
    private static string? WriteInterrupted(FieldTestJournal.Contents c)
    {
        var h = c.Header;
        var rows = FieldTestJournal.InterruptedRows(c);
        var last = c.Open?.At ?? h.Started;
        var r = new RunReport
        {
            Title = h.Title + " (interrupted)", RevitVersion = h.Revit, AddinVersion = h.Addin, Started = h.Started,
            Took = last > h.Started ? last - h.Started : TimeSpan.Zero, Interrupted = true,
            Families = h.Families is { Count: > 0 } f ? new HashSet<string>(f) : null,
            StoppedBecause = $"Revit closed while {StepOf(c)} ({FamilyTitle(c)}) ran",
            Rows = rows.Select(x => new Row(x.Family, x.Tool, x.Status, x.Ms, x.Message)).ToList(),
        };
        if (RunningCall.Recovered.TryGetValue(h.Pid, out var call)) r.Sample.Add(call);
        if (c.Project is { Length: > 0 } project && Directory.Exists(project))
            r.Sample.Add($"The test project's files are still in {project}; nothing in them is needed, delete the folder.");
        if (h.Continued) r.Sample.Add("This run was itself continuing an earlier one Revit closed during.");
        WriteReport(r, h.ReportFolder);
        return r.ReportPath;
    }
}
