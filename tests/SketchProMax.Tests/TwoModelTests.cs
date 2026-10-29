using SketchProMax.Chat;

/// <summary>Two models in two Revit windows: the shared autopilot state, each model backed up once, jobs tied to a model.</summary>
internal static class TwoModelTests
{
    public static void Run(Action<string, bool, string> check)
    {
        var folder = Path.Combine(Path.GetTempPath(), "spm-share-" + Guid.NewGuid().ToString("N"));
        var (oldFolder, oldMe, oldAlive) = (AutopilotShare.Folder, AutopilotShare.Me, AutopilotShare.IsAlive);
        try
        {
            AutopilotShare.Folder = folder;
            AutopilotShare.IsAlive = _ => true;
            var now = new DateTime(2026, 10, 29, 23, 0, 0, DateTimeKind.Utc);
            // The working model's window (100) runs the autopilot; the documentation model's window (200) reaches it.
            AutopilotShare.Me = 100;
            AutopilotShare.Begin("chat1:1", @"C:\P\Tower-Model.rvt", now);
            check("two models: the owner's own run isn't 'remote' to it", AutopilotShare.Remote(now) is null && AutopilotShare.Any(now) == "chat1:1", "");
            AutopilotShare.Me = 200;
            check("two models: the other window sees the run", AutopilotShare.Remote(now.AddSeconds(10)) == "chat1:1", "");
            check("two models: the model backed up at approval isn't backed up again", !AutopilotShare.Claim(@"C:\P\Tower-Model.rvt", now), "");
            check("two models: the documentation model is backed up before its first change, once", AutopilotShare.Claim(@"C:\P\Tower-Docs.rvt", now)
                && !AutopilotShare.Claim(@"c:\p\tower-docs.RVT", now), "");
            AutopilotShare.AppendLog("chat1:1", AutopilotEntry.Popup, "[Tower-Docs] Revit asked \"Link not found\": answered Cancel", now);
            AutopilotShare.Me = 100;
            var lines = AutopilotShare.TakeLog();
            check("two models: what the other window answered reaches the owner's report", lines.Count == 1 && lines[0].Kind == AutopilotEntry.Popup && lines[0].Text.StartsWith("[Tower-Docs]"), "");
            check("two models: each line is taken once", AutopilotShare.TakeLog().Count == 0, "");
            AutopilotShare.Beat("chat1:1", now.AddMinutes(5));
            AutopilotShare.Me = 200;
            check("two models: a run kept alive stays on", AutopilotShare.Remote(now.AddMinutes(6)) == "chat1:1", "");
            check("two models: a run whose window stopped writing is over", AutopilotShare.Remote(now.AddMinutes(9)) is null, "");
            AutopilotShare.IsAlive = _ => false;
            check("two models: a run whose Revit is gone is over", AutopilotShare.Remote(now.AddMinutes(5).AddSeconds(30)) is null, "");
            AutopilotShare.IsAlive = _ => true;
            AutopilotShare.Me = 100;
            AutopilotShare.End("chat1:1");
            AutopilotShare.Me = 200;
            check("two models: once the run ends the other window asks again", AutopilotShare.Remote(now.AddMinutes(5).AddSeconds(40)) is null && !AutopilotShare.Claim(@"C:\P\Other.rvt", now), "");
            check("two models: Unattended needs a run", !Autopilot.Unattended, "");
        }
        finally
        {
            (AutopilotShare.Folder, AutopilotShare.Me, AutopilotShare.IsAlive) = (oldFolder, oldMe, oldAlive);
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }

        // An update during a run: it carries on once the version it waited for (or a newer one) runs.
        var waiting = new AutopilotRun { Updating = "0.35.43" };
        check("update: not in while the old version runs", !Autopilot.UpdateIn(waiting, new Version(0, 35, 42)), "");
        check("update: in once the new version runs", Autopilot.UpdateIn(waiting, new Version(0, 35, 43)) && Autopilot.UpdateIn(waiting, new Version(0, 35, 44, 0)), "");
        check("update: nothing waiting, nothing to carry on", !Autopilot.UpdateIn(new AutopilotRun(), new Version(9, 9, 9)), "");
        check("update: a hold is let go after a while", Autopilot.UpdateHoldMax == TimeSpan.FromMinutes(30), "");

        // Revit freezes or crashes during a run.
        var m = TimeSpan.FromMinutes(1);
        check("freeze: stuck 10 minutes doing nothing is restarted", Autopilot.RestartAfterHang(true, true, 10 * m, idle: true, longWork: false), "");
        check("freeze: not before 10 minutes", !Autopilot.RestartAfterHang(true, true, 9 * m, true, false), "");
        check("freeze: busy (processor or files moving) gets 45 minutes", !Autopilot.RestartAfterHang(true, true, 30 * m, false, false)
            && Autopilot.RestartAfterHang(true, true, 45 * m, false, false), "");
        check("freeze: a sync or opening gets 45 minutes even when quiet", !Autopilot.RestartAfterHang(true, true, 20 * m, true, longWork: true), "");
        check("freeze: never without an autopilot", !Autopilot.RestartAfterHang(false, true, 120 * m, true, false), "");
        folder = Path.Combine(Path.GetTempPath(), "spm-share-" + Guid.NewGuid().ToString("N"));
        try
        {
            AutopilotShare.Folder = folder;
            var alive = new HashSet<int> { 100 };
            AutopilotShare.IsAlive = alive.Contains;
            var now = new DateTime(2026, 10, 30, 1, 0, 0, DateTimeKind.Utc);
            AutopilotShare.Me = 100;
            AutopilotShare.Begin("chat1:1", @"C:\P\Tower-Model.rvt", now);
            // A crash: Revit 100 is gone; the watcher starts it again once, never twice.
            alive.Remove(100);
            AutopilotShare.Me = 999; // the watcher
            check("crash: the watcher starts Revit again", AutopilotShare.ShouldRelaunch(100, now.AddMinutes(1)), "");
            check("crash: only once for that Revit", !AutopilotShare.ShouldRelaunch(100, now.AddMinutes(1)), "");
            AutopilotShare.Me = 300; // the new Revit
            var orphan = AutopilotShare.Orphan(now.AddMinutes(3));
            check("crash: the new Revit finds the run to carry on", orphan?.Run == "chat1:1" && orphan.Why == "" && orphan.Restarts == 1, "");
            AutopilotShare.Beat("chat1:1", now.AddMinutes(3));
            alive.Add(300);
            check("crash: once taken over it is the owner, with the backups kept", AutopilotShare.Read() is { Owner: 300, Restarts: 1 } st && st.Backups.Count == 1
                && AutopilotShare.Orphan(now.AddMinutes(4)) is null, "");
            // A hang: Revit 300 restarts itself; the watcher must not start a second one.
            AutopilotShare.MarkRestart("Revit stopped responding for 10 minutes");
            alive.Remove(300);
            AutopilotShare.Me = 999;
            check("hang: Revit restarted itself, the watcher starts none", !AutopilotShare.ShouldRelaunch(300, now.AddMinutes(14)), "");
            AutopilotShare.Me = 400;
            check("hang: the next Revit carries on and says why", AutopilotShare.Orphan(now.AddMinutes(15))?.Why.StartsWith("Revit stopped responding") == true, "");
            AutopilotShare.Beat("chat1:1", now.AddMinutes(15));
            alive.Add(400);
            // Closed the normal way: nothing restarts or carries on.
            AutopilotShare.MarkClosed();
            alive.Remove(400);
            AutopilotShare.Me = 999;
            check("closed: the watcher starts no Revit", !AutopilotShare.ShouldRelaunch(400, now.AddMinutes(16)), "");
            AutopilotShare.Me = 500;
            check("closed: a later Revit doesn't take the run over", AutopilotShare.Orphan(now.AddMinutes(17)) is null, "");
            // Revit that keeps crashing: given up after a few restarts.
            AutopilotShare.Me = 600;
            AutopilotShare.Begin("chat2:1", "", now);
            var tries = 0;
            for (var k = 0; k < 6; k++)
            {
                AutopilotShare.Me = 600 + k;
                AutopilotShare.Beat("chat2:1", now.AddMinutes(k));
                AutopilotShare.Me = 999;
                if (AutopilotShare.ShouldRelaunch(600 + k, now.AddMinutes(k))) tries++;
            }
            check("crash: given up after three restarts", tries == AutopilotShare.MaxRestarts, tries.ToString());
            check("crash: a run left too long ago isn't taken over", AutopilotShare.Orphan(now.AddHours(2)) is null, "");
        }
        finally
        {
            (AutopilotShare.Folder, AutopilotShare.Me, AutopilotShare.IsAlive) = (oldFolder, oldMe, oldAlive);
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }

        // Jobs tied to a model.
        var now2 = DateTime.UtcNow;
        var board = new JobBoard([]);
        var docs = board.Start("Sheets", "Make the sheets for levels 1-3", now2, force: true, out _)!;
        JobBoard.SetRevitModel(docs, "Tower-Docs");
        check("two models: a job keeps its model and its task says so", docs.RevitModel == "Tower-Docs" && docs.Task.StartsWith("Work on the model \"Tower-Docs\"")
            && docs.Items[0].Text == docs.Task, docs.Task);
        check("two models: the coordinator's list says which model", board.Describe(3).Contains("(on Tower-Docs)"), board.Describe(3));
        board.ToStart(3, now2);
        board.RunEnded(docs, true, "Made A101-A103.", now2);
        check("two models: its check works on the same model", board.StartReview(docs.Number, now2)?.RevitModel == "Tower-Docs", "");
        var plain = board.Start("Walls", "Tag the walls", now2, force: true, out _)!;
        JobBoard.SetRevitModel(plain, "  ");
        check("two models: no model given keeps the window's own", plain.RevitModel == "" && plain.Task == "Tag the walls", "");
    }
}
