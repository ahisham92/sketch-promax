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
        WriteToolCatalogue();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SessionFile)!);
            File.WriteAllText(SessionFile, JsonSerializer.Serialize(new
            {
                pid = SessionId, pipe = PipeName, revitVersion = _revitVersion, started = DateTime.UtcNow,
            }));
        }
        catch (IOException) { }
        Task.Run(() => AcceptLoop(_cts.Token));
    }

    public void Stop()
    {
        _cts.Cancel();
        try { File.Delete(SessionFile); } catch (IOException) { }
    }

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
                    var busy = await Task.WhenAny(lookup, Task.Delay(1500)).ConfigureAwait(false) != lookup;
                    if (!busy) _lastDocuments = await lookup.ConfigureAwait(false);
                    return JsonSerializer.Serialize(new { ok = true, pid = SessionId, revitVersion = _revitVersion, busy, documents = _lastDocuments });

                case "activate_document":
                    var wanted = root.GetProperty("title").GetString() ?? "";
                    var error = await _dispatcher.InvokeAsync(app => Activate(app, wanted), ct).ConfigureAwait(false);
                    return error is null ? JsonSerializer.Serialize(new { ok = true }) : Error(error);

                case "call_tool":
                    var name = root.GetProperty("name").GetString() ?? "";
                    var args = root.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object
                        ? a.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
                        : new Dictionary<string, JsonElement>();
                    var result = await CallTool(name, args, ct).ConfigureAwait(false);
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

    private Task<ToolResult> CallTool(string name, Dictionary<string, JsonElement> args, CancellationToken ct)
    {
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
        if (tool is null) return Task.FromResult(ToolResult.Error($"Unknown tool '{name}'."));
        // A learned tool's code is asked about the first time it runs (and after it changes), even when read-only.
        var confirm = tool.ModifiesModel && AgentSettings.Load().RequireApprovalForChanges
                      || tool is LearnedTool unapproved && !LearnedTools.IsApproved(unapproved.Code);

        return _dispatcher.InvokeAsync(app =>
        {
            if (app.ActiveUIDocument is null) return ToolResult.Error("No model is open in Revit. Ask the user to open one.");
            if (confirm && !Approve(tool, args)) return ToolResult.Error("The user rejected this change in Revit. Ask them how to proceed.");
            ToolResult result;
            result = FailureGuard.Run(() =>
            {
                try { return tool.Execute(app, new ToolArgs(args)); }
                catch (Exception ex) { return ToolResult.Error(Describe(tool, ex)); }
            });
            ActivityLog.Write(app.ActiveUIDocument?.Document?.Title ?? "", tool, args, result);
            return Capped(result);
        }, ct);
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
    /// so it isn't asked about again until the code changes.
    /// </summary>
    internal static bool Approve(IRevitTool tool, IReadOnlyDictionary<string, JsonElement> args)
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
            MainContent = (firstRun ? "This learned tool's code has not been approved on this PC yet, or it changed since. Read it below. " : "") +
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
