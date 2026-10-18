using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SketchProMax.Chat;

/// <summary>
/// Chats that recall each other. Each saved chat keeps a short summary of itself (<see cref="Conversation.Recall"/>):
/// what was decided, the settings and standards used, the steps that worked and what is left. A new chat on the same
/// model starts with the latest summaries of that model's other chats, so nothing has to be explained again, and the
/// recall_chats tool searches every chat of every model ("do it the same way as in the Tower project"). The summary is written by
/// a short Claude run when a chat is shrunk, left or idle (ChatPane); everything here is pure text and needs no WPF.
///
/// Shrinking: when the conversation sent with each message has grown by <see cref="AutoShrinkTokens"/> over what an empty
/// chat sends (the instructions and the Revit tools' descriptions, its floor), the pane runs Claude Code's /compact, which
/// replaces the earlier messages with a summary, so every later message costs less.
/// </summary>
internal static class ChatRecall
{
    /// <summary>The chat shrinks itself after a turn whose conversation is this much above the chat's floor.</summary>
    public const long AutoShrinkTokens = 60_000;

    /// <summary>...and in any case once a message sends this much (well before Claude Code's own near-full point).</summary>
    public const long AutoShrinkMaxTokens = 150_000;

    /// <summary>A chat is summarised again once this many items (messages, steps) were added since its last summary.</summary>
    public const int ItemsBeforeSummary = 4;

    public const int MaxSummary = 1500, MaxTranscript = 16_000, MaxPromptChats = 3, MaxPromptChars = 4500;

    /// <summary>The model the summaries are written with: the quickest, so remembering costs little of the plan.</summary>
    public const string SummaryModel = "claude-haiku-5-5";

    /// <summary>What /compact keeps (Claude Code's own summary of the conversation, used from the next message on).</summary>
    public const string CompactInstructions =
        "Keep the decisions, the settings and standards used, the elements and views worked on, the steps that worked (with " +
        "the values used) and what is still to do. Drop greetings, repeated tool output and anything already fixed.";

    /// <summary>The instructions of the run that writes a chat's summary.</summary>
    public const string SummaryInstructions =
        "You write the memory of one chat between a structural engineer and Claude inside Revit, so that later chats know " +
        "it without being told again. Use no tools. Answer with 3 to 8 short Markdown bullets and nothing else: the project " +
        "and model facts learned, the decisions and choices the user made, the standards, families, types, sizes and " +
        "settings used, the steps that worked (name the action and the values, e.g. \"made 600x600 columns at every grid " +
        "crossing on Levels 1-4\"), and what is still to do. Plain words, the model's units, no element ids, no tool names, " +
        "no greetings. If nothing in the chat is worth remembering, answer exactly NOTHING.";

    // ---- When to shrink and when to summarise ---------------------------------------------------------------------------

    /// <summary>
    /// True when the last turn sent so much that the chat should shrink itself now: <paramref name="contextTokens"/> is
    /// what its last request sent, <paramref name="floor"/> the least any request of this chat sent (0 when unknown).
    /// </summary>
    public static bool ShouldAutoShrink(long contextTokens, long floor) =>
        contextTokens >= AutoShrinkMaxTokens || floor > 0 && contextTokens - floor >= AutoShrinkTokens;

    /// <summary>The chat's floor after a request that sent <paramref name="contextTokens"/>.</summary>
    public static long Floor(long floor, long contextTokens) => contextTokens <= 0 ? floor : floor <= 0 ? contextTokens : Math.Min(floor, contextTokens);

    /// <summary>True when the chat has new messages since its summary was written (or has none and is worth one).</summary>
    public static bool NeedsSummary(Conversation conv)
    {
        if (conv.Items.Count == 0) return false;
        var from = Math.Min(Math.Max(conv.RecallCount, 0), conv.Items.Count);
        // The store keeps the latest items only: a chat trimmed below its count is judged by what was added since.
        if (conv.RecallCount > conv.Items.Count) from = 0;
        var added = conv.Items.Skip(from).ToList();
        if (!added.Any(i => i.Kind == "user")) return false;
        if (conv.Recall.Length == 0) return conv.Items.Count(i => i.Kind is "user" or "assistant" or "tool") >= 2;
        return added.Count >= ItemsBeforeSummary;
    }

    // ---- The summary run --------------------------------------------------------------------------------------------------

    /// <summary>
    /// The chat as plain text for the summary run: the user's messages, Claude's answers and the steps it took, the latest
    /// kept when it is long. The earlier summary goes first, so a long chat's memory is carried forward, not lost.
    /// </summary>
    public static string Transcript(Conversation conv, int max = MaxTranscript)
    {
        var lines = new List<string>();
        foreach (var item in conv.Items)
        {
            var text = OneLine(item.Text);
            switch (item.Kind)
            {
                case "user":
                    if (text.Length > 0) lines.Add("User: " + Cap(text, 1200));
                    break;
                case "assistant":
                    if (text.Length > 0) lines.Add("Claude: " + Cap(text, 900));
                    break;
                case "tool":
                    lines.Add("Step: " + item.Name + (text.Length > 0 ? " (" + Cap(text, 200) + ")" : "") + (item.State == "failed" ? " - failed" : ""));
                    break;
            }
        }
        var sb = new StringBuilder();
        var body = new List<string>();
        var size = 0;
        for (var i = lines.Count - 1; i >= 0 && size + lines[i].Length + 1 <= max; i--)
        {
            body.Insert(0, lines[i]);
            size += lines[i].Length + 1;
        }
        if (conv.Recall.Length > 0) sb.AppendLine("What was remembered from this chat before:").AppendLine(conv.Recall).AppendLine();
        sb.AppendLine($"The chat \"{conv.Name}\" about the model {conv.Title}" + (body.Count < lines.Count ? " (latest part):" : ":"));
        foreach (var line in body) sb.AppendLine(line);
        return sb.ToString();
    }

    /// <summary>The summary as kept: bullets only, at most <see cref="MaxSummary"/> characters; "" for NOTHING or no answer.</summary>
    public static string CleanSummary(string? answer)
    {
        var text = (answer ?? "").Replace("\r", "").Trim();
        if (text.Length == 0 || text.Trim('.', ' ').Equals("NOTHING", StringComparison.OrdinalIgnoreCase)) return "";
        var bullets = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("```", StringComparison.Ordinal))
            .Select(l => l.StartsWith("- ", StringComparison.Ordinal) || l.StartsWith("* ", StringComparison.Ordinal) ? l.Substring(2).Trim() : l.TrimStart('•', ' '))
            .Where(l => l.Length > 0 && !l.EndsWith(":", StringComparison.Ordinal)).ToList();
        var sb = new StringBuilder();
        foreach (var b in bullets)
        {
            var line = "- " + b;
            if (sb.Length + line.Length + 1 > MaxSummary) break;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line);
        }
        return sb.ToString();
    }

    // ---- What a new chat starts with ----------------------------------------------------------------------------------

    /// <summary>
    /// The block added to a chat's instructions: the latest summaries of the same model's other chats, newest first, or
    /// "" when there are none. <paramref name="chats"/> are that model's chats; <paramref name="currentId"/> is left out.
    /// </summary>
    public static string PromptBlock(IEnumerable<Conversation> chats, string modelKey, string? currentId)
    {
        var list = chats.Where(c => c.Recall.Length > 0 && string.Equals(c.Key, modelKey, StringComparison.OrdinalIgnoreCase) && c.Id != currentId)
            .OrderByDescending(c => c.Updated).Take(MaxPromptChats).ToList();
        if (list.Count == 0) return "";
        var sb = new StringBuilder();
        sb.AppendLine("What earlier chats about this model remembered (newest first). Use it so the user doesn't have to explain again, " +
                      "but check the model when it matters, and follow what the user says now when it differs:");
        foreach (var c in list)
        {
            var part = $"From the chat \"{c.Name}\" ({c.Updated.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture)}):\n{c.Recall}\n";
            if (sb.Length + part.Length > MaxPromptChars) break;
            sb.Append(part);
        }
        sb.AppendLine("Older chats, and chats about other models, can be searched with recall_chats.");
        return sb.ToString();
    }

    // ---- Searching every chat ------------------------------------------------------------------------------------------

    internal sealed record Hit(Conversation Chat, double Score, List<string> Lines);

    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "way", "same", "did", "how", "what", "was", "were", "that", "this", "from", "like", "into",
        "project", "model", "chat", "chats", "other", "another", "before", "earlier", "last", "time", "again", "please", "can",
        "you", "your", "our", "out", "all", "have", "had", "done", "make", "made", "use", "used", "just", "then",
    };

    /// <summary>The words of a query worth matching: three letters or more (or a number), common words left out.</summary>
    public static List<string> Words(string query) =>
        Regex.Matches(query ?? "", @"[\p{L}\p{N}][\p{L}\p{N}\-]*").Cast<Match>().Select(m => m.Value)
            .Where(w => (w.Length >= 3 || w.Any(char.IsDigit)) && !Common.Contains(w)).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList();

    /// <summary>
    /// The chats that best match <paramref name="query"/>, best first: words in the model's title and the chat's name
    /// count most, then its summary, then its messages and steps. A chat matching no word is left out; an empty query
    /// lists the latest chats with a summary.
    /// </summary>
    public static List<Hit> Search(IEnumerable<Conversation> chats, string query, int max = 5)
    {
        var words = Words(query);
        var hits = new List<Hit>();
        foreach (var c in chats)
        {
            if (c.Items.Count == 0 && c.Recall.Length == 0) continue;
            if (words.Count == 0)
            {
                if (c.Recall.Length > 0) hits.Add(new Hit(c, c.Updated.Ticks / 1e18, []));
                continue;
            }
            double score = 0;
            var lines = new List<string>();
            var matched = 0;
            foreach (var w in words)
            {
                var any = false;
                if (Has(c.Title, w)) { score += 4; any = true; }
                if (Has(c.Name, w)) { score += 3; any = true; }
                if (Has(c.Recall, w)) { score += 2; any = true; }
                foreach (var item in c.Items)
                {
                    if (item.Kind is not ("user" or "assistant" or "tool")) continue;
                    var text = item.Kind == "tool" ? item.Name + " " + item.Text : item.Text;
                    if (!Has(text, w)) continue;
                    score += 0.5;
                    any = true;
                    if (lines.Count < 4)
                    {
                        var line = (item.Kind == "user" ? "User: " : item.Kind == "tool" ? "Step: " : "Claude: ") + Cap(OneLine(text), 240);
                        if (!lines.Contains(line)) lines.Add(line);
                    }
                }
                if (any) matched++;
            }
            if (matched == 0) continue;
            // Chats matching more of the words come first; a little for being recent breaks ties.
            score += matched * 5 + Math.Min(1, c.Updated.Ticks / 1e18);
            hits.Add(new Hit(c, score, lines));
        }
        return hits.OrderByDescending(h => h.Score).ThenByDescending(h => h.Chat.Updated).Take(max).ToList();
    }

    /// <summary>
    /// What one chat did, for "the same way as there": its summary, the user's requests and the steps taken with their
    /// settings, in order (the latest kept when it is long).
    /// </summary>
    public static (string Summary, List<string> Requests, List<string> Steps) Details(Conversation conv, int maxSteps = 60)
    {
        var requests = conv.Items.Where(i => i.Kind == "user" && i.Text.Trim().Length > 0).Select(i => Cap(OneLine(i.Text), 400)).ToList();
        var steps = conv.Items.Where(i => i.Kind == "tool")
            .Select(i => i.Name + (i.Input.Length > 0 ? " " + Cap(OneLine(i.Input), 500) : i.Text.Length > 0 ? " (" + Cap(OneLine(i.Text), 300) + ")" : "") + (i.State == "failed" ? " - failed" : ""))
            .ToList();
        if (requests.Count > 30) requests = requests.Skip(requests.Count - 30).ToList();
        if (steps.Count > maxSteps) steps = steps.Skip(steps.Count - maxSteps).ToList();
        return (conv.Recall, requests, steps);
    }

    private static bool Has(string text, string word) => text.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0;

    private static string OneLine(string text) =>
        string.Join(" ", (text ?? "").Replace("\r", " ").Replace("\n", " ").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));

    private static string Cap(string text, int max) => text.Length <= max ? text : text.Substring(0, max).TrimEnd() + "…";
}
