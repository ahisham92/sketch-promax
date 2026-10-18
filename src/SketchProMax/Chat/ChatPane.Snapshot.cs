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

    /// <summary>
    /// The chat animations (probe chat.motion): the setting's three levels are stored and read back and map to what
    /// moves; then, in a hidden window at Full, a streamed reply's new text settles to the text colour, a finished step's
    /// check pops in and settles with no transform, Send and Stop swap with a pop that settles, the thinking mark
    /// breathes while shown, and the finished glow appears and goes. Your setting is put back afterwards. On Revit's UI
    /// thread; the task ends on it after about three seconds. Returns the problems found (none: it passed) and what ran.
    /// </summary>
    internal static Task<(List<string> Problems, string Ran)> MotionCheck()
    {
        var done = new TaskCompletionSource<(List<string>, string)>();
        var problems = new List<string>();
        foreach (var level in MotionLevels.All)
            if (MotionLevels.Parse(MotionLevels.Store(level)) != level) problems.Add($"the level {level} isn't read back as stored");
        if (MotionLevels.Parse(null) != Motion.Full || MotionLevels.Parse("") != Motion.Full) problems.Add("a settings file without the setting doesn't read as Full");
        var saved = ChatUi.Setting;
        var windows = SystemParameters.ClientAreaAnimation;
        ChatUi.SetMotion(Motion.Off);
        if (ChatUi.Animate || ChatUi.Lively) problems.Add("at Off something still moves");
        ChatUi.SetMotion(Motion.Reduced);
        if (windows && (!ChatUi.Animate || ChatUi.Lively)) problems.Add("Reduced should keep the quick opens and closes only");
        ChatUi.SetMotion(Motion.Full);
        if (windows && !ChatUi.Lively) problems.Add("Full doesn't turn the lively motion on");
        if (!windows && ChatUi.Animate) problems.Add("with Windows' animations off the chat still animates");
        if (!windows)
        {
            ChatUi.SetMotion(saved);
            done.TrySetResult((problems, "the levels were checked; Windows' animations are off, so nothing moves and the live part was skipped"));
            return done.Task;
        }

        var pane = new ChatPane(Snapshot.Sample());
        var window = new Window
        {
            Width = 420, Height = 760, Left = -32000, Top = -32000, WindowStyle = WindowStyle.None, ShowInTaskbar = false,
            ShowActivated = false, ResizeMode = ResizeMode.NoResize, Title = "Sketch ProMax animation test", Content = pane,
        };
        var thinking = ChatUi.Thinking();
        var reply = new ChatItem { Kind = "assistant" };
        var step = new ChatItem { Kind = "tool", Name = "get_elements", State = "running", Text = "Walls on Level 1" };
        FrameworkElement? mark = null;
        void Finish(Exception? error)
        {
            if (error is not null) problems.Add($"{error.GetType().Name}: {error.Message}");
            pane.StopGlide();
            try { window.Close(); } catch (InvalidOperationException) { }
            Brand.ReleaseTheme(pane);
            ChatUi.SetMotion(saved);
            done.TrySetResult((problems, "levels stored and read back; at Full a reply streamed, a step finished, Send and Stop swapped, the thinking mark and the glow ran"));
        }
        try
        {
            window.Show();
            pane._messages.Children.Add(thinking);
            // A reply streams in: two paragraphs, drawn through the same throttled path as a real answer.
            pane.Add(reply);
            pane._texts["motion"] = (reply, pane._views[reply]);
            reply.Text = "Found **12 walls** on Level 1.";
            pane._dirty.Add("motion");
            pane.RenderDirty();
            reply.Text += "\n\nThree are thicker than 200 mm.";
            pane._dirty.Add("motion");
            pane.RenderDirty();
            if (!pane._views[reply].Fading) problems.Add("new text in a streamed reply doesn't fade in");
            // A step runs, then finishes: its check pops in.
            pane.Add(step);
            pane.SetTool(pane._rows[step], "done", step.Text, "12 walls");
            mark = pane._rows[step].Mark.Content as FrameworkElement;
            if (mark?.RenderTransform is not ScaleTransform) problems.Add("a finished step's check doesn't pop in");
            // Send turns into Stop and back.
            pane._turn = new Turn();
            pane.ShowStatus();
            if (pane._stop.RenderTransform is not ScaleTransform) problems.Add("Stop doesn't pop in when it takes Send's place");
            pane._turn = null;
            pane.ShowStatus();
            pane.UpdateLayout();
            if (thinking is not { RenderTransform: ScaleTransform swell } || !swell.HasAnimatedProperties) problems.Add("the thinking mark doesn't breathe while shown");
            ChatUi.Glow((FrameworkElement)pane._messages.Children[pane._messages.Children.Count - 1]);
            var layer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(pane._messages);
            int Glows() => layer?.GetAdorners((UIElement)pane._messages.Children[pane._messages.Children.Count - 1])?.Length ?? 0;
            if (Glows() == 0) problems.Add("the finished glow doesn't show");
            // Everything settles: no fade, transform or glow left behind.
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2400) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try
                {
                    if (pane._views[reply].Fading) problems.Add("the streamed text didn't settle to the text colour");
                    if (mark is not null && mark.RenderTransform is ScaleTransform) problems.Add("the step's check kept its pop transform");
                    if (pane._send.RenderTransform is ScaleTransform || pane._stop.RenderTransform is ScaleTransform) problems.Add("Send or Stop kept its pop transform");
                    if (Glows() > 0) problems.Add("the finished glow didn't go");
                    pane._messages.Children.Remove(thinking);
                    pane.UpdateLayout();
                    if (thinking.RenderTransform is ScaleTransform s2 && s2.HasAnimatedProperties) problems.Add("the thinking mark keeps breathing once hidden");
                    Finish(null);
                }
                catch (Exception ex) { Finish(ex); }
            };
            timer.Start();
        }
        catch (Exception ex) { Finish(ex); }
        return done.Task;
    }

    /// <summary>
    /// The message box, in a hidden window (off screen, never activated) holding a stand-alone pane (probe
    /// chat.composer): typed text starts top-left where the hint was, more lines grow the box with the first line kept
    /// in place, the caret has a visible colour, the box's padding takes clicks, and a click on the card's empty part
    /// puts the caret in the box. Returns the problems found (none: it passed) and what was measured. On Revit's UI thread.
    /// </summary>
    internal static (List<string> Problems, string Measured) ComposerCheck()
    {
        var problems = new List<string>();
        var measured = "";
        var pane = new ChatPane(Snapshot.Sample());
        var window = new Window
        {
            Width = 420, Height = 760, Left = -32000, Top = -32000, WindowStyle = WindowStyle.None, ShowInTaskbar = false,
            ShowActivated = false, ResizeMode = ResizeMode.NoResize, Title = "Sketch ProMax composer test", Content = pane,
        };
        try
        {
            window.Show();
            pane.UpdateLayout();
            var box = pane._input;
            var hint = pane._hint.TranslatePoint(new Point(0, 0), pane._field);
            if (pane._hint.Visibility != Visibility.Visible) problems.Add("the hint isn't shown in the empty box");
            if (pane._hint.Text != ChatStyles.ComposerHint) problems.Add("the hint says \"" + pane._hint.Text + "\"");
            box.Text = "Now";
            pane.UpdateLayout();
            if (pane._hint.Visibility == Visibility.Visible) problems.Add("the hint stays when there is text");
            var first = box.GetRectFromCharacterIndex(0);
            if (first.IsEmpty) problems.Add("the first character's place couldn't be read");
            else
            {
                var text = box.TranslatePoint(first.TopLeft, pane._field);
                measured = $"hint at ({hint.X:0.#}, {hint.Y:0.#}), text at ({text.X:0.#}, {text.Y:0.#}), box {box.ActualHeight:0} px tall";
                if (Math.Abs(text.Y - hint.Y) > 3 || Math.Abs(text.X - hint.X) > 3) problems.Add("typed text doesn't start where the hint is (" + measured + ")");
                if (text.Y > ChatStyles.ComposerPadY + 6) problems.Add("typed text isn't at the top of the box (" + measured + ")");
                var height = box.ActualHeight;
                box.Text = "Now\nline 2\nline 3\nline 4";
                pane.UpdateLayout();
                var after = box.TranslatePoint(box.GetRectFromCharacterIndex(0).TopLeft, pane._field);
                if (box.ActualHeight <= height) problems.Add($"the box didn't grow with four lines ({height:0} px, then {box.ActualHeight:0} px)");
                if (Math.Abs(after.Y - text.Y) > 1) problems.Add($"the first line moved from {text.Y:0.#} to {after.Y:0.#} px as lines were added");
                measured += $", {box.ActualHeight:0} px with four lines";
            }
            if (box.ActualHeight + 0.5 < ChatStyles.ComposerMinHeight) problems.Add($"the box is {box.ActualHeight:0} px tall, under its {ChatStyles.ComposerMinHeight} px");
            if (box.CaretBrush is not SolidColorBrush { Color.A: > 0 } caret) problems.Add("the caret has no visible colour");
            else if (pane._field.Background is SolidColorBrush field && field.Color == caret.Color) problems.Add("the caret is the colour of the box");
            // The box's padding is part of the box: a click just inside its corner reaches the text box.
            var corner = box.TranslatePoint(new Point(3, 3), pane);
            if (pane.InputHitTest(corner) is not DependencyObject hit || !(ReferenceEquals(hit, box) || box.IsAncestorOf(hit)))
                problems.Add("a click in the box's padding doesn't reach the text box");
            // A click on the card's empty part (outside the box, not on a button) puts the caret in the box.
            box.Text = "";
            pane._scroll.Focus();
            pane._field.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount,
                System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseDownEvent });
            if (!box.IsFocused) problems.Add("a click on the card's empty part didn't put the caret in the box");
        }
        catch (Exception ex) { problems.Add($"{ex.GetType().Name}: {ex.Message}"); }
        finally
        {
            try { window.Close(); } catch (InvalidOperationException) { }
            Brand.ReleaseTheme(pane);
        }
        return (problems, measured);
    }

    /// <summary>The snapshot's extras: a step opened, and the typing line with its shimmer as while Claude starts.</summary>
    private void ShowSampleState()
    {
        if (_rows.Values.FirstOrDefault(r => r.Item.State == "done") is { } row) row.Header.IsChecked = true;
        _workingText.Text = "Sketch ProMax is thinking…";
        _working.Visibility = Visibility.Visible;
        _shimmer.Visibility = Visibility.Visible;
    }
}
