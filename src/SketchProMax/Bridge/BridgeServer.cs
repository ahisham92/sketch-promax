using System.IO;
using System.IO.Pipes;
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
            File.WriteAllText(ToolsFile, JsonSerializer.Serialize(tools, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException) { /* non-fatal: the MCP server reports that Revit hasn't run yet */ }
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                IsListening = true;
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);

                var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                var response = line is null ? Error("empty request") : await Handle(line).ConfigureAwait(false);

                var bytes = new UTF8Encoding(false).GetBytes(response + "\n");
                await pipe.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
                await pipe.FlushAsync(ct).ConfigureAwait(false);
                pipe.WaitForPipeDrain();
            }
            catch (OperationCanceledException) { return; }
            catch (IOException)
            {
                // The client went away mid-request, or the pipe is briefly unavailable. Back off and retry.
                await Task.Delay(2000, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<string> Handle(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            switch (root.GetProperty("method").GetString())
            {
                case "ping":
                    var documents = await _dispatcher.InvokeAsync(app =>
                    {
                        var active = app.ActiveUIDocument?.Document;
                        return app.Application.Documents.Cast<Autodesk.Revit.DB.Document>()
                            .Where(d => !d.IsFamilyDocument && !d.IsLinked)
                            .Select(d => new { title = d.Title, path = d.PathName, active = active != null && d.Equals(active) })
                            .ToList();
                    }).ConfigureAwait(false);
                    return JsonSerializer.Serialize(new { ok = true, pid = SessionId, revitVersion = _revitVersion, documents });

                case "activate_document":
                    var wanted = root.GetProperty("title").GetString() ?? "";
                    var error = await _dispatcher.InvokeAsync(app => Activate(app, wanted)).ConfigureAwait(false);
                    return error is null ? JsonSerializer.Serialize(new { ok = true }) : Error(error);

                case "call_tool":
                    var name = root.GetProperty("name").GetString() ?? "";
                    var args = root.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object
                        ? a.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
                        : new Dictionary<string, JsonElement>();
                    var result = await CallTool(name, args).ConfigureAwait(false);
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

    private Task<ToolResult> CallTool(string name, Dictionary<string, JsonElement> args)
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
        var confirm = tool.ModifiesModel && AgentSettings.Load().RequireApprovalForChanges;

        return _dispatcher.InvokeAsync(app =>
        {
            if (app.ActiveUIDocument is null) return ToolResult.Error("No model is open in Revit. Ask the user to open one.");
            if (confirm && !Approve(tool, args)) return ToolResult.Error("The user rejected this change in Revit. Ask them how to proceed.");
            ToolResult result;
            try { result = tool.Execute(app, new ToolArgs(args)); }
            catch (Exception ex) { result = ToolResult.Error(Describe(tool, ex)); }
            ActivityLog.Write(app.ActiveUIDocument.Document.Title, tool, args, result);
            return Capped(result);
        });
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

    private static bool Approve(IRevitTool tool, Dictionary<string, JsonElement> args)
    {
        // Show code as code, so the user can read what will run.
        var json = tool is LearnedTool learned
            ? $"Learned tool: {learned.Summary}\n\nInputs: {JsonSerializer.Serialize(args)}\n\n{learned.Code}"
            : args.TryGetValue("code", out var code) && code.ValueKind == JsonValueKind.String
            ? (args.TryGetValue("description", out var d) ? d.GetString() + "\n\n" : "") + code.GetString()
            : JsonSerializer.Serialize(args, new JsonSerializerOptions { WriteIndented = true });
        if (json.Length > 6000) json = json.Substring(0, 6000) + "\n…";
        var dialog = new TaskDialog("Sketch ProMax")
        {
            MainInstruction = $"Claude wants to run '{tool.Name}'",
            MainContent = "This changes the model. Each change can be undone with Ctrl+Z.",
            ExpandedContent = json,
            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
            DefaultButton = TaskDialogResult.Yes,
        };
        return dialog.Show() == TaskDialogResult.Yes;
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
