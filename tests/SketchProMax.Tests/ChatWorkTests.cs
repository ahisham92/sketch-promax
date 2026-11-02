using SketchProMax.Chat;

/// <summary>
/// Chats that keep working while another is shown: each chat's coordinator and jobs reach their own board (ChatBoards),
/// whichever chat the pane shows.
/// </summary>
internal static class ChatWorkTests
{
    public static void Run(Action<string, bool, string> check)
    {
        var now = new DateTime(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc);
        var boards = new ChatBoards();
        var shownBoard = new JobBoard([]);
        var awayBoard = new JobBoard([]);
        var shownJob = shownBoard.Start("Sheets for levels 1-5", "Make sheets", now, true, out _)!;
        var awayJob = awayBoard.Start("Tag every wall", "Tag the walls", now, true, out _)!;
        var shown = JobBoard.ChatKey("aaa");
        var away = JobBoard.ChatKey("bbb");
        var changed = 0;
        boards.Set(shown, shownBoard, coordinator: true, null);
        boards.Set(away, awayBoard, coordinator: true, () => changed++);

        check("boards: the shown chat's own calls reach its board", boards.For(null, shown)?.Board == shownBoard, "");
        check("boards: a chat working in the background reaches its own board, not the shown one", boards.For(away, shown)?.Board == awayBoard, "");
        check("boards: a job's calls reach the board that has it, shown or not", boards.For(shownJob.Key, shown)?.Board == shownBoard && boards.For(awayJob.Key, shown)?.Board == awayBoard, "");
        check("boards: the background chat's change call is its own", boards.For(awayJob.Key, shown)?.Changed is { } call && Run(call) && changed == 1, changed.ToString());
        check("boards: a job's chat is found", boards.ChatOf(awayJob.Key) == away && boards.ChatOf("nope") == "", boards.ChatOf(awayJob.Key));
        check("boards: an unknown chat or job reaches nothing", boards.For(JobBoard.ChatKey("zzz"), shown) is null && boards.For("123456789012", shown) is null, "");
        check("boards: no chat shown, no board for the main chat", boards.For(null, "") is null, "");
        boards.Set(away, awayBoard, coordinator: false, null);
        check("boards: a kind change is kept", boards.For(away, shown) is { Coordinator: false }, "");
        boards.Remove(away);
        check("boards: a chat let go is no longer reached", boards.For(away, shown) is null && boards.For(awayJob.Key, shown) is null && boards.Count == 1, "");
        boards.Set("", awayBoard, true, null);
        check("boards: a chat without an id isn't kept", boards.Count == 1, "");
        check("boards: the caller of a background chat is its chat key", JobBoard.Caller(away, shown) == away && JobBoard.Caller(shown, shown) is null, "");

        // Leaving a chat that works: nothing stops; only the cap, or an old connector, keeps it open.
        check("leave: a working chat can be left under the cap", ChatWork.WhyNotLeave(ChatWork.Limit - 1, false) is null, "");
        var full = ChatWork.WhyNotLeave(ChatWork.Limit, false) ?? "";
        check("leave: at the cap, one line says so and stops nothing", full.Length > 0 && !full.Contains('\n') && full.Contains(ChatWork.Limit.ToString()) && !full.Contains("Claude"), full);
        var old = ChatWork.WhyNotLeave(0, cantAnswerAway: true) ?? "";
        check("leave: an answer an old connector can't carry on says how to update", old.Contains("Install.cmd") && !old.Contains("Claude"), old);

        // What the switcher and the history say.
        check("label: a working chat shows what it does", ChatWork.Label(true, "Tagging walls", "", false) == "Working · Tagging walls", ChatWork.Label(true, "Tagging walls", "", false));
        check("label: an Autopilot run says so", ChatWork.Label(true, "", "", true).StartsWith("Autopilot working", StringComparison.Ordinal), ChatWork.Label(true, "", "", true));
        check("label: finished and failed until opened", ChatWork.Label(false, "", ChatWork.Done, false).Contains("Finished") && ChatWork.Label(false, "", ChatWork.Failed, false).Contains("error"), "");
        check("finished line: names the chat and where to open it", ChatWork.FinishedLine("Doors", ChatWork.Done).Contains("“Doors” finished") && ChatWork.FinishedLine("Doors", ChatWork.Failed).Contains("error"), "");
        check("pill: plain with nothing in the background", ChatWork.PillText(0, 0, false) == "Chats", ChatWork.PillText(0, 0, false));
        check("pill: counts working and finished chats", ChatWork.PillText(2, 1, false) == "Chats · 2 working, 1 finished" && ChatWork.PillText(2, 1, true) == "Chats · 3", ChatWork.PillText(2, 1, false));
    }

    /// <summary>The pane: leaving a chat or starting a new one stops nothing, and the Autopilot hold is gone.</summary>
    public static void RunSources(string root, Action<string, bool, string> check)
    {
        string Read(string file) => File.ReadAllText(Path.Combine(root, "src", "SketchProMax", "Chat", file)).Replace("\r\n", "\n");
        var pane = Read("ChatPane.cs");
        var auto = Read("ChatPane.Autopilot.cs");
        var session = Read("ChatPane.Session.cs");
        var newChat = pane.Substring(pane.IndexOf("private void NewChat(", StringComparison.Ordinal));
        newChat = newChat.Substring(0, newChat.IndexOf("\n    }", StringComparison.Ordinal));
        check("switch source: New chat leaves the shown chat working, it doesn't stop it", newChat.Contains("LeaveShown(") && !newChat.Contains("StopBackground(") && !newChat.Contains("EndRuns("), newChat);
        check("switch source: no chat is held open by its Autopilot run", !auto.Contains("AutopilotHolds") && !pane.Contains("AutopilotHolds"), "");
        check("switch source: the cap is the only refusal for working chats", session.Contains("ChatWork.WhyNotLeave("), "");
        check("switch source: the Autopilot clock ticks every running chat", auto.Contains("AutopilotClock()"), "");
    }

    private static bool Run(Action call)
    {
        call();
        return true;
    }
}
