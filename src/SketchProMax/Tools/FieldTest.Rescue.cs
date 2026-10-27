using System.IO;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

/// <summary>The probes for the Rescue window's parts that can be checked without freezing Revit (Revit/FreezeWatch, Revit/HeavyCommands).</summary>
internal static partial class FieldTest
{
    /// <summary>Revit's journal is found and read while Revit writes it, and its last command is named. Off Revit's thread.</summary>
    private static (string, string) RescueCause()
    {
        var file = FreezeWatch.Journal;
        if (string.IsNullOrEmpty(file)) return Fail("Revit didn't say which journal it is writing");
        var tail = FreezeWatch.JournalTail(file);
        if (tail.Count == 0) return Fail($"the journal {file} couldn't be read while Revit writes it");
        var busy = FreezeCause.Read(tail);
        return Pass($"read {tail.Count} lines of {Path.GetFileName(file)}; the last command there: {busy.What}" +
                    (busy.CommandId is { } id ? $" ({id})" : "") + $"; Revit now: {FreezeWatch.Read(App.MainWindow)}");
    }

    /// <summary>Run systems analysis has its warning: the command is bound by Sketch ProMax. Revit's thread.</summary>
    private static (string, string) RescueWarn(UIApplication app)
    {
        const string id = "ID_RUN_SYSTEMS_ANALYSIS";
        if (!FreezeCause.Load().Any(w => w.Id == id)) return Pass("the warning for Run systems analysis was turned off on this PC (Don't warn me again)");
        if (HeavyCommands.BoundIds.Contains(id)) return Pass($"warned before: {string.Join(", ", HeavyCommands.BoundIds)}");
        var command = RevitCommandId.LookupCommandId(id);
        return command is null ? Fail($"Revit {app.Application.VersionNumber} has no command {id}")
            : command.HasBinding ? Fail("another add-in has bound Run systems analysis, so Sketch ProMax can't warn before it")
            : Fail("Run systems analysis isn't bound: the warning won't show");
    }

    /// <summary>The tray icon is up and the working-or-stuck readout has figures (Revit's memory, processor). Off Revit's thread.</summary>
    private static (string, string) RescueActivity()
    {
        if (!UI.RescueTray.Running) return Fail("the Sketch ProMax tray icon isn't running, so the Rescue window can't be opened by hand");
        var a = FreezeWatch.Activity();
        return a.Memory > 0
            ? Pass($"tray icon up; Revit now: {FreezeCause.Verdict(a)} ({FreezeCause.Figures(a)})")
            : Fail("the readout has no figures: Revit's memory and processor time couldn't be read");
    }
}
