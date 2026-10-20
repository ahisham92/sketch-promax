using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace SketchProMax.Bugs;

/// <summary>
/// The bug folder in Documents\Sketch ProMax\Bugs: Reports\ holds the bug report zips this PC wrote, and "Bug log.md" is
/// the published bug log (docs/bug-log.md in the public repository, downloaded at every update check) followed by a
/// "Not sent yet" list of local reports. A report counts as sent only once a row of the published log's tables names its
/// id (the time in the zip's name), so a report older than the newest row but never logged stays listed. The local file
/// is rewritten automatically and says so at its top: the zips in Reports\ are the source. A failed download changes
/// nothing: the last copy stays. No Revit or WPF here.
/// </summary>
internal static class BugLog
{
    public const string PublishedUrl = "https://raw.githubusercontent.com/ahisham92/sketch-promax/main/docs/bug-log.md";

    private static readonly object Gate = new();

    /// <summary>Set only by the self-test and field test around report_bug and export_chats_and_bugs, so their zips go to the
    /// test's temporary folder. Not a tool argument.</summary>
    internal static string? RootOverride;

    /// <summary>Set by TritoRevit's core (App.Shared): its Bugs folder, Documents\TritoRevit - &lt;pack&gt;\Bugs (Sharing/UserFolder).
    /// The zips go straight into it, with no bug log.</summary>
    internal static Func<string>? SharedRoot;

    private static bool InShared => SharedRoot is not null && RootOverride is null;

    public static string Root => RootOverride ?? SharedRoot?.Invoke()
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Sketch ProMax", "Bugs");
    public static string ReportsFolder => InShared ? Root : Path.Combine(Root, "Reports");
    public static string LocalLog => Path.Combine(Root, "Bug log.md");

    /// <summary>The last published log downloaded, kept apart so the local file can be rebuilt after a new report.</summary>
    private static string PublishedCopy => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SketchProMax", "bug-log-published.md");

    /// <summary>A report zip: its id is the time in its name ("2026-10-17 1432"), the description its one line.</summary>
    internal sealed record Report(string Path, string Id, DateTime Created, string Description);

    /// <summary>The file name of a new report, and its id.</summary>
    public static (string Name, string Id) NameFor(DateTime when, bool seconds = false)
    {
        var id = when.ToString(seconds ? "yyyy-MM-dd HHmmss" : "yyyy-MM-dd HHmm", CultureInfo.InvariantCulture);
        return ($"Bug report {id}.zip", id);
    }

    private static readonly Regex NameId = new(@"^Bug report (\d{4}-\d\d-\d\d \d{4}(?:\d\d)?)$", RegexOptions.CultureInvariant);

    // ---- Pure text --------------------------------------------------------------------------------------------------

    /// <summary>The published log's table rows, where a report's id counts (an id mentioned in the intro does not).</summary>
    private static string Rows(string published) =>
        string.Join("\n", published.Replace("\r", "").Split('\n').Where(l => l.TrimStart().StartsWith("|", StringComparison.Ordinal)));

    /// <summary>Reports no row of the published log names yet, newest first.</summary>
    public static List<Report> Unsent(string? published, IEnumerable<Report> reports)
    {
        var rows = published is null ? "" : Rows(published);
        return reports
            .Where(r => rows.IndexOf(r.Id, StringComparison.OrdinalIgnoreCase) < 0)
            .OrderByDescending(r => r.Created).ToList();
    }

    /// <summary>The first line of the local file: it is rebuilt, so edits there are lost.</summary>
    public const string RewrittenNote =
        "> This file is rewritten automatically (at every update check and after each new report); edits made here are lost. " +
        "The report zips in the Reports folder are the source.";

    /// <summary>The local "Bug log.md": the published log as it is, then the local reports not sent yet.</summary>
    public static string Compose(string? published, IReadOnlyList<Report> unsent, DateTime? downloaded)
    {
        var sb = new StringBuilder();
        sb.AppendLine(RewrittenNote);
        sb.AppendLine();
        if (published is null)
        {
            sb.AppendLine("# Sketch ProMax bug log");
            sb.AppendLine();
            sb.AppendLine("The published bug log has not been downloaded yet (it is fetched at every update check, at Revit start and hourly).");
        }
        else
        {
            sb.AppendLine(published.TrimEnd());
            if (downloaded is { } when)
            {
                sb.AppendLine();
                sb.AppendLine($"_Downloaded {when:yyyy-MM-dd HH:mm}._");
            }
        }
        sb.AppendLine();
        sb.AppendLine("## Not sent yet");
        sb.AppendLine();
        if (unsent.Count == 0)
        {
            sb.AppendLine("No reports waiting. Use Report bug on the Sketch ProMax tab or in the pane, or tell Sketch ProMax in the Revit chat \"that's a bug\".");
            return sb.ToString();
        }
        sb.AppendLine("Bug reports on this PC that the log above doesn't list yet. Send the zips in the Reports folder (newest first):");
        sb.AppendLine();
        sb.AppendLine("| Report | What happens |");
        sb.AppendLine("|---|---|");
        foreach (var r in unsent)
            sb.AppendLine($"| {Path.GetFileName(r.Path)} | {r.Description.Replace("|", "/").Replace("\r", " ").Replace("\n", " ")} |");
        return sb.ToString();
    }

    /// <summary>True when downloaded text looks like the bug log (not an error page).</summary>
    public static bool LooksLikeLog(string text) =>
        text.TrimStart().StartsWith("#", StringComparison.Ordinal) && text.IndexOf("|", StringComparison.Ordinal) >= 0 && text.Length < 2_000_000;

    // ---- Files ------------------------------------------------------------------------------------------------------

    /// <summary>The report zips in <paramref name="folder"/>, with the one-line description read from inside each.</summary>
    public static List<Report> Reports(string? folder = null)
    {
        folder ??= ReportsFolder;
        var list = new List<Report>();
        if (!Directory.Exists(folder)) return list;
        foreach (var path in Directory.GetFiles(folder, "*.zip"))
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            var m = NameId.Match(stem);
            var id = m.Success ? m.Groups[1].Value : stem;
            var created = m.Success && DateTime.TryParseExact(id, new[] { "yyyy-MM-dd HHmm", "yyyy-MM-dd HHmmss" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d : File.GetLastWriteTime(path);
            list.Add(new Report(path, id, created, Description(path)));
        }
        return list;
    }

    private static string Description(string zip)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zip);
            var entry = archive.GetEntry("description.txt");
            if (entry is null) return "(no description)";
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            var text = reader.ReadToEnd().Trim();
            return text.Length == 0 ? "(no description)" : text.Length > 300 ? text.Substring(0, 300) + "…" : text;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { return "(could not be read)"; }
    }

    /// <summary>The local reports not sent yet, against the last downloaded log.</summary>
    public static List<Report> UnsentReports()
    {
        try { return Unsent(ReadPublished(), Reports()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static string? ReadPublished()
    {
        try { return File.Exists(PublishedCopy) ? File.ReadAllText(PublishedCopy) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Rewrites "Bug log.md" from the last downloaded log and the reports on this PC. Never throws.</summary>
    public static void RewriteLocal()
    {
        if (InShared) return; // TritoRevit's Bugs folder holds only the zips
        try
        {
            lock (Gate)
            {
                var published = ReadPublished();
                DateTime? when = published is null ? null : File.GetLastWriteTime(PublishedCopy);
                var text = Compose(published, Unsent(published, Reports()), when);
                Directory.CreateDirectory(Root);
                File.WriteAllText(LocalLog, text, new UTF8Encoding(false));
            }
        }
        catch (Exception) { /* the bug log is a convenience: never in the way */ }
    }

    /// <summary>
    /// Downloads the published log and rewrites the local file. Silent: a failed download (offline, GitHub down, an
    /// error page) leaves the last downloaded copy, and the local file, as they were.
    /// </summary>
    public static async Task SyncAsync(string userAgent, CancellationToken ct)
    {
        try
        {
            string text;
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
                using var response = await http.GetAsync(PublishedUrl, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return;
                text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            if (!LooksLikeLog(text)) return;
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PublishedCopy)!);
                File.WriteAllText(PublishedCopy, text, new UTF8Encoding(false));
            }
            RewriteLocal();
        }
        catch (Exception) { /* silent by design */ }
    }

    /// <summary>Creates the bug folder (with an up-to-date Bug log.md) and returns it.</summary>
    public static string EnsureFolder()
    {
        Directory.CreateDirectory(ReportsFolder);
        RewriteLocal();
        return Root;
    }
}
