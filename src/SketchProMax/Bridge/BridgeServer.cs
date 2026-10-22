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
///   (call_tool may carry "origin":"chat" when the in-Revit chat started the connector; see Approvals and ApprovalPolicy,
///   and "job":"<key>" when one of the chat's parallel jobs did; see JobTurns and Chat.JobBoard.)
/// The tool catalogue is written to %APPDATA%\SketchProMax\tools.json at startup so the MCP server can
/// list tools even before Revit is running. The chat's own connection inside Revit (ChatMcp) sends this window the same
/// requests without the pipe, straight to <see cref="Handle"/>.
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

    internal async Task<string> Handle(string line, CancellationToken ct)
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
                    // One of the chat's parallel jobs (its own session): its changes take turns with the other jobs'.
                    var jobKey = fromChat && root.TryGetProperty("job", out var jk) && jk.ValueKind == JsonValueKind.String && jk.GetString() is { Length: > 0 } k ? k : null;
                    var result = await CallTool(name, args, fromChat, ct, jobKey: jobKey).ConfigureAwait(false);
                    // Messages the user typed while the chat task ran go with the answer Claude reads now (any tool's,
                    // "still running" too). Not when the caller has gone: they then stay queued, and the chat sends
                    // what is left when the turn ends. A job's calls never take them: they are the main chat's.
                    if (fromChat && jobKey is null && !ct.IsCancellationRequested) result = result with { Text = JobNotes.Attach(name, result.Text) };
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

    /// <summary>A read Revit didn't take is tried this many more times, this long each, before the user is asked.</summary>
    private const int ReadRetries = 2, ReadRetryMs = 8000;

    /// <summary>How long switching models may wait for Revit (the connector waits 20 s for the answer).</summary>
    private const int ActivateWaitMs = 12000;

    /// <summary>A modal dialog open this long ends the wait early: Revit runs no add-in work behind one.</summary>
    private const int DialogWaitMs = 4000;

    /// <summary>A short "why Revit isn't answering" for pings, or null when there is nothing to say.</summary>
    private static string? Blocked(RevitBlock.State state) => state.Kind switch
    {
        RevitBlock.Kind.Dialog => string.IsNullOrWhiteSpace(state.Title) ? "a dialog is open in Revit" : $"a dialog is open in Revit: '{state.Title!.Trim()}'",
        RevitBlock.Kind.NotResponding => "Revit is busy with a long operation of its own",
        RevitBlock.Kind.EditMode => "Revit is in an edit mode",
        _ => null,
    };

    /// <summary>How long a call waits for its result before returning "still running" (the connector gives up later than this).</summary>
    private const int InlineWaitMs = 45000;

    private async Task<ToolResult> CallTool(string name, Dictionary<string, JsonElement> args, bool fromChat, CancellationToken ct, int handoffs = 0,
        string? jobKey = null)
    {
        // Job questions are answered here, without Revit's thread, so they work while Revit is busy.
        if (name == "wait_for_job") return await WaitForJobTool.AnswerAsync(new ToolArgs(args), ct).ConfigureAwait(false);
        if (name == "cancel_job") return CancelJobTool.Cancel(new ToolArgs(args));
        // The chat's parallel jobs: only the main chat of the pane manages them (not a job, not the desktop app).
        if (name == ChatJobsTool.ToolName) return ChatJobsTool.Answer(new ToolArgs(args), coordinator: fromChat && jobKey is null);
        // A call of the main chat's task (its card, Pause, Skip step, Stop, Undo all and typed notes); a job's calls are its own.
        var task = fromChat && jobKey is null;

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
        if (task && JobControl.Current is { } control && await BeforeStep(control, tool.Name, ct).ConfigureAwait(false) is { } early) return early;

        Job? job;
        JobTurns.Ticket? turn = null;
        if (jobKey is null)
        {
            // One job at a time: say what is running (or queued) instead of waiting behind it until the call times out.
            if (Jobs.Running is { } running) return Jobs.Busy(running);
            if (_dispatcher.IsRunning) return Jobs.Busy(null);
            job = Jobs.TryCreate(tool.Name, out var inTheWay);
            if (job is null) return Jobs.Busy(inTheWay);
        }
        else
        {
            // A parallel job waits instead: a change for its turn in the one queue, then any call for Revit to be free.
            (job, turn) = await TakeTurnAsync(jobKey, tool, ct).ConfigureAwait(false);
            if (job is null) return ToolResult.Error(ct.IsCancellationRequested ? "The call was cancelled while it waited its turn; nothing was run."
                : $"{tool.Name} waited more than {(int)TurnWait.TotalMinutes} minutes for its turn in Revit, so nothing was run. Call it again to wait again.");
        }
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
            var chat = task ? JobControl.Current : null;
            JobControl.Tool = chat;
            if (task)
            {
                JobFeed.StepStarted(tool.Name);
                JobUndo.BeginTool();
            }
            // Every way out of a chat call ends its step (the feed, and a skip is used up), rejected or not.
            ToolResult Ended(ToolResult r) => task && job.Continuation is null ? AfterStep(chat, tool.Name, r) : r;
            try
            {
                // Time saved and AI time: from here (the approval wait included) to the end of the run.
                TimeLog.BeginTool(app, tool, fromChat);
                if (app.ActiveUIDocument is null) return Ended(ToolResult.Error("No model is open in Revit. Ask the user to open one."));
                // Whether to ask first (Approvals, ApprovalPolicy): risky calls and the always-asked ones always; normal
                // changes per the approval level, unless the task is approved or the user said not to ask about the tool
                // again. From the chat this dialog is the only prompt the user sees (Claude Code runs there without its own).
                var decision = Approvals.Decide(app, tool, args, fromChat, chat);
                // A tool with its own approval dialog (the package plan, each redline) asks a normal change there instead.
                var askHere = decision.Ask && !(decision.CanSilence && Approvals.OwnDialog(tool.Name));
                Approvals.Call = new Approvals.CallState(decision, chat, askHere);
                if (askHere && !Approvals.Ask(tool, args, decision, chat)) return Ended(ToolResult.Error("The user rejected this change in Revit. Ask them how to proceed."));
                RunningCall.Begin(fromChat ? "the chat" : "Claude", tool.Name, app.ActiveUIDocument.Document.Title, args);
                ToolResult result;
                try
                {
                    result = FailureGuard.Run(() =>
                    {
                        try { return tool.Execute(app, new ToolArgs(args)); }
                        catch (ManualStepException manual) { return Handoff.FromException(tool, app, new ToolArgs(args), manual); }
                        catch (OperationCanceledException ex) when (job.CancelRequested || chat?.Halting == true) { return ToolResult.Error(JobControl.EndedEarly(ex.Message)); }
                        catch (Exception ex) { return ToolResult.Error(Describe(tool, ex)); }
                    });
                }
                finally { RunningCall.End(); }
                ActivityLog.Write(app.ActiveUIDocument?.Document?.Title ?? "", tool, args, result);
                TimeLog.EndTool(result);
                return Ended(Capped(result));
            }
            finally
            {
                TimeLog.EndTool(null); // rejected or no model: only the wait is counted (no-op once ended above)
                Approvals.Call = null;
                if (task) JobUndo.EndTool();
                JobControl.Tool = null;
                // The chat step of a call that handed on ends with its continuation (Jobs.Follow).
                if (task && job.Continuation is not null) job.AfterContinuation = r => AfterStep(chat, tool.Name, r);
                // A tool that handed on to a continuation (the field test) is still running: the continuation ends the job.
                if (job.Continuation is null)
                {
                    job.Finished = DateTime.UtcNow;
                    Jobs.Running = null;
                }
            }
        }, queued.Token);
        job.Task = Jobs.Follow(job, firstCall);
        // A parallel job's change holds its turn until its Revit job ends (a "still running" answer doesn't give it back).
        if (turn is not null) _ = job.Task.ContinueWith(_ => JobTurns.Shared.Leave(turn), TaskScheduler.Default);

        // Revit picks work up within a second when idle. If it doesn't, it is showing a dialog (said within a few seconds,
        // with its title), busy with a long operation of its own, in an edit mode, or running a command of its own: say
        // which rather than time out. The wait asks Revit again every second.
        var pickedUp = Task.WhenAny(job.Task, job.StartedSignal.Task);
        var why = await _dispatcher.WaitForPickupAsync(pickedUp, $"{tool.Name} (job {job.Id})", StartWaitMs, DialogWaitMs, ct).ConfigureAwait(false);
        // A read changes nothing, so before asking the user it is tried again quietly (it stays queued and Revit is asked
        // again), unless a dialog or a hung Revit says waiting can't help.
        for (var retry = 0; why is not null && !tool.ModifiesModel && retry < ReadRetries && !ct.IsCancellationRequested
                            && !pickedUp.IsCompleted && _dispatcher.Blocker().Kind is not (RevitBlock.Kind.Dialog or RevitBlock.Kind.NotResponding); retry++)
            why = await _dispatcher.WaitForPickupAsync(pickedUp, $"{tool.Name} (job {job.Id}, try {retry + 2})", ReadRetryMs, DialogWaitMs, ct).ConfigureAwait(false);
        // Only "dropped" when the bridge, not Revit, won the race: otherwise the job is running and is waited for below.
        if (why is not null && !ct.IsCancellationRequested && job.TryAbandon())
        {
            // Only the user can end an edit mode or answer a dialog: hand it over, and send the call again on Continue.
            if (handoffs >= 3) return ToolResult.Error(why);
            return await HandOverBlocked(key + "\n#" + handoffs, name, args, fromChat, tool.Name, why, handoffs, ct, jobKey).ConfigureAwait(false);
        }

        await Task.WhenAny(job.Task, Task.Delay(InlineWaitMs, ct)).ConfigureAwait(false);
        if (job.Task.IsCompleted) return job.Task.Status == TaskStatus.RanToCompletion ? job.Task.Result : Jobs.Status(job);
        return StillRunning(job);
    }

    /// <summary>The longest a parallel job's call waits for its turn and for Revit (its session allows longer: ClaudeCli.JobStart).</summary>
    internal static readonly TimeSpan TurnWait = TimeSpan.FromMinutes(30);

    /// <summary>
    /// A parallel job's call waits its turn: a change takes a ticket in the one queue (JobTurns) and waits to be at the
    /// front; then any call waits for Revit to be free of other work and creates its Revit job. The job's card says
    /// "Waiting its turn" meanwhile. Returns the Revit job and the ticket (the caller gives it back when the job ends);
    /// a null job when the wait was cancelled or too long (no ticket is kept then).
    /// </summary>
    private async Task<(Job? Job, JobTurns.Ticket? Turn)> TakeTurnAsync(string jobKey, IRevitTool tool, CancellationToken ct)
    {
        var turns = JobTurns.Shared;
        var ticket = tool.ModifiesModel ? turns.Enter(jobKey, tool.Name) : null;
        var waited = false;
        var started = DateTime.UtcNow;
        try
        {
            if (ticket is not null && turns.Position(ticket) > 1)
            {
                waited = true;
                void Moved() => Chat.ChatJobs.SetWaiting(jobKey, Math.Max(1, turns.Position(ticket)));
                turns.Changed += Moved;
                try
                {
                    Moved();
                    if (!await turns.WaitAsync(ticket, TurnWait, ct).ConfigureAwait(false)) return (null, null);
                }
                finally { turns.Changed -= Moved; }
            }
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (Jobs.Running is null && !_dispatcher.IsRunning && Jobs.TryCreate(tool.Name, out _) is { } job)
                {
                    var keep = ticket;
                    ticket = null; // handed to the caller
                    return (job, keep);
                }
                if (DateTime.UtcNow - started > TurnWait) return (null, null);
                if (!waited)
                {
                    waited = true;
                    Chat.ChatJobs.SetWaiting(jobKey, 1);
                }
                await Task.Delay(400, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { return (null, null); }
        finally
        {
            if (ticket is not null) turns.Leave(ticket);
            if (waited) Chat.ChatJobs.SetWaiting(jobKey, null);
        }
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
        int handoffs, CancellationToken ct, string? jobKey)
    {
        BlockedCall entry;
        bool first;
        lock (_blocked)
        {
            first = !_blocked.TryGetValue(key, out entry!);
            if (first) _blocked[key] = entry = new BlockedCall();
            entry.Waiters++;
        }
        if (first) _ = Task.Run(() => RunBlocked(entry, key, name, args, fromChat, tool, why, handoffs, jobKey));
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

    private async Task RunBlocked(BlockedCall entry, string key, string name, Dictionary<string, JsonElement> args, bool fromChat, string tool, string why, int handoffs,
        string? jobKey)
    {
        ToolResult result;
        try
        {
            var step = await BlockedStep(tool).ConfigureAwait(false);
            result = await Handoff.AskBlocked(step, entry.Stop.Token).ConfigureAwait(false)
                ? await CallTool(name, args, fromChat, entry.Stop.Token, handoffs + 1, jobKey).ConfigureAwait(false)
                : ToolResult.Error(why);
        }
        catch (Exception ex) { result = ToolResult.Error($"{ex.GetType().Name}: {ex.Message}"); }
        finally
        {
            lock (_blocked) { if (_blocked.TryGetValue(key, out var open) && open == entry) _blocked.Remove(key); }
        }
        entry.Result.TrySetResult(result);
    }

    /// <summary>
    /// The hand-off for a call Revit didn't take: what is holding Revit and how the user frees it, in one short line each,
    /// naming the step as the user would ("reading your model"), never by its tool id (RevitBlock.Card).
    /// </summary>
    private async Task<HandoffStep> BlockedStep(string tool)
    {
        var state = await _dispatcher.BlockerAsync().ConfigureAwait(false);
        return RevitBlock.Card(state, RevitBlock.Friendly(tool, _registry.Find(tool)?.ModifiesModel ?? true));
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
    /// Asks the user before a tool runs whose code is new (a learned tool started from the ribbon or a recipe) or that is
    /// always asked about. Call on Revit's main thread. Approving a learned tool also approves its code, so it isn't asked
    /// about again until the code changes. <paramref name="reason"/>: why it is asked.
    /// </summary>
    internal static bool Approve(IRevitTool tool, IReadOnlyDictionary<string, JsonElement> args, string? reason = null) =>
        Approvals.Ask(tool, args, new ApprovalDecision(true, AskKind.Risky,
            reason ?? (tool is LearnedTool ? "This learned tool's code has not been allowed on this PC yet, or it changed since. Read it in the details." : "Check the details before you allow it.")),
            null);

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
