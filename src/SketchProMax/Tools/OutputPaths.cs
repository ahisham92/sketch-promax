using System.IO;
using System.Text.Json;

namespace SketchProMax.Tools;

/// <summary>
/// Checks on file paths Claude passes to the tools, and which calls from the in-Revit chat need the user's approval.
/// No Revit API here, so the tests check it directly.
/// </summary>
internal static class OutputPaths
{
    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Why a caller-supplied output path is refused, or null when it is fine: network shares (\\server\share),
    /// device paths (\\?\, \\.\, CON, NUL, COM1...) and alternate data streams (file.txt:stream). Local and mapped
    /// drive paths and relative names pass.
    /// </summary>
    public static string? Problem(string? path)
    {
        if (path is null) return null;
        var p = path.Trim().Trim('"');
        if (p.Length == 0) return null;
        if (p.Length >= 2 && p[0] is '\\' or '/' && p[1] is '\\' or '/')
            return "network shares (\\\\server\\...) and device paths (\\\\?\\, \\\\.\\) are not allowed; use a folder on a drive letter";
        var colon = p.IndexOf(':');
        if (colon >= 0 && !(colon == 1 && char.IsLetter(p[0]) && p.IndexOf(':', 2) < 0))
            return "a ':' is only allowed after a drive letter";
        foreach (var segment in p.Split('\\', '/'))
        {
            var name = segment.TrimEnd(' ', '.');
            var dot = name.IndexOf('.');
            if (dot >= 0) name = name.Substring(0, dot);
            if (DeviceNames.Contains(name.TrimEnd(' '))) return $"'{segment}' is a Windows device name, not a file or folder";
        }
        return null;
    }

    /// <summary>The path unchanged when it is fine (null stays null); otherwise an ArgumentException saying why.</summary>
    public static string? Checked(string? path) =>
        Problem(path) is { } why ? throw new ArgumentException($"Can't use '{path}': {why}.") : path;

    /// <summary>Tools that run code or a saved recipe of tool calls. From the chat, each call is shown to the user first.</summary>
    public static readonly HashSet<string> CodeTools = new(StringComparer.Ordinal)
    {
        "run_revit_query_code", "run_revit_code", "save_recipe", "run_recipe", "batch_run_recipe",
    };

    /// <summary>Arguments that name a file or folder a tool writes to (or, for some, reads from).</summary>
    private static readonly HashSet<string> PathKeys = new(StringComparer.Ordinal)
    {
        "folder", "output_folder", "report_folder", "file_name", "copy_path",
    };

    /// <summary>Tools whose "path" argument is where they write.</summary>
    private static readonly HashSet<string> WritesToPath = new(StringComparer.Ordinal) { "save_legend_seed" };

    /// <summary>
    /// Whether a call that came from the in-Revit chat must be approved in Revit whatever the settings say, and why
    /// (shown in the dialog). The chat runs Claude Code headless, so Revit's dialog is the only place a person sees it.
    /// </summary>
    public static string? ChatApprovalReason(string tool, IReadOnlyDictionary<string, JsonElement> args)
    {
        if (CodeTools.Contains(tool))
            return "The Sketch ProMax chat asks to run code or a recipe. Read it below before you allow it.";
        var reasons = new List<string>();
        // Changes to the open model itself (or to its design options) are always shown, whatever the settings say.
        if (tool == "ve_apply" && args.TryGetValue("target", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() is "main" or "design_option")
            reasons.Add(t.GetString() == "main"
                ? "The Sketch ProMax chat asks to apply value engineering changes to the open model itself."
                : "The Sketch ProMax chat asks to apply value engineering changes to elements in a design option of the open model.");
        var paths = args.Where(a => (PathKeys.Contains(a.Key) || a.Key == "path" && WritesToPath.Contains(tool))
                                    && a.Value.ValueKind == JsonValueKind.String && a.Value.GetString() is { Length: > 0 })
            .Select(a => FullPath(a.Value.GetString()!)).ToList();
        if (paths.Count > 0) reasons.Add("The Sketch ProMax chat asks to write files at a location it chose: " + string.Join(", ", paths) + ".");
        return reasons.Count > 0 ? string.Join(" ", reasons) : null;
    }

    /// <summary>The path as Windows will resolve it, so the dialog shows where a relative name really lands; unchanged when it can't be resolved.</summary>
    public static string FullPath(string path)
    {
        try { return Path.GetFullPath(path.Trim().Trim('"')); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException) { return path; }
    }
}
