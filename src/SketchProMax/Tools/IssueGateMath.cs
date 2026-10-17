using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SketchProMax.Tools;

/// <summary>
/// The zero-mistake issue gate's rules, which need no Revit (so the tests run them): findings of check_drawings,
/// spec_check, boq_check and check_model grouped per sheet of the issue set (model-wide ones under "Model"), the key an
/// acceptance is kept under, whether the issue is blocked, and the rows of the pass/fail report and the transmittal.
///
/// An item blocks the issue until it is fixed (gone on the next check) or accepted with a reason. Acceptances are kept
/// per model under the item's key: the tool, the check, the group, the element and the issue text (case and spaces
/// ignored). When any of these changes (a different value, more duplicates, another sheet) the key changes, so the
/// item comes back as open.
/// </summary>
internal static class IssueGateMath
{
    public const string ModelGroup = "Model";

    /// <summary>The checks the gate runs, in order.</summary>
    public static readonly string[] Tools = ["check_drawings", "spec_check", "boq_check", "check_model"];

    /// <summary>
    /// One finding of a check. <see cref="Sheets"/>: the sheet numbers it is on, through the views on them (empty: model-wide).
    /// </summary>
    public sealed record Finding(string Tool, string Check, long ElementId, string What, string Issue)
    {
        public IReadOnlyList<string> Sheets { get; init; } = [];
    }

    /// <summary>A sheet of the issue set.</summary>
    public sealed record Sheet(string Number, string Name, long Id = 0);

    /// <summary>An item accepted with a reason, kept with the model (JSON).</summary>
    public sealed class Accepted
    {
        public string Key { get; set; } = "";
        public string Tool { get; set; } = "";
        public string Check { get; set; } = "";
        public string Group { get; set; } = "";
        public long ElementId { get; set; }
        public string Issue { get; set; } = "";
        public string Reason { get; set; } = "";
        public string By { get; set; } = "";
        public string At { get; set; } = "";
    }

    public enum Status { Open, Accepted }

    public sealed record Item(Finding Finding, string Group, string Key, Status Status, Accepted? Acceptance);

    /// <summary>One group of the pass/fail list: a sheet of the issue set, or the Model group.</summary>
    public sealed class GroupResult
    {
        public string Group { get; init; } = "";
        public string Name { get; init; } = "";
        public long SheetId { get; init; }
        public List<Item> Items { get; init; } = [];
        public int Open => Items.Count(i => i.Status == Status.Open);
        public int AcceptedCount => Items.Count(i => i.Status == Status.Accepted);
        public bool Pass => Open == 0;
        public string Title => Group == ModelGroup ? "Model (not on one sheet)" : $"{Group} {Name}".Trim();

        /// <summary>For one check: "pass", "N open" or "pass (N accepted)".</summary>
        public string CheckResult(string tool)
        {
            var mine = Items.Where(i => i.Finding.Tool == tool).ToList();
            var open = mine.Count(i => i.Status == Status.Open);
            if (open > 0) return $"{open} open";
            return mine.Count > 0 ? $"pass ({mine.Count} accepted)" : "pass";
        }
    }

    /// <summary>Lower case, single spaces: the issue text as compared for a key.</summary>
    public static string Normalize(string? text) =>
        string.Join(" ", (text ?? "").Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>The acceptance key: 16 hex characters of a hash of tool, check, group, element and issue text.</summary>
    public static string Key(string tool, string check, string group, long elementId, string issue)
    {
        var text = string.Join("\u001f", tool, check, group, elementId.ToString(CultureInfo.InvariantCulture), Normalize(issue));
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
        return string.Concat(hash.Take(8).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// The pass/fail list: one group per sheet of the issue set (in its order, passing ones too) and the Model group last.
    /// A finding goes to each of its sheets that is in the issue set; one on sheets outside the set is left out (it isn't
    /// being issued); one on no sheet goes to Model.
    /// </summary>
    public static List<GroupResult> Group(IEnumerable<Finding> findings, IReadOnlyList<Sheet> issueSet, IEnumerable<Accepted> accepted)
    {
        var byKey = new Dictionary<string, Accepted>(StringComparer.Ordinal);
        foreach (var a in accepted) if (a.Key.Length > 0) byKey[a.Key] = a;
        var groups = issueSet.Select(s => new GroupResult { Group = s.Number, Name = s.Name, SheetId = s.Id }).ToList();
        var model = new GroupResult { Group = ModelGroup, Name = "" };
        var index = groups.GroupBy(g => g.Group, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var f in findings)
        {
            var targets = f.Sheets.Count == 0 ? new List<GroupResult> { model } : f.Sheets.Distinct(StringComparer.Ordinal).Where(index.ContainsKey).Select(n => index[n]).ToList();
            foreach (var g in targets)
            {
                var key = Key(f.Tool, f.Check, g.Group, f.ElementId, f.Issue);
                if (g.Items.Any(i => i.Key == key)) continue; // the same finding twice (e.g. a duplicate warning) is one item
                var hit = byKey.TryGetValue(key, out var a) ? a : null;
                g.Items.Add(new Item(f, g.Group, key, hit is null ? Status.Open : Status.Accepted, hit));
            }
        }
        groups.Add(model);
        return groups;
    }

    /// <summary>True while any item is open: the issue can't go ahead.</summary>
    public static bool Blocked(IEnumerable<GroupResult> groups) => groups.Any(g => g.Open > 0);

    /// <summary>
    /// Whether the issue may go ahead: when nothing is open; or, with open items, only when the setting "allow issue with
    /// open items" is on, the user asked for it and gave a reason. Why not, otherwise.
    /// </summary>
    public static (bool Allowed, bool Override, string Why) CanIssue(IReadOnlyList<GroupResult> groups, bool settingOn, bool asked, string? reason)
    {
        var open = groups.Sum(g => g.Open);
        if (open == 0) return (true, false, "Every item is fixed or accepted.");
        if (!asked) return (false, false, $"{open} open item(s): fix each one (then check again) or accept it with a reason.");
        if (!settingOn)
            return (false, false, $"{open} open item(s), and issuing with open items is off in Settings (\"Allow issue with open items\"). Fix or accept them.");
        if (string.IsNullOrWhiteSpace(reason)) return (false, false, "Say why the issue goes ahead with open items; the reason is recorded in the issue record.");
        return (true, true, $"Issued with {open} open item(s); recorded in the issue record.");
    }

    /// <summary>
    /// Accepts <paramref name="items"/> with <paramref name="reason"/>: added to (or replacing in) the model's list, and
    /// returned as accepted. A reason is required.
    /// </summary>
    public static int Accept(List<Accepted> store, IEnumerable<Item> items, string reason, string by, DateTime when)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Give a reason for accepting.");
        var n = 0;
        foreach (var i in items)
        {
            store.RemoveAll(a => a.Key == i.Key);
            store.Add(new Accepted
            {
                Key = i.Key, Tool = i.Finding.Tool, Check = i.Finding.Check, Group = i.Group, ElementId = i.Finding.ElementId,
                Issue = i.Finding.Issue, Reason = reason.Trim(), By = by, At = when.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            });
            n++;
        }
        return n;
    }

    /// <summary>Puts accepted items back to open (forgets their acceptance). Returns how many were forgotten.</summary>
    public static int Unaccept(List<Accepted> store, IEnumerable<string> keys)
    {
        var set = new HashSet<string>(keys, StringComparer.Ordinal);
        return store.RemoveAll(a => set.Contains(a.Key));
    }

    /// <summary>The groups again with the store's acceptances (after an accept, without checking again).</summary>
    public static List<GroupResult> Regroup(IReadOnlyList<GroupResult> groups, IEnumerable<Accepted> accepted)
    {
        var byKey = new Dictionary<string, Accepted>(StringComparer.Ordinal);
        foreach (var a in accepted) byKey[a.Key] = a;
        return groups.Select(g => new GroupResult
        {
            Group = g.Group, Name = g.Name, SheetId = g.SheetId,
            Items = g.Items.Select(i => byKey.TryGetValue(i.Key, out var a) ? i with { Status = Status.Accepted, Acceptance = a } : i with { Status = Status.Open, Acceptance = null }).ToList(),
        }).ToList();
    }

    /// <summary>One line: blocked with the open items per group, or clear.</summary>
    public static string Verdict(IReadOnlyList<GroupResult> groups)
    {
        var open = groups.Where(g => g.Open > 0).ToList();
        var sheets = groups.Count(g => g.Group != ModelGroup);
        var accepted = groups.Sum(g => g.AcceptedCount);
        if (open.Count == 0)
            return $"Clear to issue: {sheets} sheet(s) pass" + (accepted > 0 ? $", {accepted} item(s) accepted with a reason." : ", nothing found.");
        var parts = open.Take(8).Select(g => $"{g.Group}: {g.Open}").ToList();
        if (open.Count > 8) parts.Add($"{open.Count - 8} more group(s)");
        return $"Blocked: {open.Sum(g => g.Open)} open item(s) ({string.Join(", ", parts)}). Fix each one and check again, or accept it with a reason.";
    }

    // ---- Report rows (Excel) ---------------------------------------------------------------------------------------

    /// <summary>The pass/fail list: one row per group with its result per check.</summary>
    public static IList<object?[]> SheetRows(IReadOnlyList<GroupResult> groups)
    {
        var rows = new List<object?[]> { new object?[] { "Sheet", "Name", "Result", "Open", "Accepted" }.Concat(Tools).ToArray() };
        foreach (var g in groups)
            rows.Add(new object?[] { g.Group, g.Group == ModelGroup ? "Model-wide findings" : g.Name, g.Pass ? "PASS" : "FAIL", g.Open, g.AcceptedCount }
                .Concat(Tools.Select(t => (object?)g.CheckResult(t))).ToArray());
        return rows;
    }

    /// <summary>Every item, open first, with its acceptance when it has one.</summary>
    public static IList<object?[]> FindingRows(IReadOnlyList<GroupResult> groups)
    {
        var rows = new List<object?[]> { new object?[] { "Sheet", "Status", "Tool", "Check", "Element id", "Element / where", "Issue", "Reason accepted", "Accepted by", "Accepted on", "Key" } };
        foreach (var g in groups)
            foreach (var i in g.Items.OrderBy(i => i.Status))
                rows.Add([g.Group, i.Status == Status.Open ? "OPEN" : "accepted", i.Finding.Tool, i.Finding.Check, i.Finding.ElementId > 0 ? i.Finding.ElementId : null,
                    i.Finding.What, i.Finding.Issue, i.Acceptance?.Reason, i.Acceptance?.By, i.Acceptance?.At, i.Key]);
        return rows;
    }

    /// <summary>The pass/fail report's sheets: by sheet, every item, and how each check ran.</summary>
    public static List<(string Name, IList<object?[]> Rows)> ReportSheets(IReadOnlyList<GroupResult> groups, IEnumerable<(string Tool, string Outcome)> checks)
    {
        var ran = new List<object?[]> { new object?[] { "Check", "How it ran" } };
        ran.AddRange(checks.Select(c => new object?[] { c.Tool, c.Outcome }));
        return [("Pass-fail by sheet", SheetRows(groups)), ("Findings", FindingRows(groups)), ("Checks run", ran)];
    }

    /// <summary>What an issue records: when, which revision, the gate's state and whether open items were let through.</summary>
    public sealed class IssueRecord
    {
        public string Date { get; set; } = "";
        public string Model { get; set; } = "";
        public string Revision { get; set; } = "";
        public int Sheets { get; set; }
        public string Gate { get; set; } = "";
        public int OpenItems { get; set; }
        public int AcceptedItems { get; set; }
        public bool OverrideUsed { get; set; }
        public string OverrideReason { get; set; } = "";
        public string IssuedBy { get; set; } = "";
        public string Folder { get; set; } = "";
    }

    /// <summary>The transmittal workbook: the drawings issued, the issue record, then the pass/fail report.</summary>
    public static List<(string Name, IList<object?[]> Rows)> TransmittalSheets(IssueRecord record, IEnumerable<(string Number, string Name, string Revision, string Files)> drawings,
        IReadOnlyList<GroupResult> groups, IEnumerable<(string Tool, string Outcome)> checks)
    {
        var list = new List<object?[]> { new object?[] { "Sheet number", "Sheet name", "Revision", "Issue date", "Files" } };
        list.AddRange(drawings.Select(d => new object?[] { d.Number, d.Name, d.Revision, record.Date, d.Files }));
        var rec = new List<object?[]>
        {
            new object?[] { "Field", "Value" },
            new object?[] { "Model", record.Model }, new object?[] { "Issue date", record.Date }, new object?[] { "Revision", record.Revision },
            new object?[] { "Sheets", record.Sheets }, new object?[] { "Issue check", record.Gate },
            new object?[] { "Open items", record.OpenItems }, new object?[] { "Accepted items", record.AcceptedItems },
            new object?[] { "Issued with open items (override)", record.OverrideUsed ? "YES" : "no" },
        };
        if (record.OverrideUsed) rec.Add(["Override reason", record.OverrideReason]);
        rec.Add(["Issued by", record.IssuedBy]);
        rec.Add(["Folder", record.Folder]);
        var sheets = new List<(string, IList<object?[]>)> { ("Transmittal", list), ("Issue record", rec) };
        sheets.AddRange(ReportSheets(groups, checks));
        return sheets;
    }

    // ---- The chat's result -----------------------------------------------------------------------------------------

    /// <summary>A compact result for the chat: the verdict, each group with its open items (and their keys to accept).</summary>
    public static object Chat(IReadOnlyList<GroupResult> groups, int maxPerGroup = 15) => new
    {
        blocked = Blocked(groups),
        verdict = Verdict(groups),
        sheets = groups.Where(g => g.Group != ModelGroup || g.Items.Count > 0).Select(g => new
        {
            sheet = g.Title,
            result = g.Pass ? "pass" : "FAIL",
            open = g.Open,
            accepted = g.AcceptedCount,
            checks = Tools.ToDictionary(t => t, g.CheckResult),
            openItems = g.Items.Where(i => i.Status == Status.Open).Take(maxPerGroup)
                .Select(i => new { key = i.Key, check = $"{i.Finding.Tool}: {i.Finding.Check}", id = i.Finding.ElementId > 0 ? i.Finding.ElementId : (long?)null, where = i.Finding.What, issue = i.Finding.Issue }).ToList(),
            moreOpen = g.Open > maxPerGroup ? g.Open - maxPerGroup : (int?)null,
        }).ToList(),
    };
}

/// <summary>What the gate keeps per model (JSON in %APPDATA%\SketchProMax\issue-gate): acceptances, the inputs of the
/// checks that need a file, and a line per issue.</summary>
internal sealed class IssueGateStore
{
    public List<IssueGateMath.Accepted> Accepted { get; set; } = [];
    public string? SpecChecklist { get; set; }
    public string? BoqFile { get; set; }
    public List<IssueGateMath.IssueRecord> Issues { get; set; } = [];

    public static IssueGateStore Load(string path)
    {
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<IssueGateStore>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { }
        return new();
    }

    /// <summary>Writes through a temporary file, so a crash never leaves half of it.</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (Issues.Count > 200) Issues.RemoveRange(0, Issues.Count - 200);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        if (File.Exists(path)) File.Delete(path);
        File.Move(temp, path);
    }
}
