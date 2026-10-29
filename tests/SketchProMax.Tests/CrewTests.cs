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

        // Teammates work together: one job asks another for what it found, or tells it something, and walks over to its desk.
        var mates = new JobBoard([]);
        var grids = mates.Start("Read grids", "Read the grids and levels", t0, force: true, out _)!;
        var walls = mates.Start("Tag walls", "Tag the walls on every level", t0, force: true, out _)!;
        grids.State = JobBoard.Running;
        grids.Items.Add(new ChatItem { Kind = "assistant", Text = "Found grids A-F and 1-8. Levels are L1 0 mm and L2 3500 mm." });
        grids.Now = "Running: Reading levels";
        walls.State = JobBoard.Running;
        var answer = mates.Ask(walls.Key, grids.Number, "the level heights?", forward: false, t0);
        check("teamwork: a job asking another gets what it found so far", answer is not null && answer.Contains("L2 3500 mm") && answer.Contains("Doing now: Running: Reading levels") && walls.Visited.SequenceEqual([grids.Number]), answer ?? "");
        check("teamwork: the asker's view notes the question", walls.Items.Last().Text.StartsWith("Asked job 1", StringComparison.Ordinal), walls.Items.Last().Text);
        check("teamwork: a job can't ask itself or a missing job", mates.Ask(walls.Key, walls.Number, "x", false, t0) is null && mates.Ask(walls.Key, 9, "x", false, t0) is null, "");
        mates.Ask(walls.Key, grids.Number, "the grid ids?", forward: true, t0);
        check("teamwork: forward passes the question on, marked as from the teammate", grids.Inbox.Last().StartsWith(JobBoard.TeamSays + walls.Number, StringComparison.Ordinal) && grids.Items.Last().State == JobBoard.FromTeam + walls.Number, grids.Inbox.LastOrDefault() ?? "");
        grids.State = JobBoard.Done;
        grids.Summary = "Grids A-F, 1-8; levels L1, L2.";
        check("teamwork: a finished job answers with its report", JobBoard.Findings(grids).Contains("Its report: Grids A-F"), JobBoard.Findings(grids));
        check("teamwork: telling a finished job something reopens it", mates.Tell(walls.Key, grids.Number, "Level 3 is at 7000 mm", t0) == JobBoard.Delivery.Reopened && grids.State == JobBoard.Queued, grids.State);
        // A message to a busy job reaches it with its next step, not at the end of its run.
        var busy = new JobBoard([]);
        var tagger = busy.Start("Tag walls", "Tag the walls on L1-3", t0, force: true, out _)!;
        tagger.State = JobBoard.Running;
        check("aside: a message to a working job goes with its next step", busy.Message(tagger.Number, "use the Fire Rating tag", t0, fromUser: true) == JobBoard.Delivery.Soon
            && tagger.Inbox.Count == 0 && tagger.Asides.Count == 1 && tagger.Items.Last().Name.StartsWith(JobBoard.AsidePrefix, StringComparison.Ordinal), "");
        var block = busy.TakeAsides(tagger.Key, "tag_elements", t0);
        check("aside: the step's answer carries it, with: fix now if it changes the work, else after", block is not null && block.Contains("use the Fire Rating tag") && block.Contains("If it is separate new work")
            && tagger.Asides.Count == 0 && tagger.Items.Last().Text.StartsWith("Arrived with its step (tag_elements)", StringComparison.Ordinal), block ?? "");
        check("aside: nothing waits, nothing added", busy.TakeAsides(tagger.Key, "x", t0) is null, "");
        busy.Message(tagger.Number, "also dimension the grids", t0, fromUser: true);
        var later = tagger.Asides[0].Id;
        check("aside: After this job keeps it for the end of the run", busy.SetAside(tagger.Number, later, now: false, t0) && tagger.Inbox.Count == 1 && tagger.Asides.Count == 0, "");
        check("aside: a tap after it arrived does nothing", !busy.SetAside(tagger.Number, later, now: true, t0), "");
        busy.Message(tagger.Number, "no, the other tag", t0, fromUser: true);
        busy.SetAside(tagger.Number, tagger.Asides[0].Id, now: true, t0);
        check("aside: Apply now says to apply it now", busy.TakeAsides(tagger.Key, "x", t0)!.Contains("Apply it now"), "");
        busy.Message(tagger.Number, "and level 4", t0, fromUser: true);
        var next = busy.RunEnded(tagger, true, "Tagged.", t0);
        check("aside: one that didn't arrive goes as the next message when the run ends", next is not null && next.Text.Contains("and level 4") && next.Text.Contains("also dimension the grids") && tagger.Asides.Count == 0, next?.Text ?? "");
        var idle = busy.Start("Sheets", "Make sheets", t0, force: true, out _)!;
        check("aside: a job not working gets messages as before", busy.Message(idle.Number, "A1 size", t0, fromUser: true) == JobBoard.Delivery.Queued && idle.Asides.Count == 0, "");

        var visits = new CrewPlan();
        ChatJob Seated(int n, int worker) { var j = Job(n, JobBoard.Running); j.Worker = worker; return j; }
        var one = Seated(1, 0);
        var two = Seated(2, 1);
        visits.Update([one, two], t0, null, 3);
        two.Visited.Add(1);
        e = visits.Update([one, two], t0.AddSeconds(1), null, 3);
        check("teamwork: the asker walks to the teammate's desk", e.Any(x => x.Cue == CrewCue.Visit && x.Number == 2 && x.From == visits.Seats.First(s => s.Number == 1).Slot), Cues(e));
        e = visits.Update([one, two], t0.AddSeconds(2), null, 3);
        check("teamwork: the talk uses the asker's question", CrewPlan.Asking(walls) == "the grid ids?", CrewPlan.Asking(walls));
        check("teamwork: a visit to tell something has no question", CrewPlan.Asking(Job(9, JobBoard.Running)) == "", "");
        check("teamwork: a visit plays once", e.All(x => x.Cue != CrewCue.Visit), Cues(e));
        one.Now = "Running: Tag walls on Level 2";
        one.Items.Add(new ChatItem { Kind = "assistant", Text = "I read the walls. **Found 42 walls**, now checking their types." });
        visits.Update([one, two], t0.AddSeconds(3), null, 3);
        var seat1 = visits.Seats.First(s => s.Number == 1);
        check("crew: the line under a working worker says what it is doing", CrewPlan.Status(seat1, seat1.Slot) == "Tag walls on Level 2", CrewPlan.Status(seat1, seat1.Slot));
        check("crew: the thought bubble is its latest words", seat1.Thought == "Found 42 walls, now checking their types", seat1.Thought);

        // The crew's numbers: dollars rise per answer, time saved by working side by side, a job counts once, ties have no leader.
        double seen = 0;
        var r1 = CrewStats.Rise(ref seen, 0.10);
        var r2 = CrewStats.Rise(ref seen, 0.25);
        var r3 = CrewStats.Rise(ref seen, 0.05);
        check("stats: dollars are the rise of the process total, again from zero in a new process", Math.Abs(r1 - 0.10) < 1e-9 && Math.Abs(r2 - 0.15) < 1e-9 && Math.Abs(r3 - 0.05) < 1e-9, $"{r1} {r2} {r3}");
        CrewRecord Run(int slot, int job, int from, int to, bool ok = true) => new() { Slot = slot, Chat = "c", Job = job, Start = t0.AddSeconds(from), End = t0.AddSeconds(to), Ok = ok, In = 100, Out = 10, Usd = 0.5 };
        List<CrewRecord> runs = [Run(0, 1, 0, 60), Run(1, 2, 30, 90), Run(2, 3, 200, 260, ok: false), Run(2, 3, 300, 320)];
        var (used, saved) = CrewStats.Time(runs);
        check("stats: time used adds up, time saved is the overlap", used == 200 && saved == 30, $"{used} {saved}");
        var kpis = CrewStats.Workers(runs);
        var third = kpis.First(k => k.Slot == 2);
        check("stats: a job run twice counts once and is judged by its last run", third.Jobs == 1 && third.Done == 1 && third.Seconds == 80 && third.Usd == 1.0, $"{third}");
        check("stats: a tie at the top has no leader", CrewStats.Leader(runs) == -1, "");
        runs.Add(Run(0, 4, 400, 430));
        check("stats: the most jobs done well leads", CrewStats.Leader(runs) == 0, "");
        runs.Add(new CrewRecord { Role = CrewRecord.Coordinator, Start = t0, End = t0.AddSeconds(5), In = 50, Usd = 0.2 });
        runs.Add(new CrewRecord { Role = CrewRecord.Applier, Start = t0, End = t0.AddSeconds(3) });
        var lead = CrewStats.CoordinatorKpi(runs);
        check("stats: the coordinator's turns and the jobs it handed out", lead.Turns == 1 && lead.JobsHandedOut == 4 && lead.In == 50, $"{lead}");
        check("stats: the applier's changes", CrewStats.ApplierKpi(runs) == (1, 3.0), "");
        check("stats: the period keeps only records ending in it", CrewStats.Since(runs, t0.AddSeconds(100)).Count == 3, "");
        // A cheap coordinator: its own turns run light on one model whatever the request; the jobs keep their kinds.
        var lead1 = ChatAuto.Pick("Check the BEP against the model and cost it", "auto", "auto", kind: ChatAuto.Coordinating);
        var lead2 = ChatAuto.Pick("[Job report 1] done", "auto", "auto", kind: ChatAuto.Coordinating);
        check("coordinator: runs on Sonnet, low effort, for a hard request and a job report alike", lead1.Model == "sonnet" && lead1.Effort == "low" && lead2.Model == lead1.Model && lead2.Effort == lead1.Effort, lead1.Label + " / " + lead2.Label);
        var leadSet = ChatAuto.Pick("anything", "auto", "auto", new Dictionary<string, string> { [ChatAuto.Coordinating] = "haiku" }, null, ChatAuto.Coordinating);
        check("coordinator: Settings picks its model", leadSet.Model == "haiku" && leadSet.Effort == "low", leadSet.Label);
        check("coordinator: a model picked by hand still sticks", ChatAuto.Pick("x", "opus", "high", kind: ChatAuto.Coordinating).Label == ChatAuto.Pick("x", "opus", "high").Label, "");
        check("coordinator: the job it hands out keeps the hard kind", ChatAuto.Pick("Check the BEP", "auto", "auto", kind: ChatAuto.Hard).Model == "opus", "");
        check("coordinator: its chat shrinks sooner (short memory)", ChatRecall.ShouldAutoShrink(70_000, 25_000, coordinator: true) && !ChatRecall.ShouldAutoShrink(70_000, 25_000), "");
        // A Simple chat talks to one worker directly: the least recently busy one takes it, it counts in the worker's numbers
        // but not as a job the coordinator handed out, and the chat says when the worker is overloaded or the topics mix.
        List<CrewRecord> recent = [Run(0, 1, 0, 60), Run(1, 2, 100, 160)];
        check("direct: the worker who never worked takes a new Simple chat", CrewStats.NextWorker(recent, 3) == 2, "");
        recent.Add(Run(2, 3, 200, 260));
        check("direct: then the one who worked least recently", CrewStats.NextWorker(recent, 3) == 0, "");
        check("direct: only the workers on the team", CrewStats.NextWorker(recent, 1) == 0 && CrewStats.NextWorker([], 0) == 0, "");
        var direct = Run(1, 1, 300, 330);
        direct.Chat = "simple";
        direct.Direct = true;
        recent.Add(direct);
        check("direct: a Simple chat's answer counts for the worker, not as a job handed out", CrewStats.Workers(recent).First(k => k.Slot == 1).Jobs == 2 && CrewStats.CoordinatorKpi(recent).JobsHandedOut == 3, "");
        var given = new List<string>();
        check("direct: a short chat on one topic needs no advice", DirectAdvice.Check(["Tag the walls on level 2", "Now tag the walls on level 3 too"], 20_000, 15_000, given) is null, "");
        check("direct: a heavy chat means the worker is overloaded", DirectAdvice.Check(["Tag the walls on level 2"], 80_000, 15_000, given) == DirectAdvice.Overloaded, "");
        given.Add(DirectAdvice.Overloaded);
        check("direct: each advice shows once", DirectAdvice.Check(["Tag the walls on level 2"], 90_000, 15_000, given) is null, "");
        List<string> mixed = ["Tag the walls on level 2 with fire ratings", "Export the sheets A101 to A110 as PDF files", "Estimate the concrete volume of the pile caps"];
        check("direct: three unrelated topics in a row are mixed", DirectAdvice.IsMixed(mixed) && DirectAdvice.Check(mixed, 20_000, 15_000, []) == DirectAdvice.Mixed, "");
        check("direct: the same topic carried on is not mixed", !DirectAdvice.IsMixed(["Tag the walls on level 2 with fire ratings", "Tag the walls on level 3 with fire ratings", "Fire ratings for walls on level 4 too"]), "");
        check("direct: a list of three tasks is several", DirectAdvice.IsSeveral("Please:\n1. rename the sheets\n2. tag the doors\n3. export the schedules") && DirectAdvice.IsSeveral("rename the sheets; tag the doors; export the schedules"), "");
        check("direct: one task with a semicolon is not several", !DirectAdvice.IsSeveral("Rename the sheets; keep the numbers"), "");
        check("direct: the advice names the worker and points to the coordinator", DirectAdvice.Say(DirectAdvice.Mixed, "Nova").Contains("Nova") && DirectAdvice.Say(DirectAdvice.Overloaded, "Nova").Contains("With coordinator"), "");
        check("stats: friendly numbers", CrewStats.Tokens(1234) == "1.2k" && CrewStats.Tokens(2_100_000) == "2.1M" && CrewStats.Dollars(0.4) == "$0.40" && CrewStats.Duration(45) == "45 s" && CrewStats.Duration(7500) == "2 h 5 min",
            CrewStats.Tokens(1234) + " " + CrewStats.Dollars(0.4) + " " + CrewStats.Duration(7500));
    }
}
