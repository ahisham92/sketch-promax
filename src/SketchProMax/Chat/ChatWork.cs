namespace SketchProMax.Chat;

/// <summary>
/// Chats that keep working while another is shown (ChatPane sessions): how many may work in the background, what the
/// switcher and the history say about each, and the one line when one more can't be left working. Needs no WPF or Revit
/// (the unit tests compile this file).
/// </summary>
internal static class ChatWork
{
    /// <summary>At most this many chats work in the background at once (each is a Claude Code, and its jobs, on your plan).</summary>
    public const int Limit = 3;

    /// <summary>How a background chat's work ended, until it is opened.</summary>
    public const string Done = "done", Failed = "failed";

    /// <summary>
    /// Why the shown chat, which has work going, can't be left now: <see cref="Limit"/> chats already work in the
    /// background (<paramref name="workingAway"/>), or its answer can't go on in the background (<paramref name="cantAnswerAway"/>:
    /// a connector older than the add-in). Null when it can. Nothing is stopped either way.
    /// </summary>
    public static string? WhyNotLeave(int workingAway, bool cantAnswerAway)
    {
        if (workingAway >= Limit)
            return $"{Limit} chats already work in the background, so this one stays open. Wait for one to finish (Chats, at the top), or press Stop here.";
        if (cantAnswerAway)
            return "This answer can't carry on in the background with this Sketch ProMax connector (run Install.cmd again to update it). Wait for it, or press Stop.";
        return null;
    }

    /// <summary>What the switcher and the history say about a chat in the background.</summary>
    public static string Label(bool working, string now, string ended, bool autopilot) =>
        working ? (autopilot ? "Autopilot working" : "Working") + (now.Length > 0 ? " · " + now : "…")
        : ended == Failed ? "Stopped with an error, not opened yet"
        : ended == Done ? "Finished, not opened yet"
        : "Stopped";

    /// <summary>The status line when a background chat's work ended.</summary>
    public static string FinishedLine(string name, string ended) =>
        ended == Failed ? $"“{name}” stopped with an error in the background. Open it from Chats at the top."
            : $"“{name}” finished in the background. Open it from Chats at the top.";

    /// <summary>The Chats pill: how many chats work, and finished, in the background.</summary>
    public static string PillText(int working, int finished, bool narrow)
    {
        if (working + finished == 0) return "Chats";
        if (narrow) return "Chats · " + (working + finished).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var parts = new List<string>();
        if (working > 0) parts.Add($"{working} working");
        if (finished > 0) parts.Add($"{finished} finished");
        return "Chats · " + string.Join(", ", parts);
    }
}
