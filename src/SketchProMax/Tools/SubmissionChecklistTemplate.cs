using System.Globalization;
using System.IO;

namespace SketchProMax.Tools;

/// <summary>
/// The submission checklist's questions template: the full list of questions every project starts from. Built in
/// (SubmissionChecklistBank) until the template's owner changes it; only the owner's PC can (LicenseState.IsOwnerPc),
/// everyone else answers. A changed template travels three ways, the newest (by <see cref="Template.At"/>) winning:
/// in the model (each person's record carries the template they use, see SubmissionChecklistMath.UserRecord), in a
/// shared tools pack (Sharing/SharedPacks), and as an Excel file the owner exports, edits and imports again. Answers are
/// kept by question id, so an edited question keeps its answers and a deleted one just stops being asked.
/// No Revit or WPF here, so the tests compile it.
/// </summary>
internal static class SubmissionChecklistTemplate
{
    /// <summary>One question of the template (see SubmissionChecklistMath.Question for the fields).</summary>
    public sealed class Item
    {
        public string Id { get; set; } = "";
        public string Topic { get; set; } = "";
        public string Text { get; set; } = "";
        public string Details { get; set; } = "";
        public string Types { get; set; } = "";
        public string Teams { get; set; } = "";
        public string Diagram { get; set; } = "";
    }

    /// <summary>The template: its questions in page order, when it was last changed (UTC) and by whom.</summary>
    public sealed class Template
    {
        public DateTime At { get; set; }
        public string By { get; set; } = "";
        public List<Item> Questions { get; set; } = [];

        /// <summary>The built-in questions, never changed.</summary>
        public bool BuiltIn => At == DateTime.MinValue;
    }

    /// <summary>The built-in template: the question bank as it ships.</summary>
    public static Template BuiltIn() => new()
    {
        At = DateTime.MinValue,
        Questions = SubmissionChecklistBank.Questions.Select(q => new Item
        {
            Id = q.Id, Topic = q.Topic, Text = q.Text, Details = q.Details, Types = q.Types, Teams = q.Teams, Diagram = q.Diagram,
        }).ToList(),
    };

    /// <summary>The template's questions, ready to ask (the built-in ones when there is no template or it is empty).</summary>
    public static IReadOnlyList<SubmissionChecklistMath.Question> QuestionsOf(Template? template)
    {
        if (template is null || template.BuiltIn || template.Questions.Count == 0) return SubmissionChecklistBank.Questions;
        return template.Questions.Where(i => i.Id.Trim().Length > 0 && i.Text.Trim().Length > 0)
            .GroupBy(i => i.Id.Trim(), StringComparer.Ordinal).Select(g => g.First())
            .Select(i => new SubmissionChecklistMath.Question(i.Id.Trim(), SubmissionChecklistMath.TopicOf(i.Topic).Key, i.Text.Trim(),
                (i.Details ?? "").Trim(), Clean(i.Types), (i.Teams ?? "").Trim(), (i.Diagram ?? "").Trim()))
            .ToList();
    }

    private static string Clean(string? types) =>
        string.Join(",", SubmissionChecklistMath.Split(types ?? "").Where(t => SubmissionChecklistMath.TypeOf(t) is not null));

    /// <summary>The newest of some templates (null ones left out); null when there are none.</summary>
    public static Template? Newest(params Template?[] templates) =>
        templates.Where(t => t is not null && !t.BuiltIn && t.Questions.Count > 0).OrderBy(t => t!.At).LastOrDefault();

    /// <summary>A new question's id: "tpl." and the time, with a counter for several made in the same moment.</summary>
    public static string NewId(DateTime now, IEnumerable<string> taken)
    {
        var used = new HashSet<string>(taken, StringComparer.Ordinal);
        var stem = "tpl." + now.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
        var id = stem;
        for (var i = 2; used.Contains(id); i++) id = stem + "." + i.ToString(CultureInfo.InvariantCulture);
        return id;
    }

    public static Template Copy(Template t) => SubmissionChecklistMath.FromJson<Template>(SubmissionChecklistMath.ToJson(t)) ?? new Template();

    /// <summary>A template read back from JSON: nulls made empty. Null when it isn't one.</summary>
    public static Template? Read(string? json)
    {
        var t = SubmissionChecklistMath.FromJson<Template>(json ?? "");
        if (t is null) return null;
        t.By ??= "";
        t.Questions ??= [];
        foreach (var i in t.Questions)
        {
            i.Id ??= ""; i.Topic ??= ""; i.Text ??= ""; i.Details ??= ""; i.Types ??= ""; i.Teams ??= ""; i.Diagram ??= "";
        }
        t.Questions.RemoveAll(i => i.Text.Trim().Length == 0);
        return t;
    }

    // ---- On this PC ---------------------------------------------------------------------------------------------------

    /// <summary>%APPDATA%\SketchProMax\submission checklists\template.json: the owner's template, or the newest one this PC was given.</summary>
    public static string PcPath => Path.Combine(SubmissionChecklistMath.Folder, "template.json");

    public static Template? LoadPc()
    {
        try { return File.Exists(PcPath) ? Read(File.ReadAllText(PcPath)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Keeps a template on the PC (the built-in one removes the file). Returns null when saved, else why not.</summary>
    public static string? SavePc(Template template)
    {
        try
        {
            if (template.BuiltIn) { if (File.Exists(PcPath)) File.Delete(PcPath); }
            else SafeFile.Write(PcPath, SubmissionChecklistMath.ToJson(template));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ex.Message; }
    }

    /// <summary>Keeps <paramref name="template"/> on this PC when it is newer than the one here. True when it was.</summary>
    public static bool TakeIfNewer(Template? template)
    {
        if (template is null || template.BuiltIn || template.Questions.Count == 0) return false;
        var here = LoadPc();
        if (here is not null && here.At >= template.At) return false;
        return SavePc(template) is null;
    }

    // ---- Excel --------------------------------------------------------------------------------------------------------

    private static readonly string[] Columns = ["Id", "Page", "Question", "Details", "Structure types", "Agree with (teams)", "Sketch"];
    private const string SheetName = "Questions";

    /// <summary>The template as a workbook: the Questions sheet to edit, and the pages and structure types it may name.</summary>
    public static void WriteXlsx(Stream output, Template template, DateTime now)
    {
        var rows = template.Questions.Select(i => new OfficeXlsx.Row(
        [
            i.Id, SubmissionChecklistMath.TopicOf(i.Topic).Title, i.Text, i.Details,
            string.Join("; ", SubmissionChecklistMath.Split(i.Types).Select(k => SubmissionChecklistMath.TypeOf(k)?.Name ?? k)),
            i.Teams, DiagramName(i.Diagram),
        ])).ToList();
        var questions = new OfficeXlsx.Sheet(SheetName,
            "Submission checklist questions. Edit, add or delete rows, keep the heading row, then import it again (a new row needs no Id).",
            Columns, rows, ["text", "text", "text", "text", "text", "text", "text"], [16, 26, 60, 70, 34, 22, 18], true);
        var pages = new OfficeXlsx.Sheet("Pages", "The pages a question can be on (Page column).", ["Page", "What it covers"],
            SubmissionChecklistMath.Topics.Select(t => new OfficeXlsx.Row([t.Title, t.About])).ToList(), ["text", "text"], [32, 80], false);
        var types = new OfficeXlsx.Sheet("Structure types",
            "Structure types (Structure types column, separated by semicolons; empty: every project). On the Structure checks page a question needs at least one.",
            ["Structure type", "What it is"], SubmissionChecklistMath.Types.Select(t => new OfficeXlsx.Row([t.Name, t.About])).ToList(), ["text", "text"], [32, 90], false);
        var sketches = new OfficeXlsx.Sheet("Sketches", "Sketches a question can show with its details (Sketch column; empty: its structure's own).",
            ["Sketch"], SubmissionChecklistDiagrams.All.Select(d => new OfficeXlsx.Row([d.Title])).ToList(), ["text"], [40], false);
        OfficeXlsx.Write(output, "Submission checklist questions", [questions, pages, types, sketches], now);
    }

    private static string DiagramName(string key) => key.Length == 0 ? "" : SubmissionChecklistDiagrams.Find(key)?.Title ?? key;

    /// <summary>
    /// A template read from a workbook <see cref="WriteXlsx"/> wrote (and the owner edited): rows in order, pages and
    /// types by name or key, a missing or repeated Id given a new one. Returns the template and what was set aside, or
    /// null and why it can't be read.
    /// </summary>
    public static (Template? Template, List<string> Notes, string Error) ReadXlsx(string path, DateTime now, string by)
    {
        XlsxBook book;
        try { book = XlsxBook.Read(path); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { return (null, [], ex.Message); }
        using (book)
        {
            var sheet = book.SheetNames.FirstOrDefault(n => string.Equals(n, SheetName, StringComparison.OrdinalIgnoreCase)) ?? book.SheetNames.FirstOrDefault();
            if (sheet is null) return (null, [], "the workbook has no sheets");
            var cells = book.Cells(sheet).ToList();
            return FromCells(cells.Select(c => (c.Col, c.Row, c.Text)), now, by);
        }
    }

    /// <summary>The template in the cells of a Questions sheet (see <see cref="ReadXlsx"/>); pure, for the tests.</summary>
    public static (Template? Template, List<string> Notes, string Error) FromCells(IEnumerable<(int Col, int Row, string Text)> cells, DateTime now, string by)
    {
        var notes = new List<string>();
        var grid = cells.GroupBy(c => c.Row).ToDictionary(g => g.Key, g => g.ToDictionary(c => c.Col, c => (c.Text ?? "").Trim()));
        // The heading row: the first one with a "Question" cell.
        var head = grid.OrderBy(r => r.Key).FirstOrDefault(r => r.Value.Values.Any(v => string.Equals(v, "Question", StringComparison.OrdinalIgnoreCase)));
        if (head.Value is null) return (null, notes, "it has no heading row with a Question column (export the questions to see the layout)");
        int Col(params string[] names) => head.Value.Where(kv => names.Any(n => kv.Value.StartsWith(n, StringComparison.OrdinalIgnoreCase))).Select(kv => kv.Key).DefaultIfEmpty(-1).First();
        int id = Col("Id"), page = Col("Page"), text = Col("Question"), details = Col("Details"), types = Col("Structure"), teams = Col("Agree", "Teams"), sketch = Col("Sketch");
        string At(Dictionary<int, string> row, int col) => col >= 0 && row.TryGetValue(col, out var v) ? v : "";
        var template = new Template { At = now, By = by };
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in grid.Where(r => r.Key > head.Key).OrderBy(r => r.Key))
        {
            var (rowNumber, row) = (entry.Key, entry.Value);
            var q = At(row, text);
            if (q.Length == 0)
            {
                if (row.Values.Any(v => v.Length > 0)) notes.Add($"Row {rowNumber}: no question, left out");
                continue;
            }
            var topic = TopicFrom(At(row, page));
            if (topic is null)
            {
                notes.Add($"Row {rowNumber}: page \"{At(row, page)}\" isn't one of the pages, so it went on {SubmissionChecklistMath.Topics[0].Title}");
                topic = SubmissionChecklistMath.Topics[0].Key;
            }
            var typeKeys = new List<string>();
            var (found, unknown) = TypesIn(At(row, types));
            typeKeys.AddRange(found);
            foreach (var name in unknown) notes.Add($"Row {rowNumber}: structure type \"{name}\" isn't known, left out");
            if (topic == SubmissionChecklistMath.TopicTypes && typeKeys.Count == 0)
            {
                notes.Add($"Row {rowNumber}: a Structure checks question needs a structure type, so it went on {SubmissionChecklistMath.TopicOf("basis").Title}");
                topic = "basis";
            }
            var diagram = "";
            if (At(row, sketch) is { Length: > 0 } s)
            {
                diagram = DiagramFrom(s) ?? "";
                if (diagram.Length == 0) notes.Add($"Row {rowNumber}: sketch \"{s}\" isn't known, so it shows its structure's own");
            }
            var qid = At(row, id);
            if (qid.Length == 0 || !ids.Add(qid))
            {
                if (qid.Length > 0) notes.Add($"Row {rowNumber}: Id {qid} is used above, so it is a new question");
                qid = NewId(now, ids);
                ids.Add(qid);
            }
            template.Questions.Add(new Item { Id = qid, Topic = topic, Text = q, Details = At(row, details), Types = string.Join(",", typeKeys), Teams = At(row, teams), Diagram = diagram });
        }
        if (template.Questions.Count == 0) return (null, notes, "it has no questions under the heading row");
        // Pages in their order; questions keep their order within a page.
        var order = SubmissionChecklistMath.Topics.Select((t, i) => (t.Key, i)).ToDictionary(x => x.Key, x => x.i);
        template.Questions = template.Questions.Select((q, i) => (q, i)).OrderBy(x => order.TryGetValue(x.q.Topic, out var o) ? o : 99).ThenBy(x => x.i).Select(x => x.q).ToList();
        return (template, notes, "");
    }

    private static string Plain(string s) => new string((s ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    public static string? TopicFrom(string text) =>
        SubmissionChecklistMath.Topics.FirstOrDefault(t => Plain(t.Title) == Plain(text) || Plain(t.Key) == Plain(text))?.Key;

    /// <summary>
    /// The structure types named in a cell: full names first (a name may hold a comma),
    /// then keys or names separated by commas or semicolons. Returns the keys found, in the cell's order, and what is left unknown.
    /// </summary>
    public static (List<string> Keys, List<string> Unknown) TypesIn(string cell)
    {
        var rest = " " + (cell ?? "") + " ";
        var found = new List<(int At, string Key)>();
        foreach (var t in SubmissionChecklistMath.Types.OrderByDescending(t => t.Name.Length))
        {
            var at = rest.IndexOf(t.Name, StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;
            found.Add((at, t.Key));
            rest = rest.Substring(0, at) + new string(';', t.Name.Length) + rest.Substring(at + t.Name.Length);
        }
        var unknown = new List<string>();
        var offset = 0;
        foreach (var part in rest.Split([',', ';']))
        {
            var name = part.Trim();
            if (name.Length > 0)
            {
                if (TypeFrom(name) is { } key) found.Add((offset, key));
                else unknown.Add(name);
            }
            offset += part.Length + 1;
        }
        return (found.OrderBy(f => f.At).Select(f => f.Key).Distinct().ToList(), unknown);
    }

    public static string? TypeFrom(string text) =>
        SubmissionChecklistMath.Types.FirstOrDefault(t => Plain(t.Name) == Plain(text) || Plain(t.Key) == Plain(text))?.Key;

    public static string? DiagramFrom(string text) =>
        SubmissionChecklistDiagrams.All.FirstOrDefault(d => Plain(d.Title) == Plain(text) || Plain(d.Key) == Plain(text))?.Key;
}
