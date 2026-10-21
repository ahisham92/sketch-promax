using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using SketchProMax.UI;

namespace SketchProMax.Chat;

/// <summary>
/// A parallel job's card in the chat (JobBoard): "Job 2 · Sheets for levels 1-5", its state (waiting to start, working,
/// waiting its turn, done, stopped, ended with a problem) and time, what it is doing now or what it reported, and Open
/// and Stop. Open shows the job's detail (what it was asked, what it wrote, the steps it took) and a box to message it.
/// Built once and drawn again from the job in place, so focus stays where it was. Needs no chat pane, so the field test
/// can build one.
/// </summary>
internal sealed class ParallelJobCard : ContentControl
{
    private readonly TextBlock _headline = new() { FontSize = ChatUi.SmallSize, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _state = ChatUi.Small("");
    private readonly TextBlock _clock = ChatUi.Small("");
    private readonly TextBlock _line = Brand.Text("", Brand.K.Text, ChatUi.SmallSize);
    private readonly Button _open, _stop, _send;
    private readonly Border _detail = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, ChatUi.S2, 0, 0) };
    private readonly StackPanel _log = new();
    private readonly ScrollViewer _logScroll;
    private readonly TextBox _box = new() { AcceptsReturn = false, TextWrapping = TextWrapping.Wrap, MaxHeight = 96, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly TextBlock _ack = Brand.Text("", Brand.K.Muted, ChatUi.SmallSize);
    private string _logShown = "";
    private bool _isOpen;

    public int Number { get; }

    /// <summary>Stop was pressed.</summary>
    public event Action<int>? StopPressed;

    /// <summary>A message was typed for the job: the handler sends it and says how it went, for the line under the box.</summary>
    public Func<int, string, (bool Ok, string Said)>? MessageSent;

    public ParallelJobCard(int number)
    {
        Number = number;
        Focusable = false;
        Margin = new Thickness(0, ChatUi.S3, 0, ChatUi.S1);

        _open = Pill("Open", "SpmChatPill", "Shows what the job was asked, what it did step by step, and a box to send it a message.");
        _stop = Pill("Stop", "SpmChatDanger", "Stops this job. A change it already started in Revit finishes on its own; send it a message later to carry on.");
        _send = Pill("Send", "SpmChatPrimary", "Sends the message to this job (Enter). A working job gets it when its current run ends.");
        _send.Margin = new Thickness(ChatUi.S2, 0, 0, 0);
        _open.Click += (_, _) => SetOpen(!_isOpen);
        _stop.Click += (_, _) => StopPressed?.Invoke(Number);
        _send.Click += (_, _) => Send();

        var panel = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, ChatUi.S1) };
        var icon = ChatUi.Icon(ChatUi.Paths.Grid, 14);
        icon.Margin = new Thickness(0, 0, ChatUi.S2, 0);
        DockPanel.SetDock(icon, Dock.Left);
        head.Children.Add(icon);
        _clock.Margin = new Thickness(ChatUi.S2, 0, 0, 0);
        DockPanel.SetDock(_clock, Dock.Right);
        head.Children.Add(_clock);
        _state.Margin = new Thickness(ChatUi.S2, 0, 0, 0);
        AutomationProperties.SetLiveSetting(_state, AutomationLiveSetting.Polite);
        DockPanel.SetDock(_state, Dock.Right);
        head.Children.Add(_state);
        Brand.Fg(_headline, Brand.K.Text);
        _headline.FontWeight = FontWeights.SemiBold;
        head.Children.Add(_headline);
        panel.Children.Add(head);

        _line.Margin = new Thickness(0, 0, 0, ChatUi.S1);
        panel.Children.Add(_line);

        var buttons = new WrapPanel { Margin = new Thickness(0, ChatUi.S1, 0, 0) };
        buttons.Children.Add(_open);
        buttons.Children.Add(_stop);
        panel.Children.Add(buttons);

        // The detail: the job's own conversation, then a box to message it.
        var detail = new StackPanel();
        _logScroll = new ScrollViewer
        {
            Content = _log, MaxHeight = 280, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false,
        };
        AutomationProperties.SetName(_logScroll, $"What job {number} did");
        detail.Children.Add(_logScroll);
        var label = Brand.Text($"Message job {number}", Brand.K.Text, ChatUi.SmallSize, bold: true);
        label.Margin = new Thickness(0, ChatUi.S3, 0, ChatUi.S1);
        detail.Children.Add(label);
        _box.SetResourceReference(StyleProperty, "SpmChatField");
        AutomationProperties.SetName(_box, $"Message for job {number}");
        AutomationProperties.SetHelpText(_box, "Type a message and press Enter. Esc closes the detail.");
        _box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Send();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                SetOpen(false);
            }
        };
        var row = new DockPanel();
        DockPanel.SetDock(_send, Dock.Right);
        row.Children.Add(_send);
        row.Children.Add(_box);
        detail.Children.Add(row);
        _ack.Margin = new Thickness(0, ChatUi.S1, 0, 0);
        AutomationProperties.SetLiveSetting(_ack, AutomationLiveSetting.Polite);
        detail.Children.Add(_ack);
        _detail.Child = detail;
        panel.Children.Add(_detail);

        var card = ChatUi.Elevated(panel, out var frame, ChatUi.Radius);
        frame.Padding = new Thickness(ChatUi.S3 + 2, ChatUi.S3, ChatUi.S3 + 2, ChatUi.S2);
        Content = card;
        AutomationProperties.SetName(this, $"Job {number}");
    }

    private static Button Pill(string text, string style, string help)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, ChatUi.S2, ChatUi.S1), ToolTip = help, MinWidth = 64 };
        b.SetResourceReference(StyleProperty, style);
        AutomationProperties.SetName(b, text);
        AutomationProperties.SetHelpText(b, help);
        return b;
    }

    /// <summary>The headline as shown (for the field test).</summary>
    internal string Headline => _headline.Text;

    /// <summary>The state as shown (for the field test).</summary>
    internal string StateShown => _state.Text;

    /// <summary>Draws the card from <paramref name="job"/> (a copy from JobBoard.Snapshot). Cheap when nothing changed.</summary>
    public void Render(ChatJob job, DateTime now, int limit)
    {
        var state = JobBoard.StateLabel(job);
        Set(_headline, $"Job {job.Number} · {job.Title}");
        Set(_state, state);
        Brand.Fg(_state, job.State switch
        {
            JobBoard.Done => Brand.K.SuccessText,
            JobBoard.Failed => Brand.K.DangerText,
            JobBoard.Waiting => Brand.K.WarningText,
            JobBoard.Running => Brand.K.Text,
            _ => Brand.K.Muted,
        });
        var start = job.Started == default ? job.Created : job.Started;
        var end = job.Active ? now : job.Ended == default ? start : job.Ended;
        Set(_clock, job.State == JobBoard.Queued || start == default ? "" : SketchProMax.Bridge.JobMath.Duration(end - start));

        var line = job.State switch
        {
            JobBoard.Queued => $"Starts when a place is free (up to {JobBoard.ClampLimit(limit)} jobs run at once).",
            JobBoard.Waiting => "Its change waits for Revit to finish another job's change." + (job.Now.Length > 0 ? " Next: " + job.Now : ""),
            JobBoard.Running => job.Now.Length > 0 ? "Now: " + job.Now : "Working…",
            JobBoard.Stopped => "Stopped. Open it and send a message to carry on.",
            _ => Cut(job.Summary.Trim(), 600),
        };
        Set(_line, line);
        _line.Visibility = line.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Brand.Fg(_line, job.State == JobBoard.Failed ? Brand.K.DangerText : job.State is JobBoard.Queued or JobBoard.Stopped ? Brand.K.Muted : Brand.K.Text);

        var stoppable = job.Active || job.State == JobBoard.Queued;
        _stop.Visibility = stoppable ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(this, $"Job {job.Number}: {job.Title}, {state}");
        if (_isOpen) ShowLog(job);
    }

    private void SetOpen(bool open)
    {
        _isOpen = open;
        _open.Content = open ? "Close" : "Open";
        AutomationProperties.SetName(_open, open ? "Close" : "Open");
        AutomationProperties.SetItemStatus(_open, open ? "Expanded" : "Collapsed");
        if (open) Opened?.Invoke(Number);
        ChatUi.Reveal(_detail, open);
        if (open) Dispatcher.BeginInvoke(new Action(() => _box.Focus()), System.Windows.Threading.DispatcherPriority.Input);
        else _open.Focus();
    }

    /// <summary>The detail was opened: the pane draws the job's lines into it at once.</summary>
    public event Action<int>? Opened;

    /// <summary>The job's lines, rebuilt only when one was added or a step ended.</summary>
    private void ShowLog(ChatJob job)
    {
        var shown = job.Log.Count + ";" + string.Join("|", job.Log.Skip(Math.Max(0, job.Log.Count - 3)).Select(l => l.State + l.Text.Length));
        if (shown == _logShown) return;
        var atEnd = _logScroll.VerticalOffset >= _logScroll.ScrollableHeight - 4;
        _logShown = shown;
        _log.Children.Clear();
        foreach (var l in job.Log) _log.Children.Add(LogLine(l));
        if (job.Log.Count == 0) _log.Children.Add(ChatUi.Small("Nothing yet."));
        if (atEnd) _logScroll.ScrollToEnd();
    }

    private static UIElement LogLine(JobLine line)
    {
        switch (line.Kind)
        {
            case "step":
            {
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                var mark = line.State switch
                {
                    "done" => ChatUi.Glyph(ChatUi.GlyphCheck, Brand.K.SuccessText, 11),
                    "failed" => ChatUi.Glyph(ChatUi.GlyphCross, Brand.K.DangerText, 10),
                    _ => ChatUi.Glyph(ChatUi.GlyphRight, Brand.K.Muted, 10),
                };
                mark.Margin = new Thickness(0, 0, ChatUi.S2, 0);
                mark.VerticalAlignment = VerticalAlignment.Top;
                DockPanel.SetDock(mark, Dock.Left);
                row.Children.Add(mark);
                row.Children.Add(Brand.Text(line.Text, Brand.K.Text, ChatUi.SmallSize));
                var said = line.State switch { "done" => "done", "failed" => "failed", _ => "in progress" };
                AutomationProperties.SetName(row, $"Step: {line.Text}, {said}");
                return row;
            }
            case "you":
            {
                var text = new TextBlock { FontSize = ChatUi.SmallSize, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, ChatUi.S2, 0, 2) };
                Brand.Fg(text, Brand.K.Text);
                text.Inlines.Add(new Run("Asked: ") { FontWeight = FontWeights.SemiBold });
                ChatUi.AddLines(text.Inlines, Cut(line.Text, 1500));
                return text;
            }
            case "note":
            {
                var note = Brand.Text(line.Text, Brand.K.Muted, ChatUi.SmallSize);
                note.FontStyle = FontStyles.Italic;
                note.Margin = new Thickness(0, 2, 0, 2);
                return note;
            }
            default:
            {
                var view = new MarkdownView { Margin = new Thickness(0, ChatUi.S1, 0, ChatUi.S1) };
                view.SetMarkdown(line.Text);
                return view;
            }
        }
    }

    private void Send()
    {
        var text = _box.Text.Trim();
        if (text.Length == 0)
        {
            _ack.Text = "Type a message first.";
            _box.Focus();
            return;
        }
        var (ok, said) = MessageSent?.Invoke(Number, text) ?? (false, "The chat isn't ready. Try again in a moment.");
        if (ok) _box.Clear();
        _ack.Text = said;
        Brand.Fg(_ack, ok ? Brand.K.SuccessText : Brand.K.DangerText);
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text.Substring(0, max - 1).TrimEnd() + "…";

    private static void Set(TextBlock block, string text)
    {
        if (block.Text != text) block.Text = text;
    }
}
