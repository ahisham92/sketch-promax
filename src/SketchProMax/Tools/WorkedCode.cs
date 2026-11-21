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
    /// The shortest code worth keeping and the longest kept. There is no limit on how many are kept (the owner, 19 Nov
    /// 2026: "why don't we have something unlimited ... sorted so the search becomes easier"): a small index is searched
    /// and each code is a file of its own, in a folder per topic (<see cref="Topic"/>).
    /// </summary>
    public const int MinLength = 150, MaxCode = 20000;

    /// <summary>How many numbers sum up a code's shape in the index (<see cref="Signature"/>), to compare without reading it.</summary>
    public const int SignatureSize = 32;

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
        /// <summary>What it works on, from the Revit categories and classes in its code ("Structural Columns", "Floors"): its folder.</summary>
        public string Topic { get; set; } = "";
        /// <summary>The shape of its code in a few numbers (<see cref="Signature"/>) and how many runs of five words it has.</summary>
        public uint[]? Sig { get; set; }
        public int Size { get; set; }
        /// <summary>Changed since read: its code file is written with the index.</summary>
        [System.Text.Json.Serialization.JsonIgnore] public bool Dirty { get; set; }
    }

    internal sealed record Match(Entry Entry, int Shared, double Score);

    private static readonly object Gate = new();

    public static string StoreFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax", "worked-code.json");

    // ---- Keeping -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Keeps one run that worked in <paramref name="entries"/>; the entry, or null when it isn't worth keeping (a short
    /// snippet, no description, too long). Code of a shape already kept replaces the older code and counts as one more use.
    /// </summary>
    public static Entry? Keep(List<Entry> entries, string code, string description, string tool, bool ownTransactions, string model, DateTime now)
    {
        code = (code ?? "").Trim();
        description = (description ?? "").Trim();
        if (description.Length < 4 || code.Length > MaxCode) return null;
        var shape = CodeRepeats.Shape(code);
        if (shape.Length < MinLength) return null;
        var id = CodeRepeats.Key(shape).Substring(0, 8);
        var shingles = Shingles(shape);
        var entry = entries.FirstOrDefault(e => e.Id == id) ?? Adapted(entries, shingles, description, tool);
        if (entry is null) entries.Add(entry = new Entry { Id = id, First = now });
        entry.Sig = Signature(shingles);
        entry.Size = shingles.Count;
        entry.Topic = Topic(code, description);
        entry.Dirty = true;
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
    internal static Entry? Adapted(List<Entry> entries, HashSet<string> mine, string description, string tool)
    {
        if (mine.Count == 0) return null;
        var sig = Signature(mine);
        var words = Words(description);
        Entry? best = null;
        var bestScore = SameWork;
        foreach (var e in entries)
        {
            if (!e.Tool.Equals(tool, StringComparison.Ordinal) || (words.Count > 0 && !KnownWords(e.Description).Overlaps(words))) continue;
            // An entry kept before signatures: its signature from its code, once.
            if (e.Sig is not { Length: SignatureSize } && e.Code.Length > 0)
            {
                var theirs = Known(e.Code);
                (e.Sig, e.Size) = (Signature(theirs), theirs.Count);
            }
            if (e.Sig is not { Length: SignatureSize } other || e.Size == 0) continue;
            // Too different in size to be alike enough: skip it.
            if (Math.Min(e.Size, mine.Count) < SameWork * Math.Max(e.Size, mine.Count)) continue;
            var same = 0;
            for (var i = 0; i < SignatureSize; i++) if (sig[i] == other[i]) same++;
            var score = same / (double)SignatureSize;
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
            if (Seen.Count > 5000) Seen.Clear();
            return Seen[code] = Shingles(CodeRepeats.Shape(code));
        }
    }

    private static readonly Dictionary<string, HashSet<string>> SeenWords = new(StringComparer.Ordinal);

    private static HashSet<string> KnownWords(string description)
    {
        lock (SeenWords)
        {
            if (SeenWords.TryGetValue(description, out var known)) return known;
            if (SeenWords.Count > 50000) SeenWords.Clear();
            return SeenWords[description] = Words(description);
        }
    }

    private static readonly Regex Token = new(@"[A-Za-z_][A-Za-z0-9_]*|\d+|[^\sA-Za-z0-9_]", RegexOptions.Compiled);

    /// <summary>
    /// The runs of five words of a code in <see cref="SignatureSize"/> numbers (MinHash): two codes agree in about as
    /// many of them as the share of runs they have in common, so code is compared from the index without being read.
    /// </summary>
    internal static uint[] Signature(HashSet<string> shingles)
    {
        var sig = Enumerable.Repeat(uint.MaxValue, SignatureSize).ToArray();
        foreach (var s in shingles)
        {
            var h = Fnv(s);
            for (var i = 0; i < SignatureSize; i++)
            {
                var v = Mix(h ^ (0x9E3779B97F4A7C15UL * (ulong)(i + 1)));
                if (v < sig[i]) sig[i] = v;
            }
        }
        return sig;
    }

    private static ulong Fnv(string s)
    {
        var h = 14695981039346656037UL;
        foreach (var c in s) { h ^= c; h *= 1099511628211UL; }
        return h;
    }

    private static uint Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return (uint)((z ^ (z >> 31)) >> 32);
    }

    private static readonly Regex Category = new(@"OST_([A-Za-z]+)", RegexOptions.Compiled);
    private static readonly Regex OfType = new(@"typeof\(\s*(Floor|Wall|FamilyInstance|View\w*|ViewSheet|Grid|Level|RevitLinkInstance|ImportInstance|CurveElement|DetailLine|TextNote|IndependentTag|Dimension|SpotDimension|Rebar|Pipe|Duct|RoofBase|Ceiling|Room|Group|FilledRegion|ScheduleSheetInstance|Viewport)\s*\)", RegexOptions.Compiled);

    /// <summary>
    /// What a code works on, for its folder and the index: the Revit category it names most ("OST_StructuralColumns" is
    /// "Structural Columns"), else the Revit class it collects ("Floor" is "Floors"), else "General".
    /// </summary>
    public static string Topic(string code, string description = "")
    {
        var named = Category.Matches(code ?? "").Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value)
            .GroupBy(c => c).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        if (named is { Length: > 0 }) return Spaced(named);
        if (OfType.Match(code ?? "") is { Success: true } t)
        {
            var c = t.Groups[1].Value;
            return c switch { "FamilyInstance" => "Family Instances", "RoofBase" => "Roofs", "ImportInstance" => "CAD Imports", "RevitLinkInstance" => "Revit Links",
                "Viewport" => "Viewports", _ when c.StartsWith("View", StringComparison.Ordinal) => "Views and Sheets", _ => Spaced(c) + "s" };
        }
        return "General";
    }

    private static string Spaced(string name) => Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", " ");

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

    /// <summary>
    /// What a job's task ends with when code that did its kind of work is kept (bug 127, 21 Nov 2026: the jobs wrote fresh
    /// code 1,505 times in a day, 103 failed, and got kept code 42 times; the kept-code line only went with typed messages,
    /// never to a job). "" when nothing is close: at least three of the task's words in one description.
    /// </summary>
    public static string JobHint(IEnumerable<Entry> entries, string task)
    {
        var found = Find(entries, task, 5, 3);
        if (found.Count == 0) return "";
        return "\n\nCode that already worked for this kind of work is kept on this PC:\n" +
               string.Join("\n", found.Select(m => "- " + Line(m.Entry))) +
               "\nStart from it: worked_code (action=get, id) and adapt it. Where it falls short, fix that code and run the fixed one " +
               "(the fix is kept for next time) rather than starting over. Write new code only for what none of these does, and " +
               "before writing any, try worked_code (action=find) with what you need.";
    }

    // ---- The library on this PC --------------------------------------------------------------------------------------

    /// <summary>The library's folder: index.json (what is searched) and a folder per topic with one .cs file per code.</summary>
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax", "worked-code");

    private static string IndexFile => Path.Combine(Folder, "index.json");

    /// <summary>The code of a kept entry: in memory, else its file (its topic's folder, else wherever it is). "" when gone.</summary>
    public static string CodeOf(Entry e)
    {
        if (e.Code.Length > 0) return e.Code;
        try
        {
            var file = CodeFile(Folder, e);
            if (File.Exists(file)) return File.ReadAllText(file);
            if (Directory.Exists(Folder) && Directory.EnumerateFiles(Folder, e.Id + ".cs", SearchOption.AllDirectories).FirstOrDefault() is { } found)
                return File.ReadAllText(found);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return "";
    }

    internal static string CodeFile(string folder, Entry e)
    {
        var topic = new string((e.Topic.Length > 0 ? e.Topic : "General").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
        return Path.Combine(folder, topic, e.Id + ".cs");
    }

    /// <summary>Writes the code files of changed entries, then the index (no code in it). Under <see cref="Gate"/>.</summary>
    internal static void Save(string folder, List<Entry> entries)
    {
        Directory.CreateDirectory(folder);
        foreach (var e in entries.Where(e => e.Dirty && e.Code.Length > 0))
        {
            var file = CodeFile(folder, e);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, e.Code, new UTF8Encoding(false));
            e.Dirty = false;
        }
        var index = entries.Select(e => new Entry
        {
            Id = e.Id, Description = e.Description, Tool = e.Tool, OwnTransactions = e.OwnTransactions, Model = e.Model, First = e.First, Last = e.Last,
            Uses = e.Uses, Topic = e.Topic, Sig = e.Sig, Size = e.Size,
        }).OrderBy(e => e.Topic, StringComparer.Ordinal).ThenByDescending(e => e.Uses).ToList();
        var path = Path.Combine(folder, "index.json");
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(index), new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(temp, path, null);
        else File.Move(temp, path);
    }

    /// <summary>The index of a library folder (entries without their code). Empty when there is none.</summary>
    internal static List<Entry> ReadIndex(string folder)
    {
        var path = Path.Combine(folder, "index.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path)) ?? [] : [];
    }

    /// <summary><see cref="Keep"/> against the library on this PC. Never throws: keeping must not break a call.</summary>
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
                Save(Folder, entries);
                _cached = entries;
                _cachedAt = File.GetLastWriteTimeUtc(IndexFile);
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
                if (Keep(entries, Text("code"), Text("description"), step.Tool, own, model, step.Time.ToUniversalTime()) is not null) taken++;
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

    private static string RebuiltMark => Path.Combine(Folder, "rebuilt from history.txt");

    /// <summary>
    /// Once per PC, off Revit's thread: the library is built again from all history: every model's redo log and every
    /// chat and job transcript (<see cref="FromRedo"/>, <see cref="FromTranscript"/>), oldest first; then what was kept
    /// before and isn't in that history (the desktop app's calls) is added as it was. Nothing is dropped any more (the
    /// first import, 0.35.88, stopped at 3,000). Never throws; tried again next start if it didn't finish.
    /// </summary>
    public static void BackfillOnce() => Task.Run(() =>
    {
        try
        {
            if (File.Exists(RebuiltMark)) return;
            var steps = RedoLog.Models().SelectMany(m => RedoLog.Read(m.Model).Select(s => (m.Model, s))).ToList();
            // Claude Code's transcripts, and the archived copies of those it has since deleted (Chat.HistoryArchive).
            foreach (var lines in Chat.HistoryArchive.AllTranscripts())
            {
                try { steps.AddRange(FromTranscript(lines())); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { /* a file in use is skipped */ }
            }
            var fresh = new List<Entry>();
            var taken = FromRedo(fresh, steps);
            lock (Gate)
            {
                var before = Read();
                var have = new HashSet<string>(fresh.Select(e => e.Id), StringComparer.Ordinal);
                var added = 0;
                foreach (var old in before.Where(e => !have.Contains(e.Id)))
                {
                    old.Code = CodeOf(old);
                    if (old.Code.Length == 0) continue;
                    old.Dirty = true;
                    fresh.Add(old);
                    added++;
                }
                Save(Folder, fresh);
                _cached = fresh;
                _cachedAt = File.GetLastWriteTimeUtc(IndexFile);
                foreach (var e in fresh) e.Code = "";
                File.WriteAllText(RebuiltMark, $"{DateTime.UtcNow:s} took in {taken} runs that worked from the redo logs and chat transcripts, kept {added} more from before; {fresh.Count} entries, no limit");
            }
        }
        catch (Exception) { /* tried again next start */ }
    });

    /// <summary>The kept entries (their code is read with <see cref="CodeOf"/>): a copy of the list, safe to search.</summary>
    public static List<Entry> Load()
    {
        lock (Gate) return Read().ToList();
    }

    private static List<Entry>? _cached;
    private static DateTime _cachedAt;

    /// <summary>The pointer for a failed call, from the file; "" on any trouble.</summary>
    public static string PointerFor(string description, string failedCode)
    {
        try { return Pointer(Load(), description, failedCode); }
        catch (Exception) { return ""; }
    }

    /// <summary>The kept-code lines for a job's task, from the file; "" on any trouble.</summary>
    public static string JobHintFor(string task)
    {
        try { return JobHint(Load(), task); }
        catch (Exception) { return ""; }
    }

    /// <summary>The line for a typed message, from the file; "" on any trouble.</summary>
    public static string HintFor(string message)
    {
        try { return Hint(Load(), message); }
        catch (Exception) { return ""; }
    }

    /// <summary>
    /// The index, read again only when its file changed. The one-file library of 0.35.70 to 0.35.88 (worked-code.json,
    /// code inside) is moved over once: its code goes into files, and the old file is renamed.
    /// </summary>
    private static List<Entry> Read()
    {
        try
        {
            if (!File.Exists(IndexFile) && File.Exists(StoreFile))
            {
                var old = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(StoreFile)) ?? [];
                foreach (var e in old)
                {
                    e.Dirty = true;
                    if (e.Topic.Length == 0) e.Topic = Topic(e.Code, e.Description);
                    var shingles = Shingles(CodeRepeats.Shape(e.Code));
                    (e.Sig, e.Size) = (Signature(shingles), shingles.Count);
                }
                Save(Folder, old);
                File.Move(StoreFile, StoreFile + ".moved");
            }
            if (!File.Exists(IndexFile)) return _cached = [];
            var at = File.GetLastWriteTimeUtc(IndexFile);
            if (_cached is not null && at == _cachedAt) return _cached;
            _cachedAt = at;
            return _cached = ReadIndex(Folder);
        }
        catch (Exception) { return _cached ?? []; }
    }
}
