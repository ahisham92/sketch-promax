using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace SketchProMax.Chat;

/// <summary>
/// Signs in to Claude Code from inside Sketch ProMax, with no console window: "claude auth login" runs hidden and opens
/// the browser; when the user has signed in there it saves the sign-in and ends. If the browser didn't open, the page's
/// address it printed can be opened again (that page shows a code, which is pasted back here). One at a time per Revit.
/// </summary>
internal static class SignInFlow
{
    private static readonly object Gate = new();
    private static Process? _process;
    private static readonly StringBuilder Output = new();

    public enum State { Idle, Waiting, Done, Failed }

    public static State Now { get; private set; } = State.Idle;

    /// <summary>Why the last sign-in didn't finish ("" when it did or none ran).</summary>
    public static string Problem { get; private set; } = "";

    /// <summary>The sign-in page to open by hand (it shows a code to paste back); "" until Claude Code printed it.</summary>
    public static string PageUrl { get; private set; } = "";

    /// <summary>Raised (on a background thread) when the state changes.</summary>
    public static event Action? Changed;

    private static readonly Regex UrlText = new(@"https://\S+", RegexOptions.CultureInvariant);

    /// <summary>Starts the sign-in (or keeps the one running). Throws when Claude Code can't be started.</summary>
    public static void Start(ClaudeCli.Install install)
    {
        lock (Gate)
        {
            if (_process is { HasExited: false }) return;
            Output.Clear();
            PageUrl = "";
            Problem = "";
            var info = install.Path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                ? new ProcessStartInfo("cmd.exe", "/d /s /c \"" + ClaudeCli.Quote(install.Path) + " auth login --claudeai\"")
                : new ProcessStartInfo(install.Path, "auth login --claudeai");
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardInput = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.StandardOutputEncoding = Encoding.UTF8;
            info.StandardErrorEncoding = Encoding.UTF8;
            info.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            info.EnvironmentVariables.Remove("ANTHROPIC_API_KEY");
            var process = new Process { StartInfo = info, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => Read(e.Data);
            process.ErrorDataReceived += (_, e) => Read(e.Data);
            process.Exited += (_, _) => Ended(process);
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;
            Now = State.Waiting;
        }
        Changed?.Invoke();
    }

    private static void Read(string? line)
    {
        if (string.IsNullOrEmpty(line)) return;
        lock (Gate)
        {
            Output.AppendLine(line);
            if (PageUrl.Length == 0 && UrlText.Match(line) is { Success: true } m) PageUrl = m.Value;
        }
    }

    private static void Ended(Process process)
    {
        int code;
        try { code = process.ExitCode; }
        catch (InvalidOperationException) { code = -1; }
        // The last lines may still be on their way.
        try { process.WaitForExit(1000); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        lock (Gate)
        {
            if (!ReferenceEquals(process, _process)) return; // a cancelled one
            _process = null;
            if (code == 0)
            {
                Now = State.Done;
                Problem = "";
            }
            else
            {
                Now = State.Failed;
                Problem = LastLine(Output.ToString());
            }
        }
        process.Dispose();
        if (Now == State.Done) SignInHealth.Clear();
        Changed?.Invoke();
    }

    /// <summary>The code the sign-in page showed (when it was opened by hand): passed to the waiting sign-in.</summary>
    public static bool SendCode(string code)
    {
        code = (code ?? "").Trim();
        if (code.Length == 0) return false;
        lock (Gate)
        {
            if (_process is not { HasExited: false } p) return false;
            try
            {
                p.StandardInput.WriteLine(code);
                p.StandardInput.Flush();
                return true;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException) { return false; }
        }
    }

    /// <summary>Stops a sign-in that is waiting (the user gave up, or starts over).</summary>
    public static void Cancel()
    {
        Process? p;
        lock (Gate)
        {
            p = _process;
            _process = null;
            Now = State.Idle;
        }
        if (p is not null) ClaudeCli.KillTree(p);
        Changed?.Invoke();
    }

    /// <summary>Opens the sign-in page by hand in the default browser.</summary>
    public static bool OpenPage()
    {
        var url = PageUrl;
        if (!url.StartsWith("https://", StringComparison.Ordinal)) return false;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

    internal static string LastLine(string output)
    {
        var lines = output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("Paste code", StringComparison.OrdinalIgnoreCase)
            && !l.StartsWith("Opening browser", StringComparison.OrdinalIgnoreCase) && !l.StartsWith("If the browser", StringComparison.OrdinalIgnoreCase)).ToList();
        var last = lines.LastOrDefault() ?? "";
        return last.Length > 200 ? last.Substring(0, 200) + "…" : last;
    }
}

internal static class SignInCheck
{
    /// <summary>"auth status" corrected by what the chat saw: a saved sign-in that failed counts as signed out.</summary>
    public static ClaudeCli.SignIn Real(ClaudeCli.SignIn reported) =>
        reported != ClaudeCli.SignIn.SignedOut && SignInHealth.Broken ? ClaudeCli.SignIn.SignedOut : reported;
}
