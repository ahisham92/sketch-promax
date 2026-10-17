using System.IO;
using System.IO.Pipes;
#if NETFRAMEWORK
using System.Security.AccessControl;
using System.Security.Principal;
#endif
using System.Text;
using System.Text.Json;
using Autodesk.Revit.UI;
using SketchProMax.Revit;
using SketchProMax.Settings;
using SketchProMax.Tools;

namespace SketchProMax.Bridge;

/// <summary>
/// Lets the Claude desktop app (or Claude Code), through SketchProMax.Mcp.exe, use the Revit tools.
/// Conversations run on the user's Claude plan; there is no API key.
///
/// Every Revit process gets its own pipe and announces itself with a file in %APPDATA%\SketchProMax\sessions,
/// so several Revit windows and versions can be connected at once; the MCP server picks one by model name.
///
/// Protocol: one JSON request line per connection, one JSON response line back.
///   {"method":"ping"}                                   -> {"ok":true,"pid":..,"revitVersion":"2025","documents":[..]}
///   {"method":"activate_document","title":"..."}       -> {"ok":true} or {"text":"...","isError":true}
///   {"method":"call_tool","name":"...","arguments":{}}  -> {"text":"...","isError":false,"pngBase64":null}
///   (call_tool may carry "origin":"chat" when the in-Revit chat started the connector; see OutputPaths.ChatApprovalReason.)
/// The tool catalogue is written to %APPDATA%\SketchProMax\tools.json at startup so the MCP server can
/// list tools even before Revit is running.
/// </summary>
public sealed class BridgeServer
{
    public static int SessionId { get; } = System.Diagnostics.Process.GetCurrentProcess().Id;

    public static string PipeName => $"SketchProMax.Revit.{Environment.UserName}.{SessionId}";

    private static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax");

    private static string SessionFile => Path.Combine(Root, "sessions", $"{SessionId}.json");

    public bool IsListening { get; private set; }

    public static string ToolsFile => Path.Combine(Root, "tools.json");

    private readonly ToolRegistry _registry;

    public ToolRegistry Registry => _registry;
    private readonly RevitDispatcher _dispatcher;
    private readonly CancellationTokenSource _cts = new();
    private readonly string _revitVersion;

    public BridgeServer(ToolRegistry registry, RevitDispatcher dispatcher, string revitVersion)
    {
        _registry = registry;
        _dispatcher = dispatcher;
        _revitVersion = revitVersion;
    }

    public void Start()
    {
        var ct = _cts.Token;
        Task.Run(() => AcceptLoop(ct));
        // The catalogue (every tool's schema, serialised) and the session file are written off Revit's thread: the core
        // starts while Revit may be opening a model. A connector asking before they land sees the previous ones.
        var started = DateTime.UtcNow;
        Task.Run(() =>
        {
            WriteToolCatalogue();
            if (ct.IsCancellationRequested) return; // stopped already: Stop may have removed the session file
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SessionFile)!);
                File.WriteAllText(SessionFile, JsonSerializer.Serialize(new
                {
                    pid = SessionId, pipe = PipeName, revitVersion = _revitVersion, started,
                }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        });
    }

    /// <summary>
    /// Stops listening. When Revit closes the session file goes too; when an update is being switched to
    /// (<paramref name="keepSession"/>), it stays, since the new core takes over the same pipe name straight away.
    /// </summary>
    public void Stop(bool keepSession = false)
    {
        _cts.Cancel();
        IsListening = false;
        if (keepSession) return;
        try { File.Delete(SessionFile); } catch (IOException) { }
    }

    /// <summary>When the Claude app (through the connector) last talked to this Revit window, for the ribbon.</summary>
    public static DateTime? LastContact { get; private set; }

    private void WriteToolCatalogue()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ToolsFile)!);
            var tools = _registry.All.Select(t => new
            {
                name = t.Name,
                description = ToolSchema.Description(t),
                inputSchema = ToolSchema.InputSchema(t),
            });
            // Written to a temp file and swapped in, so the MCP server never reads a half-written catalogue.
            var temp = ToolsFile + $".{SessionId}.tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(tools, new JsonSerializerOptions { WriteIndented = true }));
            try
            {
                if (File.Exists(ToolsFile)) File.Replace(temp, ToolsFile, null);
                else File.Move(temp, ToolsFile);
            }
            catch (IOException)
            {
                // Another Revit window wrote it at the same moment; copying over is still better than a stale file.
                File.Copy(temp, ToolsFile, true);
            }
            finally
            {
                try { File.Delete(temp); } catch (IOException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            /* non-fatal: the MCP server reports that Revit hasn't run yet */
        }
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                // Several connections at once: a status ping must not wait behind a long tool call.
                pipe = CreatePipe();
                IsListening = true;
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { pipe?.Dispose(); return; }
            catch (Exception)
            {
                // Whatever went wrong (pipe name taken, out of handles...), keep trying rather than go quiet for good.
                pipe?.Dispose();
                IsListening = false;
                try { await Task.Delay(2000, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                continue;
            }
            _ = Task.Run(() => Serve(pipe, ct));
        }
    }

    /// <summary>A pipe only this Windows user can open, and never from another machine.</summary>
    private static NamedPipeServerStream CreatePipe()
    {
#if NETFRAMEWORK
        var security = new PipeSecurity();
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("No current Windows user.");
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        return new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
#else
        // CurrentUserOnly gives the pipe an ACL for this user alone.
        return new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
#endif
    }

    private async Task Serve(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using (pipe)
        using (var request = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            try
            {
                var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                // If the connector goes away (Claude cancelled the call), work still queued for Revit is dropped.
                _ = WatchForDisconnect(pipe, request);
                var response = line is null ? Error("empty request") : await Handle(line, request.Token).ConfigureAwait(false);
                var bytes = new UTF8Encoding(false).GetBytes(response + "\n");
                await pipe.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
                await pipe.FlushAsync(ct).ConfigureAwait(false);
                pipe.WaitForPipeDrain();
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// The connector sends one line and then only reads, so a read that ends means it disconnected. Cancels the request
    /// then. Once the response is sent the pipe is disposed, which ends this read too; the request is over by then.
    /// </summary>
    private static async Task WatchForDisconnect(Stream pipe, CancellationTokenSource request)
    {
        var gone = false;
        try
        {
            var buffer = new byte[1];
            while (await pipe.ReadAsync(buffer, 0, 1, request.Token).ConfigureAwait(false) > 0) { }
            gone = true;
        }
        catch (IOException) { gone = true; }
        catch (Exception) { /* disposed or cancelled: the request finished normally */ }
        if (!gone) return;
        try { request.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    // The open models as of the last time Revit answered, for pings that arrive while Revit is busy.
    private volatile object _lastDocuments = Array.Empty<object>();

    private async Task<string> Handle(string line, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            LastContact = DateTime.UtcNow;
            switch (root.GetProperty("method").GetString())
            {
                case "ping":
                    var lookup = _dispatcher.InvokeAsync<object>(app =>
                    {
                        var active = app.ActiveUIDocument?.Document;
                        return app.Application.Documents.Cast<Autodesk.Revit.DB.Document>()
                            .Where(d => !d.IsFamilyDocument && !d.IsLinked)
                            .Select(d => new { title = d.Title, path = d.PathName, active = active != null && d.Equals(active) })
                            .ToList();
                    }, ct);
                    // Revit only answers when idle; a busy Revit still reports itself, with the last known models.
                    // Busy means a tool is actually running. A lookup that is merely waiting its turn gets a little
                    // longer, since the dispatcher wakes an idle Revit on its own; it is never reported as busy.
                    var answered = await Task.WhenAny(lookup, Task.Delay(1500)).ConfigureAwait(false) == lookup;
                    if (!answered && !_dispatcher.IsRunning)
                        answered = await Task.WhenAny(lookup, Task.Delay(2500)).ConfigureAwait(false) == lookup;
                    var busy = !answered && _dispatcher.IsRunning;
                    if (answered) _lastDocuments = await lookup.ConfigureAwait(false);
                    // Not answered and nothing of ours running: say what holds Revit (a dialog, a long operation, an edit mode).
                    var blocked = answered || busy ? null : Blocked(_dispatcher.Blocker());
                    return JsonSerializer.Serialize(new { ok = true, pid = SessionId, revitVersion = _revitVersion, busy, blocked, documents = _lastDocuments });

                case "activate_document":
                    if (Jobs.Running is { } busyWith) return Error(Jobs.Busy(busyWith).Text);
                    var wanted = root.GetProperty("title").GetString() ?? "";
                    // Switching documents is only allowed from the external event, not from Idling. Bounded: a Revit that
                    // doesn't take it (a dialog, an edit mode) is reported with the reason rather than left hanging.
                    // Only not being picked up means nothing changed: once Revit has started the switch it runs to the end,
                    // however long opening the model takes.
                    using (var switching = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        var waitStart = Environment.TickCount;
                        var activation = _dispatcher.InvokeFromEventAsync(app =>
                        {
                            started.TrySetResult(true);
                            return Activate(app, wanted);
                        }, switching.Token);
                        var pickedUp = Task.WhenAny(activation, started.Task);
                        var why = await _dispatcher.WaitForPickupAsync(pickedUp, "Switching to the chosen model", ActivateWaitMs, DialogWaitMs, ct).ConfigureAwait(false);
                        if (why is not null && !started.Task.IsCompleted && !activation.IsCompleted)
                        {
                            switching.Cancel();
                            return Error(why);
                        }
                        var left = Math.Max(1000, ActivateWaitMs - unchecked(Environment.TickCount - waitStart));
                        if (await Task.WhenAny(activation, Task.Delay(left, ct)).ConfigureAwait(false) != activation)
                            return Error($"Revit is switching to '{wanted}', and opening it is taking a while (a large model). The switch carries on; " +
                                         "nothing else was run. Try the call again in a moment.");
                        var error = await activation.ConfigureAwait(false);
                        return error is null ? JsonSerializer.Serialize(new { ok = true }) : Error(error);
                    }

                case "call_tool":
                    var name = root.GetProperty("name").GetString() ?? "";
                    var args = root.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object
                        ? a.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
                        : new Dictionary<string, JsonElement>();
                    var fromChat = root.TryGetProperty("origin", out var origin) && origin.ValueKind == JsonValueKind.String && origin.GetString() == "chat";
                    var result = await CallTool(name, args, fromChat, ct).ConfigureAwait(false);
                    // Messages the user typed while the chat task ran go with the answer Claude reads now (any tool's,
                    // "still running" too). Not when the caller has gone: they then stay queued, and the chat sends
                    // what is left when the turn ends.
                    if (fromChat && !ct.IsCancellationRequested) result = result with { Text = JobNotes.Attach(name, result.Text) };
                    return JsonSerializer.Serialize(new
                    {
                        text = result.Text,
                        isError = result.IsError,
                        pngBase64 = result.PngImage is null ? null : Convert.ToBase64String(result.PngImage),
                    });

                default:
                    return Error("unknown method");
            }
        }
        catch (Exception ex)
        {
            return Error($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>How long a call waits for Revit to pick it up before concluding Revit is in an edit mode or a dialog.</summary>
    private const int StartWaitMs = 20000;

    /// <summary>How long switching models may wait for Revit (the connector waits 20 s for the answer).</summary>
    private const int ActivateWaitMs = 12000;

    /// <summary>A modal dialog open this long ends the wait early: Revit runs no add-in work behind one.</summary>
    private const int DialogWaitMs = 4000;

    /// <summary>A short "why Revit isn't answering" for pings, or null when there is nothing to say.</summary>
    private static string? Blocked(RevitBlock.State state) => state.Kind switch
    {
        RevitBlock.Kind.Dialog => string.IsNullOrWhiteSpace(state.Title) ? "a dialog is open in Revit" : $"a dialog is open in Revit: '{state.Title!.Trim()}'",
        RevitBlock.Kind.NotResponding => "Revit is busy with a long operation of its own",
        _ => null,
    };

    /// <summary>How long a call waits for its result before returning "still running" (the connector gives up later than this).</summary>
    private const int InlineWaitMs = 45000;

    private async Task<ToolResult> CallTool(string name, Dictionary<string, JsonElement> args, bool fromChat, CancellationToken ct, int handoffs = 0)
    {
        // Job questions are answered here, without Revit's thread, so they work while Revit is busy.
        if (name == "wait_for_job") return await WaitForJobTool.AnswerAsync(new ToolArgs(args), ct).ConfigureAwait(false);
        if (name == "cancel_job") return CancelJobTool.Cancel(new ToolArgs(args));

        if (name == "run_learned_tool" && args.TryGetValue("name", out var inner) && inner.ValueKind == JsonValueKind.String
            && LearnedTools.Load(inner.GetString()!) is { } learned)
        {
            // Run the learned tool itself, so it gets its own approval, transaction and log entry.
            name = learned.Name;
            args = args.TryGetValue("arguments", out var a) && a.ValueKind == JsonValueKind.Object
                ? a.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
                : new Dictionary<string, JsonElement>();
        }
        var tool = _registry.Find(name);
        if (tool is null) return ToolResult.Error($"Unknown tool '{name}'.");

        var key = HandoffKeys.Call(name, args);
        if (Jobs.Running is { Continuation: { } rest } held && held.Key == key)
        {
            // The same call again (Claude retried it) while its hand-off waits for the user: wait for that one, so the
            // user sees one card or window and Continue runs the work once.
            await Task.WhenAny(rest, Task.Delay(InlineWaitMs, ct)).ConfigureAwait(false);
            if (rest.IsCompleted) return rest.Status == TaskStatus.RanToCompletion ? rest.Result : Jobs.Status(held);
            return StillRunning(held);
        }
        // The chat task's Pause, Skip step and Stop between steps: a pause waits here, off Revit's thread, so Revit stays free.
        if (fromChat && JobControl.Current is { } control && await BeforeStep(control, tool.Name, ct).ConfigureAwait(false) is { } early) return early;

        // One job at a time: say what is running (or queued) instead of waiting behind it until the call times out.
        if (Jobs.Running is { } running) return Jobs.Busy(running);
        if (_dispatcher.IsRunning) return Jobs.Busy(null);

        // A learned tool's code is asked about the first time it runs (and after it changes), even when read-only.
        // From the in-Revit chat, code, recipes and files written where Claude chose are always asked about: Claude Code
        // runs there without its own permission prompts, so this dialog is the only place the user sees them.
        var chatReason = fromChat ? OutputPaths.ChatApprovalReason(tool.Name, args) : null;
        // A few calls are always asked about, from the chat or the Claude app (what_if with confirm).
        if (OutputPaths.AlwaysAskReason(tool.Name, args) is { } always) chatReason = chatReason is null ? always : always + " " + chatReason;
        var confirm = tool.ModifiesModel && AgentSettings.Load().RequireApprovalForChanges
                      || tool is LearnedTool unapproved && !LearnedTools.IsApproved(unapproved.Code)
                      || chatReason is not null;

        var job = Jobs.TryCreate(tool.Name, out var inTheWay);
        if (job is null) return Jobs.Busy(inTheWay);
        job.Key = key;
        // Dropped while still queued if the caller goes away or cancel_job asks; once started it runs to the end.
        var queued = CancellationTokenSource.CreateLinkedTokenSource(ct, job.Abandon.Token);
        job.CanContinue = true;
        var firstCall = _dispatcher.InvokeAsync(app =>
        {
            if (!job.TryStart()) return ToolResult.Error("Dropped before Revit started it; nothing was changed.");
            Jobs.Running = job;
            job.StartedSignal.TrySetResult(true);
            // The chat task this call belongs to, for its whole run (a later task gets a control of its own).
            var chat = fromChat ? JobControl.Current : null;
            JobControl.Tool = chat;
            if (fromChat)
            {
                JobFeed.StepStarted(tool.Name);
                JobUndo.BeginTool();
            }
            // Every way out of a chat call ends its step (the feed, and a skip is used up), rejected or not.
            ToolResult Ended(ToolResult r) => fromChat && job.Continuation is null ? AfterStep(chat, tool.Name, r) : r;
            try
            {
                // Time saved and AI time: from here (the approval wait included) to the end of the run.
                TimeLog.BeginTool(app, tool, fromChat);
                if (app.ActiveUIDocument is null) return Ended(ToolResult.Error("No model is open in Revit. Ask the user to open one."));
                if (confirm && !Approve(tool, args, chatReason)) return Ended(ToolResult.Error("The user rejected this change in Revit. Ask them how to proceed."));
                var result = FailureGuard.Run(() =>
                {
                    try { return tool.Execute(app, new ToolArgs(args)); }
                    catch (ManualStepException manual) { return Handoff.FromException(tool, app, new ToolArgs(args), manual); }
                    catch (OperationCanceledException ex) when (job.CancelRequested || chat?.Halting == true) { return ToolResult.Error(JobControl.EndedEarly(ex.Message)); }
                    catch (Exception ex) { return ToolResult.Error(Describe(tool, ex)); }
                });
                ActivityLog.Write(app.ActiveUIDocument?.Document?.Title ?? "", tool, args, result);
                TimeLog.EndTool(result);
                return Ended(Capped(result));
            }
            finally
            {
                TimeLog.EndTool(null); // rejected or no model: only the wait is counted (no-op once ended above)
                if (fromChat) JobUndo.EndTool();
                JobControl.Tool = null;
                // The chat step of a call that handed on ends with its continuation (Jobs.Follow).
                if (fromChat && job.Continuation is not null) job.AfterContinuation = r => AfterStep(chat, tool.Name, r);
                // A tool that handed on to a continuation (the field test) is still running: the continuation ends the job.
                if (job.Continuation is null)
                {
                    job.Finished = DateTime.UtcNow;
                    Jobs.Running = null;
                }
            }
        }, queued.Token);
        job.Task = Jobs.Follow(job, firstCall);

        // Revit picks work up within a second when idle. If it doesn't, it is showing a dialog (said within a few seconds,
        // with its title), busy with a long operation of its own, or in an edit mode (in-place family, sketch, group
        // edit): say which rather than time out.
        var pickedUp = Task.WhenAny(job.Task, job.StartedSignal.Task);
        var why = await _dispatcher.WaitForPickupAsync(pickedUp, $"{tool.Name} (job {job.Id})", StartWaitMs, DialogWaitMs, ct).ConfigureAwait(false);
        // Only "dropped" when the bridge, not Revit, won the race: otherwise the job is running and is waited for below.
        if (why is not null && !ct.IsCancellationRequested && job.TryAbandon())
        {
            // Only the user can end an edit mode or answer a dialog: hand it over, and send the call again on Continue.
            if (handoffs >= 3) return ToolResult.Error(why);
            return await HandOverBlocked(key + "\n#" + handoffs, name, args, fromChat, tool.Name, why, handoffs, ct).ConfigureAwait(false);
        }

        await Task.WhenAny(job.Task, Task.Delay(InlineWaitMs, ct)).ConfigureAwait(false);
        if (job.Task.IsCompleted) return job.Task.Status == TaskStatus.RanToCompletion ? job.Task.Result : Jobs.Status(job);
        return StillRunning(job);
    }

    /// <summary>How long a call waits at a paused chat task before telling Claude to send it again (the connector waits longer).</summary>
    private const int PauseWaitMs = 40000;

    /// <summary>
    /// Before a chat tool call starts: waits while the task is paused, and answers for the step when the user pressed
    /// Skip step or Stop meanwhile (nothing runs then). Null: run the call.
    /// </summary>
    private static async Task<ToolResult?> BeforeStep(JobControl control, string tool, CancellationToken ct)
    {
        if (control.EnterPause() || control.State == JobState.Paused)
        {
            JobFeed.SetPaused(true);
            try { await control.WaitWhilePausedAsync(PauseWaitMs, ct).ConfigureAwait(false); }
            finally { JobFeed.SetPaused(false); }
            if (control.State == JobState.Paused) return ToolResult.Error(JobControl.PausedText);
        }
        switch (control.State)
        {
            case JobState.Skipping:
                control.StepEnded();
                JobFeed.StepEnded(tool, "skipped", "skipped by the user before it started");
                return new ToolResult(JobControl.SkippedText + " (It was skipped before it started; nothing was run.)");
            case JobState.Stopping:
                JobFeed.StepEnded(tool, "stopped", "the user stopped the task before it started");
                return ToolResult.Error(JobControl.StoppedText + " (Nothing of this step was run.)");
        }
        return null;
    }

    /// <summary>
    /// A chat tool call ended: says Skip step or Stop first and ends the step in the job feed. The user's messages are
    /// added where the answer goes back to Claude (a result collected later by wait_for_job carries them then).
    /// </summary>
    private static ToolResult AfterStep(JobControl? control, string tool, ToolResult result)
    {
        var text = JobControl.AfterStep(control, tool, result.Text, result.IsError, out var state, notes: false);
        var reason = state == "failed" ? (result.Text.Replace("\r", "").Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? "") : "";
        JobFeed.StepEnded(tool, state, reason.Length > 200 ? reason.Substring(0, 200) + "…" : reason);
        // A skipped step is not an error: Claude should move on, not try it again.
        return result with { Text = text, IsError = state != "skipped" && result.IsError };
    }

    private static ToolResult StillRunning(Job job) => new(JsonSerializer.Serialize(new
    {
        status = "still running in Revit",
        job = job.Id, tool = job.Tool, elapsed = Jobs.Duration(job.Elapsed), progress = job.Progress(),
        next = $"Call wait_for_job (a sketch-promax tool) with job_id {job.Id} to wait for the result; it is kept when it finishes. Don't send other model work meanwhile, " +
               "and use cancel_job if it should stop.",
    }));

    /// <summary>One hand-off for a call Revit didn't take, shared by every identical call while it is open.</summary>
    private sealed class BlockedCall
    {
        public readonly CancellationTokenSource Stop = new();
        public readonly TaskCompletionSource<ToolResult> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Waiters;
    }

    private readonly Dictionary<string, BlockedCall> _blocked = new();

    /// <summary>
    /// Hands a call Revit didn't take to the user, once per identical call: a retry while the step is open waits for the
    /// same answer (one card or window), and Continue sends the call again once; every waiter gets that one result.
    /// The step closes as Stop only when every caller has gone.
    /// </summary>
    private async Task<ToolResult> HandOverBlocked(string key, string name, Dictionary<string, JsonElement> args, bool fromChat, string tool, string why,
        int handoffs, CancellationToken ct)
    {
        BlockedCall entry;
        bool first;
        lock (_blocked)
        {
            first = !_blocked.TryGetValue(key, out entry!);
            if (first) _blocked[key] = entry = new BlockedCall();
            entry.Waiters++;
        }
        if (first) _ = Task.Run(() => RunBlocked(entry, key, name, args, fromChat, tool, why, handoffs));
        try
        {
            var gone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (ct.Register(() => gone.TrySetResult(true)))
            {
                if (await Task.WhenAny(entry.Result.Task, gone.Task).ConfigureAwait(false) != entry.Result.Task)
                    return ToolResult.Error("The call was cancelled while the user was asked to free Revit.");
            }
            return await entry.Result.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_blocked)
            {
                if (--entry.Waiters == 0 && !entry.Result.Task.IsCompleted)
                    try { entry.Stop.Cancel(); } catch (ObjectDisposedException) { }
            }
        }
    }

    private async Task RunBlocked(BlockedCall entry, string key, string name, Dictionary<string, JsonElement> args, bool fromChat, string tool, string why, int handoffs)
    {
        ToolResult result;
        try
        {
            result = await Handoff.AskBlocked(BlockedStep(tool), entry.Stop.Token).ConfigureAwait(false)
                ? await CallTool(name, args, fromChat, entry.Stop.Token, handoffs + 1).ConfigureAwait(false)
                : ToolResult.Error(why);
        }
        catch (Exception ex) { result = ToolResult.Error($"{ex.GetType().Name}: {ex.Message}"); }
        finally
        {
            lock (_blocked) { if (_blocked.TryGetValue(key, out var open) && open == entry) _blocked.Remove(key); }
        }
        entry.Result.TrySetResult(result);
    }

    /// <summary>The hand-off for a call Revit didn't take: what is holding Revit, and how the user ends it.</summary>
    private static HandoffStep BlockedStep(string tool)
    {
        var state = RevitBlock.Read(App.MainWindow);
        var why = $"Revit runs no add-in work while {{0}}, so '{tool}' could not start. Nothing was changed; Continue sends it again.";
        return state.Kind switch
        {
            RevitBlock.Kind.Dialog => new HandoffStep
            {
                Kind = "dialog", What = string.IsNullOrWhiteSpace(state.Title) ? "Answer or close the dialog open in Revit" : $"Answer or close the dialog '{state.Title!.Trim()}' in Revit",
                Where = "Revit's window; the dialog may be behind another window (Alt+Tab to Revit)", Why = string.Format(why, "a dialog is open"),
            },
            RevitBlock.Kind.NotResponding => new HandoffStep
            {
                Kind = "busy", What = "Wait until Revit responds again, or cancel its long operation",
                Where = "Revit's window (the status bar shows what it is doing)", Why = string.Format(why, "it is busy with a long operation of its own"),
            },
            _ => new HandoffStep
            {
                Kind = "edit_mode", What = "Finish or cancel the edit mode in Revit, or end the running command",
                Where = "Modify tab: the green tick (Finish Edit Mode) or the red cross (Cancel Edit Mode); Esc ends a command",
                Why = string.Format(why, "you edit a sketch, a group or an in-place family, or a command runs"),
            },
        };
    }

    /// <summary>Makes the named open model the active one, so tools act on it. Returns an error message or null.</summary>
    private static string? Activate(UIApplication app, string title)
    {
        var doc = app.Application.Documents.Cast<Autodesk.Revit.DB.Document>()
            .Where(d => !d.IsFamilyDocument && !d.IsLinked)
            .FirstOrDefault(d => string.Equals(d.Title, title, StringComparison.OrdinalIgnoreCase));
        if (doc is null) return $"'{title}' is not open in this Revit session.";
        if (app.ActiveUIDocument?.Document.Equals(doc) == true) return null;
        if (string.IsNullOrEmpty(doc.PathName)) return $"'{title}' has never been saved, so it can't be switched to automatically. Ask the user to click into its window in Revit.";
        try
        {
            app.OpenAndActivateDocument(doc.PathName);
            return null;
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or InvalidOperationException)
        {
            return $"Revit could not switch to '{title}' ({ex.Message}). Ask the user to click into its window in Revit.";
        }
    }

    /// <summary>
    /// Asks the user before a tool runs. Call on Revit's main thread. Approving a learned tool also approves its code,
    /// so it isn't asked about again until the code changes. <paramref name="reason"/>: why it is asked, when that is
    /// not simply a model change (a call from the in-Revit chat).
    /// </summary>
    internal static bool Approve(IRevitTool tool, IReadOnlyDictionary<string, JsonElement> args, string? reason = null)
    {
        // Show code as code, so the user can read what will run.
        var json = tool is LearnedTool learned
            ? $"Learned tool: {learned.Summary}\n\nInputs: {JsonSerializer.Serialize(args)}\n\n{learned.Code}"
            : args.TryGetValue("code", out var code) && code.ValueKind == JsonValueKind.String
            ? (args.TryGetValue("description", out var d) ? d.GetString() + "\n\n" : "") + code.GetString()
            : JsonSerializer.Serialize(args, new JsonSerializerOptions { WriteIndented = true }) + RecipeText(tool, args);
        if (json.Length > 6000) json = json.Substring(0, 6000) + "\n…";
        var firstRun = tool is LearnedTool l && !LearnedTools.IsApproved(l.Code);
        var dialog = new TaskDialog("Sketch ProMax")
        {
            MainInstruction = $"Claude wants to run '{tool.Name}'",
            MainContent = (reason is null ? "" : reason + " ") +
                          (firstRun ? "This learned tool's code has not been approved on this PC yet, or it changed since. Read it below. " : "") +
                          (tool.ModifiesModel
                              ? "This changes the model. Each change can be undone with Ctrl+Z."
                              : "It only reads the model; any change it tries is rolled back."),
            ExpandedContent = json,
            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
            DefaultButton = TaskDialogResult.No,
        };
        var yes = dialog.Show() == TaskDialogResult.Yes;
        if (yes && tool is LearnedTool approved) LearnedTools.MarkApproved(approved.Code);
        return yes;
    }

    /// <summary>For run_recipe and batch_run_recipe, the recipe's own steps, so the user sees what will actually run.</summary>
    private static string RecipeText(IRevitTool tool, IReadOnlyDictionary<string, JsonElement> args)
    {
        var key = tool.Name switch { "run_recipe" => "name", "batch_run_recipe" => "recipe", _ => null };
        if (key is null || !args.TryGetValue(key, out var name) || name.ValueKind != JsonValueKind.String) return "";
        try
        {
            var path = Recipes.PathFor(name.GetString()!);
            return File.Exists(path) ? $"\n\nRecipe ({path}):\n{File.ReadAllText(path)}" : "\n\n(No recipe file with that name.)";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return ""; }
    }

    /// <summary>An unexpected exception, with where it happened so a bug report can be fixed without guessing.</summary>
    internal static string Describe(IRevitTool tool, Exception ex)
    {
        if (ex is System.Reflection.TargetInvocationException { InnerException: { } inner }) ex = inner;
        var expected = ex is ArgumentException and not ArgumentNullException || ex is InvalidOperationException
                       || ex is Autodesk.Revit.Exceptions.ArgumentException || ex is Autodesk.Revit.Exceptions.InvalidOperationException;
        if (expected) return ex.Message;
        var frame = new System.Diagnostics.StackTrace(ex, false).GetFrames()?
            .Select(f => f.GetMethod())
            .FirstOrDefault(m => m?.DeclaringType?.Namespace?.StartsWith("SketchProMax", StringComparison.Ordinal) == true);
        var where = frame is null ? "" : $" in {frame.DeclaringType!.Name}.{frame.Name}";
        return $"{tool.Name} hit an internal error ({ex.GetType().Name}{where}: {ex.Message}). This is a Sketch ProMax bug; " +
               "tell the user, and try another way for now (for example run_revit_code).";
    }

    private const int MaxResultChars = 24000;

    /// <summary>A safety net for any tool: a huge result floods the chat, so it is cut with a note on how to ask for less.</summary>
    private static ToolResult Capped(ToolResult result)
    {
        if (result.Text.Length <= MaxResultChars) return result;
        return result with
        {
            Text = result.Text.Substring(0, MaxResultChars) +
                   $"\n…[cut at {MaxResultChars:N0} of {result.Text.Length:N0} characters. Ask for less: use a filter such as " +
                   "name_contains, category or level, or limit and offset to page through.]",
        };
    }

    private static string Error(string message) => JsonSerializer.Serialize(new { text = message, isError = true });
}
