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
    }

    private static bool Run(Action call)
    {
        call();
        return true;
    }
}
