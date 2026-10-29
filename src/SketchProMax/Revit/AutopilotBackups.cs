using System.IO;
using Autodesk.Revit.DB;

namespace SketchProMax.Revit;

/// <summary>
/// Autopilot's only safety net: each model it changes is saved and copied into a Backup folder beside it
/// ("Backup\Tower autopilot backup 2026-10-29 2310.rvt") before its first change, once per run, whichever Revit window
/// it is open in (Chat.AutopilotShare keeps which models were backed up).
/// </summary>
internal static class AutopilotBackups
{
    /// <summary>
    /// On Revit's thread, before a chat's change while an autopilot is on: backs up <paramref name="doc"/> the first time
    /// in the run. The copy's path, or null when nothing was done.
    /// </summary>
    public static string? Ensure(Document doc)
    {
        try
        {
            if (doc.IsFamilyDocument || doc.PathName.Length == 0 || !Chat.AutopilotShare.Claim(doc.PathName, DateTime.UtcNow)) return null;
            SaveFirst(doc);
            var copy = Copy(doc.PathName);
            Chat.Autopilot.Log(Chat.AutopilotEntry.Note, copy.Length > 0 ? $"Backup of \"{doc.Title}\" saved before its first change: {copy}"
                : $"No backup of \"{doc.Title}\" could be saved before its first change.");
            return copy;
        }
        catch (Exception) { return null; /* the change goes ahead; the log says no backup when it can */ }
    }

    /// <summary>Saves the model when it has unsaved changes, so the copy has it as it is now.</summary>
    public static void SaveFirst(Document doc)
    {
        if (doc.PathName.Length == 0 || !doc.IsModified || doc.IsReadOnly || doc.IsModifiable) return;
        try { doc.Save(); }
        catch (Exception) { /* the copy is then of the last save */ }
    }

    /// <summary>Copies the model's file into a Backup folder beside it; the copy's path, or "" when it couldn't.</summary>
    public static string Copy(string path)
    {
        try
        {
            var folder = Path.Combine(Path.GetDirectoryName(path) ?? "", "Backup");
            Directory.CreateDirectory(folder);
            var copy = Path.Combine(folder, Path.GetFileNameWithoutExtension(path) + " autopilot backup " + DateTime.Now.ToString("yyyy-MM-dd HHmm", System.Globalization.CultureInfo.InvariantCulture) + Path.GetExtension(path));
            File.Copy(path, copy, overwrite: true);
            return copy;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return ""; }
    }
}
