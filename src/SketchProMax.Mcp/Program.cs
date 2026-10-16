using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using SketchProMax.Agent;

// Sketch ProMax MCP server.
// Claude desktop / Claude Code launch this over stdio. Tool calls are forwarded over a named pipe
// to the Sketch ProMax add-in inside the running Revit session.

var builder = Host.CreateApplicationBuilder(args);
// stdout carries the MCP protocol; all logging must go to stderr.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services
    .AddMcpServer(o =>
    {
        o.ServerInfo = new() { Name = "sketch-promax", Version = RevitBridge.Version };
        o.ServerInstructions = SystemPrompt.Build(RevitBridge.FirmStandards());
        // Learned tools can be added mid-chat; the client is told to refresh its list when that happens.
        o.Capabilities = new() { Tools = new() { ListChanged = true } };
    })
    .WithStdioServerTransport()
    .WithListToolsHandler((ctx, ct) => ValueTask.FromResult(new ListToolsResult { Tools = RevitBridge.LoadTools() }))
    .WithCallToolHandler(async (ctx, ct) =>
    {
        var p = ctx.Params ?? throw new InvalidOperationException("Missing tool call parameters.");
        var result = await RevitBridge.CallAsync(p.Name, p.Arguments, ct);
        if (p.Name is "save_as_tool" or "delete_learned_tool" && result.IsError != true)
            await ctx.Server.SendNotificationAsync(NotificationMethods.ToolListChangedNotification, ct);
        return result;
    });

await builder.Build().RunAsync();

internal static class RevitBridge
{
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax");

    private static string SessionsFolder => Path.Combine(Folder, "sessions");

    /// <summary>This connector's version (major.minor.build), from the &lt;Version&gt; in the project file.</summary>
    public static string Version { get; } = typeof(RevitBridge).Assembly.GetName().Version is { } v
        ? $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}"
        : "0.0.0";

    // The Revit session and model Claude is working on. Null until chosen, or picked automatically when only one is open.
    // Kept as one immutable record so concurrent tool calls never see a pid from one choice and a model from another.
    private sealed record Target(int Pid, string? Model);
    private static volatile Target? _target;

    // Revit windows that are running but did not answer the last ping (busy with a long operation, or frozen).
    private static volatile int _unresponsive;

    private sealed record Session(int Pid, string Pipe, string RevitVersion, bool Busy, string? Blocked, List<(string Title, string Path, bool Active)> Documents);

    private static readonly Tool[] ConnectionTools =
    [
        new Tool
        {
            Name = "list_revit_models",
            Description = "Lists every Revit window connected to Sketch ProMax (Revit version and open models), and which one Claude is working on. Call this when the user mentions a model by name or has several Revit windows open.",
            InputSchema = JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement,
        },
        new Tool
        {
            Name = "use_revit_model",
            Description = "Chooses which open Revit model the other tools act on, by its name (part of the name is enough). Optionally narrow by Revit version when the same model is open in two versions.",
            InputSchema = JsonDocument.Parse("""
                {"type":"object","properties":{
                  "model":{"type":"string","description":"Model name as shown in Revit's title bar, or part of it"},
                  "revitVersion":{"type":"string","description":"Optional, e.g. 2024"}},
                 "required":["model"]}
                """).RootElement,
        },
    ];

    /// <summary>
    /// The job tools that tool results refer to ("still running as job N, call wait_for_job"). Always listed, first, even
    /// when the catalogue Revit wrote is missing them or damaged: a result must never name a tool Claude can't see.
    /// Revit's own definitions (tools.json) replace these when present.
    /// </summary>
    private static readonly Tool[] JobTools =
    [
        new Tool
        {
            Name = "wait_for_job",
            Description = "Get the result of a long tool call that was still running when its call returned (it said 'still running as job N'), " +
                          "or see what Revit is busy with. Waits up to wait_seconds for the job to finish, then returns its result, or its " +
                          "progress so far if it is still running. Call again to keep waiting.",
            InputSchema = JsonDocument.Parse("""
                {"type":"object","properties":{
                  "job_id":{"type":"integer","description":"The job number. Default: the latest job."},
                  "wait_seconds":{"type":"integer","description":"How long to wait for it to finish, 0-50 (default 40)."}}}
                """).RootElement,
        },
        new Tool
        {
            Name = "cancel_job",
            Description = "Ask a long-running tool call to stop. A job still waiting for Revit is dropped; a running one stops at its next check.",
            InputSchema = JsonDocument.Parse("""
                {"type":"object","properties":{"job_id":{"type":"integer","description":"Default: the running job."}}}
                """).RootElement,
        },
    ];

    public static List<Tool> LoadTools()
    {
        var file = Path.Combine(Folder, "tools.json");
        if (!File.Exists(file))
        {
            return
            [
                new Tool
                {
                    Name = "sketch_promax_status",
                    Description = "Sketch ProMax has not run in Revit yet. Call this to get setup help.",
                    InputSchema = JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement,
                },
            ];
        }

        List<Tool> tools;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            tools = doc.RootElement.EnumerateArray().Select(t => new Tool
            {
                Name = t.GetProperty("name").GetString()!,
                Description = t.GetProperty("description").GetString(),
                InputSchema = t.GetProperty("inputSchema").Clone(),
            }).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                       or KeyNotFoundException or InvalidOperationException)
        {
            // Revit may be rewriting the catalogue right now, or it is damaged; Revit writes it again on its next start.
            tools = [];
        }
        // The tools other results point to come first, so a client that shows only part of a long list still has them.
        var first = JobTools.Select(j => tools.FirstOrDefault(t => t.Name == j.Name) ?? j).ToList();
        tools.RemoveAll(t => JobTools.Any(j => j.Name == t.Name));
        tools.InsertRange(0, first);
        tools.InsertRange(0, ConnectionTools);
        tools.AddRange(LearnedTools(tools.Select(t => t.Name).ToHashSet()));
        return tools;
    }

    /// <summary>Tools Claude saved with save_as_tool, read from the files the add-in writes.</summary>
    private static IEnumerable<Tool> LearnedTools(HashSet<string> taken)
    {
        var folder = Path.Combine(Folder, "learned-tools");
        if (!Directory.Exists(folder)) yield break;
        foreach (var file in Directory.GetFiles(folder, "*.json").OrderBy(f => f))
        {
            Tool? tool = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var r = doc.RootElement;
                var name = r.GetProperty("name").GetString()!;
                if (taken.Add(name))
                    tool = new Tool
                    {
                        Name = name,
                        Description = r.GetProperty("description").GetString(),
                        InputSchema = r.GetProperty("inputSchema").Clone(),
                    };
            }
            catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException) { }
            if (tool is not null) yield return tool;
        }
    }

    public static string FirmStandards()
    {
        try
        {
            var file = Path.Combine(Folder, "settings.json");
            if (!File.Exists(file)) return "";
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            return doc.RootElement.TryGetProperty("FirmStandards", out var s) ? s.GetString() ?? "" : "";
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return ""; }
    }

    public static async Task<CallToolResult> CallAsync(string name, IDictionary<string, JsonElement>? arguments, CancellationToken ct)
    {
        if (name == "sketch_promax_status")
            return Text("Install the Sketch ProMax add-in, start Revit and open a model, then restart this app so the Revit tools load.", true);

        var sessions = await DiscoverAsync(ct);
        if (name == "list_revit_models") return Text(Describe(sessions), false);
        if (name == "use_revit_model") return Choose(sessions, arguments);

        if (sessions.Count == 0)
            return Text(_unresponsive > 0
                ? "Revit is running but not responding: it may be busy with a long operation or frozen. Ask the user to check Revit, then try again."
                : "Revit is not reachable. Ask the user to start Revit with the Sketch ProMax add-in and open a model.", true);

        var chosen = _target;
        var target = sessions.FirstOrDefault(s => s.Pid == chosen?.Pid);
        if (target is null && PreferredPid() is { } own && sessions.FirstOrDefault(s => s.Pid == own) is { } mine)
        {
            // Started by the chat pane inside a Revit window: work on that window unless Claude chose another model.
            target = mine;
            chosen = _target = new Target(mine.Pid, null);
        }
        if (target is null)
        {
            if (sessions.Count > 1)
                return Text("Several Revit windows are open. Ask the user which model to work on, then call use_revit_model.\n" + Describe(sessions), true);
            target = sessions[0];
            chosen = _target = new Target(target.Pid, null);
        }

        // Job questions don't touch a model, so they skip switching models (which would wait behind a busy Revit). Every
        // other call switches first; a busy Revit answers that at once with what it is running, so nothing runs on the wrong model.
        if (chosen!.Model is not null && name is not ("wait_for_job" or "cancel_job"))
        {
            var activate = await SendAsync(target.Pipe, JsonSerializer.Serialize(new { method = "activate_document", title = chosen.Model }), 5000, ct,
                readTimeoutMs: 15000);
            // Revit answers within about 12 s with the reason when it can't switch (a dialog, an edit mode); no answer at
            // all means even the add-in's connection thread didn't get to answer.
            if (activate is null)
                return Text("The chosen Revit window didn't answer within 20 s, so nothing was run. Revit is probably showing a dialog " +
                            "(possibly behind another window), busy with a long operation of its own (a sync, loading, printing), or frozen. " +
                            "Ask the user to look at Revit: close the dialog or wait for the operation to end, then try again. " +
                            "list_revit_models shows whether the window answers again." +
                            (target.Blocked is { } why ? $" Last seen: {why}." : ""), true);
            using var a = JsonDocument.Parse(activate);
            if (a.RootElement.TryGetProperty("isError", out var ae) && ae.GetBoolean())
                return Text(a.RootElement.GetProperty("text").GetString() ?? "Could not switch models.", true);
        }

        // From the in-Revit chat (SKETCHPROMAX_CHAT=1), Revit asks the user before code, recipe and file-writing calls run.
        var request = JsonSerializer.Serialize(new { method = "call_tool", name, arguments = arguments ?? new Dictionary<string, JsonElement>(), origin = FromChat ? "chat" : null });
        // The read has no timeout: a tool call may wait on the approval dialog in Revit.
        var responseLine = await SendAsync(target.Pipe, request, 5000, ct);
        if (responseLine is null)
            return Text("Revit is not reachable. Ask the user to start Revit with the Sketch ProMax add-in and open a model.", true);

        using var doc = JsonDocument.Parse(responseLine);
        var root = doc.RootElement;
        var text = root.GetProperty("text").GetString() ?? "";
        var isError = root.TryGetProperty("isError", out var e) && e.GetBoolean();

        var content = new List<ContentBlock> { new TextContentBlock { Text = text } };
        if (root.TryGetProperty("pngBase64", out var png) && png.ValueKind == JsonValueKind.String)
            content.Add(ImageContentBlock.FromBytes(Convert.FromBase64String(png.GetString()!), "image/png"));

        return new CallToolResult { Content = content, IsError = isError };
    }

    /// <summary>True when the in-Revit chat started this server (SKETCHPROMAX_CHAT=1 in the config it passes to Claude Code).</summary>
    private static bool FromChat => Environment.GetEnvironmentVariable("SKETCHPROMAX_CHAT") == "1";

    /// <summary>The Revit process whose chat pane started this server (SKETCHPROMAX_REVIT_PID), if any.</summary>
    private static int? PreferredPid() =>
        int.TryParse(Environment.GetEnvironmentVariable("SKETCHPROMAX_REVIT_PID"), out var pid) && pid > 0 ? pid : null;

    private static CallToolResult Choose(List<Session> sessions, IDictionary<string, JsonElement>? arguments)
    {
        var wanted = arguments is not null && arguments.TryGetValue("model", out var m) ? m.GetString() ?? "" : "";
        var version = arguments is not null && arguments.TryGetValue("revitVersion", out var v) ? v.ToString() : null;

        var matches = sessions
            .Where(s => string.IsNullOrEmpty(version) || s.RevitVersion == version)
            .SelectMany(s => s.Documents.Select(d => (Session: s, d.Title)))
            .Where(x => x.Title.Contains(wanted, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var exact = matches.Where(x => string.Equals(x.Title, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count > 0) matches = exact;

        if (matches.Count == 0)
            return Text($"No open model matches '{wanted}'.\n" + Describe(sessions), true);
        if (matches.Count > 1)
            return Text($"'{wanted}' matches more than one open model. Ask the user which one.\n" +
                string.Join("\n", matches.Select(x => $"- {x.Title} (Revit {x.Session.RevitVersion})")), true);

        _target = new Target(matches[0].Session.Pid, matches[0].Title);
        return Text($"Now working on '{matches[0].Title}' in Revit {matches[0].Session.RevitVersion}.", false);
    }

    private static string Describe(List<Session> sessions)
    {
        if (sessions.Count == 0)
            return _unresponsive > 0
                ? "Revit is running but not responding: it may be busy with a long operation or frozen. Ask the user to check Revit, then try again."
                : "No Revit window with Sketch ProMax is running.";
        var chosen = _target;
        var sb = new StringBuilder("Connected Revit windows:\n");
        foreach (var s in sessions)
        {
            var current = s.Pid == chosen?.Pid ? " (current)" : "";
            sb.AppendLine($"- Revit {s.RevitVersion}{current}{(s.Busy ? " (busy right now)" : "")}" +
                          (s.Blocked is { } why ? $" (waiting: {why}; ask the user to deal with it in Revit)" : ""));
            if (s.Documents.Count == 0) sb.AppendLine("    no model open");
            foreach (var d in s.Documents)
            {
                var flags = new List<string>();
                if (d.Active) flags.Add("active in Revit");
                if (s.Pid == chosen?.Pid && string.Equals(d.Title, chosen.Model, StringComparison.OrdinalIgnoreCase)) flags.Add("Claude is working on this");
                sb.AppendLine($"    {d.Title}{(flags.Count > 0 ? " [" + string.Join(", ", flags) + "]" : "")}");
            }
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Finds running Revit sessions from their announcement files, pinging each and removing stale ones.</summary>
    private static async Task<List<Session>> DiscoverAsync(CancellationToken ct)
    {
        var found = new List<Session>();
        var silent = 0;
        if (!Directory.Exists(SessionsFolder)) { _unresponsive = 0; return found; }

        foreach (var file in Directory.GetFiles(SessionsFolder, "*.json"))
        {
            int pid;
            string pipe, version;
            try
            {
                using var info = JsonDocument.Parse(File.ReadAllText(file));
                pid = info.RootElement.GetProperty("pid").GetInt32();
                pipe = info.RootElement.GetProperty("pipe").GetString()!;
                version = info.RootElement.TryGetProperty("revitVersion", out var rv) ? rv.GetString() ?? "" : "";
            }
            catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
            {
                continue;
            }

            if (!IsRunning(pid))
            {
                // Revit crashed or closed without cleaning up.
                try { File.Delete(file); } catch (IOException) { }
                continue;
            }

            // A Revit that doesn't answer within a few seconds is skipped, so one stuck window never blocks the others.
            var pong = await SendAsync(pipe, """{"method":"ping"}""", 1500, ct, readTimeoutMs: 7000);
            if (pong is null) { silent++; continue; }

            try
            {
                using var p = JsonDocument.Parse(pong);
                var docs = p.RootElement.TryGetProperty("documents", out var ds) && ds.ValueKind == JsonValueKind.Array
                    ? ds.EnumerateArray().Select(d => (
                        d.GetProperty("title").GetString() ?? "",
                        d.TryGetProperty("path", out var dp) ? dp.GetString() ?? "" : "",
                        d.TryGetProperty("active", out var da) && da.GetBoolean())).ToList()
                    : [];
                var busy = p.RootElement.TryGetProperty("busy", out var b) && b.ValueKind == JsonValueKind.True;
                var blocked = p.RootElement.TryGetProperty("blocked", out var bl) && bl.ValueKind == JsonValueKind.String ? bl.GetString() : null;
                found.Add(new Session(pid, pipe, version, busy, blocked, docs));
            }
            catch (JsonException) { }
        }
        _unresponsive = silent;
        return found.OrderBy(s => s.RevitVersion).ThenBy(s => s.Pid).ToList();
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return false; }
    }

    /// <summary>Sends one request line and returns the response line, or null when the pipe can't be reached.</summary>
    private static async Task<string?> SendAsync(string pipeName, string request, int connectTimeoutMs, CancellationToken ct, int readTimeoutMs = 0)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (readTimeoutMs > 0) limit.CancelAfter(connectTimeoutMs + readTimeoutMs);
        ct = limit.Token;
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(connectTimeoutMs, ct);
            var bytes = new UTF8Encoding(false).GetBytes(request + "\n");
            await pipe.WriteAsync(bytes, ct);
            await pipe.FlushAsync(ct);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false));
            return await reader.ReadLineAsync(ct);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException) { return null; }
        catch (OperationCanceledException) when (readTimeoutMs > 0 && limit.IsCancellationRequested) { return null; }
    }

    private static CallToolResult Text(string text, bool isError) =>
        new() { Content = [new TextContentBlock { Text = text }], IsError = isError };
}
