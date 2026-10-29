using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace SketchProMax.Bridge;

/// <summary>A tool call's answer as the connector passes it on: its text, whether it failed, and a picture (PNG, base64) if any.</summary>
internal sealed record ConnectorResult(string Text, bool IsError, string? PngBase64 = null);

/// <summary>
/// Who one connection serves. <paramref name="FromChat"/>: the in-Revit chat (Revit then asks before code, recipes and
/// file writes). <paramref name="Job"/>: one of the chat's parallel jobs (its changes take turns with the other jobs').
/// <paramref name="PreferredPid"/>: the Revit window whose chat it is. <paramref name="Claude"/> and
/// <paramref name="Model"/>: the Claude Code and model read_pdf_part reads a part with.
/// </summary>
internal sealed record ConnectorCaller(bool FromChat, string? Job, int? PreferredPid, string? Claude, string? Model);

/// <summary>
/// One connection's way to the Revit windows: finds the running ones from their announcement files (BridgeServer),
/// keeps which window and model Claude works on, and forwards each tool call to that window over its pipe. The connector
/// program keeps one for its whole run (one per Claude session); the chat's connection inside Revit keeps one per MCP
/// session, and reaches its own window directly (<see cref="LocalPipe"/>) rather than over the pipe. No Revit and no MCP
/// library here: the connector, the add-in and the unit tests compile this file.
/// </summary>
internal sealed class RevitTargets
{
    private readonly ConnectorCaller _caller;

    /// <summary>This Revit window's pipe and its request handler, when this runs inside Revit; requests to it skip the pipe.</summary>
    private readonly (string Pipe, Func<string, CancellationToken, Task<string>> Handle)? _local;

    public RevitTargets(ConnectorCaller caller, (string Pipe, Func<string, CancellationToken, Task<string>> Handle)? local = null)
    {
        _caller = caller;
        _local = local;
    }

    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax");

    private static string SessionsFolder => Path.Combine(Folder, "sessions");

    // The Revit session and model Claude is working on. Null until chosen, or picked automatically when only one is open.
    // Kept as one immutable record so concurrent tool calls never see a pid from one choice and a model from another.
    private sealed record Target(int Pid, string? Model);
    private volatile Target? _target;

    // Revit windows that are running but did not answer the last ping (busy with a long operation, or frozen).
    private volatile int _unresponsive;

    private sealed record Session(int Pid, string Pipe, string RevitVersion, bool Busy, string? Blocked, List<(string Title, string Path, bool Active)> Documents);

    // ---- Set by the add-in (the connector program leaves them null) ------------------------------------------------

    /// <summary>The model (its title) a chat's job was given to work on, by the job's key; null or "" when none.</summary>
    public static Func<string, string?>? JobModel;

    /// <summary>A chat or job now works on the model with this title (it chose it, or it was given it).</summary>
    public static Action<string?, string>? Touched;

    /// <summary>
    /// Before a chat's call goes to another Revit window: the reason it may not (a check job asking for a change), or null.
    /// That window doesn't know the chat's jobs, so this window decides.
    /// </summary>
    public static Func<string, string, string?>? RemoteGuard;

    /// <summary>After a chat's call came back from another Revit window: its text with what the user sent meanwhile added.</summary>
    public static Func<string, string, string, string>? RemoteAfter;

    /// <summary>A chat's job works in another Revit window now ("reading") or is back to thinking ("").</summary>
    public static Action<string, string>? RemoteActivity;

        public async Task<ConnectorResult> CallAsync(string name, IDictionary<string, JsonElement>? arguments, CancellationToken ct)
    {
        if (name == ConnectorTools.StatusTool)
            return Text("Install the Sketch ProMax add-in, start Revit and open a model, then restart this app so the Revit tools load.", true);

        // Runs here, without Revit: a part of a large PDF read by its own chat.
        if (name == PdfPartReader.ToolName) return await PdfPartReader.CallAsync(arguments, _caller.Claude, _caller.Model, ct).ConfigureAwait(false);

        var sessions = await DiscoverAsync(ct).ConfigureAwait(false);
        if (name == "list_revit_models") return Text(Describe(sessions), false);
        if (name == "use_revit_model") return Choose(sessions, arguments);

        if (sessions.Count == 0)
            return Text(_unresponsive > 0
                ? "Revit is running but not responding: it may be busy with a long operation or frozen. Ask the user to check Revit, then try again."
                : "Revit is not reachable. Ask the user to start Revit with the Sketch ProMax add-in and open a model.", true);

        var chosen = _target;
        var target = sessions.FirstOrDefault(s => s.Pid == chosen?.Pid);
        // A job given a model of its own (chat_jobs start with model=…) works on it, in whichever Revit window it is open.
        if (target is null && _caller.Job is { Length: > 0 } given && JobModel?.Invoke(given) is { Length: > 0 } wanted
            && sessions.SelectMany(s => s.Documents.Select(d => (Session: s, d.Title))).FirstOrDefault(x => string.Equals(x.Title, wanted, StringComparison.OrdinalIgnoreCase)) is { Session: not null } at)
        {
            target = at.Session;
            chosen = _target = new Target(at.Session.Pid, at.Title);
            Touched?.Invoke(given, at.Title);
        }
        if (target is null && _caller.PreferredPid is { } own && sessions.FirstOrDefault(s => s.Pid == own) is { } mine)
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
                readTimeoutMs: 15000).ConfigureAwait(false);
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

        // A chat's call to another Revit window: this window keeps the chat's jobs, so it guards and delivers for them.
        var remote = _caller.FromChat && _caller.Job is { Length: > 0 } && !(_local is { } here && here.Pipe == target.Pipe);
        var caller = _caller.Job ?? "";
        if (remote && RemoteGuard?.Invoke(caller, name) is { } refused) return Text(refused, true);
        if (remote) RemoteActivity?.Invoke(caller, "reading");

        // From the in-Revit chat, Revit asks the user before code, recipe and file-writing calls run. From one of its
        // parallel jobs, the job's changes take turns with the other jobs' in Revit.
        var request = JsonSerializer.Serialize(new
        {
            method = "call_tool", name, arguments = arguments ?? new Dictionary<string, JsonElement>(), origin = _caller.FromChat ? "chat" : null,
            job = _caller.FromChat && _caller.Job is { Length: > 0 } job ? job : null,
        });
        // The read has no timeout: a tool call may wait on the approval dialog in Revit.
        string? responseLine;
        try { responseLine = await SendAsync(target.Pipe, request, 5000, ct).ConfigureAwait(false); }
        finally { if (remote) RemoteActivity?.Invoke(caller, ""); }
        if (responseLine is null)
            return Text("Revit is not reachable. Ask the user to start Revit with the Sketch ProMax add-in and open a model.", true);

        using var doc = JsonDocument.Parse(responseLine);
        var root = doc.RootElement;
        var text = root.GetProperty("text").GetString() ?? "";
        var isError = root.TryGetProperty("isError", out var e) && e.GetBoolean();
        var png = root.TryGetProperty("pngBase64", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        if (remote && !ct.IsCancellationRequested && RemoteAfter is { } after) text = after(caller, name, text);
        return new ConnectorResult(text, isError, png);
    }

    private ConnectorResult Choose(List<Session> sessions, IDictionary<string, JsonElement>? arguments)
    {
        var wanted = arguments is not null && arguments.TryGetValue("model", out var m) ? m.GetString() ?? "" : "";
        var version = arguments is not null && arguments.TryGetValue("revitVersion", out var v) ? v.ToString() : null;

        var matches = sessions
            .Where(s => string.IsNullOrEmpty(version) || s.RevitVersion == version)
            .SelectMany(s => s.Documents.Select(d => (Session: s, d.Title)))
            .Where(x => x.Title.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0)
            .ToList();
        var exact = matches.Where(x => string.Equals(x.Title, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count > 0) matches = exact;

        if (matches.Count == 0)
            return Text($"No open model matches '{wanted}'.\n" + Describe(sessions), true);
        if (matches.Count > 1)
            return Text($"'{wanted}' matches more than one open model. Ask the user which one.\n" +
                string.Join("\n", matches.Select(x => $"- {x.Title} (Revit {x.Session.RevitVersion})")), true);

        _target = new Target(matches[0].Session.Pid, matches[0].Title);
        Touched?.Invoke(_caller.Job, matches[0].Title);
        return Text($"Now working on '{matches[0].Title}' in Revit {matches[0].Session.RevitVersion}.", false);
    }

    private string Describe(List<Session> sessions)
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
    private async Task<List<Session>> DiscoverAsync(CancellationToken ct)
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
            var pong = await SendAsync(pipe, """{"method":"ping"}""", 1500, ct, readTimeoutMs: 7000).ConfigureAwait(false);
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
    private async Task<string?> SendAsync(string pipeName, string request, int connectTimeoutMs, CancellationToken ct, int readTimeoutMs = 0)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (readTimeoutMs > 0) limit.CancelAfter(connectTimeoutMs + readTimeoutMs);
        if (_local is { } local && local.Pipe == pipeName)
        {
            // This Revit window itself: its request handler directly. A time limit cancels the request, as a connector
            // that stops waiting drops its pipe.
            try
            {
                var answer = await local.Handle(request, limit.Token).ConfigureAwait(false);
                return readTimeoutMs > 0 && limit.IsCancellationRequested ? null : answer;
            }
            catch (OperationCanceledException) when (readTimeoutMs > 0 && limit.IsCancellationRequested) { return null; }
        }
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(connectTimeoutMs, limit.Token).ConfigureAwait(false);
            var bytes = new UTF8Encoding(false).GetBytes(request + "\n");
            await pipe.WriteAsync(bytes, 0, bytes.Length, limit.Token).ConfigureAwait(false);
            await pipe.FlushAsync(limit.Token).ConfigureAwait(false);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false));
            // Cancelling closes the pipe, which ends the read (the add-in then drops work still queued for Revit).
            using (limit.Token.Register(() => { try { pipe.Dispose(); } catch (Exception) { } }))
            {
                try { return await reader.ReadLineAsync().ConfigureAwait(false); }
                catch (ObjectDisposedException) { limit.Token.ThrowIfCancellationRequested(); throw new IOException("The pipe closed."); }
                catch (IOException) when (limit.IsCancellationRequested) { limit.Token.ThrowIfCancellationRequested(); throw; }
            }
        }
        catch (Exception ex) when (ex is TimeoutException or IOException) { return null; }
        catch (OperationCanceledException) when (readTimeoutMs > 0 && limit.IsCancellationRequested) { return null; }
    }

    private static ConnectorResult Text(string text, bool isError) => new(text, isError);
}
