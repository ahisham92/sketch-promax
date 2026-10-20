using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using SketchProMax.Tools;
using static SketchProMax.Tools.ChecklistMath;

/// <summary>
/// The pre-submission checklist (Tools/ChecklistMath, Tools/ChecklistDocx): reading the office's Excel layout, the
/// answers and readiness, the file names, and the Word file signed only when ready. The workbook here is made in the
/// test in the same layout; the built-in checklist (Data/pre-submission-checklist.json) is checked too, and the Excel
/// export (ChecklistXlsx) must import back unchanged.
/// </summary>
internal static class ChecklistTests
{
    public static void Run(Action<string, bool, string> check)
    {
        // ---- Reading the workbook
        var xlsx = Workbook(
            ("Read me", [[(0, "Demo pre-submission checklist, Rev 02 (19-Oct-2026, comments added)")]]),
            ("Checklist", [
                [(0, "Demo pre-submission checklist (one per model, per submission)")],
                [(0, "Model file name"), (2, "e.g. PRJ1-070-00-ABC-002-STR-3DM-0010.rvt")],
                [(0, "Building / discipline")],
                [(0, "Trade BIM coordinator")],
                [(0, "Progress"), (2, "=COUNTIF(G15:G78,\"Y\")")],
                [(0, "#"), (1, "Check (what to do)"), (2, "Example / how to check"), (3, "Where it came from"), (4, "Client/BIM lead found it on"), (5, "Priority"), (6, "Done (Y/N/NA)"), (9, "Notes")],
                [(0, "A. File names and size")],
                [(0, "1"), (1, "The working model name has 002 after ABC."), (2, "PRJ1-070-00-ABC-002-STR-3DM-0010.rvt"), (3, "Client BIM-02"), (4, "BLDG-01, 08"), (5, "High")],
                [(0, "2"), (1, "Level field: a model that covers many levels uses 00."), (5, "High"), (9, "Waiting: BIM lead, 00 or MM")],
                [(0, "B. Coordinates, levels, grids, worksets")],
                [(0, "3"), (1, "Grids are in the model and visible in plans."), (2, "The client rejected BLDG-01 because the STR file had no grids")],
                [(0, "4"), (1, "Every element is on its BIM standard workset. Nothing on Workset1."), (2, "Link worksets: Z_Link_URS, Z_Link_AR")],
                [(0, "Sign-off")],
                [(0, "Trade BIM coordinator: ____________________     Trade group leader: ____________________     Date: __________")],
                [(0, "Yellow rows wait on an answer.")],
            ]));
        var path = Path.Combine(Path.GetTempPath(), "spm-checklist-" + Guid.NewGuid().ToString("N") + ".xlsx");
        File.WriteAllBytes(path, xlsx);
        Definition def;
        try { def = ReadXlsx(path); }
        finally { File.Delete(path); }
        check("checklist: the title, with the revision from the read-me sheet", def.Title == "Demo pre-submission checklist, Rev 02", def.Title);
        check("checklist: header fields above the table, Progress left out, e.g. dropped",
            def.Fields.Select(f => f.Label).SequenceEqual(["Model file name", "Building / discipline", "Trade BIM coordinator"]) && def.Fields[0].Example == "PRJ1-070-00-ABC-002-STR-3DM-0010.rvt",
            string.Join(" | ", def.Fields.Select(f => f.Label + "=" + f.Example)));
        check("checklist: header fields know where they are filled from", def.Fields.Select(f => f.Source).SequenceEqual(["model_file", "building", "coordinator"]), string.Join(",", def.Fields.Select(f => f.Source)));
        check("checklist: sections become stages, with their checks", def.Sections.Count == 2 && def.Sections[0].Items.Count == 2 && def.Sections[1].Items.Count == 2,
            string.Join(" | ", def.Sections.Select(s => s.Title + ":" + s.Items.Count)));
        check("checklist: stages are details, sections, review", Stages(def).SequenceEqual(["Project details", "File names and size", "Coordinates, levels, grids, worksets", "Review and sign"]), string.Join(",", Stages(def)));
        var one = def.Items.First();
        check("checklist: a check keeps its columns", one is { Number: "1", Example: "PRJ1-070-00-ABC-002-STR-3DM-0010.rvt", From: "Client BIM-02", FoundOn: "BLDG-01, 08", High: true }, one.ToString());
        check("checklist: a Waiting note marks an open point", def.Items.ElementAt(1).Waiting == "BIM lead, 00 or MM" && one.Waiting == "", def.Items.ElementAt(1).Waiting);
        check("checklist: checks get their model hint by wording", def.Items.Select(i => i.Auto).SequenceEqual(["name002", "levelfield", "grids", "worksets"]), string.Join(",", def.Items.Select(i => i.Auto)));
        check("checklist: the sign-off line names who signs", def.Signatories.SequenceEqual(["Trade BIM coordinator", "Trade group leader"]), string.Join(",", def.Signatories));
        check("checklist: a workbook without the table is refused in plain words", Refused(Workbook(("Sheet1", [[(0, "Hello")]]))).Contains("header row"), "");
        check("checklist: a file that isn't a workbook is refused", Refused(Encoding.UTF8.GetBytes("not a zip")).Contains(".xlsx"), "");
        check("checklist: column letters", Column("A1") == 0 && Column("J14") == 9 && Column("AA3") == 26 && Column("12") == -1, "");

        // ---- Answers and readiness
        check("checklist: Y/N/NA in usual spellings", Normal(" yes ") == Yes && Normal("n") == No && Normal("N/A") == NotApplicable && Normal("na") == NotApplicable && Normal("maybe") == "", "");
        var state = new State();
        var p = Count(def.Items, state);
        check("checklist: nothing answered is not ready", p is { Total: 4, Open: 4, Ready: false }, p.ToString());
        foreach (var item in def.Items) state.For(item.Id).Done = Yes;
        p = Count(def.Items, state);
        check("checklist: an open point answered without a comment is counted", p.WaitingWithoutNote == 1 && p.Ready, p.ToString());
        check("checklist: the open point asks for a comment", Problems(def, state).Single().Text.Contains("waits on an answer"), string.Join(" / ", Problems(def, state).Select(x => x.Text)));
        state.For("2").Note = "Used 00 as the BIM lead said.";
        state.For("3").Done = No;
        p = Count(def.Items, state);
        check("checklist: a No is never ready, and asks why", !p.Ready && p.No == 1 && p.NoWithoutNote == 1 && Problems(def, state).Single().Text.Contains("say why"), p.ToString());
        state.For("3").Done = NotApplicable;
        check("checklist: all Y or NA is ready", Count(def.Items, state).Ready && Problems(def, state).Count == 0, "");

        // ---- A new revision of the checklist
        var revised = new State();
        revised.For("1").Done = Yes; revised.For("1").Check = "The working model name has 002 after ABC.";
        revised.For("3").Done = Yes; revised.For("3").Check = "Scope boxes are named per the cutsheets.";
        revised.For("4").Done = No;
        revised.For("99").Done = Yes;
        var dropped = Reconcile(def, revised);
        check("checklist: a reworded or removed check loses its answer, the rest keep theirs",
            dropped == 2 && revised.Answers.ContainsKey("1") && revised.Answers.ContainsKey("4") && !revised.Answers.ContainsKey("3") && !revised.Answers.ContainsKey("99")
            && revised.Answers["4"].Check == def.Items.Single(i => i.Id == "4").Check, $"{dropped} dropped: {string.Join(",", revised.Answers.Keys)}");

        // ---- File names
        var parts = NameParts("PRJ1-070-00-ABC-002-STR-3DM-0010_detached.rvt");
        check("checklist: file name parts", parts == ("070", "00", "STR", "3DM", true), parts.ToString());
        check("checklist: a name without 002 says so", !NameParts("PRJ1-070-00-ABC-STR-3DM-0010.rvt").Has002 && NameParts("PRJ1-070-00-ABC-STR-3DM-0010.rvt").DocType == "3DM", "");
        check("checklist: building code to name", BuildingName("070") == "BLDG-07" && BuildingName("150") == "BLDG-15" && BuildingName("7") == "", "");
        check("checklist: working name drops _detached", WorkingName("PRJ1-070-00-ABC-002-STR-3DM-0010_detached") == "PRJ1-070-00-ABC-002-STR-3DM-0010.rvt", WorkingName("PRJ1-070-00-ABC-002-STR-3DM-0010_detached"));
        check("checklist: first submission adds _S03_R00", SubmissionName("PRJ1-070-00-ABC-002-STR-3DM-0010.rvt") == "PRJ1-070-00-ABC-002-STR-3DM-0010_S03_R00.rvt", "");
        check("checklist: a resubmission moves the revision on", SubmissionName("PRJ1-070-00-ABC-002-STR-3DM-0010_S03_R01.rvt") == "PRJ1-070-00-ABC-002-STR-3DM-0010_S03_R02.rvt", "");
        check("checklist: safe file names", SafeName("a/b:c*?") == "a_b_c__" && SafeName("  ") == "Untitled", SafeName("a/b:c*?"));

        // ---- The Word file
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
        check("checklist: picture kind and size from the header", ChecklistDocx.ImageKind(png) == "png" && ChecklistDocx.ImageSize(png) == (1, 1) && ChecklistDocx.ImageKind([1, 2, 3, 4]) is null, "");
        state.Fields["Model file name"] = "PRJ1 <070> & \"co\"";
        state.Signatures.Add(new Signature { Role = "Trade BIM coordinator", Name = "A. Engineer", Mode = SignMode.Mine, Date = "19-Oct-2026" });
        state.Signatures.Add(new Signature { Role = "Trade group leader", Name = "G. Leader", Mode = SignMode.Hand, Date = "19-Oct-2026" });
        var images = new Dictionary<string, ChecklistDocx.Image> { ["Trade BIM coordinator"] = new(png, "png"), ["Trade group leader"] = new(png, "png") };
        var (ok, text, pictures, error) = Docx(def, state, images);
        check("checklist: the Word file is a valid package", ok, error);
        check("checklist: ready file says READY and keeps the text escaped", text.Contains("READY FOR SUBMISSION") && text.Contains("PRJ1 <070> & \"co\""), "");
        check("checklist: signed only where a picture is used (by hand stays empty)", pictures == 1, pictures.ToString());
        state.For("1").Done = No;
        (ok, text, pictures, error) = Docx(def, state, images);
        check("checklist: not ready says so and has no signatures", ok && text.Contains("NOT READY") && pictures == 0, $"{pictures} pictures {error}");
        check("checklist: XML escaping drops control characters", ChecklistDocx.Esc("a\u0001<b>&\t") == "a&lt;b&gt;&amp;\t", ChecklistDocx.Esc("a\u0001<b>&\t"));
        // ---- The automatic model check, beside the answers
        var autoItems = new List<Item>
        {
            new("1", "1", "Grids are there.", "", "", "", "", "", "grids"),
            new("2", "2", "Warnings fixed.", "", "", "", "", "", "warnings"),
            new("3", "3", "Doc type right.", "", "", "", "", "", "doctype"),
            new("4", "4", "No model rule.", "", "", "", "", "", ""),
        };
        bool? Says(string key) => key switch { "grids" => true, "warnings" => false, _ => null };
        var autoState = new State();
        foreach (var i in autoItems) autoState.For(i.Id).Done = Yes;
        var auto = Auto(autoItems, Says, autoState);
        var disagree = Disagree(autoItems, autoState, Says);
        check("checklist: your answers 100% while the automatic check is 75% (the model fails one)",
            Percent(Count(autoItems, autoState)) == 100 && auto is { Tested: 2, Pass: 1, Fail: 1, Total: 4, Manual: 2, ManualDone: 2, Percent: 75 }, $"{Percent(Count(autoItems, autoState))}% / {auto}");
        var unanswered = Auto(autoItems, key => key == "warnings" ? false : key == "grids" ? true : null, new State());
        var allPass = Auto(autoItems, key => key is "grids" or "warnings" ? true : null, new State());
        check("checklist: checks done by hand hold the automatic check down until answered Yes or N/A",
            unanswered.Percent == 25 && allPass.Percent == 50 && allPass.Ceiling == 50 && Auto(autoItems, key => key is "grids" or "warnings" ? true : null, autoState).Percent == 100,
            $"{unanswered.Percent}% / {allPass.Percent}% (ceiling {allPass.Ceiling}%)");
        check("checklist: a Yes the model check fails is listed", disagree.Select(i => i.Id).SequenceEqual(["2"]), string.Join(",", disagree.Select(i => i.Id)));
        var autoLine = AutoLine(auto, disagree);
        check("checklist: the automatic line names the score and the disagreement", autoLine.Contains("3 of 4 checks pass (75%)") && autoLine.Contains("1 of the 2 it can test") && autoLine.Contains("#2") && AutoLine(new AutoProgress(0, 0, 0), []) == "", autoLine);
        var autoDoc = new MemoryStream();
        var autoDef = new Definition { Title = "T", Sections = [new Section("A. One", autoItems)], Signatories = ["Prepared by"] };
        ChecklistDocx.Write(autoDoc, autoDef, autoState, new ChecklistDocx.Info("m.rvt", "0", DateTime.Now, autoLine), new Dictionary<string, ChecklistDocx.Image>());
        autoDoc.Position = 0;
        using (var zip = new ZipArchive(autoDoc))
        using (var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open()))
            check("checklist: the Word file carries the automatic check", reader.ReadToEnd().Contains("Automatic check: 3 of 4 checks pass (75%)"), "");

        // ---- The built-in checklist
        var built = BuiltIn();
        check("checklist: the built-in checklist reads", built is not null && built.Title.StartsWith("Pre-submission checklist") && built.Items.Count() == 57 && built.Sections.Count == 7
            && built.Signatories.SequenceEqual(["Trade BIM coordinator", "Trade group leader"]) && built.SourcePath == BuiltInSource,
            built is null ? "null" : $"{built.Title}: {built.Items.Count()} checks, {built.Sections.Count} stages");
        if (built is null) return;
        check("checklist: built-in checks are numbered 1 to 57 in order", built.Items.Select(i => i.Id).SequenceEqual(Enumerable.Range(1, 57).Select(n => n.ToString())), "");
        var wordy = built.Items.Where(i => i.Short.Length == 0 || i.Short.Length > 45 || i.Fix.Length == 0 || i.Title.Length >= i.Check.Length).Select(i => i.Number).ToList();
        check("checklist: every built-in check has a short title (45 characters at most) and a way to fix it", wordy.Count == 0, "checks " + string.Join(", ", wordy));
        var json = ToJson(built);
        // Project numbers look like a letter and five digits, short project codes; neither may appear (the test itself names none).
        var named = System.Text.RegularExpressions.Regex.Matches(json, @"\b(?:[A-Z]\d{5}|[A-Z]\d{2}[A-Z])\b").Cast<System.Text.RegularExpressions.Match>().Select(m => m.Value).Distinct().ToList();
        check("checklist: the built-in checklist names no project or firm", named.Count == 0, string.Join(", ", named));
        check("checklist: a check without a short title shows its first sentence",
            new Item("1", "1", "Grids are in the model. They show in plans (BLDG-01).", "", "", "", "", "", "").Title == "Grids are in the model."
            && new Item("1", "1", "One sentence only", "", "", "", "", "", "").Title == "One sentence only", "");
        check("checklist: rule patterns (# digit, ? one character, * anything, spaces loose, case ignored)",
            Like("BLDG 7 LVL 01-TOC", "BLDG * LVL *") && Like("bldg  12 lvl B01", "BLDG * LVL *") && !Like("Level 1", "BLDG * LVL *")
            && Like("BLDG-01-1101", "BLDG-##-###*") && !Like("BLDG7-1101", "BLDG-##-###*") && Like("A1", "?#") && !Like("AB", "?#")
            && LikeAny("Z_Link_AR", "ST_*, Z_Link_*") && !LikeAny("MH_Ducting", "ST_*, Z_Link_*") && !LikeAny("x", ""), "");
        var ruled = new Definition { Rules = { ["levels"] = "L*", ["approvedworksets"] = "" } };
        check("checklist: a rule set wins, an empty one turns its test off, an unset one uses the default",
            Rule(ruled, "levels") == "L*" && Rule(ruled, "approvedworksets") == "" && Rule(ruled, "filesize") == "300" && Rule(null, "scales").StartsWith("1:1,"), "");
        var autoKeys = AutoRules.Select(r => r.Key).ToHashSet();
        var orphan = RuleDefs.Where(r => !autoKeys.Contains(r.Check)).Select(r => r.Key).ToList();
        check("checklist: every checking rule drives a model test", orphan.Count == 0, string.Join(", ", orphan));
        var newlyAuto = new[] { "12", "17", "25", "26", "27", "31", "39" }.Where(n => built.Items.First(i => i.Number == n).Auto.Length == 0).ToList();
        check("checklist: checks 12, 17, 25, 26, 27, 31 and 39 have model tests", newlyAuto.Count == 0, "no test on " + string.Join(", ", newlyAuto));
        var moreAuto = new[] { "2", "5", "15", "18", "19", "22", "34", "37", "38", "42", "43", "45" }.Where(n => built.Items.First(i => i.Number == n).Auto.Length == 0).ToList();
        check("checklist: checks 2, 5, 15, 18, 19, 22, 34, 37, 38, 42, 43 and 45 have model tests", moreAuto.Count == 0, "no test on " + string.Join(", ", moreAuto));
        var keyed = built.Items.Where(i => i.Auto.Length > 0 && AutoKey(i.Check) != i.Auto).Select(i => $"#{i.Number} {i.Auto} vs {AutoKey(i.Check)}").ToList();
        check("checklist: each built-in check's model test is the one its wording finds (so an imported Excel gets the same)", keyed.Count == 0, string.Join("; ", keyed));
        var manualLeft = built.Items.Where(i => i.Auto.Length == 0).Select(i => i.Number).ToList();
        check("checklist: only checks needing a person's judgement stay manual",
            manualLeft.SequenceEqual(["16", "20", "21", "33"]), string.Join(", ", manualLeft));
        var (missingDrawings, extraSheets) = CompareDrawingList(["Drawing list", "S-101", "S-102", "S-205", "A-101", "Rev P01", "Note"], ["S-101", "S-102", "S-300"]);
        check("checklist: the stage's drawing list against the sheets (only numbers shaped like the model's count)",
            missingDrawings.SequenceEqual(["S-205"]) && extraSheets.SequenceEqual(["S-300"]), string.Join(",", missingDrawings) + " / " + string.Join(",", extraSheets));
        var xml = """
            <exchange><batchtest><clashtests>
            <clashtest name="SC_Vs_MP" test_type="hard"><clashresults><clashresult name="Clash1" status="active"/><clashresult name="Clash2" status="resolved"/></clashresults></clashtest>
            <clashtest name="MP_Vs_SC"><clashresults><clashresult name="Clash1" status="new"/></clashresults></clashtest>
            </clashtests></batchtest></exchange>
            """;
        var clashTests = ClashReport(xml);
        var ourTests = OurClashTests(clashTests, "SC");
        check("checklist: a Navisworks clash report's open clashes, in the tests this trade fixes (Trade-1_Vs_Trade-2: Trade 1 fixes)",
            clashTests.Count == 2 && ourTests.Count == 1 && ourTests[0] is { Name: "SC_Vs_MP", Open: 1, Closed: 1 } && OurClashTests(clashTests, "").Count == 2
            && ClashReport("<td>New</td><td>Approved</td><td>Active</td>") is [{ Open: 2, Closed: 1 }], string.Join("; ", clashTests));
        check("checklist: the last publishing day", LastDay(new DateTime(2026, 10, 21, 9, 0, 0), DayOfWeek.Monday) == new DateTime(2026, 10, 19)
            && LastDay(new DateTime(2026, 10, 19, 23, 0, 0), DayOfWeek.Monday) == new DateTime(2026, 10, 19), "");
        var rulesPath = Path.Combine(Path.GetTempPath(), $"spm-checklist-{Guid.NewGuid():N}.xlsx");
        try
        {
            var withRules = Renumber(built);
            withRules.Rules["levels"] = "BLDG ## LVL *";
            withRules.Rules["approvedworksets"] = "ST_*, Z_Link_*";
            ChecklistXlsx.Write(rulesPath, withRules);
            var back = ReadXlsx(rulesPath);
            check("checklist: checking rules go to the Excel Rules sheet and come back",
                Rule(back, "levels") == "BLDG ## LVL *" && Rule(back, "approvedworksets") == "ST_*, Z_Link_*" && Rule(back, "scales") == Rule(null, "scales"), ToJson(back.Rules));
        }
        finally { File.Delete(rulesPath); }
        var question = ChecklistHelper.Question(built.Items.First(i => i.Number == "15"), "model.rvt", null);
        check("checklist: the helper's question has the check, how to check, no changes and the PASS / FAIL answer",
            question.Contains("check #15") && question.Contains("How to check:") && question.Contains("Don't change anything") && question.Contains("PASS, FAIL or CAN'T TELL")
            && !question.Contains("Claude"), question);
        check("checklist: the helper setting reads old and odd values as Sketch ProMax",
            ChecklistHelper.Parse(null) == ChecklistHelper.ProMax && ChecklistHelper.Parse(" SketchPro ") == ChecklistHelper.SketchPro && ChecklistHelper.Parse("none") == ChecklistHelper.None
            && ChecklistHelper.Button(ChecklistHelper.None) is null, "");

        // ---- Editing: delete a check and add one; answers follow their check
        var answered = new State();
        foreach (var item in built.Items) { answered.For(item.Id).Done = Yes; answered.For(item.Id).Check = item.Check; }
        var sections = built.Sections.Select(x => new Section(x.Title, x.Items.ToList())).ToList();
        var removed = sections[0].Items[0];
        sections[0] = new Section(sections[0].Title, sections[0].Items.Skip(1).ToList());
        sections.Add(new Section("H. Handover", [new Item("", "", "Model sent with <links> & a \"read me\".\nSecond line.", "e.g. zip", "Group leader", "BLDG-02", "High", "BIM lead to confirm", "")]));
        var edited = Renumber(new Definition { Title = built.Title, Fields = built.Fields, Sections = sections, Signatories = built.Signatories, SourcePath = EditedSource });
        var lost = Reconcile(edited, answered);
        check("checklist: renumbered checks keep their answers, the deleted one's goes",
            lost == 1 && edited.Items.Count() == 57 && edited.Items.Last().Id == "57" && !answered.Answers.ContainsKey("57")
            && answered.Answers.Count == 56 && answered.Answers["1"].Check == edited.Items.First().Check && edited.Items.First().Check != removed.Check,
            $"{lost} dropped, {answered.Answers.Count} kept");

        // ---- Excel round trip: export, import, the same checklist
        foreach (var (name, original) in new[] { ("built-in", built), ("edited", edited) })
        {
            var xlsxPath = Path.Combine(Path.GetTempPath(), $"spm-checklist-{Guid.NewGuid():N}.xlsx");
            try
            {
                ChecklistXlsx.Write(xlsxPath, original);
                var back = ReadXlsx(xlsxPath);
                string Plain(Definition d) => ToJson(new Definition { Title = d.Title, Fields = d.Fields, Sections = d.Sections, Signatories = d.Signatories });
                check($"checklist: the {name} checklist survives Excel export and import", Plain(back) == Plain(original),
                    $"{back.Items.Count()} checks back; first difference near: {First(Plain(back), Plain(original))}");
            }
            finally { File.Delete(xlsxPath); }
        }
    }

    private static string First(string a, string b)
    {
        var i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        return i >= a.Length && i >= b.Length ? "(none)" : a.Substring(Math.Max(0, i - 40), Math.Min(80, a.Length - Math.Max(0, i - 40)));
    }

    private static string Refused(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), "spm-checklist-" + Guid.NewGuid().ToString("N") + ".xlsx");
        File.WriteAllBytes(path, bytes);
        try { ReadXlsx(path); return ""; }
        catch (InvalidDataException ex) { return ex.Message; }
        finally { File.Delete(path); }
    }

    /// <summary>Writes the file and opens it again: every XML part parses, the body text, how many pictures.</summary>
    private static (bool Ok, string Text, int Pictures, string Error) Docx(Definition def, State state, Dictionary<string, ChecklistDocx.Image> images)
    {
        using var stream = new MemoryStream();
        ChecklistDocx.Write(stream, def, state, new ChecklistDocx.Info("model.rvt", "0.0.0", new DateTime(2026, 10, 19, 12, 0, 0)), images);
        stream.Position = 0;
        try
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".xml") || e.FullName.EndsWith(".rels")))
                using (var s = entry.Open()) XDocument.Load(s);
            XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            XDocument doc;
            using (var s = zip.GetEntry("word/document.xml")!.Open()) doc = XDocument.Load(s);
            var text = string.Concat(doc.Descendants(w + "t").Select(t => t.Value));
            var required = new[] { "[Content_Types].xml", "_rels/.rels", "word/_rels/document.xml.rels", "word/styles.xml", "word/footer1.xml" };
            var missing = required.Where(r => zip.GetEntry(r) is null).ToList();
            return (missing.Count == 0, text, zip.Entries.Count(e => e.FullName.StartsWith("word/media/")), missing.Count == 0 ? "" : "missing " + string.Join(", ", missing));
        }
        catch (Exception ex) { return (false, "", 0, ex.Message); }
    }

    /// <summary>A minimal .xlsx in the office's layout: shared strings for text, numbers as numbers, one row per list.</summary>
    private static byte[] Workbook(params (string Name, List<(int Col, string Text)>[] Rows)[] sheets)
    {
        var shared = new List<string>();
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string xml)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                w.Write(xml);
            }
            const string main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var book = new StringBuilder($"<workbook xmlns=\"{main}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
            var rels = new StringBuilder("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
            for (var i = 0; i < sheets.Length; i++)
            {
                book.Append($"<sheet name=\"{sheets[i].Name}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>");
                rels.Append($"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>");
                var data = new StringBuilder($"<worksheet xmlns=\"{main}\"><sheetData>");
                for (var r = 0; r < sheets[i].Rows.Length; r++)
                {
                    data.Append($"<row r=\"{r + 1}\">");
                    foreach (var (col, text) in sheets[i].Rows[r])
                    {
                        var at = $"{(char)('A' + col)}{r + 1}";
                        if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
                            data.Append($"<c r=\"{at}\"><v>{text}</v></c>");
                        else if (text.StartsWith('='))
                            data.Append($"<c r=\"{at}\" t=\"str\"><f>{System.Security.SecurityElement.Escape(text[1..])}</f><v></v></c>");
                        else
                        {
                            shared.Add(text);
                            data.Append($"<c r=\"{at}\" t=\"s\"><v>{shared.Count - 1}</v></c>");
                        }
                    }
                    data.Append("</row>");
                }
                Add($"xl/worksheets/sheet{i + 1}.xml", data.Append("</sheetData></worksheet>").ToString());
            }
            Add("xl/workbook.xml", book.Append("</sheets></workbook>").ToString());
            Add("xl/_rels/workbook.xml.rels", rels.Append("</Relationships>").ToString());
            Add("xl/sharedStrings.xml", $"<sst xmlns=\"{main}\">" + string.Concat(shared.Select(s => $"<si><t xml:space=\"preserve\">{System.Security.SecurityElement.Escape(s)}</t></si>")) + "</sst>");
        }
        return stream.ToArray();
    }
}
