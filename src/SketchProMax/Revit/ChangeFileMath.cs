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

    /// <summary>The format from which elements made in the session are marked New.</summary>
    public const int FormatNew = 2;

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

    public const string ReplayedPrefix = "replayed ";

    public static string ReplayedName(DateTime at) => ReplayedPrefix + at.ToString(Stamp, CultureInfo.InvariantCulture) + ".txt";

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
        /// <summary>The views open in the model (UniqueIds in Open, the active one in View), written when they change.</summary>
        public const string Views = "views";
        /// <summary>Written as the file is cleared: the Revit event that cleared it (in Text).</summary>
        public const string Cleared = "cleared";
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

    /// <summary>
    /// Reinforcement details. Rebar: style (0 standard, 1 stirrup/tie), hooks at each end (type UniqueId and orientation,
    /// 1 left, -1 right) and layout (Rule: Single, FixedNumber, MaximumSpacing, NumberWithSpacing, MinimumClearSpacing;
    /// N bars, Spacing, Length of the set, bars on the normal's side, first and last bar shown). Area reinforcement: its
    /// major direction (Dir). Area and path reinforcement: bar type (BarType); path: flipped.
    /// </summary>
    public sealed class Bar
    {
        [JsonPropertyName("sty"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Style { get; set; }
        [JsonPropertyName("h0"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? HookStart { get; set; }
        [JsonPropertyName("h1"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? HookEnd { get; set; }
        [JsonPropertyName("o0"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int OrientStart { get; set; }
        [JsonPropertyName("o1"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int OrientEnd { get; set; }
        [JsonPropertyName("rule"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Rule { get; set; }
        [JsonPropertyName("n"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int N { get; set; }
        [JsonPropertyName("s"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public double Spacing { get; set; }
        [JsonPropertyName("len"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public double Length { get; set; }
        [JsonPropertyName("side"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool NormalSide { get; set; }
        [JsonPropertyName("nf"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool NoFirst { get; set; }
        [JsonPropertyName("nl"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool NoLast { get; set; }
        [JsonPropertyName("free"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool FreeForm { get; set; }
        [JsonPropertyName("dir"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Xyz? Dir { get; set; }
        [JsonPropertyName("bt"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? BarType { get; set; }
        [JsonPropertyName("flip"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Flip { get; set; }
    }

    /// <summary>
    /// A box in a view's or link's own axes: Origin and the three axes (BasisX, BasisY, BasisZ), then Min and Max in those
    /// axes (a section's or plan's crop, a 3D view's section box). A 3D view's eye, up and forward go in Origin, BasisY
    /// and BasisZ with Eye set.
    /// </summary>
    public sealed class Box
    {
        [JsonPropertyName("o")] public Xyz Origin { get; set; } = new(0, 0, 0);
        [JsonPropertyName("bx"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Xyz? BasisX { get; set; }
        [JsonPropertyName("by"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Xyz? BasisY { get; set; }
        [JsonPropertyName("bz"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Xyz? BasisZ { get; set; }
        [JsonPropertyName("min"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Xyz? Min { get; set; }
        [JsonPropertyName("max"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Xyz? Max { get; set; }
        [JsonPropertyName("eye"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Eye { get; set; }
        /// <summary>A 3D view's section box (Min, Max in model axes) next to its eye.</summary>
        [JsonPropertyName("sb"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Box? SectionBox { get; set; }
    }

    /// <summary>A text note's leader: where it points (End), its elbow, arc or straight, and the side of the text it leaves.</summary>
    public sealed class Lead
    {
        [JsonPropertyName("e")] public Xyz End { get; set; } = new(0, 0, 0);
        [JsonPropertyName("k"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Xyz? Elbow { get; set; }
        [JsonPropertyName("arc"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Arc { get; set; }
        [JsonPropertyName("r"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Right { get; set; }
    }

    /// <summary>A layer of a wall, floor, roof or ceiling type: width, function (Revit's number) and material (UniqueId).</summary>
    public sealed class Layer
    {
        [JsonPropertyName("w")] public double Width { get; set; }
        [JsonPropertyName("f")] public int Function { get; set; }
        [JsonPropertyName("m"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Material { get; set; }
    }

    /// <summary>A straight stairs run: its path (P to Q), justification (Revit's number), width and base elevation.</summary>
    public sealed class Run
    {
        [JsonPropertyName("p")] public Xyz P { get; set; } = new(0, 0, 0);
        [JsonPropertyName("q")] public Xyz Q { get; set; } = new(0, 0, 0);
        [JsonPropertyName("j"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Justify { get; set; }
        [JsonPropertyName("w")] public double Width { get; set; }
        [JsonPropertyName("b"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public double Base { get; set; }
    }

    public static bool SameLayers(List<Layer>? a, List<Layer>? b) =>
        (a ?? []).Count == (b ?? []).Count && (a ?? []).Zip(b ?? [], (x, y) => Same(x.Width, y.Width) && x.Function == y.Function && x.Material == y.Material).All(same => same);

    /// <summary>The move that takes runs <paramref name="now"/> to <paramref name="want"/> when they only moved; null when their shape changed.</summary>
    public static Xyz? RunsShift(List<Run> now, List<Run> want)
    {
        if (now.Count != want.Count || now.Count == 0) return null;
        var d = new Xyz(want[0].P.X - now[0].P.X, want[0].P.Y - now[0].P.Y, want[0].P.Z - now[0].P.Z);
        for (var i = 0; i < now.Count; i++)
        {
            Xyz Moved(Xyz p) => new(p.X + d.X, p.Y + d.Y, p.Z + d.Z);
            if (!Same(Moved(now[i].P), want[i].P) || !Same(Moved(now[i].Q), want[i].Q) || !Same(now[i].Width, want[i].Width)) return null;
        }
        return d;
    }

    public static bool SameLeads(List<Lead>? a, List<Lead>? b) =>
        (a ?? []).Count == (b ?? []).Count && (a ?? []).Zip(b ?? [], (x, y) => Same(x.End, y.End) && Same(x.Elbow, y.Elbow) && x.Arc == y.Arc).All(same => same);

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
        /// <summary>
        /// Annotations: what a tag, dimension or spot points at, as Revit's stable references (they name the element by
        /// its UniqueId, so one made again is found by swapping in its new UniqueId).
        /// </summary>
        [JsonPropertyName("refs"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? Refs { get; set; }
        /// <summary>A text note's text and width; a tag's or spot's leader and a tag's orientation (0 horizontal, 1 vertical, 2 model).</summary>
        [JsonPropertyName("txt"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Text { get; set; }
        [JsonPropertyName("w"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? Width { get; set; }
        [JsonPropertyName("ldr"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Leader { get; set; }
        /// <summary>Types: their class (WallType, FamilySymbol...) and the layers of a wall, floor, roof or ceiling type, with how
        /// many are exterior and interior shell layers.</summary>
        [JsonPropertyName("cls"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Class { get; set; }
        [JsonPropertyName("layers"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<Layer>? Layers { get; set; }
        [JsonPropertyName("shells"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<int>? Shells { get; set; }
        /// <summary>Stairs: their straight runs.</summary>
        [JsonPropertyName("runs"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<Run>? Runs { get; set; }
        /// <summary>A text note's leaders.</summary>
        [JsonPropertyName("lds"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<Lead>? Leads { get; set; }
        [JsonPropertyName("or"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Orientation { get; set; }
        /// <summary>A revision cloud's revision (by UniqueId).</summary>
        [JsonPropertyName("rev"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Revision { get; set; }
        /// <summary>Reinforcement: bar layout, hooks and bar type (the bar's or system's shape is in Loops).</summary>
        [JsonPropertyName("bar"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Bar? Bar { get; set; }
        /// <summary>Made in this session, after the last save: only such an element is made again when it is missing (one that
        /// was there before and is gone now was deleted, by someone else in a shared model, and is left so).</summary>
        [JsonPropertyName("new"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool New { get; set; }
        /// <summary>Model line: the file's format (2: elements made in the session are marked New).</summary>
        [JsonPropertyName("fmt"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Format { get; set; }
        /// <summary>A group member: its group (by UniqueId); a group made again brings its members with it.</summary>
        [JsonPropertyName("grp"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Group { get; set; }
        /// <summary>Views: the kind of view (FloorPlan, Section, ThreeD, DraftingView, Schedule...), its box and a schedule's
        /// category and fields; links: their placement (Box.Origin and BasisX).</summary>
        [JsonPropertyName("vt"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ViewKind { get; set; }
        [JsonPropertyName("box"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Box? Box { get; set; }
        [JsonPropertyName("sc"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public long ScheduleCategory { get; set; }
        [JsonPropertyName("fields"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<long>? Fields { get; set; }
        /// <summary>Views line: the views open (UniqueIds); the active one is in View.</summary>
        [JsonPropertyName("open"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? Open { get; set; }
        /// <summary>Level: its elevation (internal units).</summary>
        [JsonPropertyName("elev"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? Elevation { get; set; }
        [JsonPropertyName("v"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<Value>? Values { get; set; }
        /// <summary>The transaction it came from (Move, Wall, Paste...), for the list.</summary>
        [JsonPropertyName("tx"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Tx { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new();

    public const string HeaderComment = "# Sketch ProMax change file: what changed in this model since it was last saved, one element per line. " +
                                        "It is cleared each time the model is saved. To put these changes into a model, open the model and use Lifeline > Replay a file from disk.";

    public static string Write(Line line) => JsonSerializer.Serialize(line, Json);

    /// <summary>A line read back; null for a comment or a broken line (the one being written when Revit closed, for instance).</summary>
    public static Line? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.TrimStart().StartsWith("#", StringComparison.Ordinal)) return null;
        try { return JsonSerializer.Deserialize<Line>(text, Json) is { Kind.Length: > 0 } l ? l : null; }
        catch (JsonException) { return null; }
    }

    /// <summary>A read file: the model it is for, and each element's latest line in the order elements first changed.</summary>
    public sealed record Parsed(string? Model, string? Central, DateTime? First, DateTime? Last, List<Line> Elements)
    {
        /// <summary>The views that were open (UniqueIds, newest views line) and the active one.</summary>
        public List<string> Open { get; init; } = [];
        public string? Active { get; init; }
        /// <summary>The file marks what was made in the session (New), so a missing element without the mark was deleted by someone else.</summary>
        public bool MarksNew { get; init; }
        /// <summary>A file cleared at a save: the Revit event that cleared it, and when.</summary>
        public string? ClearedBy { get; init; }
        public DateTime? ClearedAt { get; init; }

        /// <summary>Anything to bring back: changed elements, or views that were open.</summary>
        public bool Any => Elements.Count > 0 || Open.Count > 0;
    }

    public static Parsed Read(IEnumerable<string> lines)
    {
        string? model = null, central = null;
        DateTime? first = null, last = null;
        var order = new List<string>();
        var latest = new Dictionary<string, Line>(StringComparer.Ordinal);
        List<string> open = [];
        string? active = null;
        var marks = false;
        Line? cleared = null;
        foreach (var text in lines)
        {
            if (Parse(text) is not { } line) continue;
            if (line.Kind == Kinds.Model) { model ??= line.Path; central ??= line.Central; marks |= line.Format >= FormatNew; continue; }
            if (line.Kind == Kinds.Views) { open = line.Open ?? []; active = line.View; continue; }
            if (line.Kind == Kinds.Cleared) { cleared = line; continue; }
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
            if (before is not null) line.New |= before.New;
            latest[key] = line;
        }
        return new Parsed(model, central, first, last, order.Select(k => latest[k]).ToList()) { Open = open, Active = active, MarksNew = marks, ClearedBy = cleared?.Text, ClearedAt = cleared?.At };
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
        int Rank(Line l) => l.Kind == Kinds.Deleted ? 9 : l.What switch { "level" => 0, "grid" => 1, "type" => 2, "wall" or "view" or "sheet" or "group" or "link" or "stairs" => 3, _ when IsNote(l) => 5, _ => 4 };
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

    // ---- Lifeline's bug list (Revit/LifelineBugs) ----------------------------------------------------------------------

    public const string BugsFolderName = "Lifeline bugs", BugsFileName = "Lifeline bugs.txt";
    public const int MaxBugLines = 2000, MaxBugsPerSession = 300;

    public const string BugsHeader = "# Sketch ProMax Lifeline: problems on this PC, newest last. Each line: when | add-in version | Revit | model | where | what went wrong. " +
                                     "Send it to the BIM lead now and then; it is safe to delete.";

    /// <summary>One line of the bug list, on one line whatever the message holds, and not too long.</summary>
    public static string BugLine(DateTime at, string version, string revit, string? model, string where, string what)
    {
        var flat = string.Join(" ", (what ?? "").Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (flat.Length > 600) flat = flat.Substring(0, 600) + "…";
        return $"{at:yyyy-MM-dd HH:mm} | {(version is { Length: > 0 } ? version : "?")} | Revit {(revit is { Length: > 0 } ? revit : "?")} | {model ?? "-"} | {where} | {flat}";
    }

    /// <summary>What a report line says went wrong, without which element ("17:42 Walls: W1 (added or changed): why" → "why"), so one example of each reason is kept.</summary>
    public static string Reason(string reportLine)
    {
        var at = reportLine.IndexOf("): ", StringComparison.Ordinal);
        return at >= 0 ? reportLine.Substring(at + 3) : reportLine;
    }

    /// <summary>The header and the newest <paramref name="max"/> lines.</summary>
    public static List<string> TrimBugs(List<string> lines, int max)
    {
        var body = lines.Where(l => !l.StartsWith("#", StringComparison.Ordinal) && l.Length > 0).ToList();
        return [BugsHeader, .. body.Skip(Math.Max(0, body.Count - max))];
    }

    /// <summary>Tags, dimensions and spots point at other elements: they are played after everything they could point at.</summary>
    public static bool IsNote(Line l) => l.What is "tag" or "roomtag" or "dimension" or "spot" or "viewport" or "schedule-on-sheet";

    /// <summary>
    /// A stable reference with the UniqueIds of elements made again swapped for their new ones (Revit's stable reference
    /// starts with the element's UniqueId, and a linked one has it after a colon).
    /// </summary>
    public static string Remap(string reference, IReadOnlyDictionary<string, string> madeAgain)
    {
        foreach (var part in reference.Split(':'))
            if (part.Length >= 36 && madeAgain.TryGetValue(part, out var now)) reference = reference.Replace(part, now);
        return reference;
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
