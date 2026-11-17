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
/// One entry per shape (CodeRepeats.Shape), the newest code kept. In %APPDATA%\SketchProMax\worked-code.json; no Revit
/// here (the tests compile it).
/// </summary>
internal static class WorkedCode
{
    /// <summary>The most entries kept (the oldest unused go first), the shortest code worth keeping, the longest kept.</summary>
    public const int MaxEntries = 400, MinLength = 150, MaxCode = 20000;

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
        /// <summary>Whose PC it came from, for code kept by a colleague (the team folder); "" for this PC's own.</summary>
        public string By { get; set; } = "";
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
        var entry = entries.FirstOrDefault(e => e.Id == id);
        if (entry is null)
        {
            if (entries.Count >= MaxEntries) entries.Remove(entries.OrderBy(e => e.Uses > 1).ThenBy(e => e.Last).First());
            entries.Add(entry = new Entry { Id = id, First = now });
        }
        entry.Description = description;
        entry.Code = code;
        entry.Tool = tool;
        entry.OwnTransactions = ownTransactions;
        if (model.Length > 0) entry.Model = model;
        entry.Last = now;
        entry.Uses++;
        return entry;
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
        (e.Model.Length > 0 ? $", in {e.Model}" : "") + (e.By.Length > 0 ? $", kept by {e.By}" : "") + ")";

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

    // ---- The team's code --------------------------------------------------------------------------------------------

    /// <summary>
    /// Where colleagues' kept code comes from (Sharing.TeamShare sets it; the tests leave it unset): what one person's
    /// chat coded, the next person's chat finds instead of coding it again.
    /// </summary>
    public static Func<List<Entry>>? Team;

    /// <summary>
    /// What to do with a one-click tool from the team folder: "install" when this PC doesn't have it and never had it
    /// from the team, "update" when the copy here is still the one the team gave, "keep" otherwise (this PC's own tool
    /// of that name, one changed here, or one deleted here on purpose).
    /// </summary>
    public static string Decide(string? localHash, string? installedHash, string teamHash) =>
        localHash is null ? (installedHash is null ? "install" : "keep")
        : localHash == teamHash ? "keep"
        : installedHash is not null && localHash == installedHash ? "update" : "keep";

    /// <summary>This PC's code with the team's: its own first, a colleague's entry only for a shape this PC doesn't have.</summary>
    public static List<Entry> Merge(List<Entry> mine, IEnumerable<Entry> team)
    {
        var all = new List<Entry>(mine);
        var have = new HashSet<string>(mine.Select(e => e.Id), StringComparer.Ordinal);
        foreach (var e in team.OrderByDescending(e => e.Uses).ThenByDescending(e => e.Last))
            if (e.Id.Length > 0 && e.Code.Length is > 0 and <= MaxCode && e.Description.Length > 0 && have.Add(e.Id)) all.Add(e);
        return all;
    }

    /// <summary>
    /// The colleagues' code read from their files in the team folder (<paramref name="files"/>: PC name and JSON), with
    /// this PC's own file left out; a broken file is skipped. Each entry is marked with whose PC it came from.
    /// </summary>
    public static List<Entry> FromTeam(IEnumerable<(string Pc, string Json)> files, string thisPc)
    {
        var all = new List<Entry>();
        foreach (var (pc, json) in files)
        {
            if (string.Equals(pc, thisPc, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                foreach (var e in JsonSerializer.Deserialize<List<Entry>>(json) ?? [])
                {
                    if (e is null || e.Id is null || e.Code is null || e.Description is null) continue;
                    e.By = pc;
                    e.Tool = e.Tool == "run_revit_query_code" ? e.Tool : "run_revit_code";
                    e.Model ??= "";
                    all.Add(e);
                }
            }
            catch (JsonException) { /* a broken file is skipped */ }
        }
        return all;
    }

    // ---- The file on this PC -------------------------------------------------------------------------------------------

    /// <summary><see cref="Keep"/> against the file on this PC. Never throws: keeping must not break a call.</summary>
    public static void Note(string code, string description, string tool, bool ownTransactions, string model)
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

    /// <summary>The kept code: this PC's, and (unless <paramref name="own"/>) the colleagues' from the team folder.</summary>
    public static List<Entry> Load(bool own = false)
    {
        List<Entry> mine;
        lock (Gate) mine = Read();
        if (own || Team is null) return mine;
        try { return Merge(mine, Team()); }
        catch (Exception) { return mine; }
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
