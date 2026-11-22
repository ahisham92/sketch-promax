using System.Reflection;
using Autodesk.Revit.UI;

namespace SketchProMax.Update;

/// <summary>
/// Gets a downloaded update switched in when Revit stops giving add-ins Idling (bug 134). The loader switches cores at
/// Idling, and on 22 Nov 2026 Revit raised no Idling for over 90 minutes, even with the mouse moving: 0.35.114 sat at
/// "Applying" with every job paused for it, and each Continue was paused again two seconds later. Revit still ran
/// external events, so this core asks for one: from 20 s after the update was handed over, every 10 s while it still
/// waits and nothing of ours runs, it queues <see cref="TrySwitch"/> on the external event. That runs only when no Revit
/// command is active, checks the same things the loader checks (no job, self-test or chat turn, nobody working in
/// Revit, no Modify tab), and then calls the loader's own Pending and Swap. A loader that switches at Idling first wins:
/// the hand-over is cleared and this stops.
/// </summary>
internal static class SwitchNow
{
    internal const int FirstWaitMs = 20000, EveryMs = 10000;
    private const string PendingKey = "SketchProMax.Loader.Pending";
    private const BindingFlags Any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static int _watching;

    /// <summary>Called once the update is handed to the loader. Any thread; a second call while watching does nothing.</summary>
    public static void Watch(string core)
    {
        if (Interlocked.Exchange(ref _watching, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(FirstWaitMs).ConfigureAwait(false);
                while (Waiting(core))
                {
                    if (Bridge.Jobs.Running is null && !Tools.SelfTest.IsRunning && !App.Dispatcher.HasWork)
                    {
                        try { await App.Dispatcher.InvokeFromEventAsync(TrySwitch).ConfigureAwait(false); }
                        catch (Exception) { return; } // this core stopped: the switch happened, or Revit is closing
                    }
                    await Task.Delay(EveryMs).ConfigureAwait(false);
                }
            }
            catch (Exception) { /* best effort: the loader still switches at Idling, and Revit's next start uses the update */ }
            finally { Interlocked.Exchange(ref _watching, 0); }
        });
    }

    private static bool Waiting(string core) => AppDomain.CurrentDomain.GetData(PendingKey) is string path &&
                                                string.Equals(path, core, StringComparison.OrdinalIgnoreCase);

    /// <summary>On the external event (no Revit command active). True when the loader switched to the update.</summary>
    internal static bool TrySwitch(UIApplication app)
    {
        if (Bridge.Jobs.Running is not null || Tools.SelfTest.IsRunning || Chat.ChatPane.IsBusy || Revit.SwitchGate.Wait()) return false;
        var host = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "SketchProMax.Loader")?.GetType("SketchProMax.Loader.Host");
        var pending = host?.GetMethod("Pending", Any, null, Type.EmptyTypes, null);
        var swap = host?.GetMethods(Any).FirstOrDefault(m => m.Name == "Swap" && m.GetParameters().Length == 2);
        if (pending is null || swap is null) return false;
        if (pending.Invoke(null, null) is not { } target) return false;
        Tools.ActivityLog.Note("update_switch", "Revit gave no idle moment: switching to the update from the external event");
        swap.Invoke(null, [app, target]);
        return true;
    }
}
