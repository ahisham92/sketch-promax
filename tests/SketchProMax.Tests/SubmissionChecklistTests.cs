using System.IO.Compression;
using System.Text.RegularExpressions;
using SketchProMax.Tools;
using static SketchProMax.Tools.SubmissionChecklistMath;

/// <summary>
/// The technical submission checklist (Tools/SubmissionChecklistMath, Bank, Diagrams, Docx): the question bank, the
/// questions a project's structure types ask, the engineer and group leader rules (Yes / No / N/A, approve, return,
/// N/A hidden only once approved), merging everyone's records from the model, the scores, the summaries Submissions
/// status shows, and the Word file. Generic names only (Engineer, Lead, PRJ1).
/// </summary>
internal static class SubmissionChecklistTests
{
    public static void Run(Action<string, bool, string> check)
    {
        // ---- The bank
        var bank = SubmissionChecklistBank.Questions;
        var dupes = bank.GroupBy(q => q.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        check("submission checklist: question ids are unique", dupes.Count == 0, string.Join(", ", dupes));
        // Answers are kept by question id: a released id must stay in the bank (reword it, don't rename it).
        string[] released =
        [
            "basis.dbr", "basis.codes", "basis.life", "basis.survey", "basis.geotech", "basis.metocean", "basis.vessel",
            "basis.seismic", "basis.comments", "basis.scope", "basis.assumptions", "basis.environment", "levels.grading",
            "levels.marine", "levels.datum", "levels.cope", "levels.dredge", "levels.scour", "levels.grid",
            "levels.civil", "levels.electrical", "levels.mechanical", "levels.neighbours", "levels.existing",
            "loads.dead", "loads.live", "loads.equipment", "loads.cranes", "loads.berthing", "loads.mooring",
            "loads.wind", "loads.impact", "loads.waves", "loads.earth", "loads.water", "loads.seismic", "loads.thermal",
            "loads.combinations", "geo.profile", "geo.capacity", "geo.toe", "geo.negative", "geo.bearing", "geo.global",
            "geo.liquefaction", "geo.improvement", "geo.drivability", "piled.layout", "piled.model", "piled.deck",
            "piled.connection", "piled.uplift", "piled.slope", "piled.joints", "piled.precast", "blocks.stability",
            "blocks.friction", "blocks.keys", "blocks.bed", "blocks.backfill", "blocks.weight", "blocks.cope",
            "blocks.toe", "caissons.float", "caissons.stability", "caissons.walls", "caissons.joints", "caissons.fill",
            "sheets.embedment", "sheets.section", "sheets.corrosion", "sheets.anchor", "sheets.waling",
            "sheets.tielevel", "sheets.combi", "walls.stability", "walls.stem", "walls.drainage", "walls.joints",
            "revet.armour", "revet.filter", "revet.toe", "revet.crest", "revet.slope", "revet.overtopping",
            "dolphins.layout", "dolphins.deflection", "dolphins.hooks", "dolphins.access", "trestle.spans",
            "trestle.pipes", "trestle.traffic", "trestle.abutment", "roro.gradient", "roro.loads", "roro.hinge",
            "pontoon.freeboard", "pontoon.guides", "pontoon.gangway", "dwall.embedment", "dwall.panels", "dwall.stages",
            "dredge.slopes", "dredge.volumes", "dredge.disposal", "dredge.reclamation", "rails.gauge", "rails.beam",
            "rails.fixings", "rails.stops", "rails.power", "yard.pavement", "yard.falls", "yard.joint", "yard.services",
            "dur.exposure", "dur.cover", "dur.cracks", "dur.steel", "dur.cathodic", "dur.rock", "furn.fenders",
            "furn.bollards", "furn.ladders", "furn.lifesaving", "furn.navaids", "furn.edges", "furn.services",
            "furn.maintenance", "calcs.index", "calcs.checked", "calcs.software", "calcs.match", "calcs.report",
            "calcs.construction", "calcs.risks", "calcs.inspection", "dwg.list", "dwg.notes", "dwg.levels",
            "dwg.sections", "dwg.rebar", "dwg.clouds", "dwg.titleblock", "dwg.markups", "bills.method", "bills.model",
            "bills.backup", "bills.rates", "bills.marine", "bills.steel", "bills.piles", "spec.cover", "spec.match",
            "spec.marine", "spec.testing"
        ];
        var lost = released.Where(id => bank.All(q => q.Id != id)).ToList();
        check("submission checklist: released question ids are all still asked", lost.Count == 0, string.Join(", ", lost));
        var badTopic = bank.Where(q => Topics.All(t => t.Key != q.Topic)).Select(q => q.Id).ToList();
        check("submission checklist: every question is on a page", badTopic.Count == 0, string.Join(", ", badTopic));
        var badType = bank.Where(q => q.TypeKeys.Any(k => TypeOf(k) is null)).Select(q => q.Id).ToList();
        check("submission checklist: every question names known structure types", badType.Count == 0, string.Join(", ", badType));
        var typePage = bank.Where(q => q.Topic == TopicTypes && q.TypeKeys.Count == 0).Select(q => q.Id).ToList();
        check("submission checklist: structure page questions name their type", typePage.Count == 0, string.Join(", ", typePage));
        var uncovered = Types.Where(t => bank.Count(q => q.Topic == TopicTypes && q.TypeKeys.FirstOrDefault() == t.Key) < 3).Select(t => t.Key).ToList();
        check("submission checklist: every structure type has at least three checks of its own", uncovered.Count == 0, string.Join(", ", uncovered));
        var tooLong = bank.Where(q => q.Text.Length > 90 || !q.Text.EndsWith("?")).Select(q => q.Id).ToList();
        check("submission checklist: questions are short questions", tooLong.Count == 0, string.Join(", ", tooLong));
        var noDetails = bank.Where(q => q.Details.Trim().Length == 0).Select(q => q.Id).ToList();
        check("submission checklist: every question has details to show on demand", noDetails.Count == 0, string.Join(", ", noDetails));
        check("submission checklist: grading and marine teams are asked about levels",
            bank.Any(q => q.Id == "levels.grading" && q.TeamList.Contains("Grading")) && bank.Any(q => q.Id == "levels.marine" && q.TeamList.Contains("Marine")), "");
        var noDiagram = bank.Where(q => q.Diagram.Length > 0 && SubmissionChecklistDiagrams.Find(q.Diagram) is null).Select(q => q.Id).ToList();
        check("submission checklist: every diagram a question names exists", noDiagram.Count == 0, string.Join(", ", noDiagram));
        var typesWithout = Types.Where(t => SubmissionChecklistDiagrams.Find(t.Key) is null).Select(t => t.Key).ToList();
        check("submission checklist: every structure type has a section sketch", typesWithout.Count == 0, string.Join(", ", typesWithout));
        var offCanvas = SubmissionChecklistDiagrams.All.SelectMany(d => d.Shapes.Where(s => s.Kind is "poly" or "path" or "text")
            .Where(s => Enumerable.Range(0, s.Points.Length / 2).Any(i => s.Points[2 * i] < 0 || s.Points[2 * i] > SubmissionChecklistDiagrams.Width || s.Points[2 * i + 1] < 0 || s.Points[2 * i + 1] > SubmissionChecklistDiagrams.Height))
            .Select(s => d.Key + ":" + s.Text)).ToList();
        check("submission checklist: sketches stay on their drawing", offCanvas.Count == 0, string.Join(", ", offCanvas));
        var svg = SubmissionChecklistDiagrams.Svg(SubmissionChecklistDiagrams.Find("levels")!);
        check("submission checklist: a sketch draws as SVG with its levels", svg.StartsWith("<svg") && svg.Contains("CD / LAT") && svg.EndsWith("</svg>"), svg.Length.ToString());
        // Public repository rule: no people, firm, client or project names in the questions.
        var named = bank.Where(q => Regex.IsMatch(q.Text + " " + q.Details, @"\b[A-Z]\d{5}\b")).Select(q => q.Id).ToList();
        check("submission checklist: no project codes in the questions", named.Count == 0, string.Join(", ", named));

        // ---- Questions per project
        var none = QuestionsFor(new Setup(), []);
        var piled = QuestionsFor(new Setup { Types = ["piled-deck"] }, []);
        var blocks = QuestionsFor(new Setup { Types = ["gravity-blocks"] }, []);
        var both = QuestionsFor(new Setup { Types = ["gravity-blocks", "piled-deck"] }, []);
        check("submission checklist: a project with no type gets only the general questions", none.All(q => q.TypeKeys.Count == 0) && none.Count > 30, none.Count.ToString());
        check("submission checklist: picking deck on piles adds its questions, not the block ones",
            piled.Any(q => q.Id == "piled.uplift") && piled.Any(q => q.Id == "geo.capacity") && piled.All(q => q.Id != "blocks.stability") && piled.All(q => q.Id != "caissons.float"), "");
        check("submission checklist: gravity blocks don't ask about piles", blocks.All(q => q.Id != "geo.capacity" && q.Id != "piled.deck") && blocks.Any(q => q.Id == "blocks.keys"), "");
        check("submission checklist: two types ask both, each question once", both.Count == both.Select(q => q.Id).Distinct().Count()
            && both.Any(q => q.Id == "blocks.keys") && both.Any(q => q.Id == "piled.deck"), both.Count.ToString());
        var structure = both.Where(q => q.Topic == TopicTypes).Select(q => TypeOfQuestion(q, ["gravity-blocks", "piled-deck"])).ToList();
        check("submission checklist: the structure page follows the order the types were picked",
            structure.Count > 0 && structure.TakeWhile(t => t == "gravity-blocks").Count() + structure.SkipWhile(t => t == "gravity-blocks").Count(t => t == "piled-deck") == structure.Count, string.Join(",", structure.Distinct()));
        var revetOnly = QuestionsFor(new Setup { Types = ["revetment"] }, []);
        check("submission checklist: a question for two types shows when only the second is picked", revetOnly.Any(q => q.Id == "piled.slope"), "");
        var pages = both.Select(q => q.Topic).Distinct().ToList();
        check("submission checklist: pages come in their order", pages.SequenceEqual(Topics.Select(t => t.Key).Where(pages.Contains)), string.Join(",", pages));
        var skipped = QuestionsFor(new Setup { Types = ["piled-deck"], Skipped = ["piled.precast"] }, []);
        check("submission checklist: a skipped built-in question is not asked", skipped.All(q => q.Id != "piled.precast") && skipped.Count == piled.Count - 1, "");
        var added = new AddedQuestion { Id = "add.eng.1", Topic = "levels", Text = "Levels agreed with the port authority?", By = "Engineer" };
        var withAdded = QuestionsFor(new Setup { Types = ["piled-deck"] }, [added, new AddedQuestion { Id = "add.eng.2", Topic = "levels", Text = "Gone?", Deleted = true }]);
        check("submission checklist: added questions show on their page, deleted ones don't",
            withAdded.Any(q => q.Id == "add.eng.1" && q.Added && q.Topic == "levels") && withAdded.All(q => q.Id != "add.eng.2")
            && withAdded.FindIndex(q => q.Id == "add.eng.1") < withAdded.FindIndex(q => q.Topic == "loads"), "");

        // ---- Engineer answers, the group leader approves or returns: through the model, one record each
        var t0 = new DateTime(2026, 10, 24, 8, 0, 0, DateTimeKind.Utc);
        var lead = new UserRecord { User = "Lead" };
        lead.Setup = new Setup { Types = ["piled-deck"], Leads = ["Lead"], By = "Lead", At = t0 };
        var key = AddSubmission(lead, "P1", "Detailed design", new DateTime(2026, 11, 5), t0);
        var eng = new UserRecord { User = "Engineer" };
        // An engineer can't make themselves a group leader by writing a newer setup.
        eng.Setup = new Setup { Types = ["piled-deck"], Leads = ["Engineer"], By = "Engineer", At = t0.AddMinutes(1) };
        var shared = Merge([lead, eng]);
        check("submission checklist: an engineer can't take over the setup", shared.IsLead("Lead") && !shared.IsLead("Engineer") && shared.Setup.By == "Lead", string.Join(",", shared.Setup.Leads));
        var sub = shared.Find(key)!;
        check("submission checklist: the group leader's submission reaches the engineer", sub is not null && sub.Name == "Detailed design" && sub.Package == "P1" && sub.Due == new DateTime(2026, 11, 5), "");
        var qs = QuestionsFor(shared.Setup, shared.Added);
        SetAnswer(eng, sub!, "basis.dbr", "yes", null, t0.AddMinutes(5));
        SetAnswer(eng, sub!, "basis.seismic", "n/a", "Not a seismic zone", t0.AddMinutes(6));
        SetAnswer(eng, sub!, "basis.codes", "No", "Waiting for the client", t0.AddMinutes(7));
        // The group leader sees them after the engineer syncs and they reload.
        shared = Merge([lead, Copy(eng)]);
        sub = shared.Find(key)!;
        check("submission checklist: answers travel through the model", StateOf(sub, "basis.dbr") == ItemState.Answered && StateOf(sub, "basis.seismic") == ItemState.NaWaiting
            && StateOf(sub, "basis.codes") == ItemState.No && StateOf(sub, "basis.life") == ItemState.Open, "");
        var p = Count(qs, sub);
        check("submission checklist: N/A stays in view and counted until approved", p.Hidden == 0 && p.NaWaiting == 1 && p.Total == qs.Count && p.Checked == 2, p.ToString());
        SetReview(lead, sub, "basis.dbr", Approved, "", t0.AddMinutes(10));
        SetReview(lead, sub, "basis.seismic", Approved, "", t0.AddMinutes(11));
        SetReview(lead, sub, "basis.life", Returned, "Say 50 years", t0.AddMinutes(12));
        // An engineer's own "approval" never counts.
        SetReview(eng, sub, "basis.codes", Approved, "", t0.AddMinutes(13));
        shared = Merge([Copy(lead), Copy(eng)]);
        sub = shared.Find(key)!;
        p = Count(qs, sub);
        check("submission checklist: approved Yes counts, approved N/A is hidden and left out",
            StateOf(sub, "basis.dbr") == ItemState.Approved && StateOf(sub, "basis.seismic") == ItemState.Hidden && p.Hidden == 1 && p.Total == qs.Count - 1 && p.Approved == 1, p.ToString());
        check("submission checklist: only a group leader's review counts", StateOf(sub, "basis.codes") == ItemState.No, "");
        check("submission checklist: a returned question shows why", StateOf(sub, "basis.life") == ItemState.Returned && LiveReview(sub, "basis.life")!.Note == "Say 50 years", "");
        // The engineer changes an approved answer: the approval lapses until the group leader looks again.
        SetAnswer(eng, sub, "basis.dbr", "No", "Revision C came in", t0.AddMinutes(20));
        SetAnswer(eng, sub, "basis.life", "Yes", "50 years", t0.AddMinutes(21));
        shared = Merge([Copy(lead), Copy(eng)]);
        sub = shared.Find(key)!;
        check("submission checklist: a changed answer needs a new review", StateOf(sub, "basis.dbr") == ItemState.No && StateOf(sub, "basis.life") == ItemState.Answered, "");
        // A comment alone keeps the approval.
        SetNote(eng, sub, "basis.seismic", "Zone 0 per the code map", t0.AddMinutes(22));
        shared = Merge([Copy(lead), Copy(eng)]);
        sub = shared.Find(key)!;
        check("submission checklist: a comment alone keeps the approval", StateOf(sub, "basis.seismic") == ItemState.Hidden && sub.Answers["basis.seismic"].Note == "Zone 0 per the code map", "");
        // Records saved and read back (as the model and the PC keep them).
        var back = ReadRecord(ToJson(eng));
        check("submission checklist: a record reads back the same", back is not null && back.Submissions.ContainsKey(key.ToUpperInvariant()) && back.For(key).Answers["basis.dbr"].Value == No
            && Merge([lead, back]).Find(key) is { } again && StateOf(again, "basis.dbr") == ItemState.No, "");
        check("submission checklist: a broken record reads as nothing", ReadRecord("{not json") is null && ReadRecord("") is null, "");
        // Everything answered and approved: ready.
        var all = new UserRecord { User = "Engineer" };
        var ok = new UserRecord { User = "Lead", Setup = lead.Setup };
        AddSubmission(ok, "P2", "Tender", null, t0);
        var s2 = Merge([ok]).Find(KeyOf("P2", "Tender"))!;
        foreach (var q in qs) SetAnswer(all, s2, q.Id, q.Id.StartsWith("piled.") ? "NA" : "Y", "", t0.AddMinutes(1));
        foreach (var q in qs) SetReview(ok, s2, q.Id, Approved, "", t0.AddMinutes(2));
        var done = Count(qs, Merge([all, ok]).Find(KeyOf("P2", "Tender"))!);
        check("submission checklist: all approved is ready at 100%", done.Ready && done.ApprovedPercent == 100 && done.CheckedPercent == 100 && StatusText(done) == "Approved", done.ToString());
        check("submission checklist: status words", StatusText(new Progress(10, 10, 0, 0, 0, 0, 0, 0)) == "Not started" && StatusText(new Progress(10, 2, 5, 0, 1, 0, 2, 0)) == "Checking: 3 left"
            && StatusText(new Progress(10, 0, 4, 1, 0, 0, 5, 0)) == "Waiting for the group leader (5)" && StatusText(new Progress(10, 0, 4, 0, 0, 1, 5, 0)).Contains("returned"), "");
        check("submission checklist: Yes and N/A spelled any way", Normal("yes") == Yes && Normal("n/a") == NotApplicable && Normal("N A") == NotApplicable && Normal("no") == No && Normal("maybe") == "", "");
        check("submission checklist: a group leader approves Yes and N/A only", CanApprove(ItemState.Answered) && CanApprove(ItemState.NaWaiting) && !CanApprove(ItemState.No) && !CanApprove(ItemState.Open), "");
        // Deleted submissions go; the newest version of a submission wins.
        var del = new UserRecord { User = "Lead", Setup = lead.Setup };
        AddSubmission(del, "P3", "Old", null, t0);
        del.For(KeyOf("P3", "Old")).Deleted = true;
        del.For(KeyOf("P3", "Old")).At = t0.AddHours(1);
        check("submission checklist: a deleted submission is gone for everyone", Merge([del]).Find(KeyOf("P3", "Old")) is null, "");
        check("submission checklist: submission keys ignore case and spacing", KeyOf(" P1 ", "Detailed  Design") == KeyOf("p1", "detailed design"), "");

        // ---- BIM checklist score and the summary for Submissions status
        SetBim(eng, sub, 72, t0.AddMinutes(30));
        SetBim(lead, sub, 60, t0.AddMinutes(10));
        var merged = Merge([Copy(lead), Copy(eng)]).Find(key)!;
        check("submission checklist: the newest BIM checklist score wins", merged.Bim == 72, merged.Bim?.ToString() ?? "null");
        var summary = SummaryOf("PRJ1-STR.rvt", merged, qs, t0);
        check("submission checklist: the summary carries the scores", summary.Package == "P1" && summary.Bim == 72 && summary.Total == Count(qs, merged).Total && summary.Status.Length > 0, "");
        var temp = Path.Combine(Path.GetTempPath(), "spmx-subcheck-" + Guid.NewGuid().ToString("N"));
        FolderForTests = temp;
        try
        {
            SaveSummaries([summary]);
            var read = LoadSummaries();
            check("submission checklist: summaries are kept on the PC", Find(read, "p1", "DETAILED DESIGN")?.Bim == 72 && Find(read, "P1", "Another")?.Package == "P1" && Find(read, "P9", "x") is null, "");
            SaveOwn("PRJ1-STR.rvt", eng);
            check("submission checklist: the user's own record is kept on the PC", LoadOwn("PRJ1-STR.rvt", "Engineer")?.For(key).Answers.Count == eng.For(key).Answers.Count && LoadOwn("PRJ1-STR.rvt", "Nobody") is null, "");
        }
        finally
        {
            FolderForTests = null;
            try { Directory.Delete(temp, true); } catch (IOException) { }
        }

        // ---- The Word file
        using var ms = new MemoryStream();
        var shown = Merge([Copy(lead), Copy(eng)]);
        SubmissionChecklistDocx.Write(ms, shown, shown.Find(key)!, qs, new SubmissionChecklistDocx.Info("PRJ1-STR.rvt", "0.0.0", t0, new Dictionary<string, ChecklistDocx.Image>()));
        ms.Position = 0;
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var xml = new StreamReader(zip.GetEntry("word/document.xml")!.Open()).ReadToEnd();
        var parsed = true;
        try { System.Xml.Linq.XDocument.Parse(xml); } catch (System.Xml.XmlException) { parsed = false; }
        check("submission checklist: the Word file is well formed", parsed && zip.GetEntry("word/footer1.xml") is not null, xml.Length.ToString());
        check("submission checklist: the Word file has the cover, scores, questions, the agreed N/A and the sign-off",
            xml.Contains("TECHNICAL SUBMISSION CHECKLIST") && xml.Contains("P1 · Detailed design") && xml.Contains("BIM checklist") && xml.Contains("Design basis report is the latest")
            && xml.Contains("Not applicable (agreed by the group leader)") && xml.Contains("Group leader") && xml.Contains("Sign-off"), "");
        check("submission checklist: the Word file says it isn't ready while answers are No", xml.Contains("NOT READY"), "");
    }

    private static UserRecord Copy(UserRecord r) => ReadRecord(ToJson(r))!;
}
