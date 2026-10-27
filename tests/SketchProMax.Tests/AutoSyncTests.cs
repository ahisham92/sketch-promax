using SketchProMax.Chat;
using SketchProMax.Revit;

/// <summary>Auto sync (Settings > Chat): counting changes per model, when a sync is due, the limit's range and skipping it for a task.</summary>
internal static class AutoSyncTests
{
    public static void Run(Action<string, bool, string> check)
    {
        var now = new DateTime(2026, 10, 27, 12, 0, 0);
        var rule = new AutoSyncRule();
        check("auto sync: on with 250 changes by default", rule.On && rule.Limit == 250 && AutoSyncRule.DefaultLimit == 250, $"{rule.On} {rule.Limit}");
        check("auto sync: the limit is kept between 20 and 5000", AutoSyncRule.Clamp(0) == 250 && AutoSyncRule.Clamp(-3) == 250 && AutoSyncRule.Clamp(5) == 20
            && AutoSyncRule.Clamp(9000) == 5000 && AutoSyncRule.Clamp(400) == 400, "");

        // Each element counts once, however often it is touched.
        rule.Changed("Local", shared: true, Enumerable.Range(1, 200).Select(i => (long)i));
        rule.Changed("Local", shared: true, Enumerable.Range(1, 100).Select(i => (long)i));
        check("auto sync: an element touched twice is one change", rule.Count("Local") == 200 && rule.Left("Local") == 50 && !rule.Due("Local", 1, now), rule.Count("Local").ToString());
        rule.Changed("Local", shared: true, Enumerable.Range(201, 50).Select(i => (long)i));
        check("auto sync: due at the limit", rule.Count("Local") == 250 && rule.Left("Local") == 0 && rule.Due("Local", 1, now), rule.Count("Local").ToString());

        // 500 changes in one step: due after it, so the next step starts after a sync.
        var big = new AutoSyncRule();
        big.Changed("Local", true, Enumerable.Range(1, 500).Select(i => (long)i));
        check("auto sync: a step of 500 is due after it", big.Due("Local", 1, now) && big.Count("Local") == 500, "");
        big.Synced("Local");
        check("auto sync: a sync starts the count again", big.Count("Local") == 0 && !big.Due("Local", 1, now) && big.Left("Local") == 250, "");

        // Only a local copy of a central model is synchronized.
        var other = new AutoSyncRule();
        other.Changed("Detached", shared: false, Enumerable.Range(1, 900).Select(i => (long)i));
        check("auto sync: never for a model that isn't a local of a central model", !other.Due("Detached", 1, now)
            && other.Describe("Detached", 1, now).Contains("never synchronized"), other.Describe("Detached", 1, now));

        // Off in Settings: never due.
        rule.Configure(false, 250);
        check("auto sync: off in Settings, never due", !rule.Due("Local", 1, now) && rule.Describe("Local", 1, now).StartsWith("Auto sync is off"), "");
        rule.Configure(true, 100);
        check("auto sync: a lower limit applies at once", rule.Limit == 100 && rule.Due("Local", 1, now), "");

        // Skipped for a task: until the next main-chat task, or two hours, or resume.
        rule.Skip(7, now, "do it in one step");
        check("auto sync: skipped for the task", rule.Skipped(7, now) && !rule.Due("Local", 7, now) && rule.Describe("Local", 7, now).Contains("skip"), "");
        check("auto sync: the skip ends with the user's next message", !rule.Skipped(8, now) && rule.Due("Local", 8, now) && rule.Due("Local", 7, now), "");
        rule.Skip(9, now, "");
        check("auto sync: a skip lasts two hours at most", rule.Skipped(9, now.AddMinutes(119)) && !rule.Skipped(9, now.AddHours(2).AddMinutes(1)), "");
        rule.Skip(10, now, "");
        rule.Resume();
        check("auto sync: resume puts the rule back", !rule.Skipped(10, now) && rule.Due("Local", 10, now), "");

        // Two models count apart; closing one forgets it.
        rule.Changed("Second", true, [1, 2, 3]);
        check("auto sync: models count apart", rule.Count("Second") == 3 && rule.Count("Local") == 250, "");
        check("auto sync: the words say what is left", rule.Describe("Second", 10, now).Contains("3 changes since the last sync, 97 left"), rule.Describe("Second", 10, now));

        // The job cards while every job waits for the sync.
        var board = new JobBoard([]);
        var job = board.Start("Sheets", "Make sheets", now, false, out _)!;
        board.ToStart(3, now);
        board.SetActivity(job.Key, JobBoard.Syncing);
        check("auto sync: a job's card says Synchronizing with central", JobBoard.StateLabel(job) == "Synchronizing with central" && JobBoard.ActivityOf(job) == JobBoard.Syncing, JobBoard.StateLabel(job));
    }
}
