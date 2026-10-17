using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SketchProMax.Settings;
using SketchProMax.Update;

namespace SketchProMax.Chat;

/// <summary>
/// Finds Claude Code on this PC, checks the sign-in, opens the official installer and the sign-in in a console window
/// the user can see, and builds the command line of the chat's engine. Everything here may block for a few seconds
/// (it starts processes): call it from a background thread, never on Revit's UI thread.
/// </summary>
internal static class ClaudeCli
{
    /// <summary>The official native installer for Windows (code.claude.com/docs/en/setup).</summary>
    public const string InstallCommand = "irm https://claude.ai/install.ps1 | iex";

    /// <summary>The name the chat registers our MCP server under: its tools are mcp__sketch-promax__&lt;tool&gt;.</summary>
    public const string ServerName = "sketch-promax";

    private static readonly Regex VersionText = new(@"\d+\.\d+\.\d+", RegexOptions.CultureInvariant);

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SketchProMax", "chat");

    /// <summary>The chat engine's working folder: per user, holds nothing but what Claude writes there.</summary>
    public static string WorkFolder => Path.Combine(Folder, "work");

    public static string AttachmentsFolder => Path.Combine(Folder, "attachments");

    /// <summary>Where Claude Code was found, and its version text.</summary>
    internal sealed record Install(string Path, string Version);

    internal enum SignIn { SignedIn, SignedOut, Unknown }

    /// <summary>Every place Claude Code is installed to on Windows, most likely first.</summary>
    public static IEnumerable<string> Candidates()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Path.Combine(home, ".local", "bin", "claude.exe");                // native installer
        yield return Path.Combine(local, "Programs", "claude", "claude.exe");
        yield return Path.Combine(local, "Microsoft", "WinGet", "Links", "claude.exe"); // WinGet
        // PATH as the user has it now (Revit's own copy is from when it started, before an install).
        var paths = new List<string>();
        foreach (var target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            string? value = null;
            try { value = Environment.GetEnvironmentVariable("PATH", target); }
            catch (Exception ex) when (ex is System.Security.SecurityException or ArgumentException) { }
            if (value is not null) paths.AddRange(value.Split(';'));
        }
        foreach (var dir in paths.Select(p => Environment.ExpandEnvironmentVariables(p.Trim().Trim('"'))).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string exe, cmd;
            try
            {
                exe = Path.Combine(dir, "claude.exe");
                cmd = Path.Combine(dir, "claude.cmd");
            }
            catch (ArgumentException) { continue; }
            yield return exe;
            yield return cmd;
        }
        yield return Path.Combine(roaming, "npm", "claude.cmd");                        // npm install -g
    }

    /// <summary>Finds a working Claude Code (it answers --version). Null when there is none.</summary>
    public static Install? Find()
    {
        foreach (var path in Candidates().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // The Claude desktop app is also "claude.exe"; never start it by mistake.
            if (path.IndexOf("AnthropicClaude", StringComparison.OrdinalIgnoreCase) >= 0 ||
                path.IndexOf("WindowsApps", StringComparison.OrdinalIgnoreCase) >= 0) continue;
            if (!File.Exists(path)) continue;
            var (code, output) = Run(path, "--version", 20000);
            var match = VersionText.Match(output);
            if (code == 0 && match.Success) return new Install(path, match.Value);
        }
        return null;
    }

    /// <summary>
    /// Whether Claude Code is signed in: `claude auth status` exits 0 when signed in and 1 when not. An older Claude Code
    /// without that command gives Unknown; the first message then says whether it works. Detail: the sign-in method.
    /// </summary>
    public static (SignIn State, string Method) CheckSignIn(Install install)
    {
        var (code, output) = Run(install.Path, "auth status", 20000);
        var method = "";
        var json = output.IndexOf('{') is var at and >= 0 ? output.Substring(at) : "";
        try
        {
            if (json.Length > 0 && JsonNode.Parse(json) is JsonObject o)
            {
                method = o["authMethod"]?.ToString() ?? "";
                if (o["loggedIn"] is JsonValue v && v.TryGetValue<bool>(out var loggedIn))
                    return (loggedIn ? SignIn.SignedIn : SignIn.SignedOut, method);
                if (code == 0) return (SignIn.SignedIn, method);
                if (code == 1) return (SignIn.SignedOut, method);
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { }
        if (code == 1 && output.IndexOf("not logged in", StringComparison.OrdinalIgnoreCase) >= 0) return (SignIn.SignedOut, "");
        return (SignIn.Unknown, "");
    }

    /// <summary>Opens a PowerShell window running Anthropic's official installer. The user watches it run.</summary>
    public static void StartInstall()
    {
        var script = InstallCommand + "; Write-Host ''; Write-Host 'Finished. Close this window and go back to Revit: the Sketch ProMax chat finds Claude Code by itself.'";
        Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -NoExit -Command \"" + script + "\"")
        {
            UseShellExecute = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        })?.Dispose();
    }

    /// <summary>Opens a console window running `claude auth login`, which opens the browser to sign in.</summary>
    public static void StartSignIn(Install install)
    {
        var line = Quote(install.Path) + " auth login & echo. & echo When it says you are signed in, close this window and go back to Revit.";
        Process.Start(new ProcessStartInfo("cmd.exe", "/d /s /k \"" + line + "\"")
        {
            UseShellExecute = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        })?.Dispose();
    }

    /// <summary>
    /// The engine's start info: Claude Code in print mode with stream-json in and out, our MCP server (the same command
    /// the desktop app is given) and its tools allowed without prompts, Read, Glob and Grep for attached files, and
    /// nothing else that could act on the PC. Anything that would need a permission prompt is denied (dontAsk), because
    /// nobody can answer a prompt in a hidden process. The MCP server is told it serves the chat (SKETCHPROMAX_CHAT), so
    /// Revit's own dialog asks before model changes (per the settings) and always before code, recipes and files written
    /// to a folder Claude chose.
    /// </summary>
    public static ProcessStartInfo EngineStart(Install install, string? resumeSessionId, string? model = null)
    {
        Directory.CreateDirectory(WorkFolder);
        var engine = Path.Combine(Folder, "engine");
        Directory.CreateDirectory(engine);
        var pid = Process.GetCurrentProcess().Id.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var mcpFile = WriteMcpConfig(engine, pid);

        var promptFile = Path.Combine(engine, "prompt-" + pid + ".md");
        File.WriteAllText(promptFile, Prompt(), new UTF8Encoding(false));

        var args = new List<string>
        {
            "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--include-partial-messages",
            "--mcp-config", mcpFile, "--strict-mcp-config",
            "--append-system-prompt-file", promptFile,
            "--tools", "Read,Glob,Grep",
            "--allowedTools", $"mcp__{ServerName},mcp__{ServerName}__*,Read,Glob,Grep",
            "--permission-mode", "dontAsk",
        };
        // A connector older than this add-in may not pass SKETCHPROMAX_CHAT on, so Revit would not know to ask: then the
        // code and recipe tools are left out of the chat altogether.
        if (ConnectorIsOlder())
        {
            args.Add("--disallowedTools");
            args.Add(string.Join(",", SketchProMax.Tools.OutputPaths.CodeTools.Select(t => $"mcp__{ServerName}__{t}")));
        }
        if (!string.IsNullOrEmpty(resumeSessionId)) { args.Add("--resume"); args.Add(resumeSessionId!); }
        if (!string.IsNullOrEmpty(model)) { args.Add("--model"); args.Add(model!); }

        var command = string.Join(" ", args.Select(Quote));
        var cmdShim = install.Path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
        var info = cmdShim
            ? new ProcessStartInfo("cmd.exe", "/d /s /c \"" + Quote(install.Path) + " " + command + "\"")
            : new ProcessStartInfo(install.Path, command);
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.StandardOutputEncoding = Encoding.UTF8;
        info.StandardErrorEncoding = Encoding.UTF8;
        info.WorkingDirectory = WorkFolder;
        // The chat runs on the user's Claude plan: an API key in the environment would bill the API instead.
        info.EnvironmentVariables.Remove("ANTHROPIC_API_KEY");
        info.EnvironmentVariables["SKETCHPROMAX_REVIT_PID"] = pid;
        return info;
    }

    /// <summary>Writes the MCP config: the connector the installer and ClaudeRegistration register for the desktop app.</summary>
    private static string WriteMcpConfig(string engine, string pid, string name = "mcp-")
    {
        var mcp = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                [ServerName] = new JsonObject
                {
                    ["type"] = "stdio",
                    ["command"] = ClaudeRegistration.ConnectorPath,
                    ["args"] = new JsonArray(),
                    // SKETCHPROMAX_CHAT: the bridge then asks in Revit before code, recipes and files written where Claude chose.
                    ["env"] = new JsonObject { ["SKETCHPROMAX_REVIT_PID"] = pid, ["SKETCHPROMAX_CHAT"] = "1" },
                },
            },
        };
        var mcpFile = Path.Combine(engine, name + pid + ".json");
        File.WriteAllText(mcpFile, mcp.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        return mcpFile;
    }

    /// <summary>
    /// A one-shot run for the prompt enhancer (PromptBriefs): print mode, the message read from stdin, one JSON result
    /// out. No built-in tools at all; with <paramref name="readTools"/> only the planner's read-only Revit tools
    /// (PromptBriefs.PlannerTools) are allowed, and everything else is denied without asking (dontAsk), so it cannot
    /// change the model. Without them it gets no MCP server.
    /// </summary>
    public static ProcessStartInfo OneShotStart(Install install, string instructions, bool readTools, string? model)
    {
        Directory.CreateDirectory(WorkFolder);
        var engine = Path.Combine(Folder, "engine");
        Directory.CreateDirectory(engine);
        var pid = Process.GetCurrentProcess().Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var promptFile = Path.Combine(engine, (readTools ? "brief-" : "distil-") + pid + ".md");
        File.WriteAllText(promptFile, instructions + Notes(), new UTF8Encoding(false));
        var args = new List<string> { "-p", "--output-format", "json", "--append-system-prompt-file", promptFile, "--tools", "", "--permission-mode", "dontAsk" };
        if (readTools)
        {
            args.AddRange(new[] { "--mcp-config", WriteMcpConfig(engine, pid, "mcp-brief-"), "--strict-mcp-config", "--max-turns", "12" });
            args.Add("--allowedTools");
            args.Add(string.Join(",", PromptBriefs.PlannerTools.Select(t => $"mcp__{ServerName}__{t}")));
        }
        else
        {
            args.AddRange(new[] { "--strict-mcp-config", "--max-turns", "1" });
        }
        if (!string.IsNullOrEmpty(model)) { args.Add("--model"); args.Add(model!); }
        var command = string.Join(" ", args.Select(Quote));
        var info = install.Path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            ? new ProcessStartInfo("cmd.exe", "/d /s /c \"" + Quote(install.Path) + " " + command + "\"")
            : new ProcessStartInfo(install.Path, command);
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.StandardOutputEncoding = Encoding.UTF8;
        info.StandardErrorEncoding = Encoding.UTF8;
        info.WorkingDirectory = WorkFolder;
        info.EnvironmentVariables.Remove("ANTHROPIC_API_KEY");
        info.EnvironmentVariables["SKETCHPROMAX_REVIT_PID"] = pid;
        return info;
    }

    /// <summary>True when the installed connector's file version is known and older than this add-in.</summary>
    private static bool ConnectorIsOlder()
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(ClaudeRegistration.ConnectorPath);
            if (info.FileMajorPart == 0 && info.FileMinorPart == 0 && info.FileBuildPart == 0) return false;
            return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart) < Updater.Current;
        }
        catch (Exception ex) when (ex is FileNotFoundException or ArgumentException) { return false; }
    }

    /// <summary>Sketch ProMax's agent instructions (also sent by the MCP server) plus what is special about the pane.</summary>
    private static string Prompt() => SketchProMax.Agent.SystemPrompt.Build(AgentSettings.Load().FirmStandards) + """


        You are chatting in the Sketch ProMax pane inside this Revit window. The person is a structural engineer.
        - The Revit tools come from the sketch-promax MCP server and act on this Revit window. Use them for anything about
          the model. Besides them you have only Read, Glob and Grep (to look at files on this PC); there is no shell, file
          editing or web access here.
        - Revit asks the user to approve some calls before they run: model changes (unless they turned that off), and
          always run_revit_query_code, run_revit_code, recipes, and tools that write files to a folder or file name you
          give. Prefer the dedicated tools, and leave folder and file_name out unless the user asked for a location. If
          the user declines, ask what they want instead.
        - Files the user attaches are named by full path. Read them with the Read tool (PDFs and images too). For DWG or
          DXF files, pass the path to the Revit tools (for example import_cad); don't try to Read them.
        - Answer in Markdown: short paragraphs, lists and tables. Keep tables narrow (the pane is narrow), and put numbers
          with their units.
        - When the user says "remember that ..." (or "from now on ..."), save it with memory_note action=remember: scope=global
          for habits and standards that hold for every model, scope=model for facts about this model. When they say
          "forget ...", use action=forget. Confirm in one line. They can see and edit the notes in the pane's Notes tab.
        - When the user says something is a bug ("that's a bug", "report this"), call report_bug with one plain line you
          write from the conversation: what they did, what happened, what they expected, and the tool involved. Then say
          the report is saved in Documents\Sketch ProMax\Bugs\Reports.
        """ + Notes();

    /// <summary>The memory notes for this chat (global and the open model's), as a block at the end of the prompt.</summary>
    private static string Notes()
    {
        var block = MemoryNotes.PromptFor(ChatPane.CurrentModelKey);
        return block.Length == 0 ? "" : "\n\n" + block;
    }

    /// <summary>Runs a short command hidden and returns its exit code and output (stdout then stderr); -1 on timeout or failure.</summary>
    public static (int Code, string Output) Run(string exe, string arguments, int timeoutMs)
    {
        try
        {
            var cmdShim = exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
            var info = cmdShim
                ? new ProcessStartInfo("cmd.exe", "/d /s /c \"" + Quote(exe) + " " + arguments + "\"")
                : new ProcessStartInfo(exe, arguments);
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.RedirectStandardInput = true;
            info.StandardOutputEncoding = Encoding.UTF8;
            info.StandardErrorEncoding = Encoding.UTF8;
            info.WorkingDirectory = Directory.Exists(WorkFolder) ? WorkFolder : Path.GetTempPath();
            info.EnvironmentVariables.Remove("ANTHROPIC_API_KEY");
            using var process = Process.Start(info);
            if (process is null) return (-1, "");
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMs))
            {
                KillTree(process);
                return (-1, "");
            }
            Task.WaitAll(new Task[] { stdout, stderr }, 2000);
            return (process.ExitCode, (stdout.IsCompleted ? stdout.Result : "") + "\n" + (stderr.IsCompleted ? stderr.Result : ""));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or AggregateException)
        {
            return (-1, ex.Message);
        }
    }

    /// <summary>Ends a process and everything it started (Claude Code starts the MCP server).</summary>
    public static void KillTree(Process process)
    {
        try
        {
            if (process.HasExited) return;
#if NETFRAMEWORK
            using var kill = Process.Start(new ProcessStartInfo("taskkill.exe", "/PID " + process.Id + " /T /F")
            {
                UseShellExecute = false, CreateNoWindow = true,
            });
            kill?.WaitForExit(5000);
            if (!process.HasExited) process.Kill();
#else
            process.Kill(entireProcessTree: true);
#endif
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
    }

    /// <summary>Quotes one argument for the Windows command line (CommandLineToArgvW rules).</summary>
    public static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"', '&', '|', '<', '>', '^', '(', ')' }) < 0) return arg;
        var sb = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') { sb.Append('\\', slashes * 2 + 1).Append('"'); slashes = 0; continue; }
            sb.Append('\\', slashes).Append(c);
            slashes = 0;
        }
        sb.Append('\\', slashes * 2).Append('"');
        return sb.ToString();
    }
}
