using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

namespace SketchProMax.Tools;

/// <summary>
/// read_drawing: a page of a drawing (PDF, scan or picture) as an image to read, the whole page with a labelled grid,
/// then any part of it zoomed in until its dimensions, levels and notes can be read. Drawn by the add-in's own PDF engine
/// (PDFium) in a second or two, even for large scanned as-built sets, where a whole page through Revit took minutes and
/// came out too small to read (the 2026-10-30 Apapa as-built read: every try stopped after 10 minutes, so the existing
/// model was guessed). Reads only; Revit's model is not touched.
/// </summary>
public sealed class ReadDrawingTool : IRevitTool
{
    public string Name => "read_drawing";
    public string Description =>
        "Read a drawing page (PDF, scanned as-built set, or a picture) as an image, and zoom into any part of it. First call " +
        "with file and page only: the whole page with a lettered grid (columns A, B, C... left to right, rows 1, 2, 3... top " +
        "to bottom) and the page count. Then zoom with cell (\"C3\", or a block \"B2:D4\") or region [left, top, right, bottom] " +
        "as fractions of the page, until every dimension, level, note and title can be read; zoom again inside a zoom with " +
        "within. Use it to model from drawings: read each section, its title, scale and datum note, and write down what you " +
        "read with where (page and cell). Fast for big scans; reads only.";
    public string PropertiesJson => """
    {
      "file": {"type": "string", "description": "The drawing: a PDF, or a picture (PNG, JPG, TIF, BMP)."},
      "page": {"type": "integer", "description": "PDF page, from 1 (default 1)."},
      "cell": {"type": "string", "description": "Grid cell of the whole-page view to zoom into: \"C3\", or a block \"B2:D4\"."},
      "region": {"type": "array", "items": {"type": "number"}, "description": "[left, top, right, bottom] as fractions of the page (0 to 1) from its top left."},
      "within": {"type": "array", "items": {"type": "number"}, "description": "The region of an earlier zoom (as its result gave it): cell or region are then read inside that zoom."},
      "pixel_width": {"type": "integer", "description": "Longest side of the picture, 800 to 2400 pixels (default 2000)."},
      "grid": {"type": "boolean", "description": "Lay the lettered grid over the picture (default: on for the whole page, off for a zoom)."}
    }
    """;
    public string[] Required => ["file"];
    public bool ModifiesModel => false;

    private static readonly string[] Pictures = [".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp", ".gif"];

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var file = (args.OptString("file") ?? "").Trim().Trim('"');
        if (file.Length == 0) return ToolResult.Error("Give file: the drawing PDF or picture.");
        if (!File.Exists(file)) return ToolResult.Error($"{file} was not found.");
        var ext = Path.GetExtension(file).ToLowerInvariant();
        var isPdf = ext == ".pdf";
        if (!isPdf && !Pictures.Contains(ext)) return ToolResult.Error($"{Path.GetFileName(file)} is not a PDF or a picture. For DWG or DXF use list_cad_layers or import_cad.");
        var page = (int)(args.OptLong("page") ?? 1);
        if (page < 1) return ToolResult.Error("page counts from 1.");
        var max = (int)Math.Min(2400, Math.Max(800, args.OptLong("pixel_width") ?? 2000));

        // The page size first: the grid and the zoom depend on it.
        double pageW, pageH; int pages;
        BitmapSource? picture = null;
        if (isPdf)
        {
            try
            {
                (pageW, pageH, pages) = PdfEngine.Run(() =>
                {
                    using var d = PdfEngine.Doc.Open(file);
                    if (page > d.Pages) return (0.0, 0.0, d.Pages);
                    var (w, h) = d.Size(page - 1);
                    return (w, h, d.Pages);
                });
            }
            catch (PdfEngine.OpenException ex) { return ToolResult.Error(ex.Message); }
            if (pageW <= 0) return ToolResult.Error($"{Path.GetFileName(file)} has {pages} page(s); page {page} is past the end.");
        }
        else
        {
            if (page != 1) return ToolResult.Error("A picture has one page; leave page out.");
            picture = Load(file);
            (pageW, pageH, pages) = (picture.PixelWidth, picture.PixelHeight, 1);
        }

        var (cols, rows) = AsBuiltMath.GridFor(pageW, pageH);
        var outer = AsBuiltMath.Region.Whole;
        if (args.Has("within"))
        {
            var w = AsBuiltMath.Fractions(args.Raw["within"].EnumerateArray().Select(x => x.GetDouble()).ToList(), out var why);
            if (w is null) return ToolResult.Error("within: " + why);
            outer = w.Value;
        }
        var region = outer;
        var zoomed = false;
        if (args.OptString("cell") is { Length: > 0 } cell)
        {
            var r = AsBuiltMath.CellRegion(cell, cols, rows, out var why);
            if (r is null) return ToolResult.Error(why);
            region = AsBuiltMath.Within(outer, r.Value); zoomed = true;
        }
        else if (args.Has("region"))
        {
            var r = AsBuiltMath.Fractions(args.Raw["region"].EnumerateArray().Select(x => x.GetDouble()).ToList(), out var why);
            if (r is null) return ToolResult.Error(why);
            region = AsBuiltMath.Within(outer, r.Value); zoomed = true;
        }
        zoomed |= args.Has("within");
        var grid = args.OptBool("grid", !zoomed);

        var z = AsBuiltMath.Zoom(pageW, pageH, region, max);
        BitmapSource image;
        if (isPdf)
        {
            PdfEngine.Picture pic;
            try
            {
                pic = PdfEngine.Run(() =>
                {
                    using var d = PdfEngine.Doc.Open(file);
                    return PdfEngine.DrawPart(d, page - 1, z.FullWidth, z.FullHeight, z.OffsetX, z.OffsetY, z.Width, z.Height);
                });
            }
            catch (PdfEngine.OpenException ex) { return ToolResult.Error(ex.Message); }
            image = BitmapSource.Create(pic.Width, pic.Height, 96, 96, PixelFormats.Bgr32, null, pic.Pixels, pic.Stride);
        }
        else
        {
            var p = picture!;
            int x = (int)Math.Floor(region.Left * p.PixelWidth), y = (int)Math.Floor(region.Top * p.PixelHeight);
            int w = Math.Max(1, Math.Min(p.PixelWidth - x, (int)Math.Ceiling(region.W * p.PixelWidth)));
            int h = Math.Max(1, Math.Min(p.PixelHeight - y, (int)Math.Ceiling(region.H * p.PixelHeight)));
            BitmapSource part = new CroppedBitmap(p, new Int32Rect(x, y, w, h));
            var k = max / (double)Math.Max(w, h);
            if (Math.Abs(k - 1) > 0.01) part = new TransformedBitmap(part, new ScaleTransform(k, k));
            image = part;
        }
        if (grid) image = WithGrid(image, cols, rows);

        var png = Encode(image);
        var widthMm = pageW / 72 * 25.4; var heightMm = pageH / 72 * 25.4;
        var text = JsonSerializer.Serialize(new
        {
            file, page, pages,
            pageSizeMm = isPdf ? new[] { Math.Round(widthMm), Math.Round(heightMm) } : null,
            region = new[] { Math.Round(region.Left, 4), Math.Round(region.Top, 4), Math.Round(region.Right, 4), Math.Round(region.Bottom, 4) },
            grid = grid ? $"{cols} columns A to {AsBuiltMath.Column(cols - 1)}, {rows} rows 1 to {rows}" : null,
            pixels = new[] { image.PixelWidth, image.PixelHeight },
            dpiOnPaper = isPdf ? Math.Round(z.Dpi) : (double?)null,
            next = zoomed
                ? "Read what this part shows. If text is still too small, zoom again with within = this region and a cell or region inside it. " +
                  "Write down each value with page and region, and mark it as read from the drawing."
                : $"Zoom into each part with cell (A1 to {AsBuiltMath.Column(cols - 1)}{rows}) until dimensions and levels can be read." +
                  (pages > page ? $" This file has {pages} pages: look at every page that may hold a section, plan or note for the work." : ""),
        });
        return new ToolResult(text, PngImage: png);
    }

    private static BitmapSource Load(string file)
    {
        using var s = File.OpenRead(file);
        var frame = BitmapDecoder.Create(s, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        frame.Freeze();
        return frame;
    }

    /// <summary>The picture with the lettered grid drawn over it: thin red lines and a label in each cell's top left corner.</summary>
    private static BitmapSource WithGrid(BitmapSource image, int cols, int rows)
    {
        int w = image.PixelWidth, h = image.PixelHeight;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(image, new Rect(0, 0, w, h));
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(170, 220, 0, 0)), Math.Max(1, w / 1200.0));
            for (var c = 1; c < cols; c++) dc.DrawLine(pen, new Point(w * c / (double)cols, 0), new Point(w * c / (double)cols, h));
            for (var r = 1; r < rows; r++) dc.DrawLine(pen, new Point(0, h * r / (double)rows), new Point(w, h * r / (double)rows));
            var size = Math.Max(12, Math.Min(w / (double)cols, h / (double)rows) / 7);
            var face = new Typeface("Segoe UI");
            var back = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255));
            var ink = new SolidColorBrush(Color.FromRgb(200, 0, 0));
            for (var c = 0; c < cols; c++)
                for (var r = 0; r < rows; r++)
                {
                    var label = new FormattedText(AsBuiltMath.Column(c) + (r + 1).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, face, size, ink, 1.0);
                    var at = new Point(w * c / (double)cols + 3, h * r / (double)rows + 2);
                    dc.DrawRectangle(back, null, new Rect(at, new Size(label.Width + 4, label.Height)));
                    dc.DrawText(label, new Point(at.X + 2, at.Y));
                }
        }
        var target = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        return target;
    }

    private static byte[] Encode(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }
}
