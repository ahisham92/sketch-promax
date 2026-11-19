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

    /// <summary>Milliseconds of Revit's time one idle moment may spend reading (a big change is read over many moments).</summary>
    public const int ReadBudgetMs = 40;

    /// <summary>
    /// Revit raises no idle moments while a sketch, a group or a family is being edited, so what changed there used to
    /// wait unread until the edit mode was left, and was lost when Revit closed first (a real replay of 19 Nov 2026 held
    /// only deletions: two floors' new outlines were never written). When no idle moment came for <see cref="NoIdleMs"/>,
    /// the next change reads what is waiting right away, at most once every <see cref="EagerEveryMs"/> and within
    /// <see cref="ReadBudgetMs"/>, so a long job of many steps pays a few percent at most.
    /// </summary>
    public const int NoIdleMs = 2000, EagerEveryMs = 1000;

    /// <summary>True when a change should read the waiting elements itself, not wait for an idle moment.</summary>
    public static bool ReadNow(int sinceIdleMs, int sinceEagerMs) => sinceIdleMs > NoIdleMs && sinceEagerMs > EagerEveryMs;

    /// <summary>Values kept per element.</summary>
    public const int MaxValues = 200;

    /// <summary>Elements played in the first Revit call of a replay (each call is one undo step); later calls are sized by
    /// <see cref="NextChunk"/> so each takes about a third of a second and Revit stays responsive (Stop works between calls).</summary>
    public const int ReplayChunk = 5, ReplayChunkMax = 50;

    /// <summary>The next chunk's size from how long the last one took: half when it took over a second (down to one
    /// element), double when it took under 0.15 s (up to <see cref="ReplayChunkMax"/>).</summary>
    public static int NextChunk(int size, TimeSpan took) =>
        took.TotalSeconds > 1 ? Math.Max(1, size / 2) : took.TotalSeconds < 0.15 ? Math.Min(ReplayChunkMax, size * 2) : size;

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

    /// <summary>The time a replayed file was from, else null.</summary>
    public static DateTime? ReplayedTime(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (!stem.StartsWith(ReplayedPrefix, StringComparison.Ordinal)) return null;
        return DateTime.TryParseExact(stem.Substring(ReplayedPrefix.Length), Stamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : null;
    }

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
        [property: JsonPropertyName("s"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Slope = null,
        // A curve that is neither a line nor an arc, exactly (a spline, an ellipse, a whole circle): what kind and its numbers.
        [property: JsonPropertyName("c"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? C = null);

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
        /// <summary>Made in this session, after the last save. Replay makes any missing element again, marked or not: what
        /// was done is played back as it was, never guessed away as deleted by someone else.</summary>
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
        /// <summary>A sketch's slope arrow (floors, ceilings): from its tail (P) to its head (Q), and its slope (rise over run).</summary>
        [JsonPropertyName("sa"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Seg? Arrow { get; set; }
        [JsonPropertyName("sl"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? ArrowSlope { get; set; }
        /// <summary>A family loaded after the last save: its copy kept next to the change file (the file's name in <see cref="FamiliesFolder"/>).</summary>
        [JsonPropertyName("file"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? File { get; set; }
        /// <summary>What a kind keeps besides its values, by name (a material's colours, patterns and assets).</summary>
        [JsonPropertyName("x"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Dictionary<string, string>? Extra { get; set; }
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
        /// <summary>The file marks what was made in the session (New), (kept for the report; replay makes every missing element again, marked or not).</summary>
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
    /// The order elements are played in: levels, grids, materials and loaded families first (others sit on or use them), then types, then everything else in
    /// the order it changed (a host before what it hosts, when the host came in the same file), deletions last.
    /// </summary>
    public static List<Line> PlayOrder(Parsed file)
    {
        int Rank(Line l) => l.Kind == Kinds.Deleted ? 9 : l.What switch { "level" => 0, "grid" or "material" or "family" or "filter" or "linepattern" or "fillpattern" or "style" or "setup" => 1, "type" => 2, "view" when IsTemplate(l) => 2, "wall" or "view" or "sheet" or "group" or "link" or "stairs" => 3, "sheetset" => 6, _ when IsNote(l) => 5, _ => 4 };
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

    // ---- What Revit makes by itself -------------------------------------------------------------------------------------

    /// <summary>
    /// Categories Revit makes, changes and deletes by itself along with something else (the automatic dimensions and
    /// constraints of a sketch, a view's camera, section box and sun path, the preview of a loaded family's type): they
    /// never get a line, and lines of them in older files are not played. Revit's numbers for the categories.
    /// </summary>
    public static bool IsRevitsOwn(long categoryId) => categoryId is -2000261 or -2000262 or -2000500 or -2000301 or -2009609 or -2000576;

    // ---- Loaded families ------------------------------------------------------------------------------------------------

    /// <summary>The folder (in the model's change-file folder) with a copy of each family loaded after the last save.</summary>
    public const string FamiliesFolder = "families";

    /// <summary>A kept family's file name: the family's UniqueId, so two families of one name keep apart.</summary>
    public static string FamilyFileName(string unique) => SafeName(unique) + ".rfa";

    /// <summary>The kept families that no change file in the folder names any more (<paramref name="texts"/>: the folder's change files).</summary>
    public static List<string> FamiliesToDelete(IEnumerable<string> familyFiles, IEnumerable<string> texts)
    {
        var all = string.Join("\n", texts);
        return familyFiles.Where(f => all.IndexOf("\"file\":\"" + f.Substring(f.LastIndexOfAny(['/', '\\']) + 1) + "\"", StringComparison.OrdinalIgnoreCase) < 0).ToList();
    }

    // ---- Kept elements --------------------------------------------------------------------------------------------------

    /// <summary>
    /// Next to the change files: a small Revit project with a copy of each new element that can't be made again from
    /// its line (in-place families, curved stairs, extrusion roofs, pipes...), and the list of which copy is whose
    /// (the element's UniqueId, tab, its copy's UniqueId).
    /// </summary>
    public const string KeptName = "kept elements.rvt";
    public const string KeptMapName = "kept elements.txt";
    /// <summary>The drafting view of the kept-elements project that holds copies of things drawn in a view (detail groups).</summary>
    public const string KeptDetailsView = "Kept details";
    /// <summary>The kept-elements project saved under another name when Revit couldn't save it over itself; it takes the file's place once closed.</summary>
    public const string KeptSwapName = "kept elements (new).rvt";
    /// <summary>A deletion Revit doesn't let an add-in do (the view that is open, an element Revit holds).</summary>
    public const string CantDelete = "you deleted it, but Revit doesn't let an add-in delete this one: delete it by hand";

    /// <summary>A view template's line.</summary>
    public static bool IsTemplate(Line l) => l.What == "view" && l.Extra is { } x && x.ContainsKey("template");

    /// <summary>Things of the project that are kept as copies whole (a view template, a view filter, a line or fill pattern): found again by name too.</summary>
    public static bool IsDocItem(Line l) => l.What is "filter" or "linepattern" or "fillpattern" or "setup" || IsTemplate(l);

    /// <summary>The named setups kept whole as copies, as their lines' category says them (the order <see cref="ChangeKeeper.SetupKind"/> uses).</summary>
    public static readonly string[] Setups = ["Print setup", "DWG export setup", "DGN export setup", "PDF export setup", "Phase filter"];

    /// <summary>A sheet/view set's views (UniqueIds) as its line keeps them, and back.</summary>
    public static string JoinViews(IEnumerable<string> uniques) => string.Join(";", uniques.Where(u => u.Length > 0).Distinct(StringComparer.Ordinal).OrderBy(u => u, StringComparer.Ordinal));
    public static List<string> SplitViews(Line l) => l.Extra is { } x && x.TryGetValue("views", out var v) && v is { Length: > 0 } ? [.. v.Split(';')] : [];

    /// <summary>A type that comes with its first instance (a group's type with the group, an image's with the image): no step of its own when it is missing.</summary>
    public static bool ComesWithInstance(Line l) => l.What == "type" && l.CategoryId is -2000095 or -2000096 or -2000560;

    /// <summary>A group's members (UniqueIds), when its line has them.</summary>
    public static List<string> Members(Line l) =>
        l.Extra is { } x && x.TryGetValue("members", out var text) ? text.Split([';'], StringSplitOptions.RemoveEmptyEntries).ToList() : [];

    /// <summary>The lines that have something to do once everything else is played: joins and cuts, a view's graphics, a shape to check against its kept copy.</summary>
    public static List<Line> AfterLines(Parsed file) =>
        file.Elements.Where(l => l.Kind != Kinds.Deleted && l.Unique is { Length: > 0 } && l.Extra is { } x
            && (x.ContainsKey("joins") || x.ContainsKey("voids") || x.ContainsKey("cuts") || x.ContainsKey("attop") || x.ContainsKey("atbase") || x.ContainsKey("vg") || x.ContainsKey("shape"))).ToList();

    /// <summary>The line's element has a copy in the kept-elements project.</summary>
    public static bool IsKept(Line l) => l.Extra is { } x && x.ContainsKey("kept");

    /// <summary>True while a change file of the folder still names a kept element (<paramref name="texts"/>: the folder's change files).</summary>
    public static bool KeptInUse(IEnumerable<string> texts) =>
        texts.Any(t => t.IndexOf("\"kept\":\"1\"", StringComparison.Ordinal) >= 0 || t.IndexOf("\"gkept\":\"1\"", StringComparison.Ordinal) >= 0);

    // ---- A group edited after the last save -------------------------------------------------------------------------------

    /// <summary>
    /// What a group is made of, whichever way it lies: each member's kind and how far its point or ends are from the
    /// group's origin (<paramref name="members"/>), as a count and a short print. Moving, turning or mirroring the
    /// group leaves it as it is; an edit inside the group (a member moved, added, taken out, of another type) changes it.
    /// </summary>
    public static string GroupShape(IEnumerable<(string Kind, double[] Far)> members)
    {
        var parts = members.Select(m => m.Kind + "@" + string.Join(",", m.Far.OrderBy(d => d).Select(d => Math.Round(d, 2).ToString("0.##", CultureInfo.InvariantCulture))))
            .OrderBy(p => p, StringComparer.Ordinal).ToList();
        using var sha = SHA1.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join(";", parts)));
        return parts.Count.ToString(CultureInfo.InvariantCulture) + ":" + string.Concat(hash.Take(8).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// A group's line whose type was edited after the last save: the UniqueId its kept copy is under (its own, or the
    /// group of the same type that was copied), and what the group was made of then. Null when the line has neither.
    /// </summary>
    public static (string Key, string Shape)? GroupEdit(Line l)
    {
        if (l.What != "group" || l.Extra is not { } x || !x.TryGetValue("gshape", out var shape) || shape.Length == 0) return null;
        if (x.TryGetValue("glike", out var like) && like.Length > 0) return (like, shape);
        return x.ContainsKey("gkept") && l.Unique is { Length: > 0 } own ? (own, shape) : null;
    }

    // ---- Where something that wasn't done is ----------------------------------------------------------------------------

    /// <summary>The revision the "not done" clouds are drawn on (one for the model; Remove clouds takes it out again).</summary>
    public const string CloudRevision = "Lifeline: not done";

    /// <summary>Half the side of a cloud around a point, in feet: about 6 mm on paper at the view's scale, never under half a metre.</summary>
    public static double CloudHalf(int scale) => Math.Max(1.5, 0.02 * Math.Max(1, scale));

    /// <summary>
    /// The grids a point is next to (in plan): the nearest one and the nearest one crossing it, as "B / 3"; one name
    /// when no grid crosses it; "" when the model has no grids. A grid counts past its ends.
    /// </summary>
    public static string NearGrids(Xyz p, IReadOnlyList<(string Name, Xyz P, Xyz Q)> grids)
    {
        (double X, double Y) Along((string Name, Xyz P, Xyz Q) g)
        {
            double dx = g.Q.X - g.P.X, dy = g.Q.Y - g.P.Y, length = Math.Sqrt(dx * dx + dy * dy);
            return length < 1e-9 ? (0, 0) : (dx / length, dy / length);
        }
        double Far((string Name, Xyz P, Xyz Q) g)
        {
            var (ax, ay) = Along(g);
            if (ax == 0 && ay == 0) return Math.Sqrt((p.X - g.P.X) * (p.X - g.P.X) + (p.Y - g.P.Y) * (p.Y - g.P.Y));
            return Math.Abs((p.X - g.P.X) * ay - (p.Y - g.P.Y) * ax);
        }
        if (grids.Count == 0) return "";
        var first = grids.OrderBy(Far).First();
        var (fx, fy) = Along(first);
        var crossing = grids.Where(g => { var (x, y) = Along(g); return Math.Abs(x * fy - y * fx) > 0.5; }).OrderBy(Far).ToList();
        return crossing.Count == 0 ? first.Name : $"{first.Name} / {crossing[0].Name}";
    }

    /// <summary>Where something is, in words for the report and the window: view "Level 1", near grids B / 3, level L1. "" when nothing is known.</summary>
    public static string WhereText(string? view, string? grids, string? level)
    {
        var parts = new List<string>();
        if (view is { Length: > 0 }) parts.Add($"view \"{view}\"");
        if (grids is { Length: > 0 }) parts.Add((grids.Contains(" / ") ? "near grids " : "near grid ") + grids);
        if (level is { Length: > 0 }) parts.Add("level " + level);
        return string.Join(", ", parts);
    }

    /// <summary>A report line for something not done, with where it is under it when that is known.</summary>
    public static string ReportLine(string notDone, string? where) => "- " + notDone + (where is { Length: > 0 } ? Environment.NewLine + "    where: " + where : "");

    /// <summary>Revit's own step that loads a family (Load Family, Reload, Load into Project and Close): how a family loaded again over itself is told.</summary>
    public static bool IsFamilyLoad(string transaction) =>
        transaction.IndexOf("Load", StringComparison.OrdinalIgnoreCase) >= 0 && transaction.IndexOf("Famil", StringComparison.OrdinalIgnoreCase) >= 0
        || transaction.IndexOf("Load into Project", StringComparison.OrdinalIgnoreCase) >= 0 || transaction.StartsWith("Reload", StringComparison.OrdinalIgnoreCase);

    /// <summary>How long one copy into the kept-elements project may take before copies stop for the session (Revit must never feel heavy).</summary>
    public const int KeepLimitMs = 15000;

    /// <summary>The least time between two goes of copying into the kept-elements project (it is opened and saved each time).</summary>
    public const int KeepEveryMs = 15000;

    /// <summary>The most elements copied in one go.</summary>
    public const int KeepMost = 60;

    /// <summary>
    /// The "not done" lines grouped by reason, commonest first, for the window: one row for each reason with how many
    /// (the lines stay in the report). <paramref name="max"/> reasons; the rest are counted in the last entry.
    /// </summary>
    public static List<(string Reason, List<int> Items)> GroupReasons(IReadOnlyList<string> notDone, int max = 12)
    {
        var groups = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var order = new List<string>();
        for (var i = 0; i < notDone.Count; i++)
        {
            var reason = Generic(Reason(notDone[i]));
            if (!groups.TryGetValue(reason, out var list)) { groups[reason] = list = []; order.Add(reason); }
            list.Add(i);
        }
        var sorted = order.OrderByDescending(r => groups[r].Count).Select(r => (r, groups[r])).ToList();
        if (sorted.Count <= max) return sorted;
        var rest = sorted.Skip(max - 1).SelectMany(g => g.Item2).OrderBy(i => i).ToList();
        return [.. sorted.Take(max - 1), ("other reasons", rest)];
    }

    /// <summary>
    /// The one line a replay ends with: "All 350 changes are back. Check the model before you save." or
    /// "338 of 350 changes are back; 12 need you (listed below). Check the model before you save."
    /// </summary>
    public static string ReplaySummary(int back, int notDone, string? stopped)
    {
        var all = back + notDone;
        var text = notDone == 0
            ? (all == 1 ? "The change is back." : $"All {all} changes are back.")
            : $"{back} of {all} changes are back; {notDone} need{(notDone == 1 ? "s" : "")} you (listed below).";
        return text + (stopped is not null ? " " + stopped : "") + " Check the model before you save.";
    }

    /// <summary>A reason without what is particular to one element (quoted names, numbers), so alike reasons group together.</summary>
    public static string Generic(string reason)
    {
        var text = new StringBuilder(reason.Length);
        var quoted = false;
        foreach (var c in reason)
        {
            if (c == '"') { quoted = !quoted; if (!quoted) text.Append("…"); continue; }
            if (!quoted) text.Append(c);
        }
        var flat = text.ToString();
        var cut = flat.IndexOf("; in the model now it is", StringComparison.Ordinal);
        if (cut >= 0 && flat.IndexOf(": delete it by hand", cut, StringComparison.Ordinal) is var end and >= 0) flat = flat.Substring(0, cut) + flat.Substring(end);
        return flat.Trim();
    }

    // ---- A schedule's look ---------------------------------------------------------------------------------------------

    /// <summary>A schedule's headings, widths, hidden columns, sorting and grouping, filters and totals, as written in its line.</summary>
    public sealed class Look
    {
        public bool Itemized { get; set; } = true;
        public bool GrandTotal { get; set; }
        public bool Title { get; set; } = true;
        public bool Headers { get; set; } = true;
        public List<LookCol> Cols { get; set; } = [];
        public List<LookSort> Sorts { get; set; } = [];
        public List<LookFilter> Filters { get; set; } = [];
    }

    /// <summary>A column, by its parameter: heading, hidden, width.</summary>
    public sealed class LookCol { public long P { get; set; } public string? H { get; set; } public bool Hid { get; set; } public double W { get; set; } }

    /// <summary>A sorting or grouping row, by its column's parameter.</summary>
    public sealed class LookSort { public long P { get; set; } public bool Desc { get; set; } public bool Header { get; set; } public bool Footer { get; set; } public bool Blank { get; set; } }

    /// <summary>A filter row: its column's parameter, the comparison, and the value (K: s text, d number, i whole number, e element, n none).</summary>
    public sealed class LookFilter { public long P { get; set; } public string T { get; set; } = ""; public string K { get; set; } = "n"; public string? V { get; set; } }

    // ---- What a replay made again ---------------------------------------------------------------------------------------

    /// <summary>
    /// Next to a change file: the elements a replay of it made again (old UniqueId, tab, new UniqueId, one per line), so
    /// playing the file again (after a Stop, or to finish what Revit refused) sets those instead of making them twice.
    /// </summary>
    public static string MadeMapName(string changeFile) => changeFile + ".made";

    public static Dictionary<string, string> ParseMade(IEnumerable<string> lines)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var cut = line.IndexOf('\t');
            if (cut > 0 && cut < line.Length - 1) map[line.Substring(0, cut)] = line.Substring(cut + 1).Trim();
        }
        return map;
    }

    public static IEnumerable<string> WriteMade(IReadOnlyDictionary<string, string> map) => map.Select(m => m.Key + "\t" + m.Value);

    // ---- The elements seen (so a deletion can name what was deleted) ---------------------------------------------------

    /// <summary>
    /// Each element's UniqueId and category by its id, kept small (a big model has a million elements): a UniqueId is a
    /// long code shared by many elements plus a short number, so the code is kept once and the number as a number.
    /// Revit can't tell a deleted element's UniqueId, only its id: this is where the deletion's line gets it from.
    /// </summary>
    public sealed class KnownIds
    {
        private readonly Dictionary<long, (int Prefix, ulong Suffix, int Category)> _byId = [];
        private readonly Dictionary<string, int> _prefixIds = new(StringComparer.Ordinal);
        private readonly List<string> _prefixes = [];
        private readonly Dictionary<string, int> _categoryIds = new(StringComparer.Ordinal);
        private readonly List<string> _categories = [];
        // UniqueIds that aren't "code-hexnumber" (none seen so far) are kept whole.
        private readonly Dictionary<long, string> _whole = [];

        public const int Cap = 3_000_000;

        public int Count => _byId.Count;

        public bool Has(long id) => _byId.ContainsKey(id);

        public void Remember(long id, string unique, string? category)
        {
            if (string.IsNullOrEmpty(unique) || (Count >= Cap && !Has(id))) return;
            var cat = 0;
            if (category is { Length: > 0 } && !_categoryIds.TryGetValue(category, out cat))
            {
                _categories.Add(category);
                _categoryIds[category] = cat = _categories.Count;
            }
            var cut = unique.LastIndexOf('-');
            if (cut > 0 && unique.Length - cut - 1 is > 0 and <= 16
                && ulong.TryParse(unique.Substring(cut + 1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var suffix)
                && unique.Substring(cut + 1).ToLowerInvariant() == unique.Substring(cut + 1))
            {
                var head = unique.Substring(0, cut);
                if (!_prefixIds.TryGetValue(head, out var prefix))
                {
                    _prefixes.Add(head);
                    _prefixIds[head] = prefix = _prefixes.Count - 1;
                }
                // The number's width, so "0004d2a1" comes back with its zeros.
                _byId[id] = (prefix, suffix, cat | ((unique.Length - cut - 1) << 24));
                return;
            }
            _whole[id] = unique;
            _byId[id] = (-1, 0, cat);
        }

        public bool TryGet(long id, out string unique, out string? category)
        {
            unique = "";
            category = null;
            if (!_byId.TryGetValue(id, out var k)) return false;
            var cat = k.Category & 0xFFFFFF;
            category = cat > 0 ? _categories[cat - 1] : null;
            if (k.Prefix < 0) return _whole.TryGetValue(id, out unique!);
            var width = k.Category >> 24;
            unique = _prefixes[k.Prefix] + "-" + k.Suffix.ToString("x" + width, CultureInfo.InvariantCulture);
            return true;
        }

        public void Forget(long id) { _byId.Remove(id); _whole.Remove(id); }
    }

    /// <summary>Milliseconds of Revit's time one idle moment may spend noting the model's elements after it opens.</summary>
    public const int IndexBudgetMs = 15;

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

    /// <summary>
    /// What the user does about one thing a replay didn't do, as one plain instruction: the reason's own "… by hand"
    /// when it has one, else by what the line is (deleted, made, changed), with what Revit said.
    /// </summary>
    public static string ToDo(Line? line, string notDone)
    {
        var reason = Reason(notDone).Replace("\r", " ").Replace("\n", " ").Trim();
        var name = line?.Type ?? line?.Name;
        var label = line is null ? "this one" : (line.Category is { Length: > 0 } c ? c : "the element") + (name is { Length: > 0 } ? $" \"{name}\"" : "");
        var hand = reason.LastIndexOf("by hand", StringComparison.Ordinal);
        if (hand >= 0)
        {
            var from = reason.LastIndexOf(": ", hand, StringComparison.Ordinal);
            var clause = reason.Substring(from < 0 ? 0 : from + 2, hand + 7 - (from < 0 ? 0 : from + 2)).Trim();
            if (from < 0 || clause.Length < 12) clause = reason.Substring(0, hand + 7);
            return $"{label}: {clause}.";
        }
        var said = reason.Length > 200 ? reason.Substring(0, 200) + "…" : reason;
        if (line?.Kind == Kinds.Deleted) return $"Delete {label} by hand: you had deleted it, and it is still in the model. Revit said: {said}";
        if (line?.New == true) return $"Make {label} again by hand where it was. Revit said: {said}";
        return $"Check {label} and set it by hand as you had changed it. Revit said: {said}";
    }

    /// <summary>Where the step-by-step row is: "3 of 12".</summary>
    public static string StepOf(int index, int count) => $"{index + 1} of {count}";

    /// <summary>The next (or previous) item of <paramref name="count"/>, going round; -1 (not started) goes to the first, or to the last going back.</summary>
    public static int Step(int index, int count, bool back) => count <= 0 ? -1 : index < 0 ? (back ? count - 1 : 0) : ((index + (back ? -1 : 1)) % count + count) % count;

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
        ((Same(a.P, b.P) && Same(a.Q, b.Q)) || (Same(a.P, b.Q) && Same(a.Q, b.P))) && Same(a.M, b.M) && SameCurve(a.C, b.C)
        && (a.Slope is null ? b.Slope is null : b.Slope is { } s && Same(a.Slope.Value, s));

    /// <summary>Two exact curves (<see cref="Seg.C"/>) are the same: the same kind and numbers, each within a hundredth of a millimetre.</summary>
    public static bool SameCurve(string? a, string? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a == b) return true;
        // A spline through points is its points: older files also wrote end tangents, newer ones only when needed.
        static string Points(string c) => c.StartsWith("H|", StringComparison.Ordinal) && c.Split('|') is { Length: >= 3 } h ? string.Join("|", h.Take(3)) : c;
        (a, b) = (Points(a), Points(b));
        var (x, y) = (a.Split('|', ',', ';'), b.Split('|', ',', ';'));
        if (x.Length != y.Length) return false;
        for (var i = 0; i < x.Length; i++)
        {
            if (x[i] == y[i]) continue;
            if (!double.TryParse(x[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var p) || !double.TryParse(y[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var q) || !Same(p, q)) return false;
        }
        return true;
    }

    /// <summary>
    /// Two shape signatures (an element's box, volume and number of faces, as numbers with spaces between) are the same
    /// shape: lengths within a millimetre, the volume within a thousandth, the same number of faces.
    /// </summary>
    public static bool SameSignature(string? a, string? b)
    {
        if (a is null || b is null) return a == b;
        var (x, y) = (a.Split(' '), b.Split(' '));
        if (x.Length != 8 || y.Length != 8) return a == b;
        for (var i = 0; i < 8; i++)
        {
            if (!double.TryParse(x[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var p) || !double.TryParse(y[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var q)) return false;
            var slack = i < 6 ? 0.0035 : i == 6 ? 0.01 + Math.Abs(p) * 0.001 : 0.5;
            if (Math.Abs(p - q) > slack) return false;
        }
        return true;
    }

    /// <summary>
    /// A view's graphics as written in its line: the categories hidden, the categories, filters and elements drawn
    /// differently (each as "name=value" pairs), the filters in order, and the elements hidden or shown again.
    /// </summary>
    public sealed class Vg
    {
        public List<long> Hidden { get; set; } = [];
        public Dictionary<string, string> Over { get; set; } = [];
        public List<VgFilter> Filters { get; set; } = [];
        /// <summary>Elements (by UniqueId) hidden in the view, shown again, and drawn differently (empty text: as the view draws them).</summary>
        public List<string>? Hide { get; set; }
        public List<string>? Show { get; set; }
        public Dictionary<string, string>? Elements { get; set; }
    }

    public sealed class VgFilter
    {
        public string U { get; set; } = "";
        public bool Visible { get; set; } = true;
        public bool Enabled { get; set; } = true;
        public string Over { get; set; } = "";
    }

    /// <summary>
    /// The relations written in a line ("joins", "voids", "cuts", "attop", "atbase"): the other elements' UniqueIds;
    /// in "joins" each starts with + (this element cuts the other) or - (the other cuts this one).
    /// </summary>
    public static List<(string Unique, bool Cuts)> Relations(Line l, string key)
    {
        var list = new List<(string, bool)>();
        if (l.Extra is not { } x || !x.TryGetValue(key, out var text) || text.Length == 0) return list;
        foreach (var part in text.Split(';'))
        {
            if (part.Length == 0) continue;
            if (part[0] is '+' or '-') list.Add((part.Substring(1), part[0] == '+'));
            else list.Add((part, false));
        }
        return list;
    }

    /// <summary>The last line of each element that has relations to others, in file order: played after everything is made.</summary>
    public static List<Line> RelationLines(Parsed file)
    {
        var last = new Dictionary<string, Line>(StringComparer.Ordinal);
        foreach (var l in file.Elements)
        {
            if (l.Unique is not { Length: > 0 } u) continue;
            if (l.Kind == Kinds.Deleted) { last.Remove(u); continue; }
            if (l.Extra is { } x && (x.ContainsKey("joins") || x.ContainsKey("voids") || x.ContainsKey("cuts") || x.ContainsKey("attop") || x.ContainsKey("atbase"))) last[u] = l;
        }
        return file.Elements.Where(l => l.Unique is { } u && last.TryGetValue(u, out var keep) && ReferenceEquals(keep, l)).ToList();
    }

    /// <summary>Two point sets of a shape-edited slab are the same (in any order).</summary>
    public static bool SamePoints(List<Xyz>? a, List<Xyz>? b)
    {
        if (a is null || b is null) return (a?.Count ?? 0) == (b?.Count ?? 0);
        return a.Count == b.Count && a.All(p => b.Any(q => Same(p, q)));
    }

    public static bool Same(Xyz? a, Xyz? b) => a is null || b is null ? a is null && b is null : Same(a.X, b.X) && Same(a.Y, b.Y) && Same(a.Z, b.Z);
}
