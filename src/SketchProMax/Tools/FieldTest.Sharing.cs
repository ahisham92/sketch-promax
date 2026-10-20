using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Windows;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SketchProMax.Settings;
using SketchProMax.Sharing;
using SketchProMax.UI;

namespace SketchProMax.Tools;

/// <summary>
/// Test before sharing: the pre-submission checklist and Share tools in depth, for whoever looks after the Default
/// checklist and shares tools (checklist settings > Test before sharing, behind the Default's password). Each check is
/// one row in the "sharing" family. Whatever a check writes goes to the test folder: ChecklistStore, ChecklistProjects,
/// SharedPacks and UserFolder are pointed there for the thread, so this PC's own checklists, rules, password and
/// installed packs are never changed. The model checks run on the field test's throwaway project.
/// </summary>
internal static partial class FieldTest
{
    internal const string SharingFamily = "sharing";
    internal const string SharingTitle = "test before sharing";

    /// <summary>Where Test before sharing saves its report, for the user to upload.</summary>
    internal static string TestReportFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Sketch ProMax", "Test reports");

    private static async Task BeforeSharing(State s)
    {
        // The field test's own checklist and Share tools checks, when the non-tool features aren't running them anyway.
        if (s.O.Families is not null && !s.O.Families.Contains("features"))
        {
            await Probe(s, "checklist.word", app => ChecklistProbe(app, s), SharingFamily).ConfigureAwait(false);
            await Probe(s, "checklist.views", app => ChecklistViewsProbe(app, s), SharingFamily).ConfigureAwait(false);
            await Probe(s, "checklist.fix", app => ChecklistFixProbe(app, s), SharingFamily).ConfigureAwait(false);
            await Probe(s, "checklist.fix_more", app => ChecklistFixMoreProbe(app, s), SharingFamily).ConfigureAwait(false);
            await Probe(s, "checklist.fix_standards", app => ChecklistFixStandardsProbe(app, s), SharingFamily).ConfigureAwait(false);
            await Probe(s, "checklist.rules", app => ChecklistRulesProbe(app, s), SharingFamily).ConfigureAwait(false);
            await Probe(s, "checklist.pdf", () => ChecklistPdfProbe(s.Folder), SharingFamily).ConfigureAwait(false);
            await Probe(s, "share.pack", () => SharePackProbe(s.Folder), SharingFamily).ConfigureAwait(false);
        }
        // On Revit's thread: the model and the windows.
        await Probe(s, "pretest.reading", app => PretestReading(app, s), SharingFamily).ConfigureAwait(false);
        await Probe(s, "pretest.windows", app => PretestWindows(app, s), SharingFamily).ConfigureAwait(false);
        await Probe(s, "pretest.ports_rules", app => PretestPortsRules(app, s), SharingFamily).ConfigureAwait(false);
        // Off Revit's thread: files only.
        await Probe(s, "pretest.default", PretestDefault, SharingFamily).ConfigureAwait(false);
        await Probe(s, "pretest.in_use", PretestInUse, SharingFamily).ConfigureAwait(false);
        await Probe(s, "pretest.password", () => Isolated(s, "password", PretestPassword), SharingFamily).ConfigureAwait(false);
        await Probe(s, "pretest.projects", () => Isolated(s, "projects", PretestProjects), SharingFamily).ConfigureAwait(false);
        await Probe(s, "pretest.own_rules", () => Isolated(s, "rules", PretestOwnRules), SharingFamily).ConfigureAwait(false);
        await Probe(s, "pretest.excel", () => Isolated(s, "excel", PretestExcel), SharingFamily).ConfigureAwait(false);
        await Probe(s, "pretest.next_default", () => Isolated(s, "next-default", PretestNextDefault), SharingFamily).ConfigureAwait(false);
        await Probe(s, "pretest.answers", PretestAnswers, SharingFamily).ConfigureAwait(false);
        await Probe(s, "pretest.word", () => PretestWord(s.Folder), SharingFamily).ConfigureAwait(false);
        await Probe(s, "pretest.share_rules", PretestShareRules, SharingFamily).ConfigureAwait(false);
        await Probe(s, "pretest.share_export", () => Isolated(s, "share-export", PretestShareExport), SharingFamily).ConfigureAwait(false);
        await Probe(s, "pretest.share_install", () => Isolated(s, "share-install", PretestShareInstall), SharingFamily).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <paramref name="body"/> with the checklist, the project checklists, shared packs and Documents pointed at a
    /// new folder in the test folder (for this thread only), then puts them back and deletes the folder.
    /// </summary>
    private static (string, string) Isolated(State s, string name, Func<string, (string, string)> body)
    {
        var folder = Path.Combine(s.Folder, "before-sharing", name);
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        ChecklistStore.FolderForTests = Path.Combine(folder, "appdata");
        ChecklistProjects.FolderForTests = Path.Combine(folder, "appdata", "project checklists");
        ChecklistProjects.DocumentsForTests = Path.Combine(folder, "documents");
        SharedPacks.TestRoot = Path.Combine(folder, "appdata");
        UserFolder.TestDocuments = Path.Combine(folder, "documents");
        try { return body(folder); }
        finally
        {
            ChecklistStore.FolderForTests = null;
            ChecklistProjects.FolderForTests = null;
            ChecklistProjects.DocumentsForTests = null;
            SharedPacks.TestRoot = null;
            UserFolder.TestDocuments = null;
            try { Directory.Delete(folder, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static (string, string) Result(List<string> problems, List<string> done) =>
        problems.Count == 0 ? Pass(string.Join("; ", done)) : Fail(string.Join("; ", problems) + (done.Count > 0 ? ". Fine: " + string.Join("; ", done) : ""));

    private static ChecklistMath.Definition Clone(ChecklistMath.Definition def) =>
        ChecklistMath.FromJson<ChecklistMath.Definition>(ChecklistMath.ToJson(def)) ?? throw new InvalidOperationException("the checklist couldn't be copied");

    private static ChecklistMath.Definition DefaultChecklist() =>
        ChecklistMath.BuiltIn() ?? throw new InvalidOperationException("the Default checklist that ships with Sketch ProMax can't be read");

    // ---- The checklist ------------------------------------------------------------------------------------------------

    /// <summary>The Default that ships: every check whole, ids and numbers unique, tests and rules known, JSON stable, no e-mail addresses.</summary>
    private static (string, string) PretestDefault()
    {
        var def = DefaultChecklist();
        var problems = new List<string>();
        var items = def.Items.ToList();
        if (items.Count == 0) return Fail("the Default has no checks");
        foreach (var g in items.GroupBy(i => i.Id).Where(g => g.Count() > 1)) problems.Add($"check id {g.Key} is used {g.Count()} times");
        foreach (var g in items.GroupBy(i => i.Number).Where(g => g.Count() > 1)) problems.Add($"check number {g.Key} is used {g.Count()} times");
        foreach (var i in items)
        {
            if (i.Check.Trim().Length == 0) problems.Add($"check {i.Number} has no wording");
            if (i.Auto.Length > 0 && !ChecklistMath.AutoRules.Any(r => r.Key == i.Auto)) problems.Add($"check {i.Number}: its automatic test {i.Auto} doesn't exist");
            if (i.Auto.Length > 0 && ChecklistMath.AutoKey(i.Check) != i.Auto) problems.Add($"check {i.Number}: its wording no longer picks its automatic test {i.Auto}");
        }
        foreach (var section in def.Sections)
        {
            if (section.Title.Trim().Length == 0) problems.Add("a stage has no name");
            if (section.Items.Count == 0) problems.Add($"stage {section.Title} has no checks");
        }
        foreach (var key in def.Rules.Keys.Where(k => ChecklistMath.RuleDefs.All(r => r.Key != k))) problems.Add($"unknown checking rule {key}");
        foreach (var field in def.Fields.Where(f => f.Label.Trim().Length == 0)) problems.Add("a header field has no label");
        if (def.Signatories.Count == 0) problems.Add("nobody signs it");
        var json = ChecklistMath.ToJson(def);
        if (ChecklistMath.ToJson(Clone(def)) != json) problems.Add("it changes when saved and read again");
        var renumbered = ChecklistMath.Renumber(Clone(def));
        if (!renumbered.Items.Select(i => i.Check).SequenceEqual(items.Select(i => i.Check))) problems.Add("renumbering changes its checks");
        if (Regex.IsMatch(json, @"[\w.+-]+@[\w-]+\.[a-z]{2,}", RegexOptions.IgnoreCase)) problems.Add("an e-mail address is in it (the Default is public)");
        var auto = items.Count(i => i.Auto.Length > 0);
        return Result(problems,
        [
            $"{items.Count} checks in {def.Sections.Count} stages ({string.Join(", ", def.Sections.Select(x => $"{ChecklistMath.StageName(x.Title)} {x.Items.Count}"))})",
            $"{auto} with an automatic test, {items.Count - auto} you check",
            $"{items.Count(i => i.Short.Trim().Length > 0)} short titles, {items.Count(i => i.Fix.Trim().Length > 0)} with how to fix, {items.Count(i => i.High)} high priority",
            $"{def.Fields.Count} header fields, signed by {string.Join(" and ", def.Signatories)}",
            "ids and numbers unique, every test and rule known, saves and reads back the same",
        ]);
    }

    /// <summary>What this PC uses now, read only: the Default or a copy, own rules, the project checklists, the next Default, the password.</summary>
    private static (string, string) PretestInUse()
    {
        var problems = new List<string>();
        var done = new List<string>();
        var (def, note) = ChecklistStore.Load(AgentSettings.Load());
        if (def is null) return Fail("no checklist can be read on this PC: " + note);
        if (note.Length > 0) problems.Add(note);
        var builtIn = DefaultChecklist();
        var source = def.SourcePath;
        if (source == ChecklistMath.BuiltInSource)
        {
            var own = ChecklistProjects.OwnRules(def, builtIn);
            done.Add($"this PC uses the Default ({def.Items.Count()} checks), so Default updates reach it" + (own.Count > 0 ? $", with your own {own.Count} checking rules ({string.Join(", ", own.Keys)})" : ""));
        }
        else if (source == ChecklistMath.EditedSource)
            problems.Add($"this PC uses a copy of the Default edited here ({def.Items.Count()} checks, {def.SourceStamp}), so Default updates don't reach it. Edit the checklist > Go back to the built-in one, or make it the Default for everyone");
        else if (source.StartsWith("shared:", StringComparison.Ordinal))
            done.Add($"this PC uses the checklist from the shared pack {source.Substring(7)} ({def.Items.Count()} checks)");
        else
            done.Add($"this PC uses the Excel file {source} ({def.Items.Count()} checks); Default updates don't reach it");
        var names = ChecklistProjects.Names();
        var unreadable = names.Where(n => ChecklistProjects.Read(n) is null).ToList();
        if (unreadable.Count > 0) problems.Add("these project checklists can't be read: " + string.Join(", ", unreadable));
        done.Add(names.Count == 0 ? "no project checklists on this PC" : $"{names.Count} project checklists, all readable: {string.Join(", ", names.Select(n => $"{n} ({ChecklistProjects.Read(n)?.Items.Count() ?? 0})"))}");
        if (File.Exists(ChecklistProjects.NextDefaultPath))
        {
            var next = ChecklistMath.FromJson<ChecklistMath.Definition>(File.ReadAllText(ChecklistProjects.NextDefaultPath));
            if (next is null || !next.Items.Any()) problems.Add("the next Default you saved can't be read: " + ChecklistProjects.NextDefaultPath);
            else done.Add($"a next Default is saved for publishing ({next.Items.Count()} checks, {File.GetLastWriteTime(ChecklistProjects.NextDefaultPath):dd-MMM-yyyy HH:mm})");
        }
        else done.Add("no next Default saved for publishing");
        done.Add(ChecklistProjects.PasswordIs(ChecklistProjects.DefaultPassword) ? "the Default's password on this PC is still 0000" : "the Default's password on this PC was changed from 0000");
        return Result(problems, done);
    }

    /// <summary>The Default's password: 0000 to start, wrong ones refused, a new one replaces it and isn't stored as typed.</summary>
    private static (string, string) PretestPassword(string folder)
    {
        var problems = new List<string>();
        if (!ChecklistProjects.PasswordIs("0000")) problems.Add("0000 isn't accepted on a new PC");
        foreach (var wrong in new[] { "", "1234", "00000", " 0000" })
            if (ChecklistProjects.PasswordIs(wrong)) problems.Add($"the wrong password \"{wrong}\" is accepted");
        ChecklistProjects.SetPassword("Harbour-7");
        if (!ChecklistProjects.PasswordIs("Harbour-7")) problems.Add("the new password isn't accepted");
        if (ChecklistProjects.PasswordIs("0000")) problems.Add("0000 still works after the change");
        var stored = Directory.GetFiles(folder, "editing.json", SearchOption.AllDirectories).Select(File.ReadAllText).FirstOrDefault() ?? "";
        if (stored.Length == 0) problems.Add("the new password wasn't saved");
        if (stored.Contains("Harbour-7")) problems.Add("the password is saved as typed");
        return Result(problems, ["0000 works on a new PC", "4 wrong ones refused", "a new password replaces it and is saved scrambled"]);
    }

    /// <summary>A project's own checklist: a copy of the Default, used by its project code, edited, exported and imported, deleted.</summary>
    private static (string, string) PretestProjects(string folder)
    {
        var problems = new List<string>();
        var done = new List<string>();
        var settings = new AgentSettings();
        var (start, _) = ChecklistStore.Load(settings);
        if (start is null || start.SourcePath != ChecklistMath.BuiltInSource) return Fail("a new PC doesn't start with the Default");
        var count = start.Items.Count();

        var key = ChecklistProjects.ProjectKey(@"C:\Projects\PRJ1-070-00-ABC-STR-3DM-0010.rvt");
        if (key != "PRJ1") problems.Add($"the project code of PRJ1-070-00-ABC-STR-3DM-0010.rvt reads as {key}");
        if (ChecklistProjects.ProjectKey("Sample model.rvt") != "Sample model") problems.Add("a model with no project code gets one");
        done.Add("the project code is read from the file name");

        var copy = ChecklistProjects.Copy(start, "Ports");
        if (copy.Title != "Ports" || !copy.Items.Select(i => i.Check).SequenceEqual(start.Items.Select(i => i.Check)) || ChecklistMath.ToJson(copy.Rules) != ChecklistMath.ToJson(start.Rules))
            problems.Add("a new checklist isn't a full copy of the Default");
        ChecklistProjects.Save("Ports", copy);
        var read = ChecklistProjects.Read("Ports");
        if (read is null || read.Items.Count() != count || ChecklistProjects.NameOf(read.SourcePath) != "Ports") problems.Add("the new checklist doesn't read back");
        if (!ChecklistProjects.Names().SequenceEqual(["Ports"])) problems.Add("the new checklist isn't listed");
        done.Add($"a new checklist starts as all {count} checks of the Default");

        ChecklistProjects.Assign(key, "Ports");
        if (ChecklistProjects.NameOf(ChecklistStore.Load(settings, key).Def?.SourcePath) != "Ports") problems.Add("the project doesn't use its own checklist");
        if (ChecklistStore.Load(settings, "PRJ2").Def?.SourcePath != ChecklistMath.BuiltInSource) problems.Add("another project doesn't keep the Default");
        done.Add("PRJ1 uses it, PRJ2 keeps the Default");

        // Edit it the way the editor saves: one check deleted, one added, a stage renamed, a rule changed.
        var edit = ChecklistStore.Load(settings, key).Def!;
        var removed = edit.Sections[0].Items[0].Check;
        var added = "Fenders and bollards are modelled and tagged.";
        var sections = edit.Sections.Select((x, n) => n == 0
            ? new ChecklistMath.Section("A. Port model", x.Items.Skip(1).ToList())
            : n == edit.Sections.Count - 1 ? new ChecklistMath.Section(x.Title, x.Items.Concat([new ChecklistMath.Item("new", "", added, "", "", "", "", "", ChecklistMath.AutoKey(added))]).ToList()) : x).ToList();
        edit.Sections = sections;
        edit.Rules["filesize"] = "500";
        ChecklistStore.SaveInUse(ChecklistMath.Renumber(edit));
        var after = ChecklistProjects.Read("Ports");
        if (after is null || after.Items.Count() != count || after.Items.Any(i => i.Check == removed) || after.Items.All(i => i.Check != added)
            || after.Sections[0].Title != "A. Port model" || ChecklistMath.Rule(after, "filesize") != "500")
            problems.Add("the edit wasn't saved to the project checklist");
        var stillDefault = ChecklistStore.Load(settings).Def;
        if (stillDefault?.Items.Count() != count || stillDefault.Items.All(i => i.Check != removed) || ChecklistMath.Rule(stillDefault, "filesize") == "500"
            || File.Exists(ChecklistStore.SavedPath))
            problems.Add("editing the project checklist changed the Default");
        done.Add("editing it (a check deleted, one added, a stage renamed, a rule) changes it only");

        // Export to Excel, import as another checklist (the next project).
        var xlsx = Path.Combine(folder, "Ports checklist.xlsx");
        ChecklistXlsx.Write(xlsx, after!);
        var imported = ChecklistMath.ReadXlsx(xlsx);
        ChecklistProjects.Save("Next port", imported);
        var back = ChecklistProjects.Read("Next port");
        if (back is null || !back.Items.Select(i => i.Check).SequenceEqual(after!.Items.Select(i => i.Check)) || ChecklistMath.Rule(back, "filesize") != "500")
            problems.Add("exported and imported, the checklist isn't the same");
        ChecklistProjects.Assign("PRJ3", "Next port");
        done.Add($"exported to Excel ({new FileInfo(xlsx).Length / 1024} KB) and imported for PRJ3 with the same checks and rules");

        ChecklistProjects.Delete("Ports");
        if (ChecklistProjects.Names().Contains("Ports") || ChecklistProjects.Assigned(key).Length > 0) problems.Add("deleted, it is still listed or used");
        if (ChecklistStore.Load(settings, key).Def?.SourcePath != ChecklistMath.BuiltInSource) problems.Add("after deleting it, PRJ1 doesn't go back to the Default");
        if (ChecklistProjects.Assigned("PRJ3") != "Next port") problems.Add("deleting one checklist changed another project");
        done.Add("deleting it puts PRJ1 back on the Default and leaves PRJ3 alone");

        var clean = ChecklistProjects.CleanName("Ports: berth/9? \"new\"" + new string('x', 80));
        if (clean.Length > 60 || clean.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) problems.Add($"the name {clean} can't be a file name");
        else done.Add("names Windows refuses are cleaned");
        return Result(problems, done);
    }

    /// <summary>Your own checking rules on the Default: kept apart, so a newer Default still reaches you with your rules on it.</summary>
    private static (string, string) PretestOwnRules(string folder)
    {
        var problems = new List<string>();
        var settings = new AgentSettings();
        var builtIn = DefaultChecklist();
        var mine = ChecklistStore.Load(settings).Def!;
        mine.Rules["filesize"] = "250";
        mine.Rules["levels"] = "L##";
        ChecklistStore.SaveInUse(mine);
        if (File.Exists(ChecklistStore.SavedPath)) problems.Add("saving your rules made a copy of the whole Default (it would stop following updates)");
        var again = ChecklistStore.Load(settings).Def;
        if (again?.SourcePath != ChecklistMath.BuiltInSource || ChecklistMath.Rule(again, "filesize") != "250" || ChecklistMath.Rule(again, "levels") != "L##")
            problems.Add("your rules aren't on the Default when it is read again");

        var newer = Clone(builtIn);
        var added = "The newer Default's extra check is here.";
        newer.Sections = newer.Sections.Select((x, n) => n == 0 ? new ChecklistMath.Section(x.Title, x.Items.Concat([new ChecklistMath.Item("zz", "", added, "", "", "", "", "", "")]).ToList()) : x).ToList();
        var own = ChecklistProjects.OwnRules(again!, builtIn);
        var merged = ChecklistProjects.WithRules(newer, own);
        if (merged.Items.All(i => i.Check != added) || ChecklistMath.Rule(merged, "filesize") != "250") problems.Add("a newer Default doesn't arrive with your rules on it");

        var back = ChecklistStore.Load(settings).Def!;
        back.Rules["filesize"] = ChecklistMath.RuleDefs.First(r => r.Key == "filesize").Default;
        if (ChecklistProjects.OwnRules(back, builtIn).ContainsKey("filesize")) problems.Add("a rule set back to the Default's value is still kept as yours");
        return Result(problems, ["your rules are saved apart from the Default", "read back on the Default", "a newer Default (one more check) arrives with your rules on it", "a rule set back is dropped"]);
    }

    /// <summary>Excel export and import keep every check, title, how-to-fix, priority, stage, field, signatory and rule.</summary>
    private static (string, string) PretestExcel(string folder)
    {
        var problems = new List<string>();
        var def = DefaultChecklist();
        def.Rules["filesize"] = "275";
        def.Rules["schedules"] = "*Foundation*, *Pile*";
        var path = Path.Combine(folder, "Default checklist.xlsx");
        var watch = Stopwatch.StartNew();
        ChecklistXlsx.Write(path, def);
        var back = ChecklistMath.ReadXlsx(path);
        var a = def.Items.ToList();
        var b = back.Items.ToList();
        if (a.Count != b.Count) problems.Add($"{a.Count} checks written, {b.Count} read back");
        var differ = new List<string>();
        for (var n = 0; n < Math.Min(a.Count, b.Count); n++)
        {
            var (x, y) = (a[n], b[n]);
            var what = new List<string>();
            if (ChecklistMath.Normal(x.Check) != ChecklistMath.Normal(y.Check)) what.Add("wording");
            if (x.Short.Trim() != y.Short.Trim()) what.Add("short title");
            if (x.Fix.Trim() != y.Fix.Trim()) what.Add("how to fix");
            if (x.High != y.High) what.Add("priority");
            if (x.Number != y.Number) what.Add("number");
            if (x.Auto != y.Auto) what.Add("automatic test");
            if (what.Count > 0) differ.Add($"#{x.Number} {string.Join("/", what)}");
        }
        if (differ.Count > 0) problems.Add("changed by Excel: " + string.Join(", ", differ.Take(10)) + (differ.Count > 10 ? $" and {differ.Count - 10} more" : ""));
        if (!def.Sections.Select(x => x.Title).SequenceEqual(back.Sections.Select(x => x.Title))) problems.Add("the stages changed");
        if (!def.Fields.Select(f => f.Label).SequenceEqual(back.Fields.Select(f => f.Label))) problems.Add("the header fields changed");
        if (!def.Fields.Select(f => f.Source).SequenceEqual(back.Fields.Select(f => f.Source))) problems.Add("where the header fields are filled from changed");
        if (!def.Signatories.SequenceEqual(back.Signatories)) problems.Add("the signatories changed");
        foreach (var rule in ChecklistMath.RuleDefs)
            if (ChecklistMath.Rule(def, rule.Key) != ChecklistMath.Rule(back, rule.Key)) problems.Add($"rule {rule.Key} came back as \"{ChecklistMath.Rule(back, rule.Key)}\"");

        // Edited in Excel's place: one check fewer and a new short title, written and read again.
        var edited = Clone(back);
        edited.Sections = edited.Sections.Select((x, n) => n == 0 ? new ChecklistMath.Section(x.Title, x.Items.Skip(1).Select((i, k) => k == 0 ? i with { Short = "Edited title" } : i).ToList()) : x).ToList();
        ChecklistXlsx.Write(path, edited);
        var third = ChecklistMath.ReadXlsx(path);
        if (third.Items.Count() != a.Count - 1 || third.Sections[0].Items[0].Short != "Edited title") problems.Add("an edited checklist doesn't come back edited");
        return Result(problems, [$"all {a.Count} checks, {def.Sections.Count} stages, {def.Fields.Count} fields, {def.Signatories.Count} signatories and every rule survive Excel ({new FileInfo(path).Length / 1024} KB, {watch.ElapsedMilliseconds} ms)", "an edited copy comes back edited"]);
    }

    /// <summary>Make this the Default for everyone: the file saved for publishing is the checklist, clean of this PC's paths.</summary>
    private static (string, string) PretestNextDefault(string folder)
    {
        var problems = new List<string>();
        var def = ChecklistProjects.Copy(DefaultChecklist(), "Default for the next update");
        def.SourcePath = ChecklistProjects.Source("Ports");
        var path = ChecklistProjects.SaveNextDefault(def);
        if (!path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) problems.Add("it was saved outside the test folder: " + path);
        var back = ChecklistMath.FromJson<ChecklistMath.Definition>(File.ReadAllText(path));
        if (back is null || back.Items.Count() != def.Items.Count()) problems.Add("the saved file doesn't read back whole");
        else
        {
            if (back.SourcePath.Length > 0 || back.SourceStamp.Length > 0) problems.Add("it still carries where it came from on this PC");
            if (back.Items.Any(i => i.Auto.Length > 0 && ChecklistMath.AutoRules.All(r => r.Key != i.Auto))) problems.Add("it has an automatic test the add-in doesn't know");
        }
        return Result(problems, [$"saved as \"{Path.GetFileName(path)}\" in Documents\\Sketch ProMax, {def.Items.Count()} checks, nothing of this PC in it"]);
    }

    /// <summary>Answers: all Yes or N/A is 100% and ready; a No or a gap is listed; answers stay with their checks when one is deleted.</summary>
    private static (string, string) PretestAnswers()
    {
        var problems = new List<string>();
        var def = DefaultChecklist();
        var state = new ChecklistMath.State();
        var items = def.Items.ToList();
        // A check that waits on an answer also needs its comment, as in the window.
        foreach (var item in items) { var a = state.For(item.Id); a.Done = item == items[1] ? ChecklistMath.NotApplicable : ChecklistMath.Yes; a.Check = item.Check; if (item.Waiting.Length > 0) a.Note = "Done: answer received."; }
        var full = ChecklistMath.Percent(ChecklistMath.Count(items, state));
        if (full != 100) problems.Add($"all answered Yes or N/A shows {full}%");
        if (ChecklistMath.Problems(def, state).Count > 0) problems.Add("all answered still lists problems: " + ChecklistMath.Problems(def, state)[0].Text);
        state.For(items[2].Id).Done = ChecklistMath.No;
        state.Answers.Remove(items[3].Id);
        var listed = ChecklistMath.Problems(def, state);
        if (listed.Count < 2 || listed.All(p => p.Id != items[2].Id) || listed.All(p => p.Id != items[3].Id)) problems.Add("a No and an unanswered check aren't both listed");
        var part = ChecklistMath.Percent(ChecklistMath.Count(items, state));
        if (part >= 100) problems.Add("with a No and a gap it still shows 100%");

        // N/A removes the check from this model's checklist (score, open list, files); Put back brings it back.
        var kept = ChecklistMath.WithoutRemoved(def, state);
        if (kept.Items.Count() != items.Count - 1 || kept.Items.Any(i => i.Id == items[1].Id) || !ChecklistMath.Removed(state, items[1]))
            problems.Add("a check answered N/A isn't removed from the checklist that is scored and written");
        if (ChecklistMath.Count(kept.Items, state).NotApplicable != 0) problems.Add("a check removed as N/A still counts in the score");
        if (def.Items.Count() != items.Count) problems.Add("removing a check as N/A changed the checklist itself");
        state.For(items[1].Id).Done = "";
        if (ChecklistMath.WithoutRemoved(def, state).Items.Count() != items.Count) problems.Add("Put back doesn't bring the check back");
        state.For(items[1].Id).Done = ChecklistMath.NotApplicable;

        // The checklist changes under the answers: check 1 deleted and the rest renumbered.
        var changed = Clone(def);
        changed.Sections = changed.Sections.Select((x, n) => n == 0 ? new ChecklistMath.Section(x.Title, x.Items.Skip(1).ToList()) : x).ToList();
        changed = ChecklistMath.Renumber(changed);
        var dropped = ChecklistMath.Reconcile(changed, state);
        var moved = changed.Items.FirstOrDefault(i => i.Check == items[2].Check);
        if (dropped != 1) problems.Add($"{dropped} answers dropped when one check was deleted");
        if (moved is null || state.For(moved.Id).Done != ChecklistMath.No) problems.Add("an answer didn't follow its check when the checks were renumbered");
        return Result(problems, [$"all answered: 100% and nothing listed", $"one No and one gap: {part}% and both listed", "a check answered N/A leaves the score and the files, and Put back brings it back", "answers follow their checks when one is deleted and the rest renumbered"]);
    }

    /// <summary>The whole Default as the signed Word file (and a PDF through Word): every check in it, READY only when it is.</summary>
    private static (string, string) PretestWord(string testFolder)
    {
        var problems = new List<string>();
        var done = new List<string>();
        var def = DefaultChecklist();
        var state = new ChecklistMath.State();
        foreach (var item in def.Items) state.For(item.Id).Done = ChecklistMath.Yes;
        foreach (var role in def.Signatories)
            state.Signatures.Add(new ChecklistMath.Signature { Role = role, Name = "Test Signer", Mode = ChecklistMath.SignMode.Image, Date = "01-Jan-2026" });
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
        var images = def.Signatories.ToDictionary(r => r, _ => new ChecklistDocx.Image(png, "png"));
        var folder = Path.Combine(testFolder, "before-sharing", "word");
        Directory.CreateDirectory(folder);
        string Text(string path, out int pictures)
        {
            using var zip = ZipFile.OpenRead(path);
            pictures = zip.Entries.Count(e => e.FullName.StartsWith("word/media/", StringComparison.Ordinal));
            using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
            return System.Net.WebUtility.HtmlDecode(Regex.Replace(reader.ReadToEnd(), "<[^>]+>", ""));
        }
        var info = new ChecklistDocx.Info("PRJ1-070-00-ABC-STR-3DM-0010.rvt", Update.Updater.CurrentText, DateTime.Now, "Automatic check: 40 of 40 passed");
        var ready = Path.Combine(folder, "ready.docx");
        var watch = Stopwatch.StartNew();
        ChecklistDocx.Write(ready, def, state, info, images);
        var text = Text(ready, out var pics);
        var missing = def.Items.Where(i => !Regex.Replace(text, @"\s+", " ").Contains(Regex.Replace(ChecklistMath.FirstSentence(i.Check), @"\s+", " "))).Select(i => i.Number).ToList();
        if (missing.Count > 0) problems.Add("checks missing from the Word file: " + string.Join(", ", missing.Take(15)));
        if (!text.Contains("READY FOR SUBMISSION")) problems.Add("all answered and signed, it doesn't say READY FOR SUBMISSION");
        if (pics != def.Signatories.Count) problems.Add($"{pics} signature pictures for {def.Signatories.Count} signatories");
        done.Add($"all {def.Items.Count()} checks in the Word file, READY, {pics} signatures ({new FileInfo(ready).Length / 1024} KB, {watch.ElapsedMilliseconds} ms)");

        state.For(def.Items.First().Id).Done = ChecklistMath.No;
        var notReady = Path.Combine(folder, "not-ready.docx");
        ChecklistDocx.Write(notReady, def, state, info, images);
        text = Text(notReady, out pics);
        if (!text.Contains("NOT READY") || pics != 0) problems.Add($"with a No it has {pics} signatures and {(text.Contains("NOT READY") ? "says" : "doesn't say")} NOT READY");
        else done.Add("with one No: NOT READY and unsigned");

        // A check answered N/A is removed: not in the Word file, and the rest still READY.
        state.For(def.Items.First().Id).Done = ChecklistMath.Yes;
        var gone = def.Items.Last();
        state.For(gone.Id).Done = ChecklistMath.NotApplicable;
        var removedDocx = Path.Combine(folder, "one-removed.docx");
        ChecklistDocx.Write(removedDocx, ChecklistMath.WithoutRemoved(def, state), state, info, images);
        text = Text(removedDocx, out _);
        if (Regex.Replace(text, @"\s+", " ").Contains(Regex.Replace(ChecklistMath.FirstSentence(gone.Check), @"\s+", " ")) || !text.Contains("READY FOR SUBMISSION"))
            problems.Add("a check removed as N/A is still in the Word file, or the rest isn't READY");
        else done.Add($"check #{gone.Number} answered N/A is left out of the Word file, the rest READY");

        if (Type.GetTypeFromProgID("Word.Application") is null) done.Add("Word isn't installed, so Create PDF saves the Word file (not tested)");
        else
        {
            var pdf = Path.Combine(folder, "ready.pdf");
            watch.Restart();
            ChecklistPdf.Convert(ready, pdf).GetAwaiter().GetResult();
            var head = new byte[5];
            using (var f = File.OpenRead(pdf)) _ = f.Read(head, 0, head.Length);
            if (System.Text.Encoding.ASCII.GetString(head) != "%PDF-") problems.Add("Word wrote a file that isn't a PDF");
            else done.Add($"the full checklist as a PDF through Word ({new FileInfo(pdf).Length / 1024} KB, {watch.Elapsed.TotalSeconds:0.0} s)");
        }
        return Result(problems, done);
    }

    /// <summary>The Run checks reading of the whole Default on the test project, in the window's short steps: no errors, no long hold.</summary>
    private static (string, string) PretestReading(UIApplication app, State s)
    {
        if (TestDoc(app, s, out var why) is not { } doc) return Fail(why);
        var problems = new List<string>();
        var def = DefaultChecklist();
        var watch = Stopwatch.StartNew();
        var reading = ChecklistFacts.Plan(doc, def);
        var calls = 0;
        var longest = TimeSpan.Zero;
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            if (reading.NextName.Length > 0) names.Add(reading.NextName);
            var call = Stopwatch.StartNew();
            var more = reading.RunFor(TimeSpan.FromMilliseconds(200));
            calls++;
            if (call.Elapsed > longest) longest = call.Elapsed;
            if (!more) break;
        }
        var quick = watch.Elapsed;
        var facts = reading.Result;
        var broken = facts.Hints.Where(h => h.Value.Text.StartsWith("Couldn't", StringComparison.Ordinal)).Select(h => $"{h.Key}: {h.Value.Text}").ToList();
        if (broken.Count > 0) problems.Add("tests that failed to read the model: " + string.Join("; ", broken));
        if (longest > TimeSpan.FromSeconds(2)) problems.Add($"one step held Revit for {longest.TotalSeconds:0.0} s (the window would look stuck)");
        if (!facts.Fields.TryGetValue("file_size", out var size) || size.Length == 0) problems.Add("Final file size wasn't filled from the model");
        if (names.Count == 0) problems.Add("the progress bar had no check names to show");
        var tested = def.Items.Count(i => i.Auto.Length > 0 && facts.Hints.ContainsKey(i.Auto));
        var slowest = string.Join(", ", reading.Slowest.Take(5).Select(p => $"{p.Key} {p.Value.TotalMilliseconds:0} ms"));

        watch.Restart();
        var slow = ChecklistFacts.Read(doc, def, app);
        var slowBroken = slow.Hints.Where(h => h.Value.Text.StartsWith("Couldn't", StringComparison.Ordinal) || h.Value.Pending).Select(h => h.Key).ToList();
        if (slowBroken.Count > 0) problems.Add("slow checks not read: " + string.Join(", ", slowBroken));
        return Result(problems,
        [
            $"Run checks read {facts.Hints.Count} tests ({tested} checks settled) in {quick.TotalSeconds:0.0} s over {calls} steps, longest step {longest.TotalMilliseconds:0} ms",
            $"slowest: {slowest}",
            $"Final file size from the model: {size} MB",
            $"with the slow checks: {slow.Hints.Count} tests in {watch.Elapsed.TotalSeconds:0.0} s",
        ]);
    }

    /// <summary>The checklist's windows build on the test project without an error: the checklist, its settings, the editor (Default and own), the rules.</summary>
    private static (string, string) PretestWindows(UIApplication app, State s)
    {
        if (TestDoc(app, s, out var why) is not { } doc) return Fail(why);
        var problems = new List<string>();
        var built = new List<string>();
        var def = DefaultChecklist();
        var own = ChecklistProjects.Copy(def, "Ports");
        own.SourcePath = ChecklistProjects.Source("Ports");
        void Try(string name, Func<Window> make)
        {
            Window? window = null;
            try
            {
                window = make();
                window.Measure(new System.Windows.Size(1300, 800));
                built.Add(name);
            }
            catch (Exception ex) { problems.Add($"{name}: {ex.GetType().Name}: {ex.Message}"); }
            finally { try { window?.Close(); } catch (InvalidOperationException) { } }
        }
        string? draft = null;
        Try("the checklist", () => { var (w, d) = ChecklistWindow.ForTest(doc, def); draft = d; return w; });
        Try("the checklist on a project checklist", () => ChecklistWindow.ForTest(doc, own).Window);
        static void None() { }
        var actions = new ChecklistSettingsWindow.Actions(None, None, None, None, _ => { }, None, None, None, None, None, None, None);
        Try("its settings (Default)", () => ChecklistSettingsWindow.ForTest(actions, new ChecklistSettingsWindow.Project("PRJ1", "", [])));
        Try("its settings (own checklist)", () => ChecklistSettingsWindow.ForTest(actions, new ChecklistSettingsWindow.Project("PRJ1", "Ports", ["Ports", "Marine"])));
        Try("the editor on the Default", () => ChecklistEditor.ForTest(def));
        Try("the editor on an own checklist", () => ChecklistEditor.ForTest(own));
        Try("checking rules", () => ChecklistRulesWindow.ForTest(def));
        try { if (draft is not null && File.Exists(draft)) File.Delete(draft); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return Result(problems, ["built: " + string.Join(", ", built)]);
    }

    /// <summary>
    /// The rules a ports job needed, on the throwaway project: piles (columns named by the rule, or below the lowest
    /// level) aren't floating; detail lines test only the views asked for and skip views by name; a sheet with no title
    /// block is listed, and the landing page family isn't taken for a title block. Changes are rolled back.
    /// </summary>
    private static (string, string) PretestPortsRules(UIApplication app, State s)
    {
        if (TestDoc(app, s, out var why) is not { } doc) return Fail(why);
        var problems = new List<string>();
        var done = new List<string>();
        var columns = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StructuralColumns).WhereElementIsNotElementType().GetElementCount();
        if (ChecklistFacts.FloatingColumns(doc, "*").Count > 0) problems.Add("columns named by the piles rule are still tested for floating");
        else done.Add($"the piles rule leaves all {columns} columns out of the floating test");

        var def = DefaultChecklist();
        def.Rules["detaillines"] = "0";
        def.Rules["detailviews"] = "plans, sections, elevations";
        def.Rules["detailskip"] = "";
        var all = ChecklistFacts.Read(doc, def).Hints.TryGetValue("detaillines", out var h1) ? h1 : null;
        def.Rules["detailskip"] = "*";
        var none = ChecklistFacts.Read(doc, def).Hints.TryGetValue("detaillines", out var h2) ? h2 : null;
        def.Rules.Remove("detailskip");
        def.Rules.Remove("detailviews");
        var plans = ChecklistFacts.Read(doc, def).Hints.TryGetValue("detaillines", out var h3) ? h3 : null;
        if (all is null || none is null || plans is null) problems.Add("the detail lines test didn't run");
        else
        {
            if (none.Level != ChecklistFacts.Level.Info || none.Items.Count > 0) problems.Add("skipping every view by name still tests some: " + none.Text);
            if (plans.Items.Count > all.Items.Count) problems.Add("plans only lists more views than all kinds");
            if (plans.Text.IndexOf("section", StringComparison.OrdinalIgnoreCase) >= 0 && plans.Level == ChecklistFacts.Level.Warn) problems.Add("by default it still tests sections: " + plans.Text);
            done.Add($"detail lines: all kinds {all.Items.Count} listed, plans only (the default) {plans.Items.Count}, every view skipped by name 0");
        }

        using var group = new TransactionGroup(doc, "Test before sharing: sheet with no title block");
        group.Start();
        try
        {
            using (var t = new Transaction(doc, "Sheet with no title block"))
            {
                t.Start();
                var sheet = ViewSheet.Create(doc, ElementId.InvalidElementId);
                sheet.SheetNumber = "ZZ-TEST-01";
                t.Commit();
            }
            var hint = ChecklistFacts.Read(doc, def).Hints.TryGetValue("titleblock", out var tb) ? tb : null;
            if (hint is null || !hint.Items.Any(i => i.StartsWith("ZZ-TEST-01", StringComparison.Ordinal))) problems.Add("a sheet with no title block isn't listed: " + hint?.Text);
            else done.Add("a sheet with no title block is listed");
            if (hint is not null && hint.Text.IndexOf("LandingPage", StringComparison.OrdinalIgnoreCase) >= 0) problems.Add("the landing page family is taken for a title block");
        }
        finally { RollBack(group); }
        return Result(problems, done);
    }

    // ---- Share tools -------------------------------------------------------------------------------------------------

    /// <summary>When a shared pack may put its checklist in place on a colleague's PC, case by case.</summary>
    private static (string, string) PretestShareRules()
    {
        var problems = new List<string>();
        var cases = new (string What, bool Saved, string? Source, string? Excel, bool Write)[]
        {
            ("a colleague with no checklist of their own gets the pack's", false, null, null, true),
            ("the pack's own copy is updated", true, SharedPackMath.ChecklistSource("qa-tools"), null, true),
            ("a checklist they edited is kept", true, ChecklistMath.EditedSource, null, false),
            ("one from another pack is kept", true, SharedPackMath.ChecklistSource("other-pack"), null, false),
            ("their own Excel checklist is kept", false, null, @"C:\Checklists\Ours.xlsx", false),
        };
        foreach (var c in cases)
        {
            var (write, why) = SharedPackMath.DecideChecklist("qa-tools", c.Saved, c.Source, c.Excel);
            if (write != c.Write) problems.Add($"{c.What}: it would {(write ? "replace it" : "keep it")} ({why})");
        }
        return Result(problems, cases.Select(c => c.What).ToList());
    }

    /// <summary>
    /// Share tools with everything this PC can share (windows, ribbon tools, its learned tools and recipes): each one is
    /// in the pack and the zip, learned tools and recipes exactly as saved here, the buttons and the readme list them,
    /// and the installer is in the zip.
    /// </summary>
    private static (string, string) PretestShareExport(string folder)
    {
        var problems = new List<string>();
        var done = new List<string>();
        var catalog = SharedTools.Catalog();
        var keys = catalog.Select(i => i.Key).ToList();
        var request = new SharedPacks.Request("Test before sharing", keys, "", "Made by Test before sharing", Path.Combine(folder, "zips"), false);
        var (pack, files, notes) = SharedPacks.Build("test-before-sharing", request, SharedTools.RibbonTool, Update.Updater.CurrentText);
        var left = keys.Count - pack.Tools.Count;
        if (left > 0) problems.Add($"{left} of {keys.Count} items left out: " + string.Join("; ", notes));
        foreach (var tool in pack.Tools)
        {
            if (tool.Title.Trim().Length == 0) problems.Add($"{tool.Name} has no title");
            if (SharedPackMath.ButtonText(tool.Title).Length > 32) problems.Add($"{tool.Title}: its button text is too long");
            if (tool.Kind is not ("learned" or "recipe")) continue;
            var mine = tool.Kind == "learned" ? LearnedTools.PathFor(tool.Name) : Recipes.PathFor(tool.Name);
            if (tool.File is null || !files.TryGetValue(tool.File, out var packed)) problems.Add($"{tool.Title}: its file isn't in the pack");
            else if (File.Exists(mine) && packed != File.ReadAllText(mine)) problems.Add($"{tool.Title}: the pack's copy differs from the one saved on this PC");
        }
        done.Add($"{pack.Tools.Count} of {keys.Count} items packed: {catalog.Count(i => i.Group == "Windows")} windows, {pack.Tools.Count(t => t.Kind == "tool")} ribbon tools, " +
                 $"{pack.Tools.Count(t => t.Kind == "learned")} learned tools, {pack.Tools.Count(t => t.Kind == "recipe")} recipes");
        done.Add(pack.Checklist is null ? "no checklist in the pack: colleagues use the Default" : $"the pack carries this PC's checklist ({ChecklistMath.FromJson<ChecklistMath.Definition>(files[pack.Checklist])?.Items.Count() ?? 0} checks)");

        var result = SharedPacks.Export(request, SharedTools.RibbonTool, Update.Updater.CurrentText);
        if (Path.GetFileName(result.Zip) != SharedPackMath.ZipName("Test before sharing")) problems.Add("the zip is named " + Path.GetFileName(result.Zip));
        using (var zip = ZipFile.OpenRead(result.Zip))
        {
            var names = zip.Entries.Select(e => e.FullName.Replace('\\', '/')).ToList();
            foreach (var need in new[] { "Install TritoRevit.cmd", "install-tritorevit.ps1", "README.txt", "pack/pack.json", "pack/buttons.txt" })
                if (!names.Contains(need)) problems.Add("the zip has no " + need);
            foreach (var tool in result.Pack.Tools.Where(t => t.File is not null))
                if (!names.Contains("pack/" + tool.File)) problems.Add($"{tool.Title}: its file isn't in the zip");
            string Read(string name)
            {
                var entry = zip.GetEntry(name);
                if (entry is null) return "";
                using var reader = new StreamReader(entry.Open());
                return reader.ReadToEnd();
            }
            var buttons = Read("pack/buttons.txt").Split('\n').Count(l => l.StartsWith("button\t", StringComparison.Ordinal));
            if (buttons != result.Pack.Tools.Count) problems.Add($"{buttons} buttons listed for {result.Pack.Tools.Count} tools");
            var readme = Read("README.txt");
            var unlisted = result.Pack.Tools.Where(t => !readme.Contains(t.Title)).Select(t => t.Title).ToList();
            if (unlisted.Count > 0) problems.Add("the readme doesn't list " + string.Join(", ", unlisted));
            if (Read("Install TritoRevit.cmd").Trim().Length == 0 || Read("install-tritorevit.ps1").Trim().Length == 0) problems.Add("the installer is empty");
            done.Add($"zip \"{Path.GetFileName(result.Zip)}\": {names.Count} files, {new FileInfo(result.Zip).Length / 1024} KB, installer, readme and {buttons} buttons");
        }
        if (result.Notes.Count > 0) done.Add("notes: " + string.Join("; ", result.Notes));
        return Result(problems, done);
    }

    /// <summary>
    /// A colleague's PC (a test folder): the zip installs, the pack's checklist (when it carries one) goes in, a new
    /// revision follows by itself, their own edited checklist and project checklists are kept, a problem report reaches
    /// the sharer's update folder, and removing the pack takes its buttons and its checklist away.
    /// </summary>
    private static (string, string) PretestShareInstall(string folder)
    {
        var problems = new List<string>();
        var done = new List<string>();
        var update = Path.Combine(folder, "update folder");
        Directory.CreateDirectory(update);
        // No learned tools or recipes: installing those would write to this PC's own tool folders.
        var items = SharedPackMath.Commands.Select(c => "command:" + c.Command).Concat(["tool:check_model"]).ToList();
        var request = new SharedPacks.Request("QA tools", items, update, "", Path.Combine(folder, "zips"), true);
        var first = SharedPacks.Export(request, SharedTools.RibbonTool, Update.Updater.CurrentText);
        if (first.Published is null) problems.Add("not published to the update folder");
        var carries = first.Pack.Checklist is not null;

        // The colleague already has a project checklist of their own.
        var ports = ChecklistProjects.Copy(DefaultChecklist(), "Ports");
        ChecklistProjects.Save("Ports", ports);
        var portsBefore = ChecklistMath.ToJson(ChecklistProjects.Read("Ports")!);

        var installed = SharedPacks.InstallZip(first.Zip, out var why);
        if (installed is null) return Fail("the zip didn't install: " + why);
        if (SharedPacks.List().All(p => p.Pack.Id != installed.Id)) problems.Add("installed, but not listed");
        done.Add($"installed from the zip: {installed.Tools.Count} buttons, revision {installed.Revision}");
        var saved = File.Exists(ChecklistStore.SavedPath) ? ChecklistMath.FromJson<ChecklistMath.Definition>(File.ReadAllText(ChecklistStore.SavedPath)) : null;
        if (carries)
        {
            if (saved?.SourcePath != SharedPackMath.ChecklistSource(installed.Id)) problems.Add("the pack carries a checklist, but the colleague doesn't get it");
            else done.Add($"the colleague gets the pack's checklist ({saved.Items.Count()} checks)");
        }
        else if (saved is not null) problems.Add("the pack carries no checklist, yet one was put in place");
        else done.Add("the pack carries no checklist: the colleague keeps the Default");

        var second = SharedPacks.Export(request with { Note = "Second revision" }, SharedTools.RibbonTool, Update.Updater.CurrentText);
        var followed = SharedPacks.Sync();
        var now = SharedPacks.List().FirstOrDefault(p => p.Pack.Id == installed.Id);
        if (now?.Pack.Revision != second.Pack.Revision || followed.Count != 1) problems.Add($"revision {second.Pack.Revision} wasn't followed (installed: {now?.Pack.Revision.ToString() ?? "none"})");
        else done.Add($"revision {second.Pack.Revision} followed from the update folder by itself");

        if (carries)
        {
            // The colleague edits their checklist: the next revision must not replace it.
            var theirs = ChecklistMath.FromJson<ChecklistMath.Definition>(File.ReadAllText(ChecklistStore.SavedPath))!;
            theirs.SourcePath = ChecklistMath.EditedSource;
            theirs.Title = "Colleague's own";
            ChecklistStore.Save(theirs);
            SharedPacks.Export(request with { Note = "Third revision" }, SharedTools.RibbonTool, Update.Updater.CurrentText);
            SharedPacks.Sync();
            var kept = ChecklistMath.FromJson<ChecklistMath.Definition>(File.ReadAllText(ChecklistStore.SavedPath));
            if (kept?.Title != "Colleague's own") problems.Add("a newer revision replaced the checklist the colleague edited");
            else done.Add("a newer revision keeps the checklist the colleague edited");
        }
        if (ChecklistMath.ToJson(ChecklistProjects.Read("Ports")!) != portsBefore) problems.Add("installing or updating the pack changed the colleague's project checklist");
        else done.Add("the colleague's project checklist is untouched");

        var report = Path.Combine(folder, "Bug report test.zip");
        File.WriteAllText(report, "test");
        var reached = UserFolder.Deliver(report);
        var copied = Path.Combine(update, "Bug reports", Environment.UserName + " - Bug report test.zip");
        if (reached.Count != 1 || !File.Exists(copied)) problems.Add("a problem report didn't reach the update folder's Bug reports");
        else done.Add("a problem report reaches the sharer's update folder (Bug reports)");
        var folderName = SharedPackMath.FolderName(SharedPacks.List().Select(i => i.Pack));
        if (folderName != "TritoRevit - QA tools") problems.Add("the colleague's Documents folder would be " + folderName);
        if (SharedPackMath.ReadMe(SharedPacks.List().Select(i => i.Pack)).Trim().Length == 0) problems.Add("the how-to for problem reports is empty");

        SharedPacks.Remove(installed.Id);
        if (SharedPacks.List().Any(p => p.Pack.Id == installed.Id)) problems.Add("removed, the pack is still listed");
        else done.Add("Remove takes the pack away");
        return Result(problems, done);
    }
}
