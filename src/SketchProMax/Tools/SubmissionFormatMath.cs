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
    /// <summary>
    /// One view a sheet of a kind carries: what its title says, its usual scale, and whether the sheet must have it. The
    /// scale is not fixed (the owner, 2026-11-17: a long berth or a wide section takes another scale so it fits the sheet):
    /// any scale of <see cref="StandardScales"/> is right, as long as the same views of a berth share it.
    /// </summary>
    internal sealed record ViewRule(string Says, string Pattern, int[] Scales, bool Required = true);

    /// <summary>One kind of sheet in a berth's set.</summary>
    internal sealed record Kind(string Key, int From, int To, string Title, string NamePattern, string Scale, bool Required,
        ViewRule[] Views, string[] Schedules, string[] Shows);

    public static readonly Kind[] Kinds =
    [
        new("layout", 100, 100, "OVERALL LAYOUT", @"^OVERALL LAYOUT$", "usually 1:300", true,
            [new("BERTH <n> OVERALL PLAN", @"OVERALL PLAN", [300])],
            ["FURNITURE SCHEDULE"],
            ["the whole berth with its parts named (PART 1, PART 2, ...) and matchlines (drawn longer than the part is fine)", "the quay wall furniture schedule (fenders, bollards, ladders, mooring rings)"]),
        new("piles", 101, 109, "PILES PLAN - PART <k>", @"^PILES PLAN - PART \d+$", "usually 1:100", true,
            [new("BERTH <n> - PART <k> PILES PLAN", @"PART \d+ PILES PLAN", [100])],
            ["PILES SCHEDULE"],
            ["every pile inside the matchlines tagged with its type mark, filled with its type's colour; beyond a matchline the piles show with their tags hidden", "grid bubbles and the dimensions between grids, both ways",
             "the piles schedule (type mark, description, top level, bottom level, length, count for the berth) with the colour key, and the sheet pile wall schedule where the berth has one",
             "the neighbouring berth hatched and named at the matchline"]),
        new("framing", 111, 119, "FRAMING PLAN - PART <k>", @"^FRAMING PLAN - PART \d+$", "usually 1:100", true,
            [new("BERTH <n> - PART <k> FRAMING PLAN", @"PART \d+ FRAMING PLAN", [100]), new("BERTH <n> - PART <k> ELEVATION", @"PART \d+ ELEVATION", [100])],
            [],
            ["the framing plan above and the front elevation of the same part below it", "beams and slab thickenings named with their sizes, expansion joints named",
             "slab thickenings, pits and approach slab hatched, with the hatch key in the legend", "fenders, bollards, ladders and mooring rings named; levels in the elevation",
             "REFERENCE DRAWINGS listing only drawings that exist"]),
        new("section", 301, 399, "DESIGN SECTION <s> CROSS SECTION", @"^DESIGN SECTION \w+ CROSS SECTION$", "usually 1:100", true,
            [new("BERTH <n> - DESIGN SECTION <s> CROSS SECTION", @"DESIGN SECTION \w+ CROSS SECTION", [100]),
             new("BERTH <n> - TYPICAL CROSS SECTION AT FENDER AND BOLLARD LOCATION", @"TYPICAL CROSS SECTION", [100], false)],
            [],
            ["one sheet per design section of the berth", "grids with the dimensions between them and the overall width", "every pile named with its type mark and diameter, levels at top and toe",
             "front and rear capping beams, deck and approach slab named with sizes", "water levels, dredge level and scour protection, with the tidal bar in the legend"]),
        new("slope", 301, 399, "LONGITUDINAL SLOPE SECTIONS <i>", @"^LONGITUDINAL SLOPE SECTIONS \d+$", "usually 1:100", false,
            [new("BERTH <n> - LONGITUDINAL SLOPE - ...", @"LONGITUDINAL SLOPE", [100])],
            [],
            ["only where the berth's deck slopes along its length: the sections along the front beam and each pile row"]),
        new("cross", 301, 399, "CROSS SECTIONS - <i>", @"^CROSS SECTIONS - \d+$", "usually 1:100", false,
            [new("BERTH <n> - SECTION <j>", @"BERTH \d+ - SECTION \w+$", [100])],
            [],
            ["only where the berth needs more cross sections than its design sections: several on a sheet, numbered in order"]),
        new("piledetails", 401, 409, "PILES DETAILS - <i>", @"^PILES DETAILS - \d+$", "usually sections 1:25, elevations 1:50", true,
            [new("BERTH <n> - <PILE> - <DS> - SECTION A, B, ...", @" - SECTION [A-Z]$", [25]), new("BERTH <n> - <PILE> - DESIGN SECTION <s>", @"DESIGN SECTION \w+", [50])],
            [],
            ["for every pile type of every design section: its elevation with the reinforcement, and a cross section at each change of reinforcement",
             "section marks on the elevation that match the section titles", "bars, spirals and spacers named; the pile diameter dimensioned",
             "the same title wording for the same pile in every berth"]),
    ];

    /// <summary>The scales a drawing may use. A view takes its usual scale, or the next one of these that fits it on the sheet.</summary>
    public static readonly int[] StandardScales = [5, 10, 20, 25, 50, 75, 100, 125, 150, 200, 250, 300, 400, 500, 750, 1000, 1250, 1500, 2000, 2500, 3000, 5000];

    /// <summary>What every sheet's title block carries, and what the office's QC sheet checks on every sheet before issue.</summary>
    public static readonly string[] EverySheet =
    [
        "Sheet title in three lines: STRUCTURAL / BERTH <n> / the sheet's name; the sheet number with the revision after it",
        "Notes legend of this berth's notes, key plan with only this berth highlighted, legend, tidal bar",
        "Stage and month (FINAL DESIGN, the month of issue), revision row: Rev, phase, date, ISSUED FOR APPROVAL, approver",
        "The issue date written in the same form on every sheet (the date itself is later on sheets added in a later revision)",
        "Matchlines stop inside the view crop",
        "Every drawing number cited in a note is a sheet of the submission",
        "No dimension shows a typed value over what the model measures",
        "No empty tag, no tag on top of another, no section mark pointing to a view that is on no sheet",
        "Every view lies inside the sheet's frame; a view too long for it takes the next standard scale, the same for all parts",
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
        var lines = new List<string>
        {
            "Sheet number: <project prefix>-<berth, two digits>-<discipline>-<series>, general sheets under berth 00.",
            "In the names below, <k> is the part's number, <s> the design section's number (1, 2a, ...), <i> and <j> count from 1.",
            "Scales: each view has a usual scale. When the view is too long or wide for the sheet at that scale, use the nearest standard scale that fits (1:75, 1:100, 1:150, 1:200, 1:250, 1:300, 1:500), and keep one scale for the same views of a berth (all its parts, all its sections).",
        };
        foreach (var k in Kinds)
        {
            var series = k.From == k.To ? k.From.ToString() : k.Key == "piles" || k.Key == "framing" ? $"{k.From}, {k.From + 1}, ... (one per part)" : $"{k.From}, {k.From + 1}, ...";
            lines.Add($"{series}  {k.Title}  ({k.Scale}{(k.Required ? "" : ", when needed")})");
            foreach (var v in k.Views) lines.Add($"    view: {v.Says.Replace("<n>", n)}  usually 1:{v.Scales[0]}{(v.Required ? "" : "  (when needed)")}");
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
        public List<PlacedView> Views = [];
        /// <summary>True when a key plan was found (a view on the sheet named KEY PLAN, or yes/no berth switches in the title block).</summary>
        public bool KeyPlanFound;
        /// <summary>What the key plan highlights, as far as the model says: a whole berth (part 0) or one part of it ("Berth 6 - Part 2").</summary>
        public List<(int Berth, int Part)> KeyPlanOn = [];
        /// <summary>True when the key plan is yes/no berth switches in the title block (the owner, 2026-11-17: that is how it is made).</summary>
        public bool KeyPlanSwitches;
        /// <summary>True when the title block has switches for the parts of a berth ("Berth 6 - Part 2"), not only for whole berths.</summary>
        public bool KeyPlanParts;
        /// <summary>Yes/no parameters of the title block whose names name no berth: listed when no berth switch was found, so the names can be taught.</summary>
        public List<string> OtherSwitches = [];
        public List<string> Schedules = [];
        /// <summary>Drawing numbers cited in the notes on the sheet and its views.</summary>
        public List<string> Cited = [];
        /// <summary>Dimensions whose shown value was typed over.</summary>
        public int TypedDimensions;
    }

    /// <summary>One view on a sheet: its title on the sheet, its scale, and what was read in it.</summary>
    internal sealed class PlacedView
    {
        public string Title = "";
        public int Scale;
        /// <summary>Tags with no text; tags lying on another tag; matchlines that run past the crop.</summary>
        public int EmptyTags, OverlappingTags, MatchlinesPastCrop;
        /// <summary>The view's box runs outside the sheet's frame.</summary>
        public bool OffSheet;
        /// <summary>With <see cref="OffSheet"/>: the view's own crop is inside the sheet, only something drawn around it (a level head, a tag, a note) runs past the paper.</summary>
        public bool OffSheetAroundOnly;
        /// <summary>Its tags and matchlines were not read in this run (the time for drawing views ran out): the next run reads them.</summary>
        public bool NotJudged;
        /// <summary>
        /// Of the categories the view tags (the owner's rule, 2026-11-17: inside the matchlines every pile is tagged; outside
        /// them the tag is hidden): how many elements lie inside the view's matchlines, how many of those have no tag, and
        /// how many tags sit on elements beyond a matchline. A view with no matchline is all inside.
        /// </summary>
        public int Taggable, UntaggedInside, TaggedOutside;
        public static implicit operator PlacedView((string Title, int Scale) v) => new() { Title = v.Title, Scale = v.Scale };
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
                    issues.Add(new(label, "Sheets", $"No sheet \"{k.Title}\" (series {k.From}{(k.From == k.To ? "" : "+")}).", $"Add it, with: {string.Join("; ", k.Views.Where(v => v.Required).Select(v => v.Says.Replace("<n>", berth.ToString())))}."));
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

            var scales = new List<(string Sheet, string Group, ViewRule Rule, PlacedView View)>();
            foreach (var (sheet, at, kind) in byKind)
            {
                var name = Bare(sheet.Number);
                if (kind is null) { issues.Add(new(name, "Names", $"\"{Clean(sheet.Name)}\" is not a sheet of the format for series {at.Series}.", "Rename or renumber it, or confirm it is an extra sheet.")); continue; }
                if (!Regex.IsMatch(Clean(sheet.Name), kind.NamePattern, RegexOptions.IgnoreCase))
                    issues.Add(new(name, "Names", $"Sheet name \"{Clean(sheet.Name)}\" differs from the format.", $"Name it \"{kind.Title}\"."));
                else if (kind.Key is "piles" or "framing" && Regex.Match(sheet.Name, @"PART (\d+)", RegexOptions.IgnoreCase) is { Success: true } part && int.Parse(part.Groups[1].Value) != at.Series % 10)
                    issues.Add(new(name, "Names", $"The name says part {part.Groups[1].Value} and the number says part {at.Series % 10}.", "Make the name and the number agree."));
                if (sheet.Views.Count == 0) { issues.Add(new(name, "Views", "The sheet has no view.", "Place its views: " + string.Join("; ", kind.Views.Where(v => v.Required).Select(v => v.Says.Replace("<n>", berth.ToString()))) + ".")); continue; }
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
                        if (!StandardScales.Contains(v.Scale))
                            issues.Add(new(name, "Scale", $"\"{Clean(v.Title)}\" is at 1:{v.Scale}, which is not a standard scale.", $"Set it to 1:{rule.Scales[0]}, or the nearest standard scale that fits the sheet."));
                        scales.Add((name, kind.Key + "|" + rule.Pattern, rule, v));
                        if (kind.Key == "piles")
                        {
                            if (v.UntaggedInside > 0)
                                issues.Add(new(name, "Tags", $"{v.UntaggedInside} of {v.Taggable} piles inside the matchlines have no tag in \"{Clean(v.Title)}\".", "Tag them with the tag the others have (tag_elements_in_view)."));
                            if (v.TaggedOutside > 0)
                                issues.Add(new(name, "Tags", $"{v.TaggedOutside} tag(s) are on piles beyond the matchline in \"{Clean(v.Title)}\".", "Hide those tags: a pile beyond the matchline is tagged on its own part's plan."));
                        }
                        if (Regex.Match(v.Title, @"BERTH (\d+)", RegexOptions.IgnoreCase) is { Success: true } b && int.Parse(b.Groups[1].Value) != berth)
                            issues.Add(new(name, "Names", $"View title \"{Clean(v.Title)}\" names another berth.", $"It is on a Berth {berth} sheet: correct the title or the view."));
                        else if (!Regex.IsMatch(v.Title, @"BERTH \d+", RegexOptions.IgnoreCase))
                            issues.Add(new(name, "Names", $"View title \"{Clean(v.Title)}\" doesn't start with the berth.", $"Start it with \"BERTH {berth}\"."));
                    }
                }
                foreach (var schedule in kind.Schedules)
                    if (!sheet.Schedules.Any(s => s.IndexOf(schedule, StringComparison.OrdinalIgnoreCase) >= 0))
                        issues.Add(new(name, "Schedules", $"No \"BERTH {berth} - {schedule}\" on the sheet.", "Place the schedule on the sheet."));
                // The key plan: only this berth is on, and on a part's sheet only that part.
                static string Says((int Berth, int Part) k) => k.Part > 0 ? $"Berth {k.Berth} - Part {k.Part}" : $"Berth {k.Berth}";
                var on = sheet.KeyPlanOn.Distinct().OrderBy(k => k.Berth).ThenBy(k => k.Part).ToList();
                var partOf = kind.Key is "piles" or "framing" ? at.Series % 10 : 0;
                if (sheet.KeyPlanSwitches && on.Count == 0)
                    issues.Add(new(name, "Key plan", "The key plan highlights nothing: every berth switch of the title block is off.", $"Switch on {Says((berth, partOf))} in the sheet's title block."));
                else if (sheet.KeyPlanFound && on.Count > 0)
                {
                    var wrong = on.Where(k => k.Berth != berth || (partOf > 0 && k.Part > 0 && k.Part != partOf) || (partOf == 0 && k.Part > 0)).ToList();
                    // A part's sheet shows its part, where the title block has part switches at all.
                    var missingPart = partOf > 0 && wrong.Count == 0 && on.All(k => k.Part == 0) && sheet.KeyPlanParts;
                    if (wrong.Count > 0 || missingPart)
                        issues.Add(new(name, "Key plan", $"The key plan highlights {(on.Count > 6 ? $"{on.Count} berths and parts ({string.Join(", ", on.Take(3).Select(Says))}, ...)" : string.Join(", ", on.Select(Says)))}.", $"In the sheet's title block switch on only {Says((berth, partOf))}."));
                }
            }
            // The same views of a berth share one scale: all its parts, all its sections.
            foreach (var group in scales.Where(x => StandardScales.Contains(x.View.Scale)).GroupBy(x => x.Group))
            {
                var by = group.GroupBy(x => x.View.Scale).OrderByDescending(g => g.Count()).ThenBy(g => Math.Abs(g.Key - group.First().Rule.Scales[0])).ToList();
                if (by.Count < 2) continue;
                foreach (var x in by.Skip(1).SelectMany(g => g))
                    issues.Add(new(x.Sheet, "Scale", $"\"{Clean(x.View.Title)}\" is at 1:{x.View.Scale}; the berth's other views of this kind are at 1:{by[0].Key}.", $"Use one scale for all of them: the one that fits the longest."));
            }
        }

        // What the QC sheet checks on every sheet of the submission.
        // Sheets added in a later revision carry a later date, so the date itself may differ; the way it is written may not.
        var forms = inScope.Select(p => p.Sheet.IssueDate.Trim()).Where(d => d.Length > 0).GroupBy(DateForm).OrderByDescending(g => g.Count()).ToList();
        if (forms.Count > 1)
            foreach (var (sheet, _) in inScope.Where(p => p.Sheet.IssueDate.Trim().Length > 0 && DateForm(p.Sheet.IssueDate.Trim()) != forms[0].Key))
                issues.Add(new(Bare(sheet.Number), "Title block", $"Issue date \"{sheet.IssueDate.Trim()}\" is written another way than on the other sheets (\"{forms[0].First()}\").", $"Write it like {forms[0].First()}."));
        foreach (var (sheet, _) in inScope)
        {
            foreach (var cited in sheet.Cited.Select(Bare).Distinct(StringComparer.OrdinalIgnoreCase).Where(c => !numbers.Contains(c)))
                issues.Add(new(Bare(sheet.Number), "Cross-references", $"A note cites {cited}, which is not a sheet in the model.", "Correct the number, or remove the line if that drawing is not part of the submission."));
            foreach (var v in sheet.Views)
            {
                var title = Clean(v.Title);
                if (v.OffSheet && v.OffSheetAroundOnly) issues.Add(new(Bare(sheet.Number), "Fit", $"Something drawn around \"{title}\" (a level head, a grid head, a tag or a note) runs past the paper; the view itself is inside.", "Pull it back inside or hide it, or move the view."));
                else if (v.OffSheet) issues.Add(new(Bare(sheet.Number), "Fit", $"\"{title}\" runs outside the sheet's frame.", "Move it inside, tighten its crop, or take the next standard scale (the same for the berth's other views of this kind)."));
                if (v.EmptyTags > 0) issues.Add(new(Bare(sheet.Number), "Tags", $"{v.EmptyTags} empty tag(s) in \"{title}\".", "Fill the value the tag reads (type mark, mark), or delete the tag."));
                if (v.OverlappingTags > 0) issues.Add(new(Bare(sheet.Number), "Tags", $"{v.OverlappingTags} tag(s) lie on another tag in \"{title}\".", "arrange_tags action=fix_overlaps, then look at the view."));
                // A matchline running past the crop is not reported (the owner, 2026-11-18: matchlines are typically drawn
                // longer than each part, and that is fine). The count is still read for whoever looks.
            }
            if (sheet.TypedDimensions > 0)
                issues.Add(new(Bare(sheet.Number), "Dimensions", $"{sheet.TypedDimensions} dimension(s) show a typed value, not what the model measures.", "Remove the typed value, and move the element if the model is off."));
        }
        return issues;
    }

    /// <summary>
    /// A tag's box (u0, v0, u1, v1 in the view) without its leader: the text sits around the tag's head, and the leader
    /// stretches the box to one side only, so on each axis the shorter way from the head to the box's edge is half the text.
    /// </summary>
    public static (double U0, double V0, double U1, double V1) WithoutLeader(double u0, double v0, double u1, double v1, double headU, double headV)
    {
        if (headU <= u0 || headU >= u1 || headV <= v0 || headV >= v1) return (u0, v0, u1, v1);
        double du = Math.Min(headU - u0, u1 - headU), dv = Math.Min(headV - v0, v1 - headV);
        return (headU - du, headV - dv, headU + du, headV + dv);
    }

    /// <summary>
    /// The size of a rectangle lying along a view's axes, from the size of its box along the model's axes
    /// (<paramref name="boxW"/>, <paramref name="boxH"/>) and the view's turn (cosine and sine of its angle, both
    /// positive). The box itself when the view isn't turned, or is turned near 45 degrees (where it can't be told).
    /// </summary>
    public static (double W, double H) Unturned(double boxW, double boxH, double cos, double sin)
    {
        var d = cos * cos - sin * sin;
        if (sin < 1e-6 || Math.Abs(d) < 0.2) return (boxW, boxH);
        // Turned past 45 degrees the rectangle's sides swap roles.
        if (d < 0) { (cos, sin) = (sin, cos); d = -d; var (w2, h2) = ((boxW * cos - boxH * sin) / d, (boxH * cos - boxW * sin) / d); return w2 > 0 && h2 > 0 ? (h2, w2) : (boxW, boxH); }
        var (w, h) = ((boxW * cos - boxH * sin) / d, (boxH * cos - boxW * sin) / d);
        return w > 0 && h > 0 ? (w, h) : (boxW, boxH);
    }

    /// <summary>How a date is written: digits as 9, letters as A ("14/05/2026" is 99/99/9999, "06/17/26" is 99/99/99).</summary>
    public static string DateForm(string date) => Regex.Replace(Regex.Replace(date.Trim(), @"\d", "9"), @"\p{L}", "A");

    /// <summary>
    /// What to look at by eye on the sheets of <paramref name="berths"/>, not failures by themselves: on sheets other than
    /// the piles plans (where it is a failure), elements of a tagged category with no tag, and a key plan the model
    /// doesn't describe.
    /// </summary>
    public static List<string> Look(IReadOnlyList<Sheet> all, IEnumerable<int>? berths = null)
    {
        var wanted = (berths ?? []).Where(b => b > 0).ToHashSet();
        var lines = new List<string>();
        var noKeyPlan = new List<string>();
        var switches = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sheet in all)
        {
            if (Parse(sheet.Number) is not { Berth: > 0 } at || (wanted.Count > 0 && !wanted.Contains(at.Berth))) continue;
            if (KindOf(at.Series, sheet.Name)?.Key != "piles")
                foreach (var v in sheet.Views.Where(v => v.UntaggedInside > 0))
                    lines.Add($"{Bare(sheet.Number)}: {v.UntaggedInside} of {v.Taggable} elements of the tagged categories have no tag in \"{Clean(v.Title)}\". See whether they should have one.");
            if (!sheet.KeyPlanFound) { noKeyPlan.Add(Bare(sheet.Number)); switches.UnionWith(sheet.OtherSwitches); }
        }
        // Dates written like most of them only: one written another way is a failure of the check, not a later revision.
        var dated = all.Where(s => Parse(s.Number) is { } at && (at.Berth == 0 || wanted.Count == 0 || wanted.Contains(at.Berth)) && s.IssueDate.Trim().Length > 0).ToList();
        var form = dated.GroupBy(s => DateForm(s.IssueDate)).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
        var dates = dated.Where(s => DateForm(s.IssueDate) == form).GroupBy(s => s.IssueDate.Trim()).OrderByDescending(g => g.Count()).ToList();
        if (dates.Count > 1)
            lines.Add("Issue dates: " + string.Join("; ", dates.Select(g => $"{g.Key} on {g.Count()} sheet(s)" + (g != dates[0] ? $" ({string.Join(", ", g.Take(4).Select(s => Bare(s.Number)))}{(g.Count() > 4 ? ", ..." : "")})" : ""))) +
                      ". Right when those sheets were issued in another revision.");
        var pending = all.Where(s => Parse(s.Number) is { } at && (at.Berth == 0 || wanted.Count == 0 || wanted.Contains(at.Berth)) && s.Views.Any(v => v.NotJudged)).Select(s => Bare(s.Number)).ToList();
        if (pending.Count > 0)
            lines.Insert(0, $"Not read yet: the views on {pending.Count} sheet(s) ({string.Join(", ", pending.Take(6))}{(pending.Count > 6 ? ", ..." : "")}). Run the check again to carry on.");
        if (noKeyPlan.Count > 0)
            lines.Add($"Key plan: the model doesn't say which berth it highlights on {noKeyPlan.Count} sheet(s) ({string.Join(", ", noKeyPlan.Take(6))}{(noKeyPlan.Count > 6 ? ", ..." : "")}). Look at one sheet per berth (capture_view_image): only that berth is highlighted." +
                      (switches.Count > 0 ? $" The title block's yes/no switches name no berth: {string.Join(", ", switches.Take(20))}. Tell the user, so the right ones can be read." : ""));
        return lines;
    }

    /// <summary>
    /// The berths a title block's yes/no switch is for, by its name: "Berth 9", "BERTH 09", "B9", "B-10", "KP 9",
    /// "Key Plan - Berth 5&amp;6", "9". Empty when the name names no berth ("Show Logo", "A1").
    /// </summary>
    public static List<int> SwitchBerths(string name)
    {
        var n = Clean(name).ToUpperInvariant();
        var found = new List<int>();
        var m = Regex.Match(n, @"(?:BERTHS?|KEY ?PLAN|\bKP|\bBTH|\bBR)\D{0,3}(\d{1,2})(?:\s*(?:&|AND|,|\+)\s*(\d{1,2}))*\b");
        if (m.Success)
        {
            found.Add(int.Parse(m.Groups[1].Value));
            foreach (Capture c in m.Groups[2].Captures) found.Add(int.Parse(c.Value));
        }
        else if (Regex.Match(n, @"^(?:B[\s\-_.#]*)?(\d{1,2})$") is { Success: true } bare) found.Add(int.Parse(bare.Groups[1].Value));
        return found.Where(b => b > 0).Distinct().ToList();
    }

    /// <summary>The part a title block's switch is for ("Berth 6 - Part 2" is 2); 0 when it is for a whole berth.</summary>
    public static int SwitchPart(string name) =>
        Regex.Match(Clean(name), @"PART\D{0,2}(\d{1,2})\b", RegexOptions.IgnoreCase) is { Success: true } m ? int.Parse(m.Groups[1].Value) : 0;

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
