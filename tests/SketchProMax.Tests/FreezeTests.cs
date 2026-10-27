using SketchProMax.Revit;

/// <summary>The Rescue window's reading of Revit's journal, its words, and the commands warned about before they run.</summary>
internal static class FreezeTests
{
    // The end of a real Revit 2024 journal that froze (27 Oct 2026), shortened: Sketch ProMax's own steps finished, then
    // Run systems analysis was clicked and its energy model left Revit not responding.
    private static readonly string[] Froze =
    [
        "Jrn.Data  _",
        "        \"Transaction Successful\"  , \"Sketch ProMax: Temp read view: unhide the CAD category\" ",
        "  'E 27-Oct-2026 12:37:55.612;   0:< ",
        "  Jrn.RibbonEvent \"TabActivated:Analyze\" ",
        "  'E 27-Oct-2026 12:37:56.141;   0:< ",
        "  Jrn.Command \"Ribbon\"  , \"Run systems analysis. , ID_RUN_SYSTEMS_ANALYSIS\" ",
        "  ' 1:< TaskDialog \"This analysis requires an energy analytical model. It can take a significant amount of time to create one and Revit will not be accessible during this time. What do you want to do?\"",
        "  'Id : TaskDialog_SystemsAnalysis_RequireEAM",
        "    'C 27-Oct-2026 12:37:56.280;   1:< Notify Active View. ",
        "    Jrn.Data  _",
        "            \"TaskDialogResult\"  , \"This analysis requires an energy analytical model.\" ,  _",
        "             \"Create the Energy Analytical Model and continue to run the analysis.\"  _",
        "            , \"1001\" ",
        "  ' 2:< ANTECEDENT:",
        "  'editor ArrowEditor",
        "  'inTransaction APITransactionIdentity_1:-1 Create Energy Analysis Model ",
        "  'C 27-Oct-2026 12:39:13.410;  DBG_INFO: Element has no panel faces for point cloud generation.",
    ];

    public static void Run(Action<string, bool, string> check)
    {
        var busy = FreezeCause.Read(Froze);
        check("freeze: the journal names the command that froze Revit", busy.What == "Run systems analysis" && busy.CommandId == "ID_RUN_SYSTEMS_ANALYSIS", busy.ToString());
        check("freeze: and the step it is in", busy.Step == "Create Energy Analysis Model", busy.Step ?? "");
        check("freeze: when it started", busy.Since == new DateTime(2026, 10, 27, 12, 37, 56), busy.Since?.ToString("s") ?? "");
        check("freeze: it is Revit's own, not Sketch ProMax's, and its dialog was answered", !busy.Ours && busy.Dialog is null, busy.ToString());
        var line = FreezeCause.Line(busy, TimeSpan.FromMinutes(6));
        check("freeze: the window's line", line == "Revit is busy with Run systems analysis (Create Energy Analysis Model). It hasn't responded for 6 min.", line);
        check("freeze: whose work", FreezeCause.Whose(busy, jobRunning: false) == "This is Revit's own command, not Sketch ProMax.", FreezeCause.Whose(busy, false));
        check("freeze: a running Sketch ProMax task is named as ours", FreezeCause.Whose(busy, jobRunning: true).StartsWith("This is a Sketch ProMax task"), "");
        check("freeze: the warning is offered after a minute, not before",
            FreezeCause.OfferWarning(busy, TimeSpan.FromSeconds(61)) && !FreezeCause.OfferWarning(busy, TimeSpan.FromSeconds(30)), "");

        // A Sketch ProMax step still in its transaction (no "Transaction Successful" for it yet).
        var ours = FreezeCause.Read(
        [
            "  'E 27-Oct-2026 12:37:00.000;   0:< ",
            "  Jrn.Command \"KeyboardShortcut\"  , \"Move selected objects or their copies , ID_EDIT_MOVE\" ",
            "  'inTransaction APITransactionIdentity_1:-1 Sketch ProMax: Temporary view to read text ",
        ]);
        check("freeze: a Sketch ProMax transaction in progress is ours", ours.Ours && FreezeCause.Line(ours, TimeSpan.FromSeconds(40)).StartsWith("Revit is busy with a Sketch ProMax task"), ours.ToString());
        check("freeze: no warning is offered for our own task", !FreezeCause.OfferWarning(ours, TimeSpan.FromMinutes(5)), "");

        // Sync with central still running: the SLOG bracket names it; Esc presses don't hide the command before it.
        var sync = FreezeCause.Read(
        [
            "  'E 27-Oct-2026 11:06:00.000;   0:< ",
            "  Jrn.Command \"Ribbon\"  , \"Save the active project back to the Central Model , ID_FILE_SAVE_TO_CENTRAL\" ",
            "  ' 0:< SLOG $e98617fb 2026-10-27 11:06:02.000 >STC ",
            "  ' 0:< SLOG $e98617fb 2026-10-27 11:06:03.000 >STC:Save ",
            "  Jrn.Command \"AccelKey\"  , \"Cancel the current operation , ID_CANCEL_EDITOR\" ",
        ]);
        check("freeze: a sync still running is named as synchronizing", sync.What == "Synchronizing with central", sync.ToString());
        check("freeze: never a warning for syncing", !FreezeCause.OfferWarning(sync, TimeSpan.FromMinutes(10)), "");
        check("freeze: the window waits 5 min for a sync, 20 s for a command",
            FreezeCause.OpenAfter(sync) == TimeSpan.FromMinutes(5) && FreezeCause.OpenAfter(busy) == TimeSpan.FromSeconds(20), "");

        var asking = FreezeCause.Read(
        [
            "  Jrn.Command \"Ribbon\"  , \" , ID_FILE_CADFORMAT_LINK\" ",
            "  ' 0:< TaskDialog \"The following nested links will not appear\"",
        ]);
        check("freeze: a command with no description gets its id in words, and an unanswered dialog is seen",
            asking.What == "File cadformat link" && asking.Dialog == "The following nested links will not appear", asking.ToString());
        var nothing = FreezeCause.Read(["random", "lines"]);
        check("freeze: a journal that says nothing gives a plain answer", nothing.What == "something of its own" && nothing.CommandId is null && FreezeCause.Whose(nothing, false) == "This is Revit's own work, not Sketch ProMax.", nothing.ToString());
        check("freeze: durations", FreezeCause.Duration(TimeSpan.FromSeconds(45)) == "45 s" && FreezeCause.Duration(TimeSpan.FromMinutes(72)) == "1 h 12 min", "");

        // The commands warned about: built in, learned with the longest freeze, and turned off.
        var file = Path.Combine(Path.GetTempPath(), "spm-freeze-" + Guid.NewGuid().ToString("N") + ".json");
        FreezeCause.FileForTest = file;
        try
        {
            check("warn: Run systems analysis is warned about from the start", FreezeCause.Load().Any(w => w.Id == "ID_RUN_SYSTEMS_ANALYSIS" && w.Seconds == 0), "");
            FreezeCause.Learn("ID_RUN_SYSTEMS_ANALYSIS", "Run systems analysis", TimeSpan.FromMinutes(25), new DateTime(2026, 10, 27));
            FreezeCause.Learn("ID_RUN_SYSTEMS_ANALYSIS", "Run systems analysis", TimeSpan.FromMinutes(2), new DateTime(2026, 10, 28));
            var learned = FreezeCause.Load().Single(w => w.Id == "ID_RUN_SYSTEMS_ANALYSIS");
            check("warn: the longest freeze is kept", learned.Seconds == 1500 && learned.On == "27 Oct", learned.ToString());
            var (main, content) = FreezeCause.Warning(learned);
            check("warn: the warning names the command and its freeze", main == "Run systems analysis can freeze Revit for a long time" && content.StartsWith("On 27 Oct it kept Revit frozen for 25 min.") && !content.Contains("Claude"), content);
            FreezeCause.Learn("ID_SOMETHING_SLOW", "Something slow", TimeSpan.FromSeconds(90), new DateTime(2026, 10, 27));
            check("warn: a command ticked in the Rescue window is added", FreezeCause.Load().Any(w => w.Id == "ID_SOMETHING_SLOW"), "");
            FreezeCause.Forget("ID_RUN_SYSTEMS_ANALYSIS");
            FreezeCause.Forget("ID_SOMETHING_SLOW");
            check("warn: Don't warn me again takes it off, built-in ones too", FreezeCause.Load().Count == 0, string.Join(",", FreezeCause.Load()));
        }
        finally
        {
            FreezeCause.FileForTest = null;
            try { File.Delete(file); } catch (Exception) { }
        }
    }
}
