namespace SketchProMax.Tools;

/// <summary>
/// The set of Revit tools offered to Claude. Add a new capability by implementing
/// <see cref="IRevitTool"/> and registering it here.
/// </summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, IRevitTool> _tools;

    public ToolRegistry(IEnumerable<IRevitTool> tools) =>
        _tools = tools.ToDictionary(t => t.Name);

    public IReadOnlyCollection<IRevitTool> All => _tools.Values;

    public IRevitTool? Find(string name) => _tools.GetValueOrDefault(name);

    public static ToolRegistry CreateDefault() => new(
    [
        // Read-only
        new GetModelContextTool(),
        new QueryElementsTool(),
        new GetElementParametersTool(),
        new ListViewsAndSheetsTool(),
        new ListTypesTool(),
        new SelectElementsTool(),
        new RunModelAuditTool(),
        new CaptureViewImageTool(),
        // Model-changing (go through the approval gate)
        new SetParametersTool(),
        new CreateWallsTool(),
        new TagElementsInViewTool(),
        new CreateSheetTool(),
        new CreateScheduleTool(),
    ]);
}
