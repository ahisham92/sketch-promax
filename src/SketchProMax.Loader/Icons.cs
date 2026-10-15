using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SketchProMax.Loader;

/// <summary>
/// Ribbon icons. The main buttons use the product logo (embedded PNGs); every other button gets an icon drawn here at
/// runtime in the logo's palette: a rounded tile with the logo's blue gradient, a white line glyph and an amber accent,
/// so the whole tab reads as one family. Drawn on a 32-unit grid and rendered at 16 and 32 px; nothing binary to ship.
/// </summary>
internal static class Icons
{
    public static readonly Color Blue = Color.FromRgb(0x1D, 0x4E, 0xD8);
    public static readonly Color Navy = Color.FromRgb(0x0B, 0x1E, 0x4F);
    public static readonly Color Amber = Color.FromRgb(0xFB, 0xBF, 0x24);
    public static readonly Color Green = Color.FromRgb(0x22, 0xC5, 0x5E);
    public static readonly Color Red = Color.FromRgb(0xEF, 0x44, 0x44);
    public static readonly Color Gray = Color.FromRgb(0x94, 0xA3, 0xB8);

    private static readonly Dictionary<string, BitmapSource> Cache = new();

    /// <summary>A glyph icon at 16 or 32 px.</summary>
    public static BitmapSource Get(string glyph, int size)
    {
        var key = glyph + "@" + size;
        if (Cache.TryGetValue(key, out var hit)) return hit;
        var image = Render(size, dc =>
        {
            Tile(dc);
            Glyph(dc, glyph, size);
        });
        Cache[key] = image;
        return image;
    }

    /// <summary>The product logo, with an optional status dot in the lower right corner.</summary>
    public static BitmapSource Logo(int size, Color? dot = null)
    {
        var key = "logo@" + size + (dot is { } d ? d.ToString() : "");
        if (Cache.TryGetValue(key, out var hit)) return hit;
        var logo = LoadPng(size) ?? Get("cube", size);
        BitmapSource image = logo;
        if (dot is { } color)
        {
            image = Render(size, dc =>
            {
                dc.DrawImage(logo, new Rect(0, 0, 32, 32));
                // A white ring keeps the dot readable on the blue logo.
                dc.DrawEllipse(Brush(Colors.White), null, new Point(25, 25), 7, 7);
                dc.DrawEllipse(Brush(color), null, new Point(25, 25), 5, 5);
            });
        }
        Cache[key] = image;
        return image;
    }

    private static BitmapSource? LoadPng(int size)
    {
        var name = $"SketchProMax.Loader.logo-{(size <= 16 ? 16 : size <= 32 ? 32 : 64)}.png";
        try
        {
            using var stream = typeof(Icons).Assembly.GetManifestResourceStream(name);
            if (stream is null) return null;
            var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapSource frame = decoder.Frames[0];
            if (frame.PixelWidth != size)
                frame = new TransformedBitmap(frame, new ScaleTransform((double)size / frame.PixelWidth, (double)size / frame.PixelHeight));
            // Ribbon images look blurry unless they are 96 DPI.
            var normalized = Render(size, dc => dc.DrawImage(frame, new Rect(0, 0, 32, 32)));
            return normalized;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or InvalidOperationException) { return null; }
    }

    private static BitmapSource Render(int size, Action<DrawingContext> paint)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size / 32.0, size / 32.0));
            paint(dc);
            dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static void Tile(DrawingContext dc)
    {
        var gradient = new LinearGradientBrush(Blue, Navy, new Point(0, 0), new Point(1, 1));
        gradient.Freeze();
        dc.DrawRoundedRectangle(gradient, null, new Rect(1, 1, 30, 30), 7, 7);
    }

    private static Pen Stroke(Color c, double width)
    {
        var pen = new Pen(Brush(c), width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return pen;
    }

    private static void Path(DrawingContext dc, string data, Pen? pen, Brush? fill = null) =>
        dc.DrawGeometry(fill, pen, Geometry.Parse(data));

    /// <summary>White line glyphs, a little bolder at 16 px so they survive the small size.</summary>
    private static void Glyph(DrawingContext dc, string glyph, int size)
    {
        var w = size <= 16 ? 3.0 : 2.4;
        var white = Stroke(Colors.White, w);
        var amber = Stroke(Amber, w);
        var whiteFill = Brush(Colors.White);
        var amberFill = Brush(Amber);
        switch (glyph)
        {
            case "chat": // speech bubble with a spark
                Path(dc, "M8,9 H24 A2,2 0 0 1 26,11 V19 A2,2 0 0 1 24,21 H15 L10,25 V21 H8 A2,2 0 0 1 6,19 V11 A2,2 0 0 1 8,9 Z", white);
                dc.DrawEllipse(amberFill, null, new Point(16, 15), 2.2, 2.2);
                break;
            case "copy": // two sheets, the front one with text lines
                Path(dc, "M11,7 H22 V10", white);
                Path(dc, "M8,10 H19 A1,1 0 0 1 20,11 V25 A1,1 0 0 1 19,26 H8 A1,1 0 0 1 7,25 V11 A1,1 0 0 1 8,10 Z", white);
                Path(dc, "M10.5,15 H16.5 M10.5,19 H16.5", amber);
                Path(dc, "M22,10 H24 A1,1 0 0 1 25,11 V22", white);
                break;
            case "open": // window with an arrow leaving it
                Path(dc, "M15,8 H9 A2,2 0 0 0 7,10 V23 A2,2 0 0 0 9,25 H22 A2,2 0 0 0 24,23 V17", white);
                Path(dc, "M18,7 H25 V14 M25,7 L15,17", amber);
                break;
            case "job": // clock
                dc.DrawEllipse(null, white, new Point(16, 16), 9, 9);
                Path(dc, "M16,11 V16 L20,18", amber);
                break;
            case "job-running": // clock with a moving arc
                dc.DrawEllipse(null, white, new Point(16, 16), 9, 9);
                Path(dc, "M16,7 A9,9 0 0 1 25,16", Stroke(Amber, w + 0.8));
                Path(dc, "M16,11 V16 L20,18", white);
                break;
            case "cancel": // stop: X in a circle
                dc.DrawEllipse(null, white, new Point(16, 16), 9, 9);
                Path(dc, "M12.5,12.5 L19.5,19.5 M19.5,12.5 L12.5,19.5", amber);
                break;
            case "bolt": // quick tools
                Path(dc, "M18,5 L9,18 H15 L13,27 L23,13 H17 Z", null, amberFill);
                break;
            case "refresh":
                Path(dc, "M23,12 A8,8 0 1 0 24,18", white);
                Path(dc, "M24,7 V13 H18", amber);
                break;
            case "folder":
                Path(dc, "M6,11 V23 A1,1 0 0 0 7,24 H25 A1,1 0 0 0 26,23 V13 A1,1 0 0 0 25,12 H15 L13,9 H7 A1,1 0 0 0 6,10 Z", white);
                Path(dc, "M6,15 H26", amber);
                break;
            case "recipe": // checklist
                Path(dc, "M14,10 H24 M14,16 H24 M14,22 H24", white);
                Path(dc, "M7,10 L8.5,11.5 L11,8.5 M7,16 L8.5,17.5 L11,14.5 M7,22 L8.5,23.5 L11,20.5", amber);
                break;
            case "learned": // code brackets with a spark
                Path(dc, "M11,10 L6,16 L11,22 M21,10 L26,16 L21,22", white);
                Path(dc, "M18,9 L14,23", amber);
                break;
            case "check": // shield with a tick
                Path(dc, "M16,5 L25,8.5 V15 C25,21 21,25 16,27 C11,25 7,21 7,15 V8.5 Z", white);
                Path(dc, "M11.5,16 L15,19.5 L21,12.5", amber);
                break;
            case "test": // flask
                Path(dc, "M13,6 V13 L7,24 A1.5,1.5 0 0 0 8.3,26 H23.7 A1.5,1.5 0 0 0 25,24 L19,13 V6 M11.5,6 H20.5", white);
                Path(dc, "M10,20 H22 L24,24.5 H8 Z", null, amberFill);
                break;
            case "version": // tag with a dot
                Path(dc, "M7,8 V15 L17,25 L25,17 L15,7 H8 Z", white);
                dc.DrawEllipse(amberFill, null, new Point(11.5, 11.5), 2, 2);
                break;
            case "update": // circular arrows
                Path(dc, "M8,15 A8,8 0 0 1 22,10", white);
                Path(dc, "M24,17 A8,8 0 0 1 10,22", white);
                Path(dc, "M22,5.5 V10.5 H17", amber);
                Path(dc, "M10,26.5 V21.5 H15", amber);
                break;
            case "download": // arrow into a tray
                Path(dc, "M16,6 V19 M11,14 L16,19 L21,14", amber);
                Path(dc, "M7,20 V24 A1,1 0 0 0 8,25 H24 A1,1 0 0 0 25,24 V20", white);
                break;
            case "news": // spark
                Path(dc, "M16,5 L18.5,13.5 L27,16 L18.5,18.5 L16,27 L13.5,18.5 L5,16 L13.5,13.5 Z", null, amberFill);
                dc.DrawEllipse(whiteFill, null, new Point(24.5, 8), 1.8, 1.8);
                break;
            case "settings": // gear
            {
                var teeth = new GeometryGroup();
                for (var i = 0; i < 8; i++)
                {
                    var r = new RectangleGeometry(new Rect(14.2, 4.5, 3.6, 5), 0.8, 0.8)
                    { Transform = new RotateTransform(i * 45, 16, 16) };
                    teeth.Children.Add(r);
                }
                teeth.Children.Add(new EllipseGeometry(new Point(16, 16), 8.5, 8.5));
                dc.DrawGeometry(whiteFill, null, teeth);
                dc.DrawEllipse(Brush(Navy), null, new Point(16, 16), 4.5, 4.5);
                dc.DrawEllipse(amberFill, null, new Point(16, 16), 2, 2);
                break;
            }
            case "pane": // window with a docked side panel listing steps
                Path(dc, "M8,7 H24 A2,2 0 0 1 26,9 V23 A2,2 0 0 1 24,25 H8 A2,2 0 0 1 6,23 V9 A2,2 0 0 1 8,7 Z", white);
                Path(dc, "M18,7 V25", white);
                Path(dc, "M20.5,12 H23.5 M20.5,16 H23.5 M20.5,20 H23.5", amber);
                break;
            case "field": // clipboard with a tick
                Path(dc, "M12,8 H9 A1,1 0 0 0 8,9 V26 A1,1 0 0 0 9,27 H23 A1,1 0 0 0 24,26 V9 A1,1 0 0 0 23,8 H20", white);
                Path(dc, "M12.5,5.5 H19.5 V10 H12.5 Z", white);
                Path(dc, "M11.5,18 L15,21.5 L21,14.5", amber);
                break;
            case "help":
                Path(dc, "M11.5,12 A4.5,4.5 0 1 1 18,16 C16.5,17 16,18 16,20", white);
                dc.DrawEllipse(amberFill, null, new Point(16, 24.5), 2, 2);
                break;
            case "cube": // fallback logo
            default:
                Path(dc, "M16,6 L25,11 V21 L16,26 L7,21 V11 Z M7,11 L16,16 L25,11 M16,16 V26", white);
                dc.DrawEllipse(amberFill, null, new Point(24, 7), 2.4, 2.4);
                break;
        }
    }
}
