using SketchProMax.Chat;

/// <summary>Saved chats and the open chat across an in-session update (ChatStore, ChatHandover), in a temporary folder.</summary>
internal static class ChatHandoverTests
{
    public static void Run(Action<string, bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "spm-chat-tests-" + Guid.NewGuid().ToString("N"));
        ChatStore.RootOverride = root;
        try
        {
            // Round trip: what is saved is read back, session id included; an empty new chat is not written.
            const string key = @"C:\Models\Test.rvt";
            ChatStore.Save(ChatStore.New(key, "Test"));
            check("store: an empty new chat is not written", ChatStore.All().Count == 0, "");
            var open = ChatStore.New(key, "Test");
            open.Items.Add(new ChatItem { Kind = "user", Text = "List the levels", Time = DateTime.UtcNow });
            open.Items.Add(new ChatItem { Kind = "assistant", Text = "Level 1, Level 2.", Time = DateTime.UtcNow });
            open.SessionId = "session-1";
            ChatStore.Save(open);
            var read = ChatStore.Read(open.File);
            check("store: round trip keeps messages and session", read is { SessionId: "session-1" } && read.Items.Count == 2 && read.Items[1].Text == "Level 1, Level 2." && read.Name == "List the levels",
                read is null ? "null" : $"{read.Items.Count} {read.SessionId} {read.Name}");
            check("store: model key is case-insensitive", ChatStore.Load(key.ToUpperInvariant(), "Test").File == open.File, "");

            // A newer chat of the same model: Load (a fresh start) opens it, the handover opens the chat that was open.
            var newer = ChatStore.New(key, "Test");
            newer.Items.Add(new ChatItem { Kind = "user", Text = "Newer chat", Time = DateTime.UtcNow });
            ChatStore.Save(newer);
            open.Updated = DateTime.UtcNow.AddDays(-1);
            ChatStore.Save(open, touch: false);
            check("store: Load opens the model's latest", ChatStore.Load(key, "Test").File == newer.File, "");

            // The swap: the old pane saves after its last message (a cut-off answer marked), then leaves the chat; the new one reopens it.
            open.Items.Add(new ChatItem { Kind = "assistant", Text = "Half an ans", Time = DateTime.UtcNow });
            open.Items.Add(new ChatItem { Kind = "note", Text = "cut off", Time = DateTime.UtcNow });
            ChatStore.Save(open);
            ChatHandover.Leave(open, "unsent text", [@"C:\pic.png"]);
            var left = ChatHandover.Take();
            var back = ChatHandover.Open(left, key, "Test (renamed title)");
            check("handover: the open chat is reopened, not the latest", back.File == open.File, Path.GetFileName(back.File));
            check("handover: messages, cut-off answer and session kept", back.Items.Count == 4 && back.Items[2].Text == "Half an ans" && back.SessionId == "session-1", $"{back.Items.Count} {back.SessionId}");
            check("handover: unsent message and attachments kept", left is { Draft: "unsent text" } && left.Attachments.SequenceEqual(new[] { @"C:\pic.png" }), "");
            check("handover: the title follows the model", back.Title == "Test (renamed title)", back.Title);
            check("handover: taken once", ChatHandover.Take() is null && ChatHandover.Peek() is null, "");
            check("handover: chats on disk unchanged", ChatStore.All().Count == 2, ChatStore.All().Count.ToString());

            // A new, empty chat was open: a new chat again, not the latest saved one.
            ChatHandover.Leave(ChatStore.New(key, "Test"), "", []);
            var fresh = ChatHandover.Open(ChatHandover.Take(), key, "Test");
            check("handover: an empty new chat stays new", fresh.Items.Count == 0 && fresh.File.Length == 0, fresh.File);

            // Another model, a deleted file, or a stale handover: the model's latest, as on a fresh start.
            ChatHandover.Leave(open, "", []);
            check("handover: another model opens its own latest", ChatHandover.Open(ChatHandover.Take(), "Other.rvt", "Other").Items.Count == 0, "");
            ChatHandover.Leave(open, "", [], DateTime.UtcNow - ChatHandover.MaxAge - TimeSpan.FromMinutes(1));
            check("handover: a stale one is ignored", ChatHandover.Take() is null, "");
            var gone = new ChatHandoverState { File = Path.Combine(root, "missing.json"), Key = key, At = DateTime.UtcNow };
            check("handover: a missing file falls back to the latest", ChatHandover.Open(gone, key, "Test").File == ChatStore.Load(key, "Test").File && ChatStore.Load(key, "Test").File.Length > 0, "");
        }
        finally
        {
            ChatStore.RootOverride = null;
            AppDomain.CurrentDomain.SetData(ChatHandover.DataKey, null);
            try { Directory.Delete(root, true); } catch (Exception) { }
        }
    }
}
