using System.Text.RegularExpressions;

namespace SketchProMax.Chat;

/// <summary>
/// What the chat says when its Revit tools didn't load or connect, by the way it reaches them: the connection inside
/// Revit (Bridge.ChatMcp) or the connector program (the fallback). A company PC that blocks starting programs it doesn't
/// trust (AppLocker, App Control) is named as that, with what to ask IT, rather than sending the user to reinstall.
/// No Revit here: the unit tests compile this file.
/// </summary>
internal static class ToolsNote
{
    // Node's EPERM/EACCES (Claude Code starting the program), Windows' own refusals, and the policy wordings.
    private static readonly Regex BlockedText = new(
        @"\bEPERM\b|\bEACCES\b|not permitted|access is denied|permission denied|blocked by (your )?(group policy|system administrator|administrator)|" +
        @"application control|software restriction|this program is blocked|has been blocked",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>True when an error says the PC refused to start or reach the program (a policy, not a missing file).</summary>
    public static bool IsBlocked(string? text) => !string.IsNullOrEmpty(text) && BlockedText.IsMatch(text);

    /// <summary>The tools aren't in the session at all.</summary>
    public static string Missing(bool inside, bool job = false) => inside
        ? $"The Revit tools didn't load {(job ? "for this job" : "in this chat")}. {(job ? "Send it a message to try again" : "Press New chat to try again")}; if it happens again, restart Revit."
        : $"The Revit tools didn't load {(job ? "for this job" : "in this chat")}. Run Install.cmd from the Sketch ProMax download again{(job ? "." : ", then press New chat.")}";

    /// <param name="status">Claude Code's word for it (failed, needs-auth...).</param>
    /// <param name="inside">The chat used the connection inside Revit, at <paramref name="where"/>; otherwise the connector program at <paramref name="where"/>.</param>
    /// <param name="blocked">The PC refused it (<see cref="IsBlocked"/>).</param>
    public static string Failed(string status, bool inside, string where, bool blocked, bool job = false)
    {
        var again = job ? "send the job a message" : "press New chat";
        if (blocked && !inside)
            return $"Your company blocks Sketch ProMax's helper program ({where}). Ask IT to allow it, or restart Revit so the chat connects inside Revit without it, then {again}.";
        if (blocked)
            return $"Your company blocks the chat's connection to Revit on this PC ({where}). Ask IT to allow connections to this PC's own address (127.0.0.1), then {again}.";
        return inside
            ? $"The Revit tools didn't connect ({status}). To try again, {again}; if it happens again, restart Revit."
            : $"The Revit tools didn't connect ({status}). Check that Sketch ProMax is installed (Install.cmd), then {again}.";
    }
}
