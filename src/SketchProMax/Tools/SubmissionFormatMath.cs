using System.Text.RegularExpressions;

namespace SketchProMax.Tools;

/// <summary>
/// The format of a berth's drawing submission, built in (the owner, 2026-11-17: "this is the format we always want, I
/// need it built in"), read from the final design submissions already issued: which sheets a berth gets, how they are
/// numbered and named, which views sit on each with which title and scale, which schedules, and what the office's own
/// QC sheet checks before issue. <see cref="Check"/> compares a model's sheets with it, so a submission is planned from
/// this list and not from zero, and "done" means every line here holds. No project names or codes: the sheet number's
/// prefix is read from the model. No Revit here (the tests compile it).
/// </summary>
internal static class SubmissionFormat
{
    /// <summary>One view a sheet of a kind carries: what its title says, its scales, and whether the sheet must have it.</summary>
    internal sealed record ViewRule(string Says, string Pattern, int[] Scales, bool Required = true);

    /// <summary>One kind of sheet in a berth's set.</summary>
    internal sealed record Kind(string Key, int From, int To, string Title, string NamePattern, string Scale, bool Required,
        ViewRule[] Views, string[] Schedules, string[] Shows);

    public static readonly Kind[] Kinds =
    [
        new("layout", 100, 100, "OVERALL LAYOUT", @"^OVERALL LAYOUT$", "1:300", true,
            [new("BERTH <n> OVERALL PLAN", @"OVERALL PLAN", [300])],
            ["FURNITURE SCHEDULE"],
            ["the whole berth with its parts named (PART 1, PART 2, ...) and matchlines inside the crop", "the quay wall furniture schedule (fenders, bollards, ladders, mooring rings)"]),
        new("piles", 101, 109, "PILES PLAN - PART <k>", @"^PILES PLAN - PART \d+$", "1:100", true,
            [new("BERTH <n> - PART <k> PILES PLAN", @"PART \d+ PILES PLAN", [100])],
            ["PILES SCHEDULE"],
            ["every pile tagged with its type mark, filled with its type's colour", "grid bubbles and the dimensions between grids, both ways",
             "the piles schedule (type mark, description, top level, bottom level, length, count for the berth) with the colour key, and the sheet pile wall schedule where the berth has one",
             "the neighbouring berth hatched and named at the matchline"]),
        new("framing", 111, 119, "FRAMING PLAN - PART <k>", @"^FRAMING PLAN - PART \d+$", "1:100", true,
            [new("BERTH <n> - PART <k> FRAMING PLAN", @"PART \d+ FRAMING PLAN", [100]), new("BERTH <n> - PART <k> ELEVATION", @"PART \d+ ELEVATION", [100])],
            [],
            ["the framing plan above and the front elevation of the same part below it", "beams and slab thickenings named with their sizes, expansion joints named",
             "slab thickenings, pits and approach slab hatched, with the hatch key in the legend", "fenders, bollards, ladders and mooring rings named; levels in the elevation",
             "REFERENCE DRAWINGS listing only drawings that exist"]),
        new("section", 301, 399, "DESIGN SECTION <s> CROSS SECTION", @"^DESIGN SECTION \w+ CROSS SECTION$", "1:75", true,
            [new("BERTH <n> - DESIGN SECTION <s> CROSS SECTION", @"DESIGN SECTION \w+ CROSS SECTION", [75])],
            [],
            ["one sheet per design section of the berth", "grids with the dimensions between them and the overall width", "every pile named with its type mark and diameter, levels at top and toe",
             "front and rear capping beams, deck and approach slab named with sizes", "water levels, dredge level and scour protection, with the tidal bar in the legend"]),
        new("slope", 301, 399, "LONGITUDINAL SLOPE SECTIONS <i>", @"^LONGITUDINAL SLOPE SECTIONS \d+$", "1:75", false,
            [new("BERTH <n> - LONGITUDINAL SLOPE - ...", @"LONGITUDINAL SLOPE", [75])],
            [],
            ["only where the berth's deck slopes along its length: the sections along the front beam and each pile row"]),
        new("piledetails", 401, 409, "PILES DETAILS - <i>", @"^PILES DETAILS - \d+$", "AS SHOWN", true,
            [new("BERTH <n> - <PILE> - <DS> - SECTION A, B, ...", @" - SECTION [A-Z]$", [25]), new("BERTH <n> - <PILE> - DESIGN SECTION <s>", @"DESIGN SECTION \w+", [50])],
            [],
            ["for every pile type of every design section: its elevation with the reinforcement, and a cross section at each change of reinforcement",
             "section marks on the elevation that match the section titles", "bars, spirals and spacers named; the pile diameter dimensioned",
             "the same title wording for the same pile in every berth"]),
    ];

    /// <summary>What every sheet's title block carries, and what the office's QC sheet checks on every sheet before issue.</summary>
    public static readonly string[] EverySheet =
    [
        "Sheet title in three lines: STRUCTURAL / BERTH <n> / the sheet's name; the sheet number with the revision after it",
        "Notes legend of this berth's notes, key plan with only this berth highlighted, legend, tidal bar",
        "Stage and month (FINAL DESIGN, the month of issue), revision row: Rev, phase, date, ISSUED FOR APPROVAL, approver",
        "The same issue date, in the same form, on every sheet of the submission",
        "Matchlines stop inside the view crop",
        "Every drawing number cited in a note is a sheet of the submission",
        "No dimension shows a typed value over what the model measures",
        "No empty tag, no section mark pointing to a view that is on no sheet",
    ];

    /// <summary>How the files go out.</summary>
    public static readonly string[] Files =
    [
        "One PDF and one DWG per sheet, named <sheet number>-<revision, two digits>",
        "List of Drawings (Excel): DRAWING NO., Title, Revision, Scale, DATE; general details first, then each berth under its own heading",
    ];

    // ---- Sheet numbers -------------------------------------------------------------------------------------------------

    private static readonly Regex NumberRule = new(@"^(?<prefix>.*?)-(?<berth>\d{2})-(?<disc>[A-Z]{2})-(?<series>\d{3})(-(?<rev>\d{1,2}))?$", RegexOptions.CultureInvariant);

    /// <summary>
    /// A sheet number of the form &lt;prefix&gt;-&lt;berth, two digits&gt;-&lt;discipline&gt;-&lt;series, three digits&gt;[-revision]:
    /// its berth (0 for the general sheets) and series. Null when the number isn't of this form.
    /// </summary>
    public static (int Berth, int Series, string Prefix, string Discipline)? Parse(string number)
    {
        var m = NumberRule.Match((number ?? "").Trim().ToUpperInvariant());
        return m.Success ? (int.Parse(m.Groups["berth"].Value), int.Parse(m.Groups["series"].Value), m.Groups["prefix"].Value, m.Groups["disc"].Value) : null;
    }

    /// <summary>The kind of a berth's sheet by its series and name; null when the format has no such sheet.</summary>
    public static Kind? KindOf(int series, string name)
    {
        var inRange = Kinds.Where(k => series >= k.From && series <= k.To).ToList();
        return inRange.FirstOrDefault(k => Regex.IsMatch(Clean(name), k.NamePattern, RegexOptions.IgnoreCase)) ?? (inRange.Count == 1 ? inRange[0] : null);
    }

    private static string Clean(string text) => Regex.Replace(text ?? "", @"\s+", " ").Trim();

    // ---- Showing -------------------------------------------------------------------------------------------------------

    /// <summary>The format in plain lines, for berth <paramref name="berth"/> (0: any berth).</summary>
    public static List<string> Show(int berth = 0)
    {
        var n = berth > 0 ? berth.ToString() : "<n>";
        var lines = new List<string> { "Sheet number: <project prefix>-<berth, two digits>-<discipline>-<series>, general sheets under berth 00." };
        foreach (var k in Kinds)
        {
            var series = k.From == k.To ? k.From.ToString() : k.Key == "piles" || k.Key == "framing" ? $"{k.From}, {k.From + 1}, ... (one per part)" : $"{k.From}, {k.From + 1}, ...";
            lines.Add($"{series}  {k.Title}  ({k.Scale}{(k.Required ? "" : ", when needed")})");
            foreach (var v in k.Views) lines.Add($"    view: {v.Says.Replace("<n>", n)}  at 1:{string.Join(" or 1:", v.Scales)}");
            foreach (var s in k.Schedules) lines.Add($"    schedule: BERTH {n} - {s}");
            foreach (var s in k.Shows) lines.Add($"    shows: {s}");
        }
        lines.Add("Every sheet:");
        lines.AddRange(EverySheet.Select(s => "    " + s.Replace("<n>", n)));
        lines.Add("Files:");
        lines.AddRange(Files.Select(s => "    " + s));
        return lines;
    }

    // ---- Checking ------------------------------------------------------------------------------------------------------

    /// <summary>One sheet as read from the model.</summary>
    internal sealed class Sheet
    {
        public string Number = "", Name = "", IssueDate = "";
        public List<(string Title, int Scale)> Views = [];
        public List<string> Schedules = [];
        /// <summary>Drawing numbers cited in the notes on the sheet and its views.</summary>
        public List<string> Cited = [];
        /// <summary>Dimensions whose shown value was typed over.</summary>
        public int TypedDimensions;
    }

    internal sealed record Issue(string Sheet, string Check, string Problem, string Fix);

    /// <summary>
    /// Compares the sheets of <paramref name="berths"/> (every berth found when empty) with the format: sheets missing from
    /// a berth's set, names and view titles that differ, wrong scales, missing schedules, empty sheets, a different issue
    /// date, drawings cited that aren't sheets, typed-over dimensions, and pile details missing for a design section.
    /// </summary>
    public static List<Issue> Check(IReadOnlyList<Sheet> all, IEnumerable<int>? berths = null)
    {
        var issues = new List<Issue>();
        var parsed = all.Select(s => (Sheet: s, At: Parse(s.Number))).Where(p => p.At is not null).Select(p => (p.Sheet, At: p.At!.Value)).ToList();
        var wanted = (berths ?? []).Where(b => b > 0).Distinct().OrderBy(b => b).ToList();
        if (wanted.Count == 0) wanted = parsed.Select(p => p.At.Berth).Where(b => b > 0).Distinct().OrderBy(b => b).ToList();
        var numbers = new HashSet<string>(all.Select(s => Bare(s.Number)), StringComparer.OrdinalIgnoreCase);
        var inScope = parsed.Where(p => p.At.Berth == 0 || wanted.Contains(p.At.Berth)).ToList();

        foreach (var berth in wanted)
        {
            var mine = parsed.Where(p => p.At.Berth == berth).ToList();
            var label = $"Berth {berth}";
            if (mine.Count == 0) { issues.Add(new(label, "Sheets", "The berth has no sheets numbered in the format.", "Make its set: " + string.Join(", ", Kinds.Where(k => k.Required).Select(k => k.Title)) + ".")); continue; }
            var byKind = mine.Select(p => (p.Sheet, p.At, Kind: KindOf(p.At.Series, p.Sheet.Name))).ToList();
            foreach (var k in Kinds.Where(k => k.Required))
                if (!byKind.Any(p => p.Kind == k))
                    issues.Add(new(label, "Sheets", $"No sheet \"{k.Title}\" (series {k.From}{(k.From == k.To ? "" : "+")}).", $"Add it, with: {string.Join("; ", k.Views.Select(v => v.Says.Replace("<n>", berth.ToString())))}."));
            // A part has both its piles plan and its framing plan.
            var pileParts = byKind.Where(p => p.Kind?.Key == "piles").Select(p => p.At.Series % 10).ToHashSet();
            var frameParts = byKind.Where(p => p.Kind?.Key == "framing").Select(p => p.At.Series % 10).ToHashSet();
            foreach (var part in pileParts.Except(frameParts).OrderBy(x => x)) issues.Add(new(label, "Sheets", $"Part {part} has a piles plan and no framing plan.", $"Add FRAMING PLAN - PART {part} (series {110 + part})."));
            foreach (var part in frameParts.Except(pileParts).OrderBy(x => x)) issues.Add(new(label, "Sheets", $"Part {part} has a framing plan and no piles plan.", $"Add PILES PLAN - PART {part} (series {100 + part})."));
            // Every design section drawn has its pile details.
            var sections = byKind.Where(p => p.Kind?.Key == "section").Select(p => Regex.Match(Clean(p.Sheet.Name), @"DESIGN SECTION (\w+)", RegexOptions.IgnoreCase).Groups[1].Value)
                .Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var detailTitles = byKind.Where(p => p.Kind?.Key == "piledetails").SelectMany(p => p.Sheet.Views.Select(v => v.Title)).ToList();
            foreach (var s in sections)
                if (detailTitles.Count > 0 && !detailTitles.Any(t => NamesSection(t, s)))
                    issues.Add(new(label, "Completeness", $"Design section {s} has a cross section and no pile details.", $"Add the pile details of every pile type of design section {s}."));

            foreach (var (sheet, at, kind) in byKind)
            {
                var name = Bare(sheet.Number);
                if (kind is null) { issues.Add(new(name, "Names", $"\"{Clean(sheet.Name)}\" is not a sheet of the format for series {at.Series}.", "Rename or renumber it, or confirm it is an extra sheet.")); continue; }
                if (!Regex.IsMatch(Clean(sheet.Name), kind.NamePattern, RegexOptions.IgnoreCase))
                    issues.Add(new(name, "Names", $"Sheet name \"{Clean(sheet.Name)}\" differs from the format.", $"Name it \"{kind.Title}\"."));
                else if (kind.Key is "piles" or "framing" && Regex.Match(sheet.Name, @"PART (\d+)", RegexOptions.IgnoreCase) is { Success: true } part && int.Parse(part.Groups[1].Value) != at.Series % 10)
                    issues.Add(new(name, "Names", $"The name says part {part.Groups[1].Value} and the number says part {at.Series % 10}.", "Make the name and the number agree."));
                if (sheet.Views.Count == 0) { issues.Add(new(name, "Views", "The sheet has no view.", "Place its views: " + string.Join("; ", kind.Views.Select(v => v.Says.Replace("<n>", berth.ToString()))) + ".")); continue; }
                foreach (var rule in kind.Views)
                {
                    var matches = sheet.Views.Where(v => Regex.IsMatch(Clean(v.Title), rule.Pattern, RegexOptions.IgnoreCase)).ToList();
                    if (matches.Count == 0)
                    {
                        if (rule.Required) issues.Add(new(name, "Views", $"No view titled like \"{rule.Says.Replace("<n>", berth.ToString())}\".", "Place it, or set the view's title on sheet."));
                        continue;
                    }
                    foreach (var v in matches)
                    {
                        if (!rule.Scales.Contains(v.Scale))
                            issues.Add(new(name, "Scale", $"\"{Clean(v.Title)}\" is at 1:{v.Scale}.", $"Set it to 1:{string.Join(" or 1:", rule.Scales)}."));
                        if (Regex.Match(v.Title, @"BERTH (\d+)", RegexOptions.IgnoreCase) is { Success: true } b && int.Parse(b.Groups[1].Value) != berth)
                            issues.Add(new(name, "Names", $"View title \"{Clean(v.Title)}\" names another berth.", $"It is on a Berth {berth} sheet: correct the title or the view."));
                        else if (!Regex.IsMatch(v.Title, @"BERTH \d+", RegexOptions.IgnoreCase))
                            issues.Add(new(name, "Names", $"View title \"{Clean(v.Title)}\" doesn't start with the berth.", $"Start it with \"BERTH {berth}\"."));
                    }
                }
                foreach (var schedule in kind.Schedules)
                    if (!sheet.Schedules.Any(s => s.IndexOf(schedule, StringComparison.OrdinalIgnoreCase) >= 0))
                        issues.Add(new(name, "Schedules", $"No \"BERTH {berth} - {schedule}\" on the sheet.", "Place the schedule on the sheet."));
            }
        }

        // What the QC sheet checks on every sheet of the submission.
        var dates = inScope.Select(p => p.Sheet.IssueDate.Trim()).Where(d => d.Length > 0).GroupBy(d => d).OrderByDescending(g => g.Count()).ToList();
        if (dates.Count > 1)
            foreach (var (sheet, _) in inScope.Where(p => p.Sheet.IssueDate.Trim().Length > 0 && p.Sheet.IssueDate.Trim() != dates[0].Key))
                issues.Add(new(Bare(sheet.Number), "Title block", $"Issue date \"{sheet.IssueDate.Trim()}\"; the other sheets show \"{dates[0].Key}\".", $"Set it to {dates[0].Key}."));
        foreach (var (sheet, _) in inScope)
        {
            foreach (var cited in sheet.Cited.Select(Bare).Distinct(StringComparer.OrdinalIgnoreCase).Where(c => !numbers.Contains(c)))
                issues.Add(new(Bare(sheet.Number), "Cross-references", $"A note cites {cited}, which is not a sheet in the model.", "Correct the number, or remove the line if that drawing is not part of the submission."));
            if (sheet.TypedDimensions > 0)
                issues.Add(new(Bare(sheet.Number), "Dimensions", $"{sheet.TypedDimensions} dimension(s) show a typed value, not what the model measures.", "Remove the typed value, and move the element if the model is off."));
        }
        return issues;
    }

    /// <summary>A sheet number without its revision ("...-SC-101-0" is "...-SC-101").</summary>
    public static string Bare(string number)
    {
        var n = (number ?? "").Trim().ToUpperInvariant();
        return Regex.Replace(n, @"(-\d{3})-\d{1,2}$", "$1");
    }

    /// <summary>The drawing numbers a note cites: anything shaped like a sheet number of the format.</summary>
    public static List<string> CitedIn(string text) =>
        Regex.Matches((text ?? "").ToUpperInvariant(), @"[A-Z0-9]+(?:-[A-Z0-9]+)+-\d{2}-[A-Z]{2}-\d{3}(?:-\d{1,2})?").Cast<Match>().Select(m => Bare(m.Value)).Distinct().ToList();

    /// <summary>True when a pile-detail view title is for design section <paramref name="section"/> ("1a": "DS01a" or "DESIGN SECTION 1a").</summary>
    private static bool NamesSection(string title, string section)
    {
        var t = Clean(title);
        return Regex.IsMatch(t, @"DESIGN SECTION " + Regex.Escape(section) + @"\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(t, @"DS0*" + Regex.Escape(section) + @"\b", RegexOptions.IgnoreCase);
    }

    /// <summary>Per sheet, the checks that found something: the office's QC matrix (a sheet with none is OK).</summary>
    public static Dictionary<string, List<string>> Matrix(IEnumerable<Issue> issues) =>
        issues.GroupBy(i => i.Sheet).ToDictionary(g => g.Key, g => g.Select(i => i.Check).Distinct().ToList());
}
