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

    /// <summary>A built-in tool, or a learned tool read fresh from disk so tools saved in another Revit window work too.</summary>
    public IRevitTool? Find(string name) => _tools.TryGetValue(name, out var tool) ? tool : LearnedTools.Load(name);

    public bool IsBuiltIn(string name) => _tools.ContainsKey(name);

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
        new DimensionRoomsTool(),
        new SetCurtainGridTool(),
        new ExportBcfTool(),

        // Translation, CAD conversion, coordinates, bulk placement
        new CollectTextTool(),
        new ApplyTextTool(),
        new ImportCadTool(),
        new ListCadLayersTool(),
        new ConvertCadTool(),
        new GetCoordinatesTool(),
        new SetCoordinatesTool(),
        new ConvertCoordinatesTool(),
        new PlaceManyTool(),
        new CreateMepOpeningsTool(),
        new CutSlabTool(),
        new AddSlabEdgesTool(),
        new HollowCoreLayoutTool(),

        // Title blocks
        new ListTitleBlocksTool(),
        new FillTitleBlocksTool(),
        new SetSheetTitleBlockTool(),
        new TitleBlockFromCadTool(),
        new CopyFromFileTool(),
        new EditFamilyTextTool(),
        new CreateProjectParametersTool(),
        new PlaceLegendOnSheetsTool(),
        new SetSheetRevisionsTool(),
        new ExportDrawingListTool(),
        new ExportQuantitiesTool(),
        new CheckScheduleTool(),

        // Steel
        new LoadSteelSectionsTool(),
        new PlaceBracingTool(),
        new ListSteelConnectionTypesTool(),
        new CreateSteelConnectionsTool(),
        new ExportSteelTakeoffTool(),

        // Reinforcement
        new ReinforceBeamTool(),
        new ReinforceColumnTool(),
        new ReinforceFootingTool(),
        new ReinforceSlabWallTool(),
        new RebarSectionTool(),
        new Rebar3dViewTool(),
        new ExportBarScheduleTool(),

        // Precast and post-tensioning
        new ExportPrecastScheduleTool(),
        new PlacePtTendonsTool(),
        new ExportTendonScheduleTool(),

        // Coordination and checks
        new FindClashesTool(),
        new CheckDrawingsTool(),
        new CheckCodeRulesTool(),
        new ConstructionSequenceTool(),
        new CopyMonitorTool(),
        new LoadingPlanTool(),

        // Architecture to structure, and issuing drawings
        new ArchToStructureTool(),
        new CreateRampTool(),
        new EquipmentPadsTool(),
        new SleeveScheduleTool(),
        new GeneralNotesTool(),
        new TypicalDetailsTool(),
        new IssueDrawingsTool(),
        new LinkModelTool(),
        new CheckModelTool(),
        new FindMissingItemsTool(),
        new ModelSnapshotTool(),
        new CoordinationProgressTool(),
        new BoqCheckTool(),
        new BoqItemBreakdownTool(),
        new CostEstimateTool(),
        new SpecCheckTool(),
        new AnalyticalCheckTool(),
        new ExportAnalyticalTool(),
        new CreateRaftTool(),
        new WorkProgressTool(),

        // Marine, infrastructure and robustness
        new CreatePilesTool(),
        new MarineStructureTool(),
        new PortFurnitureTool(),
        new DesignVesselTool(),
        new VesselCollisionCheckTool(),
        new ProgressiveCollapseCheckTool(),

        // Detailing
        new AddKeynoteTagsTool(),
        new PlaceDetailComponentsTool(),
        new DimensionElementsTool(),
        new CreateToposolidTool(),

        // Activity log
        new GetActivityLogTool(),
        new ListRecipesTool(),

        // Anything else, through the Revit API
        new RunRevitQueryCodeTool(),
        new RunRevitCodeTool(),
        new SketchProMax.Bridge.WaitForJobTool(),
        new SketchProMax.Bridge.CancelJobTool(),
        // Worksharing
        new ListWorksetsTool(),
        new MakeWorksetsEditableTool(),
        new BorrowElementsTool(),
        new ElementOwnersTool(),
        new RelinquishMineTool(),
        // Detailing, views and annotation repair
        new PlaceDetailItemCopyTool(),
        new DuplicateViewToLocationTool(),
        new RelocateViewWithDetailingTool(),
        new CopyViewDetailingTool(),
        new AddDimensionsTool(),
        new CopyDimensionsTool(),
        new ReflowAnnotationsTool(),
        new TrimMatchlinesTool(),
        // Cuts, voids and geometry repair
        new PlaceVoidCutsTool(),
        new VerifyVoidCutTool(),
        new CutOpeningsInJoinedTool(),
        new CreateFloorsFromFacesTool(),
        new CutGeometryTool(),
    ]);
        // Recipes replay other tools, so they need the registry itself.
        registry.Add(new SaveRecipeTool(registry));
        registry.Add(new RunRecipeTool(registry));
        registry.Add(new BatchRunRecipeTool(registry));
        registry.Add(new SaveAsToolTool(registry));
        registry.Add(new ListLearnedToolsTool());
        registry.Add(new RunLearnedToolTool());
        registry.Add(new DeleteLearnedToolTool());
        registry.Add(new RunSelfTestTool(registry));
        registry.Add(new SelfTestStatusTool());
        registry.Add(new RunFieldTestTool(registry));
        return registry;
    }
}
