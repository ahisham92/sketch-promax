using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SketchProMax.Chat;

/// <summary>When the chat writes a brief before running a request (the model menu's "Brief before running").</summary>
internal enum BriefMode { Always, ModelChanges, Off }

/// <summary>What the user picked on a brief card.</summary>
internal enum BriefChoice { RunBrief, RunEdited, RunAsTyped }

/// <summary>One thing the brief found genuinely ambiguous, answered with one tap.</summary>
internal sealed class BriefQuestion
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public List<string> Options { get; set; } = [];
    /// <summary>The option the planner thinks safest or most likely; picked until the user taps another.</summary>
    public int Recommended { get; set; }
    /// <summary>The option the user tapped; -1 while they haven't.</summary>
    public int Chosen { get; set; } = -1;

    /// <summary>The option that counts now: the tapped one, else the recommended one.</summary>
    public int Effective => Chosen >= 0 && Chosen < Options.Count ? Chosen : Recommended >= 0 && Recommended < Options.Count ? Recommended : 0;
}

/// <summary>One line of what the request is about in the open model ("Levels": "Level 1 (0 mm), Level 2 (3500 mm)").</summary>
internal sealed class BriefScope
{
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>
/// A precise brief made from the user's message and the open model before anything runs: what is in scope (exact names
/// and counts), the units, the tools it means to use, every assumption, and one-tap questions. Kept in the chat history
/// as JSON on the brief's ChatItem.
/// </summary>
internal sealed class PromptBrief
{
    /// <summary>change, multi_step, read_only or unsure.</summary>
    public string Kind { get; set; } = "unsure";
    /// <summary>What the user typed.</summary>
    public string Request { get; set; } = "";
    /// <summary>The request rewritten as a precise instruction; "{q1}" marks where question q1's answer goes.</summary>
    public string Prompt { get; set; } = "";
    public List<BriefScope> Scope { get; set; } = [];
    public string Units { get; set; } = "";
    public List<string> Tools { get; set; } = [];
    public List<string> Assumptions { get; set; } = [];
    public List<BriefQuestion> Questions { get; set; } = [];

    public bool IsReadOnly => Kind == "read_only";
}

/// <summary>
/// The prompt enhancer's rules, which need no Revit and no WPF: when to make a brief, how the planner is asked and its
/// answer read, the text a brief runs as, and how an edit to a brief becomes a note in the chat's memory.
/// </summary>
internal static class PromptBriefs
{
    public const int MaxQuestions = 4, MaxOptions = 4, MaxItems = 12, MaxNote = 300;

    /// <summary>The prefix of a note distilled from an edited brief; the planner reads notes with it as brief preferences.</summary>
    public const string NotePrefix = "Brief preference: ";

    /// <summary>
    /// The only Sketch ProMax tools the planning run may call: they read the model and write nothing (no files, no
    /// selection). Anything else is denied, because the planning run allows these alone and never asks.
    /// </summary>
    public static readonly string[] PlannerTools =
    [
        "get_model_context", "query_elements", "get_element_parameters", "list_views_and_sheets", "list_types",
        "list_view_templates", "list_view_filters", "list_revisions", "list_title_blocks", "list_worksets", "read_schedule",
    ];

    // ---- When to make a brief ------------------------------------------------------------------------------------------

    private static readonly Regex Confirmation = new(
        @"^((yes|yep|yeah|no|nope|ok(ay)?|sure|please|go( ahead| on)?|do it|proceed|continue|carry on|thanks?( you)?|thx|stop|cancel|undo( that| it)?|retry|try again|again|perfect|great|good|fine|correct|right)\b[\s,.!]*){1,3}$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>A follow-up that carries on with earlier work ("continue with the rest", "finish the move", "do it again"):
    /// the chat already has what it needs, so a planning run would only read the model again.</summary>
    private static readonly Regex Continuation = new(
        @"^(please\s+|ok(ay)?[\s,]+|now\s+|so\s+)*(continue|carry on|go on|keep going|finish|complete|resume|repeat|redo|retry|try (it )?again|do (it|that|this|the same|them) again|proceed|go ahead|same (as before|again|way))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The longest continuation (in words) that runs without a brief; a longer message may add a new task.</summary>
    public const int MaxContinuationWords = 15;

    /// <summary>True for a short follow-up that carries on with earlier work.</summary>
    public static bool IsContinuation(string text)
    {
        var t = text.Trim();
        return t.Length > 0 && Continuation.IsMatch(t) && t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length <= MaxContinuationWords;
    }

    private static readonly Regex ChangeWord = new(
        @"\b(create|add|place|put|insert|delete|remove|erase|move|shift|copy|duplicate|rename|renumber|tag|dimension|annotate|change|modify|edit|update|replace|swap|apply|assign|make|draw|generate|build|fix|align|join|unjoin|split|trim|extend|load|import|link|rotate|mirror|offset|purge|clean ?up|hide|unhide|isolate|recolou?r|override|attach|detach|resize|adjust|convert|merge|reinforce|populate)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex QuestionStart = new(
        @"^(how( many| much| do| does| is| are| can)?|what|which|where|who|when|why|is|are|does|do|did|can you (tell|show|explain|check|list|count|find)|could you (tell|explain)|tell me|explain|list|show me|count|check( if| whether)?|find|summari[sz]e|describe|compare|review)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Steps = new(@"(\band then\b|\bthen\b|\bafter that\b|;|^\s*(\d+[.)]|[-*])\s)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    /// <summary>A quick look at the words: Read for a plain question, Change when it clearly acts, null when unsure.</summary>
    public static bool? LooksReadOnly(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return null;
        var acts = ChangeWord.IsMatch(t);
        var asks = QuestionStart.IsMatch(t) || t.EndsWith("?", StringComparison.Ordinal);
        if (asks && !acts && !Steps.IsMatch(t)) return true;
        if (acts && !asks) return false;
        return null;
    }

    /// <summary>
    /// Whether to make a brief before running <paramref name="text"/>: never when Off, for a short reply to Claude
    /// ("yes", "continue": it answers what Claude proposed) or a short follow-up that carries on with earlier work; always when Always; with Model changes only, not for a
    /// plain question. Unsure counts as a model change.
    /// </summary>
    public static bool ShouldPlan(BriefMode mode, string text)
    {
        if (mode == BriefMode.Off) return false;
        var t = text.Trim();
        if (t.Length == 0 || Confirmation.IsMatch(t) || IsContinuation(t)) return false;
        if (mode == BriefMode.Always) return true;
        // A word or two that asks for no change ("hi", "thanks a lot"): not worth a planning run of up to minutes.
        if (!ChangeWord.IsMatch(t) && t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length <= 2) return false;
        return LooksReadOnly(t) != true;
    }

    /// <summary>Whether the brief is shown as a card (otherwise the message runs as typed): Model changes only skips read-only ones.</summary>
    public static bool ShowCard(BriefMode mode, PromptBrief brief) =>
        mode switch { BriefMode.Off => false, BriefMode.Always => true, _ => !brief.IsReadOnly };

    // ---- The setting ---------------------------------------------------------------------------------------------------

    public static string Label(BriefMode mode) => mode switch
    {
        BriefMode.Always => "Always",
        BriefMode.Off => "Off",
        _ => "Model changes only",
    };

    public static string Hint(BriefMode mode) => mode switch
    {
        BriefMode.Always => "A brief for every message, questions too",
        BriefMode.Off => "Messages run as you type them",
        _ => "A brief before changes and multi-step work; questions run at once",
    };

    public static BriefMode ParseMode(string? text) => (text ?? "").Trim().ToLowerInvariant() switch
    {
        "always" => BriefMode.Always,
        "off" => BriefMode.Off,
        _ => BriefMode.ModelChanges,
    };

    public static string ModeText(BriefMode mode) => mode switch { BriefMode.Always => "always", BriefMode.Off => "off", _ => "changes" };

    /// <summary>The setting, kept in <paramref name="folder"/>\brief.txt (Model changes only at first).</summary>
    public static BriefMode LoadMode(string folder)
    {
        var file = Path.Combine(folder, "brief.txt");
        try { return File.Exists(file) ? ParseMode(File.ReadAllText(file)) : BriefMode.ModelChanges; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return BriefMode.ModelChanges; }
    }

    public static void SaveMode(string folder, BriefMode mode)
    {
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "brief.txt"), ModeText(mode));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* not remembered: the default next time */ }
    }

    // ---- Asking the planner --------------------------------------------------------------------------------------------

    /// <summary>The planning run's instructions (added to Claude Code's own system prompt).</summary>
    public const string PlannerInstructions = """
        You prepare a brief before Sketch ProMax works on a Revit model, so the user can check it before anything runs.
        You can only read the model now. Nothing you do may change it, and you must not do the task itself.

        1. Start from what the message already gives you: the conversation so far, what this chat remembers, and the
           model note (what changed since the last answer, the active view, the selected elements with their ids). Don't
           read again what they already say. Read only what is missing with the read-only Revit tools (get_model_context,
           list_types, list_views_and_sheets, query_elements, list_view_templates ...) to find the exact levels, types,
           views, sheets and element counts the request is about: at most three calls, none for a follow-up that only
           finishes or repeats earlier work. The Revit tools are listed by name only: load the ones you use with
           ToolSearch in one call (select:mcp__sketch-promax__get_model_context,mcp__sketch-promax__query_elements).
        2. Answer with ONE JSON object and nothing else (no prose, no code fence):
           {"kind":"change","brief":"...","scope":[{"label":"Levels","value":"Level 1 (0 mm), Level 2 (3500 mm)"}],
            "units":"mm","tools":["create_walls"],"assumptions":["..."],
            "questions":[{"id":"q1","question":"...","options":["...","..."],"recommended":0}]}
           - kind: "change" when the task would change the model (create, place, modify, delete, tag, dimension, rename,
             load ...), "multi_step" when it needs several steps but changes nothing, "read_only" for a question or a
             report, "unsure" when you can't tell.
           - brief: the request rewritten as a precise instruction for the assistant that will do it, using the exact names,
             counts and units from the model. Write {q1} where the answer to question q1 goes.
           - scope: what is in scope, each with exact names and counts (levels, types, views, sheets, element counts).
           - tools: the Sketch ProMax tool names you expect to be used.
           - assumptions: every assumption you made, one short line each.
           - questions: only what is genuinely ambiguous and would change the result (0 to 4), each with 2 to 4 short
             options; recommended is the index of the safest or most likely option.
        Follow the user's notes, especially those starting "Brief preference:".
        """;

    /// <summary>The planning run's message: the request, its attachments, and the end of the conversation so far.</summary>
    public static string PlanMessage(string request, IEnumerable<string> attachmentNames, IEnumerable<(string Kind, string Text)> recent,
        string remembered = "", string modelNote = "")
    {
        var sb = new StringBuilder();
        if (remembered.Trim().Length > 0) sb.Append("What this chat remembers of its earlier work:\n").Append(Shorten(remembered, 1500)).Append("\n\n");
        var context = new StringBuilder();
        foreach (var (kind, text) in Enumerable.Reverse(recent.ToList()))
        {
            var line = (kind == "user" ? "User: " : "Assistant: ") + Shorten(text, 600);
            if (context.Length + line.Length > 3000) break;
            context.Insert(0, line + "\n");
        }
        if (context.Length > 0) sb.Append("The conversation so far (latest last):\n").Append(context).Append('\n');
        sb.Append("Make the brief for this request:\n").Append(request.Trim());
        var names = attachmentNames.Where(n => n.Length > 0).ToList();
        if (names.Count > 0) sb.Append("\n\nAttached files (the assistant will read them later): ").Append(string.Join(", ", names));
        sb.Append(modelNote);
        return sb.ToString();
    }

    private static string Shorten(string text, int max)
    {
        var one = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return one.Length <= max ? one : one.Substring(0, max) + "…";
    }

    /// <summary>
    /// The answer in a one-shot run's output (--output-format json: one result object, possibly after other lines):
    /// its text and whether it is an error. ("", true) when there is no result.
    /// </summary>
    public static (string Text, bool IsError) ResultOf(string? output)
    {
        foreach (var raw in Enumerable.Reverse((output ?? "").Replace("\r", "").Split('\n')))
        {
            var line = raw.Trim();
            if (!line.StartsWith("{", StringComparison.Ordinal)) continue;
            try
            {
                if (JsonNode.Parse(line) is not JsonObject o || Str(Get(o, "type")) != "result") continue;
                var isError = Get(o, "is_error") is JsonValue v && v.TryGetValue<bool>(out var e) && e;
                var text = Str(Get(o, "result"));
                if (text.Length == 0 && Get(o, "errors") is JsonArray errors) text = string.Join(" ", errors.Select(Str));
                return (text, isError);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException) { }
        }
        return ("", true);
    }

    // ---- Reading the planner's answer ----------------------------------------------------------------------------------

    /// <summary>
    /// The brief in the planner's answer: the first JSON object in it that has a brief (a code fence or prose around it is
    /// allowed). Null when there is none. Lists are trimmed to sensible lengths; an unknown kind counts as unsure.
    /// </summary>
    public static PromptBrief? Parse(string? answer, string request)
    {
        if (string.IsNullOrWhiteSpace(answer)) return null;
        var text = answer!;
        for (var start = text.IndexOf('{'); start >= 0; start = text.IndexOf('{', start + 1))
        {
            var end = MatchingBrace(text, start);
            if (end < 0) continue;
            JsonObject? o;
            try { o = JsonNode.Parse(text.Substring(start, end - start + 1)) as JsonObject; }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException) { continue; }
            if (o is null) continue;
            var brief = FromJson(o, request);
            if (brief is not null) return brief;
        }
        return null;
    }

    /// <summary>The index of the brace closing the one at <paramref name="start"/>, skipping strings; -1 when it never closes.</summary>
    private static int MatchingBrace(string text, int start)
    {
        var depth = 0;
        var inString = false;
        for (var k = start; k < text.Length; k++)
        {
            var c = text[k];
            if (inString)
            {
                if (c == '\\') k++;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return k;
        }
        return -1;
    }

    private static JsonNode? Get(JsonObject o, params string[] names)
    {
        foreach (var pair in o)
            if (names.Any(n => string.Equals(n, pair.Key, StringComparison.OrdinalIgnoreCase))) return pair.Value;
        return null;
    }

    private static string Str(JsonNode? node) => node switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s.Trim(),
        JsonValue v => v.ToJsonString().Trim('"'),
        JsonArray a => string.Join(", ", a.Select(Str).Where(s => s.Length > 0)),
        _ => "",
    };

    private static List<string> Strings(JsonNode? node) => node switch
    {
        JsonArray a => a.Select(Str).Where(s => s.Length > 0).Take(MaxItems).ToList(),
        JsonValue => Str(node) is { Length: > 0 } s ? [s] : [],
        _ => [],
    };

    private static PromptBrief? FromJson(JsonObject o, string request)
    {
        var prompt = Str(Get(o, "brief", "prompt", "improved", "instruction"));
        if (prompt.Length == 0) return null;
        var kind = Str(Get(o, "kind", "type", "classification")).ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        kind = kind switch
        {
            "change" or "model_change" or "changes" or "modify" => "change",
            "multi_step" or "multistep" or "multi" => "multi_step",
            "read_only" or "readonly" or "read" or "question" => "read_only",
            _ => "unsure",
        };
        var brief = new PromptBrief
        {
            Kind = kind, Request = request.Trim(), Prompt = prompt,
            Units = Str(Get(o, "units", "unit")),
            Tools = Strings(Get(o, "tools", "tools_to_use", "plan")).Select(t => t.StartsWith("mcp__", StringComparison.Ordinal) ? t.Substring(t.LastIndexOf("__", StringComparison.Ordinal) + 2) : t).Distinct().ToList(),
            Assumptions = Strings(Get(o, "assumptions")),
        };
        switch (Get(o, "scope", "context"))
        {
            case JsonArray scope:
                foreach (var entry in scope.Take(MaxItems))
                {
                    if (entry is JsonObject s)
                    {
                        var label = Str(Get(s, "label", "name", "what"));
                        var value = Str(Get(s, "value", "items", "detail"));
                        if (label.Length > 0 || value.Length > 0) brief.Scope.Add(new BriefScope { Label = label, Value = value });
                    }
                    else if (Str(entry) is { Length: > 0 } line) brief.Scope.Add(new BriefScope { Value = line });
                }
                break;
            case JsonObject map:
                foreach (var pair in map.Take(MaxItems))
                    if (Str(pair.Value) is { Length: > 0 } value) brief.Scope.Add(new BriefScope { Label = pair.Key, Value = value });
                break;
        }
        if (Get(o, "questions", "choices") is JsonArray questions)
        {
            foreach (var entry in questions)
            {
                if (brief.Questions.Count >= MaxQuestions || entry is not JsonObject q) continue;
                var options = Strings(Get(q, "options", "choices", "answers")).Take(MaxOptions).ToList();
                var question = Str(Get(q, "question", "text", "ask"));
                if (question.Length == 0 || options.Count < 2) continue;
                var id = Str(Get(q, "id"));
                if (id.Length == 0 || brief.Questions.Any(x => x.Id == id)) id = "q" + (brief.Questions.Count + 1);
                var recommended = Get(q, "recommended", "default") is JsonValue r && r.TryGetValue<int>(out var at) ? at : 0;
                brief.Questions.Add(new BriefQuestion { Id = id, Text = question, Options = options, Recommended = recommended >= 0 && recommended < options.Count ? recommended : 0 });
            }
        }
        return brief;
    }

    // ---- The text a brief runs as --------------------------------------------------------------------------------------

    /// <summary>Records a one-tap answer; false when the question or option doesn't exist.</summary>
    public static bool Answer(PromptBrief brief, string questionId, int option)
    {
        var q = brief.Questions.FirstOrDefault(x => x.Id == questionId);
        if (q is null || option < 0 || option >= q.Options.Count) return false;
        q.Chosen = option;
        return true;
    }

    /// <summary>
    /// The brief as the card shows it and as "Run this" sends it (Markdown): the instruction with the answers filled in,
    /// then scope, units, tools, assumptions, the decisions, and the user's own words.
    /// </summary>
    public static string Compose(PromptBrief brief)
    {
        var prompt = brief.Prompt;
        foreach (var q in brief.Questions) prompt = prompt.Replace("{" + q.Id + "}", q.Options[q.Effective]);
        var sb = new StringBuilder(prompt.Trim()).Append('\n');
        if (brief.Scope.Count > 0)
        {
            sb.Append("\n**In scope**\n");
            foreach (var s in brief.Scope) sb.Append("- ").Append(s.Label.Length > 0 ? s.Label + ": " : "").Append(s.Value).Append('\n');
        }
        if (brief.Units.Length > 0) sb.Append("\n**Units:** ").Append(brief.Units).Append('\n');
        if (brief.Tools.Count > 0) sb.Append("\n**Tools:** ").Append(string.Join(", ", brief.Tools)).Append('\n');
        if (brief.Assumptions.Count > 0)
        {
            sb.Append("\n**Assumptions**\n");
            foreach (var a in brief.Assumptions) sb.Append("- ").Append(a).Append('\n');
        }
        if (brief.Questions.Count > 0)
        {
            sb.Append("\n**Decisions**\n");
            foreach (var q in brief.Questions) sb.Append("- ").Append(q.Text).Append(" → ").Append(q.Options[q.Effective]).Append('\n');
        }
        if (brief.Request.Length > 0) sb.Append("\n_The user's own words:_ ").Append(Shorten(brief.Request, 1000)).Append('\n');
        return sb.ToString().TrimEnd();
    }

    /// <summary>What a choice on the card runs: the brief, the user's edited brief, or exactly what they typed.</summary>
    public static string TextFor(BriefChoice choice, PromptBrief brief, string? edited = null) => choice switch
    {
        BriefChoice.RunAsTyped => brief.Request,
        BriefChoice.RunEdited => (edited ?? "").Trim().Length > 0 ? edited!.Trim() : Compose(brief),
        _ => Compose(brief),
    };

    // ---- Learning from edits -------------------------------------------------------------------------------------------

    private static string Norm(string line) => string.Join(" ", line.Trim().TrimStart('-', '*', ' ').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>The lines the user took out of the brief and the lines they put in (blank lines and spacing ignored).</summary>
    public static (List<string> Removed, List<string> Added) Diff(string brief, string edited)
    {
        List<string> Lines(string t) => t.Replace("\r", "").Split('\n').Select(Norm).Where(l => l.Length > 0).ToList();
        var before = Lines(brief);
        var after = Lines(edited);
        var had = new HashSet<string>(before, StringComparer.OrdinalIgnoreCase);
        var has = new HashSet<string>(after, StringComparer.OrdinalIgnoreCase);
        return (before.Where(l => !has.Contains(l)).Distinct().ToList(), after.Where(l => !had.Contains(l)).Distinct().ToList());
    }

    /// <summary>True when the edit changed something worth remembering.</summary>
    public static bool IsEdit(string brief, string edited)
    {
        if (edited.Trim().Length == 0) return false;
        var (removed, added) = Diff(brief, edited);
        return removed.Count + added.Count > 0;
    }

    /// <summary>The message that asks Claude to distil an edit into one preference for later briefs.</summary>
    public static string DistilMessage(string request, string brief, string edited)
    {
        var (removed, added) = Diff(brief, edited);
        var sb = new StringBuilder();
        sb.Append("A Revit user asked: ").Append(Shorten(request, 500)).Append('\n');
        sb.Append("Before running it, they edited the brief that was written for it.\n");
        if (removed.Count > 0) sb.Append("Lines they removed or changed:\n").Append(string.Join("\n", removed.Take(12).Select(l => "- " + Shorten(l, 200)))).Append('\n');
        if (added.Count > 0) sb.Append("Lines they wrote instead:\n").Append(string.Join("\n", added.Take(12).Select(l => "- " + Shorten(l, 200)))).Append('\n');
        sb.Append("""

            Write ONE short sentence (at most 25 words) saying what this user prefers in briefs from now on, general enough
            to help with later requests (for example "Use 200 mm walls unless told otherwise" or "Always list the sheets
            by number"). If the edit only fixed this one request and teaches nothing general, answer NONE. Answer with the
            sentence only.
            """);
        return sb.ToString();
    }

    /// <summary>
    /// The memory note for a distilled preference: one line starting "Brief preference: ", at most 300 characters; ""
    /// when there is nothing to keep (NONE, empty, or only quotes and punctuation).
    /// </summary>
    public static string PreferenceNote(string? distilled)
    {
        var line = (distilled ?? "").Replace("\r", "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        line = line.Trim('"', '\'', '`', '*', ' ', '-');
        if (line.StartsWith(NotePrefix.Trim(), StringComparison.OrdinalIgnoreCase)) line = line.Substring(NotePrefix.Trim().Length).Trim();
        if (line.Length == 0 || !line.Any(char.IsLetterOrDigit) || Regex.IsMatch(line, @"^none\b", RegexOptions.IgnoreCase)) return "";
        line = char.ToUpperInvariant(line[0]) + line.Substring(1);
        var note = NotePrefix + line;
        return note.Length <= MaxNote ? note : note.Substring(0, MaxNote - 1).TrimEnd() + "…";
    }

    /// <summary>The note kept when the preference couldn't be distilled: what the user changed, in their words.</summary>
    public static string FallbackNote(string request, string brief, string edited)
    {
        var (removed, added) = Diff(brief, edited);
        if (added.Count == 0 && removed.Count == 0) return "";
        var sb = new StringBuilder("For requests like \"").Append(Shorten(request, 60)).Append("\", the user");
        if (added.Count > 0) sb.Append(" wrote \"").Append(Shorten(string.Join("; ", added.Take(3)), 120)).Append('"');
        if (removed.Count > 0) sb.Append(added.Count > 0 ? " instead of \"" : " removed \"").Append(Shorten(string.Join("; ", removed.Take(3)), 80)).Append('"');
        return PreferenceNote(sb.ToString());
    }

    // ---- Storage on the chat item --------------------------------------------------------------------------------------

    public static string ToJson(PromptBrief brief) => JsonSerializer.Serialize(brief);

    public static PromptBrief? FromStored(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<PromptBrief>(json!); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException) { return null; }
    }
}
