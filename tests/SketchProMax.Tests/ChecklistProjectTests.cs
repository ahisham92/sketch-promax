using SketchProMax.Tools;
using static SketchProMax.Tools.ChecklistMath;

/// <summary>
/// Project checklists (Tools/ChecklistProjects): the project a model belongs to, a checklist made from picked checks,
/// keeping and assigning them per project, and the editing password. Runs in a temporary folder.
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
        var picked = office.Sections.Take(2).Select(s => s.Items.First().Id).ToList();
        var ports = ChecklistProjects.Pick(office, "Ports", picked);
        check("checklist projects: picking keeps only the ticked checks, numbered again",
            ports.Items.Count() == 2 && ports.Sections.Count == 2 && ports.Items.Select(i => i.Number).SequenceEqual(["1", "2"])
            && ports.Items.Select(i => i.Check).SequenceEqual(office.Sections.Take(2).Select(s => s.Items.First().Check))
            && ports.Title == "Ports" && ports.Signatories.SequenceEqual(office.Signatories) && ports.Fields.Count == office.Fields.Count,
            string.Join(", ", ports.Items.Select(i => i.Number + " " + i.Title)));

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
                back is not null && back.Items.Count() == 2 && ChecklistProjects.NameOf(back.SourcePath) == "Ports"
                && ChecklistProjects.Assigned("prj1") == "Ports" && ChecklistProjects.Assigned("PRJ2") == ""
                && ChecklistProjects.Names().SequenceEqual(["Ports"]),
                back?.SourcePath ?? "not read");
            ChecklistProjects.Delete("Ports");
            check("checklist projects: deleting one sends its projects back to the office checklist",
                ChecklistProjects.Names().Count == 0 && ChecklistProjects.Assigned("PRJ1") == "" && ChecklistProjects.Read("Ports") is null, "");

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
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }
}
