using SketchProMax.Chat;

/// <summary>Bug 123: what the coordinator says about other jobs comes from the jobs as they are (JobTruth).</summary>
internal static class JobTruthTests
{
    public static void Run(Action<string, bool, string> check)
    {
        var names = JobBoard.WorkerName;
        JobBoard.WorkerName = slot => slot switch { 1 => "Sam", 2 => "Kim", _ => null };
        try
        {
            var t = new DateTime(2026, 11, 21, 20, 36, 21, DateTimeKind.Utc);
            ChatJob Job(int n, string title, string state, int worker, int reviewOf = 0) =>
                new() { Number = n, Title = title, State = state, Worker = worker, ReviewOf = reviewOf, Task = title };

            // 21 Nov 2026 22:36: job 254 drew until 2 s before the reply, then a teammate checked it.
            var before = new List<ChatJob> { Job(253, "Cut ICT pit sand", JobBoard.Done, 1), Job(254, "A-101 existing connection detail", JobBoard.Running, 2) };
            var after = new List<ChatJob>
            {
                Job(253, "Cut ICT pit sand", JobBoard.Running, 1), Job(254, "A-101 existing connection detail", JobBoard.Done, 2),
                Job(257, "Check: A-101 existing connection detail", JobBoard.Running, 3, reviewOf: 254),
            };
            const string reply = "I've passed it to Sam. She'll use the same re-join so the cold ironing pit wall cuts the sand 👍 Your other worker is still drawing the existing connection detail on A-101.";
            var fix = JobTruth.Correction(reply, before, after);
            check("job truth (bug 123): a job called busy that finished during the reply gets a correction",
                fix is not null && fix.Contains("Kim has just finished") && fix.Contains("A-101") && fix.Contains("checking it now"), fix ?? "(none)");
            check("job truth: a reply that calls nobody busy is left alone",
                JobTruth.Correction("I've passed it to Sam.", before, after) is null, "");
            check("job truth: a job still running is not corrected",
                JobTruth.Correction(reply, before, before) is null, "");
            check("job truth: a reply about the one still working (Sam) is not corrected for the other",
                JobTruth.Correction("Sam is still on the cold ironing pit sand.", before, after) is null, "");
            check("job truth: named by its number",
                JobTruth.Correction("Job 254 is still working on it.", before, after) is { } byNumber && byNumber.Contains("Kim"), "");
            var gone = new List<ChatJob> { Job(254, "A-101 existing connection detail", JobBoard.Done, 2) };
            check("job truth: named by its worker when the worker has nothing else going",
                JobTruth.Correction("Kim is still busy.", before, gone) is { } byName && !byName.Contains("checking"), "");
            var busyAgain = new List<ChatJob> { Job(254, "A-101 existing connection detail", JobBoard.Done, 2), Job(260, "Tag piles", JobBoard.Running, 2) };
            check("job truth: not by worker name when that worker already has another job",
                JobTruth.Correction("Kim is still busy.", before, busyAgain) is null, "");

            var line = JobTruth.Line(after, except: 253);
            check("job truth: the live line names a finished job under check as finished, not working",
                line.Contains("job 254 (Kim)") && line.Contains("finished, a teammate is checking it") && !line.Contains("job 257"), line);
            check("job truth: no other job working says so",
                JobTruth.Line([Job(252, "Cloud", JobBoard.Done, 0)], except: 253) == "No other job is working right now.", "");

            // 22 Nov 2026 09:10: asked how many comments were left, the coordinator said nothing was in the model while the
            // jobs had made 117 changes (bug 132).
            var fixing = new List<ChatJob> { Job(370, "Rev1 fixes: B9 sheets", JobBoard.Running, 1), Job(372, "Rev1 fixes: B10 sheets", JobBoard.Running, 2) };
            fixing[0].Changes = 41;
            var note = JobTruth.Note(fixing);
            check("job truth (bug 132): each message carries the jobs with the changes they made, and how to say it",
                note.Contains("job 370 (Sam)") && note.Contains("41 changes made in the model so far (not checked yet)") && note.Contains("job 372 (Kim)")
                && note.Contains("no model change yet") && note.Contains("never \"nothing in the model\""), note);
            check("job truth: no note when no job is open", JobTruth.Note([Job(252, "Cloud", JobBoard.Done, 0)]) == "", "");
            var board = new JobBoard([]);
            var started = board.Start("Fix", "Fix it", t, force: true, out _)!;
            board.MarkChanged(started.Key);
            board.MarkChanged(started.Key);
            check("job truth: model changes are counted per job", board.Snapshot().Single(j => j.Number == started.Number).Changes == 2, "");
        }
        finally { JobBoard.WorkerName = names; }
    }
}
