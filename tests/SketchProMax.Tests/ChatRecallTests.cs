using SketchProMax.Chat;

/// <summary>Shrink chat and chats that recall each other (ChatRecall, the stream's compaction lines), without Revit or WPF.</summary>
internal static class ChatRecallTests
{
    public static void Run(Action<string, bool, string> check)
    {
        // The stream: each request's size, and the compaction.
        var parser = new StreamParser();
        var events = parser.ParseLine("""{"type":"stream_event","event":{"type":"message_start","message":{"id":"m1","usage":{"input_tokens":3,"cache_read_input_tokens":40000,"cache_creation_input_tokens":997}}},"parent_tool_use_id":null}""");
        check("shrink: a request's size is read", events.OfType<ContextEvent>().SingleOrDefault() is { Tokens: 41000 }, string.Join(" | ", events));
        check("shrink: no size without usage", new StreamParser().ParseLine("""{"type":"stream_event","event":{"type":"message_start","message":{"id":"m2"}}}""").OfType<ContextEvent>().Any() == false, "");
        var compact = new StreamParser().ParseLine("""{"type":"system","subtype":"compact_boundary","session_id":"s","compact_metadata":{"trigger":"auto","pre_tokens":155000}}""");
        check("shrink: compaction read with its trigger", compact.SingleOrDefault() is CompactedEvent { Trigger: "auto", PreTokens: 155000 }, string.Join(" | ", compact));
        check("shrink: compaction without metadata still read", new StreamParser().ParseLine("""{"type":"system","subtype":"compact_boundary"}""").SingleOrDefault() is CompactedEvent, "");
        var line = StreamInput.Compact(ChatRecall.CompactInstructions);
        using (var doc = System.Text.Json.JsonDocument.Parse(line))
        {
            var content = doc.RootElement.GetProperty("message").GetProperty("content");
            check("shrink: /compact is sent as a plain user string", doc.RootElement.GetProperty("type").GetString() == "user" &&
                content.ValueKind == System.Text.Json.JsonValueKind.String && content.GetString()!.StartsWith("/compact Keep the decisions"), line);
        }
        check("shrink: automatic once the chat grew by the threshold", ChatRecall.ShouldAutoShrink(50_000 + ChatRecall.AutoShrinkTokens, 50_000) && !ChatRecall.ShouldAutoShrink(50_000 + ChatRecall.AutoShrinkTokens - 1, 50_000), "");
        check("shrink: automatic at the cap, whatever the floor", ChatRecall.ShouldAutoShrink(ChatRecall.AutoShrinkMaxTokens, 0) && !ChatRecall.ShouldAutoShrink(ChatRecall.AutoShrinkMaxTokens - 1, 0), "");
        check("shrink: the floor is the least seen", ChatRecall.Floor(0, 52_000) == 52_000 && ChatRecall.Floor(52_000, 80_000) == 52_000 && ChatRecall.Floor(52_000, 9_000) == 9_000 && ChatRecall.Floor(52_000, 0) == 52_000, "");

        // When a chat is summarised.
        Conversation Chat(string key, string name, int days, string recall = "", params (string Kind, string Text)[] items)
        {
            var c = new Conversation { Key = key, Title = key, Id = Guid.NewGuid().ToString("N").Substring(0, 12), Name = name, Updated = DateTime.UtcNow.AddDays(-days), Recall = recall };
            foreach (var (kind, text) in items) c.Items.Add(new ChatItem { Kind = kind, Text = text, Name = kind == "tool" ? "create_columns" : "" });
            c.RecallCount = recall.Length > 0 ? c.Items.Count : 0;
            return c;
        }
        var empty = Chat("A", "Empty", 0);
        check("recall: an empty chat isn't summarised", !ChatRecall.NeedsSummary(empty), "");
        var greeting = Chat("A", "Hi", 0, "", ("user", "hi"));
        check("recall: one message alone isn't summarised", !ChatRecall.NeedsSummary(greeting), "");
        var fresh = Chat("A", "Columns", 0, "", ("user", "columns 600x600"), ("tool", "600x600"), ("assistant", "Done"));
        check("recall: a chat with no summary is summarised", ChatRecall.NeedsSummary(fresh), "");
        var done = Chat("A", "Columns", 0, "- 600x600", ("user", "columns 600x600"), ("assistant", "Done"));
        check("recall: nothing new, no new summary", !ChatRecall.NeedsSummary(done), "");
        done.Items.Add(new ChatItem { Kind = "assistant", Text = "More" });
        check("recall: only Claude's words added, no new summary", !ChatRecall.NeedsSummary(done), "");
        for (var i = 0; i < 3; i++) done.Items.Add(new ChatItem { Kind = "user", Text = "next " + i });
        check("recall: new requests, summarised again", ChatRecall.NeedsSummary(done), "");
        done.RecallCount = 999; // the store trimmed the chat below its count
        check("recall: a trimmed chat is judged afresh", ChatRecall.NeedsSummary(done), "");

        // The transcript the summary run reads.
        var transcript = ChatRecall.Transcript(fresh);
        check("recall: transcript has requests, steps and answers", transcript.Contains("User: columns 600x600") && transcript.Contains("Step: create_columns (600x600)") && transcript.Contains("Claude: Done"), transcript);
        var longChat = Chat("A", "Long", 0, "- earlier memory", Enumerable.Range(0, 400).Select(i => ("user", "request number " + i + " " + new string('x', 100))).ToArray());
        var cut = ChatRecall.Transcript(longChat);
        check("recall: a long transcript keeps the latest and the earlier summary", cut.Length <= ChatRecall.MaxTranscript + 400 && cut.Contains("request number 399") && !cut.Contains("request number 0 ") && cut.Contains("earlier memory") && cut.Contains("(latest part)"), cut.Length.ToString());

        // What the summary run's answer becomes.
        check("recall: NOTHING keeps nothing", ChatRecall.CleanSummary("NOTHING.") == "" && ChatRecall.CleanSummary("") == "" && ChatRecall.CleanSummary(null) == "", "");
        check("recall: answer kept as bullets", ChatRecall.CleanSummary("Here is the memory:\n* one\n- two\n```\n• three") == "- one\n- two\n- three", ChatRecall.CleanSummary("Here is the memory:\n* one\n- two\n```\n• three"));
        var huge = ChatRecall.CleanSummary(string.Join("\n", Enumerable.Range(0, 100).Select(i => "- bullet " + i + " " + new string('y', 50))));
        check("recall: summary capped", huge.Length <= ChatRecall.MaxSummary && huge.StartsWith("- bullet 0"), huge.Length.ToString());

        // What a new chat starts with.
        var a1 = Chat("Tin Can.rvt", "Columns", 3, "- columns 600x600 C40");
        var a2 = Chat("Tin Can.rvt", "Beams", 1, "- beams 300x600");
        var a3 = Chat("tin can.RVT", "Slabs", 2, "- slabs 250 thick");
        var a4 = Chat("Tin Can.rvt", "Oldest", 9, "- piles 600");
        var aNow = Chat("Tin Can.rvt", "Now", 0, "- the open chat's own memory");
        var b1 = Chat("Tower.rvt", "Pile caps for the Tower", 5, "- pile caps 1.5 m deep");
        var all = new List<Conversation> { a1, a2, a3, a4, aNow, b1 };
        var block = ChatRecall.PromptBlock(all, "Tin Can.rvt", aNow.Id);
        check("recall: new chat gets the latest three of its model, newest first",
            block.Contains("300x600") && block.Contains("250 thick") && block.Contains("C40") && !block.Contains("piles 600") &&
            block.IndexOf("300x600", StringComparison.Ordinal) < block.IndexOf("250 thick", StringComparison.Ordinal), block);
        check("recall: not its own memory, not another model's", !block.Contains("open chat's own") && !block.Contains("pile caps"), block);
        check("recall: nothing to start with is empty", ChatRecall.PromptBlock(all, "Other.rvt", null) == "", "");

        // Searching every chat.
        b1.Items.Add(new ChatItem { Kind = "tool", Name = "create_foundations", Text = "pile caps 1500 deep", Input = "{\"depth\": 1500}" });
        var hits = ChatRecall.Search(all, "do the pile caps the same way as in the Tower project");
        check("recall: search finds the other project's chat first", hits.FirstOrDefault()?.Chat == b1 && hits[0].Lines.Count > 0, string.Join(" | ", hits.Select(h => h.Chat.Name + " " + h.Score)));
        var tower = ChatRecall.Search(all, "the same way as in the Tower project");
        check("recall: search leaves out chats with no matching word", tower.Count == 1 && tower[0].Chat == b1, string.Join(" | ", tower.Select(h => h.Chat.Name)));
        check("recall: common words are not searched", ChatRecall.Words("do it the same way as in the project").Count == 0 && ChatRecall.Words("Level 2 beams").Contains("2"), string.Join(",", ChatRecall.Words("do it the same way as in the project")));
        check("recall: an empty search lists chats with a memory", ChatRecall.Search(all, "").Count == 5, "");
        var (summary, requests, steps) = ChatRecall.Details(b1);
        check("recall: open gives the summary and the steps with their settings", summary.Contains("1.5 m") && steps.Count == 1 && steps[0].Contains("create_foundations") && steps[0].Contains("1500"), string.Join(" | ", steps));
    }
}
