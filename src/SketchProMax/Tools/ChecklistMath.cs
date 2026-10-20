using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SketchProMax.Tools;

/// <summary>
/// The pre-submission checklist (its button is next to Chat; UI/ChecklistWindow): the checklist read from the
/// office's Excel sheet, the answers given in the window, what is still open, and the model hints each check gets. No
/// Revit or WPF here, so the tests run it; ChecklistDocx writes the signed Word file and ChecklistFacts reads the model.
///
/// The Excel layout is the office's own (one sheet named Checklist, or the first sheet with a "#" / "Check" header row):
/// header fields above the table (label in column A), then the table, where a row whose first cell is text is a section
/// (a stage in the window) and a row whose first cell is a number is a check; the columns are found by their headings
/// (Check, Example, Where it came from, found it on, Priority, Notes). A Notes cell starting "Waiting" marks a check that
/// waits on an answer. The row after "Sign-off" names who signs ("Trade BIM coordinator: ___  Trade group leader: ___").
/// The office's checklist ships built in (Data/pre-submission-checklist.json, with the names of people, the client and the
/// project replaced by roles and generic codes); an imported Excel file or edits made in the add-in replace it (ChecklistStore).
/// </summary>
internal static class ChecklistMath
{
    public const string Yes = "Y", No = "N", NotApplicable = "NA";

    public sealed record Field(string Label, string Example, string Source);

    public sealed record Item(string Id, string Number, string Check, string Example, string From, string FoundOn, string Priority, string Waiting, string Auto)
    {
        [System.Text.Json.Serialization.JsonIgnore]
        public bool High => Priority.Trim().Equals("High", StringComparison.OrdinalIgnoreCase);

        /// <summary>A few plain words for the list (the office's full wording stays in Check, and goes in the Word file).</summary>
        public string Short { get; init; } = "";

        /// <summary>How to put it right when it isn't done.</summary>
        public string Fix { get; init; } = "";

        /// <summary>What the list shows: the short title, else the check's first sentence.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string Title => Short.Trim().Length > 0 ? Short.Trim() : FirstSentence(Check);
    }

    /// <summary>The first sentence of <paramref name="text"/> (all of it when it has one).</summary>
    public static string FirstSentence(string text)
    {
        text = (text ?? "").Trim();
        var m = Regex.Match(text, @"^.+?[.!?](?=\s+[A-Z(])");
        return m.Success ? m.Value : text;
    }

    public sealed record Section(string Title, IReadOnlyList<Item> Items);

    public sealed class Definition
    {
        public string Title { get; set; } = "";
        public List<Field> Fields { get; set; } = [];
        public List<Section> Sections { get; set; } = [];
        public List<string> Signatories { get; set; } = [];
        public string SourcePath { get; set; } = "";
        public string SourceStamp { get; set; } = "";

        /// <summary>The checking rules set for this checklist (RuleDefs keys); a rule not set uses its default.</summary>
        public Dictionary<string, string> Rules { get; set; } = new(StringComparer.Ordinal);

        [System.Text.Json.Serialization.JsonIgnore]
        public IEnumerable<Item> Items => Sections.SelectMany(s => s.Items);
    }

    // ---- Checking rules -------------------------------------------------------------------------------------------

    /// <summary>
    /// A checking rule: what the automatic model test compares the model with (a name pattern, a list, a size), set per
    /// checklist (Checking rules… in the window) and carried in the Excel file's Rules sheet. Patterns: # is a digit,
    /// ? any one character, * anything; lists are comma separated. An empty value turns that part of the test off.
    /// </summary>
    public sealed record RuleDef(string Key, string Label, string Default, string Help, string Check);

    public static readonly RuleDef[] RuleDefs =
    [
        new("filename", "Model file name pattern", "*-ABC-002-*", "The working model's file name, e.g. PRJ1-###-##-ABC-002-STR-3DM-####.", "name002"),
        new("filesize", "Largest file size (MB)", "300", "The model file may be at most this size.", "filesize"),
        new("levels", "Level name pattern", "BLDG * LVL *", "Every level name matches one of these.", "levels"),
        new("scopeboxes", "Scope box name pattern", "BLDG-##-###*", "Every scope box name matches one of these.", "scopeboxes"),
        new("linkworksets", "Worksets that must exist", "Z_Link_URS, Z_Link_CAD, Z_Link_AR, Z_Link_EL, Z_Link_ME, Z_Link_SC, Z_Link_SS", "Each of these worksets is in the model.", "worksets"),
        new("approvedworksets", "Approved worksets", "", "Every workset matches one of these (empty: not tested). Workset1 and Shared Levels and Grids are always allowed.", "newworksets"),
        new("columns", "Column levels tested", "yes", "yes: a column that runs past the next level up is flagged (list the double-height ones in the comment). no: not tested.", "columns"),
        new("classification", "Classification parameter", "Assembly Code", "The type parameter holding the UniFormat code; every model type in use must have it filled.", "uniformat"),
        new("ifc", "IFC entity parameter", "IfcExportAs", "The type or instance parameter holding the IFC entity; every model type in use must have it filled.", "ifc"),
        new("badparameters", "Project parameters not allowed", "", "Project parameter names matching any of these are flagged, e.g. another firm's prefix (empty: not tested).", "parameters"),
        new("titleblock", "Title block family", "", "The title block every sheet uses (empty: read from the check's example).", "titleblock"),
        new("tbmodelname", "Sheet parameter with the model name", "", "The sheet or title block parameter that shows the model name; it must match the file name (empty: not tested).", "tbmodelname"),
        new("scales", "Approved scales", "1:1, 1:2, 1:5, 1:10, 1:20, 1:25, 1:50, 1:100, 1:200, 1:250, 1:500, 1:1000", "Views on sheets use one of these scales.", "scales"),
        new("viewfolders", "View folder parameters", "", "The view parameters the Project Browser sorts by, e.g. Discipline, Sub-Discipline; a view with one empty lands in ??? (empty: not tested).", "viewfolders"),
        new("navisview", "Navisworks view name", "*Navisworks Export*", "The 3D view exported to NWC.", "navisview"),
        new("landing", "Landing page family", "LandingPage", "The family placed on the starting view.", "landing"),
        new("submission", "Submitted copy ending", "_S03_R##", "The submitted copy is the model's name with this added; Sketch ProMax looks for it next to the model.", "submission"),
        new("exportfolder", "Folder of the NWC and IFC files", "", "Where the NWC and IFC exports are saved (empty: the model's folder and the folders in it).", "exportnames"),
        new("typenames", "Type name pattern", "*_*_*", "Every model type in use is named like one of these, e.g. FN_SC_40MPa_Raft_600 (empty: not tested).", "familynames"),
        new("othertrades", "Categories that don't belong in this model", "auto", "Other trades' categories, comma separated. auto: picked from the discipline in the file name " +
            "(STR: doors, windows, furniture, ceilings, pipes, ducts, cable trays, MEP equipment and fixtures, and walls that aren't structural). Empty: not tested.", "othertrades"),
        new("detaillines", "Detail lines and filled regions per plan or section", "20", "A plan, section or elevation on a sheet with more than this many is listed for a look (empty: not tested).", "detaillines"),
        new("legendcategories", "Categories whose types need a legend entry", "Walls, Floors, Structural Foundations", "Every type in use in these categories must be shown in a legend (empty: not tested).", "legend"),
        new("standardtemplate", "The trade's standard template file", "", "The trade's standard .rte or .rvt; its view templates, filters and browser organisation must all be in the model (empty: not tested).", "standards"),
        new("schedules", "Schedules that must be in the model", "", "Name patterns of the schedules you use, e.g. *Foundation*. Schedules on no sheet are always listed: tick Schedules in e-Transmit or place them.", "schedules"),
        new("clashcategories", "Categories clashed against each other", "auto", "auto: picked from the discipline in the file name (STR: structural columns, framing, foundations, floors and walls). Empty: not tested.", "selfclash"),
        new("modelscore", "Lowest model check score (%)", "90", "Sketch ProMax's model check (warnings, duplicates, beams on nothing, columns off grid and more) must score at least this. " +
            "It stands in for the Model Checker template; keep that score in the QA/QC record too (empty: not tested).", "modelscore"),
        new("midp", "MIDP file", "", "The latest MIDP (.xlsx or .csv), or the folder it is saved in (the newest is read); the model's number must be in it (empty: not tested).", "midp"),
        new("stagelist", "Drawing list for the stage", "", "The stage's drawing list (.xlsx or .csv), or its folder (the newest is read); every drawing number in it shaped like this model's sheet numbers must be a sheet (empty: not tested).", "stagelist"),
        new("clashreport", "Navisworks clash report", "", "The federated model's clash report (.xml or .html), or the folder it is saved in (the newest is read); none of your clashes may be New, Active or Reviewed (empty: not tested).", "federated"),
        new("healthcheck", "Model Health Check report name", "*-RPT-*.pdf", "The Health Check report's file name pattern, looked for in the package folder, else next to the model (empty: only the signed checklist is checked).", "signed"),
        new("ifcsetup", "IFC export setup file (.json)", "", "The office's IFC export setup saved from Revit's IFC exporter; Export the NWC and IFC applies it (empty: IFC 4x3, shared coordinates, visible elements only, no links).", "ifcsettings"),
        new("nwcday", "Day the NWC is published", "Monday", "The newest NWC must be from this day of the week or later, and newer than the model's last save (empty: only newer than the model).", "nwcdate"),
        new("packagefolder", "Package folder", "", "Where the e-Transmit package is saved (a folder, or a ZIP in it: the newest is read). The package checks look in it and open the packaged model (empty: not tested).", "packagecontents"),
        new("packagecontents", "Files the package must have", ".rvt, .ifc, .nwc, .dwg, .pdf", "File types the package must contain, e.g. add .xlsx for a BOQ.", "packagecontents"),
    ];

    /// <summary>The rule's value for this checklist: set, else the default ("" turns that test off).</summary>
    public static string Rule(Definition? def, string key)
    {
        if (def is not null && def.Rules.TryGetValue(key, out var v)) return (v ?? "").Trim();
        return RuleDefs.FirstOrDefault(r => r.Key == key)?.Default ?? "";
    }

    /// <summary>A comma separated rule as its parts.</summary>
    public static List<string> RuleList(string value) =>
        (value ?? "").Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).Where(p => p.Length > 0).ToList();

    /// <summary>Whether <paramref name="value"/> matches a pattern: # a digit, ? any one character, * anything; case ignored.</summary>
    public static bool Like(string value, string pattern)
    {
        var rx = "^" + Regex.Escape(pattern.Trim()).Replace("\\*", ".*").Replace("\\?", ".").Replace("\\#", "[0-9]").Replace("\\ ", "\\s+") + "$";
        return Regex.IsMatch((value ?? "").Trim(), rx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>Whether <paramref name="value"/> matches any pattern of the list.</summary>
    public static bool LikeAny(string value, string patterns) => RuleList(patterns).Any(p => Like(value, p));

    /// <summary>SourcePath of the checklist that ships with the add-in, and of one edited in the add-in.</summary>
    public const string BuiltInSource = "built-in", EditedSource = "edited";

    /// <summary>The checklist that ships with the add-in (an embedded resource), or null when it can't be read.</summary>
    public static Definition? BuiltIn()
    {
        try
        {
            using var stream = typeof(ChecklistMath).Assembly.GetManifestResourceStream("SketchProMax.pre-submission-checklist.json");
            if (stream is null) return null;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var def = FromJson<Definition>(reader.ReadToEnd());
            if (def is null || !def.Items.Any()) return null;
            def.SourcePath = BuiltInSource;
            return def;
        }
        catch (IOException) { return null; }
    }

    /// <summary>
    /// The checklist as edited: checks numbered 1, 2, 3… down the stages (ids follow), the model hint matched again
    /// from each check's wording. Answers follow their check by its wording (Reconcile), so renumbering loses none.
    /// </summary>
    public static Definition Renumber(Definition def)
    {
        var n = 0;
        var sections = def.Sections.Select(s => new Section(s.Title.Trim(), s.Items.Select(i =>
        {
            var num = (++n).ToString(CultureInfo.InvariantCulture);
            var check = i.Check.Trim();
            return i with { Id = num, Number = num, Check = check, Auto = AutoKey(check) };
        }).ToList())).ToList();
        return new Definition
        {
            Title = def.Title.Trim(), Fields = def.Fields.ToList(), Sections = sections, Signatories = def.Signatories.ToList(),
            SourcePath = def.SourcePath, SourceStamp = def.SourceStamp, Rules = new(def.Rules, StringComparer.Ordinal),
        };
    }

    public sealed class Answer
    {
        /// <summary>The check's wording when it was answered: a new revision that rewords or renumbers it doesn't inherit the answer.</summary>
        public string Check { get; set; } = "";
        public string Done { get; set; } = "";
        public string Note { get; set; } = "";
        public string By { get; set; } = "";
        public string Date { get; set; } = "";
    }

    /// <summary>How one signatory signs: with the user's saved signature, a picked image, or by hand on the printout.</summary>
    public static class SignMode { public const string Mine = "mine", Image = "image", Hand = "hand"; }

    public sealed class Signature
    {
        public string Role { get; set; } = "";
        public string Name { get; set; } = "";
        public string Mode { get; set; } = SignMode.Hand;
        public string ImagePath { get; set; } = "";
        public string Date { get; set; } = "";
    }

    /// <summary>What the window keeps per model between openings.</summary>
    public sealed class State
    {
        public string Checklist { get; set; } = "";
        public Dictionary<string, string> Fields { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, Answer> Answers { get; set; } = new(StringComparer.Ordinal);
        public List<Signature> Signatures { get; set; } = [];
        public int Stage { get; set; }

        public Answer For(string id) => Answers.TryGetValue(id, out var a) ? a : Answers[id] = new Answer();
    }

    // ---- Progress -------------------------------------------------------------------------------------------------

    public sealed record Progress(int Total, int Yes, int No, int NotApplicable, int Open, int WaitingWithoutNote, int NoWithoutNote)
    {
        public int Answered => Yes + No + NotApplicable;

        /// <summary>The office's rule: nothing left unanswered and no check marked N.</summary>
        public bool Ready => Total > 0 && Open == 0 && No == 0;
    }

    /// <summary>Your answers as a percentage: checks answered Yes or N/A out of all checks.</summary>
    public static int Percent(Progress p) => p.Total == 0 ? 0 : (int)Math.Round(100.0 * (p.Yes + p.NotApplicable) / p.Total, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The automatic check: checks the model can test (Tested) and how many of those pass or fail, out of all the checks
    /// (Total). The checks the model can't test (Manual) count only once you answer them Yes or N/A (ManualDone), so the
    /// score can't reach 100% while they are left undone.
    /// </summary>
    public sealed record AutoProgress(int Tested, int Pass, int Fail, int Total = 0, int Manual = 0, int ManualDone = 0)
    {
        /// <summary>Passed checks (by the model, or answered Yes or N/A when the model can't test them) out of all the checks.</summary>
        public int Percent => Total > 0 ? (int)Math.Round(100.0 * (Pass + ManualDone) / Total, MidpointRounding.AwayFromZero)
            : Tested == 0 ? 0 : (int)Math.Round(100.0 * Pass / Tested, MidpointRounding.AwayFromZero);

        /// <summary>The highest score the model test alone can give: the rest needs your answers.</summary>
        public int Ceiling => Total == 0 ? 100 : (int)Math.Round(100.0 * (Total - Manual) / Total, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// The model's own verdict on the checks it can test: <paramref name="verdict"/> gives true (passes), false (fails) or
    /// null (the model can't tell) for a check's model rule. The other checks count from <paramref name="state"/> when
    /// answered Yes or N/A. Separate from the answers score: you can be at 100% while it is at 50%.
    /// </summary>
    public static AutoProgress Auto(IEnumerable<Item> items, Func<string, bool?> verdict, State? state = null)
    {
        int total = 0, pass = 0, fail = 0, manual = 0, manualDone = 0;
        foreach (var item in items)
        {
            total++;
            switch (item.Auto.Length == 0 ? null : verdict(item.Auto))
            {
                case true: pass++; break;
                case false: fail++; break;
                default:
                    manual++;
                    var done = state is not null && state.Answers.TryGetValue(item.Id, out var a) ? Normal(a.Done) : "";
                    if (done == Yes || done == NotApplicable) manualDone++;
                    break;
            }
        }
        return new AutoProgress(pass + fail, pass, fail, total, manual, manualDone);
    }

    /// <summary>Checks you answered Yes that the model check fails: worth a second look before signing.</summary>
    public static List<Item> Disagree(IEnumerable<Item> items, State state, Func<string, bool?> verdict) =>
        items.Where(i => i.Auto.Length > 0 && verdict(i.Auto) == false && state.Answers.TryGetValue(i.Id, out var a) && Normal(a.Done) == Yes).ToList();

    /// <summary>The automatic check in one line, for the Word file and the window ("Automatic model check: 6 of 12 passed (50%)").</summary>
    public static string AutoLine(AutoProgress auto, IReadOnlyCollection<Item> disagree)
    {
        if (auto.Tested == 0) return "";
        var line = $"Automatic check: {auto.Pass + auto.ManualDone} of {auto.Total} checks pass ({auto.Percent}%): the model test passed {auto.Pass} of the {auto.Tested} it can test"
                   + (auto.Manual > 0 ? $", and {auto.ManualDone} of the {auto.Manual} checked by hand are Yes or N/A." : ".");
        if (disagree.Count > 0) line += " Answered Yes but failed by the model check: " + string.Join(", ", disagree.Select(i => "#" + i.Number)) + ".";
        return line;
    }

    public static Progress Count(IEnumerable<Item> items, State state)
    {
        int total = 0, yes = 0, no = 0, na = 0, open = 0, waiting = 0, noNote = 0;
        foreach (var item in items)
        {
            total++;
            var a = state.Answers.TryGetValue(item.Id, out var found) ? found : null;
            var done = Normal(a?.Done);
            var note = (a?.Note ?? "").Trim();
            switch (done)
            {
                case Yes: yes++; break;
                case No: no++; if (note.Length == 0) noNote++; break;
                case NotApplicable: na++; break;
                default: open++; break;
            }
            if (item.Waiting.Length > 0 && done.Length > 0 && note.Length == 0) waiting++;
        }
        return new Progress(total, yes, no, na, open, waiting, noNote);
    }

    /// <summary>Y, N or NA in any usual spelling (yes, no, n/a, done...), else "".</summary>
    public static string Normal(string? value)
    {
        var v = (value ?? "").Trim().ToUpperInvariant().Replace("/", "").Replace(" ", "");
        return v switch
        {
            "Y" or "YES" or "DONE" or "OK" => Yes,
            "N" or "NO" or "NOTDONE" => No,
            "NA" or "N.A." or "NOTAPPLICABLE" or "-" => NotApplicable,
            _ => "",
        };
    }

    /// <summary>What still needs the user before the checklist is ready, one line each with the check it is about.</summary>
    public static List<(string Id, string Text)> Problems(Definition def, State state)
    {
        var list = new List<(string, string)>();
        foreach (var item in def.Items)
        {
            var a = state.Answers.TryGetValue(item.Id, out var found) ? found : null;
            var done = Normal(a?.Done);
            var note = (a?.Note ?? "").Trim();
            if (done.Length == 0) list.Add((item.Id, $"#{item.Number} not answered: {Short(item.Check)}"));
            else if (done == No) list.Add((item.Id, $"#{item.Number} is No{(note.Length == 0 ? " (say why in its comment)" : "")}: {Short(item.Check)}"));
            else if (item.Waiting.Length > 0 && note.Length == 0) list.Add((item.Id, $"#{item.Number} waits on an answer: write what you did in its comment"));
        }
        return list;
    }

    private static string Short(string text) => text.Length <= 90 ? text : text.Substring(0, 87).TrimEnd() + "…";

    /// <summary>
    /// Keeps each answer with its check: a check renumbered (a new revision, or one added or deleted above it) takes its
    /// answer along by its wording; answers of reworded or deleted checks are dropped, so an answer never lands on a
    /// different check. Returns how many were dropped.
    /// </summary>
    public static int Reconcile(Definition def, State state)
    {
        var old = state.Answers;
        var kept = new Dictionary<string, Answer>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        // Same number and wording (or no wording kept yet) first, then the same wording under another number (renumbered).
        foreach (var item in def.Items)
            if (old.TryGetValue(item.Id, out var a) && (a.Check.Length == 0 || Same(a.Check, item.Check))) { kept[item.Id] = a; used.Add(item.Id); }
        foreach (var item in def.Items)
        {
            if (kept.ContainsKey(item.Id)) continue;
            var hit = old.FirstOrDefault(p => !used.Contains(p.Key) && p.Value.Check.Length > 0 && Same(p.Value.Check, item.Check));
            if (hit.Value is null) continue;
            kept[item.Id] = hit.Value;
            used.Add(hit.Key);
        }
        foreach (var item in def.Items)
            if (kept.TryGetValue(item.Id, out var a) && a.Check.Length == 0) a.Check = item.Check;
        var dropped = old.Count - used.Count;
        state.Answers = kept;
        return dropped;
    }

    private static bool Same(string a, string b) =>
        string.Equals(Regex.Replace(a, @"\s+", " ").Trim(), Regex.Replace(b, @"\s+", " ").Trim(), StringComparison.OrdinalIgnoreCase);

    // ---- Stages ---------------------------------------------------------------------------------------------------

    /// <summary>The window's stages: project details, one per section, then review and sign.</summary>
    public static List<string> Stages(Definition def)
    {
        var list = new List<string> { "Project details" };
        list.AddRange(def.Sections.Select(s => StageName(s.Title)));
        list.Add("Review and sign");
        return list;
    }

    /// <summary>"A. File names and size" reads as "File names and size" in the stage list (the letter is shown as the step number).</summary>
    public static string StageName(string title) => Regex.Replace(title.Trim(), @"^[A-Z]\.\s+", "");

    // ---- Model hints ----------------------------------------------------------------------------------------------

    /// <summary>
    /// The model hint a check gets, found by its wording (so a renumbered or reworded sheet keeps them): the key that
    /// ChecklistFacts answers. A check matching none gets no hint.
    /// </summary>
    public static readonly (string Key, Regex Pattern)[] AutoRules =
    [
        ("name002", Rx(@"\b002 after ABC")),
        ("doctype", Rx(@"^Doc type")),
        ("levelfield", Rx(@"^Level field")),
        ("filesize", Rx(@"^File size")),
        ("urs", Rx(@"URS is linked")),
        ("grids", Rx(@"^Grids are in the model")),
        ("levels", Rx(@"^Unused levels")),
        ("worksets", Rx(@"on its .*workset")),
        ("scopeboxes", Rx(@"^Scope boxes")),
        ("categories", Rx(@"right Revit category|No Generic Models")),
        ("imports", Rx(@"^No imported CAD")),
        ("groups", Rx(@"^Model Groups")),
        ("warnings", Rx(@"^Warnings are fixed")),
        ("landing", Rx(@"^Landing page")),
        ("titleblock", Rx(@"^All sheets use the title block")),
        ("scales", Rx(@"approved scale")),
        ("drawinglist", Rx(@"List of Drawings sheet")),
        ("viewtemplates", Rx(@"view template and filters for its purpose")),
        ("unusedviews", Rx(@"^Unused views")),
        ("navisview", Rx(@"^A '?3D Navisworks Export'? view")),
        ("central", Rx(@"exported from the central")),
        ("links", Rx(@"links unloaded")),
        ("newworksets", Rx(@"^No new worksets")),
        ("columns", Rx(@"^Columns are modelled level by level")),
        ("uniformat", Rx(@"Classification code")),
        ("parameters", Rx(@"^Project parameters follow")),
        ("ifc", Rx(@"^IFC entity")),
        ("tbmodelname", Rx(@"model name shown in the title block")),
        ("viewfolders", Rx(@"^Every view is in a folder")),
        ("submission", Rx(@"^The submitted copy adds")),
        ("exportnames", Rx(@"^NWC and IFC files have")),
        ("familynames", Rx(@"^Family and type names follow")),
        ("duplicates", Rx(@"^No duplicate, overlapping")),
        ("floating", Rx(@"^No floating elements")),
        ("othertrades", Rx(@"Other trades' elements are removed")),
        ("detaillines", Rx(@"^No detail lines or filled regions")),
        ("legend", Rx(@"own hatch, shown in the legend")),
        ("standards", Rx(@"^Browser organisation, view templates")),
        ("schedules", Rx(@"^Schedules you use stay")),
        ("selfclash", Rx(@"no clashes inside your own trade")),
        ("modelscore", Rx(@"^Model Checker template is run")),
        ("midp", Rx(@"model number comes from the latest MIDP")),
        ("stagelist", Rx(@"^The drawing list has everything")),
        ("federated", Rx(@"clash tests in the federated model")),
        ("signed", Rx(@"^The checklist is signed")),
        ("nwcsettings", Rx(@"^NWC settings")),
        ("ifcsettings", Rx(@"^IFC:\s")),
        ("nwcdate", Rx(@"NWC is published on")),
        ("etransmit", Rx(@"^e-Transmit settings")),
        ("backgrounds", Rx(@"another trade as background")),
        ("civil3d", Rx(@"^Civil 3D only")),
        ("packagecontents", Rx(@"^Package has everything")),
        ("packageopens", Rx(@"^Open the package from a clean folder")),
    ];

    private static Regex Rx(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string AutoKey(string check)
    {
        foreach (var (key, pattern) in AutoRules)
            if (pattern.IsMatch(check)) return key;
        return "";
    }

    /// <summary>The header field a label is filled from: model_file, submission_file, building, stage, coordinator, leader, date or "".</summary>
    public static string FieldSource(string label)
    {
        var l = label.ToLowerInvariant();
        if (l.Contains("submission") && l.Contains("name")) return "submission_file";
        if (l.Contains("model") && l.Contains("file name")) return "model_file";
        if (l.Contains("building") || l.Contains("discipline")) return "building";
        if (l.Trim() == "stage") return "stage";
        if (l.Contains("coordinator")) return "coordinator";
        if (l.Contains("leader")) return "leader";
        if (l.Trim() == "date") return "date";
        return "";
    }

    /// <summary>
    /// The office's file name parts (PRJ1-070-00-ABC-002-STR-3DM-0010): the building code, the level field, the
    /// discipline and the doc type, or empty strings when the name doesn't follow the pattern.
    /// </summary>
    public static (string Building, string Level, string Discipline, string DocType, bool Has002) NameParts(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName ?? "");
        var m = Regex.Match(name, @"^[A-Z0-9]+-(?<b>\d{3})-(?<l>[A-Z0-9]{2})-(?<o>[A-Z]{3})(?<two>-002)?-(?<d>[A-Z]{2,4})-(?<t>[A-Z0-9]{3})-\d{4}", RegexOptions.IgnoreCase);
        if (!m.Success) return ("", "", "", "", name.IndexOf("-002-", StringComparison.Ordinal) >= 0);
        return (m.Groups["b"].Value, m.Groups["l"].Value.ToUpperInvariant(), m.Groups["d"].Value.ToUpperInvariant(), m.Groups["t"].Value.ToUpperInvariant(), m.Groups["two"].Success);
    }

    /// <summary>"070" reads as "BLDG-07" (the building codes are the building number times ten).</summary>
    public static string BuildingName(string code)
    {
        if (code.Length != 3 || !int.TryParse(code, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return "";
        return n % 10 == 0 ? $"BLDG-{n / 10:00}" : $"BLDG-{code}";
    }

    /// <summary>
    /// The submitted file's name: the working name with _S03_R00 added (a name that already has _Snn_Rnn keeps it, with
    /// the revision moved on by one: a resubmission).
    /// </summary>
    public static string SubmissionName(string modelFile)
    {
        var ext = Path.GetExtension(modelFile);
        if (ext.Length == 0) ext = ".rvt";
        var name = Path.GetFileNameWithoutExtension(modelFile);
        name = Regex.Replace(name, @"_detached$", "", RegexOptions.IgnoreCase);
        var m = Regex.Match(name, @"^(?<base>.*)_(?<s>S\d{2})_R(?<r>\d{2})$", RegexOptions.IgnoreCase);
        if (m.Success)
            return $"{m.Groups["base"].Value}_{m.Groups["s"].Value.ToUpperInvariant()}_R{int.Parse(m.Groups["r"].Value, CultureInfo.InvariantCulture) + 1:00}{ext}";
        return name + "_S03_R00" + ext;
    }

    /// <summary>A model's working file name: its title with _detached (and a user name a local copy adds) dropped.</summary>
    public static string WorkingName(string title)
    {
        var name = Path.GetFileNameWithoutExtension(title ?? "");
        name = Regex.Replace(name, @"_detached$", "", RegexOptions.IgnoreCase);
        return name.Length == 0 ? "" : name + ".rvt";
    }

    /// <summary>Who signs, from the line under "Sign-off": each "Role: ____" except Date.</summary>
    public static List<string> ParseSignatories(string line)
    {
        var list = new List<string>();
        foreach (Match m in Regex.Matches(line ?? "", @"([A-Za-z][A-Za-z /&\-]*?)\s*:\s*_+"))
        {
            var role = m.Groups[1].Value.Trim();
            if (role.Length > 0 && !role.Equals("Date", StringComparison.OrdinalIgnoreCase) && !list.Contains(role)) list.Add(role);
        }
        return list;
    }

    // ---- Files the checks read: lists, reports, dates ----------------------------------------------------------------

    /// <summary>
    /// A rule naming a file or the folder it is saved in: the file itself, or the newest file in the folder with one of
    /// <paramref name="extensions"/>; "" when there is none.
    /// </summary>
    public static string NewestFile(string fileOrFolder, params string[] extensions)
    {
        var path = (fileOrFolder ?? "").Trim().Trim('"');
        if (path.Length == 0) return "";
        if (File.Exists(path)) return path;
        if (!Directory.Exists(path)) return "";
        return Directory.EnumerateFiles(path).Where(f => extensions.Any(e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase)) && !Path.GetFileName(f).StartsWith("~$", StringComparison.Ordinal))
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() ?? "";
    }

    /// <summary>Every non-empty cell of an .xlsx (all sheets) or the fields of a .csv / .txt, as text.</summary>
    public static List<string> FileCells(string path)
    {
        if (path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".xlsm", StringComparison.OrdinalIgnoreCase))
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return ReadSheets(stream).Values.SelectMany(rows => rows.SelectMany(r => r.Cells.Values)).Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
        }
        var text = File.ReadAllText(path);
        return text.Split(['\n', '\r', ',', ';', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(c => c.Trim().Trim('"').Trim()).Where(c => c.Length > 0).ToList();
    }

    /// <summary>A drawing number's shape: its digits as #, the rest kept ("S-101" and "S-205" are both "S-###").</summary>
    public static string Shape(string number) => Regex.Replace((number ?? "").Trim().ToUpperInvariant(), @"\d", "#");

    /// <summary>
    /// The stage's drawing list against the model's sheets: the list's numbers shaped like the model's sheet numbers that
    /// aren't sheets (Missing), and the model's sheets the list doesn't have (Extra).
    /// </summary>
    public static (List<string> Missing, List<string> Extra) CompareDrawingList(IEnumerable<string> listCells, IReadOnlyCollection<string> sheetNumbers)
    {
        var sheets = new HashSet<string>(sheetNumbers.Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
        var shapes = new HashSet<string>(sheets.Select(Shape), StringComparer.Ordinal);
        var listed = listCells.Select(c => c.Trim()).Where(c => c.Length > 1 && shapes.Contains(Shape(c))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var missing = listed.Where(n => !sheets.Contains(n)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        var listedSet = new HashSet<string>(listed, StringComparer.OrdinalIgnoreCase);
        var extra = sheets.Where(n => !listedSet.Contains(n)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        return (missing, extra);
    }

    /// <summary>A Navisworks clash report's open clashes (New, Active, Reviewed) per test, from its XML or HTML.</summary>
    public sealed record ClashTest(string Name, int Open, int Closed);

    /// <summary>
    /// The clash tests in a Navisworks report and their open and closed clashes. XML reports give each test; an HTML
    /// report is counted as one test named "report".
    /// </summary>
    public static List<ClashTest> ClashReport(string text)
    {
        static bool IsOpen(string status) => status.ToLowerInvariant() is "new" or "active" or "reviewed";
        var tests = new List<ClashTest>();
        if (Regex.IsMatch(text, @"<clashtest\b", RegexOptions.IgnoreCase))
        {
            foreach (Match t in Regex.Matches(text, @"<clashtest\b(?<attrs>[^>]*)>(?<body>.*?)</clashtest>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                var name = Regex.Match(t.Groups["attrs"].Value, @"\bname=""(?<n>[^""]*)""", RegexOptions.IgnoreCase).Groups["n"].Value;
                var statuses = Regex.Matches(t.Groups["body"].Value, @"<clashresult\b[^>]*\bstatus=""(?<s>[^""]*)""", RegexOptions.IgnoreCase).Cast<Match>().Select(m => m.Groups["s"].Value).ToList();
                tests.Add(new ClashTest(System.Net.WebUtility.HtmlDecode(name), statuses.Count(IsOpen), statuses.Count(s => !IsOpen(s))));
            }
            return tests;
        }
        var cells = Regex.Matches(text, @">\s*(?<s>New|Active|Reviewed|Approved|Resolved)\s*<", RegexOptions.IgnoreCase).Cast<Match>().Select(m => m.Groups["s"].Value).ToList();
        if (cells.Count > 0) tests.Add(new ClashTest("report", cells.Count(IsOpen), cells.Count(s => !IsOpen(s))));
        return tests;
    }

    /// <summary>
    /// The clash tests this trade fixes: in "Trade-1_Vs_Trade-2" the first trade fixes. A test whose first trade names
    /// <paramref name="discipline"/> is ours; with no discipline, or no test named that way, every test counts.
    /// </summary>
    public static List<ClashTest> OurClashTests(IReadOnlyList<ClashTest> tests, string discipline)
    {
        if ((discipline ?? "").Length == 0) return tests.ToList();
        var ours = tests.Where(t => Regex.Match(t.Name, @"^\s*(?<first>.+?)\s*[_\- ]\s*vs\.?\s*[_\- ]", RegexOptions.IgnoreCase) is { Success: true } m
                                    && Regex.IsMatch(m.Groups["first"].Value, @"(^|[^A-Za-z])" + Regex.Escape(discipline) + @"($|[^A-Za-z])", RegexOptions.IgnoreCase)).ToList();
        return ours.Count > 0 ? ours : tests.ToList();
    }

    /// <summary>The start of the most recent <paramref name="day"/> on or before <paramref name="now"/>.</summary>
    public static DateTime LastDay(DateTime now, DayOfWeek day) => now.Date.AddDays(-(((int)now.DayOfWeek - (int)day + 7) % 7));

    // ---- Reading the Excel sheet ----------------------------------------------------------------------------------

    /// <summary>Reads the checklist from the office's Excel file (.xlsx). Throws InvalidDataException with a plain reason when it can't.</summary>
    public static Definition ReadXlsx(string path)
    {
        Dictionary<string, List<(int Row, Dictionary<int, string> Cells)>> sheets;
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            sheets = ReadSheets(stream);
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException or NotSupportedException)
        {
            throw new InvalidDataException("it isn't an Excel workbook (.xlsx) Sketch ProMax can read. Save it from Excel as .xlsx and pick it again.", ex);
        }
        var def = FromRows(sheets);
        // The revision ("Rev 01") is usually on the read-me sheet's first line, not the checklist's title.
        if (!Regex.IsMatch(def.Title, @"\bRev\b", RegexOptions.IgnoreCase))
            foreach (var rows in sheets.Values)
                if (rows.Count > 0 && rows[0].Cells.Values.FirstOrDefault() is { } first
                    && Regex.Match(first, @"\bRev\.?\s*\d+\b", RegexOptions.IgnoreCase) is { Success: true } rev
                    && first.IndexOf(def.Title, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    def.Title += ", " + rev.Value;
                    break;
                }
        def.SourcePath = path;
        def.SourceStamp = Stamp(path);
        return def;
    }

    /// <summary>The file's size and last write time, to notice a newer revision of the same file.</summary>
    public static string Stamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return ""; }
    }

    internal static Definition FromRows(Dictionary<string, List<(int Row, Dictionary<int, string> Cells)>> sheets)
    {
        // The Checklist sheet, else the first with a "#" / "Check" header row.
        List<(int Row, Dictionary<int, string> Cells)>? rows = null;
        foreach (var pair in sheets.OrderBy(p => p.Key.Equals("Checklist", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
            if (HeaderRow(pair.Value) >= 0) { rows = pair.Value; break; }
        if (rows is null)
            throw new InvalidDataException("no sheet has a header row with \"#\" and \"Check\" columns, so the checks can't be found. Use the office's checklist layout.");

        var header = HeaderRow(rows);
        var head = rows[header].Cells;
        int Col(params string[] names)
        {
            foreach (var pair in head)
                foreach (var n in names)
                    if (pair.Value.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) return pair.Key;
            return -1;
        }
        int number = head.First(p => p.Value.Trim() == "#").Key;
        int check = Col("Check");
        int example = Col("Example"), from = Col("Where it came", "Source", "Reference"), found = Col("found it on"), priority = Col("Priority"), notes = Col("Notes", "Remarks");
        int shortTitle = Col("Short title"), fix = Col("How to fix");
        string Cell(Dictionary<int, string> cells, int col) => col >= 0 && cells.TryGetValue(col, out var v) ? v.Trim() : "";

        var def = new Definition();
        // Above the table: the title (first text), then the header fields (label in the first column, example in the third).
        for (var i = 0; i < header; i++)
        {
            var cells = rows[i].Cells;
            var first = Cell(cells, 0);
            if (first.Length == 0) continue;
            if (def.Title.Length == 0) { def.Title = Regex.Replace(first, @"\s*\(.*\)\s*$", ""); continue; }
            if (first.Equals("Progress", StringComparison.OrdinalIgnoreCase)) continue;
            var ex = cells.Where(p => p.Key > 0).Select(p => p.Value.Trim()).FirstOrDefault(v => v.Length > 0 && !v.StartsWith("=", StringComparison.Ordinal)) ?? "";
            def.Fields.Add(new Field(first, Regex.Replace(ex, @"^e\.g\.\s*", "", RegexOptions.IgnoreCase), FieldSource(first)));
        }

        string? sectionTitle = null;
        var items = new List<Item>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        void Close()
        {
            if (sectionTitle is not null && items.Count > 0) def.Sections.Add(new Section(sectionTitle, items.ToList()));
            items.Clear();
        }
        for (var i = header + 1; i < rows.Count; i++)
        {
            var cells = rows[i].Cells;
            var first = Cell(cells, number);
            if (first.Length == 0) continue;
            if (first.StartsWith("Sign", StringComparison.OrdinalIgnoreCase) && first.IndexOf("off", StringComparison.OrdinalIgnoreCase) > 0)
            {
                Close();
                sectionTitle = null;
                for (var j = i + 1; j < rows.Count && def.Signatories.Count == 0; j++) def.Signatories = ParseSignatories(Cell(rows[j].Cells, number));
                break;
            }
            if (double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            {
                var text = Cell(cells, check);
                if (text.Length == 0) continue;
                var num = n.ToString("0.##", CultureInfo.InvariantCulture);
                var id = num;
                for (var k = 2; !ids.Add(id); k++) id = num + "-" + k;
                var note = Cell(cells, notes);
                var waiting = Regex.IsMatch(note, @"^\s*(Waiting|Open point|TBC)\b", RegexOptions.IgnoreCase) ? Regex.Replace(note, @"^\s*Waiting\s*:\s*", "", RegexOptions.IgnoreCase) : "";
                items.Add(new Item(id, num, text, Cell(cells, example), Cell(cells, from), Cell(cells, found), Cell(cells, priority), waiting, AutoKey(text))
                    { Short = Cell(cells, shortTitle), Fix = Cell(cells, fix) });
                continue;
            }
            // Text in the first column and nothing in the check column: a section (a note row with text across is ignored).
            if (Cell(cells, check).Length == 0 && first.Length <= 80 && !first.StartsWith("Yellow", StringComparison.OrdinalIgnoreCase))
            {
                Close();
                sectionTitle = first;
            }
        }
        Close();
        // The Rules sheet (written by Export to Excel): rule name in column A, its value in B, its key in C.
        if (sheets.FirstOrDefault(p => p.Key.Equals("Rules", StringComparison.OrdinalIgnoreCase)).Value is { } ruleRows)
            foreach (var (_, cells) in ruleRows)
            {
                var key = Cell(cells, 2);
                var rule = RuleDefs.FirstOrDefault(r => r.Key == key) ?? RuleDefs.FirstOrDefault(r => r.Label.Equals(Cell(cells, 0), StringComparison.OrdinalIgnoreCase));
                if (rule is not null) def.Rules[rule.Key] = Cell(cells, 1);
            }
        if (def.Title.Length == 0) def.Title = "Pre-submission checklist";
        if (def.Signatories.Count == 0) def.Signatories = ["Prepared by", "Approved by"];
        if (!def.Sections.Any())
            throw new InvalidDataException("the sheet has the table header but no numbered checks under a section row.");
        return def;
    }

    private static int HeaderRow(List<(int Row, Dictionary<int, string> Cells)> rows)
    {
        for (var i = 0; i < rows.Count && i < 60; i++)
        {
            var values = rows[i].Cells.Values.Select(v => v.Trim()).ToList();
            if (values.Contains("#") && values.Any(v => v.StartsWith("Check", StringComparison.OrdinalIgnoreCase))) return i;
        }
        return -1;
    }

    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PkgRel = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>Every sheet's non-empty cells as text (shared and inline strings, numbers, cached formula results), by sheet name.</summary>
    internal static Dictionary<string, List<(int Row, Dictionary<int, string> Cells)>> ReadSheets(Stream xlsx)
    {
        using var zip = new ZipArchive(xlsx, ZipArchiveMode.Read);
        XDocument Load(string name)
        {
            var entry = zip.GetEntry(name) ?? throw new InvalidDataException($"the workbook has no {name}");
            using var s = entry.Open();
            return XDocument.Load(s);
        }
        var shared = new List<string>();
        if (zip.GetEntry("xl/sharedStrings.xml") is not null)
            foreach (var si in Load("xl/sharedStrings.xml").Root!.Elements(Main + "si"))
                shared.Add(string.Concat(si.Descendants(Main + "t").Select(t => t.Value)));

        var rels = Load("xl/_rels/workbook.xml.rels").Root!.Elements(PkgRel + "Relationship")
            .ToDictionary(r => (string)r.Attribute("Id")!, r => (string)r.Attribute("Target")!);
        var result = new Dictionary<string, List<(int, Dictionary<int, string>)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var sheet in Load("xl/workbook.xml").Root!.Element(Main + "sheets")!.Elements(Main + "sheet"))
        {
            var name = (string?)sheet.Attribute("name") ?? "";
            var rid = (string?)sheet.Attribute(RelNs + "id") ?? "";
            if (!rels.TryGetValue(rid, out var target)) continue;
            target = target.StartsWith("/", StringComparison.Ordinal) ? target.TrimStart('/') : "xl/" + target;
            if (zip.GetEntry(target) is null) continue;
            var rows = new List<(int, Dictionary<int, string>)>();
            foreach (var row in Load(target).Descendants(Main + "row"))
            {
                var cells = new Dictionary<int, string>();
                foreach (var c in row.Elements(Main + "c"))
                {
                    var col = Column((string?)c.Attribute("r") ?? "");
                    if (col < 0) continue;
                    var type = (string?)c.Attribute("t") ?? "";
                    string text = type switch
                    {
                        "s" => int.TryParse(c.Element(Main + "v")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && i >= 0 && i < shared.Count ? shared[i] : "",
                        "inlineStr" => string.Concat(c.Descendants(Main + "t").Select(t => t.Value)),
                        _ => c.Element(Main + "v")?.Value ?? "",
                    };
                    if (text.Trim().Length > 0) cells[col] = text;
                }
                if (cells.Count > 0) rows.Add((int.TryParse((string?)row.Attribute("r"), out var r) ? r : rows.Count + 1, cells));
            }
            result[name] = rows;
        }
        return result;
    }

    /// <summary>"C12" is column 2 (zero-based).</summary>
    internal static int Column(string reference)
    {
        var col = 0;
        var any = false;
        foreach (var ch in reference)
        {
            if (ch is >= 'A' and <= 'Z') { col = col * 26 + (ch - 'A' + 1); any = true; }
            else if (ch is >= 'a' and <= 'z') { col = col * 26 + (ch - 'a' + 1); any = true; }
            else break;
        }
        return any ? col - 1 : -1;
    }

    // ---- Saved files ----------------------------------------------------------------------------------------------

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string ToJson<T>(T value) => JsonSerializer.Serialize(value, Json);

    public static T? FromJson<T>(string text) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(text); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException) { return null; }
    }

    /// <summary>A file name for the model's draft and Word file: the model's title without characters Windows refuses.</summary>
    public static string SafeName(string title)
    {
        var name = new StringBuilder();
        foreach (var ch in (title ?? "").Trim())
            name.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), ch) >= 0 || ch is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : ch);
        var s = name.ToString().Trim('.', ' ');
        if (s.Length > 80) s = s.Substring(0, 80);
        return s.Length == 0 ? "Untitled" : s;
    }
}
