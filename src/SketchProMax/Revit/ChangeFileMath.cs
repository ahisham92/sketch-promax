using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SketchProMax.Revit;

/// <summary>
/// The change file's format and rules, without Revit (the tests compile it); Revit/ChangeFile.cs writes it and
/// Revit/ChangeReplay.cs plays it onto a model.
///
/// Each model has a text file in Documents\Sketch ProMax\Change files\&lt;model&gt;: what changed since it was last saved,
/// one line per element, written within seconds of each change (so a crash loses almost nothing). A line holds the
/// element's whole state as it is now (its category, family and type, where it is, its level and host, and every
/// value you can type in its properties), so playing the file onto an older copy of the model makes each element as it
/// was: moved and changed elements are set again, new ones are made again where Revit allows, deleted ones are deleted.
/// Elements are found by their UniqueId, which is the same in every copy of a model (local, central, backups).
///
/// Saving (or synchronizing) the model clears the file; the one just cleared is kept as "last save", in case the save
/// itself is what broke the model. A file left over when the model opens again (Revit closed by itself, or the model
/// was closed without saving) is kept as "not saved" with its time, the newest few of them.
/// </summary>
internal static class ChangeFileMath
{
    public const string CurrentName = "changes since last save.txt", LastSaveName = "changes before last save.txt";
    public const string LeftoverPrefix = "not saved ";
    public const int KeepLeftovers = 5;

    /// <summary>Elements read per idle moment (the rest wait for the next), so Revit never stalls on a big change.</summary>
    public const int ReadPerIdle = 200;

    /// <summary>Values kept per element.</summary>
    public const int MaxValues = 200;

    /// <summary>Elements played per Revit call when replaying (each call is one undo step).</summary>
    public const int ReplayChunk = 100;

    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Sketch ProMax", "Change files");

    /// <summary>The model's folder: its file name and a short code of its full path, so models of the same name keep apart.</summary>
    public static string FolderName(string modelPath)
    {
        using var sha = SHA1.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(modelPath.Trim().ToLowerInvariant()));
        return $"{SafeName(FileStem(modelPath))} ({hash[0]:x2}{hash[1]:x2}{hash[2]:x2})";
    }

    public static string FileStem(string modelPath)
    {
        var trimmed = modelPath.Trim().TrimEnd('/', '\\');
        var cut = trimmed.LastIndexOfAny(['/', '\\']);
        var name = cut >= 0 ? trimmed.Substring(cut + 1) : trimmed;
        return name.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 4) : name;
    }

    public static string SafeName(string name)
    {
        var bad = new HashSet<char>(Path.GetInvalidFileNameChars()) { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };
        var clean = new string(name.Select(c => bad.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (clean.Length > 60) clean = clean.Substring(0, 60).Trim();
        return clean.Length == 0 ? "Model" : clean;
    }

    private const string Stamp = "yyyy-MM-dd HHmmss";

    public static string LeftoverName(DateTime at) => LeftoverPrefix + at.ToString(Stamp, CultureInfo.InvariantCulture) + ".txt";

    public static DateTime? LeftoverTime(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (!stem.StartsWith(LeftoverPrefix, StringComparison.Ordinal)) return null;
        return DateTime.TryParseExact(stem.Substring(LeftoverPrefix.Length), Stamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : null;
    }

    /// <summary>Leftover files to delete so the newest <see cref="KeepLeftovers"/> stay (other files are never touched).</summary>
    public static List<string> LeftoversToDelete(IEnumerable<string> files) =>
        files.Where(f => LeftoverTime(f) is not null).OrderByDescending(f => LeftoverTime(f)).Skip(KeepLeftovers).ToList();

    // ---- Lines ----------------------------------------------------------------------------------------------------------

    public static class Kinds
    {
        public const string Model = "model", State = "state", Deleted = "deleted";
    }

    /// <summary>A point in Revit's internal units (feet), as stored.</summary>
    public sealed record Xyz(
        [property: JsonPropertyName("x")] double X,
        [property: JsonPropertyName("y")] double Y,
        [property: JsonPropertyName("z")] double Z);

    /// <summary>
    /// One edge of a sketch or path: a straight line from P to Q, or an arc through M. Roof edges also say whether they
    /// set the slope and at what angle. Curves of other kinds (splines, ellipses) are written as short straight lines
    /// (the line's Approx says so).
    /// </summary>
    public sealed record Seg(
        [property: JsonPropertyName("p")] Xyz P,
        [property: JsonPropertyName("q")] Xyz Q,
        [property: JsonPropertyName("m"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Xyz? M = null,
        [property: JsonPropertyName("s"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Slope = null);

    /// <summary>
    /// One value from the element's properties. Found again by its built-in id (Bip), else its shared parameter's GUID,
    /// else its name. Kind: d (number, internal units), i (whole number), s (text), e (another element, by UniqueId in
    /// Ref, or Ref empty for none).
    /// </summary>
    public sealed class Value
    {
        [JsonPropertyName("n"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Name { get; set; }
        [JsonPropertyName("b"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Bip { get; set; }
        [JsonPropertyName("g"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Guid { get; set; }
        [JsonPropertyName("k")] public string Kind { get; set; } = "";
        [JsonPropertyName("d"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? Number { get; set; }
        [JsonPropertyName("i"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Whole { get; set; }
        [JsonPropertyName("s"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Text { get; set; }
        [JsonPropertyName("r"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Ref { get; set; }
    }

    /// <summary>One line of the file.</summary>
    public sealed class Line
    {
        [JsonPropertyName("k")] public string Kind { get; set; } = "";
        [JsonPropertyName("t")] public DateTime At { get; set; }
        /// <summary>Model: the model's path and central model, written once at the top.</summary>
        [JsonPropertyName("path"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Path { get; set; }
        [JsonPropertyName("central"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Central { get; set; }
        /// <summary>State and Deleted: the element.</summary>
        [JsonPropertyName("u"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Unique { get; set; }
        [JsonPropertyName("id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public long Id { get; set; }
        [JsonPropertyName("c"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Category { get; set; }
        [JsonPropertyName("bic"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public long CategoryId { get; set; }
        [JsonPropertyName("name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Name { get; set; }
        /// <summary>What it is: instance (a family instance), wall, grid, level, type, other.</summary>
        [JsonPropertyName("what"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? What { get; set; }
        /// <summary>Its type as "Family: Type" (and the type's UniqueId), level and host.</summary>
        [JsonPropertyName("type"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Type { get; set; }
        [JsonPropertyName("tu"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? TypeUnique { get; set; }
        [JsonPropertyName("lvl"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Level { get; set; }
        [JsonPropertyName("lu"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? LevelUnique { get; set; }
        [JsonPropertyName("host"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Host { get; set; }
        [JsonPropertyName("st"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Structural { get; set; }
        /// <summary>Where it is: a point (and its rotation in radians), or a straight line from P to Q.</summary>
        [JsonPropertyName("p"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Xyz? P { get; set; }
        [JsonPropertyName("q"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Xyz? Q { get; set; }
        [JsonPropertyName("rot"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? Rotation { get; set; }
        /// <summary>Facing and hand flipped (doors and windows).</summary>
        [JsonPropertyName("ff"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool FacingFlipped { get; set; }
        [JsonPropertyName("hf"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool HandFlipped { get; set; }
        /// <summary>A location curve's arc: a point on it between P and Q.</summary>
        [JsonPropertyName("m"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Xyz? M { get; set; }
        /// <summary>
        /// The sketch (floors, ceilings, roofs, openings, filled regions, toposolids, walls with an edited profile) as
        /// loops of edges, a railing's path as one loop, and points of a shape-edited floor, roof or toposolid.
        /// </summary>
        [JsonPropertyName("loops"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<List<Seg>>? Loops { get; set; }
        [JsonPropertyName("pts"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<Xyz>? Points { get; set; }
        [JsonPropertyName("approx"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Approx { get; set; }
        /// <summary>The sketch also had a slope arrow (not in the loops: redone by hand when the element is made again).</summary>
        [JsonPropertyName("arrow"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool SlopeArrow { get; set; }
        /// <summary>Filled regions and detail lines: the view they are drawn in; model and detail lines: their line style, and a model line's plane.</summary>
        [JsonPropertyName("view"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? View { get; set; }
        [JsonPropertyName("ls"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? LineStyle { get; set; }
        [JsonPropertyName("po"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Xyz? PlaneOrigin { get; set; }
        [JsonPropertyName("pn"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Xyz? PlaneNormal { get; set; }
        /// <summary>Openings: the top level of a shaft (by UniqueId).</summary>
        [JsonPropertyName("top"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? TopLevel { get; set; }
        /// <summary>Level: its elevation (internal units).</summary>
        [JsonPropertyName("elev"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? Elevation { get; set; }
        [JsonPropertyName("v"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<Value>? Values { get; set; }
        /// <summary>The transaction it came from (Move, Wall, Paste...), for the list.</summary>
        [JsonPropertyName("tx"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Tx { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new();

    public const string HeaderComment = "# Sketch ProMax change file: what changed in this model since it was last saved, one element per line. " +
                                        "It is cleared each time the model is saved. To put these changes into a model, open the model and use Recover > Replay a change file.";

    public static string Write(Line line) => JsonSerializer.Serialize(line, Json);

    /// <summary>A line read back; null for a comment or a broken line (the one being written when Revit closed, for instance).</summary>
    public static Line? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.TrimStart().StartsWith("#", StringComparison.Ordinal)) return null;
        try { return JsonSerializer.Deserialize<Line>(text, Json) is { Kind.Length: > 0 } l ? l : null; }
        catch (JsonException) { return null; }
    }

    /// <summary>A read file: the model it is for, and each element's latest line in the order elements first changed.</summary>
    public sealed record Parsed(string? Model, string? Central, DateTime? First, DateTime? Last, List<Line> Elements);

    public static Parsed Read(IEnumerable<string> lines)
    {
        string? model = null, central = null;
        DateTime? first = null, last = null;
        var order = new List<string>();
        var latest = new Dictionary<string, Line>(StringComparer.Ordinal);
        var added = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in lines)
        {
            if (Parse(text) is not { } line) continue;
            if (line.Kind == Kinds.Model) { model ??= line.Path; central ??= line.Central; continue; }
            var key = line.Unique ?? (line.Id != 0 ? "#" + line.Id.ToString(CultureInfo.InvariantCulture) : null);
            if (key is null) continue;
            first ??= line.At;
            last = line.At;
            if (!latest.TryGetValue(key, out var before)) order.Add(key);
            // A later line names only the values that changed since the element's last line: keep the earlier ones.
            else if (line.Kind == Kinds.State && before.Values is { Count: > 0 } old)
            {
                var merged = old.ToDictionary(KeyOf, StringComparer.Ordinal);
                foreach (var v in line.Values ?? []) merged[KeyOf(v)] = v;
                line.Values = merged.Values.ToList();
            }
            latest[key] = line;
        }
        return new Parsed(model, central, first, last, order.Select(k => latest[k]).ToList());
    }

    /// <summary>A value's key: its built-in id, else its shared GUID, else its name.</summary>
    public static string KeyOf(Value v) => v.Bip != 0 ? "b" + v.Bip.ToString(CultureInfo.InvariantCulture) : v.Guid is { Length: > 0 } g ? "g" + g : "n" + v.Name;

    /// <summary>A value as text, to tell whether it changed since the element's last line.</summary>
    public static string Print(Value v) => v.Kind + "|" + v.Number?.ToString("R", CultureInfo.InvariantCulture) + "|" + v.Whole + "|" + v.Text + "|" + v.Ref;

    /// <summary>
    /// The values to write for an element: all of them on its first line (empty text and empty references left out, they
    /// are the usual state), then only those that changed since (<paramref name="written"/> keeps what was written).
    /// </summary>
    public static List<Value> Changed(List<Value> now, Dictionary<string, string>? written)
    {
        if (written is null) return now.Where(v => !(v.Kind == "s" && string.IsNullOrEmpty(v.Text)) && !(v.Kind == "e" && string.IsNullOrEmpty(v.Ref))).ToList();
        return now.Where(v => !written.TryGetValue(KeyOf(v), out var was) || was != Print(v)).ToList();
    }

    /// <summary>A number rounded so the file stays small: a ten-millionth of a foot (or radian) is far below anything Revit shows.</summary>
    public static double Round(double d) => Math.Round(d, 7);

    /// <summary>The counts the Replay preview shows: elements to set again, to delete, and all.</summary>
    public static (int Changed, int Deleted) Count(Parsed file) =>
        (file.Elements.Count(e => e.Kind == Kinds.State), file.Elements.Count(e => e.Kind == Kinds.Deleted));

    /// <summary>
    /// The order elements are played in: levels and grids first (others sit on them), then types, then everything else in
    /// the order it changed (a host before what it hosts, when the host came in the same file), deletions last.
    /// </summary>
    public static List<Line> PlayOrder(Parsed file)
    {
        int Rank(Line l) => l.Kind == Kinds.Deleted ? 9 : l.What switch { "level" => 0, "grid" => 1, "type" => 2, "wall" => 3, _ => 4 };
        var ranked = file.Elements.Select((l, i) => (Line: l, Index: i)).OrderBy(x => Rank(x.Line)).ThenBy(x => x.Index).Select(x => x.Line).ToList();
        // Hosted instances after their host, when the host is in the file.
        var position = ranked.Select((l, i) => (l.Unique, i)).Where(x => x.Unique is not null).GroupBy(x => x.Unique!).ToDictionary(g => g.Key, g => g.First().i);
        var result = new List<Line>();
        var placed = new HashSet<Line>();
        void Place(Line l, int depth)
        {
            if (placed.Contains(l)) return;
            if (depth < 5 && l.Host is { } host && position.TryGetValue(host, out var at) && ranked[at] != l) Place(ranked[at], depth + 1);
            if (placed.Add(l)) result.Add(l);
        }
        foreach (var l in ranked) Place(l, 0);
        return result;
    }

    /// <summary>A line as the Replay window lists it: "17:42 Structural Columns: C 600x600 (moved or changed)".</summary>
    public static string Describe(Line l)
    {
        var what = l.Kind == Kinds.Deleted ? "deleted" : "added or changed";
        var name = l.Type ?? l.Name;
        var label = l.Category is { Length: > 0 } c ? (name is { Length: > 0 } ? $"{c}: {name}" : c) : (name ?? $"element {l.Id}");
        return $"{l.At.ToString("HH:mm", CultureInfo.CurrentCulture)} {label} ({what}{(l.Tx is { Length: > 0 } tx ? ", " + tx : "")})";
    }

    /// <summary>Two numbers are the same value (lengths in feet, angles in radians): a hundredth of a millimetre.</summary>
    public static bool Same(double a, double b) => Math.Abs(a - b) < 3.3e-5;

    /// <summary>
    /// Two sketches are the same shape: the same number of loops, and every edge of one in the other (either direction,
    /// in any order), so a sketch is only edited when it really changed.
    /// </summary>
    public static bool SameLoops(List<List<Seg>>? a, List<List<Seg>>? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a.Count != b.Count || a.Sum(l => l.Count) != b.Sum(l => l.Count)) return false;
        var left = b.SelectMany(l => l).ToList();
        foreach (var seg in a.SelectMany(l => l))
        {
            var match = left.FindIndex(o => SameSeg(seg, o));
            if (match < 0) return false;
            left.RemoveAt(match);
        }
        return true;
    }

    // An arc's M is its middle point (half way along it), the same whichever way round it was drawn.
    private static bool SameSeg(Seg a, Seg b) =>
        ((Same(a.P, b.P) && Same(a.Q, b.Q)) || (Same(a.P, b.Q) && Same(a.Q, b.P))) && Same(a.M, b.M)
        && (a.Slope is null ? b.Slope is null : b.Slope is { } s && Same(a.Slope.Value, s));

    /// <summary>Two point sets of a shape-edited slab are the same (in any order).</summary>
    public static bool SamePoints(List<Xyz>? a, List<Xyz>? b)
    {
        if (a is null || b is null) return (a?.Count ?? 0) == (b?.Count ?? 0);
        return a.Count == b.Count && a.All(p => b.Any(q => Same(p, q)));
    }

    public static bool Same(Xyz? a, Xyz? b) => a is null || b is null ? a is null && b is null : Same(a.X, b.X) && Same(a.Y, b.Y) && Same(a.Z, b.Z);
}
