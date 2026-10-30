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

    /// <summary>A progress update. Null when it went, else why not.</summary>
    public static Task<string?> UpdateAsync(string text) => Try(c => c.UpdateAsync(text));

    /// <summary>A question with a one-tap button for the assumed answer. The message id (0 when it couldn't go), and why not.</summary>
    public static async Task<(long Id, string? Error)> AskAsync(string text, string assumed, DateTime deadlineUtc)
    {
        long id = 0;
        var error = await Try(async c => id = await c.AskAsync(text, assumed.Length > 0 ? [assumed] : null, assumed, deadlineUtc).ConfigureAwait(false)).ConfigureAwait(false);
        return (id, error);
    }

    /// <summary>What the phone sent since last time (answers, messages, Stop/Resume), or null when it couldn't be read.</summary>
    public static async Task<List<IrisMessage>?> InboxAsync()
    {
        List<IrisMessage>? got = null;
        var error = await Try(async c => got = await c.InboxAsync().ConfigureAwait(false)).ConfigureAwait(false);
        if (got is { Count: > 0 }) Save();
        return error is null ? got : null;
    }

    public static Task<string?> SendTestAsync() =>
        Try(c => c.UpdateAsync("Sketch ProMax is connected. Autopilot questions and progress will come here."));

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
