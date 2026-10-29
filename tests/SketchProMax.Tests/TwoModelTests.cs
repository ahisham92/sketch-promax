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
            var read = 0;
            var lines = AutopilotShare.TakeLog(ref read);
            check("two models: what the other window answered reaches the owner's report", lines.Count == 1 && lines[0].Kind == AutopilotEntry.Popup && lines[0].Text.StartsWith("[Tower-Docs]"), "");
            check("two models: each line is taken once", AutopilotShare.TakeLog(ref read).Count == 0, "");
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
