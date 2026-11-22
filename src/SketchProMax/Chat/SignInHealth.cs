using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace SketchProMax.Chat;

/// <summary>
/// Whether the chat's sign-in really works. Claude Code's "auth status" only says a sign-in is saved: when the saved
/// sign-in has expired and can't be refreshed it still says signed in, and every message then fails with "Failed to
/// authenticate". So a failure like that is remembered here (with the saved sign-in's file stamp, also across Revit
/// restarts), and the chat shows "Sign-in expired" with Sign in again until the user signs in (the file changes), an
/// answer works again, or they press Check again.
/// </summary>
internal static class SignInHealth
{
    private static readonly object Gate = new();
    private static bool _loaded;
    private static string? _failedStamp; // the credentials file's stamp when the sign-in failed; null = no failure known

    /// <summary>Raised (on any thread) when the state changes: a failure, a sign-in, a working answer.</summary>
    public static event Action? Changed;

    /// <summary>
    /// Text that says the saved sign-in no longer works (expired, refused, signed out). A network that blocked the
    /// request is not that (bug 137): Claude Code says "Failed to authenticate. API Error: 403 Web Page Blocked" when
    /// the office web filter answers instead of the service, and the sign-in itself is still fine.
    /// </summary>
    public static bool LooksExpired(string text) => !LooksBlocked(text) && Regex.IsMatch(text ?? "",
        @"failed to authenticate|oauth session expired|session expired and could not be refreshed|oauth token (has )?expired|" +
        @"oauth token (has been )?revoked|invalid (oauth|bearer) token",
        RegexOptions.IgnoreCase);

    /// <summary>
    /// Text that says the network stopped the request before it reached the service: an office web filter or proxy page
    /// ("403 Web Page Blocked"), or the connection failed. The sign-in is fine; the same request works once the network
    /// lets it through, so it is tried again instead of asking the user to sign in (bug 137).
    /// </summary>
    public static bool LooksBlocked(string text) => Regex.IsMatch(text ?? "",
        @"web ?page blocked|page (has been |is |was )?blocked|blocked by (your |the )?(administrator|admin|proxy|firewall|network|organi[sz]ation|policy)|" +
        @"\b40[37]\b[^\n]{0,40}\b(blocked|proxy|firewall)\b|proxy authentication required|<\s*html|<!doctype html|" +
        @"econnreset|econnrefused|econnaborted|etimedout|enotfound|eai_again|enetunreach|socket hang up|fetch failed|getaddrinfo|" +
        @"unable to connect|connection (error|refused|reset|timed out)|network (error|is unreachable)|" +
        @"unable to (get|verify) [^\n]{0,40}certificate|self[- ]signed certificate",
        RegexOptions.IgnoreCase);

    /// <summary>What the network said, short, for the user (the first line, at most <paramref name="max"/> characters).</summary>
    public static string BlockedWords(string text, int max = 120)
    {
        var line = Regex.Replace((text ?? "").Trim(), @"\s+", " ");
        line = Regex.Replace(line, @"^failed to authenticate\.?\s*", "", RegexOptions.IgnoreCase);
        if (Regex.IsMatch(line, @"<\s*html|<!doctype", RegexOptions.IgnoreCase)) line = "a blocked-page reply";
        return line.Length <= max ? line : line.Substring(0, max).TrimEnd() + "…";
    }

    public const string NetBlockedStart = "The network blocked the connection";

    /// <summary>Times a job the network blocked is started again by itself before it is reported.</summary>
    public const int NetRetries = 3;

    /// <summary>What a job is sent when it carries on after the network blocked it.</summary>
    public const string NetCarryOn = "The network blocked your last request for a moment (the sign-in is fine). Carry on from where you stopped " +
                                     "(what you found or changed stays) and finish your task. Don't end without your report.";

    /// <summary>A job's summary when the network blocked it.</summary>
    public static string JobNetBlocked(string said) =>
        $"{NetBlockedStart} ({BlockedWords(said)}). Your sign-in is fine. It tries again by itself; " +
        "if it keeps happening, check the internet or VPN, or ask IT why the page is blocked.";

    public static bool IsNetBlocked(string summary) => (summary ?? "").StartsWith(NetBlockedStart, StringComparison.Ordinal);

    /// <summary>The wait before try <paramref name="attempt"/>: 1, 2, then 4 minutes, so a short block passes.</summary>
    public static TimeSpan NetRetryWait(int attempt) => TimeSpan.FromMinutes(Math.Pow(2, Math.Max(0, Math.Min(attempt, NetRetries) - 1)));

    /// <summary>The chat's message when the network blocked it (bug 137): not a sign-in problem, so no Sign in again.</summary>
    public static string ChatNetBlocked(string said) =>
        $"The network blocked the connection ({BlockedWords(said)}). Your sign-in is fine and the conversation is kept. " +
        "Send your message again in a minute; if it keeps happening, check the internet or VPN, or ask IT why the page is blocked.";

    /// <summary>The chat's folder (ClaudeCli.Folder); a test points it at a temporary one.</summary>
    internal static string Folder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SketchProMax", "chat");

    private static string MarkerFile => Path.Combine(Folder, "signin-failed.txt");

    /// <summary>Claude Code's saved sign-in on Windows (and Linux): .credentials.json in its config folder.</summary>
    public static string CredentialsFile
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (string.IsNullOrWhiteSpace(dir)) dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
            return Path.Combine(dir!, ".credentials.json");
        }
    }

    /// <summary>The saved sign-in's stamp (time and size): it changes when the user signs in again.</summary>
    public static string Stamp()
    {
        try
        {
            var info = new FileInfo(CredentialsFile);
            return info.Exists ? info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + info.Length : "none";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return "none"; }
    }

    private static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try { if (File.Exists(MarkerFile)) _failedStamp = File.ReadAllText(MarkerFile).Trim(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void Write()
    {
        try
        {
            if (_failedStamp is null) { if (File.Exists(MarkerFile)) File.Delete(MarkerFile); }
            else
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(MarkerFile, _failedStamp);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>A message failed because the sign-in doesn't work.</summary>
    public static void MarkFailed()
    {
        lock (Gate)
        {
            Load();
            var stamp = Stamp();
            if (_failedStamp == stamp) return;
            _failedStamp = stamp;
            Write();
        }
        Changed?.Invoke();
    }

    /// <summary>An answer worked, the user signed in again, or asked to try anyway: the failure is forgotten.</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            Load();
            if (_failedStamp is null) return;
            _failedStamp = null;
            Write();
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// The sign-in is known not to work: it failed and hasn't been renewed since. A new saved sign-in (the file changed)
    /// clears the failure by itself.
    /// </summary>
    public static bool Broken
    {
        get
        {
            lock (Gate)
            {
                Load();
                if (_failedStamp is null) return false;
                if (_failedStamp == Stamp()) return true;
                _failedStamp = null;
                Write();
                return false;
            }
        }
    }

    /// <summary>Forgets what was loaded (a test switches folders).</summary>
    internal static void Reset()
    {
        lock (Gate) { _loaded = false; _failedStamp = null; }
    }
}
