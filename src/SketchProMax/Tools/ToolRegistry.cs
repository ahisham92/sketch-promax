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

    private void Add(IRevitTool tool) => _tools[tool.Name] = tool;

    public IReadOnlyCollection<IRevitTool> All => _tools.Values;

    public IRevitTool? Find(string name) => _tools.TryGetValue(name, out var tool) ? tool : null;

    public static ToolRegistry CreateDefault()
    {
        var registry = new ToolRegistry(
    [
        // Read-only: understand the model
        new GetModelContextTool(),
        new QueryElementsTool(),
        new GetElementParametersTool(),
        new ListViewsAndSheetsTool(),
        new ListViewTemplatesTool(),
        new ListTypesTool(),
        new ListRevisionsTool(),
        new ListTextNotesTool(),
        new ReadScheduleTool(),
        new SelectElementsTool(),
        // Read-only: QA and output
        new RunModelAuditTool(),
        new CaptureViewImageTool(),
        new ExportScheduleTool(),
        new ExportPdfTool(),
        // Documentation (change the model; go through the approval gate)
        new TagElementsInViewTool(),
        new DimensionGridsTool(),
        new DimensionWallsTool(),
        new CreatePlanViewsTool(),
        new DuplicateViewsTool(),
        new ApplyViewTemplateTool(),
        new CreateSheetsTool(),
        new PlaceViewsOnSheetTool(),
        new RenameElementsTool(),
        new CreateScheduleTool(),
        new CreateRevisionTool(),
        new CloudElementsTool(),
        new UpdateTextNotesTool(),
        // View filters and graphics
        new ListViewFiltersTool(),
        new CreateViewFilterTool(),
        new ApplyViewFilterTool(),
        new ColorByParameterTool(),
        // Images, sketches and families from references
        new ImportImageTool(),
        new CreateDraftingViewTool(),
        new CreateFamilyFromShapesTool(),
        // Modeling
        new CreateLevelsTool(),
        new CreateGridsTool(),
        new CreateWallsTool(),
        new CreateFloorsTool(),
        new CreateCeilingsTool(),
        new CreateRoofTool(),
        new PlaceHostedFamiliesTool(),
        new PlaceFamilyInstancesTool(),
        new PlaceColumnsTool(),
        new PlaceBeamsTool(),
        new TransformElementsTool(),
        // Model data
        new SetParametersTool(),
        new PlaceRoomsTool(),
        new DuplicateTypeTool(),
        new ChangeTypeTool(),
        new WorksetsTool(),
        new DeleteElementsTool(),

        // Views, graphics, annotation, schedules, exports, links, families
        new CreateSectionTool(),
        new CreateElevationsTool(),
        new CreateCalloutTool(),
        new Create3DViewTool(),
        new SetViewGraphicsTool(),
        new AddAnnotationsTool(),
        new ArrangeTagsTool(),
        new FormatScheduleTool(),
        new CreateSheetListTool(),
        new ExportFilesTool(),
        new QueryLinkedModelsTool(),
        new LoadFamilyTool(),

        // More modeling and dimensions
        new CreateStairsTool(),
        new CreateRailingsTool(),
        new CreateRoomSeparationLinesTool(),
        new SetMaterialTool(),
        new CreateMepRunsTool(),
        new DimensionOpeningsTool(),
        new AddSpotElevationsTool(),
        new SetCurtainGridTool(),
        new ExportBcfTool(),

        // Activity log
        new GetActivityLogTool(),
        new ListRecipesTool(),

        // Anything else, through the Revit API
        new RunRevitQueryCodeTool(),
        new RunRevitCodeTool(),
    ]);
        // Recipes replay other tools, so they need the registry itself.
        registry.Add(new SaveRecipeTool(registry));
        registry.Add(new RunRecipeTool(registry));
        return registry;
    }
}
