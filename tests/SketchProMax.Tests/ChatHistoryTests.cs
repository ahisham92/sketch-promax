using SketchProMax.Chat;

/// <summary>The chat history drawer's rules: names from the first request, date groups, search and order.</summary>
internal static class ChatHistoryTests
{
    public static void Run(Action<string, bool, string> check)
    {
        check("history: title from the first line", ChatHistory.AutoTitle("list the levels with their elevations\nand more") == "List the levels with their elevations", ChatHistory.AutoTitle("list the levels with their elevations\nand more"));
        check("history: title without markdown", ChatHistory.AutoTitle("**Check** the `beams` on level 2.") == "Check the beams on level 2", ChatHistory.AutoTitle("**Check** the `beams` on level 2."));
        var longTitle = ChatHistory.AutoTitle("please go through every sheet in the set and renumber the drawings so they follow the new scheme");
        check("history: long title cut at a word", longTitle.EndsWith("…") && longTitle.Length <= 49 && !longTitle.Contains("  "), longTitle);
        check("history: empty request is New chat", ChatHistory.AutoTitle("  \n ") == "New chat" && ChatHistory.AutoTitle(null) == "New chat", "");

        var now = new DateTime(2026, 10, 16, 12, 0, 0);
        check("history: group this week", ChatHistory.GroupOf(now.AddDays(-6), now) == ChatHistory.Week && ChatHistory.GroupOf(now, now) == ChatHistory.Week, "");
        check("history: group this month", ChatHistory.GroupOf(now.AddDays(-7), now) == ChatHistory.Month && ChatHistory.GroupOf(now.AddDays(-29), now) == ChatHistory.Month, "");
        check("history: group older", ChatHistory.GroupOf(now.AddDays(-30), now) == ChatHistory.Older, "");

        ChatSummary S(string name, string key, int daysAgo, bool pinned = false, string search = "") =>
            new("f-" + name, key, key + " title", name, pinned, now.AddDays(-daysAgo).ToUniversalTime(), 3, search);
        var chats = new[]
        {
            S("Levels", "A", 1), S("Beams", "A", 10), S("Sheets", "B", 2), S("Old one", "A", 90), S("Pinned one", "B", 40, pinned: true),
            S("Tags", "A", 3, search: "missing tags on grid C"),
        };
        var mine = ChatHistory.Arrange(chats, "a", allModels: false, null, now);
        check("history: this model only", mine.SelectMany(s => s.Chats).All(c => c.Key == "A") && mine.Sum(s => s.Chats.Count) == 4, "");
        check("history: sections in order, newest first", mine.Select(s => s.Group).SequenceEqual(new[] { ChatHistory.Week, ChatHistory.Month, ChatHistory.Older }) &&
                                                          mine[0].Chats.Select(c => c.Name).SequenceEqual(new[] { "Levels", "Tags" }), string.Join(",", mine.Select(s => s.Group)));
        var all = ChatHistory.Arrange(chats, "A", allModels: true, null, now);
        check("history: pinned first", all[0].Group == ChatHistory.PinnedGroup && all[0].Chats.Single().Name == "Pinned one", "");
        check("history: open model's chats first with all models", all[1].Chats.Select(c => c.Name).SequenceEqual(new[] { "Levels", "Tags", "Sheets" }), string.Join(",", all[1].Chats.Select(c => c.Name)));
        var found = ChatHistory.Arrange(chats, "A", allModels: true, "grid tags", now);
        check("history: search in messages, every word", found.SelectMany(s => s.Chats).Single().Name == "Tags", "");
        check("history: search in model title", ChatHistory.Arrange(chats, "A", true, "B TITLE", now).SelectMany(s => s.Chats).Select(c => c.Name)
            .Intersect(new[] { "Sheets", "Pinned one" }).Count() == 2 && ChatHistory.Arrange(chats, "A", false, "b title", now).All(s => s.Chats.All(c => c.Key == "A")), "");
        check("history: no match, no sections", ChatHistory.Arrange(chats, "A", true, "zzz", now).Count == 0, "");

        var conv = new Conversation { Key = "k", Title = "Model", Items = { new ChatItem { Kind = "user", Text = "Put these columns on a sheet" }, new ChatItem { Kind = "assistant", Text = "Done." } } };
        var summary = ChatHistory.Summarize(conv);
        check("history: summary names an unnamed chat from its first request", summary.Name == "Put these columns on a sheet" && summary.Search.Contains("Done."), summary.Name);
        conv.Name = "Columns";
        check("history: a renamed chat keeps its name", ChatHistory.Summarize(conv).Name == "Columns", "");
    }
}
