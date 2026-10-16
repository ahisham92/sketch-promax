using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.UI;
using SketchProMax.Chat;
using SketchProMax.Tools;

namespace SketchProMax.Bugs;

/// <summary>
/// "Export chats &amp; bugs": one zip in Documents\Sketch ProMax\Bugs\Exports for learning from how the add-in is used:
/// the in-Revit chats of every model updated in the period (readable Markdown with tool calls and errors), every bug
/// report zip (sent or not), the activity log for the period, the versions and the assemblies report. Text only, apart
/// from the screenshots already inside the bug reports. Read-only on the model.
/// </summary>
public sealed class ExportChatsAndBugsTool : IRevitTool
{
    public string Name => "export_chats_and_bugs";
    public string Description =>
        "Export chats & bugs: write ONE zip to Documents\\Sketch ProMax\\Bugs\\Exports with the in-Revit chats of every model " +
        "updated in the period (Markdown, tool calls and errors included), all bug report zips (sent and not sent), the " +
        "activity log for the period (every tool call and whether it worked), Bug log.md, the add-in and Revit versions and " +
        "the loaded-assemblies report. For the user to send to the developer so both the chats and the bugs can be learned " +
        "from. Read-only; nothing is sent anywhere.";
    public string PropertiesJson => """
    {
      "period": {"type": "string", "enum": ["day", "week", "all"], "description": "Chats and activity of the last day, the last week (default) or everything."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args) => ToolResult.Ok(ChatsAndBugsExport.Export(app, args.OptString("period") ?? "week"));
}

internal static class ChatsAndBugsExport
{
    private const long BugReportsCap = 40_000_000;

    public static string Folder => Path.Combine(BugLog.Root, "Exports");

    /// <summary>The start of the period, or null for everything.</summary>
    internal static DateTime? Since(string period, DateTime now) => period.Trim().ToLowerInvariant() switch
    {
        "day" => now.AddDays(-1),
        "week" => now.AddDays(-7),
        "all" => null,
        _ => throw new ArgumentException("period is day, week or all."),
    };

    private static void Step(string text)
    {
        if (SketchProMax.Bridge.Jobs.CancelRequested) throw new OperationCanceledException("Stopped on request (cancel_job). No export was written.");
        SketchProMax.Bridge.Jobs.Report("Export chats & bugs: " + text);
    }

    public static object Export(UIApplication app, string period)
    {
        var now = DateTime.Now;
        var since = Since(period, now);
        var files = new List<(string Name, byte[] Data)>();
        var notes = new List<string>();
        var utf8 = new UTF8Encoding(false);

        Step("chats");
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var chats = ChatStore.All()
            .Where(c => c.Items.Count > 0 && (since is null || c.Updated.ToLocalTime() >= since))
            .OrderByDescending(c => c.Updated).ToList();
        foreach (var conv in chats)
        {
            if (string.IsNullOrEmpty(conv.Title)) conv.Title = Path.GetFileNameWithoutExtension(conv.Key);
            var name = ProjectPack.Safe(conv.Title.Length > 0 ? conv.Title : "chat");
            var unique = name;
            for (var i = 2; !used.Add(unique); i++) unique = $"{name} ({i})";
            files.Add(($"chats/{unique}.md", utf8.GetBytes(ProjectPack.RenderChat(conv))));
        }

        Step("bug reports");
        long bugBytes = 0;
        var reports = BugLog.Reports().OrderByDescending(r => r.Created).ToList();
        var unsent = new HashSet<string>(BugLog.UnsentReports().Select(r => r.Path), StringComparer.OrdinalIgnoreCase);
        var bugCount = 0;
        foreach (var report in reports)
        {
            byte[] data;
            try { data = File.ReadAllBytes(report.Path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { notes.Add($"{Path.GetFileName(report.Path)} could not be read: {ex.Message}"); continue; }
            if (bugBytes + data.Length > BugReportsCap) { notes.Add($"{Path.GetFileName(report.Path)} and older reports left out to keep the zip small; send them from Bugs\\Reports."); break; }
            bugBytes += data.Length;
            bugCount++;
            files.Add(($"bugs/{Path.GetFileName(report.Path)}", data));
        }
        BugLog.RewriteLocal();
        try { if (File.Exists(BugLog.LocalLog)) files.Add(("Bug log.md", File.ReadAllBytes(BugLog.LocalLog))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        Step("activity log");
        var (activityFiles, calls, failed) = Activity(since);
        files.AddRange(activityFiles);

        Step("versions");
        string assemblies;
        try { assemblies = SketchProMax.Revit.AssemblyReport.Describe(); }
        catch (Exception ex) { assemblies = "## Assemblies\n\nCould not be read: " + ex.Message; }
        files.Add(("assemblies.md", utf8.GetBytes(assemblies)));

        var periodText = since is null ? "everything" : $"since {since:yyyy-MM-dd HH:mm}";
        var readme = new StringBuilder();
        readme.AppendLine($"# Sketch ProMax chats and bugs, {now:yyyy-MM-dd HH:mm}");
        readme.AppendLine();
        readme.AppendLine($"- Period: {periodText} (local time)");
        readme.AppendLine($"- Add-in: Sketch ProMax {Update.Updater.CurrentText}");
        readme.AppendLine($"- Revit: {app.Application.VersionName} ({app.Application.VersionNumber}, build {app.Application.VersionBuild})");
        readme.AppendLine();
        readme.AppendLine("| Part | What it is |");
        readme.AppendLine("|---|---|");
        readme.AppendLine($"| chats/ | {chats.Count} in-Revit chat(s) updated in the period, one per model, oldest message first, tool calls and errors included |");
        readme.AppendLine($"| bugs/ | {bugCount} bug report zip(s), sent and not sent ({reports.Count(r => unsent.Contains(r.Path))} not in the published log yet); each has report.md, a screenshot, the last tool calls and the assemblies |");
        readme.AppendLine("| Bug log.md | The published bug log with this PC's reports not sent yet |");
        readme.AppendLine($"| activity/ | {calls} tool call(s) in the period, one JSON line each ({failed} did not work) |");
        readme.AppendLine("| assemblies.md | Which copies of the shared libraries are loaded |");
        if (notes.Count > 0)
        {
            readme.AppendLine();
            readme.AppendLine("## Notes");
            readme.AppendLine();
            foreach (var n in notes) readme.AppendLine("- " + n);
        }

        Step("writing the zip");
        Directory.CreateDirectory(Folder);
        var file = Path.Combine(Folder, $"Chats and bugs {now:yyyy-MM-dd HHmm}.zip");
        if (File.Exists(file)) file = Path.Combine(Folder, $"Chats and bugs {now:yyyy-MM-dd HHmmss}.zip");
        var partial = file + ".partial";
        try
        {
            using (var stream = new FileStream(partial, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                void Add(string name, byte[] data)
                {
                    var entry = zip.CreateEntry(name, name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                    using var s = entry.Open();
                    s.Write(data, 0, data.Length);
                }
                Add("README.md", utf8.GetBytes(readme.ToString()));
                foreach (var (name, data) in files) Add(name, data);
            }
            File.Move(partial, file);
        }
        catch
        {
            try { File.Delete(partial); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
        var size = new FileInfo(file).Length;
        return new
        {
            file, size = (size / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB", period = periodText,
            chats = chats.Count, bug_reports = bugCount, tool_calls = calls, failed_calls = failed, notes,
            next = "Tell the user the zip is saved (the folder opens with it selected) and that they can send it to the developer.",
        };
    }

    /// <summary>The activity-log lines in the period, one file per day.</summary>
    private static (List<(string, byte[])> Files, int Calls, int Failed) Activity(DateTime? since)
    {
        var files = new List<(string, byte[])>();
        int calls = 0, failed = 0;
        var folder = Path.GetDirectoryName(ActivityLog.FileFor("2000-01-01"))!;
        if (!Directory.Exists(folder)) return (files, 0, 0);
        foreach (var day in Directory.GetFiles(folder, "*.jsonl").OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal))
        {
            var stem = Path.GetFileNameWithoutExtension(day);
            if (since is { } s && DateTime.TryParseExact(stem, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) && date < s.Date) continue;
            string[] lines;
            try
            {
                using var stream = new FileStream(day, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                lines = reader.ReadToEnd().Replace("\r", "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            }
            catch (IOException) { continue; }
            var kept = lines.Where(l => since is null || Time(l) is not { } t || t >= since).ToList();
            if (kept.Count == 0) continue;
            calls += kept.Count;
            failed += kept.Count(l => l.Contains("\"ok\":false"));
            files.Add(($"activity/{stem}.jsonl", Encoding.UTF8.GetBytes(string.Join("\n", kept) + "\n")));
        }
        return (files, calls, failed);
    }

    private static DateTime? Time(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.TryGetProperty("time", out var t) && DateTime.TryParse(t.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        }
        catch (JsonException) { return null; }
    }
}
