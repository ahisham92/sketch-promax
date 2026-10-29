using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace SketchProMax.Revit;

/// <summary>
/// While an autopilot works with nobody at the computer (Chat.Autopilot.Unattended), Revit's dialogs are answered as
/// they appear, so nothing waits on a click: Cancel where there is one (the step is undone and the worker finds another
/// way), else No, else OK. Each answer goes in the autopilot's report. Outside autopilot nothing is touched.
/// </summary>
internal static class UnattendedDialogs
{
    public static void Watch(UIApplication app)
    {
        try { app.DialogBoxShowing += OnDialog; } catch (Exception) { }
    }

    private static void OnDialog(object? sender, DialogBoxShowingEventArgs e)
    {
        if (!Chat.Autopilot.Unattended) return;
        try
        {
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
