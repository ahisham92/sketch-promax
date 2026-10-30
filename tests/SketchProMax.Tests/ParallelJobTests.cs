using System.Text.Json;
using SketchProMax.Bridge;
using SketchProMax.Chat;

/// <summary>The chat's parallel jobs: opening and routing, the limit, messages, Stop, the report, the turn queue and saving.</summary>
internal static class ParallelJobTests
{
    public static void Run(Action<string, bool, string> check)
    {
        var now = new DateTime(2026, 10, 21, 9, 0, 0, DateTimeKind.Utc);
        var jobs = new List<ChatJob>();
        var board = new JobBoard(jobs);

        // ---- Opening and routing
        var sheets = board.Start("Sheets for levels 1-5", "Create a sheet for each of levels 1 to 5 with its floor plan.", now, false, out _);
        var walls = board.Start("Wall warnings", "Check the walls for warnings and list them.", now.AddSeconds(1), false, out var notSimilar);
        check("jobs: separate work opens numbered jobs, waiting to start", sheets is { Number: 1, State: JobBoard.Queued } && walls is { Number: 2 } && notSimilar is null
            && sheets.Key.Length == 12 && sheets.Key != walls.Key && sheets.Items.Count == 1 && sheets.Items[0].Kind == "user" && sheets.Items[0].State == JobBoard.FromCoordinator, $"{sheets?.Number} {walls?.Number} {notSimilar?.Number}");
        var follow = board.Start("Level 6", "also add level 6", now, false, out var similar);
        check("jobs: a follow-up is routed to the job it belongs to, not opened", follow is null && similar == sheets && jobs.Count == 2, similar?.Title ?? "none");
        check("jobs: routing scores", JobRouting.Score("also add level 6", sheets!) >= JobRouting.Threshold && JobRouting.Score("also add level 6", walls!) == 0
            && JobRouting.Best(jobs, "check the roof drainage") is null && JobRouting.Best(jobs, "now the wall warnings on level 2 too") == walls
            && JobRouting.Score("6", sheets!) == 0, JobRouting.Score("also add level 6", sheets!).ToString("0.00"));
        check("jobs: words compared without plural s or filler", JobRouting.Words("Also add the Levels and Sheets, 6").SetEquals(["level", "sheet", "6"])
            && JobRouting.Words("Copies").Contains("copy"), string.Join(",", JobRouting.Words("Also add the Levels and Sheets, 6")));
        var forced = board.Start("Level 6 sheet", "also add level 6", now.AddSeconds(2), true, out _);
        check("jobs: new=true opens it anyway, titled", forced is { Number: 3, Title: "Level 6 sheet" } && jobs.Count == 3, forced?.Title ?? "");
        var untitled = new JobBoard([]).Start("", "Tag all the beams on level 2 in their plans", now, true, out _);
        check("jobs: a job without a title gets one from its task", untitled?.Title == "Tag all the beams on level 2 in their plans", untitled?.Title ?? "");

        // ---- The limit: oldest first, the next when one ends
        check("jobs: limit clamps to 1-6, 3 by default", JobBoard.ClampLimit(0) == 3 && JobBoard.ClampLimit(9) == 6 && JobBoard.ClampLimit(2) == 2 && JobBoard.DefaultLimit == 3, "");
        var started = board.ToStart(2, now);
        check("jobs: no more than the limit start, oldest first", started.Count == 2 && started[0].Job == sheets && started[1].Job == walls
            && board.ActiveCount == 2 && forced!.State == JobBoard.Queued && board.OpenCount == 3, string.Join(",", started.Select(s => s.Job.Number)));
        check("jobs: a new job's first message is its whole task", started[0].Message.Text.StartsWith("Your job (number 1, \"Sheets for levels 1-5\")") && started[0].Message.Text.Contains("floor plan")
            && started[0].Message.Files.Count == 0, started[0].Message.Text);
        check("jobs: nothing more starts while the limit is reached", board.ToStart(2, now).Count == 0, "");

        // ---- Messages, run ends, Stop
        check("jobs: a message to a running job waits for its run to end", board.Message(1, "also add level 6", now) == JobBoard.Delivery.Queued && sheets!.Inbox.Count == 1, "");
        check("jobs: a message to no such job", board.Message(9, "hi", now) == JobBoard.Delivery.NotFound && board.Message(1, "  ", now) == JobBoard.Delivery.NotFound, "");
        check("jobs: the user's message in the job view, with a file", board.Message(1, "", now, fromUser: true, ["C:\\a\\plan.png"]) == JobBoard.Delivery.Queued
            && sheets!.Items[^1] is { Kind: "user", State: "", Name: "plan.png" } && sheets.Items[^1].Files.SequenceEqual(["C:\\a\\plan.png"])
            && sheets.Items[^2].State == JobBoard.FromCoordinator, sheets!.Items[^1].Name);
        var next = board.RunEnded(sheets!, true, "Made 5 sheets.", now);
        check("jobs: a run that ends with messages waiting carries on with them, saying who sent each", next is not null
            && next.Text == "The main chat says: also add level 6\n\nThe user says: (see the attached files)" && next.Files.SequenceEqual(["C:\\a\\plan.png"])
            && sheets!.State == JobBoard.Running && sheets.Inbox.Count == 0 && sheets.InboxFiles.Count == 0, next?.Text ?? "null");
        check("jobs: a run that ends is done with its summary", board.RunEnded(sheets!, true, "  Made 6 sheets, A-101 to A-106.  ", now.AddMinutes(2)) is null
            && sheets!.State == JobBoard.Done && sheets.Summary == "Made 6 sheets, A-101 to A-106." && sheets.Ended == now.AddMinutes(2), sheets!.State);
        check("jobs: the next starts when one ends", board.ToStart(2, now).Select(s => s.Job).SequenceEqual([forced!]), "");
        check("jobs: a failed run says so", board.RunEnded(forced!, false, "It couldn't open the view.", now) is null && forced!.State == JobBoard.Failed
            && JobBoard.StateLabel(forced) == "Ended with a problem", forced!.State);
        check("jobs: a message to a finished job opens it again with the message", board.Message(1, "rename them to S-101 on", now, fromUser: true) == JobBoard.Delivery.Reopened
            && sheets!.State == JobBoard.Queued && sheets.Summary == "", sheets!.State);
        sheets!.HasSession = true;
        var resumed = board.ToStart(3, now).FirstOrDefault(s => s.Job == sheets);
        check("jobs: a job with a session starts with the messages waiting for it", resumed.Message?.Text == "The user says: rename them to S-101 on", resumed.Message?.Text ?? "");
        var restarted = board.Restart(sheets, resumed.Message!);
        check("jobs: a job whose session is gone starts from its task, the message after it", !sheets.HasSession && restarted.Text.StartsWith("Your job (number 1")
            && restarted.Text.EndsWith("Since then:\n\nThe user says: rename them to S-101 on") && sheets.Inbox.Count == 0, restarted.Text);
        check("jobs: a resumed job with nothing waiting carries on", JobBoard.NextMessage(new ChatJob { HasSession = true }).Text == JobBoard.CarryOn, "");
        check("jobs: Stop stops a running job and tells which state it was in", board.Stop(2, now) == JobBoard.Running && walls!.State == JobBoard.Stopped
            && walls.Items[^1].Kind == "note" && board.Stop(2, now) == JobBoard.Stopped && board.Stop(42, now) is null, walls!.State);
        check("jobs: a stopped job's run end changes nothing", board.RunEnded(walls!, true, "late", now) is null && walls!.State == JobBoard.Stopped && walls.Summary == "", "");

        // ---- Waiting its turn, words on the card, the list, the report
        check("jobs: a change waiting its turn shows on the card", board.SetWaiting(sheets.Key, 3) && sheets.State == JobBoard.Waiting
            && JobBoard.StateLabel(sheets) == "Waiting to apply (2 ahead)" && board.SetWaiting(sheets.Key, null) && sheets.State == JobBoard.Running
            && !board.SetWaiting(walls!.Key, 1) && !board.SetWaiting("nope", 1), JobBoard.StateLabel(sheets));
        // What each job does now, for its card and its animation: thinking and reading in parallel, applying one at a time.
        var seen = new List<string>();
        foreach (var (activity, ahead) in new[] { (JobBoard.Reading, 0), (JobBoard.WaitingToApply, 2), (JobBoard.Applying, 0), ("", 0) })
        {
            board.SetActivity(sheets.Key, activity, ahead);
            seen.Add(JobBoard.ActivityOf(sheets) + "/" + JobBoard.StateLabel(sheets));
        }
        check("jobs: a job shows when it reads, waits to apply, applies and thinks", seen.SequenceEqual(["reading/Reading the model", "waiting/Waiting to apply (1 ahead)",
            "applying/Applying changes", "thinking/Thinking"]) && sheets.State == JobBoard.Running && !board.SetActivity(sheets.Key, "")
            && !board.SetActivity(walls!.Key, JobBoard.Applying) && JobBoard.ActivityOf(walls!) == "", string.Join(", ", seen));
        var labels = new[] { JobBoard.Queued, JobBoard.Running, JobBoard.Waiting, JobBoard.Done, JobBoard.Failed, JobBoard.Stopped }
            .Select(s => JobBoard.StateLabel(new ChatJob { State = s })).ToList();
        check("jobs: every state has words, none name the model", labels.SequenceEqual(["Waiting to start", "Thinking", "Waiting to apply", "Done", "Ended with a problem", "Stopped"])
            && labels.All(l => !l.Contains("Claude")), string.Join(", ", labels));
        var list = board.Describe(3);
        check("jobs: the list gives each job's number, title, state and task", list.Contains("Job 1 \"Sheets for levels 1-5\": thinking") && list.Contains("Job 2 \"Wall warnings\": stopped")
            && list.Contains("up to 3 run at once") && new JobBoard([]).Describe(4).StartsWith("No jobs"), list);
        var finished = new ChatJob { Number = 1, Title = sheets.Title, State = JobBoard.Done, Summary = "Made 6 sheets." };
        var report = JobBoard.Report([finished, forced!], 1);
        check("jobs: the report for the main chat", report.StartsWith("[Job report from Sketch ProMax, not typed by the user]") && report.Contains("Job 1 \"Sheets for levels 1-5\" finished:\nMade 6 sheets.")
            && report.Contains("Job 3 \"Level 6 sheet\" ended with a problem") && report.Contains("1 other job(s) are still open") && !report.Contains("Claude"), report);

        // ---- The job's own chat (the job view)
        var log = new JobBoard([]);
        var long1 = log.Start("Long", "x", now, true, out _)!;
        for (var i = 0; i < JobBoard.MaxItems + 20; i++) log.AddItem(long1, new ChatItem { Kind = "tool", Name = "list_views", Text = "Step " + i, State = "done" }, now);
        var big = log.AddItem(long1, new ChatItem { Kind = "assistant", Text = new string('a', JobBoard.MaxLine + 50) }, now);
        check("jobs: its chat keeps the latest items, each capped", long1.Items.Count == JobBoard.MaxItems && long1.Items[^2].Text == "Step 319" && big.Text.Length == JobBoard.MaxLine + 1
            && long1.Now.Length <= 120 && big.Time == now, long1.Items.Count.ToString());
        var shown = log.ItemsOf(long1.Number);
        log.AddNote(long1, "later", now);
        check("jobs: the view's copy of its items doesn't change under it", shown.Count == JobBoard.MaxItems && log.ItemsOf(long1.Number)[^1].Text == "later" && log.ItemsOf(99).Count == 0, "");
        var legacy = JsonSerializer.Deserialize<ChatJob>("""{"Number":4,"Key":"aaaaaaaaaaaa","Title":"Old","Log":[{"Kind":"you","Text":"Do it"},{"Kind":"step","Text":"List views","State":"failed"},{"Kind":"text","Text":"Done."},{"Kind":"note","Text":"Stopped."}]}""")!;
        new JobBoard([legacy]);
        check("jobs: a job saved before job views gets its lines as its chat", legacy.Log is null && legacy.Items.Select(i => i.Kind).SequenceEqual(["user", "note", "assistant", "note"])
            && legacy.Items[0].State == JobBoard.FromCoordinator && legacy.Items[1].Text == "Step: List views (failed)" && !JsonSerializer.Serialize(legacy).Contains("\"Log\""),
            string.Join(",", legacy.Items.Select(i => i.Kind)));

        // ---- Thread safety: jobs opened from several threads at once get one number each
        var many = new JobBoard([]);
        Parallel.For(0, 40, i => many.Start("Task " + i, "Do thing number " + i + " on sheet zz" + i, now, true, out _));
        var numbers = many.Snapshot().Select(j => j.Number).OrderBy(n => n).ToList();
        check("jobs: opened from several threads, each gets its own number", numbers.SequenceEqual(Enumerable.Range(1, 40)), string.Join(",", numbers.Take(10)));

        // ---- Saved with the chat; running ones come back stopped
        var conv = new Conversation { Key = "m", Title = "M", Id = "c1", Jobs = jobs };
        var json = JsonSerializer.Serialize(conv);
        var back = JsonSerializer.Deserialize<Conversation>(json)!;
        new JobBoard(back.Jobs).AfterReload(now);
        check("jobs: saved with the chat and read back; running and waiting ones stopped", back.Jobs.Count == 3 && back.Jobs[0].State == JobBoard.Stopped
            && back.Jobs[0].Items.Count == sheets.Items.Count + 1 && back.Jobs[0].Items[0].State == JobBoard.FromCoordinator && back.Jobs[2].State == JobBoard.Failed && back.Jobs[0].Key == sheets.Key, json.Length.ToString());
        var old = JsonSerializer.Deserialize<Conversation>("""{"Key":"m","Items":[]}""")!;
        check("jobs: a chat saved before jobs has none", old.Jobs is { Count: 0 }, "");

        var folder = Path.Combine(Path.GetTempPath(), "spm-jobs-" + Guid.NewGuid().ToString("N"));
        try
        {
            check("jobs: sessions.json: none at first", JobSessions.Get(folder, "abc") is null, "");
            JobSessions.Set(folder, "abc", "s-1");
            JobSessions.Set(folder, "def", "s-2");
            JobSessions.Set(folder, "abc", "s-3");
            check("jobs: sessions.json keeps each job's session", JobSessions.Get(folder, "abc") == "s-3" && JobSessions.Get(folder, "def") == "s-2"
                && File.ReadAllText(Path.Combine(folder, "sessions.json")).Contains("\"def\":\"s-2\""), File.ReadAllText(Path.Combine(folder, "sessions.json")));
            File.WriteAllText(Path.Combine(folder, "sessions.json"), "{ not json");
            check("jobs: a damaged sessions.json reads as empty", JobSessions.Get(folder, "abc") is null, "");
        }
        finally { try { Directory.Delete(folder, true); } catch (IOException) { } }

        // ---- The turn queue
        var turns = new JobTurns();
        var moved = 0;
        turns.Changed += () => Interlocked.Increment(ref moved);
        var a = turns.Enter("j1", "create_sheets");
        var b = turns.Enter("j2", "create_views");
        var c = turns.Enter("j3", "rename_elements");
        check("turns: first come, first served", turns.Position(a) == 1 && turns.Position(b) == 2 && turns.Position(c) == 3
            && turns.Snapshot().Select(t => t.Job).SequenceEqual(["j1", "j2", "j3"]), "");
        var bWaits = turns.WaitAsync(b, TimeSpan.FromSeconds(5), CancellationToken.None);
        check("turns: the second waits while the first holds its turn", !bWaits.Wait(100), "");
        turns.Leave(a);
        check("turns: the next moves up when one ends", bWaits.Wait(2000) && bWaits.Result && turns.Position(b) == 1 && turns.Position(c) == 2 && turns.Position(a) == 0, "");
        turns.Leave(a); // twice is harmless
        using (var cancel = new CancellationTokenSource(50))
            check("turns: a caller that goes away leaves the queue", !turns.WaitAsync(c, TimeSpan.FromSeconds(5), cancel.Token).Result && turns.Position(c) == 0, "");
        check("turns: a wait that takes too long leaves the queue", !turns.WaitAsync(turns.Enter("j4", "x"), TimeSpan.FromMilliseconds(30), CancellationToken.None).Result
            && turns.Snapshot().Count == 1, string.Join(",", turns.Snapshot().Select(t => t.Job)));
        turns.Leave(b);
        var d = turns.Enter("j5", "x");
        check("turns: an empty queue gives the turn at once", turns.WaitAsync(d, TimeSpan.FromMilliseconds(10), CancellationToken.None).Result && moved >= 8, moved.ToString());
        check("turns: a read doesn't wait for a change that just got its turn", !turns.ChangeOverdue(TimeSpan.FromMinutes(1)), "");
        Thread.Sleep(30);
        check("turns: reads let a change go first once it has waited for Revit a while", turns.ChangeOverdue(TimeSpan.FromMilliseconds(10)), "");
        turns.Began(d);
        check("turns: reads no longer wait once the change got Revit", !turns.ChangeOverdue(TimeSpan.FromMilliseconds(10)), "");
        turns.Leave(d);
        check("turns: an empty line holds no read back", !turns.ChangeOverdue(TimeSpan.Zero), "");
        check("turns: Revit's breather is a tenth of the call, a quarter second to two seconds",
            JobTurns.Breather(TimeSpan.FromMilliseconds(100)) == TimeSpan.FromMilliseconds(250) && JobTurns.Breather(TimeSpan.FromSeconds(5)) == TimeSpan.FromMilliseconds(500)
            && JobTurns.Breather(TimeSpan.FromMinutes(5)) == TimeSpan.FromSeconds(2), "");
        check("turns: with nobody at the PC the breather is a twentieth of a second, however long the call",
            JobTurns.Breather(TimeSpan.FromMinutes(5), away: true) == TimeSpan.FromMilliseconds(50) && JobTurns.Breather(TimeSpan.FromMilliseconds(100), away: true) == TimeSpan.FromMilliseconds(50), "");
        {
            var away = new JobTurns();
            JobTurns.SinceInput = () => TimeSpan.FromMinutes(10);
            away.Rested(TimeSpan.FromMinutes(5));
            var awayLeft = away.RestLeft;
            var here = new JobTurns();
            JobTurns.SinceInput = () => TimeSpan.FromSeconds(5);
            here.Rested(TimeSpan.FromMinutes(5));
            JobTurns.SinceInput = () => TimeSpan.Zero;
            check("turns: the breather shrinks only after two minutes without mouse or keys", awayLeft <= TimeSpan.FromMilliseconds(50) && here.RestLeft > TimeSpan.FromSeconds(1), $"{awayLeft} {here.RestLeft}");
        }
        check("turns: no breather before any call", turns.RestLeft == TimeSpan.Zero, "");
        turns.Rested(TimeSpan.FromSeconds(10));
        check("turns: the next call waits for the breather after one ends", turns.RestLeft > TimeSpan.FromMilliseconds(800) && turns.RestLeft <= TimeSpan.FromSeconds(1), turns.RestLeft.ToString());

        // ---- Step rows for the main chat's job calls
        check("steps: chat_jobs in plain words", StepNames.Describe("chat_jobs", """{"action":"start","title":"Sheets for levels 1-5","task":"…"}""") == "Start a job: Sheets for levels 1-5"
            && StepNames.Describe("mcp__sketch-promax__chat_jobs", """{"action":"message","job":2,"text":"also level 6"}""") == "Message job 2"
            && StepNames.Describe("chat_jobs", """{"action":"stop","job":"3"}""") == "Stop job 3" && StepNames.Describe("chat_jobs", "{}") == "List the jobs"
            && StepNames.Title("chat_jobs") == "Jobs" && StepNames.ReadOnly("chat_jobs") && JobCardModel.Quiet("chat_jobs"),
            StepNames.Describe("chat_jobs", """{"action":"message","job":2}"""));
        check("settings: the job limit is on the Chat tab", SketchProMax.UI.SettingsLayout.Settings.TryGetValue("ChatJobLimit", out var tab) && tab == SketchProMax.UI.SettingsLayout.Chat, "");
    }
}
