using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SketchProMax.Tools;

/// <summary>
/// The drawing package from one sentence, without Revit: the structured plan (what the tool takes), a best-effort reader
/// for a plain English request ("GA plans, sections and column schedule for levels 1-5 on A1 with our title block"),
/// and the check of a plan against what the model has (levels, title blocks, sheet numbers and view names already
/// taken), which turns it into the exact views, sheets, tags and dimension strings to make, with counts. The chat's
/// brief handles other languages and phrasing; this reader is the fallback for a sentence typed straight in.
/// Nothing here deletes or renames: names and numbers already in the model are stepped over, never reused.
/// </summary>
internal static class DrawingPackage
{
    public static readonly string[] ViewKinds = ["floor_plan", "structural_plan", "ceiling_plan", "section", "elevation"];
    public static readonly string[] IssueKinds = ["register", "transmittal", "none"];
    public static readonly string[] Stages = ["Views", "Dimensions", "Tags", "Sheets", "Title block fields", "Issue sheet"];

    /// <summary>ISO sheet sizes, landscape, mm.</summary>
    public static readonly IReadOnlyDictionary<string, (double W, double H)> Sizes = new Dictionary<string, (double, double)>(StringComparer.OrdinalIgnoreCase)
    {
        ["A0"] = (1189, 841), ["A1"] = (841, 594), ["A2"] = (594, 420), ["A3"] = (420, 297), ["A4"] = (297, 210),
    };

    /// <summary>Schedule words a request may use, and the Revit category each one schedules.</summary>
    public static readonly IReadOnlyDictionary<string, string> ScheduleWords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["column"] = "Structural Columns", ["beam"] = "Structural Framing", ["framing"] = "Structural Framing",
        ["foundation"] = "Structural Foundations", ["footing"] = "Structural Foundations", ["pile"] = "Structural Foundations",
        ["door"] = "Doors", ["window"] = "Windows", ["room"] = "Rooms", ["wall"] = "Walls", ["floor"] = "Floors", ["slab"] = "Floors",
    };

    /// <summary>Fields asked for in each schedule (missing ones are reported by create_schedule, not fatal).</summary>
    public static IReadOnlyList<string> ScheduleFields(string category) => category switch
    {
        "Structural Columns" => ["Type Mark", "Family and Type", "Base Level", "Top Level", "Structural Material", "Count"],
        "Structural Framing" => ["Type Mark", "Family and Type", "Reference Level", "Length", "Structural Material", "Count"],
        "Structural Foundations" => ["Type Mark", "Family and Type", "Level", "Structural Material", "Count"],
        "Doors" or "Windows" => ["Mark", "Family and Type", "Level", "Width", "Height", "Count"],
        "Rooms" => ["Number", "Name", "Level", "Area"],
        _ => ["Family and Type", "Level", "Count"],
    };

    /// <summary>Tag categories per plan kind when the request doesn't say.</summary>
    public static IReadOnlyList<string> DefaultTags(string kind) => kind switch
    {
        "structural_plan" => ["Structural Columns", "Structural Framing"],
        "floor_plan" => ["Doors", "Windows", "Rooms"],
        _ => [],
    };

    // ---- The structured plan -----------------------------------------------------------------------------------------

    /// <summary>What the user asked for: the tool's inputs, or what <see cref="Parse"/> read from a sentence.</summary>
    public sealed class Spec
    {
        public string Request = "";
        /// <summary>ga, structural_ga, rcp or custom: only picks the defaults (plan kind, tags, sheet prefix).</summary>
        public string Package = "ga";
        /// <summary>Level names, numbers ("3"), ranges ("1-5") or "all". Empty: all levels.</summary>
        public List<string> Levels = [];
        public List<string> Views = [];
        /// <summary>Categories to schedule ("Structural Columns").</summary>
        public List<string> Schedules = [];
        public int Sections = 2;
        public int Scale = 100;
        public string SheetSize = "A1";
        /// <summary>"" or "our": the model's usual title block for the size; otherwise a title block name.</summary>
        public string TitleBlock = "";
        public string SheetPrefix = "";
        public int FirstNumber = 101;
        /// <summary>Tag categories; null: the defaults per plan kind; empty: no tags.</summary>
        public List<string>? Tags;
        public bool Dimensions = true;
        public string? ViewTemplate;
        public string IssueSheet = "register";
        public Dictionary<string, string> TitleBlockFields = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>What the sentence reader understood or assumed, one line each.</summary>
        public List<string> Read = [];
    }

    // ---- Reading a sentence --------------------------------------------------------------------------------------------

    private const RegexOptions Ri = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly Regex Range = new(@"\b(?:levels?|floors?|storeys?|stories|l)\s*(-?\d{1,3})\s*(?:-|–|—|to|through|thru|until|\.\.)\s*(?:levels?|l)?\s*(-?\d{1,3})\b", Ri);
    private static readonly Regex GroundTo = new(@"\b(?:ground|gf)(?:\s+floor)?\s*(?:-|–|—|to|through|\.\.)\s*(?:levels?|l)?\s*(\d{1,3})\b", Ri);
    private static readonly Regex ListOf = new(@"\b(?:levels?|floors?)\s+((?:-?\d{1,3}\s*(?:,|and|&)\s*)+-?\d{1,3})\b", Ri);
    private static readonly Regex One = new(@"\b(?:level|floor|l)\s*(-?\d{1,3})\b(?!\s*(?:-|–|to))", Ri);
    private static readonly Regex AllLevels = new(@"\b(?:all|every|each)\s+(?:the\s+)?(?:levels?|floors?|storeys?)\b", Ri);
    private static readonly Regex ScaleText = new(@"\b1\s*[:/]\s*(\d{1,4})\b", Ri);
    private static readonly Regex SizeText = new(@"(?<![\w-])(A[0-4])(?![\w-])", Ri);
    private static readonly Regex Numbering = new(@"\b(?:numbered|numbering|starting(?:\s+(?:at|from|with))?|sheet\s+numbers?(?:\s+from)?)\s+(?:sheets?\s+)?([A-Za-z]{1,4}[-_.]?)?(\d{2,4})\b", Ri);
    private static readonly Regex Prefix = new(@"\bprefix\s+['""]?([A-Za-z0-9]{1,4}[-_.]?)['""]?", Ri);
    private static readonly Regex NamedTitleBlock = new(@"title\s*-?\s*block\s+(?:called|named)\s+['""]?([^'"",;]+?)['""]?(?=$|[,;.]|\s+(?:and|for|on|at|with)\b)|(?:with|using)\s+(?:the\s+)?['""]([^'""]+)['""]\s+title\s*-?\s*block", Ri);
    private static readonly Regex OurTitleBlock = new(@"\b(?:our|the\s+office|the\s+firm'?s?|the\s+company|the\s+standard|the\s+usual|the\s+default)\s+title\s*-?\s*blocks?\b", Ri);
    private static readonly Regex SectionCount = new(@"\b(\d|one|two|three|four|five|six)\s+(?:building\s+|cross\s+)?sections?\b", Ri);
    private static readonly Regex Template = new(@"\b(?:view\s+)?template\s+['""]([^'""]+)['""]", Ri);
    private static readonly Regex Schedule = new(@"\b(column|beam|framing|foundation|footing|pile|door|window|room|wall|floor|slab)s?\s+schedules?\b|\bschedules?\s+of\s+(column|beam|framing|foundation|footing|pile|door|window|room|wall|floor|slab)s?\b", Ri);
    private static readonly Regex TagWhat = new(@"\btag(?:s|ged|ging)?\s+(?:the\s+|all\s+)?((?:(?:column|beam|door|window|room|wall|floor|slab|foundation|footing)s?(?:\s*(?:,|and|&)\s*)?)+)", Ri);

    private static int Word(string w) => w.ToLowerInvariant() switch
    {
        "one" => 1, "two" => 2, "three" => 3, "four" => 4, "five" => 5, "six" => 6,
        _ => int.TryParse(w, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0,
    };

    /// <summary>
    /// A best-effort reading of a plain English request. Whatever it doesn't find keeps its default, and
    /// <see cref="Spec.Read"/> says what was read and what was assumed, so the plan shown for approval is honest.
    /// </summary>
    public static Spec Parse(string request)
    {
        var s = new Spec { Request = request ?? "" };
        var t = " " + (request ?? "").Replace('–', '-').Replace('—', '-') + " ";

        var structural = Regex.IsMatch(t, @"\b(structural|framing|engineering\s+plans?|s\.?\s?g\.?\s?a)\b", Ri);
        var ceiling = Regex.IsMatch(t, @"\b(ceiling|rcp|reflected)\b", Ri);
        var plans = Regex.IsMatch(t, @"\b(ga|g\.a\.|general\s+arrangements?|plans?|layouts?|floor\s+plans?)\b", Ri);
        if (structural) { s.Package = "structural_ga"; s.Views.Add("structural_plan"); }
        if (ceiling) s.Views.Add("ceiling_plan");
        if (plans && !structural && !(ceiling && !Regex.IsMatch(t, @"\b(ga|general\s+arrangements?|floor\s+plans?)\b", Ri))) s.Views.Add("floor_plan");
        if (s.Views.SequenceEqual(["ceiling_plan"])) s.Package = "rcp";

        if (Regex.IsMatch(t, @"\bsections?\b", Ri))
        {
            s.Views.Add("section");
            var m = SectionCount.Match(t);
            s.Sections = m.Success ? Math.Max(1, Word(m.Groups[1].Value)) : Regex.IsMatch(t, @"\b(a|one)\s+(?:building\s+)?section\b", Ri) ? 1 : 2;
            s.Read.Add($"{s.Sections} section(s)" + (m.Success ? "" : " (assumed: one each way across the building)"));
        }
        else s.Sections = 0;
        if (Regex.IsMatch(t, @"\belevations?\b", Ri)) s.Views.Add("elevation");

        foreach (Match m in Schedule.Matches(t))
        {
            var word = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).ToLowerInvariant();
            if (ScheduleWords.TryGetValue(word, out var cat) && !s.Schedules.Contains(cat)) s.Schedules.Add(cat);
        }
        if (s.Views.Count == 0 && s.Schedules.Count == 0)
        {
            s.Views.Add("floor_plan");
            s.Read.Add("no view types named: assumed GA floor plans");
        }
        s.Read.Add("views: " + string.Join(", ", s.Views.Concat(s.Schedules.Select(c => c + " schedule"))));

        // Levels
        if (AllLevels.IsMatch(t)) s.Levels.Add("all");
        else if (Range.Match(t) is { Success: true } r) s.Levels.Add($"{r.Groups[1].Value}-{r.Groups[2].Value}");
        else if (GroundTo.Match(t) is { Success: true } g) s.Levels.Add($"0-{g.Groups[1].Value}");
        else if (ListOf.Match(t) is { Success: true } list)
            s.Levels.AddRange(Regex.Split(list.Groups[1].Value, @"\s*(?:,|and|&)\s*", Ri).Where(x => x.Trim().Length > 0).Select(x => x.Trim()));
        else
            foreach (Match one in One.Matches(t))
                if (!s.Levels.Contains(one.Groups[1].Value)) s.Levels.Add(one.Groups[1].Value);
        s.Read.Add(s.Levels.Count == 0 ? "no levels named: assumed all levels" : "levels: " + string.Join(", ", s.Levels));

        if (ScaleText.Match(t) is { Success: true } sc) { s.Scale = int.Parse(sc.Groups[1].Value, CultureInfo.InvariantCulture); s.Read.Add($"scale 1:{s.Scale}"); }
        else s.Read.Add("no scale given: assumed 1:100");
        if (SizeText.Match(t) is { Success: true } sz) { s.SheetSize = sz.Groups[1].Value.ToUpperInvariant(); s.Read.Add($"sheet size {s.SheetSize}"); }
        else s.Read.Add("no sheet size given: assumed A1");

        if (NamedTitleBlock.Match(t) is { Success: true } tb)
        {
            s.TitleBlock = (tb.Groups[1].Success ? tb.Groups[1].Value : tb.Groups[2].Value).Trim();
            s.Read.Add($"title block '{s.TitleBlock}'");
        }
        else
        {
            s.TitleBlock = "our";
            s.Read.Add(OurTitleBlock.IsMatch(t) ? "your title block: the one this model's sheets use for that size" : "no title block named: the one this model's sheets use for that size");
        }

        if (Numbering.Match(t) is { Success: true } num)
        {
            if (num.Groups[1].Success) s.SheetPrefix = num.Groups[1].Value;
            s.FirstNumber = int.Parse(num.Groups[2].Value, CultureInfo.InvariantCulture);
            s.Read.Add($"sheets numbered from {s.SheetPrefix}{num.Groups[2].Value}");
        }
        if (Prefix.Match(t) is { Success: true } px) s.SheetPrefix = px.Groups[1].Value;

        if (Regex.IsMatch(t, @"\b(no|without)\s+tags?\b|\buntagged\b", Ri)) { s.Tags = []; s.Read.Add("no tags"); }
        else if (TagWhat.Match(t) is { Success: true } tw)
        {
            s.Tags = [];
            foreach (Match w in Regex.Matches(tw.Groups[1].Value, @"[a-z]+", Ri))
            {
                var word = w.Value.ToLowerInvariant().TrimEnd('s');
                if (ScheduleWords.TryGetValue(word, out var cat) && !s.Tags.Contains(cat)) s.Tags.Add(cat);
            }
            s.Read.Add("tags: " + string.Join(", ", s.Tags));
        }
        if (Regex.IsMatch(t, @"\b(no|without)\s+dim(?:ension)?s?\b|\bundimensioned\b", Ri)) { s.Dimensions = false; s.Read.Add("no dimensions"); }

        if (Regex.IsMatch(t, @"\btransmittal\b", Ri)) s.IssueSheet = "transmittal";
        else if (Regex.IsMatch(t, @"\b(no|without)\s+(?:a\s+)?(?:register|issue\s+sheet|transmittal)\b", Ri)) s.IssueSheet = "none";
        if (Template.Match(t) is { Success: true } vt) s.ViewTemplate = vt.Groups[1].Value.Trim();
        return s;
    }

    /// <summary>
    /// The tool's inputs: a sentence in request (read first), then any structured field, which wins over what the
    /// sentence said. Bad values throw ArgumentException with what to give instead.
    /// </summary>
    public static Spec FromArgs(IReadOnlyDictionary<string, JsonElement> raw)
    {
        bool Has(string k) => raw.TryGetValue(k, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
        string? Str(string k) => Has(k) ? raw[k].ValueKind == JsonValueKind.String ? raw[k].GetString() : raw[k].GetRawText() : null;
        List<string>? List(string k)
        {
            if (!Has(k)) return null;
            var v = raw[k];
            if (v.ValueKind == JsonValueKind.String) return v.GetString()!.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            if (v.ValueKind != JsonValueKind.Array) throw new ArgumentException($"{k} must be a list.");
            return v.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.GetRawText()).Where(x => x.Trim().Length > 0).ToList();
        }
        int? Int(string k)
        {
            if (!Has(k)) return null;
            var v = raw[k];
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
            if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return n;
            throw new ArgumentException($"{k} must be a whole number.");
        }

        var request = Str("request");
        var s = string.IsNullOrWhiteSpace(request) ? new Spec() : Parse(request!);
        if (string.IsNullOrWhiteSpace(request)) s.Sections = 0;
        if (Str("package") is { } p) s.Package = p.Trim().ToLowerInvariant();
        if (List("levels") is { } levels) s.Levels = levels;
        if (List("views") is { } views) s.Views = views.Select(v => v.Trim().ToLowerInvariant().Replace(' ', '_')).ToList();
        if (List("schedules") is { } schedules)
            s.Schedules = schedules.Select(c => ScheduleWords.TryGetValue(c.Trim().TrimEnd('s'), out var cat) ? cat : c.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (Int("sections") is { } sections)
        {
            s.Sections = sections;
            if (sections > 0 && !s.Views.Contains("section")) s.Views.Add("section");
        }
        else if (s.Views.Contains("section") && s.Sections == 0) s.Sections = 2;
        if (Int("scale") is { } scale) s.Scale = scale;
        if (Str("sheet_size") is { } size) s.SheetSize = size.Trim().ToUpperInvariant();
        if (Str("title_block") is { } tb) s.TitleBlock = tb.Trim();
        if (Str("sheet_prefix") is { } prefix) s.SheetPrefix = prefix.Trim();
        if (Int("first_number") is { } first) s.FirstNumber = first;
        if (List("tags") is { } tags) s.Tags = tags.Select(c => ScheduleWords.TryGetValue(c.Trim().TrimEnd('s'), out var cat) ? cat : c.Trim()).ToList();
        if (Has("dimensions")) s.Dimensions = raw["dimensions"].ValueKind == JsonValueKind.True;
        if (Str("view_template") is { Length: > 0 } vt) s.ViewTemplate = vt.Trim();
        if (Str("issue_sheet") is { } issue) s.IssueSheet = issue.Trim().ToLowerInvariant();
        if (Has("title_block_fields"))
        {
            if (raw["title_block_fields"].ValueKind != JsonValueKind.Object) throw new ArgumentException("title_block_fields must be an object of field name -> value.");
            foreach (var f in raw["title_block_fields"].EnumerateObject())
                s.TitleBlockFields[f.Name] = f.Value.ValueKind == JsonValueKind.String ? f.Value.GetString() ?? "" : f.Value.GetRawText();
        }
        if (s.Views.Count == 0 && s.Schedules.Count == 0)
            s.Views.Add(s.Package switch { "structural_ga" => "structural_plan", "rcp" => "ceiling_plan", _ => "floor_plan" });
        if (!s.Views.Contains("section")) s.Sections = 0;
        return s;
    }

    // ---- What the model has --------------------------------------------------------------------------------------------

    public sealed record LevelFact(string Name, double ElevationMm);

    /// <param name="Label">"Family: Type".</param>
    /// <param name="WidthMm">Sheet width read from the type (0 when it has none).</param>
    /// <param name="Sheets">How many sheets in the model use it.</param>
    public sealed record TitleBlockFact(string Label, double WidthMm, double HeightMm, int Sheets);

    public sealed class Facts
    {
        public List<LevelFact> Levels = [];
        public List<TitleBlockFact> TitleBlocks = [];
        public HashSet<string> SheetNumbers = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ViewNames = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ViewTemplates = new(StringComparer.OrdinalIgnoreCase);
        public int Grids;
        public bool StructuralPlanType = true, CeilingPlanType = true;
    }

    // ---- The checked plan ----------------------------------------------------------------------------------------------

    /// <param name="Key">The plan's own id for the view ("plan:Level 1:floor_plan").</param>
    /// <param name="Kind">floor_plan, structural_plan, ceiling_plan, section, elevation or schedule.</param>
    public sealed record ViewItem(string Key, string Kind, string Name, string? Level = null, string? Side = null, string? Category = null, int Index = 0);

    public sealed record SheetItem(string Number, string Name, List<string> Views, bool Register = false);

    public sealed record TagItem(string View, string Category);

    public sealed class Plan
    {
        public Spec Spec = new();
        public List<LevelFact> Levels = [];
        public List<ViewItem> Views = [];
        public List<SheetItem> Sheets = [];
        public List<TagItem> Tags = [];
        public List<string> DimensionViews = [];
        /// <summary>The title block to use ("Family: Type"), or null when one must be loaded first (a hand-off).</summary>
        public string? TitleBlock;
        /// <summary>Why the user must load a title block before sheets can be made; null when one is there.</summary>
        public string? TitleBlockMissing;
        public List<string> Errors = [];
        public List<string> Warnings = [];
        public bool Ok => Errors.Count == 0;

        public SheetItem? Register => Sheets.FirstOrDefault(s => s.Register);

        public int Count(string kind) => Views.Count(v => v.Kind == kind);

        /// <summary>The counts shown for approval.</summary>
        public object Counts() => new
        {
            levels = Levels.Count,
            views = Views.Count(v => v.Kind != "schedule"),
            plans = Views.Count(v => v.Kind.EndsWith("_plan", StringComparison.Ordinal)),
            sections = Count("section"),
            elevations = Count("elevation"),
            schedules = Count("schedule"),
            sheets = Sheets.Count,
            titleBlocks = Sheets.Count,
            dimensionStrings = DimensionViews.Count > 0 ? $"about {DimensionViews.Count * 2} (one per grid direction in {DimensionViews.Count} plan(s))" : "none",
            tagRuns = Tags.Count,
            tagCategories = Tags.Select(t => t.Category).Distinct().ToList(),
        };

        /// <summary>Each stage with how many items it runs, in order.</summary>
        public List<(string Stage, int Items)> StageItems() =>
        [
            ("Views", Views.Count),
            ("Dimensions", DimensionViews.Count),
            ("Tags", Tags.Count),
            ("Sheets", Sheets.Count(s => !s.Register)),
            ("Title block fields", Spec.TitleBlockFields.Count > 0 && Sheets.Count > 0 ? 1 : 0),
            ("Issue sheet", Register is null ? 0 : 1),
        ];
    }

    private static readonly Regex FirstInt = new(@"(?<![A-Za-z0-9])(?:L|Level\s*|Lvl\s*|FL\s*)?(-?\d{1,3})(?!\d)", Ri);

    /// <summary>The number a level's name gives it: "Level 3" 3, "L03" 3, "B1" -1, "Ground" 0; null when it has none.</summary>
    public static int? LevelNumber(string name)
    {
        var n = name.Trim();
        if (Regex.IsMatch(n, @"^(?:ground|gf|g)\b", Ri) || Regex.IsMatch(n, @"\bground\s+(?:floor|level)\b", Ri)) return 0;
        if (Regex.Match(n, @"\b(?:B|Basement\s*)(\d{1,2})\b", Ri) is { Success: true } b) return -int.Parse(b.Groups[1].Value, CultureInfo.InvariantCulture);
        if (Regex.Match(n, @"\bL(\d{1,3})\b", Ri) is { Success: true } l) return int.Parse(l.Groups[1].Value, CultureInfo.InvariantCulture);
        var m = FirstInt.Match(n);
        return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>The levels the words name, lowest first; what can't be matched goes to <paramref name="errors"/>, a range with gaps to <paramref name="warnings"/>.</summary>
    public static List<LevelFact> ResolveLevels(IReadOnlyList<string> wanted, IReadOnlyList<LevelFact> levels, List<string> errors, List<string> warnings)
    {
        var all = levels.OrderBy(l => l.ElevationMm).ToList();
        if (wanted.Count == 0 || wanted.Any(w => Regex.IsMatch(w.Trim(), @"^(all|every|\*)$", Ri))) return all;
        var picked = new List<LevelFact>();
        string Available() => string.Join(", ", all.Select(l => l.Name).Take(30)) + (all.Count > 30 ? ", ..." : "");
        foreach (var raw in wanted)
        {
            var w = raw.Trim().Replace('–', '-').Replace('—', '-');
            if (all.FirstOrDefault(l => string.Equals(l.Name, w, StringComparison.OrdinalIgnoreCase)) is { } exact) { picked.Add(exact); continue; }
            int? lo = null, hi = null;
            if (Regex.Match(w, @"^(?:levels?\s*|l)?(-?\d{1,3})\s*(?:-|to|through|\.\.)\s*(?:levels?\s*|l)?(-?\d{1,3})$", Ri) is { Success: true } r)
            {
                lo = int.Parse(r.Groups[1].Value, CultureInfo.InvariantCulture);
                hi = int.Parse(r.Groups[2].Value, CultureInfo.InvariantCulture);
                if (lo > hi) (lo, hi) = (hi, lo);
            }
            else if (Regex.Match(w, @"^(?:levels?\s*|l)?(-?\d{1,3})$", Ri) is { Success: true } one) lo = hi = int.Parse(one.Groups[1].Value, CultureInfo.InvariantCulture);
            else if (Regex.IsMatch(w, @"^(ground|gf|g)$", Ri)) lo = hi = 0;

            List<LevelFact> hits;
            if (lo is { } a && hi is { } b) hits = all.Where(l => LevelNumber(l.Name) is { } n && n >= a && n <= b).ToList();
            else hits = all.Where(l => l.Name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (hits.Count == 0) errors.Add($"No level matches '{raw}'. Levels in the model: {Available()}.");
            else if (lo is { } a2 && hi is { } b2 && a2 != b2)
            {
                var found = hits.Select(l => LevelNumber(l.Name)).Distinct().Count();
                if (found < b2 - a2 + 1) warnings.Add($"Levels {a2} to {b2}: only {found} of {b2 - a2 + 1} numbers have a level ({string.Join(", ", hits.Select(l => l.Name))}). Levels in the model: {Available()}.");
            }
            picked.AddRange(hits);
        }
        return picked.Distinct().OrderBy(l => l.ElevationMm).ToList();
    }

    /// <summary>
    /// The title block for the size: a named one (by label, family or type, with the size breaking a tie), else for
    /// "our" the one this model's sheets use most among those of that size, else the most used one. Null with a reason
    /// when the user must load it first.
    /// </summary>
    public static string? PickTitleBlock(string wanted, string size, IReadOnlyList<TitleBlockFact> blocks, List<string> warnings, out string? missing)
    {
        missing = null;
        var w = (wanted ?? "").Trim();
        var mine = w.Length == 0 || Regex.IsMatch(w, @"^(our|ours|default|firm|office|company|standard|usual)$", Ri);
        if (blocks.Count == 0)
        {
            missing = mine ? $"No title block is loaded in this model. Load your {size} title block family." : $"No title block is loaded; load '{w}'.";
            return null;
        }
        var sized = blocks.Where(b => FitsSize(b, size)).ToList();
        if (!mine)
        {
            var hits = blocks.Where(b => b.Label.Equals(w, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 0) hits = blocks.Where(b => b.Label.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (hits.Count == 0)
            {
                missing = $"No title block called '{w}' is loaded (loaded: {string.Join(", ", blocks.Select(b => b.Label).Take(10))}). Load it.";
                return null;
            }
            if (hits.Count > 1) hits = hits.OrderByDescending(b => FitsSize(b, size)).ThenByDescending(b => b.Sheets).ToList();
            if (!FitsSize(hits[0], size) && Sizes.ContainsKey(size)) warnings.Add($"Title block '{hits[0].Label}' doesn't look like {size}; it is used as asked.");
            return hits[0].Label;
        }
        if (sized.Count > 0) return sized.OrderByDescending(b => b.Sheets).ThenBy(b => b.Label, StringComparer.OrdinalIgnoreCase).First().Label;
        var usual = blocks.OrderByDescending(b => b.Sheets).ThenBy(b => b.Label, StringComparer.OrdinalIgnoreCase).First();
        if (Sizes.ContainsKey(size)) warnings.Add($"No {size} title block is loaded; using '{usual.Label}', the one this model's sheets use most. Load your {size} title block and run again to change it.");
        return usual.Label;
    }

    /// <summary>A title block is of the size when its sheet is within 10 mm of it (either way round), or its name says so.</summary>
    public static bool FitsSize(TitleBlockFact b, string size)
    {
        if (!Sizes.TryGetValue(size, out var s)) return true;
        if (b.WidthMm > 0 && b.HeightMm > 0)
        {
            bool Near(double x, double y) => Math.Abs(x - y) <= 10;
            if (Near(b.WidthMm, s.W) && Near(b.HeightMm, s.H) || Near(b.WidthMm, s.H) && Near(b.HeightMm, s.W)) return true;
        }
        return Regex.IsMatch(b.Label, $@"(?<![A-Za-z0-9]){Regex.Escape(size)}(?![0-9])", Ri);
    }

    /// <summary>A name that is not taken yet: the name itself, else "name (2)", "name (3)"...; it is taken from then on.</summary>
    public static string Unique(string name, ISet<string> taken)
    {
        var n = name;
        for (var i = 2; taken.Contains(n); i++) n = $"{name} ({i})";
        taken.Add(n);
        return n;
    }

    /// <summary>A sheet number from the prefix and n (at least three digits), stepping over numbers already taken.</summary>
    public static string NextNumber(string prefix, ref int n, ISet<string> taken)
    {
        while (true)
        {
            var number = prefix + n.ToString(n < 0 ? "0" : "000", CultureInfo.InvariantCulture);
            n++;
            if (taken.Add(number)) return number;
        }
    }

    private static string PlanLabel(string kind) => kind switch
    {
        "structural_plan" => "Structural GA Plan",
        "ceiling_plan" => "Reflected Ceiling Plan",
        _ => "GA Plan",
    };

    /// <summary>Checks the request against the model and works out every view, sheet, tag run and dimension string.</summary>
    public static Plan Resolve(Spec spec, Facts facts)
    {
        var plan = new Plan { Spec = spec };
        var errors = plan.Errors;
        var unknown = spec.Views.Where(v => !ViewKinds.Contains(v)).ToList();
        if (unknown.Count > 0) errors.Add($"Unknown view type(s) {string.Join(", ", unknown)}; use {string.Join(", ", ViewKinds)}.");
        if (spec.Scale < 1 || spec.Scale > 5000) errors.Add($"Scale 1:{spec.Scale} is not a drawing scale; give e.g. 100 for 1:100.");
        if (spec.SheetSize.Length > 0 && !Sizes.ContainsKey(spec.SheetSize)) errors.Add($"Sheet size '{spec.SheetSize}' is not one of {string.Join(", ", Sizes.Keys)}.");
        if (!IssueKinds.Contains(spec.IssueSheet)) errors.Add($"issue_sheet is register, transmittal or none, not '{spec.IssueSheet}'.");
        if (spec.Sections < 0 || spec.Sections > 8) errors.Add("sections is 0 to 8.");
        if (spec.FirstNumber < 0 || spec.FirstNumber > 99999) errors.Add("first_number is 0 to 99999.");
        if (spec.SheetPrefix.IndexOfAny(@"\:{}[]|;<>?`~".ToCharArray()) >= 0) errors.Add($"Sheet prefix '{spec.SheetPrefix}' has characters Revit refuses in sheet numbers (\\ : {{ }} [ ] | ; < > ? ` ~).");
        if (facts.Levels.Count == 0) errors.Add("The model has no levels.");
        if (spec.ViewTemplate is { Length: > 0 } vt && !facts.ViewTemplates.Contains(vt))
            errors.Add($"No view template named '{vt}'" + (facts.ViewTemplates.Count > 0 ? $"; templates: {string.Join(", ", facts.ViewTemplates.Take(15))}." : "; the model has none."));
        if (spec.Views.Contains("structural_plan") && !facts.StructuralPlanType) errors.Add("The model has no structural plan view type; use floor_plan.");
        if (spec.Views.Contains("ceiling_plan") && !facts.CeilingPlanType) errors.Add("The model has no ceiling plan view type.");
        var planKinds = spec.Views.Where(v => v.EndsWith("_plan", StringComparison.Ordinal)).Distinct().ToList();
        plan.Levels = planKinds.Count > 0 || spec.Levels.Count > 0 ? ResolveLevels(spec.Levels, facts.Levels, errors, plan.Warnings) : [];
        if (errors.Count > 0) return plan;

        var names = new HashSet<string>(facts.ViewNames, StringComparer.OrdinalIgnoreCase);
        foreach (var level in plan.Levels)
            foreach (var kind in planKinds)
                plan.Views.Add(new ViewItem($"plan:{level.Name}:{kind}", kind, Unique($"{level.Name} - {PlanLabel(kind)}", names), level.Name));
        for (var i = 0; i < spec.Sections; i++)
        {
            var letter = ((char)('A' + i)).ToString();
            plan.Views.Add(new ViewItem($"section:{letter}", "section", Unique($"Section {letter}-{letter}", names), Index: i));
        }
        if (spec.Views.Contains("elevation"))
        {
            if (plan.Views.All(v => !v.Kind.EndsWith("_plan", StringComparison.Ordinal)))
                plan.Warnings.Add("Elevation markers are placed in the first plan of the package, or the model's first floor plan when the package has none.");
            foreach (var side in new[] { "North", "South", "East", "West" })
                plan.Views.Add(new ViewItem($"elevation:{side}", "elevation", Unique($"{side} Elevation", names), Side: side));
        }
        foreach (var cat in spec.Schedules.Distinct(StringComparer.OrdinalIgnoreCase))
            plan.Views.Add(new ViewItem($"schedule:{cat}", "schedule", Unique($"{Singular(cat)} Schedule", names), Category: cat));
        if (plan.Views.Count == 0) { errors.Add("Nothing to draw: name plans, sections, elevations or schedules."); return plan; }
        if (plan.Views.Count > 400) errors.Add($"{plan.Views.Count} views is more than one package should make (400 at most); split it by levels.");

        // Dimensions: one string per grid direction in each GA or structural plan.
        if (spec.Dimensions)
        {
            var dimmable = plan.Views.Where(v => v.Kind is "floor_plan" or "structural_plan").Select(v => v.Key).ToList();
            if (dimmable.Count > 0 && facts.Grids < 2) plan.Warnings.Add("The model has fewer than two grids, so the plans get no grid dimensions.");
            else plan.DimensionViews = dimmable;
        }
        // Tags: the asked categories in every plan, else the defaults per plan kind.
        foreach (var v in plan.Views.Where(v => v.Kind.EndsWith("_plan", StringComparison.Ordinal)))
            foreach (var cat in spec.Tags ?? DefaultTags(v.Kind))
                plan.Tags.Add(new TagItem(v.Key, cat));

        // Sheets: one per plan, sections and elevations two to a sheet, the schedules on one, then the issue sheet.
        var prefix = spec.SheetPrefix.Length > 0 ? spec.SheetPrefix : spec.Package == "structural_ga" || planKinds.Contains("structural_plan") ? "S-" : "A-";
        var numbers = new HashSet<string>(facts.SheetNumbers, StringComparer.OrdinalIgnoreCase);
        var n = spec.FirstNumber;
        var moved = false;
        string Number()
        {
            var before = n;
            var number = NextNumber(prefix, ref n, numbers);
            if (n - before > 1) moved = true;
            return number;
        }
        foreach (var v in plan.Views.Where(v => v.Kind.EndsWith("_plan", StringComparison.Ordinal)))
            plan.Sheets.Add(new SheetItem(Number(), $"{v.Level} {PlanLabel(v.Kind)}", [v.Key]));
        foreach (var (kind, title) in new[] { ("section", "Sections"), ("elevation", "Elevations") })
        {
            var group = plan.Views.Where(v => v.Kind == kind).ToList();
            for (var i = 0; i < group.Count; i += 2)
                plan.Sheets.Add(new SheetItem(Number(), group.Count > 2 ? $"{title} {i / 2 + 1}" : title, group.Skip(i).Take(2).Select(v => v.Key).ToList()));
        }
        var schedules = plan.Views.Where(v => v.Kind == "schedule").ToList();
        if (schedules.Count > 0) plan.Sheets.Add(new SheetItem(Number(), schedules.Count == 1 ? schedules[0].Name : "Schedules", schedules.Select(v => v.Key).ToList()));
        if (spec.IssueSheet != "none")
        {
            // The register goes just before the first drawing when that number is free, else after the last.
            var at = spec.FirstNumber - 1;
            var first = prefix + at.ToString("000", CultureInfo.InvariantCulture);
            var number = at >= 0 && numbers.Add(first) ? first : Number();
            plan.Sheets.Insert(0, new SheetItem(number, spec.IssueSheet == "transmittal" ? "Transmittal and Drawing Register" : "Drawing Register", [], Register: true));
        }
        if (moved) plan.Warnings.Add($"Some sheet numbers from {prefix}{spec.FirstNumber:000} on are taken already; the package uses the next free ones (existing sheets are not touched).");
        if (plan.Sheets.Count > 300) errors.Add($"{plan.Sheets.Count} sheets is more than one package should make (300 at most); split it by levels.");

        plan.TitleBlock = PickTitleBlock(spec.TitleBlock, spec.SheetSize, facts.TitleBlocks, plan.Warnings, out plan.TitleBlockMissing);
        if (plan.TitleBlockMissing is { } why) plan.Warnings.Add(why + " The run hands this step to you, then makes the sheets.");
        return plan;
    }

    private static string Singular(string category)
    {
        var last = category.Split(' ').Last();
        var one = last switch
        {
            "Columns" => "Column", "Framing" => "Beam", "Foundations" => "Foundation", "Doors" => "Door", "Windows" => "Window",
            "Rooms" => "Room", "Walls" => "Wall", "Floors" => "Floor", _ => last.TrimEnd('s'),
        };
        return one;
    }

    // ---- Sections, checks and time -----------------------------------------------------------------------------------------

    /// <summary>
    /// Section lines across a building's extents (any length unit): the first across the long direction's middle, the
    /// second the other way, more at the quarter points. Each line runs past the extents by <paramref name="margin"/>;
    /// depth is how far it looks (to the far side). The section looks to the left of start to end.
    /// </summary>
    public static List<(double X0, double Y0, double X1, double Y1, double Depth)> SectionLines(double minX, double minY, double maxX, double maxY, int count, double margin)
    {
        var lines = new List<(double, double, double, double, double)>();
        double W = maxX - minX, H = maxY - minY;
        // Axis 0 cuts along x (the line runs east-west, looking north); axis 1 along y (running south, looking east).
        // The first section runs the long way, the second across it, and so on.
        var first = W >= H ? 0 : 1;
        for (var i = 0; i < count; i++)
        {
            var axis = i % 2 == 0 ? first : 1 - first;
            var k = i % 2 == 0 ? (count + 1) / 2 : count / 2;
            var j = i / 2;
            var f = (j + 1.0) / (k + 1.0);
            if (axis == 0)
            {
                var y = minY + f * H;
                lines.Add((minX - margin, y, maxX + margin, y, maxY - y + margin));
            }
            else
            {
                var x = minX + f * W;
                lines.Add((x, maxY + margin, x, minY - margin, maxX - x + margin));
            }
        }
        return lines;
    }

    /// <summary>Pairs of boxes on one sheet that overlap (by more than a hair), by their labels.</summary>
    public static List<(string A, string B)> Overlaps(IReadOnlyList<(string Label, LayoutMath.Rect Box)> boxes, double tolerance = 1e-4)
    {
        var pairs = new List<(string, string)>();
        for (var i = 0; i < boxes.Count; i++)
            for (var j = i + 1; j < boxes.Count; j++)
            {
                var a = boxes[i].Box;
                var b = boxes[j].Box;
                if (a.X0 < b.X1 - tolerance && a.X1 > b.X0 + tolerance && a.Y0 < b.Y1 - tolerance && a.Y1 > b.Y0 + tolerance) pairs.Add((boxes[i].Label, boxes[j].Label));
            }
        return pairs;
    }

    /// <summary>True when the box is not inside the sheet (a view parked beside it because it didn't fit).</summary>
    public static bool OffSheet(LayoutMath.Rect box, LayoutMath.Rect sheet, double tolerance = 1e-3) =>
        box.X0 < sheet.X0 - tolerance || box.Y0 < sheet.Y0 - tolerance || box.X1 > sheet.X1 + tolerance || box.Y1 > sheet.Y1 + tolerance;

    /// <summary>Minutes the work of a stage would take by hand with one tool's rate: per-item tools once plus per item, per-run tools per item.</summary>
    public static double ByHandMinutes(string tool, int items, IEnumerable<TimeRate>? overrides)
    {
        if (items <= 0) return 0;
        var rate = TimeMath.RateFor(tool, true, overrides);
        var minutes = rate.PerItem > 0 ? rate.PerRun + rate.PerItem * items : rate.PerRun * items;
        return Math.Min(TimeMath.MaxEstimateMinutes, minutes);
    }

    /// <summary>The plan's by-hand estimate before anything runs (tags and dimensions per view, not per tag).</summary>
    public static double PlannedByHandMinutes(Plan plan, IEnumerable<TimeRate>? overrides) =>
        ByHandMinutes("create_plan_views", plan.Views.Count(v => v.Kind.EndsWith("_plan", StringComparison.Ordinal)), overrides)
        + ByHandMinutes("create_section", plan.Count("section"), overrides)
        + ByHandMinutes("create_elevations", plan.Count("elevation"), overrides)
        + ByHandMinutes("create_schedule", plan.Count("schedule"), overrides)
        + ByHandMinutes("dimension_grids", plan.DimensionViews.Count * 2, overrides)
        + ByHandMinutes("tag_elements_in_view", plan.Tags.Count * 10, overrides)
        + ByHandMinutes("create_sheets", plan.Sheets.Count, overrides)
        + ByHandMinutes("place_views_on_sheet", plan.Views.Count, overrides)
        + (plan.Register is null ? 0 : ByHandMinutes("create_sheet_list", 1, overrides) + ByHandMinutes("export_drawing_list", 1, overrides));
}
