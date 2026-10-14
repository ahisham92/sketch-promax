using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

/// <summary>
/// Deterministic documentation checks. The checks themselves are plain code so results are
/// repeatable and auditable; Claude's job is to explain them and propose fixes.
/// </summary>
public sealed class RunModelAuditTool : IRevitTool
{
    public string Name => "run_model_audit";
    public string Description =>
        "Run deterministic QA/QC checks and return the failures with element ids: unplaced or unbounded rooms, " +
        "rooms without a number, doors/windows not tagged in any plan view, views not placed on any sheet, " +
        "empty sheets, and duplicate room numbers.";
    public string PropertiesJson => "{}";
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;

        var rooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms)
            .WhereElementIsNotElementType().Cast<Room>().ToList();
        var unplacedRooms = rooms.Where(r => r.Location is null).Select(r => r.Id.Value).ToList();
        var unboundedRooms = rooms.Where(r => r.Location != null && r.Area <= 0).Select(r => r.Id.Value).ToList();
        var roomsWithoutNumber = rooms.Where(r => string.IsNullOrWhiteSpace(r.Number)).Select(r => r.Id.Value).ToList();
        var duplicateRoomNumbers = rooms.Where(r => !string.IsNullOrWhiteSpace(r.Number))
            .GroupBy(r => r.Number).Where(g => g.Count() > 1)
            .ToDictionary(g => g.Key, g => g.Select(r => r.Id.Value).ToList());

        var tagged = new HashSet<long>();
        foreach (var tag in new FilteredElementCollector(doc).OfClass(typeof(IndependentTag)).Cast<IndependentTag>())
            foreach (var id in tag.GetTaggedLocalElementIds()) tagged.Add(id.Value);

        List<long> Untagged(BuiltInCategory c) => new FilteredElementCollector(doc).OfCategory(c)
            .WhereElementIsNotElementType().Select(e => e.Id.Value).Where(id => !tagged.Contains(id)).ToList();

        var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().ToList();
        var placed = sheets.SelectMany(s => s.GetAllPlacedViews()).Select(v => v.Value).ToHashSet();
        var printableTypes = new[] { ViewType.FloorPlan, ViewType.CeilingPlan, ViewType.Elevation, ViewType.Section, ViewType.Detail, ViewType.DraftingView };
        var viewsNotOnSheets = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
            .Where(v => !v.IsTemplate && printableTypes.Contains(v.ViewType) && !placed.Contains(v.Id.Value))
            .Select(v => new { id = v.Id.Value, name = v.Name }).ToList();
        var emptySheets = sheets.Where(s => s.GetAllPlacedViews().Count == 0)
            .Select(s => new { id = s.Id.Value, number = s.SheetNumber }).ToList();

        var untaggedDoors = Untagged(BuiltInCategory.OST_Doors);
        var untaggedWindows = Untagged(BuiltInCategory.OST_Windows);

        return ToolResult.Ok(new
        {
            summary = new
            {
                unplacedRooms = unplacedRooms.Count,
                unboundedRooms = unboundedRooms.Count,
                roomsWithoutNumber = roomsWithoutNumber.Count,
                duplicateRoomNumbers = duplicateRoomNumbers.Count,
                untaggedDoors = untaggedDoors.Count,
                untaggedWindows = untaggedWindows.Count,
                viewsNotOnSheets = viewsNotOnSheets.Count,
                emptySheets = emptySheets.Count,
            },
            unplacedRooms, unboundedRooms, roomsWithoutNumber, duplicateRoomNumbers,
            untaggedDoors = untaggedDoors.Take(200), untaggedWindows = untaggedWindows.Take(200),
            viewsNotOnSheets = viewsNotOnSheets.Take(200), emptySheets,
        });
    }
}

/// <summary>
/// Exports a view to PNG and hands the image to Claude, so the agent can visually check
/// what a drawing actually looks like (overlapping tags, clutter, missing annotation).
/// </summary>
public sealed class CaptureViewImageTool : IRevitTool
{
    public string Name => "capture_view_image";
    public string Description =>
        "Render a view or sheet to a PNG image and return it so you can look at it. Use this to visually " +
        "check drawings: overlapping tags or dimensions, crowded annotation, missing labels, layout issues.";
    public string PropertiesJson => """
    {
      "view_id": {"type": "integer", "description": "View or sheet id. Defaults to the active view."},
      "pixel_width": {"type": "integer", "description": "Image width in pixels (default 1600, max 2400)."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var viewId = args.OptLong("view_id") is { } id ? new ElementId(id) : doc.ActiveView.Id;
        var dir = Path.Combine(Path.GetTempPath(), "SketchProMax", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        var options = new ImageExportOptions
        {
            ExportRange = ExportRange.SetOfViews,
            FilePath = Path.Combine(dir, "view"),
            ZoomType = ZoomFitType.FitToPage,
            PixelSize = (int)Math.Clamp(args.OptLong("pixel_width") ?? 1600, 400, 2400),
            HLRandWFViewsFileType = ImageFileType.PNG,
            ShadowViewsFileType = ImageFileType.PNG,
            ImageResolution = ImageResolution.DPI_150,
        };
        options.SetViewsAndSheets([viewId]);
        doc.ExportImage(options);

        var file = Directory.GetFiles(dir, "*.png").FirstOrDefault();
        if (file is null) return ToolResult.Error("Revit did not produce an image for that view.");
        var bytes = File.ReadAllBytes(file);
        try { Directory.Delete(dir, true); } catch (IOException) { }

        return new ToolResult($"Image of view '{doc.GetElement(viewId)?.Name}'.", PngImage: bytes);
    }
}
