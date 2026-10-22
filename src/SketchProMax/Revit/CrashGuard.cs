using System.Threading.Tasks;
using System.Windows.Threading;

namespace SketchProMax.Revit;

/// <summary>
/// The last line of defence against Sketch ProMax closing Revit. An exception from our code that reaches Revit's UI
/// dispatcher (a window's button, a BeginInvoke from a background thread, an async handler) is logged and marked handled
/// instead of going on to Revit; a task's exception nobody looked at is marked observed. An exception on a background
/// thread can't be stopped from ending the process, but it is written to the activity log and the field test's
/// breadcrumbs first, so the next start can say what closed Revit. Started with the core and stopped with it (a newer
/// core switched to while Revit runs hooks its own).
/// </summary>
internal static class CrashGuard
{
    private static Dispatcher? _ui;
    private static int _lastUiLog;

    public static void Start(Dispatcher? ui)
    {
        Stop();
        _ui = ui;
        if (ui is not null) ui.UnhandledException += OnUi;
        AppDomain.CurrentDomain.UnhandledException += OnFatal;
        TaskScheduler.UnobservedTaskException += OnUnobserved;
    }

    public static void Stop()
    {
        if (_ui is { } ui) ui.UnhandledException -= OnUi;
        _ui = null;
        AppDomain.CurrentDomain.UnhandledException -= OnFatal;
        TaskScheduler.UnobservedTaskException -= OnUnobserved;
    }

    private static void OnUi(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            if (!Bugs.BugReport.Ours(e.Exception)) return;
            e.Handled = true;
            // One line per few seconds: a handler failing on every redraw doesn't fill the log.
            if (unchecked(Environment.TickCount - _lastUiLog) < 5000) return;
            _lastUiLog = Environment.TickCount;
            Tools.ActivityLog.Note("ui_error_caught", Describe(e.Exception));
        }
        catch (Exception) { /* never fail here */ }
    }

    private static void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        try { if (Bugs.BugReport.Ours(e.Exception)) e.SetObserved(); }
        catch (Exception) { }
    }

    private static void OnFatal(object sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            if (e.ExceptionObject is not Exception ex || !Bugs.BugReport.Ours(ex)) return;
            var line = "Revit is closing on an unhandled exception from Sketch ProMax on a background thread: " + Describe(ex);
            Tools.ActivityLog.Note("revit_closing", line);
            Tools.FieldTest.NoteFatal(line);
        }
        catch (Exception) { }
    }

    private static string Describe(Exception ex)
    {
        var text = ex.ToString();
        return text.Length > 3000 ? text.Substring(0, 3000) + "…" : text;
    }
}
