using System.Collections.Generic;
using System.IO;

namespace SketchProMax.Bugs;

/// <summary>
/// The folders where Sketch ProMax keeps what a person makes and what goes wrong on this PC (Help > Open folders), so
/// they can be opened, or copied from a licensed colleague's PC, without knowing the paths. The paths are spelled out
/// like ChatStore's, so this builds without Revit (unit tests); a test checks they match the folders the add-in uses.
/// </summary>
internal static class UserFolders
{
    internal sealed record Folder(string Title, string Path, string Tip);

    private static string Documents => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    private static string Roaming => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax");
    private static string Local => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SketchProMax");

    public static IReadOnlyList<Folder> List() =>
    [
        new("Bugs", BugLog.Root, "Report bug zips (Reports), Export chats & bugs zips (Exports) and the bug log."),
        new("Learned tools", System.IO.Path.Combine(Roaming, "learned-tools"), "The tools saved from the chat, one .json file each."),
        new("Recipes", System.IO.Path.Combine(Roaming, "recipes"), "Saved recipes: steps the chat can run again."),
        new("Chats", System.IO.Path.Combine(Local, "chat", "conversations"), "The saved chats of every model."),
        new("Checklists", System.IO.Path.Combine(Documents, "Sketch ProMax", "Checklists"), "The checklist Word files and PDFs."),
        new("PDF backups", System.IO.Path.Combine(Documents, "Sketch ProMax", "PDF backups"), "The old file each time the PDF editor saves over a PDF."),
        new("Change files", System.IO.Path.Combine(Documents, "Sketch ProMax", "Change files"), "Each model's changes since its last save, to replay after a crash (Lifeline)."),
        new("Field tests", System.IO.Path.Combine(Local, "field-tests"), "Field test reports."),
        new("Settings", Roaming, "Settings, license and everything above that is saved per user."),
    ];
}
