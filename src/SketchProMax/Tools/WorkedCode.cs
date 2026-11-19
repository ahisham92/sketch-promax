using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SketchProMax.Tools;

/// <summary>
/// Code that worked once is not written again. Every run_revit_code and run_revit_query_code that worked is kept here
/// with what it did (its description), so the next chat or job that needs the same work finds it (worked_code tool, the
/// pointer on a failed code call, the line that goes with a typed message) and adapts it instead of starting from an
/// empty page. The 2026-11-17 review: 1,166 code calls in a month, 161 failed, the same failures met again and again,
/// and only one call of a saved tool, because code written a little differently each time never counted as "the same".
/// The library stays on this PC, inside Sketch ProMax (the owner's choice, 2026-11-17: not shared between users);
/// code that worked three times becomes a ready-made tool (CodeRepeats). One entry per shape (CodeRepeats.Shape), the
/// newest code kept. In %APPDATA%\SketchProMax\worked-code.json; no Revit
/// here (the tests compile it).
/// </summary>
internal static class WorkedCode
{
    /// <summary>
    /// The most entries kept (the oldest unused go first), the shortest code worth keeping, the longest kept. 400 filled
    /// in two days on 19 Nov 2026 (every adapted copy was a new entry), so older code was being dropped.
    /// </summary>
    public const int MaxEntries = 3000, MinLength = 150, MaxCode = 20000;

    /// <summary>
    /// How alike two pieces of code must be (shared runs of five words, 0 to 1) for the newer to count as the older one
    /// adapted: another use of the same entry, not a new one.
    /// </summary>
    public const double SameWork = 0.55;

    internal sealed class Entry
    {
        public string Id { get; set; } = "";
        public string Description { get; set; } = "";
        public string Code { get; set; } = "";
        /// <summary>run_revit_code or run_revit_query_code.</summary>
        public string Tool { get; set; } = "";
        public bool OwnTransactions { get; set; }
        public string Model { get; set; } = "";
        public DateTime First { get; set; }
        public DateTime Last { get; set; }
        /// <summary>How many times code of this shape worked.</summary>
        public int Uses { get; set; }
    }

    internal sealed record Match(Entry Entry, int Shared, double Score);

    private static readonly object Gate = new();

    public static string StoreFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax", "worked-code.json");

    // ---- Keeping -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Keeps one run that worked in <paramref name="entries"/>; the entry, or null when it isn't worth keeping (a short
    /// snippet, no description, too long). Code of a shape already kept replaces the older code and counts as one more use.
    /// </summary>
    public static Entry? Keep(List<Entry> entries, string code, string description, string tool, bool ownTransactions, string model, DateTime now,
        Dictionary<Entry, HashSet<string>>? cache = null)
    {
        code = (code ?? "").Trim();
        description = (description ?? "").Trim();
        if (description.Length < 4 || code.Length > MaxCode) return null;
        var shape = CodeRepeats.Shape(code);
        if (shape.Length < MinLength) return null;
        var id = CodeRepeats.Key(shape).Substring(0, 8);
        var entry = entries.FirstOrDefault(e => e.Id == id) ?? Adapted(entries, shape, description, tool, cache);
        if (entry is null)
        {
            if (entries.Count >= MaxEntries) entries.Remove(entries.OrderBy(e => e.Uses > 1).ThenBy(e => e.Last).First());
            entries.Add(entry = new Entry { Id = id, First = now });
        }
        cache?.Remove(entry);
        entry.Description = description;
        entry.Code = code;
        entry.Tool = tool;
        entry.OwnTransactions = ownTransactions;
        if (model.Length > 0) entry.Model = model;
        entry.Last = now;
        entry.Uses++;
        return entry;
    }

    /// <summary>
    /// The kept entry that <paramref name="shape"/> adapts, or null: the same tool, code that shares most of its runs of
    /// five words (<see cref="SameWork"/>) and a description that shares a word. On 19 Nov 2026 code taken from the library
    /// and changed a little (one more filter, other ids) was kept as a new entry each time: 400 entries, nearly all "worked
    /// 1x", so nothing ever reached the three uses that make a ready-made tool.
    /// </summary>
    internal static Entry? Adapted(List<Entry> entries, string shape, string description, string tool, Dictionary<Entry, HashSet<string>>? cache = null)
    {
        var mine = Shingles(shape);
        if (mine.Count == 0) return null;
        var words = Words(description);
        Entry? best = null;
        var bestScore = SameWork;
        foreach (var e in entries)
        {
            if (!e.Tool.Equals(tool, StringComparison.Ordinal) || (words.Count > 0 && !KnownWords(e.Description).Overlaps(words))) continue;
            if (cache is null || !cache.TryGetValue(e, out var theirs))
            {
                theirs = Known(e.Code);
                if (cache is not null) cache[e] = theirs;
            }
            // Too different in size to be alike enough: skip the count.
            if (theirs.Count == 0 || Math.Min(theirs.Count, mine.Count) < SameWork * Math.Max(theirs.Count, mine.Count)) continue;
            var shared = mine.Count(theirs.Contains);
            var score = shared / (double)(mine.Count + theirs.Count - shared);
            if (score >= bestScore) (best, bestScore) = (e, score);
        }
        return best;
    }

    /// <summary>Shingles of code already seen in this Revit session, by the code itself (the library is read again for every keep).</summary>
    private static readonly Dictionary<string, HashSet<string>> Seen = new(StringComparer.Ordinal);

    private static HashSet<string> Known(string code)
    {
        lock (Seen)
        {
            if (Seen.TryGetValue(code, out var known)) return known;
            if (Seen.Count > 2 * MaxEntries) Seen.Clear();
            return Seen[code] = Shingles(CodeRepeats.Shape(code));
        }
    }

    private static readonly Dictionary<string, HashSet<string>> SeenWords = new(StringComparer.Ordinal);

    private static HashSet<string> KnownWords(string description)
    {
        lock (SeenWords)
        {
            if (SeenWords.TryGetValue(description, out var known)) return known;
            if (SeenWords.Count > 2 * MaxEntries) SeenWords.Clear();
            return SeenWords[description] = Words(description);
        }
    }

    private static readonly Regex Token = new(@"[A-Za-z_][A-Za-z0-9_]*|\d+|[^\sA-Za-z0-9_]", RegexOptions.Compiled);

    /// <summary>Runs of five tokens of the code's shape, for <see cref="Adapted"/>.</summary>
    internal static HashSet<string> Shingles(string shape)
    {
        var tokens = Token.Matches(shape).Cast<System.Text.RegularExpressions.Match>().Select(m => m.Value).ToList();
        var set = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i + 5 <= tokens.Count; i++) set.Add(string.Join(" ", tokens.GetRange(i, 5)));
        return set;
    }

    // ---- Finding -------------------------------------------------------------------------------------------------------

    private static readonly Regex Word = new(@"[A-Za-z][A-Za-z0-9]+", RegexOptions.Compiled);

    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "from", "that", "this", "these", "those", "into", "onto", "all", "any", "each", "every", "are", "was", "were",
        "has", "have", "had", "not", "but", "its", "their", "them", "then", "than", "can", "could", "should", "would", "will", "you", "your", "our",
        "please", "need", "want", "make", "made", "get", "set", "use", "using", "also", "only", "just", "now", "again", "still", "same", "one", "two",
        "what", "which", "where", "when", "why", "how", "there", "here", "does", "did", "done", "check", "find", "list", "show", "read", "see",
        "element", "elements", "revit", "model", "code", "new", "per", "via", "out", "off", "over", "under", "some", "other", "like",
    };

    /// <summary>The words that say what a text is about: lower case, no common words, plurals folded ("piles" is "pile").</summary>
    public static HashSet<string> Words(string text)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        foreach (System.Text.RegularExpressions.Match m in Word.Matches(text ?? ""))
        {
            var w = m.Value.ToLowerInvariant();
            if (w.Length < 3 || Common.Contains(w)) continue;
            if (w.Length > 4 && w.EndsWith("ies", StringComparison.Ordinal)) w = w.Substring(0, w.Length - 3) + "y";
            else if (w.Length > 3 && w.EndsWith("s", StringComparison.Ordinal) && !w.EndsWith("ss", StringComparison.Ordinal)) w = w.Substring(0, w.Length - 1);
            if (w.Length > 5 && w.EndsWith("ing", StringComparison.Ordinal)) w = w.Substring(0, w.Length - 3);
            else if (w.Length > 4 && w.EndsWith("ed", StringComparison.Ordinal)) w = w.Substring(0, w.Length - 2);
            words.Add(w);
        }
        return words;
    }

    /// <summary>
    /// The kept code that did work like <paramref name="text"/>, best first: at least <paramref name="minShared"/> words
    /// of the text are in its description. Among equals, the one used more and later.
    /// </summary>
    public static List<Match> Find(IEnumerable<Entry> entries, string text, int max = 5, int minShared = 2)
    {
        var asked = Words(text);
        if (asked.Count == 0) return [];
        var found = new List<Match>();
        foreach (var entry in entries)
        {
            var has = Words(entry.Description);
            if (has.Count == 0) continue;
            var shared = asked.Count(has.Contains);
            if (shared < Math.Min(minShared, asked.Count)) continue;
            // How much of the description the text covers counts too, so a long description doesn't win on two stray words.
            found.Add(new Match(entry, shared, shared / (double)asked.Count + shared / (double)has.Count));
        }
        return found.OrderByDescending(m => m.Shared).ThenByDescending(m => m.Score).ThenByDescending(m => m.Entry.Uses).ThenByDescending(m => m.Entry.Last)
            .Take(max).ToList();
    }

    public static string Line(Entry e) =>
        $"{e.Id}: {OneLine(e.Description, 140)} ({e.Tool}{(e.OwnTransactions ? ", own_transactions" : "")}, worked {e.Uses}x, last {e.Last:yyyy-MM-dd}" +
        (e.Model.Length > 0 ? $", in {e.Model}" : "") + ")";

    private static string OneLine(string text, int max)
    {
        var t = Regex.Replace(text ?? "", @"\s+", " ").Trim();
        return t.Length > max ? t.Substring(0, max) + "…" : t;
    }

    /// <summary>What a failed code call's result ends with when code that did similar work is kept: use that, don't try a third way.</summary>
    public static string Pointer(IEnumerable<Entry> entries, string description, string failedCode)
    {
        var shape = CodeRepeats.Shape(failedCode ?? "");
        var id = shape.Length >= MinLength ? CodeRepeats.Key(shape).Substring(0, 8) : "";
        var found = Find(entries.Where(e => e.Id != id), description, 2, 3);
        if (found.Count == 0) return "";
        return "Code that did similar work and worked before is kept: " + string.Join("; ", found.Select(m => Line(m.Entry))) +
               ". Get it with worked_code (action=get, id) and adapt it, rather than writing it another way.";
    }

    /// <summary>
    /// What goes to the chat with a typed message (never shown) when code that did this kind of work is kept. "" when
    /// nothing is close enough: at least three of the message's words in one description.
    /// </summary>
    public static string Hint(IEnumerable<Entry> entries, string message)
    {
        var found = Find(entries, message, 2, 3);
        if (found.Count == 0) return "";
        return "\n\n[Sketch ProMax, not typed by the user: code that did similar work and worked is kept: " +
               string.Join("; ", found.Select(m => Line(m.Entry))) +
               ". When this needs code, get it with worked_code (action=get, id) and adapt it instead of writing it again.]";
    }

    // ---- The file on this PC -------------------------------------------------------------------------------------------

    /// <summary><see cref="Keep"/> against the file on this PC. Never throws: keeping must not break a call.</summary>
    public static void Note(string code, string description, string tool, bool ownTransactions, string model) =>
        // Off Revit's thread: reading, comparing with every kept entry and writing the library takes a while.
        Task.Run(() => NoteNow(code, description, tool, ownTransactions, model));

    private static void NoteNow(string code, string description, string tool, bool ownTransactions, string model)
    {
        try
        {
            lock (Gate)
            {
                var entries = Read();
                if (Keep(entries, code, description, tool, ownTransactions, model, DateTime.UtcNow) is null) return;
                Directory.CreateDirectory(Path.GetDirectoryName(StoreFile)!);
                var temp = StoreFile + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(entries), new UTF8Encoding(false));
                if (File.Exists(StoreFile)) File.Replace(temp, StoreFile, null);
                else File.Move(temp, StoreFile);
            }
        }
        catch (Exception) { /* not kept this time */ }
    }

    /// <summary>
    /// The code written before the library began (17 Nov 2026) is kept too: every code change that worked is in the redo
    /// logs (RedoLog, the whole arguments), so it goes into the library once, oldest first, as if kept when it ran (19 Nov
    /// 2026: grading done two weeks before was written again from nothing). Returns how many runs were taken in.
    /// </summary>
    internal static int FromRedo(List<Entry> entries, IEnumerable<(string Model, RedoLog.Step Step)> steps)
    {
        var cache = new Dictionary<Entry, HashSet<string>>();
        var taken = 0;
        foreach (var (model, step) in steps.OrderBy(s => s.Step.Time))
        {
            if (step.Tool is not ("run_revit_code" or "run_revit_query_code")) continue;
            try
            {
                using var doc = JsonDocument.Parse(step.Arguments);
                var r = doc.RootElement;
                string Text(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                var own = r.TryGetProperty("own_transactions", out var o) && o.ValueKind == JsonValueKind.True;
                if (Keep(entries, Text("code"), Text("description"), step.Tool, own, model, step.Time.ToUniversalTime(), cache) is not null) taken++;
            }
            catch (JsonException) { /* an unreadable step is skipped */ }
        }
        return taken;
    }

    /// <summary>
    /// Every code call that worked in Claude Code's own transcripts of the chats and jobs on this PC (…\.claude\projects\
    /// …\&lt;session&gt;.jsonl, one JSON line per message: a tool_use with its whole input, then its tool_result with
    /// is_error): reads too (clash checks, level checks), which the redo logs don't keep. The owner, 19 Nov 2026: "all,
    /// meaning all, to avoid wasting time". The runs found, oldest first (model unknown: "").
    /// </summary>
    internal static List<(string Model, RedoLog.Step Step)> FromTranscript(IEnumerable<string> lines)
    {
        var calls = new Dictionary<string, (DateTime Time, string Tool, string Args)>(StringComparer.Ordinal);
        var worked = new List<(string, RedoLog.Step)>();
        foreach (var raw in lines)
        {
            if (raw.IndexOf("tool_", StringComparison.Ordinal) < 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var r = doc.RootElement;
                if (!r.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
                var time = r.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.String && DateTime.TryParse(ts.GetString(),
                    System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal, out var t) ? t : DateTime.MinValue;
                foreach (var part in content.EnumerateArray())
                {
                    var type = part.TryGetProperty("type", out var ty) ? ty.GetString() : null;
                    if (type == "tool_use" && part.TryGetProperty("name", out var name) && part.TryGetProperty("id", out var id) && part.TryGetProperty("input", out var input))
                    {
                        var tool = name.GetString() ?? "";
                        var bare = tool.EndsWith("run_revit_query_code", StringComparison.Ordinal) ? "run_revit_query_code"
                            : tool.EndsWith("run_revit_code", StringComparison.Ordinal) ? "run_revit_code" : null;
                        if (bare is not null && id.GetString() is { Length: > 0 } key) calls[key] = (time, bare, input.GetRawText());
                    }
                    else if (type == "tool_result" && part.TryGetProperty("tool_use_id", out var use) && use.GetString() is { } key && calls.TryGetValue(key, out var call))
                    {
                        calls.Remove(key);
                        var error = part.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
                        if (!error) worked.Add(("", new RedoLog.Step(0, call.Time, call.Tool, call.Args, "")));
                    }
                }
            }
            catch (JsonException) { /* a line that isn't one message */ }
        }
        return worked;
    }

    private static string BackfillMark => StoreFile + ".from-history";

    /// <summary>
    /// Once per PC, off Revit's thread: <see cref="FromRedo"/> over every model's redo log and every chat and job transcript
    /// (<see cref="FromTranscript"/>), all of it, oldest first. Never throws.
    /// </summary>
    public static void BackfillOnce() => Task.Run(() =>
    {
        try
        {
            if (File.Exists(BackfillMark)) return;
            var steps = RedoLog.Models().SelectMany(m => RedoLog.Read(m.Model).Select(s => (m.Model, s))).ToList();
            // Claude Code's transcripts, and the archived copies of those it has since deleted (Chat.HistoryArchive).
            foreach (var lines in Chat.HistoryArchive.AllTranscripts())
            {
                try { steps.AddRange(FromTranscript(lines())); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { /* a file in use is skipped */ }
            }
            lock (Gate)
            {
                var entries = Read();
                var taken = FromRedo(entries, steps);
                Directory.CreateDirectory(Path.GetDirectoryName(StoreFile)!);
                var temp = StoreFile + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(entries), new UTF8Encoding(false));
                if (File.Exists(StoreFile)) File.Replace(temp, StoreFile, null);
                else File.Move(temp, StoreFile);
                // The mark says when, and how much came in from how many models (the library's size after).
                File.WriteAllText(BackfillMark, $"{DateTime.UtcNow:s} took in {taken} runs that worked (redo logs and chat transcripts); {entries.Count} kept");
            }
        }
        catch (Exception) { /* tried again next start */ }
    });

    public static List<Entry> Load()
    {
        lock (Gate) return Read();
    }

    /// <summary>The pointer for a failed call, from the file; "" on any trouble.</summary>
    public static string PointerFor(string description, string failedCode)
    {
        try { return Pointer(Load(), description, failedCode); }
        catch (Exception) { return ""; }
    }

    /// <summary>The line for a typed message, from the file; "" on any trouble.</summary>
    public static string HintFor(string message)
    {
        try { return Hint(Load(), message); }
        catch (Exception) { return ""; }
    }

    private static List<Entry> Read()
    {
        try
        {
            if (File.Exists(StoreFile))
                return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(StoreFile)) ?? [];
        }
        catch (Exception) { /* a broken file starts again */ }
        return [];
    }
}
