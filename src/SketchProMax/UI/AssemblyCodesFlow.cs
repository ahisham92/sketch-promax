using System.IO;
using System.Windows;
using Autodesk.Revit.UI;
using SketchProMax.Tools;

namespace SketchProMax.UI;

/// <summary>
/// The one Assembly Codes button (the checklist's UniFormat check and Submissions status): pick the LOI Excel, its code
/// list is loaded into the model, then every type in use without a code is shown with the code the UniFormat rules give
/// it, to change or type, and Fill writes them. Each step says what it did in a dialog.
/// </summary>
internal static class AssemblyCodesFlow
{
    /// <summary>The Excel picked last time, so the next time is one click.</summary>
    private static string LastFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax", "assembly codes excel.txt");

    /// <param name="onModel">Runs on the model the window is for (it says so when another model is in front).</param>
    /// <returns>What was done, or null when the user stopped before anything changed.</returns>
    public static async Task<string?> Run(Window owner, Func<Func<UIDocument, string>, Task<string>> onModel, ChecklistMath.Definition? def)
    {
        string last = "";
        try { if (File.Exists(LastFile)) last = File.ReadAllText(LastFile).Trim(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var pick = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Pick the Excel with the Assembly Codes (your LOI matrix)",
            Filter = "LOI matrix Excel or assembly code file|*.xlsx;*.txt|All files|*.*",
        };
        if (last.Length > 0 && File.Exists(last)) { pick.InitialDirectory = Path.GetDirectoryName(last); pick.FileName = Path.GetFileName(last); }
        if (pick.ShowDialog(owner) != true) return null;
        var path = pick.FileName;
        try { Directory.CreateDirectory(Path.GetDirectoryName(LastFile)!); File.WriteAllText(LastFile, path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        const string split = "\u0001";
        var said = await onModel(ui => AssemblyCodes.Load(ui.Document, path) + split + AssemblyCodes.Suggest(ui.Document, def));
        if (!said.Contains(split))
        {
            MessageBox.Show(owner, said, "Sketch ProMax: Assembly Codes", MessageBoxButton.OK, MessageBoxImage.Warning);
            return said;
        }
        var loaded = said.Substring(0, said.IndexOf(split, StringComparison.Ordinal));
        var rows = said.Substring(loaded.Length + 1).Split('\n').Select(l => l.Split('\t')).Where(p => p.Length == 3).Select(p => (Id: p[0], What: p[1], Code: p[2])).ToList();
        if (rows.Count == 0)
        {
            var done = loaded + "\n\nEvery type in use has an Assembly Code from the list: nothing to fill.";
            MessageBox.Show(owner, done, "Sketch ProMax: Assembly Codes", MessageBoxButton.OK, MessageBoxImage.Information);
            return done;
        }
        var types = rows.Count(r => !r.Id.StartsWith(AssemblyCodes.CategoryKey, StringComparison.Ordinal));
        var categories = rows.Count - types;
        var given = rows.Count(r => r.Code.Length > 0);
        var edited = ChecklistProjectWindows.EditNames(owner, "Assembly Codes", rows.Select(r => (r.What, r.Code)).ToList(),
            $"{loaded}\n\n{types} type{(types == 1 ? "" : "s")} in use, in {categories} categor{(categories == 1 ? "y" : "ies")}, need an Assembly Code. " +
            "The ALL rows at the top give every type of that category the code you type (such as B1010.20 for all Floors); a type's own row below wins over it. " +
            (given > 0 ? $"{given} box{(given == 1 ? " was" : "es were")} filled from the UniFormat rules of the project's checklist. " :
                "None was filled for you: the project's checklist has no UniFormat rules (Pre-submission checklist > Checklist for this project > Import your project's checklist Excel). ") +
            "An empty box is skipped. Nothing changes until you press Fill; Ctrl+Z in Revit undoes it.", "Fill");
        if (edited is null) return loaded;
        var answer = string.Join("\n", rows.Zip(edited, (r, code) => (r.Id, Code: code)).Where(x => x.Code is not null).Select(x => x.Id + "\t" + x.Code));
        var result = answer.Length == 0 ? "No type was given a code." : await onModel(ui => AssemblyCodes.Apply(ui.Document, def, answer));
        ActivityLog.Note("assembly_codes", result);
        MessageBox.Show(owner, result, "Sketch ProMax: Assembly Codes", MessageBoxButton.OK, MessageBoxImage.Information);
        return result;
    }
}
