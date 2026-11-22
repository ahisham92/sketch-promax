using W = SketchProMax.Revit.LifelineWatchMath;

/// <summary>Lifeline's warnings and saving the local copy by itself (Revit/LifelineWatchMath), and the wiring.</summary>
internal static class LifelineWatchTests
{
    public static void Run(string repoRoot, Action<string, bool, string> check)
    {
        string Src(params string[] parts) => File.ReadAllText(Path.Combine([repoRoot, "src", "SketchProMax", .. parts])).Replace("\r\n", "\n");
        static TimeSpan Min(double m) => TimeSpan.FromMinutes(m);

        // The signs.
        var fine = new W.Signs(6000, 12000, 32000, 80000, "C:", 2500);
        check("lifeline watch: a healthy PC gets no hint", W.Judge(fine).Count == 0, "");
        check("lifeline watch: little free memory is a hint with the numbers", W.Judge(fine with { FreeMemoryMb = 800 }) is [{ Key: "memory" } m] && m.Detail.Contains("Save now"), "");
        check("lifeline watch: under 6% of memory free is a hint too", W.Judge(fine with { FreeMemoryMb = 3000, TotalMemoryMb = 64000 }) is [{ Key: "memory" }], "");
        check("lifeline watch: an almost full disk is named", W.Judge(fine with { FreeDiskMb = 900, Disk = "D:" }) is [{ Key: "disk" } d] && d.Main.Contains("D:"), "");
        check("lifeline watch: window handles near Windows' limit", W.Judge(fine with { Handles = W.HandleWarn }) is [{ Key: "handles" }] && W.Judge(fine with { Handles = W.HandleWarn - 1 }).Count == 0, "");
        check("lifeline watch: what couldn't be read is no hint", W.Judge(new W.Signs(-1, -1, -1, -1, "", 0)).Count == 0, "");
        check("lifeline watch: several signs, several hints", W.Judge(fine with { FreeMemoryMb = 100, FreeDiskMb = 10, Handles = 9900 }).Select(w => w.Key).SequenceEqual(["memory", "disk", "handles"]), "");

        // The server.
        check("lifeline watch: a server or mapped drive central is watched", W.IsServerPath(@"\\server\share\Port.rvt", @"C:\Work\Port_me.rvt") && W.IsServerPath(@"P:\Models\Port.rvt", @"C:\Work\Port_me.rvt"), "");
        check("lifeline watch: cloud models, Revit Server and a file that is its own central aren't", !W.IsServerPath("RSN://srv/Port.rvt", "x") && !W.IsServerPath("Autodesk Docs://P/Port.rvt", "x")
            && !W.IsServerPath(@"C:\Work\Port.rvt", @"c:\work\port.rvt") && !W.IsServerPath("", "x") && !W.IsServerPath(null, "x"), "");
        var one = W.Server(0, false, false);
        var two = W.Server(one.Fails, one.Lost, false);
        var three = W.Server(two.Fails, two.Lost, false);
        var back = W.Server(three.Fails, three.Lost, true);
        check("lifeline watch: one slow answer isn't a lost server", one is { Lost: false, Say: null }, "");
        check("lifeline watch: lost after two looks, said once", two is { Lost: true, Say: "lost" } && three is { Lost: true, Say: null }, "");
        check("lifeline watch: back at the first answer, said once", back is { Lost: false, Say: "back", Fails: 0 } && W.Server(0, false, true).Say is null, "");
        check("lifeline watch: the server hint says save local, don't synchronize", W.ServerLost("Port").Detail.Contains("Save your local") && W.ServerLost("Port").Detail.Contains("don't synchronize"), "");

        // A lot unsaved.
        check("lifeline watch: many changes are worth a hint", W.Unsaved("Port", Min(10), W.ManyChanges, false) is { Key: "unsaved" }, "");
        check("lifeline watch: changes left an hour are worth a hint", W.Unsaved("Port", Min(61), 3, false) is { } u && u.Detail.Contains("61 min"), "");
        check("lifeline watch: nothing changed, or little and lately, is not", W.Unsaved("Port", Min(600), 0, false) is null && W.Unsaved("Port", Min(30), 40, false) is null, "");
        check("lifeline watch: no unsaved hint while it is saved by itself", W.Unsaved("Port", Min(61), 900, true) is null, "");
        check("lifeline watch: the same hint again only after a quarter of an hour", W.Due(1000, null) && !W.Due(1000 + W.RepeatMs - 1, 1000) && W.Due(1000 + W.RepeatMs, 1000), "");

        // Saving by itself.
        check("lifeline watch: minutes are kept between 5 and 240, 20 when unset", W.ClampMinutes(0) == 20 && W.ClampMinutes(2) == 5 && W.ClampMinutes(40) == 40 && W.ClampMinutes(999) == 240, "");
        check("lifeline watch: a save is due after the user's minutes, at a quiet moment", W.SaveDue(true, Min(40), 40, TimeSpan.FromSeconds(6), false, Min(999)) && !W.SaveDue(true, Min(39), 40, TimeSpan.FromSeconds(6), false, Min(999)), "");
        check("lifeline watch: no save with nothing changed", !W.SaveDue(false, Min(90), 20, Min(5), false, Min(999)), "");
        check("lifeline watch: no save while a tool is open or the user is working", !W.SaveDue(true, Min(90), 20, Min(5), true, Min(999)) && !W.SaveDue(true, Min(90), 20, TimeSpan.FromSeconds(1), false, Min(999)), "");
        check("lifeline watch: a save that failed waits five minutes", !W.SaveDue(true, Min(90), 20, Min(5), false, Min(4)) && W.SaveDue(true, Min(90), 20, Min(5), false, Min(5)), "");
        check("lifeline watch: the hints say it isn't synchronized", W.Saving("Port").Detail.Contains("Nothing is synchronized") && W.Saved("Port", TimeSpan.FromSeconds(12), 40).Detail.Contains("not synchronized")
            && W.Saved("Port", TimeSpan.FromSeconds(12), 40).Detail.Contains("40 min"), "");
        check("lifeline watch: the window's line says on or off", W.About(true, 40).StartsWith("On: every 40 min") && W.About(false, 40).StartsWith("Off") && W.About(true, 40).Contains("never synchronizes"), "");

        // Synchronizing by itself.
        var quiet = TimeSpan.FromSeconds(6);
        check("lifeline watch: synchronize minutes are kept between 10 and 480, 60 when unset", W.ClampSyncMinutes(0) == 60 && W.ClampSyncMinutes(3) == 10 && W.ClampSyncMinutes(90) == 90 && W.ClampSyncMinutes(9999) == 480, "");
        check("lifeline watch: a synchronize is due after the user's minutes, at a quiet moment", W.SyncDue(true, Min(90), 90, quiet, false, false, true, Min(999)) && !W.SyncDue(true, Min(89), 90, quiet, false, false, true, Min(999)), "");
        check("lifeline watch: no synchronize with nothing changed", !W.SyncDue(false, Min(900), 60, quiet, false, false, true, Min(999)), "");
        check("lifeline watch: no synchronize while the server doesn't answer", !W.SyncDue(true, Min(900), 60, quiet, false, false, false, Min(999)), "");
        check("lifeline watch: no synchronize while a tool is open, the user is working or a chat step is running", !W.SyncDue(true, Min(900), 60, quiet, true, false, true, Min(999))
            && !W.SyncDue(true, Min(900), 60, TimeSpan.FromSeconds(1), false, false, true, Min(999)) && !W.SyncDue(true, Min(900), 60, quiet, false, true, true, Min(999)), "");
        check("lifeline watch: a synchronize that failed waits ten minutes", !W.SyncDue(true, Min(900), 60, quiet, false, false, true, Min(9)) && W.SyncDue(true, Min(900), 60, quiet, false, false, true, Min(10)), "");
        check("lifeline watch: the synchronize hints say what happened", W.Synced("Port", TimeSpan.FromSeconds(40), 90).Detail.Contains("90 min") && W.SyncFailed("Port", "Busy.").Detail.Contains("stay in your local")
            && W.AboutSync(true, 90).StartsWith("On: every 90 min") && W.AboutSync(false, 90).StartsWith("Off"), "");

        // The wiring.
        check("lifeline watch: synchronizing by itself is off unless turned on", Src("Settings", "AgentSettings.cs").Contains("public bool LifelineAutoSync { get; set; }\n")
            && Src("Settings", "AgentSettings.cs").Contains("public int LifelineAutoSyncMinutes { get; set; } = 60;"), "");
        check("lifeline watch: it synchronizes the way every Sketch ProMax synchronize does, at an idle moment", Src("Revit", "LifelineWatch.cs").Contains("AutoSync.SyncNow(doc,")
            && Src("Revit", "LifelineWatch.cs").Contains("AutoSync.Working") && Src("Revit", "LifelineWatch.cs").Contains("AutoSync.Shared(doc)"), "");
        var watch = Src("Revit", "LifelineWatch.cs");
        var settings = Src("Settings", "AgentSettings.cs");
        check("lifeline watch: saving by itself is off unless turned on; warnings are on", settings.Contains("public bool LifelineAutoSave { get; set; }\n") && settings.Contains("public bool LifelineWarnings { get; set; } = true;")
            && settings.Contains("public int LifelineAutoSaveMinutes { get; set; } = 20;"), "");
        check("lifeline watch: the save saves the local copy and doesn't synchronize", watch.Contains("doc.Save();") && !watch.Contains("SynchronizeWithCentral(") && !watch.Contains("SaveAs("), "");
        check("lifeline watch: it only says, in the corner hint, and never asks", watch.Contains("UI.CommandHint.Show(") && !watch.Contains("TaskDialog") && !watch.Contains("MessageBox") && !watch.Contains(".Cancel"), "");
        check("lifeline watch: the save waits for an idle moment with no tool open, and not the central itself", watch.Contains("RevitDispatcher.Idle += OnIdle") && watch.Contains("SwitchGate.ContextualTab() != false")
            && watch.Contains("doc.IsModifiable") && watch.Contains("doc.IsReadOnly"), "");
        check("lifeline watch: started and stopped with Lifeline", Src("App.cs").Contains("Revit.LifelineWatch.Start(app)") && Src("App.cs").Contains("Revit.LifelineWatch.Stop()"), "");
        check("lifeline watch: set in the Lifeline window, kept at once", Src("UI", "RecoverWindow.cs").Contains("WatchSection()") && Src("UI", "RecoverWindow.cs").Contains("LifelineWatch.ReadSettings()"), "");
        // Bug 134: Revit gave no Idling for 1.5 hours; the update and the chat tab must not wait for it.
        var now = Src("Update", "SwitchNow.cs");
        check("update: handed over, it is also switched from the external event", Src("Update", "Updater.cs").Contains("SwitchNow.Watch(core)") && now.Contains("InvokeFromEventAsync(TrySwitch)"), "");
        check("update: the external-event switch checks what the loader checks", now.Contains("Bridge.Jobs.Running is not null || Tools.SelfTest.IsRunning || Chat.ChatPane.IsBusy || Revit.SwitchGate.Wait()"), "");
        var loader = File.ReadAllText(Path.Combine(repoRoot, "src", "SketchProMax.Loader", "LoaderApp.cs"));
        check("update: the loader still has the Pending and Swap the switch calls", loader.Contains("private static Candidate? Pending()") && loader.Contains("private static void Swap(UIApplication app, Candidate target)"), "");
        check("chat tab: filled after a few seconds without Revit's idle moment", Src("UI", "LazyPane.cs").Contains("FallbackSeconds") && Src("UI", "LazyPane.cs").Contains("Fill(); }, ui)"), "");
    }
}
