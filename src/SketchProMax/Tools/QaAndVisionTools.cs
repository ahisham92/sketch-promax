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
            note = "Lists are capped at 200 entries (duplicate room numbers at 100); the summary has the full counts.",
            unplacedRooms = unplacedRooms.Take(200), unboundedRooms = unboundedRooms.Take(200),
            roomsWithoutNumber = roomsWithoutNumber.Take(200),
            duplicateRoomNumbers = duplicateRoomNumbers.Take(100).ToDictionary(k => k.Key, k => k.Value.Take(200).ToList()),
            untaggedDoors = untaggedDoors.Take(200), untaggedWindows = untaggedWindows.Take(200),
            viewsNotOnSheets = viewsNotOnSheets.Take(200), emptySheets = emptySheets.Take(200),
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
      "pixel_width": {"type": "integer", "description": "Image width in pixels (default 1600, max 2400)."},
      "element_ids": {"type": "array", "items": {"type": "integer"}, "description": "Plan views: zoom to these elements (the view itself is left unchanged)."},
      "margin": {"type": "number", "description": "Around element_ids, project units. Default 2 m."}
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
            PixelSize = (int)Math.Min(Math.Max(args.OptLong("pixel_width") ?? 1600, 400), 2400),
            HLRandWFViewsFileType = ImageFileType.PNG,
            ShadowViewsFileType = ImageFileType.PNG,
            ImageResolution = ImageResolution.DPI_150,
        };
        options.SetViewsAndSheets([viewId]);

        // Zooming crops the view for the export only: the crop is committed inside a group that is then rolled back.
        ViewPlan? plan = null;
        List<BoundingBoxXYZ> boxes = [];
        double margin = 0;
        var ids = args.LongList("element_ids");
        if (ids.Count > 0)
        {
            plan = doc.GetElement(viewId) as ViewPlan;
            if (plan is null) return ToolResult.Error("element_ids zoom works on plan views; capture the whole view instead.");
            boxes = ids.Select(i => doc.GetElement(new ElementId(i))?.get_BoundingBox(null)).OfType<BoundingBoxXYZ>().ToList();
            if (boxes.Count == 0) return ToolResult.Error("None of those elements have a location to zoom to.");
            margin = args.OptDouble("margin") is { } m ? RevitHelpers.ToInternal(doc, m) : 2000 / 304.8;
        }
        var zoom = plan is null ? null : new TransactionGroup(doc, "Sketch ProMax: zoom for image");
        try
        {
            if (plan != null && zoom != null)
            {
                zoom.Start();
                var crop = plan.CropBox;
                var inverse = crop.Transform.Inverse;
                var corners = boxes.SelectMany(ViewKit.Corners).Select(inverse.OfPoint).ToList();
                using var t = new Transaction(doc, "Sketch ProMax: zoom for image");
                t.Start();
                plan.CropBox = new BoundingBoxXYZ
                {
                    Transform = crop.Transform,
                    Min = new XYZ(corners.Min(p => p.X) - margin, corners.Min(p => p.Y) - margin, crop.Min.Z),
                    Max = new XYZ(corners.Max(p => p.X) + margin, corners.Max(p => p.Y) + margin, crop.Max.Z),
                };
                plan.CropBoxActive = true;
                t.Commit();
            }
            doc.ExportImage(options);
        }
        finally { if (zoom != null) { if (zoom.HasStarted() && !zoom.HasEnded()) zoom.RollBack(); zoom.Dispose(); } }

        var file = Directory.GetFiles(dir, "*.png").FirstOrDefault();
        if (file is null) return ToolResult.Error("Revit did not produce an image for that view.");
        var bytes = File.ReadAllBytes(file);
        try { Directory.Delete(dir, true); } catch (IOException) { }

        return new ToolResult($"Image of view '{doc.GetElement(viewId)?.Name}'.", PngImage: bytes);
    }
}
