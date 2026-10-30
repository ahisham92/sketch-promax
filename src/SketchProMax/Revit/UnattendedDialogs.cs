using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace SketchProMax.Revit;

/// <summary>
/// While an autopilot works with nobody at the computer (Chat.Autopilot.Unattended), the dialogs Revit shows during
/// Sketch ProMax's own work on Revit's thread are answered as they appear, so nothing waits on a click: Cancel where
/// there is one (the step is undone and the worker finds another way), else No, else OK. Each answer goes in the
/// autopilot's report. A dialog of the user's own command (Synchronize with Central, Link Revit, closing a view or Revit)
/// is never touched: on 31 Oct 2026 a run that carried on after Revit was opened again cancelled every one of them.
/// </summary>
internal static class UnattendedDialogs
{
    public static void Watch(UIApplication app)
    {
        try { app.DialogBoxShowing += OnDialog; } catch (Exception) { }
    }

    /// <summary>This core stops: a newer one watches instead (an older handler left behind would keep answering).</summary>
    public static void Unwatch(UIApplication app)
    {
        try { app.DialogBoxShowing -= OnDialog; } catch (Exception) { }
    }

    private static void OnDialog(object? sender, DialogBoxShowingEventArgs e)
    {
        try
        {
            // Only while our work runs on Revit's thread: the user's own commands' dialogs show as usual.
            if (!Chat.Autopilot.Unattended || App.Dispatcher is not { IsRunning: true }) return;
            var type = e is MessageBoxShowingEventArgs box ? box.DialogType : -1;
            var said = e switch
            {
                TaskDialogShowingEventArgs task => task.Message,
                MessageBoxShowingEventArgs message => message.Message,
                _ => "",
            };
            var result = Chat.Autopilot.SafeResult(type);
            if (!e.OverrideResult(result)) return;
            var what = said.Trim().Length > 0 ? Chat.JobBoard.OneLine(said, 160) : e.DialogId;
            Chat.Autopilot.Log(Chat.AutopilotEntry.Popup, $"Revit asked \"{what}\": answered {Chat.Autopilot.ResultName(result)}.");
        }
        catch (Exception) { /* a dialog must never fail because of us */ }
    }
}
