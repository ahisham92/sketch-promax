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

    /// <summary>Text that says the saved sign-in no longer works (expired, refused, signed out).</summary>
    public static bool LooksExpired(string text) => Regex.IsMatch(text ?? "",
        @"failed to authenticate|oauth session expired|session expired and could not be refreshed|oauth token (has )?expired|" +
        @"oauth token (has been )?revoked|invalid (oauth|bearer) token",
        RegexOptions.IgnoreCase);

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
