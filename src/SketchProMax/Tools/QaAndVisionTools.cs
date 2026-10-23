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
        // A sheet holding only a schedule has no viewports, so schedule instances (not title block revision schedules)
        // count as content too. Placeholder sheets hold nothing by design and are not reported.
        var withSchedules = new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>()
            .Where(i => !i.IsTitleblockRevisionSchedule).Select(i => i.OwnerViewId.Value).ToHashSet();
        var emptySheets = sheets.Where(s => !s.IsPlaceholder && s.GetAllPlacedViews().Count == 0 && !withSchedules.Contains(s.Id.Value))
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
        "check drawings: overlapping tags or dimensions, crowded annotation, missing labels, layout issues. " +
        "By default it grabs the screen when the view is the open, active view in Revit and no export-only option " +
        "(element_ids, margin, region, resolution_dpi) is given, and otherwise uses Revit's image export; the result says which and why. " +
        "To read small print on a sheet (the title panel, a drawing number, a schedule), pass region [x_min, y_min, x_max, y_max] in sheet " +
        "coordinates (project units on paper, as place_views_on_sheet reports them): the sheet is exported at high resolution and that region cut out. " +
        "source 'export' forces Revit's image export, which prints the view like a print/PDF would; " +
        "source 'screen' forces a grab of exactly what Revit shows in the open, active view window (selection highlights included). " +
        "Note: Revit draws a CUT pattern override only where the cut plane passes through an element; parts seen beyond " +
        "it (e.g. the lower part of a sloped slab in a section) use the projection/surface pattern, normally the " +
        "material's. The result lists filters/categories that override only the cut pattern.";
    public string PropertiesJson => """
    {
      "view_id": {"type": "integer", "description": "View or sheet id. Defaults to the active view."},
      "pixel_width": {"type": "integer", "description": "Image width in pixels (default 1600, max 2400)."},
      "element_ids": {"type": "array", "items": {"type": "integer"}, "description": "Plans, sections, elevations and detail views: zoom to these elements (the view itself is left unchanged). The image is exported at the pixel density of the export resolution, so it can come out narrower than pixel_width; for small print use a sheet region."},
      "region": {"type": "array", "items": {"type": "number"}, "description": "Sheets: [x_min, y_min, x_max, y_max] in sheet coordinates (project units, paper size) to export at high resolution, e.g. the title panel."},
      "margin": {"type": "number", "description": "Around element_ids, project units. Default 2 m."},
      "source": {"type": "string", "enum": ["export", "screen"], "description": "Default: screen when the view is the active view and no element_ids/margin/resolution_dpi are given, else export. export: Revit image export. screen: what the open, active view window shows right now."},
      "resolution_dpi": {"type": "integer", "enum": [72, 150, 300, 600], "description": "Export resolution, which sets how line weights and patterns are drawn (default 150)."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var viewId = args.OptLong("view_id") is { } id ? new ElementId(id) : doc.ActiveView.Id;
        var maxWidth = (int)Math.Min(Math.Max(args.OptLong("pixel_width") ?? 1600, 400), 2400);
        var patternNotes = CutPatternNotes(doc, doc.GetElement(viewId) as View, args.LongList("element_ids"));
        var source = args.OptString("source");
        if (source is not (null or "export" or "screen")) throw new ArgumentException("source must be export or screen.");
        string how;
        if (source is null)
        {
            // Screen is what the user sees and is instant; export is needed for zooming to elements or a set resolution.
            var exportOnly = new[] { "element_ids", "margin", "region", "resolution_dpi" }.Where(args.Has).ToList();
            var isActive = doc.ActiveView.Id == viewId && app.ActiveUIDocument.GetOpenUIViews().Any(u => u.ViewId == viewId);
            if (exportOnly.Count > 0) how = $"Used image export because {string.Join(", ", exportOnly)} need{(exportOnly.Count == 1 ? "s" : "")} it.";
            else if (!isActive) how = "Used image export because this view is not the active view in Revit (screen capture needs it open and current).";
            else
            {
                try
                {
                    var uiActive = app.ActiveUIDocument.GetOpenUIViews().First(u => u.ViewId == viewId);
                    var grab = ScreenGrab.Capture(app.MainWindowHandle, uiActive.GetWindowRectangle(), maxWidth);
                    return new ToolResult($"Screen image of view '{doc.GetElement(viewId)?.Name}' as Revit shows it now (source=screen, chosen because it is the active view; " +
                                          "pass source=export for a print-like image)." + patternNotes, PngImage: grab);
                }
                // The caller didn't ask for the screen, so any capture failure (Win32, GDI+, an odd window state) falls back to export.
                catch (Exception ex) when (ex is not OperationCanceledException) { how = $"Used image export because the screen capture failed: {ex.Message}"; }
            }
        }
        else how = source == "export" ? "source=export as asked." : "";
        if (source == "screen")
        {
            if (args.LongList("element_ids").Count > 0 || args.Has("region"))
                return ToolResult.Error("source=screen shows the view as it is on screen; zoom in Revit, or use source=export with element_ids or region.");
            var uiView = app.ActiveUIDocument.GetOpenUIViews().FirstOrDefault(u => u.ViewId == viewId);
            if (uiView is null || doc.ActiveView.Id != viewId)
                return ToolResult.Error($"'{doc.GetElement(viewId)?.Name}' is not the active view in Revit. Open it (make it the current tab), then capture again, or use source=export.");
            var shot = ScreenGrab.Capture(app.MainWindowHandle, uiView.GetWindowRectangle(), maxWidth);
            return new ToolResult($"Screen image of view '{doc.GetElement(viewId)?.Name}' as Revit shows it now (source=screen as asked)." + patternNotes, PngImage: shot);
        }
        var dir = Path.Combine(Path.GetTempPath(), "SketchProMax", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        var options = new ImageExportOptions
        {
            ExportRange = ExportRange.SetOfViews,
            FilePath = Path.Combine(dir, "view"),
            ZoomType = ZoomFitType.FitToPage,
            PixelSize = maxWidth,
            HLRandWFViewsFileType = ImageFileType.PNG,
            ShadowViewsFileType = ImageFileType.PNG,
            ImageResolution = (args.OptLong("resolution_dpi") ?? 150) switch
            {
                72 => ImageResolution.DPI_72,
                300 => ImageResolution.DPI_300,
                600 => ImageResolution.DPI_600,
                _ => ImageResolution.DPI_150,
            },
        };
        options.SetViewsAndSheets([viewId]);

        // Sheet region: print the whole sheet at a resolution that makes the region about pixel_width wide, then cut it out.
        var regionArgs = args.Has("region") ? args.Array("region").Select(e => e.GetDouble()).ToList() : null;
        LayoutMath.Rect? page = null, region = null;
        if (regionArgs is not null)
        {
            if (doc.GetElement(viewId) is not ViewSheet sheet) return ToolResult.Error("region works on sheets; for a plan, use element_ids (or capture the sheet it is on).");
            if (regionArgs.Count != 4) return ToolResult.Error("region needs four numbers [x_min, y_min, x_max, y_max] in sheet coordinates.");
            var o = sheet.Outline;
            page = new LayoutMath.Rect(o.Min.U, o.Min.V, o.Max.U, o.Max.V);
            double r0 = RevitHelpers.ToInternal(doc, regionArgs[0]), r1 = RevitHelpers.ToInternal(doc, regionArgs[1]);
            double r2 = RevitHelpers.ToInternal(doc, regionArgs[2]), r3 = RevitHelpers.ToInternal(doc, regionArgs[3]);
            region = new LayoutMath.Rect(Math.Min(r0, r2), Math.Min(r1, r3), Math.Max(r0, r2), Math.Max(r1, r3));
            if (page.Value.W <= 0 || region.Value.W <= 0 || region.Value.H <= 0 || !region.Value.Overlaps(page.Value))
                return ToolResult.Error($"region is outside the sheet; the sheet spans x {RevitHelpers.FromInternal(doc, page.Value.X0)}..{RevitHelpers.FromInternal(doc, page.Value.X1)}, " +
                                        $"y {RevitHelpers.FromInternal(doc, page.Value.Y0)}..{RevitHelpers.FromInternal(doc, page.Value.Y1)}.");
            var (dpi, pixels) = LayoutMath.ExportSize(page.Value.W * 304.8, region.Value.W * 304.8, maxWidth, MaxSheetPixels);
            options.PixelSize = pixels;
            options.ImageResolution = Resolution(dpi);
            how += $" Exported the sheet {pixels} px wide at {dpi} dpi and cut out the region.";
        }

        // Zooming crops the view for the export only: the crop is committed inside a group that is then rolled back.
        // Plans, and sections, elevations and detail views (ViewSection): both crop with a box in the view's own frame.
        View? plan = null;
        List<BoundingBoxXYZ> boxes = [];
        double margin = 0;
        var ids = args.LongList("element_ids");
        if (ids.Count > 0)
        {
            plan = doc.GetElement(viewId) is View { } v and (ViewPlan or ViewSection) ? v : null;
            if (plan is null) return ToolResult.Error("element_ids zoom works on plans, sections, elevations and detail views; capture the whole view (or the sheet with region) instead.");
            // An element's model box, else its box in this view (annotation and detail items have only that one).
            boxes = ids.Select(i => doc.GetElement(new ElementId(i)) is { } e ? e.get_BoundingBox(null) ?? e.get_BoundingBox(plan) : null).OfType<BoundingBoxXYZ>().ToList();
            if (boxes.Count == 0) return ToolResult.Error("None of those elements have a location to zoom to in this view (they may be in another view, or deleted).");
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
                // Keep the pixel width at the export resolution's density for this crop on paper. A much larger image of a
                // small crop is drawn at a zoom Revit's text placement doesn't follow, which shifts tags and text off their lines.
                var paperMm = (corners.Max(p => p.X) - corners.Min(p => p.X) + 2 * margin) * 304.8 / Math.Max(1, plan.Scale);
                var (dpi, pixels) = args.OptLong("resolution_dpi") is { } asked
                    ? ((int)asked, (int)Math.Max(64, Math.Min(maxWidth, Math.Round(paperMm / 25.4 * asked))))
                    : LayoutMath.ExportSize(paperMm, paperMm, maxWidth, maxWidth);
                options.PixelSize = pixels;
                options.ImageResolution = Resolution(dpi);
                if (pixels < maxWidth * 0.8)
                    how += $" The zoomed area is {paperMm:0} mm wide on paper, so it was exported {pixels} px wide at {dpi} dpi to keep text in place; " +
                           "use a larger margin for a wider image, or capture the sheet with region for full-resolution print.";
            }
            doc.ExportImage(options);
        }
        finally { if (zoom != null) { if (zoom.HasStarted() && !zoom.HasEnded()) zoom.RollBack(); zoom.Dispose(); } }

        var file = Directory.GetFiles(dir, "*.png").FirstOrDefault();
        if (file is null) return ToolResult.Error("Revit did not produce an image for that view.");
        var bytes = File.ReadAllBytes(file);
        try { Directory.Delete(dir, true); } catch (IOException) { }
        if (page is { } pg && region is { } rg) bytes = CropPng(bytes, pg, rg, maxWidth, ref how);

        return new ToolResult($"Image of view '{doc.GetElement(viewId)?.Name}' (Revit image export). {how}" + patternNotes, PngImage: bytes);
    }

    /// <summary>Widest sheet export for a region capture; an A0 sheet at this width is about 170 dpi.</summary>
    private const int MaxSheetPixels = 8000;

    private static ImageResolution Resolution(long dpi) => dpi switch
    {
        <= 72 => ImageResolution.DPI_72,
        <= 150 => ImageResolution.DPI_150,
        <= 300 => ImageResolution.DPI_300,
        _ => ImageResolution.DPI_600,
    };

    /// <summary>Cuts a sheet region out of the exported page image and scales it to at most maxWidth.</summary>
    private static byte[] CropPng(byte[] png, LayoutMath.Rect page, LayoutMath.Rect region, int maxWidth, ref string how)
    {
        using var input = new MemoryStream(png);
        var frame = new System.Windows.Media.Imaging.PngBitmapDecoder(input, System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0];
        int w = frame.PixelWidth, h = frame.PixelHeight;
        // The export fits the sheet's outline to the image; a different shape means the image holds more than the outline.
        if (Math.Abs(w / (double)h - page.W / page.H) > 0.02 * (page.W / page.H))
            how += " Note: the exported image's shape differs from the sheet outline, so the cut-out may be offset a little.";
        var (x, y, cw, ch) = LayoutMath.PixelRegion(page, region, w, h);
        System.Windows.Media.Imaging.BitmapSource image = new System.Windows.Media.Imaging.CroppedBitmap(frame, new System.Windows.Int32Rect(x, y, cw, ch));
        if (cw > maxWidth)
        {
            var k = maxWidth / (double)cw;
            image = new System.Windows.Media.Imaging.TransformedBitmap(image, new System.Windows.Media.ScaleTransform(k, k));
        }
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    /// <summary>
    /// Overrides that set a cut pattern but no surface pattern: those elements look two-toned wherever they are only
    /// partly cut (sloped or stepped elements), on screen and in export alike. Cheap: filters, model categories, and
    /// the given elements only.
    /// </summary>
    private static string CutPatternNotes(Document doc, View? view, List<long> elementIds)
    {
        if (view is null || view is ViewSheet || view.ViewType is ViewType.Schedule or ViewType.DraftingView or ViewType.ThreeD) return "";
        static bool CutOnly(OverrideGraphicSettings o) =>
            (o.CutForegroundPatternId != ElementId.InvalidElementId || o.CutForegroundPatternColor.IsValid)
            && o.SurfaceForegroundPatternId == ElementId.InvalidElementId && !o.SurfaceForegroundPatternColor.IsValid;
        var hits = new List<string>();
        try
        {
            var owner = view.ViewTemplateId != ElementId.InvalidElementId && doc.GetElement(view.ViewTemplateId) is View tpl ? tpl : view;
            foreach (var f in owner.GetFilters())
                if (owner.GetFilterVisibility(f) && CutOnly(owner.GetFilterOverrides(f))) hits.Add($"filter '{doc.GetElement(f)?.Name}'");
            foreach (Category c in doc.Settings.Categories)
                if (c.CategoryType == CategoryType.Model && c.get_AllowsVisibilityControl(owner) && CutOnly(owner.GetCategoryOverrides(c.Id)))
                    hits.Add($"category '{c.Name}'");
            foreach (var i in elementIds.Take(200))
                if (CutOnly(view.GetElementOverrides(new ElementId(i)))) hits.Add($"element {i}");
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { return ""; }
        if (hits.Count == 0) return "";
        return $"\nNote: {string.Join(", ", hits.Take(10))}{(hits.Count > 10 ? $" and {hits.Count - 10} more" : "")} override only the CUT pattern. " +
               "Revit fills with it only where the cut plane passes through the element; the parts seen beyond the cut plane " +
               "(a sloped cover below the plane, the far end of a stepped slab) use the surface pattern, which falls back to the " +
               "material's. That is how Revit draws the view, not an export fault; add a matching Surface (projection) pattern " +
               "override to show the whole element in the override. Compare with source=screen if in doubt.";
    }
}

/// <summary>
/// Grabs the pixels of an open Revit view window. PrintWindow with PW_RENDERFULLCONTENT renders the Revit main window
/// (DirectX view canvases included) even when other windows cover it; the view's rectangle is then cut out.
/// </summary>
internal static class ScreenGrab
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out Rect r);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetWindowDC(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);

    private struct Rect { public int Left, Top, Right, Bottom; }
    private const uint PwRenderFullContent = 2;

    public static byte[] Capture(IntPtr main, Autodesk.Revit.DB.Rectangle view, int maxWidth)
    {
        if (main == IntPtr.Zero) throw new InvalidOperationException("Revit's main window was not found; use source=export.");
        if (IsIconic(main)) throw new InvalidOperationException("Revit is minimized; restore its window, or use source=export.");
        if (!GetWindowRect(main, out var w)) throw new InvalidOperationException("Could not read Revit's window position; use source=export.");
        int width = w.Right - w.Left, height = w.Bottom - w.Top;
        int x = Math.Max(0, view.Left - w.Left), y = Math.Max(0, view.Top - w.Top);
        int cw = Math.Min(width - x, view.Right - view.Left), ch = Math.Min(height - y, view.Bottom - view.Top);
        if (cw < 50 || ch < 50) throw new InvalidOperationException("The view window is too small or off-screen to capture; use source=export.");
        return Grab(main, width, height, x, y, cw, ch, maxWidth, new System.Windows.Media.Imaging.PngBitmapEncoder());
    }

    /// <summary>All of Revit's main window as a JPEG at most <paramref name="maxWidth"/> wide (bug reports).</summary>
    public static byte[] Window(IntPtr main, int maxWidth)
    {
        if (main == IntPtr.Zero) throw new InvalidOperationException("Revit's main window was not found.");
        if (IsIconic(main)) throw new InvalidOperationException("Revit is minimized.");
        if (!GetWindowRect(main, out var w)) throw new InvalidOperationException("Could not read Revit's window position.");
        int width = w.Right - w.Left, height = w.Bottom - w.Top;
        if (width < 50 || height < 50) throw new InvalidOperationException("Revit's window is too small to capture.");
        return Grab(main, width, height, 0, 0, width, height, maxWidth, new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 70 });
    }

    private static byte[] Grab(IntPtr main, int width, int height, int x, int y, int cw, int ch, int maxWidth, System.Windows.Media.Imaging.BitmapEncoder encoder)
    {
        var windowDc = GetWindowDC(main);
        var memDc = CreateCompatibleDC(windowDc);
        var bitmap = CreateCompatibleBitmap(windowDc, width, height);
        var old = SelectObject(memDc, bitmap);
        try
        {
            if (!PrintWindow(main, memDc, PwRenderFullContent))
                throw new InvalidOperationException("Windows would not render Revit's window; use source=export.");
            SelectObject(memDc, old);
            System.Windows.Media.Imaging.BitmapSource image = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                bitmap, IntPtr.Zero, new System.Windows.Int32Rect(x, y, cw, ch),
                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            if (cw > maxWidth)
            {
                var k = maxWidth / (double)cw;
                image = new System.Windows.Media.Imaging.TransformedBitmap(image, new System.Windows.Media.ScaleTransform(k, k));
            }
            if (encoder is System.Windows.Media.Imaging.JpegBitmapEncoder)
                image = new System.Windows.Media.Imaging.FormatConvertedBitmap(image, System.Windows.Media.PixelFormats.Bgr24, null, 0);
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(memDc);
            ReleaseDC(main, windowDc);
        }
    }
}
