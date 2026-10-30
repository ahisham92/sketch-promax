using SketchProMax.Chat;

/// <summary>The autopilot's connection watch: pausing when the internet or the company network drops, carrying on when back.</summary>
internal static class ConnectionWatchTests
{
    public static void Run(Action<string, bool, string> check)
    {
        var w = new ConnectionWatch();
        check("connection: a probe that never worked never pauses (a proxy may refuse it)", w.Record(false, null) == "" && w.Record(false, null) == "" && w.Record(false, null) == "" && w.Down == "", w.Down);
        check("connection: the first good probe changes nothing", w.Record(true, true) == "", "");
        check("connection: one failed probe doesn't pause", w.Record(false, true) == "" && w.Down == "", w.Down);
        check("connection: two failed probes in a row pause, for the internet", w.Record(false, true) == "pause" && w.Down == ConnectionWatch.Internet, w.Down);
        check("connection: still down, no change", w.Record(false, true) == "" && w.Down == ConnectionWatch.Internet, "");
        check("connection: one good probe carries on", w.Record(true, true) == "resume" && w.Down == "", w.Down);
        check("connection: a single blip in between doesn't pause", w.Record(false, true) == "" && w.Record(true, true) == "" && w.Record(false, true) == "" && w.Down == "", w.Down);

        var v = new ConnectionWatch();
        v.Record(true, true);
        check("connection: the VPN dropping pauses for the company network", v.Record(true, false) == "" && v.Record(true, false) == "pause" && v.Down == ConnectionWatch.Network, v.Down);
        check("connection: internet gone as well: the reason becomes the internet", v.Record(false, false) == "" && v.Record(false, false) == "" && v.Down == ConnectionWatch.Internet, v.Down);
        check("connection: internet back, VPN still off: stays paused, for the network", v.Record(true, false) == "" && v.Down == ConnectionWatch.Network, v.Down);
        check("connection: VPN back: carries on", v.Record(true, true) == "resume" && v.Down == "", v.Down);

        var local = new ConnectionWatch();
        local.Record(true, null);
        check("connection: a model not on a company share never pauses for the network", local.Record(true, null) == "" && local.Record(true, null) == "" && local.Down == "", local.Down);

        check("connection: the reasons read plainly", ConnectionWatch.Why(ConnectionWatch.Internet) == ConnectionWatch.NoInternet && ConnectionWatch.Why(ConnectionWatch.Network).Contains("VPN"), "");
        var since = new DateTime(2026, 10, 31, 6, 30, 0, DateTimeKind.Utc);
        var back = ConnectionWatch.BackLine(ConnectionWatch.Internet, since, since.AddMinutes(95));
        check("connection: the back line says how long and that it carried on", back.Contains("internet connection is back after 95 minutes") && back.Contains("carried on"), back);
        check("connection: one minute reads singular", ConnectionWatch.BackLine(ConnectionWatch.Network, since, since.AddSeconds(20)).Contains("company network is back after 1 minute;"), "");

        var run = new AutopilotRun { State = AutopilotRun.Paused, Offline = ConnectionWatch.Internet };
        check("connection: a pause for the connection is told apart", ConnectionWatch.IsConnectionPause(run), "");
        check("connection: a usage pause is not the connection's", !ConnectionWatch.IsConnectionPause(new AutopilotRun { State = AutopilotRun.Paused }), "");

        // Cut off: the run's own jobs that failed while it was down (from a minute before), not reviewers nor older ones.
        ChatJob J(int n, string state, int minutes, int goalCheck = 0) => new() { Number = n, State = state, Ended = since.AddMinutes(minutes), GoalCheck = goalCheck };
        var jobs = new[] { J(3, JobBoard.Failed, 10), J(5, JobBoard.Failed, 10), J(6, JobBoard.Done, 10), J(7, JobBoard.Failed, -30), J(8, JobBoard.Failed, 0, goalCheck: 1), J(9, JobBoard.Failed, 0), J(10, JobBoard.Running, 0) };
        var cut = ConnectionWatch.CutOff(jobs, jobsBefore: 4, since).Select(j => j.Number).ToList();
        check("connection: the jobs the drop cut off are told to carry on", cut.SequenceEqual([5, 9]), string.Join(",", cut));
        check("connection: they check the model before carrying on", ConnectionWatch.CarryOnLine.Contains("check what is already in the model") && ConnectionWatch.CarryOnLine.Contains("carry on"), "");
    }
}
