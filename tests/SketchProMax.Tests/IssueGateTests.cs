using System.Text.Json;
using SketchProMax.Tools;
using static SketchProMax.Tools.IssueGateMath;

/// <summary>The zero-mistake issue gate's rules (Tools/IssueGateMath): grouping per sheet, acceptance keys, blocking, the report.</summary>
internal static class IssueGateTests
{
    public static void Run(Action<string, bool, string> check)
    {
        // ---- Keys
        var k = Key("check_drawings", "sheet_fields", "A-101", 42, "Missing: Drawn By.");
        check("issue gate: key is 16 hex characters", k.Length == 16 && k.All(c => Uri.IsHexDigit(c)), k);
        check("issue gate: key ignores case and spaces", k == Key("check_drawings", "sheet_fields", "A-101", 42, "  missing:   DRAWN by. "), "");
        check("issue gate: key changes with the issue text", k != Key("check_drawings", "sheet_fields", "A-101", 42, "Missing: Checked By."), "");
        check("issue gate: key changes with element, group and check",
            new[] { Key("check_drawings", "sheet_fields", "A-101", 43, "Missing: Drawn By."), Key("check_drawings", "sheet_fields", "A-102", 42, "Missing: Drawn By."),
                    Key("check_drawings", "empty_sheets", "A-101", 42, "Missing: Drawn By."), Key("check_model", "sheet_fields", "A-101", 42, "Missing: Drawn By.") }.All(x => x != k), "");

        // ---- Grouping
        List<Sheet> set = [new("A-101", "Plan", 1), new("A-102", "Sections", 2)];
        var onA = new Finding("check_drawings", "dimension_overrides", 10, "Level 1", "Dimension shows '500' but measures 450") { Sheets = ["A-101"] };
        var legend = new Finding("check_drawings", "dimension_overrides", 11, "Legend", "Dimension shows 'X' but measures 3") { Sheets = ["A-101", "A-102"] };
        var elsewhere = new Finding("check_drawings", "sheet_fields", 99, "Z-900 Other", "Missing: Drawn By.") { Sheets = ["Z-900"] };
        var model = new Finding("check_model", "wall_gaps", 7, "Walls Generic", "end 1 stops 50 mm short");
        var dup = new Finding("check_model", "warnings", 8, "Walls", "Highlighted walls overlap.");
        var groups = Group([onA, legend, elsewhere, model, dup, dup], set, []);
        check("issue gate: one group per sheet of the set, in order, then Model",
            groups.Select(g => g.Group).SequenceEqual(["A-101", "A-102", ModelGroup]), string.Join(",", groups.Select(g => g.Group)));
        check("issue gate: a finding goes to its sheet", groups[0].Items.Any(i => i.Finding == onA) && !groups[1].Items.Any(i => i.Finding == onA), "");
        check("issue gate: a view on two sheets puts its finding on both", groups[0].Items.Any(i => i.Finding == legend) && groups[1].Items.Any(i => i.Finding == legend), "");
        check("issue gate: a finding on a sheet outside the issue set is left out", groups.All(g => g.Items.All(i => i.Finding != elsewhere)), "");
        check("issue gate: model-wide findings go to Model, the same one once", groups[2].Items.Count == 2 && groups[2].Items.Count(i => i.Finding == dup) == 1, groups[2].Items.Count.ToString());
        check("issue gate: everything is open at first, so it blocks", Blocked(groups) && groups.All(g => g.Items.All(i => i.Status == Status.Open)), "");
        var clean = Group([model], set, []);
        check("issue gate: a sheet with nothing found passes", clean[0].Pass && clean[0].Items.Count == 0 && clean[0].CheckResult("check_drawings") == "pass", "");

        // ---- Accepting
        var store = new List<Accepted>();
        var threw = false;
        try { Accept(store, groups[2].Items, "  ", "me", DateTime.Now); } catch (ArgumentException) { threw = true; }
        check("issue gate: accepting needs a reason", threw && store.Count == 0, "");
        var n = Accept(store, groups[2].Items, "Known: approved by the engineer", "me", new DateTime(2026, 10, 17, 9, 30, 0));
        check("issue gate: accepted items are kept with reason, who and when",
            n == 2 && store.All(a => a.Reason == "Known: approved by the engineer" && a.By == "me" && a.At == "2026-10-17 09:30" && a.Group == ModelGroup), "");
        Accept(store, groups[2].Items.Take(1), "Second reason", "me", DateTime.Now);
        check("issue gate: accepting again replaces, not duplicates", store.Count == 2 && store.Count(a => a.Reason == "Second reason") == 1, "");
        var regrouped = Regroup(groups, store);
        check("issue gate: after accepting, Model passes and the sheets still block",
            regrouped[2].Pass && regrouped[2].AcceptedCount == 2 && Blocked(regrouped) && regrouped[0].Open == 2, "");
        var rerun = Group([onA, legend, model, dup], set, store);
        check("issue gate: acceptances apply on the next check", rerun[2].Pass, "");
        var changed = model with { Issue = "end 1 stops 80 mm short" };
        var rerun2 = Group([changed, dup], set, store);
        check("issue gate: a changed finding comes back open", rerun2[2].Items.Single(i => i.Finding == changed).Status == Status.Open && rerun2[2].Open == 1, "");
        Accept(store, rerun[0].Items.Concat(rerun[1].Items), "Drawing note explains it", "me", DateTime.Now);
        var all = Group([onA, legend, model, dup], set, store);
        check("issue gate: everything fixed or accepted clears it", !Blocked(all) && all.All(g => g.Pass), Verdict(all));
        check("issue gate: per-check result says accepted", all[0].CheckResult("check_drawings") == "pass (2 accepted)" && all[0].CheckResult("check_model") == "pass", all[0].CheckResult("check_drawings"));
        var reopened = Unaccept(store, [store[0].Key]);
        check("issue gate: unaccept opens an item again", reopened == 1 && Blocked(Group([onA, legend, model, dup], set, store)), "");

        // ---- Blocking and the override
        var blocked = Group([onA], set, []);
        check("issue gate: clear issues without asking", CanIssue(all, false, false, null) is (true, false, _), "");
        check("issue gate: open items block by default", CanIssue(blocked, false, false, null) is (false, false, _), "");
        check("issue gate: asking with the setting off is refused", CanIssue(blocked, false, true, "deadline") is (false, false, var w1) && w1.Contains("Settings"), "");
        check("issue gate: the override needs a reason", CanIssue(blocked, true, true, " ") is (false, false, _), "");
        check("issue gate: the override with the setting on and a reason goes ahead, marked", CanIssue(blocked, true, true, "Client deadline") is (true, true, _), "");
        check("issue gate: the setting alone doesn't let open items through", CanIssue(blocked, true, false, "x") is (false, false, _), "");
        var verdict = Verdict(blocked);
        check("issue gate: verdict names the open items per sheet", verdict.StartsWith("Blocked: 1 open") && verdict.Contains("A-101: 1"), verdict);
        check("issue gate: clear verdict", Verdict(all).StartsWith("Clear to issue"), Verdict(all));

        // ---- Report, transmittal, chat
        var rows = SheetRows(blocked);
        check("issue gate: pass/fail rows have a column per check", rows[0].Length == 5 + Tools.Length && rows.Count == 4 && (string?)rows[1][2] == "FAIL" && (string?)rows[2][2] == "PASS", "");
        var findings = FindingRows(rerun2);
        check("issue gate: findings rows list open first with the key", findings.Count == 1 + rerun2.Sum(g => g.Items.Count) && (string?)findings[1][1] == "OPEN" && ((string?)findings[1][10])?.Length == 16, "");
        var record = new IssueRecord { Date = "2026-10-17", Model = "Test", Revision = "1 For construction", Sheets = 2, Gate = verdict, OpenItems = 1, OverrideUsed = true, OverrideReason = "Client deadline", IssuedBy = "me" };
        var tx = TransmittalSheets(record, [("A-101", "Plan", "1", "A-101 - Plan.pdf")], blocked, [("check_drawings", "ran: 1 finding(s)")]);
        var rec = tx.Single(t => t.Name == "Issue record").Rows;
        check("issue gate: transmittal has drawings, record and the pass/fail report",
            tx.Select(t => t.Name).SequenceEqual(["Transmittal", "Issue record", "Pass-fail by sheet", "Findings", "Checks run"]) && tx[0].Rows.Count == 2, string.Join(",", tx.Select(t => t.Name)));
        check("issue gate: the issue record says the override was used, and why",
            rec.Any(r => (string?)r[0] == "Issued with open items (override)" && (string?)r[1] == "YES") && rec.Any(r => (string?)r[0] == "Override reason" && (string?)r[1] == "Client deadline"), "");
        var chat = JsonSerializer.Serialize(Chat(blocked));
        check("issue gate: chat result has the verdict, the sheet and the key to accept",
            chat.Contains("\"blocked\":true") && chat.Contains("A-101 Plan") && chat.Contains(blocked[0].Items[0].Key) && !chat.Contains("\"Model (not on one sheet)\""), chat.Length > 300 ? chat.Substring(0, 300) : chat);

        // ---- The per-model file
        var path = Path.Combine(Path.GetTempPath(), "spm-issue-gate-" + Guid.NewGuid().ToString("N"), "model.json");
        try
        {
            new IssueGateStore { Accepted = store, SpecChecklist = "spec.json", Issues = [record] }.Save(path);
            var back = IssueGateStore.Load(path);
            check("issue gate: the per-model file round-trips", back.Accepted.Count == store.Count && back.Accepted[0].Key == store[0].Key && back.SpecChecklist == "spec.json" && back.Issues.Single().OverrideUsed, "");
            File.WriteAllText(path, "{ not json");
            check("issue gate: a broken file reads as empty", IssueGateStore.Load(path).Accepted.Count == 0, "");
            check("issue gate: a broken file is never saved over (acceptances and history kept)",
                IssueGateStore.Load(path, out var bad).Accepted.Count == 0 && bad && Throws(() => IssueGateStore.ForUpdate(path)) && File.ReadAllText(path) == "{ not json", "");
            File.Delete(path);
            check("issue gate: no file yet is an empty store to write", IssueGateStore.ForUpdate(path).Accepted.Count == 0, "");
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch (IOException) { } }

        // ---- Only the user clears the gate: accepting items and issuing with open items are always asked about
        static IReadOnlyDictionary<string, JsonElement> Args(string json) => JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        check("issue gate: accepting items or issuing with open items is always asked; a plain check or issue is not",
            OutputPaths.AlwaysAskReason("issue_check", Args("""{"accept":[{"key":"k","reason":"r"}]}""")) is not null
            && OutputPaths.AlwaysAskReason("issue_drawings", Args("""{"allow_open_items":true,"open_items_reason":"r"}""")) is not null
            && OutputPaths.AlwaysAskReason("issue_check", Args("""{"accept":[]}""")) is null
            && OutputPaths.AlwaysAskReason("issue_drawings", Args("""{"sheet_numbers":["A-101"]}""")) is null, "");
    }

    private static bool Throws(Action a)
    {
        try { a(); return false; }
        catch (IOException) { return true; }
    }
}
