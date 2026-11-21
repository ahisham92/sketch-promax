using SketchProMax.Chat;

/// <summary>Bug 123: what the coordinator says about other jobs comes from the jobs as they are (JobTruth).</summary>
internal static class JobTruthTests
{
    public static void Run(Action<string, bool, string> check)
    {
        var names = JobBoard.WorkerName;
        JobBoard.WorkerName = slot => slot switch { 1 => "Carmen", 2 => "Clara", _ => null };
        try
        {
            var t = new DateTime(2026, 11, 21, 20, 36, 21, DateTimeKind.Utc);
            ChatJob Job(int n, string title, string state, int worker, int reviewOf = 0) =>
                new() { Number = n, Title = title, State = state, Worker = worker, ReviewOf = reviewOf, Task = title };

            // 21 Nov 2026 22:36: job 254 drew until 2 s before the reply, then a teammate checked it.
            var before = new List<ChatJob> { Job(253, "Cut ICT pit sand", JobBoard.Done, 1), Job(254, "00-SC-203 existing connection detail", JobBoard.Running, 2) };
            var after = new List<ChatJob>
            {
                Job(253, "Cut ICT pit sand", JobBoard.Running, 1), Job(254, "00-SC-203 existing connection detail", JobBoard.Done, 2),
                Job(257, "Check: 00-SC-203 existing connection detail", JobBoard.Running, 3, reviewOf: 254),
            };
            const string reply = "I've passed it to Carmen. She'll use the same re-join so the cold ironing pit wall cuts the sand 👍 Your other worker is still drawing the existing connection detail on 00-SC-203.";
            var fix = JobTruth.Correction(reply, before, after);
            check("job truth (bug 123): a job called busy that finished during the reply gets a correction",
                fix is not null && fix.Contains("Clara has just finished") && fix.Contains("00-SC-203") && fix.Contains("checking it now"), fix ?? "(none)");
            check("job truth: a reply that calls nobody busy is left alone",
                JobTruth.Correction("I've passed it to Carmen.", before, after) is null, "");
            check("job truth: a job still running is not corrected",
                JobTruth.Correction(reply, before, before) is null, "");
            check("job truth: a reply about the one still working (Carmen) is not corrected for the other",
                JobTruth.Correction("Carmen is still on the cold ironing pit sand.", before, after) is null, "");
            check("job truth: named by its number",
                JobTruth.Correction("Job 254 is still working on it.", before, after) is { } byNumber && byNumber.Contains("Clara"), "");
            var gone = new List<ChatJob> { Job(254, "00-SC-203 existing connection detail", JobBoard.Done, 2) };
            check("job truth: named by its worker when the worker has nothing else going",
                JobTruth.Correction("Clara is still busy.", before, gone) is { } byName && !byName.Contains("checking"), "");
            var busyAgain = new List<ChatJob> { Job(254, "00-SC-203 existing connection detail", JobBoard.Done, 2), Job(260, "Tag piles", JobBoard.Running, 2) };
            check("job truth: not by worker name when that worker already has another job",
                JobTruth.Correction("Clara is still busy.", before, busyAgain) is null, "");

            var line = JobTruth.Line(after, except: 253);
            check("job truth: the live line names a finished job under check as finished, not working",
                line.Contains("job 254 (Clara)") && line.Contains("finished, a teammate is checking it") && !line.Contains("job 257"), line);
            check("job truth: no other job working says so",
                JobTruth.Line([Job(252, "Cloud", JobBoard.Done, 0)], except: 253) == "No other job is working right now.", "");
        }
        finally { JobBoard.WorkerName = names; }
    }
}
