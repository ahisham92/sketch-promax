using System.IO;
using System.Text;
using System.Text.Json;

namespace SketchProMax.Tools;

/// <summary>
/// A job done before is replayed, not worked out again (bug 135). The owner, 22 Nov 2026: "if I gave the same tasks of today
/// tomorrow then we can get 100%", from all the history. Every job that ended done, in every chat on this PC, is remembered
/// with its title and task; its steps (the Revit tool and code calls that worked, in order) come from Claude Code's own
/// transcript of the job's session, read once and kept. A new job (or check) whose title and task read like one of them
/// starts with that job's steps: the code of each is one worked_code get away (id "recipe-key#N"), to run again with only
/// ids, levels and names changed. In %APPDATA%\SketchProMax\job-recipes; no Revit here (the tests compile it).
/// </summary>
internal static class JobRecipes
{
    public sealed class Recipe
    {
        public string Key { get; set; } = "";
        public string Title { get; set; } = "";
        public string Task { get; set; } = "";
        /// <summary>The job's Claude Code session, whose transcript holds its steps.</summary>
        public string Session { get; set; } = "";
        /// <summary>A teammate's check passed it (checks themselves and unchecked jobs: false).</summary>
        public bool Passed { get; set; }
        public DateTime Ended { get; set; }
        /// <summary>How many steps it has, once read (-1: not read yet).</summary>
        public int Steps { get; set; } = -1;
        /// <summary>From the owner's shared library (Sharing/Library): its steps are read from there.</summary>
        [System.Text.Json.Serialization.JsonIgnore] public bool Shared { get; set; }
    }

    /// <summary>One call of the job's that worked: the tool (bare name), what it said it did, and its whole input (JSON).</summary>
    public sealed record Step(string Tool, string What, string Input);

    /// <summary>How alike a new job's words must be to a done one's (Dice: twice the shared words over both counts), and at least how many words shared.</summary>
    public const double Alike = 0.45;
    public const int MinShared = 5;

    /// <summary>Steps shown in a hint; the rest are fetched by id like the others.</summary>
    public const int ShownSteps = 40;

    /// <summary>Calls that are about the job, not the model: not steps.</summary>
    private static readonly HashSet<string> NotSteps = new(StringComparer.Ordinal)
    {
        "chat_jobs", "wait_for_job", "cancel_job", "memory_note", "update_progress_checklist", "chat_questions", "ask_user", "recall_chats",
        "worked_code", "list_learned_tools", "save_as_tool", "get_activity_log", "list_revit_models", "use_revit_model", "auto_sync",
        "self_test_status", "report_bug",
    };

    private static readonly object Gate = new();

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax", "job-recipes");
    private static string IndexFile => Path.Combine(Folder, "index.json");
    private static string StepsFile(string key) => StepsFile(Folder, key);
    private static string StepsFile(string folder, string key) => Path.Combine(folder, "steps", Safe(key) + ".json");

    /// <summary>The owner's done jobs a licensed PC got with the shared library (Sharing/Library).</summary>
    public static string SharedFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax", "library", "job-recipes");
    private static string Safe(string key) => new((key ?? "").Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());

    /// <summary>
    /// The calls that worked in one session's transcript, in order: Sketch ProMax tools only (any name ending in the bare
    /// tool name after "__"), none of <see cref="NotSteps"/>, none that came back as an error.
    /// </summary>
    public static List<Step> FromTranscript(IEnumerable<string> lines)
    {
        var open = new Dictionary<string, (string Tool, string What, string Input)>(StringComparer.Ordinal);
        var steps = new List<Step>();
        foreach (var raw in lines)
        {
            if (raw.IndexOf("tool_", StringComparison.Ordinal) < 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (!doc.RootElement.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content) ||
                    content.ValueKind != JsonValueKind.Array) continue;
                foreach (var part in content.EnumerateArray())
                {
                    var type = part.TryGetProperty("type", out var ty) ? ty.GetString() : null;
                    if (type == "tool_use" && part.TryGetProperty("name", out var n) && part.TryGetProperty("id", out var id) && part.TryGetProperty("input", out var input))
                    {
                        var name = n.GetString() ?? "";
                        if (!name.Contains("sketch-promax", StringComparison.OrdinalIgnoreCase)) continue;
                        var bare = name.Substring(name.LastIndexOf("__", StringComparison.Ordinal) is var at and >= 0 ? at + 2 : 0);
                        if (NotSteps.Contains(bare) || id.GetString() is not { Length: > 0 } key) continue;
                        var what = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                            ? d.GetString() ?? "" : "";
                        open[key] = (bare, what, input.GetRawText());
                    }
                    else if (type == "tool_result" && part.TryGetProperty("tool_use_id", out var use) && use.GetString() is { } key && open.TryGetValue(key, out var call))
                    {
                        open.Remove(key);
                        if (part.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True) continue;
                        if (ResultSaysError(part)) continue;
                        steps.Add(new Step(call.Tool, call.What, call.Input.Length > WorkedCode.MaxCode ? call.Input.Substring(0, WorkedCode.MaxCode) : call.Input));
                    }
                }
            }
            catch (JsonException) { /* a line that isn't one message */ }
        }
        return steps;
    }

    // A tool's answer that is an error in its text (some connectors don't set is_error).
    private static bool ResultSaysError(JsonElement part)
    {
        if (!part.TryGetProperty("content", out var c)) return false;
        var text = c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" :
            c.ValueKind == JsonValueKind.Array ? string.Concat(c.EnumerateArray().Select(x => x.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : "")) : "";
        return text.StartsWith("Not run yet", StringComparison.Ordinal) || text.StartsWith("The code did not compile", StringComparison.Ordinal);
    }

    /// <summary>The done jobs that read like <paramref name="text"/>, best first (a passed one before an unchecked one of equal likeness).</summary>
    public static List<(Recipe Recipe, double Score)> Best(IEnumerable<Recipe> recipes, string text, string? exceptKey, int max = 2)
    {
        var asked = WorkedCode.Words(text);
        if (asked.Count < MinShared) return [];
        var found = new List<(Recipe, double)>();
        foreach (var r in recipes)
        {
            if (r.Steps == 0 || string.Equals(r.Key, exceptKey, StringComparison.Ordinal)) continue;
            var has = WorkedCode.Words(r.Title + " " + r.Task);
            var shared = asked.Count(has.Contains);
            if (shared < MinShared) continue;
            var score = 2.0 * shared / (asked.Count + has.Count);
            if (score < Alike) continue;
            found.Add((r, score + (r.Passed ? 0.05 : 0)));
        }
        return found.OrderByDescending(f => f.Item2).ThenByDescending(f => f.Item1.Ended).Take(max).ToList();
    }

    /// <summary>What a new job's task ends with: the earlier job and its steps, each fetched by id.</summary>
    public static string Hint(Recipe r, List<Step> steps)
    {
        if (steps.Count == 0) return "";
        var sb = new StringBuilder();
        sb.Append(r.Shared ? "\n\nThis kind of job was done before (the shared library)" : "\n\nThis kind of job was done before on this PC").Append(r.Passed ? " and passed its check" : "")
          .Append($": \"{OneLine(r.Title, 120)}\" ({r.Ended:yyyy-MM-dd}). Its calls that worked, in order:\n");
        for (var i = 0; i < steps.Count && i < ShownSteps; i++)
            sb.Append($"{i + 1}. {steps[i].Tool}{(steps[i].What.Length > 0 ? ": " + OneLine(steps[i].What, 120) : "")} [id {r.Key}#{i + 1}]\n");
        if (steps.Count > ShownSteps) sb.Append($"… and {steps.Count - ShownSteps} more (ids {r.Key}#{ShownSteps + 1} to #{steps.Count}).\n");
        sb.Append("Replay it rather than working it out again: worked_code action=get with a step's id gives its exact input (the code, or the " +
                  "tool's arguments). Run it again with only what differs changed (ids, levels, names, sizes). Read the model first where the " +
                  "earlier result may already be there, and skip steps that only looked around.");
        return sb.ToString();
    }

    /// <summary>A step's whole input by its id ("key#N"), or null.</summary>
    public static Step? StepById(string id)
    {
        var hash = (id ?? "").LastIndexOf('#');
        if (hash <= 0 || !int.TryParse(id!.Substring(hash + 1), out var n) || n < 1) return null;
        var key = id.Substring(0, hash);
        var steps = ReadSteps(key) ?? ReadSteps(SharedFolder, key);
        return steps is not null && n <= steps.Count ? steps[n - 1] : null;
    }

    // ---- On this PC ------------------------------------------------------------------------------------------------

    /// <summary>Remembers a job that ended done (or its check's verdict). Never throws.</summary>
    public static void Remember(string key, string title, string task, string? session, bool passed, DateTime ended)
    {
        if (key.Length == 0 || string.IsNullOrEmpty(session)) return;
        try
        {
            lock (Gate)
            {
                var index = ReadIndex();
                var r = index.FirstOrDefault(x => x.Key == key);
                if (r is null) index.Add(r = new Recipe { Key = key });
                if (r.Session != session) r.Steps = -1; // a new session: read again
                (r.Title, r.Task, r.Session, r.Ended) = (title, task, session!, ended);
                r.Passed |= passed;
                WriteIndex(index);
            }
        }
        catch (Exception) { /* remembering must not break a job */ }
    }

    /// <summary>The hint for a new job's title and task, or "" (nothing alike, or any trouble).</summary>
    public static string HintFor(string text, string? exceptKey)
    {
        try
        {
            List<Recipe> index;
            lock (Gate) index = ReadIndex();
            var all = WithShared(index, ReadIndex(SharedFolder));
            foreach (var (r, _) in Best(all, text, exceptKey, 3))
            {
                var steps = r.Shared ? ReadSteps(SharedFolder, r.Key) ?? [] : StepsOf(r, index);
                if (steps.Count > 0) return Hint(r, steps);
            }
        }
        catch (Exception) { }
        return "";
    }

    /// <summary>A recipe's steps: kept ones, else read from its transcript once and kept.</summary>
    private static List<Step> StepsOf(Recipe r, List<Recipe> index)
    {
        if (r.Steps >= 0 && ReadSteps(r.Key) is { } kept) return kept;
        var steps = Chat.HistoryArchive.Find(r.Session) is { } lines ? FromTranscript(lines()) : [];
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StepsFile(r.Key))!);
            File.WriteAllText(StepsFile(r.Key), JsonSerializer.Serialize(steps), new UTF8Encoding(false));
            r.Steps = steps.Count;
            var fresh = ReadIndex();
            if (fresh.FirstOrDefault(x => x.Key == r.Key) is { } same) same.Steps = steps.Count;
            WriteIndex(fresh);
        }
        return steps;
    }

    /// <summary>This PC's done jobs, then the shared library's (with their steps read) whose key this PC doesn't have.</summary>
    internal static List<Recipe> WithShared(List<Recipe> mine, List<Recipe> shared)
    {
        var all = new List<Recipe>(mine);
        var have = new HashSet<string>(mine.Select(r => r.Key), StringComparer.Ordinal);
        foreach (var r in shared)
            if (r.Key.Length > 0 && r.Steps > 0 && have.Add(r.Key)) { r.Shared = true; all.Add(r); }
        return all;
    }

    /// <summary>
    /// The owner's PC, before the library is uploaded: every done job whose steps were never read has them read from its
    /// transcript now, so it travels with them. Never throws.
    /// </summary>
    public static void FillSteps()
    {
        try
        {
            List<Recipe> index;
            lock (Gate) index = ReadIndex();
            foreach (var r in index.Where(r => r.Steps < 0).ToList())
                try { StepsOf(r, index); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        catch (Exception) { }
    }

    private static List<Step>? ReadSteps(string key) => ReadSteps(Folder, key);

    private static List<Step>? ReadSteps(string folder, string key)
    {
        try
        {
            var file = StepsFile(folder, key);
            return File.Exists(file) ? JsonSerializer.Deserialize<List<Step>>(File.ReadAllText(file)) : null;
        }
        catch (Exception) { return null; }
    }

    private static List<Recipe> ReadIndex() => ReadIndex(Folder);

    private static List<Recipe> ReadIndex(string folder)
    {
        var file = Path.Combine(folder, "index.json");
        try { return File.Exists(file) ? JsonSerializer.Deserialize<List<Recipe>>(File.ReadAllText(file)) ?? [] : []; }
        catch (Exception) { return []; }
    }

    private static void WriteIndex(List<Recipe> index)
    {
        Directory.CreateDirectory(Folder);
        var temp = IndexFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(index), new UTF8Encoding(false));
        if (File.Exists(IndexFile)) File.Replace(temp, IndexFile, null);
        else File.Move(temp, IndexFile);
    }

    private static string OneLine(string text, int max)
    {
        var t = System.Text.RegularExpressions.Regex.Replace(text ?? "", @"\s+", " ").Trim();
        return t.Length > max ? t.Substring(0, max) + "…" : t;
    }

    private static string BackfillMark => Path.Combine(Folder, "from history.txt");

    /// <summary>
    /// Once per PC, off Revit's thread: every job that ended done in every saved chat is remembered (steps are read when a
    /// new job first needs them). <paramref name="jobsFolder"/> gives a chat's jobs folder (its sessions.json). Never throws.
    /// </summary>
    public static void BackfillOnce(Func<Chat.Conversation, string> jobsFolder) => System.Threading.Tasks.Task.Run(() =>
    {
        try
        {
            if (File.Exists(BackfillMark)) return;
            var count = 0;
            foreach (var conv in Chat.ChatStore.All())
            {
                var folder = jobsFolder(conv);
                foreach (var job in conv.Jobs.Where(j => j.State == Chat.JobBoard.Done && j.GoalCheck == 0))
                {
                    var session = Chat.JobSessions.Get(folder, job.Key);
                    if (string.IsNullOrEmpty(session)) continue;
                    Remember(job.Key, job.Title, job.Task, session, job.PassedAt > DateTime.MinValue, job.Ended > DateTime.MinValue ? job.Ended : job.Created);
                    count++;
                }
            }
            Directory.CreateDirectory(Folder);
            File.WriteAllText(BackfillMark, $"{DateTime.UtcNow:s} remembered {count} done jobs from every chat");
        }
        catch (Exception) { /* tried again next start */ }
    });
}
