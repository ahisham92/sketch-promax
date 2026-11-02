using System.IO;
using System.Text.Json;
using System.Threading;

namespace SketchProMax.Phone;

/// <summary>
/// This PC's link to the user's phone through Iris (the user's own website, Settings › Chat › Your phone): the autopilot's
/// questions and progress go there and the answers, messages and Stop/Resume taps come back. The token and the message
/// key are kept in the user's own profile (iris.json); the key never goes to the website, only to the phone inside the QR
/// code. Calls are network calls: never on Revit's thread.
/// </summary>
internal static class PhoneLink
{
    /// <summary>What the phone shows as buttons on this tool's tab.</summary>
    public static readonly string[] Commands = ["Resume", "Stop"];

    private static readonly object Gate = new();
    private static IrisClient? _client;

    private static string StateFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax", "iris.json");

    private static IrisClient Client
    {
        get
        {
            lock (Gate)
            {
                if (_client is not null) return _client;
                IrisState? state = null;
                try { if (File.Exists(StateFile)) state = JsonSerializer.Deserialize<IrisState>(File.ReadAllText(StateFile)); }
                catch (Exception) { /* a damaged file: not connected */ }
                return _client = new IrisClient(state);
            }
        }
    }

    /// <summary>Connected to a phone: the autopilot's questions and progress go there.</summary>
    public static bool Connected => Client.State.Connected;

    /// <summary>
    /// The phone is on (the chat's Phone switch): questions, progress and replies go to it and it is read. Off while the
    /// user is at the PC, so nothing reaches the phone; the link stays, so turning it on again is instant. Kept in
    /// phone-off (there: off).
    /// </summary>
    public static bool On
    {
        get
        {
            if (_on is { } on) return on;
            try { _on = !File.Exists(OffFile); }
            catch (Exception) { _on = true; }
            return _on.Value;
        }
    }

    private static bool? _on;

    private static string OffFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax", "phone-off");

    /// <summary>Connected and on: what the autopilot and the chat go by.</summary>
    public static bool Active => Connected && On;

    /// <summary>Raised (on the caller's thread) when On or the connection changed.</summary>
    public static event Action? Changed;

    /// <summary>Turns the phone on or off at once; the file is written in the background.</summary>
    public static void SetOn(bool on)
    {
        _on = on;
        Task.Run(() =>
        {
            try
            {
                if (on) File.Delete(OffFile);
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(OffFile)!);
                    File.WriteAllText(OffFile, "");
                }
            }
            catch (Exception) { /* kept for this session */ }
        });
        Changed?.Invoke();
    }

    /// <summary>Tells the switch the connection changed (connected, or disconnected).</summary>
    public static void Notify() => Changed?.Invoke();

    /// <summary>The Iris address this PC is connected to (or last tried).</summary>
    public static string Site => Client.State.Site;

    public static void Save()
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
                File.WriteAllText(StateFile, JsonSerializer.Serialize(Client.State));
            }
            catch (Exception) { /* kept in memory for this session */ }
        }
    }

    /// <summary>Normalises what the user typed as the address: https added, trailing slash dropped, "/iris" added when only the site was given.</summary>
    public static string Address(string typed)
    {
        var text = (typed ?? "").Trim().TrimEnd('/');
        if (text.Length == 0) return "";
        if (!text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            text = "https://" + text;
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.AbsolutePath == "/" || uri.AbsolutePath.Length == 0))
            text += "/iris";
        return text;
    }

    /// <summary>Starts connecting: the link the phone scans (as a QR code).</summary>
    public static async Task<string> StartAsync(string site, CancellationToken ct = default)
    {
        var link = await Client.StartPairingAsync(Address(site), "Sketch ProMax", Environment.MachineName, Commands, ct).ConfigureAwait(false);
        Save();
        return link;
    }

    /// <summary>"waiting", "expired" or "connected".</summary>
    public static async Task<string> CheckAsync(CancellationToken ct = default)
    {
        var status = await Client.CheckPairingAsync(ct).ConfigureAwait(false);
        if (status != "waiting") Save();
        return status;
    }

    public static async Task DisconnectAsync()
    {
        try { await Client.DisconnectAsync().ConfigureAwait(false); }
        catch (Exception) { /* forgotten here even when the website can't be reached */ }
        Save();
    }

    /// <summary>A progress update, in <paramref name="topic"/>'s chat on the phone (the project). Null when it went, else why not.</summary>
    public static Task<string?> UpdateAsync(string text, string topic = "") => Try(c => c.UpdateAsync(text, default, topic));

    /// <summary>A question with a one-tap button for the assumed answer. The message id (0 when it couldn't go), and why not.</summary>
    public static async Task<(long Id, string? Error)> AskAsync(string text, string assumed, DateTime deadlineUtc, string topic = "")
    {
        long id = 0;
        var error = await Try(async c => id = await c.AskAsync(text, assumed.Length > 0 ? [assumed] : null, assumed, deadlineUtc, default, topic).ConfigureAwait(false)).ConfigureAwait(false);
        return (id, error);
    }

    // ---- One chat per project on the phone ---------------------------------------------------------------------------

    /// <summary>The projects a Revit on this PC has chats for, and when it last said so (phone-projects.json, by process).</summary>
    internal sealed class ProjectClaim
    {
        public List<string> Projects { get; set; } = [];
        public DateTime Beat { get; set; }
    }

    /// <summary>
    /// Which Revit takes a message the phone sent in <paramref name="topic"/>'s chat: one with a chat for that project, the
    /// one whose Autopilot runs first, then the Revit that takes the phone's messages (<paramref name="reader"/>), then the
    /// lowest process id, so every Revit on the PC works out the same one. A message in the main chat (""), or for a project
    /// no Revit has open, goes to the reader, which says so on the phone. Null when no Revit is known.
    /// </summary>
    internal static int? TopicTaker(string topic, IReadOnlyDictionary<int, ProjectClaim> claims, int? reader, int? runOwner,
        Func<int, bool> alive, DateTime nowUtc)
    {
        if (topic.Trim().Length == 0) return reader;
        var open = claims.Where(c => nowUtc - c.Value.Beat <= ReaderFresh && alive(c.Key) && c.Value.Projects.Any(p => PhoneProjects.Same(p, topic)))
            .Select(c => c.Key).OrderBy(p => p).ToList();
        if (open.Count == 0) return reader;
        if (runOwner is { } run && open.Contains(run)) return run;
        if (reader is { } r && open.Contains(r)) return r;
        return open[0];
    }

    private static string ProjectsFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax", "phone-projects.json");

    private static Dictionary<int, ProjectClaim> ReadClaims()
    {
        try
        {
            if (File.Exists(ProjectsFile))
                return JsonSerializer.Deserialize<Dictionary<int, ProjectClaim>>(File.ReadAllText(ProjectsFile)) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return [];
    }

    /// <summary>This Revit's projects with chats (the chat pane says, every minute): their phone messages come here.</summary>
    public static void Claim(IEnumerable<string> projects, DateTime nowUtc)
    {
        try
        {
            var claims = ReadClaims();
            foreach (var gone in claims.Where(c => nowUtc - c.Value.Beat > ReaderFresh || !Chat.AutopilotShare.IsAlive(c.Key)).Select(c => c.Key).ToList()) claims.Remove(gone);
            claims[Chat.AutopilotShare.Me] = new ProjectClaim
            {
                Projects = projects.Where(p => p.Trim().Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Beat = nowUtc,
            };
            Directory.CreateDirectory(Path.GetDirectoryName(ProjectsFile)!);
            File.WriteAllText(ProjectsFile, JsonSerializer.Serialize(claims));
        }
        catch (Exception) { /* the reader takes every message meanwhile */ }
    }

    /// <summary>Whether this Revit takes a message the phone sent in <paramref name="topic"/>'s chat (<paramref name="reader"/>: it takes the phone's messages).</summary>
    public static bool TakesTopic(string topic, bool reader, DateTime nowUtc)
    {
        try
        {
            var me = Chat.AutopilotShare.Me;
            int? readerPid = reader ? me : null;
            if (!reader)
                try { if (File.Exists(ReaderFile)) readerPid = JsonSerializer.Deserialize<ReaderState>(File.ReadAllText(ReaderFile))?.Pid; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
            var run = Chat.AutopilotShare.Any(nowUtc) is not null ? Chat.AutopilotShare.Read()?.Owner : null;
            return TopicTaker(topic, ReadClaims(), readerPid, run, Chat.AutopilotShare.IsAlive, nowUtc) is { } taker ? taker == me : reader;
        }
        catch (Exception) { return reader; }
    }

    // ---- One Revit takes the phone's messages ---------------------------------------------------------------------

    /// <summary>Which Revit on this PC takes the phone's messages and Stop/Resume (phone-reader.json).</summary>
    internal sealed class ReaderState
    {
        public int Pid { get; set; }
        public DateTime Beat { get; set; }
    }

    /// <summary>A reader not heard from for this long (closed, hung) is replaced.</summary>
    public static readonly TimeSpan ReaderFresh = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Whether this Revit (<paramref name="me"/>) takes the phone's messages: the Revit whose chat runs an autopilot when one
    /// runs, else the one that took them first while it keeps answering (<paramref name="held"/>), else this one.
    /// </summary>
    internal static bool ShouldRead(ReaderState? held, int me, int? runOwner, Func<int, bool> alive, DateTime nowUtc)
    {
        if (runOwner is { } owner) return owner == me || !alive(owner);
        return held is null || held.Pid == me || nowUtc - held.Beat > ReaderFresh || !alive(held.Pid);
    }

    private static string ReaderFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax", "phone-reader.json");

    /// <summary>True when this Revit takes the phone's messages now (it then says so in phone-reader.json). Any thread.</summary>
    public static bool TakesMessages(DateTime nowUtc)
    {
        try
        {
            ReaderState? held = null;
            try { if (File.Exists(ReaderFile)) held = JsonSerializer.Deserialize<ReaderState>(File.ReadAllText(ReaderFile)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
            var me = Chat.AutopilotShare.Me;
            var run = Chat.AutopilotShare.Any(nowUtc) is not null ? Chat.AutopilotShare.Read()?.Owner : null;
            if (!ShouldRead(held, me, run, Chat.AutopilotShare.IsAlive, nowUtc)) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(ReaderFile)!);
            File.WriteAllText(ReaderFile, JsonSerializer.Serialize(new ReaderState { Pid = me, Beat = nowUtc }));
            return true;
        }
        catch (Exception) { return true; /* the file can't be used: as before, this Revit answers */ }
    }

    /// <summary>The coordinator's name (Settings › Chat crew names): the phone shows the chat as from them (Sam, ...).</summary>
    public static string Agent { get; set; } = "";

    private static string _agentSent = "";

    /// <summary>What the phone sent since last time (answers, messages, Stop/Resume), or null when it couldn't be read.</summary>
    public static async Task<List<IrisMessage>?> InboxAsync()
    {
        List<IrisMessage>? got = null;
        var error = await Try(async c => got = await c.InboxAsync().ConfigureAwait(false)).ConfigureAwait(false);
        if (got is { Count: > 0 }) Save();
        if (error is null && Agent.Length > 0 && Agent != _agentSent)
        {
            var name = Agent;
            // An older Iris without names answers 404/405: asked again only when the name changes.
            try { await Client.SetAgentAsync(name).ConfigureAwait(false); _agentSent = name; }
            catch (IrisException) { _agentSent = name; }
            catch (Exception) { /* no network: the next read tries again */ }
        }
        return error is null ? got : null;
    }

    public static Task<string?> SendTestAsync(string topic = "") =>
        Try(c => c.UpdateAsync("Sketch ProMax is connected. Autopilot questions and progress will come here.", default, topic));

    private static async Task<string?> Try(Func<IrisClient, Task> call)
    {
        try
        {
            await call(Client).ConfigureAwait(false);
            return null;
        }
        catch (IrisException e) when (e.Disconnected)
        {
            Client.Forget();
            Save();
            Changed?.Invoke();
            return "your phone disconnected Sketch ProMax; connect it again in Settings › Chat";
        }
        catch (Exception e)
        {
            return e is TaskCanceledException ? "the website did not answer" : e.GetBaseException().Message;
        }
    }
}
