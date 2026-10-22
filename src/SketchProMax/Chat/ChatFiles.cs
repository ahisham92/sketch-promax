using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SketchProMax.Chat;

/// <summary>
/// The files a step made that the chat shows as a card (Open, Show in folder, Save a copy): the "file" or "files" of a
/// tool's result, when they are Excel, Word, PowerPoint, PDF or CSV files. make_document is the main source; exports
/// such as a priced bill of quantities show the same way. No WPF here, so it can be tested anywhere.
/// </summary>
internal static class ChatFiles
{
    /// <summary>The file types shown as a card.</summary>
    public static readonly string[] Kinds = [".xlsx", ".xlsm", ".csv", ".docx", ".pptx", ".pdf"];

    /// <summary>At most this many files per step; the rest are in the same folder (Show in folder).</summary>
    public const int Most = 6;

    private static readonly Regex One = new(@"""file""\s*:\s*""((?:[^""\\]|\\.)*)""", RegexOptions.CultureInvariant);
    private static readonly Regex Many = new(@"""files""\s*:\s*\[\s*((?:""(?:[^""\\]|\\.)*""\s*,?\s*)*)\]", RegexOptions.CultureInvariant);
    private static readonly Regex Quoted = new(@"""((?:[^""\\]|\\.)*)""", RegexOptions.CultureInvariant);

    /// <summary>
    /// The document files named in a tool's result text (JSON, possibly cut short), in order, without repeats. Read
    /// with patterns rather than a JSON parse, because the chat keeps only the start of a long result.
    /// </summary>
    public static List<string> Find(string? result)
    {
        var found = new List<string>();
        if (string.IsNullOrEmpty(result)) return found;
        void Take(string escaped)
        {
            string path;
            try { path = JsonSerializer.Deserialize<string>("\"" + escaped + "\"") ?? ""; }
            catch (JsonException) { return; }
            path = path.Trim();
            if (path.Length == 0 || !IsDocument(path) || found.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
            found.Add(path);
        }
        foreach (Match m in One.Matches(result!)) Take(m.Groups[1].Value);
        foreach (Match m in Many.Matches(result!))
            foreach (Match q in Quoted.Matches(m.Groups[1].Value)) Take(q.Groups[1].Value);
        return found.Take(Most).ToList();
    }

    /// <summary>True for a full path whose type is one the card shows.</summary>
    public static bool IsDocument(string path)
    {
        try
        {
            // A full path: a drive letter (checked as text too, so the tests run off Windows) or a rooted one.
            if (!Regex.IsMatch(path, @"^[A-Za-z]:[\\/]") && !Path.IsPathRooted(path)) return false;
            var ext = Path.GetExtension(path);
            return Kinds.Contains(ext, StringComparer.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
    }

    /// <summary>The kind of file in plain words.</summary>
    public static string Kind(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".xlsx" or ".xlsm" => "Excel workbook",
        ".csv" => "CSV table (opens in Excel)",
        ".docx" => "Word document",
        ".pptx" => "PowerPoint presentation",
        ".pdf" => "PDF",
        _ => "File",
    };

    /// <summary>"12 KB", "1.4 MB".</summary>
    public static string Size(long bytes) => bytes < 1024 * 1024
        ? Math.Max(1, (int)Math.Round(bytes / 1024.0)).ToString(CultureInfo.InvariantCulture) + " KB"
        : (bytes / (1024.0 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";

    /// <summary>The Save a copy dialog's filter for this kind of file.</summary>
    public static string Filter(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return $"{Kind(path).Split(' ')[0]} (*{ext})|*{ext}|All files (*.*)|*.*";
    }
}
