using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using Autodesk.Revit.UI;
using SketchProMax.Tools;
using SketchProMax.UI;

namespace SketchProMax.Bugs;

/// <summary>
/// Writes one small bug report zip to Documents\Sketch ProMax\Bugs\Reports: the user's one-line description, a JPEG of
/// Revit's main window, the add-in and Revit versions, the model's name, the last activity-log entries (failed calls
/// included) and the assemblies report. Used by the Report bug button (which asks for the description) and by the
/// report_bug tool (Claude passes it). Runs on Revit's thread.
/// </summary>
internal static class BugReport
{
    private const int MaxActivity = 50, ScreenshotWidth = 1600;

    /// <summary>A screenshot taken before any dialog opens, or null with the reason.</summary>
    public static (byte[]? Jpeg, string? Error) Screenshot()
    {
        try { return (ScreenGrab.Window(App.MainWindow, ScreenshotWidth), null); }
        catch (Exception ex) { return (null, ex.Message); }
    }

    /// <summary>Writes the zip and refreshes Bug log.md. Returns the zip's path.</summary>
    public static string Create(UIApplication app, string description, (byte[]? Jpeg, string? Error) shot, string from)
    {
        description = string.Join(" ", (description ?? "").Replace("\r", " ").Replace("\n", " ").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
        if (description.Length == 0) throw new ArgumentException("Say in one line what went wrong.");
        if (description.Length > 500) description = description.Substring(0, 500) + "…";

        var now = DateTime.Now;
        Directory.CreateDirectory(BugLog.ReportsFolder);
        var (name, id) = BugLog.NameFor(now);
        var file = Path.Combine(BugLog.ReportsFolder, name);
        if (File.Exists(file))
        {
            (name, id) = BugLog.NameFor(now, seconds: true);
            file = Path.Combine(BugLog.ReportsFolder, name);
        }

        var doc = app.ActiveUIDocument?.Document;
        var model = "(no model open)";
        var view = "";
        try
        {
            if (doc is not null) model = (doc.IsFamilyDocument ? "family " : "") + ProjectPack.ModelName(doc);
            view = app.ActiveUIDocument?.ActiveView?.Name ?? "";
        }
        catch (Exception) { /* a document being closed */ }
        var activity = Activity(MaxActivity);
        var failed = activity.Count(l => l.Contains("\"ok\":false"));

        var md = new StringBuilder();
        md.AppendLine($"# Bug report {id}");
        md.AppendLine();
        md.AppendLine($"**What happens:** {description}");
        md.AppendLine();
        md.AppendLine($"- Reported: {now:yyyy-MM-dd HH:mm} (local time), from {from}");
        md.AppendLine($"- Add-in: Sketch ProMax {Update.Updater.CurrentText}");
        md.AppendLine($"- Revit: {app.Application.VersionName} ({app.Application.VersionNumber}, build {app.Application.VersionBuild})");
        md.AppendLine($"- Model: {model}" + (view.Length > 0 ? $", view: {view}" : ""));
        md.AppendLine($"- Screenshot: {(shot.Jpeg is not null ? "screenshot.jpg (Revit's main window)" : "none (" + shot.Error + ")")}");
        md.AppendLine($"- Activity: the last {activity.Count} tool calls in activity.jsonl ({failed} did not work), oldest first");
        md.AppendLine();
        string assemblies;
        try { assemblies = SketchProMax.Revit.AssemblyReport.Describe(); }
        catch (Exception ex) { assemblies = "## Assemblies\n\nCould not be read: " + ex.Message; }

        var partial = file + ".partial";
        try
        {
            using (var stream = new FileStream(partial, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                void Add(string entryName, byte[] data, bool compress = true)
                {
                    var entry = zip.CreateEntry(entryName, compress ? CompressionLevel.Optimal : CompressionLevel.NoCompression);
                    using var s = entry.Open();
                    s.Write(data, 0, data.Length);
                }
                var utf8 = new UTF8Encoding(false);
                Add("description.txt", utf8.GetBytes(description));
                Add("report.md", utf8.GetBytes(md.ToString()));
                Add("activity.jsonl", utf8.GetBytes(string.Join("\n", activity) + "\n"));
                Add("assemblies.md", utf8.GetBytes(assemblies));
                if (shot.Jpeg is not null) Add("screenshot.jpg", shot.Jpeg, compress: false);
            }
            File.Move(partial, file);
        }
        catch
        {
            try { File.Delete(partial); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
        BugLog.RewriteLocal();
        return file;
    }

    /// <summary>The last <paramref name="max"/> activity-log lines across the daily files, oldest first.</summary>
    internal static List<string> Activity(int max)
    {
        var lines = new List<string>();
        try
        {
            var folder = Path.GetDirectoryName(ActivityLog.FileFor("2000-01-01"))!;
            if (!Directory.Exists(folder)) return lines;
            foreach (var day in Directory.GetFiles(folder, "*.jsonl").OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal).Take(30))
            {
                string[] text;
                try
                {
                    using var stream = new FileStream(day, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    text = reader.ReadToEnd().Replace("\r", "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                }
                catch (IOException) { continue; }
                lines.InsertRange(0, text.Skip(Math.Max(0, text.Length - (max - lines.Count))));
                if (lines.Count >= max) break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return lines;
    }

    /// <summary>The Report bug button: screenshot first, then a one-line description, then the zip.</summary>
    public static Result Command(UIApplication app)
    {
        var shot = Screenshot();
        var window = new InputsWindow("Report bug", "One small zip for the Sketch ProMax developer.",
            "Say in one line what went wrong. The report also holds a picture of the Revit window, the versions, the model's name, " +
            "the last tool calls and their errors. Nothing is sent: the zip is saved in Documents\\Sketch ProMax\\Bugs\\Reports.",
            [],
            [new InputsWindow.Field("description", "What went wrong", "For example: the tags are placed on top of each other in Level 2.", "string", true, null, null)],
            "Save report");
        if (window.ShowDialog() != true) return Result.Cancelled;
        string file;
        try { file = Create(app, window.Values["description"], shot, "the Report bug button"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            TaskDialog.Show("Sketch ProMax", $"The bug report could not be saved: {ex.Message}");
            return Result.Failed;
        }
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true })?.Dispose(); }
        catch (Exception) { TaskDialog.Show("Sketch ProMax", $"Bug report saved: {file}"); }
        return Result.Succeeded;
    }

    /// <summary>The Open bug folder button.</summary>
    public static Result OpenFolder()
    {
        string folder;
        try { folder = BugLog.EnsureFolder(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TaskDialog.Show("Sketch ProMax", $"Couldn't create the bug folder: {ex.Message}");
            return Result.Failed;
        }
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true })?.Dispose();
            return Result.Succeeded;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            TaskDialog.Show("Sketch ProMax", $"Couldn't open {folder}: {ex.Message}");
            return Result.Failed;
        }
    }
}

/// <summary>Files a bug report when the user says something is a bug.</summary>
public sealed class ReportBugTool : IRevitTool
{
    public string Name => "report_bug";
    public string Description =>
        "File a Sketch ProMax bug report when the user says something is a bug (\"that's a bug\", \"report this\"). Writes ONE " +
        "small zip to Documents\\Sketch ProMax\\Bugs\\Reports with the description, a picture of the Revit window, add-in and " +
        "Revit versions, the model name, the last 50 tool calls with their errors and the loaded-assemblies report. Nothing is " +
        "sent anywhere; the user sends the zip. Write the description yourself from the conversation: one plain line saying " +
        "what the user did, what happened and what they expected, with the tool name. Read-only on the model.";
    public string PropertiesJson => """
    {
      "description": {"type": "string", "description": "One line: what happened, with the tool involved and what was expected."}
    }
    """;
    public string[] Required => ["description"];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var file = BugReport.Create(app, args.String("description"), BugReport.Screenshot(), "the chat (report_bug)");
        var size = new FileInfo(file).Length;
        return ToolResult.Ok(new
        {
            file, size = (size / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB",
            folder = BugLog.Root,
            next = "Tell the user the report is saved and listed under \"Not sent yet\" in Bug log.md in that folder (Open bug folder on the " +
                   "Sketch ProMax tab), and that they can send the zip to the developer.",
        });
    }
}
