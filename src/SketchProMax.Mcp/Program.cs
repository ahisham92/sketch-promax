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
        o.ServerInfo = new() { Name = "sketch-promax", Version = "0.3.0" };
        o.ServerInstructions = SystemPrompt.Build(RevitBridge.FirmStandards());
    })
    .WithStdioServerTransport()
    .WithListToolsHandler((ctx, ct) => ValueTask.FromResult(new ListToolsResult { Tools = RevitBridge.LoadTools() }))
    .WithCallToolHandler(async (ctx, ct) =>
    {
        var p = ctx.Params ?? throw new InvalidOperationException("Missing tool call parameters.");
        return await RevitBridge.CallAsync(p.Name, p.Arguments, ct);
    });

await builder.Build().RunAsync();

internal static class RevitBridge
{
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax");

    private static string PipeName => $"SketchProMax.Revit.{Environment.UserName}";

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

        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        return doc.RootElement.EnumerateArray().Select(t => new Tool
        {
            Name = t.GetProperty("name").GetString()!,
            Description = t.GetProperty("description").GetString(),
            InputSchema = t.GetProperty("inputSchema").Clone(),
        }).ToList();
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

        var request = JsonSerializer.Serialize(new { method = "call_tool", name, arguments = arguments ?? new Dictionary<string, JsonElement>() });
        string responseLine;
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000, ct);
            var bytes = new UTF8Encoding(false).GetBytes(request + "\n");
            await pipe.WriteAsync(bytes, ct);
            await pipe.FlushAsync(ct);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false));
            responseLine = await reader.ReadLineAsync(ct) ?? "";
        }
        catch (TimeoutException)
        {
            return Text("Revit is not reachable. Ask the user to start Revit with the Sketch ProMax add-in and open a model.", true);
        }

        using var doc = JsonDocument.Parse(responseLine);
        var root = doc.RootElement;
        var text = root.GetProperty("text").GetString() ?? "";
        var isError = root.TryGetProperty("isError", out var e) && e.GetBoolean();

        var content = new List<ContentBlock> { new TextContentBlock { Text = text } };
        if (root.TryGetProperty("pngBase64", out var png) && png.ValueKind == JsonValueKind.String)
            content.Add(ImageContentBlock.FromBytes(Convert.FromBase64String(png.GetString()!), "image/png"));

        return new CallToolResult { Content = content, IsError = isError };
    }

    private static CallToolResult Text(string text, bool isError) =>
        new() { Content = [new TextContentBlock { Text = text }], IsError = isError };
}
