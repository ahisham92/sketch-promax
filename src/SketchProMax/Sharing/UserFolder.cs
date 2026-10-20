using System.IO;

namespace SketchProMax.Sharing;

/// <summary>
/// Where TritoRevit (App.Shared) puts what a colleague makes and the problem reports they send back:
/// Documents\TritoRevit - &lt;pack name&gt;, named after the pack that brought the checklist (else the only or first pack),
/// with Bugs inside for the report zips. Sketch ProMax keeps its own folders. No Revit API here.
/// </summary>
internal static class UserFolder
{
    /// <summary>For the tests: stands in for Documents.</summary>
    [ThreadStatic] internal static string? TestDocuments;

    private static string Documents => TestDocuments ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    /// <summary>The folder itself (not created here).</summary>
    public static string Root => Path.Combine(Documents, SharedPackMath.FolderName(SharedPacks.List().Select(i => i.Pack)));

    public static string Bugs => Path.Combine(Root, "Bugs");

    /// <summary>Where a checklist's Word file or PDF goes: this folder under TritoRevit, Documents\Sketch ProMax\Checklists otherwise.</summary>
    public static string Checklists => App.Shared ? Root : Path.Combine(Documents, "Sketch ProMax", "Checklists");

    public const string ReadMeName = "How to report a problem.txt";

    /// <summary>
    /// Makes the folder, its Bugs folder and the short how-to next to them, once TritoRevit runs. Never throws.
    /// </summary>
    public static void Ensure()
    {
        if (!App.Shared) return;
        try
        {
            Directory.CreateDirectory(Bugs);
            var readMe = Path.Combine(Root, ReadMeName);
            var text = SharedPackMath.ReadMe(SharedPacks.List().Select(i => i.Pack));
            if (!File.Exists(readMe) || File.ReadAllText(readMe) != text) File.WriteAllText(readMe, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Copies a report to "Bug reports" in the update folder of each installed pack, so the person who shared them gets it.
    /// Returns the folders it reached. Never throws: a PC that can't reach the folder still has the zip in Bugs.
    /// </summary>
    public static List<string> Deliver(string zip)
    {
        var reached = new List<string>();
        foreach (var from in SharedPacks.List().Select(i => i.Pack.UpdateFrom).Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!Directory.Exists(from)) continue;
                var target = Path.Combine(from, "Bug reports");
                Directory.CreateDirectory(target);
                File.Copy(zip, Path.Combine(target, Environment.UserName + " - " + Path.GetFileName(zip)), overwrite: true);
                reached.Add(target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
        return reached;
    }
}
