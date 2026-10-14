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
/// Lets an outside Claude client (Claude desktop app or Claude Code, through SketchProMax.Mcp.exe)
/// use the same Revit tools as the in-Revit chat pane. Conversations then run on the user's Claude
/// plan instead of an API key.
///
/// Protocol: one JSON request line per connection on a per-user named pipe, one JSON response line back.
///   {"method":"ping"}                                   -> {"ok":true,"document":"..."}
///   {"method":"call_tool","name":"...","arguments":{}}  -> {"text":"...","isError":false,"pngBase64":null}
/// The tool catalogue is written to %APPDATA%\SketchProMax\tools.json at startup so the MCP server can
/// list tools even before Revit is running.
/// </summary>
public sealed class BridgeServer
{
    public static string PipeName => $"SketchProMax.Revit.{Environment.UserName}";

    public static string ToolsFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax", "tools.json");

    private readonly ToolRegistry _registry;
    private readonly RevitDispatcher _dispatcher;
    private readonly CancellationTokenSource _cts = new();

    public BridgeServer(ToolRegistry registry, RevitDispatcher dispatcher)
    {
        _registry = registry;
        _dispatcher = dispatcher;
    }

    public void Start()
    {
        WriteToolCatalogue();
        Task.Run(() => AcceptLoop(_cts.Token));
    }

    public void Stop() => _cts.Cancel();

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
                // Pipe already owned by another Revit session, or the client went away. Back off and retry.
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
                    var title = await _dispatcher.InvokeAsync(app => app.ActiveUIDocument?.Document.Title).ConfigureAwait(false);
                    return JsonSerializer.Serialize(new { ok = true, document = title });

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
        var tool = _registry.Find(name);
        if (tool is null) return Task.FromResult(ToolResult.Error($"Unknown tool '{name}'."));
        var confirm = tool.ModifiesModel && AgentSettings.Load().RequireApprovalForChanges;

        return _dispatcher.InvokeAsync(app =>
        {
            if (app.ActiveUIDocument is null) return ToolResult.Error("No model is open in Revit. Ask the user to open one.");
            if (confirm && !Approve(tool, args)) return ToolResult.Error("The user rejected this change in Revit. Ask them how to proceed.");
            try { return tool.Execute(app, new ToolArgs(args)); }
            catch (Exception ex) { return ToolResult.Error($"{ex.GetType().Name}: {ex.Message}"); }
        });
    }

    private static bool Approve(IRevitTool tool, Dictionary<string, JsonElement> args)
    {
        var json = JsonSerializer.Serialize(args, new JsonSerializerOptions { WriteIndented = true });
        if (json.Length > 1500) json = json.Substring(0, 1500) + "\n…";
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

    private static string Error(string message) => JsonSerializer.Serialize(new { text = message, isError = true });
}
