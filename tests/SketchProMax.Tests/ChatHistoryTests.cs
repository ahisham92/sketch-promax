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

/// <summary>Saved prompts: newest first, no repeats, kept in a file.</summary>
internal static class SavedPromptTests
{
    public static void Run(Action<string, bool, string> check)
    {
        var now = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
        var list = SavedPrompts.Add([], "List the levels", now);
        list = SavedPrompts.Add(list, "Check the beams", now);
        list = SavedPrompts.Add(list, "  list   the LEVELS ", now);
        check("prompts: newest first, a repeat moves up", list.Count == 2 && list[0].Text == "list   the LEVELS" && list[1].Text == "Check the beams", string.Join("|", list.Select(p => p.Text)));
        check("prompts: titled", list[1].Title == "Check the beams", "");
        check("prompts: blank not saved", SavedPrompts.Add(list, "  ", now).Count == 2, "");
        check("prompts: remove", SavedPrompts.Remove(list, "check the beams").Single().Text == "list   the LEVELS", "");
        var many = Enumerable.Range(0, SavedPrompts.Max + 5).Aggregate(new List<SavedPrompt>(), (l, i) => SavedPrompts.Add(l, "prompt " + i, now));
        check("prompts: capped", many.Count == SavedPrompts.Max && many[0].Text == "prompt " + (SavedPrompts.Max + 4), "");
        var file = Path.Combine(Path.GetTempPath(), "spm-prompts-" + Guid.NewGuid().ToString("N"), "prompts.json");
        try
        {
            check("prompts: none before saving", SavedPrompts.Load(file).Count == 0, "");
            check("prompts: saved and read back", SavedPrompts.Save(file, list) && SavedPrompts.Load(file).Select(p => p.Text).SequenceEqual(list.Select(p => p.Text)), "");
            File.WriteAllText(file, "{ not json");
            check("prompts: damaged file reads as none", SavedPrompts.Load(file).Count == 0, "");
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(file)!, true); } catch (IOException) { } }
    }
}

/// <summary>The home screen's prompt library: groups, tool grouping, search, tailored prompts.</summary>
internal static class PromptLibraryTests
{
    public static void Run(Action<string, bool, string> check)
    {
        var tools = new[] { ("place_columns", "Places structural columns at points or grid intersections. More text."), ("reinforce_beam", "Adds bars to beams."), ("embodied_carbon", "Estimates embodied carbon.") };
        var lib = PromptLibrary.Load(tools);
        var groups = lib.Groups.Select(g => g.Name).ToList();
        check("library: ribbon-like groups", new[] { "Model", "Documents", "Check", "Issue", "Structure", "Steel", "Rebar", "Marine", "Carbon" }.All(groups.Contains), string.Join(",", groups));
        check("library: hand-written capabilities in known groups", lib.Capabilities.Count(c => c.Tool == "") >= 30 && lib.Capabilities.All(c => groups.Contains(c.Group)), "");
        check("library: a tool becomes a capability", lib.Capabilities.Any(c => c.Tool == "place_columns" && c.Group == "Structure" && c.Title == "Place columns" &&
                                                                               c.Summary == "Places structural columns at points or grid intersections." && c.Prompt.Contains("place_columns")), "");
        check("library: counts per group add up", lib.Counts().Sum(c => c.Count) == lib.Capabilities.Count, "");
        check("library: search every word", lib.Find(null, "carbon embodied").Any() && lib.Find("Rebar", "beam").Any(c => c.Tool == "reinforce_beam") && !lib.Find(null, "zzqx").Any(), "");

        check("library: three project types, four phases", lib.ProjectTypes.Count == 3 && lib.Phases.Count == 4, "");
        check("library: more than 64 tailored prompts", lib.TailoredPrompts.Count > 64, lib.TailoredPrompts.Count.ToString());
        var thin = lib.ProjectTypes.SelectMany(t => lib.Phases.Select(p => (t.Id, p.Id, n: lib.For(t.Id, p.Id).Count()))).Where(x => x.n < 5).ToList();
        check("library: every type and phase has ideas", thin.Count == 0, string.Join(", ", thin));

        check("library: tool groups by name", PromptLibrary.GroupForTool("export_bar_schedule") == "Rebar" && PromptLibrary.GroupForTool("place_bracing") == "Steel" &&
                                               PromptLibrary.GroupForTool("vessel_collision_check") == "Marine" && PromptLibrary.GroupForTool("issue_drawings") == "Issue" &&
                                               PromptLibrary.GroupForTool("find_clashes") == "Check" && PromptLibrary.GroupForTool("create_sheets") == "Documents" &&
                                               PromptLibrary.GroupForTool("carbon_view") == "Carbon" && PromptLibrary.GroupForTool("query_elements") == "Data" &&
                                               PromptLibrary.GroupForTool("place_beams") == "Structure" && PromptLibrary.GroupForTool("import_image") == "Model", "");
        check("library: humanize", PromptLibrary.Humanize("create_3d_view") == "Create 3D view" && PromptLibrary.Humanize("export_pdf") == "Export PDF", PromptLibrary.Humanize("create_3d_view"));
        check("library: first sentence", PromptLibrary.FirstSentence("One. Two.") == "One." && PromptLibrary.FirstSentence("v1.2 is fine") == "v1.2 is fine", "");
        check("library: damaged data is empty, not an error", PromptLibrary.Parse("{ nope", tools).Groups.Count == 0, "");
    }
}
