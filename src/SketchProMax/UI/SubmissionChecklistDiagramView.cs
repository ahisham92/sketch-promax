using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using static SketchProMax.Tools.SubmissionChecklistDiagrams;

namespace SketchProMax.UI;

/// <summary>
/// Draws a submission checklist sketch (Tools/SubmissionChecklistDiagrams) in WPF: on white "paper" in both themes, in
/// the same colours as its SVG, scaled to the width given; and as a PNG for the Word file.
/// </summary>
internal static class SubmissionChecklistDiagramView
{
    public static FrameworkElement View(Diagram d, double width)
    {
        var border = new Border
        {
            Child = new Viewbox { Child = Canvas(d), Stretch = Stretch.Uniform }, Width = width, Height = width * Height / Width,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Background = Brushes.White, Padding = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        border.SetResourceReference(Border.BorderBrushProperty, Brand.K.BorderSubtle);
        System.Windows.Automation.AutomationProperties.SetName(border, d.Title + " sketch");
        border.ToolTip = d.Title + " (typical, not to scale)";
        return border;
    }

    private static Brush B(string hex) => hex == "none" ? Brushes.Transparent : new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

    public static Canvas Canvas(Diagram d)
    {
        var c = new Canvas { Width = Width, Height = Height, Background = Brushes.White, ClipToBounds = true };
        foreach (var s in d.Shapes)
        {
            var (fill, stroke, dash) = Colors(s.Look);
            var p = s.Points;
            UIElement? e = null;
            switch (s.Kind)
            {
                case "poly":
                case "path":
                    var pts = new PointCollection(Enumerable.Range(0, p.Length / 2).Select(i => new Point(p[2 * i], p[2 * i + 1])));
                    e = s.Kind == "poly"
                        ? new Polygon { Points = pts, Fill = B(fill), Stroke = B(stroke), StrokeThickness = 1 }
                        : new Polyline { Points = pts, Stroke = B(stroke), StrokeThickness = 2, StrokeDashArray = dash ? new DoubleCollection(new double[] { 4, 2 }) : null };
                    break;
                case "rect":
                    e = new Rectangle { Width = p[2], Height = p[3], Fill = B(fill), Stroke = B(stroke), StrokeThickness = 1 };
                    System.Windows.Controls.Canvas.SetLeft(e, p[0]);
                    System.Windows.Controls.Canvas.SetTop(e, p[1]);
                    break;
                case "circle":
                    e = new Ellipse { Width = 2 * p[2], Height = 2 * p[2], Fill = B(fill), Stroke = B(stroke) };
                    System.Windows.Controls.Canvas.SetLeft(e, p[0] - p[2]);
                    System.Windows.Controls.Canvas.SetTop(e, p[1] - p[2]);
                    break;
                case "level":
                    c.Children.Add(new Line { X1 = 0, X2 = Width, Y1 = p[0], Y2 = p[0], Stroke = B("#1D4ED8"), StrokeThickness = 0.6, StrokeDashArray = new DoubleCollection(new double[] { 3, 3 }) });
                    e = Label(s.Text, 4, p[0] - 13, "#1D4ED8");
                    break;
                case "text":
                    e = Label(s.Text, p[0], p[1] - 11, "#334155");
                    break;
            }
            if (e is not null) c.Children.Add(e);
        }
        return c;
    }

    private static TextBlock Label(string text, double x, double y, string color)
    {
        var t = new TextBlock { Text = text, FontSize = 10, Foreground = B(color), FontFamily = new FontFamily("Segoe UI") };
        System.Windows.Controls.Canvas.SetLeft(t, x);
        System.Windows.Controls.Canvas.SetTop(t, y);
        return t;
    }

    /// <summary>The sketch as a PNG at <paramref name="scale"/> times its size, twice by default (null when WPF can't draw it here).</summary>
    public static byte[]? Png(Diagram d, double scale = 2)
    {
        try
        {
            // Scaled by a Viewbox, not a transform on the canvas itself, which RenderTargetBitmap would offset.
            var box = new Viewbox { Child = Canvas(d), Width = Width * scale, Height = Height * scale, Stretch = Stretch.Uniform };
            box.Measure(new Size(Width * scale, Height * scale));
            box.Arrange(new Rect(0, 0, Width * scale, Height * scale));
            box.UpdateLayout();
            var bmp = new RenderTargetBitmap((int)(Width * scale), (int)(Height * scale), 96, 96, PixelFormats.Pbgra32);
            bmp.Render(box);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OutOfMemoryException) { return null; }
    }
}
