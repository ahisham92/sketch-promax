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

    // ---- One chat at a time changes a model -----------------------------------------------------------------------------

    /// <summary>
    /// A chat working in the background takes the model changes asked in the shown chat when it works on the same model
    /// and is an Autopilot run or a coordinator with jobs open: two chats changing one model side by side would undo
    /// each other's work.
    /// </summary>
    public static bool TakesHandoff(bool sameModel, bool autopilotOn, int jobsOpen) => sameModel && (autopilotOn || jobsOpen > 0);

    /// <summary>
    /// Whether a model change from the caller <paramref name="key"/> (the bridge's caller key: null for the shown chat's
    /// own answer, a job's key, or a background chat's key) is refused and handed over: only the shown chat's own answer
    /// and its jobs (<paramref name="shownJob"/>), and only while another chat works on its model (<paramref name="elsewhere"/>).
    /// </summary>
    public static bool RefusesChange(string? key, bool shownJob, bool elsewhere) =>
        elsewhere && (key is null || (!JobBoard.IsChatKey(key) && shownJob));

    /// <summary>What the refused call answers, so the chat stops changing the model and says where the request went.</summary>
    public static string HandoffRefusal(string chat) =>
        $"Not run: the chat “{chat}” is already working on this model, so the user's request was handed to it and it will do it there. " +
        "A note already tells the user so. Don't change the model from this chat (reading is fine): answer only what the request asks to know, else end with no more than a short line.";

    /// <summary>The one line in the chat the request came from.</summary>
    public static string HandedLine(string chat) => $"Sent to “{chat}”, which is already working on this model; it will do it there.";

    /// <summary>The turn the working chat's coordinator gets with the requests handed to it.</summary>
    public static string HandedTurn(IReadOnlyList<string> requests, string from) =>
        $"[Handed over from the user's chat “{from}”, typed by the user there] The user asked for this while you work on the same model, " +
        "so it comes to you instead of a second chat changing the model alongside your work. Fit it into your work (a job, or a message " +
        "to the job it belongs to), without undoing what is done:\n\n" + string.Join("\n\n", requests);

    // ---- Messages typed while an answer ran ------------------------------------------------------------------------------

    /// <summary>
    /// Messages typed while an answer ran ("will apply after this item") that no step picked up go as the next message
    /// however the answer ended (finished, Stop, killed, failed or stuck): never left behind a dead answer. Only a model
    /// switch waiting (<paramref name="hold"/>), the pane closing or a chat not ready keeps them back.
    /// </summary>
    public static bool LeftoverGoes(bool hold, bool closing, bool ready) => !hold && !closing && ready;
}
