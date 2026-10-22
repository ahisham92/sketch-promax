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

    /// <summary>The permission rule that keeps the chat from reading Claude Code's transcripts of earlier conversations.</summary>
    public const string TranscriptRule = "Read(~/.claude/projects/**/*.jsonl)";

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
        var script = InstallCommand + "; Write-Host ''; Write-Host 'Finished. Close this window and go back to Revit: the Sketch ProMax chat finds it by itself.'";
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
    /// The engine's start info: Claude Code in print mode with stream-json in and out, our MCP server (the chat's own
    /// connection inside Revit, or the connector the desktop app is given when that couldn't start) and its tools allowed
    /// without prompts, Read, Glob and Grep for attached files, and nothing else that could act on the PC. Anything that
    /// would need a permission prompt is denied (dontAsk), because nobody can answer a prompt in a hidden process. The MCP
    /// server knows it serves the chat (inside Revit always; the connector by SKETCHPROMAX_CHAT), so Revit's own dialog
    /// asks before model changes (per the settings) and always before code, recipes and files written to a folder Claude chose.
    /// </summary>
    /// <param name="coordinator">A chat With coordinator (ChatTypes): it may start jobs with chat_jobs and is told how.
    /// A Simple chat never gets chat_jobs or those instructions.</param>
    /// <param name="effort">Claude Code's --effort level (ChatAuto.Efforts); left out when empty or this Claude Code doesn't take it.</param>
    public static ProcessStartInfo EngineStart(Install install, string? resumeSessionId, string? model = null, bool coordinator = true, string? effort = null)
    {
        Directory.CreateDirectory(WorkFolder);
        var engine = Path.Combine(Folder, "engine");
        Directory.CreateDirectory(engine);
        var pid = Process.GetCurrentProcess().Id.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var mcpFile = WriteMcpConfig(engine, pid, claude: install.Path, model: model);

        var promptFile = Path.Combine(engine, "prompt-" + pid + ".md");
        File.WriteAllText(promptFile, Prompt(coordinator), new UTF8Encoding(false));

        var args = new List<string>
        {
            "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--include-partial-messages",
            "--mcp-config", mcpFile, "--strict-mcp-config",
            "--append-system-prompt-file", promptFile,
            "--tools", "Read,Glob,Grep",
            "--allowedTools", $"mcp__{ServerName},mcp__{ServerName}__*,Read,Glob,Grep",
            "--permission-mode", "dontAsk",
        };
        // A connector older than this add-in (only used when the connection inside Revit couldn't start) may not pass
        // SKETCHPROMAX_CHAT on, so Revit would not know to ask: then the code and recipe tools are left out of the chat altogether.
        // Claude Code's own transcripts of earlier conversations (…\.claude\projects\…\<session>.jsonl): after a shrink
        // it is told it may read the full transcript, which would send the long conversation again and fill the chat with
        // file steps. The summary is what a shrink keeps; anything else is asked for or found with recall_chats.
        var denied = new List<string> { TranscriptRule };
        if (OlderConnectorInUse) denied.AddRange(SketchProMax.Tools.OutputPaths.CodeTools.Select(t => $"mcp__{ServerName}__{t}"));
        // Parallel jobs need the connection to pass the job on (so its changes take turns): the one inside Revit always
        // does, the connector program when it is as new as this add-in. And a chat With coordinator: a Simple chat does
        // everything itself.
        if (!JobsAvailable || !coordinator) denied.Add($"mcp__{ServerName}__{SketchProMax.Tools.ChatJobsTool.ToolName}");
        args.Add("--disallowedTools");
        args.Add(string.Join(",", denied));
        if (!string.IsNullOrEmpty(resumeSessionId)) { args.Add("--resume"); args.Add(resumeSessionId!); }
        if (!string.IsNullOrEmpty(model)) { args.Add("--model"); args.Add(model!); }
        AddEffort(args, effort);

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

    /// <summary>
    /// A parallel job's own Claude Code (Chat.JobBoard, ChatPane.Jobs): like the chat's engine, with the job's instructions,
    /// the connector told which job it serves (SKETCHPROMAX_JOB, so its changes take turns with the other jobs'), no
    /// chat_jobs (a job doesn't start jobs), whole messages rather than streamed words, and its own session: a new id
    /// (<paramref name="newSession"/>) or the one it resumes, never the main chat's.
    /// </summary>
    public static ProcessStartInfo JobStart(Install install, string jobKey, string? resume, string? newSession, string? model, string? effort = null)
    {
        Directory.CreateDirectory(WorkFolder);
        var engine = Path.Combine(Folder, "engine");
        Directory.CreateDirectory(engine);
        var pid = Process.GetCurrentProcess().Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var mcpFile = WriteMcpConfig(engine, pid, "mcp-job-" + jobKey + "-", install.Path, model, jobKey);
        var promptFile = Path.Combine(engine, "job-" + jobKey + "-" + pid + ".md");
        File.WriteAllText(promptFile, JobPrompt(), new UTF8Encoding(false));
        var args = new List<string>
        {
            "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
            "--mcp-config", mcpFile, "--strict-mcp-config",
            "--append-system-prompt-file", promptFile,
            "--tools", "Read,Glob,Grep",
            "--allowedTools", $"mcp__{ServerName},mcp__{ServerName}__*,Read,Glob,Grep",
            "--permission-mode", "dontAsk",
            "--disallowedTools", string.Join(",", TranscriptRule, $"mcp__{ServerName}__{SketchProMax.Tools.ChatJobsTool.ToolName}"),
        };
        if (!string.IsNullOrEmpty(resume)) { args.Add("--resume"); args.Add(resume!); }
        else { args.Add("--session-id"); args.Add(newSession ?? Guid.NewGuid().ToString()); }
        if (!string.IsNullOrEmpty(model)) { args.Add("--model"); args.Add(model!); }
        AddEffort(args, effort);
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
        // Its own session, always: nothing of a parent session carries over.
        foreach (var name in info.EnvironmentVariables.Keys.Cast<string>().Where(k => k == "CLAUDECODE" || k.StartsWith("CLAUDE_CODE_", StringComparison.Ordinal) && k.Contains("SESSION")).ToList())
            info.EnvironmentVariables.Remove(name);
        info.EnvironmentVariables["SKETCHPROMAX_REVIT_PID"] = pid;
        // A change may wait its turn behind other jobs' changes (BridgeServer.TurnWait): the call must not time out first.
        info.EnvironmentVariables["MCP_TOOL_TIMEOUT"] = "2400000";
        return info;
    }

    /// <summary>
    /// True when the chat may run parallel jobs: always with the connection inside Revit; with the connector program
    /// only when it is as new as this add-in (an older one doesn't pass the job on, so the job's changes couldn't take turns).
    /// </summary>
    public static bool JobsAvailable => !OlderConnectorInUse;

    /// <summary>
    /// True when the chat falls back to the connector program (the connection inside Revit couldn't start) and that is
    /// older than this add-in, so it may lack newer connector tools (read_pdf_part) and the job key.
    /// </summary>
    internal static bool OlderConnectorInUse => Bridge.ChatMcp.Ensure() is null && ConnectorIsOlder();

    /// <summary>
    /// Set once Claude Code refused --effort (a version older than the option, ChatAuto.IsEffortRefusal): from then on
    /// this Revit session starts it without, at its own default effort.
    /// </summary>
    public static volatile bool EffortRefused;

    private static void AddEffort(List<string> args, string? effort)
    {
        if (EffortRefused || string.IsNullOrEmpty(effort) || !ChatAuto.Efforts.Contains(effort)) return;
        args.Add("--effort");
        args.Add(effort!);
    }

    /// <summary>
    /// Writes the MCP config. Normally the chat's own connection inside Revit (Bridge.ChatMcp): its address, its token, and
    /// headers naming the parallel job and the model. When that couldn't start, the connector program the installer and
    /// ClaudeRegistration register for the desktop app, as before. Written at each start, so a new core's address is used.
    /// </summary>
    private static string WriteMcpConfig(string engine, string pid, string name = "mcp-", string? claude = null, string? model = null, string? job = null)
    {
        if (!string.IsNullOrEmpty(claude)) Bridge.ChatMcp.Claude = claude;
        var mcp = new JsonObject { ["mcpServers"] = new JsonObject { [ServerName] = McpServer(pid, claude, model, job) } };
        var mcpFile = Path.Combine(engine, name + pid + ".json");
        File.WriteAllText(mcpFile, mcp.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        return mcpFile;
    }

    /// <summary>The config's entry for our server: the connection inside Revit, or the connector program.</summary>
    internal static JsonObject McpServer(string pid, string? claude, string? model, string? job)
    {
        if (Bridge.ChatMcp.Ensure() is { } http)
        {
            var headers = new JsonObject { ["Authorization"] = "Bearer " + http.Token };
            // The job this session is (its changes take turns with the other jobs'), and the model read_pdf_part reads with.
            if (!string.IsNullOrEmpty(job)) headers[Bridge.McpHttpServer.JobHeader] = job;
            if (!string.IsNullOrEmpty(model)) headers[Bridge.McpHttpServer.ModelHeader] = model;
            return new JsonObject { ["type"] = "http", ["url"] = http.Url, ["headers"] = headers };
        }
        return new JsonObject
        {
            ["type"] = "stdio",
            ["command"] = ClaudeRegistration.ConnectorPath,
            ["args"] = new JsonArray(),
            // SKETCHPROMAX_CHAT: the bridge then asks in Revit before code, recipes and files written where Claude chose.
            // SKETCHPROMAX_CLAUDE and _MODEL: read_pdf_part reads a part of a large PDF with the same Claude Code and model.
            // SKETCHPROMAX_JOB: the parallel job this connector serves (its changes take turns with the other jobs').
            ["env"] = Env(pid, claude, model, job),
        };
    }

    /// <summary>The address the chat's Claude Code reaches the Revit tools at now: the connection inside Revit, or "" for the connector program.</summary>
    internal static string McpAddress => Bridge.ChatMcp.Current?.Url ?? "";

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

    private static JsonObject Env(string pid, string? claude, string? model, string? job = null)
    {
        var env = new JsonObject { ["SKETCHPROMAX_REVIT_PID"] = pid, ["SKETCHPROMAX_CHAT"] = "1" };
        if (!string.IsNullOrEmpty(claude)) env["SKETCHPROMAX_CLAUDE"] = claude;
        if (!string.IsNullOrEmpty(model)) env["SKETCHPROMAX_MODEL"] = model;
        if (!string.IsNullOrEmpty(job)) env["SKETCHPROMAX_JOB"] = job;
        return env;
    }

    /// <summary>
    /// What the chat says about Revit tools that didn't connect (<paramref name="status"/> from Claude Code), naming a PC
    /// that blocks them: Claude Code's own log of the connection says EPERM or access denied, or, for the connector
    /// program, Revit itself isn't allowed to start it. Reads files and may start the connector: call off the UI thread.
    /// </summary>
    internal static string ToolsFailed(string status, string address, bool job = false)
    {
        var inside = address.Length > 0;
        var blocked = ToolsNote.IsBlocked(LastMcpError()) || !inside && ConnectorRefused();
        return ToolsNote.Failed(status, inside, inside ? address : ClaudeRegistration.ConnectorPath, blocked, job);
    }

    /// <summary>The end of Claude Code's newest log of connecting to our server (its cache folder, the last few minutes), or "".</summary>
    private static string LastMcpError()
    {
        try
        {
            var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "claude-cli-nodejs", "Cache");
            if (!Directory.Exists(cache)) return "";
            var newest = Directory.GetDirectories(cache)
                .Select(d => Path.Combine(d, "mcp-logs-" + ServerName))
                .Where(Directory.Exists)
                .SelectMany(d => new DirectoryInfo(d).GetFiles())
                .Where(f => f.LastWriteTimeUtc > DateTime.UtcNow.AddMinutes(-10))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (newest is null) return "";
            using var stream = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(Math.Max(0, stream.Length - 4000), SeekOrigin.Begin);
            return new StreamReader(stream, Encoding.UTF8).ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException) { return ""; }
    }

    /// <summary>True when Windows refuses to start the connector program for Revit too (a policy such as AppLocker), not when it is missing.</summary>
    private static bool ConnectorRefused()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(ClaudeRegistration.ConnectorPath)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            });
            if (process is null) return false;
            // It started: with its input closed it ends by itself.
            process.StandardInput.Close();
            if (!process.WaitForExit(3000)) KillTree(process);
            return false;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // 5 access denied, 1260 blocked by group policy, 4551 blocked by App Control; 2 (not found) is a broken install.
            return ex.NativeErrorCode is 5 or 1260 or 4551 || ToolsNote.IsBlocked(ex.Message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException) { return false; }
    }

    /// <summary>True when the installed connector is older than this add-in (so it may lack newer connector tools, such as read_pdf_part).</summary>
    internal static bool ConnectorIsOlder()
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
    private static string Prompt(bool coordinator) => SketchProMax.Agent.SystemPrompt.Build(AgentSettings.Load().FirmStandards) + """


        You are chatting in the Sketch ProMax pane inside this Revit window. The person is a structural engineer.
        - The Revit tools come from the sketch-promax MCP server and act on this Revit window. Use them for anything about
          the model. Besides them you have only Read, Glob and Grep (to look at files on this PC); there is no shell, file
          editing or web access here.
        - Revit asks the user to approve some calls before they run: always run_revit_query_code, run_revit_code, new
          recipes and learned tools, deletes that take other elements with them, and writing over existing files outside
          Sketch ProMax's own folders; other model changes only if the user set it to ask. Prefer the dedicated tools,
          and leave folder and file_name out unless the user asked for a location. If the user declines, ask what they
          want instead. Don't ask the user for permission in the chat before a normal change: Revit asks when needed.
        - Files the user attaches are named by full path. Pictures and PDFs that fit come with the message itself, and the
          text of Word and PowerPoint files is in the message: read them there. A PDF too large to send whole comes split
          into parts, with a text file of its pages: find the pages in the text, then ask the parts that cover them with
          read_pdf_part, which reads a part in full. Without parts, read the text file and look at a page's drawings
          with redline_read (file and page), which shows any PDF page as a picture, not only redlines. Read other files
          with the Read tool. For DWG or DXF files, pass the path to the Revit tools (for example import_cad); don't try to
          Read them.
        - Files: when the user wants something to keep or send (an Excel sheet, a Word report or letter, a PowerPoint
          presentation, a PDF; a bill of quantities, a cost plan, a schedule, a summary of the model), make it with
          make_document. Get the facts first with the Revit tools, then write the whole file in one call, with formulas
          for amounts and totals in Excel. For pictures of the model, take them with capture_view_image and give their
          paths. The chat shows the file as a card with Open, Show in folder and Save a copy: say in a line or two what is
          in it, without the path. The model's own priced quantities and cost plan come from export_quantities and
          cost_estimate, which write their workbooks themselves.
        - Keep it easy: lead with the answer in a sentence or two of plain words, then only the detail that helps. No tool
          names, internal ids, element ids, JSON or API terms unless the user asks for them. Use the model's own units.
        - "What is in this model?" and other overview questions: a few lines only, such as the model's name, the levels,
          the main elements with counts (walls, floors, columns, beams, foundations...), and how many views and sheets.
          Then offer more detail in one short line. Don't list everything.
        - When a call says Revit didn't start it, the user already sees a card in the chat that says what to do. Say it in
          one short line (for example "Revit is busy: press Esc in Revit, then Continue"), without the technical reason.
        - Answer in Markdown: short paragraphs, lists and tables. Keep tables narrow (the pane is narrow), and put numbers
          with their units.
        - When the user says "remember that ..." (or "from now on ..."), save it with memory_note action=remember: scope=global
          for habits and standards that hold for every model, scope=model for facts about this model. When they say
          "forget ...", use action=forget. Confirm in one line. They can see and edit the notes in the pane's Notes tab.
        - When the user says something is a bug ("that's a bug", "report this"), call report_bug with one plain line you
          write from the conversation: what they did, what happened, what they expected, and the tool involved. Then say
          the report is saved in Documents\Sketch ProMax\Bugs\Reports.
        - Earlier chats: below is what recent chats about this model remembered. When the user refers to something done
          in another chat or project ("the same way as in the Tower project", "like last time"), call recall_chats with the words
          that name it, then action=open on the best match and follow its steps and settings here (checking this model
          first). Say in one line which chat you followed. If nothing matches, ask the user which project they mean.
        - When the chat was shrunk, the summary you have is what it keeps. Don't try to open the earlier conversation's
          file (the .jsonl transcript): it isn't allowed here, and it would cost far more than it saves. If a detail is
          missing, read it from the model or ask the user in one line.
        """ + (coordinator ? JobsPrompt() : SimplePrompt) + Notes() + Recall();

    /// <summary>A Simple chat (ChatTypes): it may have used jobs before it was switched, so it is told it has none now.</summary>
    private const string SimplePrompt = """

        - This is a simple chat: there are no jobs here. Do all the work in this chat yourself, step by step, even if
          earlier messages mention jobs.
        """;

    /// <summary>How the main chat coordinates parallel jobs (chat_jobs); nothing when jobs aren't available.</summary>
    private static string JobsPrompt() => !JobsAvailable ? "" : $"""

        - Jobs: you coordinate. When a request has separate parts that can go side by side ("make sheets for levels 1-5
          and check the walls"), or is one long piece of model work, hand it to jobs with chat_jobs. Call action=list first;
          a follow-up that belongs to an existing job ("also add level 6" after a sheets job) goes to that job with
          action=message, never to a new job. Otherwise action=start, once per part: a short title, and the whole task in
          full with every detail (the job doesn't see this chat), and its kind: simple (rename, tag, list, fill parameters,
          short summaries), everyday (sheets, views, dimensions, checks, normal modelling) or hard (a spec or BEP against
          the model, redlines, tricky modelling logic, costing decisions); the kind picks its model and effort. Up to {ChatJobs.Limit} jobs run at once; more wait to start.
          Reads run in parallel; changes to the model take turns, one at a time. Answer quick questions and small single
          changes yourself, without a job.
        - After starting or messaging jobs, say in one line which jobs have it and end your turn: don't wait for them and
          don't do their work. While jobs work, leave model changes to them (Revit may say it is busy). Each job reports
          back when it finishes, in a message marked as a job report: then tell the user briefly. Stop a job with
          action=stop when the user asks.
        """;

    /// <summary>A parallel job's instructions: Sketch ProMax's agent instructions plus what is special about a job.</summary>
    private static string JobPrompt() => SketchProMax.Agent.SystemPrompt.Build(AgentSettings.Load().FirmStandards) + """


        You are one job of the Sketch ProMax chat in this Revit window: the main chat gave you one task, and you work on it
        in the background, perhaps alongside other jobs on the same model. The person is a structural engineer; they see
        your progress in your own view of the chat (they open your card) and can send you messages there.
        - Do your task completely with the Revit tools from the sketch-promax MCP server. Besides them you have only Read,
          Glob and Grep (to look at files on this PC). Don't start other work.
        - Other jobs may work at the same time. Reads run at once; changes to the model take turns, one at a time, so a
          change may wait a while before it runs: that is normal, don't send it again. Don't change or undo elements
          outside your task.
        - Revit asks the user to approve some calls; if they decline, leave that part and say so.
        - When the task asks for a file (Excel, Word, PowerPoint or PDF), make it with make_document after getting the
          facts, and name the file in your report.
        - Messages that reach you later say who sent them: "The user says: …" (the person, typing in your view) or "The
          main chat says: …" (the coordinator). Both count; the person's word wins if they disagree.
        - If you need the person to decide something the model can't settle, end with one short question: they answer in
          your view.
        - End every run with a short report for the main chat, in plain words: what you did (with counts and names),
          anything you couldn't do and why, and what the person should check. A few lines; no tool names, element ids or
          JSON.
        """ + Notes();

    /// <summary>The memory notes for this chat (global and the open model's), as a block at the end of the prompt.</summary>
    private static string Notes()
    {
        var block = MemoryNotes.PromptFor(ChatPane.CurrentModelKey);
        return block.Length == 0 ? "" : "\n\n" + block;
    }

    /// <summary>What this model's other chats remembered (ChatRecall), as a block at the end of the prompt.</summary>
    private static string Recall()
    {
        try
        {
            var key = ChatPane.CurrentChatKey;
            var block = ChatRecall.PromptBlock(ChatStore.ForModel(key), key, ChatPane.CurrentChatId);
            return block.Length == 0 ? "" : "\n\n" + block;
        }
        catch (Exception) { return ""; }
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
