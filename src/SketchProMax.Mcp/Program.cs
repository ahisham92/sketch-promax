using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using SketchProMax.Agent;
using SketchProMax.Bridge;

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
        if (ConnectorTools.ChangesList(p.Name) && result.IsError != true)
            await ctx.Server.SendNotificationAsync(NotificationMethods.ToolListChangedNotification, ct);
        return result;
    });

await builder.Build().RunAsync();

internal static class RevitBridge
{
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax");

    /// <summary>This connector's version (major.minor.build), from the &lt;Version&gt; in the project file.</summary>
    public static string Version { get; } = typeof(RevitBridge).Assembly.GetName().Version is { } v
        ? $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}"
        : "0.0.0";

    // From the in-Revit chat's config (SKETCHPROMAX_CHAT=1), when it runs this program rather than its own connection
    // inside Revit: Revit then asks the user before code, recipe and file-writing calls run. SKETCHPROMAX_JOB: one of the
    // chat's parallel jobs (its changes take turns). SKETCHPROMAX_REVIT_PID: the window whose chat it is.
    // SKETCHPROMAX_CLAUDE and _MODEL: read_pdf_part reads a part with the same Claude Code and model.
    private static readonly RevitTargets Targets = new(new ConnectorCaller(
        FromChat: Environment.GetEnvironmentVariable("SKETCHPROMAX_CHAT") == "1",
        Job: Environment.GetEnvironmentVariable("SKETCHPROMAX_JOB") is { Length: > 0 } key ? key : null,
        PreferredPid: int.TryParse(Environment.GetEnvironmentVariable("SKETCHPROMAX_REVIT_PID"), out var pid) && pid > 0 ? pid : null,
        Claude: Environment.GetEnvironmentVariable("SKETCHPROMAX_CLAUDE"),
        Model: Environment.GetEnvironmentVariable("SKETCHPROMAX_MODEL")));

    public static List<Tool> LoadTools()
    {
        var catalogue = ConnectorTools.ReadCatalogue(Path.Combine(Folder, "tools.json"));
        var tools = catalogue is null ? [ConnectorTools.Status] : ConnectorTools.Compose(catalogue, Path.Combine(Folder, "learned-tools"));
        return tools.Select(t => new Tool { Name = t.Name, Description = t.Description, InputSchema = t.InputSchema }).ToList();
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
        var result = await Targets.CallAsync(name, arguments, ct);
        var content = new List<ContentBlock> { new TextContentBlock { Text = result.Text } };
        if (result.PngBase64 is { } png)
            content.Add(ImageContentBlock.FromBytes(Convert.FromBase64String(png), "image/png"));
        return new CallToolResult { Content = content, IsError = result.IsError };
    }
}
