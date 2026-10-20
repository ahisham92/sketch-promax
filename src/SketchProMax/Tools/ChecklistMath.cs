using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SketchProMax.Tools;

/// <summary>
/// The pre-submission checklist (Issue > Pre-submission checklist, UI/ChecklistWindow): the checklist read from the
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

        [System.Text.Json.Serialization.JsonIgnore]
        public IEnumerable<Item> Items => Sections.SelectMany(s => s.Items);
    }

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
            SourcePath = def.SourcePath, SourceStamp = def.SourceStamp,
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

    /// <summary>The automatic check: checks the model can test (Tested), and how many of those it passes or fails.</summary>
    public sealed record AutoProgress(int Tested, int Pass, int Fail)
    {
        public int Percent => Tested == 0 ? 0 : (int)Math.Round(100.0 * Pass / Tested, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// The model's own verdict on the checks it can test: <paramref name="verdict"/> gives true (passes), false (fails) or
    /// null (the model can't tell) for a check's model rule. Separate from the answers: you can be at 100% while it is at 50%.
    /// </summary>
    public static AutoProgress Auto(IEnumerable<Item> items, Func<string, bool?> verdict)
    {
        int pass = 0, fail = 0;
        foreach (var item in items)
        {
            if (item.Auto.Length == 0) continue;
            switch (verdict(item.Auto))
            {
                case true: pass++; break;
                case false: fail++; break;
            }
        }
        return new AutoProgress(pass + fail, pass, fail);
    }

    /// <summary>Checks you answered Yes that the model check fails: worth a second look before signing.</summary>
    public static List<Item> Disagree(IEnumerable<Item> items, State state, Func<string, bool?> verdict) =>
        items.Where(i => i.Auto.Length > 0 && verdict(i.Auto) == false && state.Answers.TryGetValue(i.Id, out var a) && Normal(a.Done) == Yes).ToList();

    /// <summary>The automatic check in one line, for the Word file and the window ("Automatic model check: 6 of 12 passed (50%)").</summary>
    public static string AutoLine(AutoProgress auto, IReadOnlyCollection<Item> disagree)
    {
        if (auto.Tested == 0) return "";
        var line = $"Automatic model check: {auto.Pass} of {auto.Tested} checks the model can test passed ({auto.Percent}%).";
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
    /// The office's file name parts (D01C-070-00-ABC-002-STR-3DM-0010): the building code, the level field, the
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
                items.Add(new Item(id, num, text, Cell(cells, example), Cell(cells, from), Cell(cells, found), Cell(cells, priority), waiting, AutoKey(text)));
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
