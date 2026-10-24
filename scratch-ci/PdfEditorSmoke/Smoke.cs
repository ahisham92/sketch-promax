using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SketchProMax.UI;

namespace SketchProMax;

internal static class Smoke
{
    public static bool Dark;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;

    [STAThread]
    public static int Main(string[] args)
    {
        var folder = Path.Combine(Path.GetTempPath(), "pdf-smoke");
        Directory.CreateDirectory(folder);
        var problems = new List<string>();
        Tools.PdfEditCheck.Run(Path.Combine(folder, "check"), (n, ok, d) => { Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {n} {d}"); if (!ok) problems.Add(n); });
        var big = Path.Combine(folder, "Big drawing set.pdf");
        var bytes = Tools.PdfEditCheck.Labeled("A", 300);
        File.WriteAllBytes(big, bytes);
        var pics = Path.Combine(folder, "Pictures.pdf");
        File.WriteAllBytes(pics, Tools.PdfFiles.Sample(6, noise: 200));

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) => { Console.WriteLine("UNHANDLED " + e.Exception); problems.Add("unhandled: " + e.Exception.Message); e.Handled = true; };
        app.Startup += async (_, _) =>
        {
            try
            {
                var w = (PdfEditorWindow)PdfEditorWindow.ForTest();
                w.Show();
                Shot(w, "empty-light");
                var open = (Task)typeof(PdfEditorWindow).GetMethod("OpenFiles", Any)!.Invoke(w, [new List<string> { big }, true])!;
                await open;
                await Wait(2500);
                Shot(w, "opened-light");
                var grid = (ListBox)typeof(PdfEditorWindow).GetField("_grid", Any)!.GetValue(w)!;
                Console.WriteLine($"items: {grid.Items.Count}");
                if (grid.Items.Count != 300) problems.Add("items " + grid.Items.Count);
                var pick = grid.GetType().GetMethod("Pick", Any)!;
                pick.Invoke(grid, [new[] { (ListBoxItem)grid.Items[1], (ListBoxItem)grid.Items[2], (ListBoxItem)grid.Items[3] }]);
                await Wait(300);
                Shot(w, "picked-light");
                typeof(PdfEditorWindow).GetMethod("Rotate", Any)!.Invoke(w, [1]);
                await Wait(1200);
                Shot(w, "turned-light");
                typeof(PdfEditorWindow).GetMethod("DeletePicked", Any)!.Invoke(w, []);
                await Wait(800);
                if (grid.Items.Count != 297) problems.Add("after delete " + grid.Items.Count);
                typeof(PdfEditorWindow).GetMethod("Undo", Any)!.Invoke(w, []);
                await Wait(800);
                if (grid.Items.Count != 300) problems.Add("after undo " + grid.Items.Count);
                // Merge the picture PDF in after page 1.
                pick.Invoke(grid, [new[] { (ListBoxItem)grid.Items[0] }]);
                await (Task)typeof(PdfEditorWindow).GetMethod("AddFiles", Any)!.Invoke(w, [new List<string> { pics }, 1])!;
                await Wait(2500);
                Shot(w, "merged-light");
                // Scroll to the end: pictures there are drawn too.
                var scroller = (ScrollViewer?)typeof(PdfEditorWindow).GetField("_scroller", Any)!.GetValue(w);
                scroller?.ScrollToEnd();
                await Wait(2500);
                Shot(w, "end-light");
                var zoom = (ComboBox)typeof(PdfEditorWindow).GetField("_zoom", Any)!.GetValue(w)!;
                zoom.SelectedIndex = 2;
                scroller?.ScrollToHome();
                await Wait(2500);
                Shot(w, "large-light");
                Dark = true;
                Brand.ShowTheme(w, true);
                zoom.SelectedIndex = 0;
                await Wait(2500);
                Shot(w, "small-dark");
                Dark = false;
                Brand.ShowTheme(w, false);
                var doc = (Tools.PdfEditSession)typeof(PdfEditorWindow).GetField("_doc", Any)!.GetValue(w)!;
                var preview = new PdfPreviewWindow(w, doc, 2);
                preview.Show();
                await Wait(1500);
                Shot(preview, "preview");
                preview.Close();
                var split = (Window)typeof(SplitChoiceWindow).GetConstructors(Any)[0].Invoke([w, 306, new List<int> { 4, 9 }, @"C:\Projects\Big drawing set parts"]);
                split.Show();
                await Wait(500);
                Shot(split, "split");
                split.Close();
                var pages = (Window)typeof(PagesWindow).GetConstructors(Any)[0].Invoke([w, "Revised sheets.pdf", 12, "1-2", 2]);
                pages.Show();
                await Wait(400);
                Shot(pages, "pages");
                pages.Close();
                // Save over the file, then the window shows the saved file.
                var saved = (Task<bool>)typeof(PdfEditorWindow).GetMethod("SaveDoc", Any)!.Invoke(w, [false])!;
                // The merged doc still has File = big (opened), so Save writes back with a backup.
                Console.WriteLine("saved: " + await saved);
                await Wait(1500);
                Shot(w, "saved-light");
                if (grid.Items.Count != 306) problems.Add("after save " + grid.Items.Count);
                typeof(PdfEditorWindow).GetField("_doc", Any)!.SetValue(w, doc); // keep
                w.Close();
            }
            catch (Exception ex) { Console.WriteLine("FAILED " + ex); problems.Add(ex.Message); }
            Console.WriteLine(problems.Count == 0 ? "SMOKE OK" : "SMOKE PROBLEMS: " + string.Join("; ", problems));
            app.Shutdown(problems.Count == 0 ? 0 : 1);
        };
        return app.Run();
    }

    private static async Task Wait(int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) await Task.Delay(50);
    }

    private static void Shot(Window w, string name)
    {
        w.UpdateLayout();
        var root = (FrameworkElement)w.Content;
        var width = (int)Math.Max(1, root.ActualWidth);
        var height = (int)Math.Max(1, root.ActualHeight);
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var bg = new DrawingVisual();
        using (var dc = bg.RenderOpen()) dc.DrawRectangle(w.Background ?? Brushes.White, null, new Rect(0, 0, width, height));
        rtb.Render(bg);
        rtb.Render(root);
        var enc = new JpegBitmapEncoder { QualityLevel = 70 };
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        enc.Save(ms);
        var dir = Environment.GetEnvironmentVariable("SHOTS") ?? ".";
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name + ".jpg"), ms.ToArray());
        Console.WriteLine($"shot {name} {width}x{height} {ms.Length / 1024} KB");
    }
}
