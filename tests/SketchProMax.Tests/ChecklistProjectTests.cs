using SketchProMax.Tools;
using static SketchProMax.Tools.ChecklistMath;

/// <summary>
/// Project checklists (Tools/ChecklistProjects): the project a model belongs to, a new checklist copied from the Default,
/// your own rules over a newer Default, keeping and assigning checklists per project, the next Default, and the password. Runs in a temporary folder.
/// </summary>
internal static class ChecklistProjectTests
{
    public static void Run(Action<string, bool, string> check)
    {
        check("checklist projects: the project is the code before the first dash",
            ChecklistProjects.ProjectKey(@"C:\Models\PRJ1-070-00-ABC-002-STR-3DM-0010_detached.rvt") == "PRJ1"
            && ChecklistProjects.ProjectKey("prj2-0100D-RVT-TIN-SB_someone.rvt") == "PRJ2"
            && ChecklistProjects.ProjectKey("Office tower.rvt") == "Office tower",
            ChecklistProjects.ProjectKey("prj2-0100D-RVT-TIN-SB_someone.rvt"));
        check("checklist projects: names become file names", ChecklistProjects.CleanName("  Ports: berth / quay  ") == "Ports_ berth _ quay", ChecklistProjects.CleanName("  Ports: berth / quay  "));
        check("checklist projects: the source marks a project checklist",
            ChecklistProjects.NameOf(ChecklistProjects.Source("Ports")) == "Ports" && ChecklistProjects.NameOf(EditedSource) is null && ChecklistProjects.NameOf(null) is null, "");

        var office = BuiltIn()!;
        var ports = ChecklistProjects.Copy(office, "Ports");
        check("checklist projects: a new checklist starts as a full copy of the Default",
            ports.Title == "Ports" && ports.Items.Select(i => i.Check).SequenceEqual(office.Items.Select(i => i.Check))
            && ports.Signatories.SequenceEqual(office.Signatories) && ports.Fields.Count == office.Fields.Count && office.Title != "Ports",
            $"{ports.Items.Count()} of {office.Items.Count()} checks");

        // Your own checking rules on the Default are kept apart, and laid over a newer Default.
        var mine = ChecklistMath.FromJson<Definition>(ChecklistMath.ToJson(office))!;
        mine.Rules["midp"] = @"C:\Project\MIDP.xlsx";
        var own = ChecklistProjects.OwnRules(mine, office);
        var newer = ChecklistMath.FromJson<Definition>(ChecklistMath.ToJson(office))!;
        newer.Sections[0] = new Section(newer.Sections[0].Title, newer.Sections[0].Items.Take(1).ToList());
        var merged = ChecklistProjects.WithRules(newer, own);
        check("checklist projects: your own rules survive a newer Default",
            own.Count == 1 && own["midp"] == @"C:\Project\MIDP.xlsx" && merged.Rules["midp"] == @"C:\Project\MIDP.xlsx" && merged.Items.Count() < office.Items.Count(),
            string.Join(", ", own.Select(p => p.Key + "=" + p.Value)));

        var folder = Path.Combine(Path.GetTempPath(), "spm-project-checklists-" + Guid.NewGuid().ToString("N"));
        ChecklistProjects.FolderForTests = folder;
        try
        {
            check("checklist projects: none at first, the office checklist is used",
                ChecklistProjects.Names().Count == 0 && ChecklistProjects.Assigned("PRJ1") == "", "");
            ChecklistProjects.Save("Ports", ports);
            ChecklistProjects.Assign("PRJ1", "Ports");
            var back = ChecklistProjects.Read("Ports");
            check("checklist projects: a saved checklist reads back and is used by its project only",
                back is not null && back.Items.Count() == office.Items.Count() && ChecklistProjects.NameOf(back.SourcePath) == "Ports"
                && ChecklistProjects.Assigned("prj1") == "Ports" && ChecklistProjects.Assigned("PRJ2") == ""
                && ChecklistProjects.Names().SequenceEqual(["Ports"]),
                back?.SourcePath ?? "not read");
            ChecklistProjects.Delete("Ports");
            check("checklist projects: deleting one sends its projects back to the office checklist",
                ChecklistProjects.Names().Count == 0 && ChecklistProjects.Assigned("PRJ1") == "" && ChecklistProjects.Read("Ports") is null, "");

            ChecklistProjects.DocumentsForTests = folder;
            var next = ChecklistProjects.SaveNextDefault(ports);
            var saved = ChecklistMath.FromJson<Definition>(File.ReadAllText(next));
            check("checklist projects: Make this the Default saves the checklist for the next update",
                saved is not null && saved.Items.Count() == ports.Items.Count() && saved.SourcePath == "" && ChecklistProjects.NameOf(ports.SourcePath) == "Ports", next);

            check("checklist projects: the editing password is 0000 until changed",
                ChecklistProjects.PasswordIs("0000") && !ChecklistProjects.PasswordIs("") && !ChecklistProjects.PasswordIs("1234"), "");
            ChecklistProjects.SetPassword("harbour");
            check("checklist projects: a changed password replaces 0000 and isn't stored as typed",
                ChecklistProjects.PasswordIs("harbour") && !ChecklistProjects.PasswordIs("0000")
                && !File.ReadAllText(Path.Combine(folder, "editing.json")).Contains("harbour"), "");
        }
        finally
        {
            ChecklistProjects.FolderForTests = null;
            ChecklistProjects.DocumentsForTests = null;
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }
}
