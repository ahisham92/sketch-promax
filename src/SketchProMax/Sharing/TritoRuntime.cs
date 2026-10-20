using System.IO;
using SketchProMax.Update;

namespace SketchProMax.Sharing;

/// <summary>
/// The TritoRevit add-in's own copy of this core (%LOCALAPPDATA%\TritoRevit\versions\&lt;version&gt;), kept up to date
/// without restarting Revit, from the same releases Sketch ProMax updates from: when a newer one is out it is downloaded
/// and unpacked there, and TritoRevit is told (AppDomain "TritoRevit.Pending"); it switches to it the next time Revit is
/// idle with nothing running. TritoRevit.dll itself (Addins\&lt;year&gt;\TritoRevit\TritoRevit.dll) is replaced only by a
/// higher TritoRevit version, as the Sketch ProMax loader is, so Revit's "Always Load" keeps holding.
/// Always automatic and silent: TritoRevit has no update window. No Revit API here.
/// </summary>
internal static class TritoRuntime
{
    public static string LocalRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TritoRevit");

    private static string VersionsFolder => Path.Combine(LocalRoot, "versions");

    private static string AddinsRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Autodesk", "Revit", "Addins");

    public const string Dll = "TritoRevit.dll", Manifest = "TritoRevit.addin", Folder = "TritoRevit";
    private const string PendingKey = "TritoRevit.Pending";

    /// <summary>True when TritoRevit is installed for some Revit version on this PC.</summary>
    public static bool Installed()
    {
        try { return Directory.Exists(AddinsRoot) && Directory.GetDirectories(AddinsRoot).Any(d => File.Exists(Path.Combine(d, Manifest))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Looks for a newer release and, when there is one, downloads it and asks TritoRevit to switch to it. Returns the
    /// new version, or null. Throws on network and disk errors (the caller retries later).
    /// </summary>
    public static async Task<string?> CheckAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(LocalRoot);
        FileStream gate;
        try { gate = new FileStream(Path.Combine(LocalRoot, "update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; } // another Revit window is updating; this one finds it on disk within a minute
        try
        {
            CleanUp();
            var release = await Updater.LatestReleaseAsync(ct).ConfigureAwait(false);
            if (release is null || Updater.Parse(release.Version) <= Updater.Current) return null;
            var folder = Path.Combine(VersionsFolder, release.Version);
            if (!File.Exists(Path.Combine(folder, "Revit" + App.RevitYear, "SketchProMax.dll")))
                folder = await Updater.DownloadAndUnpackAsync(release, new UpdateProgress(), ct, LocalRoot).ConfigureAwait(false);
            SettleLoaders(folder);
            var core = Path.Combine(folder, "Revit" + App.RevitYear, "SketchProMax.dll");
            if (File.Exists(core)) AppDomain.CurrentDomain.SetData(PendingKey, core);
            return release.Version;
        }
        finally { gate.Dispose(); }
    }

    /// <summary>
    /// Puts a newer TritoRevit.dll from <paramref name="versionFolder"/> (a version folder holding Revit&lt;year&gt;\TritoRevit)
    /// in place for every Revit version that has TritoRevit installed. Never throws.
    /// </summary>
    public static void SettleLoaders(string versionFolder) =>
        Settle(year => Path.Combine(versionFolder, "Revit" + year, Folder, Dll));

    /// <summary>
    /// For Sketch ProMax, which runs the TritoRevit tab when both are installed: TritoRevit.dll from the running build
    /// (its TritoRevit folder, for this Revit version only: an installed build holds one year).
    /// </summary>
    public static void SettleFromRunning()
    {
        var running = Path.GetDirectoryName(typeof(TritoRuntime).Assembly.Location);
        if (running is not null) Settle(year => year == App.RevitYear ? Path.Combine(running, Folder, Dll) : null);
    }

    private static void Settle(Func<string, string?> source)
    {
        try
        {
            if (!Directory.Exists(AddinsRoot)) return;
            foreach (var revit in Directory.GetDirectories(AddinsRoot))
            {
                if (!File.Exists(Path.Combine(revit, Manifest))) continue;
                if (source(Path.GetFileName(revit)) is { } from && File.Exists(from))
                    Updater.PlaceLoader(from, Path.Combine(revit, Folder, Dll), "TritoRevit");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    /// <summary>Old version folders nothing runs, and DLLs moved aside. Anything still loaded is left for next time.</summary>
    private static void CleanUp()
    {
        try
        {
            if (Directory.Exists(AddinsRoot))
                foreach (var revit in Directory.GetDirectories(AddinsRoot))
                {
                    var folder = Path.Combine(revit, Folder);
                    if (!Directory.Exists(folder)) continue;
                    foreach (var old in Directory.GetFiles(folder, "TritoRevit.*.old").Concat(Directory.GetFiles(folder, "TritoRevit.*.new")))
                        try { File.Delete(old); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            if (!Directory.Exists(VersionsFolder)) return;
            var running = Path.GetDirectoryName(Path.GetDirectoryName(typeof(TritoRuntime).Assembly.Location)) ?? "";
            var folders = Directory.GetDirectories(VersionsFolder)
                .Where(f => Version.TryParse(Path.GetFileName(f), out _))
                .OrderByDescending(f => Updater.Parse(Path.GetFileName(f))).ToList();
            // Keep the running one and the newest; the rest go once no Revit has them loaded (a rename fails while one does).
            foreach (var folder in folders.Skip(1))
            {
                if (string.Equals(Path.GetFullPath(folder).TrimEnd('\\', '/'), Path.GetFullPath(running).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) continue;
                var doomed = folder + ".del";
                try
                {
                    if (Directory.Exists(doomed)) Directory.Delete(doomed, true);
                    Directory.Move(folder, doomed);
                    Directory.Delete(doomed, true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            foreach (var partial in Directory.GetDirectories(VersionsFolder, "*.del"))
                try { Directory.Delete(partial, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
