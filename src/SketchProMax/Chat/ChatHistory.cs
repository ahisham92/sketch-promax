using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SketchProMax.Chat;

/// <summary>One line of a saved conversation, as the pane shows it.</summary>
internal sealed class ChatItem
{
    /// <summary>user, assistant, tool, note, error, brief or job.</summary>
    public string Kind { get; set; } = "";
    public string Text { get; set; } = "";
    /// <summary>Tools: the short tool name. User: attachment names.</summary>
    public string Name { get; set; } = "";
    /// <summary>Tools: running, done or failed. Briefs: pending, run, edited, typed, stale or failed.</summary>
    public string State { get; set; } = "";
    /// <summary>When it was added (UTC); default for items saved before 0.31, which then show no time.</summary>
    public DateTime Time { get; set; }
    /// <summary>Tools: the input, indented JSON (shortened), for the step's details.</summary>
    public string Input { get; set; } = "";
    /// <summary>Tools: the start of the result, for the step's details and its element ids.</summary>
    public string Result { get; set; } = "";
    /// <summary>User: the full paths of the attached files, for thumbnails.</summary>
    public List<string> Files { get; set; } = [];
    /// <summary>Briefs: the PromptBrief as JSON, with the one-tap answers.</summary>
    public string Brief { get; set; } = "";
    /// <summary>Jobs: the live job card (JobCardModel) as JSON: its steps, how each ended, and the summary.</summary>
    public string Job { get; set; } = "";
}

/// <summary>A saved conversation: the Claude Code session to resume and what the pane showed.</summary>
internal sealed class Conversation
{
    /// <summary>The model it belongs to (ChatPane.ModelKey).</summary>
    public string Key { get; set; } = "";
    /// <summary>The model's title.</summary>
    public string Title { get; set; } = "";
    /// <summary>This chat among the model's chats; empty in a file saved before 0.31 (one chat per model).</summary>
    public string Id { get; set; } = "";
    /// <summary>The chat's name: made from the first request, or what the user renamed it to.</summary>
    public string Name { get; set; } = "";
    public bool Pinned { get; set; }
    public string SessionId { get; set; } = "";
    public DateTime Updated { get; set; }
    public List<ChatItem> Items { get; set; } = [];

    /// <summary>The file it was read from (not saved).</summary>
    [JsonIgnore] public string File { get; set; } = "";
    /// <summary>The messages as last read or saved (not saved): a save moves <see cref="Updated"/> only when they changed.</summary>
    [JsonIgnore] public string? SavedItems { get; set; }
    /// <summary>The whole chat as last read or saved (not saved): an unchanged chat isn't written again.</summary>
    [JsonIgnore] public string? SavedJson { get; set; }
}

/// <summary>A saved chat as the history drawer lists it, without its messages.</summary>
internal sealed record ChatSummary(string File, string Key, string ModelTitle, string Name, bool Pinned, DateTime Updated, int Count, string Search);

/// <summary>
/// The history drawer's rules, which need no WPF: chat names from the first request, the date groups, search, and the
/// order (pinned first, then by date; the open model's chats first or only).
/// </summary>
internal static class ChatHistory
{
    public const string PinnedGroup = "Pinned", Week = "Last 7 days", Month = "Last 30 days", Older = "Older";
    public static readonly string[] Groups = [PinnedGroup, Week, Month, Older];

    private static readonly Regex Space = new(@"\s+", RegexOptions.CultureInvariant);
    private static readonly Regex Markup = new(@"[`*_#>\[\]]+", RegexOptions.CultureInvariant);

    /// <summary>
    /// A chat's name from its first request: the first line, without Markdown marks, at most <paramref name="max"/>
    /// characters, cut at a word. "New chat" when there is no text.
    /// </summary>
    public static string AutoTitle(string? request, int max = 48)
    {
        var line = (request ?? "").Replace("\r", "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        line = Space.Replace(Markup.Replace(line, ""), " ").Trim().TrimEnd('.', ':', ';', ',');
        if (line.Length == 0) return "New chat";
        if (line.Length <= max) return char.ToUpperInvariant(line[0]) + line.Substring(1);
        var cut = line.LastIndexOf(' ', max - 1);
        line = line.Substring(0, cut > max / 2 ? cut : max - 1).TrimEnd(' ', ',', ';', ':', '-');
        return char.ToUpperInvariant(line[0]) + line.Substring(1) + "…";
    }

    /// <summary>The date group of a chat last updated at <paramref name="updatedLocal"/>.</summary>
    public static string GroupOf(DateTime updatedLocal, DateTime nowLocal)
    {
        var days = (nowLocal.Date - updatedLocal.Date).TotalDays;
        return days < 7 ? Week : days < 30 ? Month : Older;
    }

    /// <summary>True when every word of <paramref name="query"/> is in the chat's name, model or messages (any case).</summary>
    public static bool Matches(ChatSummary chat, string? query)
    {
        var words = Space.Split((query ?? "").Trim()).Where(w => w.Length > 0).ToList();
        if (words.Count == 0) return true;
        var hay = chat.Name + "\n" + chat.ModelTitle + "\n" + chat.Search;
        return words.All(w => hay.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    /// <summary>
    /// The drawer's sections in order (Pinned, Last 7 days, Last 30 days, Older; empty ones left out), each newest first.
    /// With <paramref name="allModels"/> off only <paramref name="modelKey"/>'s chats are listed; with it on, that model's
    /// chats come first in each section.
    /// </summary>
    public static List<(string Group, List<ChatSummary> Chats)> Arrange(IEnumerable<ChatSummary> chats, string modelKey, bool allModels, string? query, DateTime nowLocal)
    {
        bool Mine(ChatSummary c) => string.Equals(c.Key, modelKey, StringComparison.OrdinalIgnoreCase);
        var shown = chats.Where(c => (allModels || Mine(c)) && Matches(c, query)).ToList();
        return Groups
            .Select(g => (Group: g, Chats: shown
                .Where(c => (c.Pinned ? PinnedGroup : GroupOf(c.Updated.ToLocalTime(), nowLocal)) == g)
                .OrderByDescending(Mine).ThenByDescending(c => c.Updated).ToList()))
            .Where(s => s.Chats.Count > 0)
            .ToList();
    }

    /// <summary>The summary of a conversation: its name (made from the first request when it has none) and searchable text.</summary>
    public static ChatSummary Summarize(Conversation conv)
    {
        var search = new StringBuilder();
        foreach (var item in conv.Items)
        {
            if (search.Length > 4000) break;
            if (item.Kind is "user" or "assistant") search.Append(item.Text.Length > 600 ? item.Text.Substring(0, 600) : item.Text).Append('\n');
        }
        var name = conv.Name.Length > 0 ? conv.Name : AutoTitle(conv.Items.FirstOrDefault(i => i.Kind == "user")?.Text);
        return new ChatSummary(conv.File, conv.Key, conv.Title, name, conv.Pinned, conv.Updated, conv.Items.Count, search.ToString());
    }
}
