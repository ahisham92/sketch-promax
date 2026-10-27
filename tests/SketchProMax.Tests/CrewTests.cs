using SketchProMax.Chat;

/// <summary>The crew strip's rules (CrewPlan): who has a worker at a desk, what is acted out, and the line beside it.</summary>
internal static class CrewTests
{
    public static void Run(Action<string, bool, string> check)
    {
        var t0 = new DateTime(2026, 10, 27, 9, 0, 0, DateTimeKind.Utc);
        ChatJob Job(int n, string state, int asked = 1) => new()
        {
            Number = n, Key = "k" + n, Title = "Job " + n, State = state,
            Items = Enumerable.Range(0, asked).Select(_ => new ChatItem { Kind = "user", Text = "do it" }).Concat([new ChatItem { Kind = "assistant", Text = "ok" }]).ToList(),
        };
        string Cues(List<CrewEvent> e) => string.Join(",", e.Select(x => x.Cue + ":" + x.Number));

        // The first look seats who is already working, without a show.
        var plan = new CrewPlan();
        var first = plan.Update([Job(1, JobBoard.Running), Job(2, JobBoard.Queued), Job(3, JobBoard.Done)], t0);
        check("crew: first look is quiet", first.Count == 0 && plan.Seats.Select(s => s.Number).SequenceEqual([1]), Cues(first));
        check("crew: a job waiting to start has no worker yet", plan.Seats.All(s => s.Number != 2), "");

        // A job starts: its worker arrives. A new message: a sheet is handed over.
        var e = plan.Update([Job(1, JobBoard.Running), Job(2, JobBoard.Running)], t0.AddSeconds(1));
        check("crew: a started job's worker arrives", Cues(e) == "Arrive:2", Cues(e));
        e = plan.Update([Job(1, JobBoard.Running, asked: 2), Job(2, JobBoard.Running)], t0.AddSeconds(2));
        check("crew: a message to a job hands it a sheet", Cues(e) == "Handover:1", Cues(e));
        e = plan.Update([Job(1, JobBoard.Running, asked: 2), Job(2, JobBoard.Running)], t0.AddSeconds(3));
        check("crew: nothing new, nothing acted out", e.Count == 0, Cues(e));

        // What a running job does now: thinking and reading at the desk, one applying, the others waiting.
        string Act(ChatJob j) => j.Number switch { 1 => "applying", 2 => "waiting", 3 => "reading", _ => "" };
        e = plan.Update([Job(1, JobBoard.Running, asked: 2), Job(2, JobBoard.Waiting), Job(3, JobBoard.Running), Job(4, JobBoard.Running)], t0.AddSeconds(4), Act);
        var moods = string.Join(",", plan.Seats.Select(s => s.Number + ":" + s.Mood));
        check("crew: moods from the job's activity", moods == "1:Applying,2:Waiting,3:Reading,4:Thinking", moods);
        check("crew: the line says who is in Revit", CrewPlan.Caption(plan.Seats, CrewNames.Defaults) == "Bolt applies Nova's change · 1 thinking · 1 reading the model · 1 in line", CrewPlan.Caption(plan.Seats, CrewNames.Defaults));
        check("crew: without an activity, Waiting still reads as waiting", CrewPlan.Mood(Job(5, JobBoard.Waiting), null) == CrewMood.Waiting && CrewPlan.Mood(Job(5, JobBoard.Running), null) == CrewMood.Thinking, "");

        var lines = string.Join(",", plan.Seats.Select(s => s.Number + ":" + s.Line));
        check("crew: the line at the applier: applying first, then waiting; the rest at their desks", lines == "1:0,2:1,3:-1,4:-1", lines);
        var slots = string.Join(",", plan.Seats.Select(s => s.Number + ":" + s.Slot));
        check("crew: each job has its own worker, kept while seated", slots == "1:0,2:1,3:2,4:3", slots);
        check("crew: workers by name, more jobs than names get a number", plan.WorkerOf(1, CrewNames.Defaults) == "Nova" && plan.WorkerOf(3, CrewNames.Defaults) == "Pixel"
            && plan.WorkerOf(4, CrewNames.Defaults) == "Worker 4" && plan.WorkerOf(9, CrewNames.Defaults) is null, plan.WorkerOf(4, CrewNames.Defaults) ?? "");

        // Done: the worker lingers with a tick, then leaves; stopped leaves at once.
        e = plan.Update([Job(1, JobBoard.Done, asked: 2), Job(2, JobBoard.Stopped), Job(3, JobBoard.Running), Job(4, JobBoard.Running)], t0.AddSeconds(5));
        check("crew: a stopped job's worker leaves at once", Cues(e) == "Leave:2", Cues(e));
        check("crew: a finished job's worker stays a moment", plan.Seats.Any(s => s.Number == 1 && s.Mood == CrewMood.Done) && plan.Lingering, "");
        e = plan.Update([Job(1, JobBoard.Done, asked: 2), Job(3, JobBoard.Running), Job(4, JobBoard.Running)], t0.AddSeconds(5) + CrewPlan.Linger);
        check("crew: ...then leaves", Cues(e) == "Leave:1" && !plan.Lingering, Cues(e));

        // A freed worker takes the next job.
        e = plan.Update([Job(3, JobBoard.Running), Job(4, JobBoard.Running), Job(5, JobBoard.Running)], t0.AddSeconds(12));
        check("crew: the first free worker takes a new job", plan.WorkerOf(5, CrewNames.Defaults) == "Nova" && Cues(e) == "Arrive:5", Cues(e));

        // A finished job sent a message keeps its worker (waiting to start again) and gets the sheet.
        e = plan.Update([Job(3, JobBoard.Done), Job(4, JobBoard.Running)], t0.AddSeconds(20));
        e = plan.Update([Job(3, JobBoard.Queued, asked: 2), Job(4, JobBoard.Running)], t0.AddSeconds(21));
        check("crew: a finished job asked again keeps its worker", Cues(e) == "Handover:3" && plan.Seats.Any(s => s.Number == 3 && s.Mood == CrewMood.Resting && s.Line < 0), Cues(e));

        // The pane reads the board's copies: what a job does now must come with them.
        var board = new JobBoard([]);
        var started = board.Start("Tag doors", "Tag the doors", t0, force: true, out _);
        board.ToStart(3, t0);
        board.SetActivity(started!.Key, JobBoard.Applying);
        check("crew: a board copy keeps what the job does now", JobBoard.ActivityOf(board.Snapshot()[0]) == JobBoard.Applying, JobBoard.ActivityOf(board.Snapshot()[0]));

        // The names: defaults, from Settings, and from the Windows account.
        var named = CrewNames.From(" Jane ", "", "Ann, Bo ,, Cy", null, "someone");
        check("crew: names from Settings, blanks get their defaults", named.You == "Jane" && named.Coordinator == "Atlas" && named.Applier == "Bolt"
            && string.Join("|", named.Workers) == "Ann|Bo|Cy", string.Join("|", named.Workers));
        check("crew: you default to the Windows account's first name", CrewNames.FromAccount("jane.doe") == "Jane" && CrewNames.FromAccount("DOMAIN\\jdoe") == "Jdoe"
            && CrewNames.FromAccount("") == "You" && CrewNames.FromAccount("123") == "You", CrewNames.FromAccount("jane.doe"));
        check("crew: an empty workers list falls back to the defaults", CrewNames.From(null, null, " , ", null, null).Workers.Count == 3, "");

        // Another chat: start over quietly.
        plan.Reset();
        e = plan.Update([Job(7, JobBoard.Running)], t0.AddSeconds(30));
        check("crew: after a reset the first look is quiet again", e.Count == 0 && plan.Seats.Count == 1, Cues(e));
        e = plan.Update([], t0.AddSeconds(31));
        check("crew: a job gone from the board leaves", Cues(e) == "Leave:7" && plan.Seats.Count == 0 && CrewPlan.Caption(plan.Seats, CrewNames.Defaults) == "Atlas is ready", Cues(e));
    }
}
