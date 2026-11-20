using SketchProMax.Chat;

/// <summary>Pause all / Continue (Chat.JobPause) and the Away button (Chat.Away.Set), kept through a version switch. No Revit here.</summary>
internal static class JobPauseTests
{
    public static void Run(Action<string, bool, string> check)
    {
        JobPause.Reset();
        JobPause.StoreFile = null;
        var now = new DateTime(2026, 11, 19, 17, 40, 0, DateTimeKind.Utc);
        check("pause: a job's call runs while not paused and counts as in Revit until it ends",
            JobPause.Enter("job-1") is null && JobPause.Calling("job-1") && !JobPause.Calling("job-2"), "");
        JobPause.Exit("job-1");
        check("pause: once its call ends the job has nothing in Revit", !JobPause.Calling("job-1"), "");
        JobPause.Enter("job-1");
        JobPause.Pause(JobPause.ForUser, now);
        check("pause: a call already running isn't touched; a new one runs nothing and tells the job to stop",
            JobPause.Calling("job-1") && JobPause.Enter("job-2") is { } refused && refused.Contains("nothing was run") && !JobPause.Calling("job-2"), "");
        JobPause.Exit("job-1");
        check("pause (bug 111): a job whose call was refused is known, once, so its run ending is a pause, not done",
            JobPause.TookRefusal("job-2") && !JobPause.TookRefusal("job-2") && !JobPause.TookRefusal("job-1"), "");
        JobPause.Pause(JobPause.ForUpdate, now);
        check("pause: an update never takes over the user's own pause (it would carry on by itself)", JobPause.Why == JobPause.ForUser, JobPause.Why);
        check("pause: Continue ends it", JobPause.Continue() && !JobPause.On && JobPause.Enter("job-3") is null, "");
        JobPause.Exit("job-3");
        JobPause.Pause(JobPause.ForUpdate, now);
        check("pause for an update: carries on without it after 10 minutes",
            !JobPause.UpdateTooLong(now.AddMinutes(9)) && JobPause.UpdateTooLong(now + JobPause.UpdateGiveUp), "");
        var folder = Path.Combine(Path.GetTempPath(), "spm-pause-" + Guid.NewGuid().ToString("N"));
        try
        {
            JobPause.StoreFile = Path.Combine(folder, "jobs-paused.txt");
            JobPause.Continue();
            JobPause.Pause(JobPause.ForUser, now);
            JobPause.Reset();
            JobPause.Restore();
            check("pause: kept through a version switch or a restart", JobPause.Why == JobPause.ForUser && JobPause.Since == now, JobPause.Why + " " + JobPause.Since.ToString("o"));
            JobPause.Continue();
            JobPause.Reset();
            JobPause.Restore();
            check("pause: Continue is kept too", !JobPause.On && !File.Exists(JobPause.StoreFile), "");

            Away.Reset();
            Away.StoreFile = Path.Combine(folder, "away.txt");
            Away.Set(true, now);
            check("away button: on for 12 hours", Away.Until == now + Away.Longest, Away.Until.ToString("o"));
            Away.Reset();
            Away.Restore();
            check("away button: kept through a version switch", Away.Until == now + Away.Longest, Away.Until.ToString("o"));
            Away.Set(false, now);
            check("away button: off", !Away.On && !File.Exists(Away.StoreFile), "");
            check("away (bug 112): a window is closed only with nobody at the PC (no keyboard or mouse for 2 min)",
                !Away.NobodyThere(true, TimeSpan.FromSeconds(30)) && Away.NobodyThere(true, TimeSpan.FromMinutes(3))
                && !Away.NobodyThere(false, TimeSpan.FromHours(1)) && !Away.NobodyThere(true, TimeSpan.Zero), "");
            var start = DateTime.UtcNow;
            Away.Set(true, start);
            var early = Away.Watch(TimeSpan.FromSeconds(1), start.AddSeconds(5)) || Away.Watch(TimeSpan.FromMinutes(10), start.AddMinutes(10))
                        || Away.Watch(TimeSpan.FromSeconds(1), start.AddMinutes(10));
            check("away (bug 112): a short quiet and back doesn't end it", !early && Away.On, "");
            Away.Watch(TimeSpan.FromMinutes(20), start.AddMinutes(30));
            check("away (bug 112): the PC used again after 15 quiet minutes ends it (the user is back)",
                Away.Watch(TimeSpan.FromSeconds(1), start.AddMinutes(31)) && !Away.On, "");
        }
        finally
        {
            JobPause.StoreFile = null;
            Away.StoreFile = null;
            JobPause.Reset();
            Away.Reset();
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }
}
