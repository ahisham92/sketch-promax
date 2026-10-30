namespace SketchProMax.Chat;

/// <summary>
/// The autopilot's watch on the connection: the internet (the chat's service) and the company network the central model
/// sits on (VPN). Probed every 30 s while a run is on (ChatPane.AutopilotConnection); two failed probes in a row pause the
/// run, one good probe carries it on. A probe is trusted only once it has worked this session, so a PC whose proxy refuses
/// the probe never pauses for nothing. The rules are plain, tested; the probes themselves are in ConnectionProbe.
/// </summary>
internal sealed class ConnectionWatch
{
    public const string Internet = "internet", Network = "network";

    /// <summary>Failed probes in a row before the run pauses (about a minute).</summary>
    public const int FailsToPause = 2;

    public const string NoInternet = "no internet connection", NoNetwork = "the company network isn't reachable (VPN off?)";

    private int _internetFails, _networkFails;

    /// <summary>A probe of this kind has worked at least once this session.</summary>
    public bool InternetSeen { get; private set; }
    public bool NetworkSeen { get; private set; }

    /// <summary>What is down now ("" when nothing): <see cref="Internet"/> or <see cref="Network"/>.</summary>
    public string Down { get; private set; } = "";

    /// <summary>
    /// One round of probes (<paramref name="network"/> null when there is no company folder to probe). The change:
    /// "pause" (with what is down in <see cref="Down"/>), "resume", or "" for none.
    /// </summary>
    public string Record(bool internet, bool? network)
    {
        if (internet) { InternetSeen = true; _internetFails = 0; }
        else if (InternetSeen) _internetFails++;
        if (network == true) { NetworkSeen = true; _networkFails = 0; }
        else if (network == false && NetworkSeen) _networkFails++;
        else if (network is null) _networkFails = 0;

        var down = _internetFails >= FailsToPause ? Internet : _networkFails >= FailsToPause ? Network : "";
        if (Down.Length == 0 && down.Length > 0)
        {
            Down = down;
            return "pause";
        }
        // Back only once nothing is down: internet back while the VPN still isn't keeps the pause (with its new reason).
        if (Down.Length > 0)
        {
            var still = !internet && InternetSeen ? Internet : network == false && NetworkSeen ? Network : "";
            if (still.Length == 0)
            {
                Down = "";
                return "resume";
            }
            Down = still;
        }
        return "";
    }

    public static string Why(string down) => down == Network ? NoNetwork : NoInternet;

    /// <summary>A paused run's reason is the connection's (not a usage limit's, nor the user's).</summary>
    public static bool IsConnectionPause(AutopilotRun run) => run.State == AutopilotRun.Paused && run.Offline.Length > 0;

    /// <summary>The note in the log and chat when the connection came back.</summary>
    public static string BackLine(string down, DateTime since, DateTime now)
    {
        var minutes = Math.Max(1, (int)Math.Round((now - since).TotalMinutes));
        return $"The {(down == Network ? "company network" : "internet connection")} is back after {minutes} minute{(minutes == 1 ? "" : "s")}; the run carried on by itself.";
    }

    /// <summary>
    /// The jobs cut off by the drop: those of this run that failed while the connection was down (from a minute
    /// before it was seen down, since a job notices first). Each is sent <see cref="CarryOnLine"/>.
    /// </summary>
    public static List<ChatJob> CutOff(IEnumerable<ChatJob> jobs, int jobsBefore, DateTime since) =>
        jobs.Where(j => j.Number > jobsBefore && j.GoalCheck == 0 && j.State is JobBoard.Failed && j.Ended >= since.AddMinutes(-1)).ToList();

    public const string CarryOnLine =
        "The connection dropped during your work and is back now. First check what is already in the model (what you did before the drop " +
        "may be there, or rolled back), then carry on with your task from there. Don't do a part twice.";
}
