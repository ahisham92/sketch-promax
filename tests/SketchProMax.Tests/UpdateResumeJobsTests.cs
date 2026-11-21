using System.Text.Json;
using SketchProMax.Chat;

/// <summary>
/// Jobs carry on by themselves after an in-session update (21 Nov 2026, the owner: "when it updates it should continue
/// jobs automatically"): why each job is paused, jobs waiting to start kept, the handed-over chat found again, and the
/// chats working in the background handed over too. No Revit here.
/// </summary>
internal static class UpdateResumeJobsTests
{
    public static void Run(Action<string, bool, string> check)
    {
        JobPause.Reset();
        JobPause.StoreFile = null;
        var now = new DateTime(2026, 11, 21, 17, 17, 28, DateTimeKind.Utc);

        // ---- Why a job is paused, and which carry on by themselves
        var jobs = new List<ChatJob>();
        var board = new JobBoard(jobs);
        var a = board.Start("WE clashes", "Find clashes with the WE model", now, true, out _)!;
        var b = board.Start("Pile heads", "Check every pile head", now, true, out _)!;
        var c = board.Start("Old grading", "Grade the strips", now, true, out _)!;
        var waiting = board.Start("Existing model", "Clashes with the existing model", now, true, out _)!;
        foreach (var j in new[] { a, b, c }) j.State = JobBoard.Running;
        JobPause.Pause(JobPause.ForUpdate, now);
        board.Pause(a.Number, now);
        board.Pause(b.Number, now, JobPause.ForUpdate);
        board.Pause(c.Number, now, JobPause.ForUser);
        check("update resume: a job paused while the update pause is on is paused for the update", a.PausedBy == JobPause.ForUpdate && a.PausedAt == now, a.PausedBy);
        check("update resume: its note says it carries on by itself", a.Items[^1].Text == JobBoard.PausedForUpdateNote, a.Items[^1].Text);
        check("update resume: by itself, only the jobs paused for the update carry on; the user's waits for Continue",
            board.PausedJobs(forUpdateOnly: true).Select(j => j.Number).SequenceEqual([a.Number, b.Number]) && board.PausedJobs().Count == 3,
            string.Join(",", board.PausedJobs(forUpdateOnly: true).Select(j => j.Number)));
        var legacy = new ChatJob { Number = 90, Key = "legacy000000", Title = "Paused by 0.35.104", State = JobBoard.Stopped, Paused = true };
        check("update resume: a job paused by a version that kept no reason counts as the update's (the first update into this version)",
            new JobBoard([legacy]).PausedJobs(forUpdateOnly: true).Count == 1, "");
        JobPause.Reset();

        // ---- Opened again by the newer version: nothing lost
        var conv = new Conversation { Key = "central.rvt", Title = "TIN-SB", Id = "c1", Jobs = jobs };
        var back = JsonSerializer.Deserialize<Conversation>(JsonSerializer.Serialize(conv))!;
        var running = back.Jobs.First(j => j.Number == a.Number);
        running.State = JobBoard.Running; running.Paused = false; running.PausedBy = "";
        var reloaded = new JobBoard(back.Jobs);
        reloaded.AfterReload(now, afterUpdate: true);
        check("update resume: the reason is saved with the chat", back.Jobs.First(j => j.Number == b.Number).PausedBy == JobPause.ForUpdate, "");
        check("update resume: a job waiting to start still waits after the update (it used to be stopped)",
            back.Jobs.First(j => j.Number == waiting.Number).State == JobBoard.Queued, back.Jobs.First(j => j.Number == waiting.Number).State);
        check("update resume: a job cut off at work is paused for the update, not stopped",
            running is { State: JobBoard.Stopped, Paused: true, PausedBy: JobPause.ForUpdate }, running.State + " " + running.PausedBy);
        var restart = JsonSerializer.Deserialize<Conversation>(JsonSerializer.Serialize(conv))!;
        new JobBoard(restart.Jobs).AfterReload(now);
        check("update resume: opened after a restart (not an update), waiting jobs stop as before",
            restart.Jobs.First(j => j.Number == waiting.Number) is { State: JobBoard.Stopped, Paused: false }, "");

        // ---- A chat opened later (it wasn't up when the update went in): recent update-paused jobs only
        check("update resume: a chat opened later carries on its jobs paused for the update, not the user's",
            reloaded.LeftPausedByUpdate(now.AddMinutes(5)).Select(j => j.Number).OrderBy(n => n).SequenceEqual(new[] { a.Number, b.Number }), "");
        check("update resume: not when the pause is long gone", reloaded.LeftPausedByUpdate(now + JobBoard.UpdateResumeWithin).Count == 0, "");
        reloaded.Message(b.Number, JobPause.CarryOn, now);
        check("update resume: sent on, it waits to start with no reason left", back.Jobs.First(j => j.Number == b.Number) is { State: JobBoard.Queued, Paused: false, PausedBy: "" }, "");

        // ---- The chat handed over: found again even when it belongs to another model's history
        var root = Path.Combine(Path.GetTempPath(), "spm-update-resume-" + Guid.NewGuid().ToString("N"));
        ChatStore.RootOverride = root;
        try
        {
            const string chatKey = @"\\server\Models\TIN-SB.rvt", docKey = @"C:\Local\TIN-SB_detached.rvt";
            var shown = ChatStore.New(chatKey, "TIN-SB");
            shown.Items.Add(new ChatItem { Kind = "user", Text = "Final checks", Time = now });
            shown.Jobs = [new ChatJob { Number = 1, Key = "aaaaaaaaaaaa", Title = "Clashes", State = JobBoard.Stopped, Paused = true, PausedBy = JobPause.ForUpdate }];
            ChatStore.Save(shown);
            var other = ChatStore.New(docKey, "TIN-SB_detached");
            other.Items.Add(new ChatItem { Kind = "user", Text = "Another chat of the detached model", Time = now });
            ChatStore.Save(other);
            var away = ChatStore.New(docKey, "TIN-SB_detached");
            away.Items.Add(new ChatItem { Kind = "user", Text = "Background work", Time = now });
            away.Jobs = [new ChatJob { Number = 1, Key = "bbbbbbbbbbbb", Title = "Sheets", State = JobBoard.Queued }];
            ChatStore.Save(away);

            ChatHandover.Leave(shown, "", [], paneKey: docKey, paneTitle: "TIN-SB_detached", away: [away.File, shown.File, ""]);
            var left = ChatHandover.Take();
            check("update resume (21 Nov): the chat continued from another model's history comes back, not the model's latest",
                ChatHandover.Open(left, docKey, "TIN-SB_detached").File == shown.File, "");
            check("update resume: the background chats are handed over (not the shown one twice, nothing empty)",
                left is { Away.Count: 1 } && left.Away[0] == away.File && ChatHandover.OpenAway(left).Single().Jobs[0].State == JobBoard.Queued, "");
            var fromOld = new ChatHandoverState { File = shown.File, Key = chatKey, Title = "TIN-SB", At = DateTime.UtcNow };
            check("update resume: from a version that didn't say the pane's model, a chat with jobs to carry on still comes back",
                ChatHandover.Open(fromOld, docKey, "TIN-SB_detached").File == shown.File, "");
            shown.Jobs = [];
            ChatStore.Save(shown);
            check("update resume: ...but one with nothing to carry on opens the model's own chat, as before",
                ChatHandover.Open(fromOld, docKey, "TIN-SB_detached").File != shown.File, "");
        }
        finally
        {
            ChatStore.RootOverride = null;
            AppDomain.CurrentDomain.SetData(ChatHandover.DataKey, null);
            try { Directory.Delete(root, true); } catch (Exception) { }
        }
    }
}
