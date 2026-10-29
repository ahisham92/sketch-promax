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

        // The job pills: the side pane lists only open jobs (and the one whose chat is open); the large window lists
        // the open ones on top and the finished ones under them, the latest first. A finished job sent a message is open again.
        List<ChatJob> pills = [Job(1, JobBoard.Done), Job(2, JobBoard.Running), Job(3, JobBoard.Failed), Job(4, JobBoard.Queued), Job(5, JobBoard.Stopped)];
        string Numbers((List<ChatJob> Jobs, int Open) l) => string.Join(",", l.Jobs.Select(j => j.Number)) + "/" + l.Open;
        var side = JobBoard.ForSwitcher(pills, all: false, shown: null);
        check("jobs: the side pane lists only open jobs", Numbers(side) == "2,4/2", Numbers(side));
        side = JobBoard.ForSwitcher(pills, all: false, shown: 3);
        check("jobs: the side pane keeps the finished job whose chat is open", Numbers(side) == "2,4,3/2", Numbers(side));
        var column = JobBoard.ForSwitcher(pills, all: true, shown: null);
        check("jobs: the large window lists open jobs first, then finished", Numbers(column) == "2,4,5,3,1/2", Numbers(column));
        pills[0].State = JobBoard.Queued;
        side = JobBoard.ForSwitcher(pills, all: false, shown: null);
        check("jobs: a finished job sent a message shows again", Numbers(side) == "1,2,4/3", Numbers(side));
        check("jobs: nothing open, the side pane lists nothing", JobBoard.ForSwitcher([Job(1, JobBoard.Done)], false, null).Jobs.Count == 0, "");

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
        check("crew: a stopped job's worker leaves at once, a done one says so", Cues(e) == "Finish:1,Leave:2", Cues(e));
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

        // Three workers, one job each: a fourth job waits on the coordinator's desk; a finished worker's desk is taken at once.
        var three = new CrewPlan();
        three.Update([], t0);
        three.Update([Job(1, JobBoard.Running), Job(2, JobBoard.Running), Job(3, JobBoard.Running), Job(4, JobBoard.Queued)], t0, null, 3);
        check("crew: more jobs than workers wait for a free worker", three.Seats.Count == 3 && three.Unassigned == 1
            && CrewPlan.Caption(three.Seats, CrewNames.Defaults, three.Unassigned).EndsWith("1 job waits for a free worker"), CrewPlan.Caption(three.Seats, CrewNames.Defaults, three.Unassigned));
        three.Update([Job(1, JobBoard.Done), Job(2, JobBoard.Running), Job(3, JobBoard.Running), Job(4, JobBoard.Queued)], t0.AddSeconds(1), null, 3);
        e = three.Update([Job(1, JobBoard.Done), Job(2, JobBoard.Running), Job(3, JobBoard.Running), Job(4, JobBoard.Running)], t0.AddSeconds(2), null, 3);
        check("crew: the next job takes the finished worker's desk, who leaves", Cues(e) == "Leave:1,Arrive:4" && three.WorkerOf(4, CrewNames.Defaults) == "Nova"
            && three.Seats.Count == 3 && three.Unassigned == 0, Cues(e));

        // A job keeps its worker (saved with the chat) when that one is free.
        var kept = new CrewPlan();
        var again = Job(6, JobBoard.Running);
        again.Worker = 2;
        kept.Update([again], t0, null, 3);
        check("crew: a job keeps the worker it had", kept.WorkerOf(6, CrewNames.Defaults) == "Pixel", kept.WorkerOf(6, CrewNames.Defaults) ?? "");
        var copyBoard = new JobBoard([]);
        var w = copyBoard.Start("Tag walls", "Tag the walls", t0, force: true, out _);
        copyBoard.SetWorker(w!.Number, 1);
        check("crew: the board keeps a job's worker in its copies", copyBoard.Snapshot()[0].Worker == 1, "");

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

        // A job's plan: TodoWrite gives the whole list, TaskCreate and TaskUpdate one step each.
        var steps = new Dictionary<string, string>();
        check("plan: TodoWrite gives the whole list", JobPlan.Apply(steps, "TodoWrite", """{"todos":[{"content":"a","status":"completed"},{"content":"b","status":"in_progress"},{"content":"c","status":"pending"},{"content":"d","status":"pending"}]}""", "")
            && steps.Count == 4 && JobPlan.Count(steps) == (1, 4), string.Join(",", steps.Values));
        check("plan: a step under way counts half", JobPlan.Percent(steps, finished: false) == 38, JobPlan.Percent(steps, false).ToString());
        check("plan: no plan, no figure; a finished job is 100", JobPlan.Percent(new Dictionary<string, string>(), false) == -1 && JobPlan.Percent(steps, true) == 100, "");
        var tasks = new Dictionary<string, string>();
        JobPlan.Apply(tasks, "TaskCreate", """{"subject":"Read the walls"}""", "Task #4 created successfully: Read the walls");
        JobPlan.Apply(tasks, "TaskCreate", """{"subject":"Tag them"}""", "created");
        JobPlan.Apply(tasks, "TaskCreate", """{"subject":"Check"}""", "Task #6 created");
        JobPlan.Apply(tasks, "TaskUpdate", """{"taskId":"4","status":"completed"}""", "");
        JobPlan.Apply(tasks, "TaskUpdate", """{"taskId":"6","status":"deleted"}""", "");
        check("plan: TaskCreate and TaskUpdate keep steps by id", tasks.Count == 2 && tasks["4"] == JobPlan.Finished && JobPlan.Count(tasks) == (1, 2), string.Join(",", tasks.Select(t => t.Key + "=" + t.Value)));
        check("plan: every step ticked says 99 while it still works", JobPlan.Percent(new Dictionary<string, string> { ["1"] = "completed" }, false) == 99
            && JobPlan.AllDone(new Dictionary<string, string> { ["1"] = "completed" }) && !JobPlan.AllDone(tasks), "");
        check("plan: a broken call changes nothing", !JobPlan.Apply(tasks, "TaskUpdate", "{not json", "") && !JobPlan.Apply(tasks, "TodoWrite", "{}", "") && tasks.Count == 2, "");
        var planBoard = new JobBoard([]);
        var planned = planBoard.Start("Tag walls", "Tag the walls", t0, force: true, out _)!;
        planBoard.UpdatePlan(planned, "TaskCreate", "{}", "Task #1 created");
        check("plan: the board keeps a job's plan in its copies", planBoard.Snapshot()[0].Plan.Count == 1, "");

        // What the workers show and say: how far along, ready for work, and a few friendly words.
        var progress = new CrewPlan();
        var p1 = Job(1, JobBoard.Running);
        progress.Update([p1], t0);
        check("crew: no plan yet says planning", CrewPlan.Status(progress.Seats[0], 0) == "Planning…", CrewPlan.Status(progress.Seats[0], 0));
        p1.Plan = new() { ["1"] = "completed", ["2"] = "completed", ["3"] = "pending", ["4"] = "pending", ["5"] = "pending" };
        e = progress.Update([p1], t0.AddSeconds(1));
        check("crew: a new plan says how big the job is", Cues(e) == "Plan:1" && e[0].Say == "Smart plan!", Cues(e) + " " + (e.Count > 0 ? e[0].Say : ""));
        check("crew: a worker shows how far along and what is left", progress.Seats[0].Percent == 40 && CrewPlan.Status(progress.Seats[0], 0) == "40% · 3 left", CrewPlan.Status(progress.Seats[0], 0));
        e = progress.Update([p1], t0.AddSeconds(2), _ => JobBoard.Applying);
        check("crew: its turn at Revit: My turn!", Cues(e) == "Turn:1" && e[0].Say == "My turn!" && CrewPlan.Status(progress.Seats[0], 0) == "40% · applying", CrewPlan.Status(progress.Seats[0], 0));
        p1.State = JobBoard.Done;
        p1.Started = t0;
        p1.Ended = t0.AddMinutes(5);
        e = progress.Update([p1], t0.AddSeconds(3));
        check("crew: a finished job says so", Cues(e) == "Finish:1" && e[0].Say == "Job done!" && progress.Seats[0].Percent == 100 && CrewPlan.Status(progress.Seats[0], 0) == "Job done!", Cues(e));
        check("crew: workers without a job are ready for work", CrewPlan.Status(null, 0) == "Ready for work" && CrewPlan.Status(null, 1).Length > 0, "");
        var big = Job(9, JobBoard.Running);
        big.Task = new string('x', 700);
        check("crew: a long task is a big job", CrewPlan.Say(CrewCue.Arrive, big) == "Big job!", CrewPlan.Say(CrewCue.Arrive, big));
        big.State = JobBoard.Done;
        big.Started = t0;
        big.Ended = t0.AddSeconds(40);
        check("crew: a quick job says so", CrewPlan.Say(CrewCue.Finish, big) == "Quick job, done!", CrewPlan.Say(CrewCue.Finish, big));
        big.State = JobBoard.Failed;
        check("crew: a failed job hit a snag", CrewPlan.Say(CrewCue.Finish, big) == "Hit a snag", "");

        // The card, pill and job list say how far along a working job is.
        var carded = Job(4, JobBoard.Running);
        carded.Plan = new() { ["1"] = "completed", ["2"] = "pending" };
        check("plan: a working job's state says how far along it is", JobBoard.StateLabel(carded) == "Thinking · 50% · 1 left", JobBoard.StateLabel(carded));
        carded.State = JobBoard.Done;
        check("plan: a finished job's state is plain", JobBoard.StateLabel(carded) == "Done", JobBoard.StateLabel(carded));

        // The jobs go round the team: the next job goes to the free worker who has waited longest, not always the first.
        var round = new CrewPlan();
        round.Update([], t0);
        round.Update([Job(1, JobBoard.Running)], t0.AddSeconds(1), null, 3);
        round.Update([Job(1, JobBoard.Stopped)], t0.AddSeconds(2), null, 3);
        round.Update([Job(1, JobBoard.Stopped), Job(2, JobBoard.Running)], t0.AddSeconds(3), null, 3);
        check("crew: the next job goes to a worker who hasn't had one", round.WorkerOf(2, CrewNames.Defaults) == "Echo", round.WorkerOf(2, CrewNames.Defaults) ?? "");
        round.Update([Job(1, JobBoard.Stopped), Job(2, JobBoard.Stopped), Job(3, JobBoard.Running)], t0.AddSeconds(4), null, 3);
        check("crew: then the one after", round.WorkerOf(3, CrewNames.Defaults) == "Pixel", round.WorkerOf(3, CrewNames.Defaults) ?? "");
        var stepping = Job(5, JobBoard.Running);
        stepping.Items.Add(new ChatItem { Kind = "tool", Name = "query_elements" });
        stepping.Items.Add(new ChatItem { Kind = "tool", Name = "query_elements" });
        var stepPlan = new CrewPlan();
        stepPlan.Update([stepping], t0);
        check("crew: without a plan a worker shows its steps", CrewPlan.Status(stepPlan.Seats[0], 0) == "Step 2", CrewPlan.Status(stepPlan.Seats[0], 0));

        check("crew: a worker's thought is its step in a few words", CrewPlan.Doing("Running: Find walls on Level 1… If Revit asks, answer it there.") == "Find walls on Level 1"
            && CrewPlan.Doing("Starting…") == "Getting started" && CrewPlan.Doing("") == "" && CrewPlan.Doing(new string('a', 40)).Length == 28, CrewPlan.Doing("Running: Find walls on Level 1… If Revit asks, answer it there."));

        // A whole job to one worker while others are free goes back to be divided, unless it can't be.
        check("divide: two free workers, one whole job: divide it", JobBoard.MustDivide(2, null) && JobBoard.MustDivide(3, " "), "");
        check("divide: one free worker, or a reason it can't be divided: start it", !JobBoard.MustDivide(1, null) && !JobBoard.MustDivide(3, "one sheet"), "");

        // The team: a job starting hears who else works on what, and what the finished jobs found, each report once.
        var levels = Job(1, JobBoard.Done);
        levels.Summary = "Levels: L1 0 mm, L2 3500 mm, L3 7000 mm.";
        var sheets = Job(2, JobBoard.Running);
        sheets.Task = "Sheets for levels 1-3";
        var tags = Job(3, JobBoard.Running);
        var team = JobBoard.Team(tags, [levels, sheets, tags], out var shared);
        check("team: a new job hears the others' parts and the finished reports", team.Contains("Job 2 \"Job 2\" works alongside you on: Sheets for levels 1-3")
            && team.Contains("L2 3500 mm") && shared.SequenceEqual([1]), team);
        tags.Heard.AddRange(shared);
        tags.HasSession = true;
        check("team: a report reaches a job once", JobBoard.Team(tags, [levels, sheets, tags], out var none) == "" && none.Count == 0, "");
        var teamBoard = new JobBoard([]);
        teamBoard.Start("Read grids", "Read the grids", t0, force: true, out _);
        teamBoard.Start("Tag walls", "Tag the walls", t0, force: true, out _);
        var starting = teamBoard.ToStart(1, t0);
        check("team: the board adds the team to a starting job's message", starting.Count == 1 && starting[0].Message.Text.Contains("From the team") && starting[0].Message.Text.Contains("Tag the walls"), starting.Count > 0 ? starting[0].Message.Text : "");
        check("team: the job list says how many workers are free", teamBoard.Describe(3).Contains("1 worker(s) free now"), teamBoard.Describe(3));

        // In the strip: several jobs handed out at once are a huddle; a report passed on is a call between desks.
        var crew = new CrewPlan();
        crew.Update([], t0);
        e = crew.Update([Job(1, JobBoard.Running), Job(2, JobBoard.Running)], t0.AddSeconds(1), null, 3);
        check("crew: two jobs at once start with a huddle", Cues(e) == "Huddle:0,Arrive:1,Arrive:2", Cues(e));
        var done1 = Job(1, JobBoard.Done);
        done1.Worker = 0;
        var heard = Job(3, JobBoard.Running);
        heard.Heard.Add(1);
        e = crew.Update([done1, Job(2, JobBoard.Running), heard], t0.AddSeconds(2), null, 3);
        check("crew: a job given another's report gets a call from that desk", e.Any(x => x.Cue == CrewCue.Share && x.Number == 3 && x.From == 0), Cues(e));
    }
}
