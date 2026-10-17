using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SketchProMax.UI;

namespace SketchProMax.Chat;

/// <summary>
/// Checks of the chat's look and feel that a person then judges (field test probes chat.dpi and chat.frames): the pane
/// with sample content drawn to PNGs at each Windows scale, and frame times while the drawer opens and a reply streams.
/// Both use a stand-alone pane (the constructor given a sample conversation) that finds, sends and saves nothing.
/// </summary>
internal sealed partial class ChatPane
{
    internal static class Snapshot
    {
        /// <summary>Windows' display scales and their DPI.</summary>
        public static readonly (int Percent, double Dpi)[] Scales = [(100, 96), (125, 120), (150, 144), (175, 168)];

        private const string Reply = """
            ## Walls on Level 1

            Found **12 walls**; three are thicker than `200 mm`:

            | Type | Thickness | Count |
            |:--|--:|--:|
            | Generic - 300 mm | 300 mm | 2 |
            | Exterior - Brick on block | 290 mm | 1 |

            1. Select them with the ids in the step above.
            2. Change their type in Properties.

            ```csharp
            var walls = new FilteredElementCollector(doc)
                .OfClass(typeof(Wall))
                .Where(w => w.Width > 0.656);
            ```

            > Widths are read from each wall's type.
            """;

        /// <summary>A conversation with each kind of card: your message, steps (done, failed), a Markdown answer, a brief.</summary>
        public static Conversation Sample()
        {
            var t = DateTime.UtcNow.AddMinutes(-3);
            var brief = new PromptBrief
            {
                Kind = "change", Request = "Make the thick walls 200 mm",
                Prompt = "Change the 3 walls on Level 1 thicker than 200 mm to {q1}.",
                Assumptions = ["Only walls on Level 1"],
                Questions = [new BriefQuestion { Id = "q1", Text = "Which wall type?", Options = ["Generic - 200 mm", "A new type"], Recommended = 0 }],
            };
            return new Conversation
            {
                Key = "(snapshot)", Title = "Sample model", Name = "Thick walls on Level 1",
                Items =
                [
                    new ChatItem { Kind = "user", Text = "Which walls on Level 1 are thicker than 200 mm?", Time = t },
                    new ChatItem { Kind = "tool", Name = "get_elements", State = "done", Text = "Walls on Level 1", Input = "{\n  \"category\": \"Walls\"\n}", Result = "12 walls: 310452, 310457, 310460", Time = t },
                    new ChatItem { Kind = "tool", Name = "get_parameters", State = "failed", Text = "Width", Result = "The parameter Width was not found on 2 walls.", Time = t },
                    new ChatItem { Kind = "assistant", Text = Reply, Time = t },
                    new ChatItem { Kind = "user", Text = "Make the thick walls 200 mm", Time = t.AddMinutes(1) },
                    new ChatItem { Kind = "brief", State = "pending", Brief = PromptBriefs.ToJson(brief), Text = PromptBriefs.Compose(brief), Time = t.AddMinutes(1) },
                    new ChatItem { Kind = "job", Job = SampleJob(t.AddMinutes(2)).ToJson(), Time = t.AddMinutes(2) },
                ],
            };
        }

        /// <summary>A finished job card: a step done, one skipped, one failed with its reason.</summary>
        private static JobCardModel SampleJob(DateTime started)
        {
            var job = JobCardModel.FromPlan(["change_type", "tag_elements_in_view", "create_schedule"], started);
            job.Steps[0].State = "done";
            job.Steps[0].Done = job.Steps[0].Total = 3;
            job.Steps[1].State = "skipped";
            job.Steps[1].Reason = "skipped by you (Skip step)";
            job.Steps[2].State = "failed";
            job.Steps[2].Reason = "A schedule with this name already exists.";
            job.Think("The three walls share one type, so one change covers them.", newBlock: true);
            job.Finish("done", started.AddSeconds(95), "Done in 1 min 35 s. By hand: about 12 min. Saved: 10 min.", 3);
            return job;
        }

        /// <summary>
        /// Draws the pane (light and dark) at 100, 125, 150 and 175 % to PNGs in <paramref name="folder"/>, as Windows
        /// would lay it out at that scale (the root's DPI is set, so text and borders snap to that DPI's pixels). The only
        /// RenderTargetBitmap in the chat: the live pane never draws to a bitmap. On Revit's UI thread.
        /// </summary>
        public static List<string> Render(string folder, double width = 400, double height = 1200)
        {
            Directory.CreateDirectory(folder);
            var files = new List<string>();
            ChatUi.NoMotion = true; // the settled look, not a frame of a fade
            try
            {
                foreach (var dark in new[] { false, true })
                    foreach (var (percent, dpi) in Scales)
                    {
                        var pane = new ChatPane(Sample());
                        try
                        {
                            Brand.ShowTheme(pane, dark);
                            pane.ShowSampleState();
                            var scale = dpi / 96;
                            VisualTreeHelper.SetRootDpi(pane, new DpiScale(scale, scale));
                            var size = new Size(width, height);
                            pane.Measure(size);
                            pane.Arrange(new Rect(size));
                            pane.UpdateLayout();
                            pane._scroll.ScrollToEnd();
                            pane.UpdateLayout();
                            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), dpi, dpi, PixelFormats.Pbgra32);
                            bitmap.Render(pane);
                            var encoder = new PngBitmapEncoder();
                            encoder.Frames.Add(BitmapFrame.Create(bitmap));
                            var file = Path.Combine(folder, $"chat-{percent}-{(dark ? "dark" : "light")}.png");
                            using (var stream = File.Create(file)) encoder.Save(stream);
                            files.Add(file);
                        }
                        finally { Brand.ReleaseTheme(pane); }
                    }
            }
            finally { ChatUi.NoMotion = false; }
            return files;
        }

        /// <summary>
        /// Frame times in a hidden window (off screen, never activated) holding a stand-alone pane: the history drawer
        /// opens and closes, then a sample reply streams in through the same path as Claude's. On Revit's UI thread; the
        /// task ends on it after about four seconds.
        /// </summary>
        public static Task<(double AverageMs, double WorstMs, int Frames, int Hitches, bool Animated)> Frames()
        {
            var done = new TaskCompletionSource<(double, double, int, int, bool)>();
            var pane = new ChatPane(Sample());
            var window = new Window
            {
                Width = 420, Height = 760, Left = -32000, Top = -32000, WindowStyle = WindowStyle.None, ShowInTaskbar = false,
                ShowActivated = false, ResizeMode = ResizeMode.NoResize, Title = "Sketch ProMax frame test", Content = pane,
            };
            var stamps = new List<TimeSpan>();
            void OnFrame(object? sender, EventArgs e) { if (e is RenderingEventArgs r) stamps.Add(r.RenderingTime); }
            var item = new ChatItem { Kind = "assistant" };
            var words = Reply.Split(' ');
            var shown = 0;
            var tick = 0;
            var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(30) };
            void Finish(Exception? error)
            {
                timer.Stop();
                CompositionTarget.Rendering -= OnFrame;
                pane.StopGlide();
                try { window.Close(); } catch (InvalidOperationException) { }
                Brand.ReleaseTheme(pane);
                if (error is not null) done.TrySetException(error);
                else
                {
                    var f = FrameTimes.Of(stamps);
                    done.TrySetResult((f.AverageMs, f.WorstMs, f.Frames, f.Hitches, ChatUi.Animate));
                }
            }
            timer.Tick += (_, _) =>
            {
                try
                {
                    tick++;
                    if (tick == 3) pane.OpenDrawer();
                    else if (tick == 25) pane.CloseDrawer();
                    else if (tick == 40)
                    {
                        pane.Add(item);
                        pane._texts["frames"] = (item, pane._views[item]);
                    }
                    else if (tick > 40 && shown < words.Length)
                    {
                        // About the pace Claude writes: a few words every 30 ms, drawn by the same throttled path.
                        var take = Math.Min(3, words.Length - shown);
                        item.Text += (shown > 0 ? " " : "") + string.Join(" ", words, shown, take);
                        shown += take;
                        pane._dirty.Add("frames");
                        pane.RequestRender();
                    }
                    else if (tick > 40 && shown >= words.Length && tick > 160) Finish(null);
                }
                catch (Exception ex) { Finish(ex); }
            };
            try
            {
                window.Show();
                CompositionTarget.Rendering += OnFrame;
                timer.Start();
            }
            catch (Exception ex) { Finish(ex); }
            return done.Task;
        }
    }

    /// <summary>The snapshot's extras: a step opened, and the typing line with its shimmer as while Claude starts.</summary>
    private void ShowSampleState()
    {
        if (_rows.Values.FirstOrDefault(r => r.Item.State == "done") is { } row) row.Header.IsChecked = true;
        _workingText.Text = "Claude is thinking…";
        _working.Visibility = Visibility.Visible;
        _shimmer.Visibility = Visibility.Visible;
    }
}
