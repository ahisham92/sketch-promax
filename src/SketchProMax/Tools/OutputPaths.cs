using System.IO;
using System.Text.Json;

namespace SketchProMax.Tools;

/// <summary>
/// Checks on file paths Claude passes to the tools, and the calls that are always asked about (see ApprovalPolicy for
/// the rest of the approval rules). No Revit API here, so the tests check it directly.
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

    /// <summary>Tools that run or save code or recipes of tool calls (left out of the chat when its connector is too old to say it is the chat).</summary>
    public static readonly HashSet<string> CodeTools = new(StringComparer.Ordinal)
    {
        "run_revit_query_code", "run_revit_code", "save_recipe", "run_recipe", "batch_run_recipe",
    };

    /// <summary>
    /// Calls that are asked about in Revit whatever the settings say and wherever they come from (the chat, the Claude app
    /// or a recipe), and why: what_if with confirm, which copies the saved model and changes the copy (or a design option);
    /// accepting issue check items, and issuing with open items.
    /// </summary>
    public static string? AlwaysAskReason(string tool, IReadOnlyDictionary<string, JsonElement> args)
    {
        // The issue gate's ways past an open item: only the user decides them, so Claude can't clear the gate on its own.
        if (tool == "issue_check" && args.TryGetValue("accept", out var acc) && acc.ValueKind == JsonValueKind.Array && acc.GetArrayLength() > 0)
            return $"Claude asks to accept {acc.GetArrayLength()} open item(s) of the issue check with the reasons in the details; accepted items no longer block the issue.";
        if (tool == "issue_drawings" && args.TryGetValue("allow_open_items", out var allow) && allow.ValueKind == JsonValueKind.True)
            return "Claude asks to issue these drawings even though the issue check has open items (the reason in the details goes into the issue record).";
        if (tool != "what_if" || !(args.TryGetValue("confirm", out var c) && c.ValueKind == JsonValueKind.True)) return null;
        var option = args.TryGetValue("target", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == "design_option";
        return option
            ? "Claude asks to try a what-if in a design option of the open model: only the elements in that option change (Ctrl+Z undoes it)."
            : "Claude asks to try a what-if on a copy of the saved model: the copy is changed, measured, saved and closed; the open model is not changed.";
    }

    /// <summary>The path as Windows will resolve it, so the dialog shows where a relative name really lands; unchanged when it can't be resolved.</summary>
    public static string FullPath(string path)
    {
        try { return Path.GetFullPath(path.Trim().Trim('"')); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException) { return path; }
    }
}
