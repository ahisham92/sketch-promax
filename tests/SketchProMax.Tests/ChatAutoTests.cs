using System.Text.Json;
using SketchProMax.Chat;

/// <summary>Chat types (With coordinator or Simple chat) and Automatic model and effort: sorting work, picking, stickiness.</summary>
internal static class ChatAutoTests
{
    public static void Run(Action<string, bool, string> check)
    {
        // ---- Chat types
        var before = ChatStore.NewType;
        try
        {
            ChatStore.NewType = ChatTypes.Coordinator;
            check("types: a new chat takes the default type", ChatStore.New("k", "t").Type == ChatTypes.Coordinator, "");
            ChatStore.NewType = ChatTypes.Simple;
            check("types: the default can be Simple chat; New chat's arrow picks the other", ChatStore.New("k", "t").Type == ChatTypes.Simple
                && ChatStore.New("k", "t", ChatTypes.Coordinator).Type == ChatTypes.Coordinator, "");
        }
        finally { ChatStore.NewType = before; }
        var legacy = JsonSerializer.Deserialize<Conversation>("""{"Key":"k","Items":[]}""")!;
        var legacyJobs = JsonSerializer.Deserialize<Conversation>("""{"Key":"k","Items":[],"Jobs":[{"Number":1,"Key":"abcdefabcdef","Title":"x"}]}""")!;
        check("types: a chat saved before types is a Simple chat, or With coordinator when it has jobs",
            ChatTypes.Of(legacy) == ChatTypes.Simple && ChatTypes.Of(legacyJobs) == ChatTypes.Coordinator && legacy.Type == "", "");
        var simple = new Conversation { Key = "k", Type = ChatTypes.Simple, Jobs = legacyJobs.Jobs };
        check("types: a stored type wins, and is saved with the chat", ChatTypes.Of(simple) == ChatTypes.Simple
            && ChatTypes.Of(JsonSerializer.Deserialize<Conversation>(JsonSerializer.Serialize(simple))!) == ChatTypes.Simple, "");
        check("types: names and parsing", ChatTypes.Label(ChatTypes.Coordinator) == "With coordinator" && ChatTypes.Label(ChatTypes.Simple) == "Simple chat"
            && ChatTypes.Parse("simple") == ChatTypes.Simple && ChatTypes.Parse("odd") == ChatTypes.Coordinator && ChatTypes.Parse(null) == ChatTypes.Coordinator, "");
        check("types: switching waits for open jobs and the answer", ChatTypes.WhyNotSwitch(true, false)!.Contains("Jobs are still open")
            && ChatTypes.WhyNotSwitch(false, true)!.Contains("still answering") && ChatTypes.WhyNotSwitch(false, false) is null, "");
        var summary = ChatHistory.Summarize(new Conversation { Key = "k", Type = ChatTypes.Simple, Items = [new ChatItem { Kind = "user", Text = "hi" }] });
        check("types: the history lists each chat's type", summary.Type == ChatTypes.Simple
            && ChatHistory.Summarize(legacyJobs).Type == ChatTypes.Coordinator, summary.Type);

        // ---- Sorting work
        (string, string)[] cases =
        [
            ("rename the level 2 views", ChatAuto.Simple), ("Tag all beams in this view", ChatAuto.Simple), ("how many doors are there?", ChatAuto.Simple),
            ("fill the Mark parameter on the columns", ChatAuto.Simple), ("make sheets for levels 1 to 5", ChatAuto.Everyday),
            ("add dimensions to the grids on level 3", ChatAuto.Everyday), ("model the slab edges on the podium", ChatAuto.Everyday),
            ("check the model against the BEP", ChatAuto.Hard), ("apply the redlines from this PDF", ChatAuto.Hard),
            ("estimate the cost of the frame and suggest savings", ChatAuto.Hard), ("why does the beam join fail here?", ChatAuto.Hard),
        ];
        var wrong = cases.Where(c => ChatAuto.Classify(c.Item1).Kind != c.Item2).Select(c => $"{c.Item1} -> {ChatAuto.Classify(c.Item1).Kind}").ToList();
        check("auto: requests are sorted into simple, everyday and hard work", wrong.Count == 0, string.Join("; ", wrong));
        check("auto: a long simple-sounding request isn't simple", ChatAuto.Classify("rename " + new string('x', 300)).Kind == ChatAuto.Everyday, "");
        check("auto: attached documents are hard work, a job report simple", ChatAuto.Classify("look at this", documents: true).Kind == ChatAuto.Hard
            && ChatAuto.Classify("[Job report from Sketch ProMax, not typed by the user]\nJob 1 ...").Kind == ChatAuto.Simple, "");
        check("auto: it says why", ChatAuto.Classify("rename the views").Why == "\"rename\"", ChatAuto.Classify("rename the views").Why);

        // ---- Picking and stickiness
        var simplePick = ChatAuto.Pick("rename the views", "auto", "auto");
        var everyday = ChatAuto.Pick("make sheets for levels 1 to 5", "", "");
        var hard = ChatAuto.Pick("compare the spec with the model", "auto", "auto");
        check("auto: Haiku/low, Sonnet/medium and Opus/high by default", simplePick is { Model: "haiku", Effort: "low", ModelAuto: true, EffortAuto: true }
            && everyday is { Model: "sonnet", Effort: "medium" } && hard is { Model: "opus", Effort: "high" }, $"{simplePick.Label}; {everyday.Label}; {hard.Label}");
        var stuck = ChatAuto.Pick("rename the views", "opus", "xhigh");
        check("auto: a model and effort picked by hand stick", stuck is { Model: "opus", Effort: "xhigh", ModelAuto: false, EffortAuto: false }, stuck.Label);
        var half = ChatAuto.Pick("compare the spec", "haiku", "auto");
        check("auto: the model can stick while the effort stays automatic", half is { Model: "haiku", Effort: "high", ModelAuto: false, EffortAuto: true }, half.Label);
        var settings = ChatAuto.Pick("rename the views", "auto", "auto", new Dictionary<string, string> { [ChatAuto.Simple] = "sonnet" }, new Dictionary<string, string> { [ChatAuto.Simple] = "medium" });
        check("auto: Settings' model and effort per kind of work", settings is { Model: "sonnet", Effort: "medium" }, settings.Label);
        var junk = ChatAuto.Pick("rename the views", "auto", "auto", new Dictionary<string, string> { [ChatAuto.Simple] = "gpt" }, new Dictionary<string, string> { [ChatAuto.Simple] = "turbo" });
        check("auto: an unknown setting falls back to the kind's default", junk is { Model: "haiku", Effort: "low" }, junk.Label);
        check("auto: the coordinator's kind for a job wins over the words", ChatAuto.Pick("rename", "auto", "auto", kind: ChatAuto.Hard) is { Model: "opus", Kind: ChatAuto.Hard, Why: "picked by the coordinator" }, "");
        check("auto: a model the plan refused is skipped", ChatAuto.Pick("compare the spec", "auto", "auto", refused: ["opus"]).Model == "sonnet"
            && ChatAuto.Pick("compare the spec", "opus", "auto", refused: ["opus"]).Model == "opus", "");
        check("auto: stored values parsed", ChatAuto.ParseModel("") == ChatAuto.Automatic && ChatAuto.ParseModel("Sonnet") == "sonnet" && ChatAuto.ParseModel("nope") == ChatAuto.Automatic
            && ChatAuto.ParseEffort("HIGH") == "high" && ChatAuto.ParseEffort("ultra") == ChatAuto.Automatic && ChatAuto.IsAuto(null) && !ChatAuto.IsAuto("low"), "");
        check("auto: the effort levels Claude Code takes", ChatAuto.Efforts.SequenceEqual(["low", "medium", "high", "xhigh", "max"]), "");
        check("auto: what the chat says", ChatAuto.Said(simplePick) == "Automatic: Haiku, low effort (simple work: \"rename\")."
            && ChatAuto.Said(half) == "Automatic effort: Haiku, high effort (hard reasoning: \"compare\").", ChatAuto.Said(half));
        check("auto: an older Claude Code's refusal of --effort", ChatAuto.IsEffortRefusal("error: unknown option '--effort'")
            && !ChatAuto.IsEffortRefusal("Warning: Unknown --effort value 'x' — ignoring it and using the default effort.") && !ChatAuto.IsEffortRefusal(null), "");
        var before2 = (ChatStore.NewModel, ChatStore.NewEffort);
        try
        {
            check("auto: a new chat starts on Automatic", ChatStore.New("k", "t") is { Model: ChatAuto.Automatic, Effort: ChatAuto.Automatic }, "");
            ChatStore.NewModel = "opus";
            ChatStore.NewEffort = "high";
            check("auto: or on the Settings' default", ChatStore.New("k", "t") is { Model: "opus", Effort: "high" }, "");
        }
        finally { (ChatStore.NewModel, ChatStore.NewEffort) = before2; }
        var old = JsonSerializer.Deserialize<Conversation>("""{"Key":"k","Items":[]}""")!;
        check("auto: a chat saved before the pickers is on Automatic", ChatAuto.IsAuto(old.Model) && ChatAuto.IsAuto(old.Effort), "");
        check("auto: no picker label names the vendor", ChatModels.All.Select(m => m.Label + m.Hint).Concat(ChatAuto.Efforts.Select(ChatAuto.EffortLabel)).All(t => !t.Contains("Claude")), "");
        check("settings: the type, model and effort defaults are on the Chat tab", new[] { "ChatTypeDefault", "ChatModelDefault", "ChatEffortDefault", "ChatModelSimple", "ChatEffortHard" }
            .All(k => SketchProMax.UI.SettingsLayout.Settings.TryGetValue(k, out var tab) && tab == SketchProMax.UI.SettingsLayout.Chat), "");
    }
}
