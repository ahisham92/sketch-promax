using System.IO;
using SketchProMax.Chat;

/// <summary>
/// Moving between chats: which chats belong to an open model (central, file, never saved), moving them when a model is
/// saved, and how the bridge tells the shown chat's calls from a chat working in the background.
/// </summary>
internal static class ChatSwitchTests
{
    public static void Run(Action<string, bool, string> check)
    {
        // Which chats are a model's.
        var central = ChatModelKeys.For(@"C:\Users\me\Tower_me.rvt", "Tower", @"\\server\Tower.rvt", detached: false, "a1");
        check("chat model: a local copy's chats are its central's", central == @"\\server\Tower.rvt", central);
        var opened = ChatModelKeys.For(@"\\server\Tower.rvt", "Tower", @"\\server\Tower.rvt", detached: false, "a1");
        check("chat model: the central opened itself shares them", opened == central, opened);
        var file = ChatModelKeys.For(@"C:\p\Shed.rvt", "Shed", null, detached: false, "a1");
        check("chat model: a saved model's chats are its file's", file == @"C:\p\Shed.rvt", file);
        var one = ChatModelKeys.For("", "Project1", null, detached: false, "a1");
        var two = ChatModelKeys.For("", "Project1", null, detached: false, "b2");
        check("chat model: two new projects named Project1 don't share chats", one != two && ChatModelKeys.IsUnsaved(one) && ChatModelKeys.IsUnsaved(two), one + " / " + two);
        var detached = ChatModelKeys.For("", "Tower_detached", @"\\server\Tower.rvt", detached: true, "c3");
        check("chat model: a detached copy has its own chats, not the central's nor other detached copies'",
            ChatModelKeys.IsUnsaved(detached) && detached != central && detached != ChatModelKeys.For("", "Tower_detached", null, detached: true, "d4"), detached);
        check("chat model: a local copy's old chats move to the central", ChatModelKeys.Legacy(@"C:\Users\me\Tower_me.rvt", central) == @"C:\Users\me\Tower_me.rvt", "");
        check("chat model: nothing moves for a central or a plain file", ChatModelKeys.Legacy(@"\\server\Tower.rvt", central) is null && ChatModelKeys.Legacy(@"C:\p\Shed.rvt", file) is null
            && ChatModelKeys.Legacy("", one) is null, "");

        // Saving an unsaved model takes its chats along.
        var root = Path.Combine(Path.GetTempPath(), "spm-switch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            ChatStore.RootOverride = root;
            var chat = ChatStore.New(one, "Project1", ChatTypes.Simple);
            chat.Items.Add(new ChatItem { Kind = "user", Text = "make levels" });
            ChatStore.Save(chat);
            var other = ChatStore.New(two, "Project1", ChatTypes.Simple);
            other.Items.Add(new ChatItem { Kind = "user", Text = "other project" });
            ChatStore.Save(other);
            var moved = ChatStore.Rekey(one, @"C:\p\New.rvt");
            var now = ChatStore.ForModel(@"C:\p\New.rvt");
            check("chat model: Save As moves the unsaved model's chats to the file", moved == 1 && now.Count == 1 && now[0].Items[0].Text == "make levels" && now[0].Id == chat.Id,
                $"{moved} moved, {now.Count} found");
            check("chat model: the old copy is gone and the other new project keeps its own", ChatStore.ForModel(one).Count == 0 && ChatStore.ForModel(two).Count == 1, "");
        }
        finally
        {
            ChatStore.RootOverride = null;
            try { Directory.Delete(root, recursive: true); } catch (Exception) { }
        }

        var notes = MemoryNotes.Parse("## Global\n- g\n## Model: unsaved:x:Project1\n- levels are 4 m\n## Model: C:\\p\\New.rvt\n- keep grids\n");
        var rekeyed = MemoryNotes.Rekey(notes, "unsaved:x:Project1", @"C:\p\New.rvt");
        var (_, model) = MemoryNotes.For(notes, @"C:\p\New.rvt");
        check("chat model: memory notes follow the saved model", rekeyed && model.SequenceEqual(new[] { "keep grids", "levels are 4 m" }) && MemoryNotes.For(notes, "unsaved:x:Project1").Model.Count == 0,
            string.Join(" | ", model));

        // The bridge: the shown chat is the main chat; a chat left working is a caller of its own, like a job.
        var shown = JobBoard.ChatKey("abc123");
        check("background chat: the shown chat's calls are the main chat's", JobBoard.Caller(shown, shown) is null && JobBoard.Caller(null, shown) is null, "");
        check("background chat: another chat's calls take turns like a job", JobBoard.Caller(JobBoard.ChatKey("zzz999"), shown) == "chat-zzz999", "");
        check("background chat: a job stays a job", JobBoard.Caller("abcdefabcdef", shown) == "abcdefabcdef", "");
        check("background chat: a chat without an id has no key", JobBoard.ChatKey("") == "" && !JobBoard.IsChatKey("abcdefabcdef") && JobBoard.IsChatKey(shown), "");
    }
}
