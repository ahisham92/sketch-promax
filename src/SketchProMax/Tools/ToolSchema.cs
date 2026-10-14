using System.Text.Json;

namespace SketchProMax.Tools;

/// <summary>Single source for how a tool is described to Claude, used by both the API pane and the MCP bridge.</summary>
internal static class ToolSchema
{
    public static string Description(IRevitTool tool) =>
        tool.Description + (tool.ModifiesModel ? " (Changes the model.)" : "");

    public static Dictionary<string, JsonElement> Properties(IRevitTool tool) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(tool.PropertiesJson) ?? [];

    public static object InputSchema(IRevitTool tool) => new
    {
        type = "object",
        properties = Properties(tool),
        required = tool.Required,
    };
}
