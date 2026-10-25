using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Shapes;
using SketchProMax.Bridge;
using SketchProMax.UI;

namespace SketchProMax.Chat;

/// <summary>
/// The live job card: a thin progress bar along the top, the task's headline and time, "Now: …", a one-line
/// "Thinking…" that opens to the whole thought, the planned steps updating in place (waiting, a spinner while running,
/// done, failed, skipped or stopped with the reason), and Pause/Resume, Skip step and Stop while it runs. Once the task
/// ends it collapses to its summary (done in, elements changed, time saved), with Undo all when that is safe and the steps
/// one click away. Built once and updated in place, so focus stays on the button pressed. Needs no chat pane, so the field
/// test can build one from a sample stream.
/// </summary>
internal sealed class JobCardView : ContentControl
{
    // The thin bar: a track with a filled part (star columns), drawn by hand so nothing in it moves on its own.
    private readonly Border _bar = new() { Height = 3, CornerRadius = new CornerRadius(1.5), SnapsToDevicePixels = true };
    private readonly ColumnDefinition _filled = new() { Width = new GridLength(0, GridUnitType.Star) }, _rest = new() { Width = new GridLength(1, GridUnitType.Star) };
    private double _value = -1;
    private readonly TextBlock _headline = new() { FontSize = ChatUi.SmallSize, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _clock = ChatUi.Small("");
    private readonly TextBlock _now = ChatUi.Small("", Brand.K.Text);
    private readonly TextBlock _progress = ChatUi.Small("");
    private readonly ToggleButton _thinkToggle = new();
    private readonly TextBlock _thinkLine = new() { FontSize = ChatUi.SmallSize, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _thinkChevron = ChatUi.Glyph(ChatUi.GlyphRight, Brand.K.Muted, 9);
    private readonly TextBlock _thinkText = Brand.Text("", Brand.K.Muted, ChatUi.SmallSize);
    private readonly Border _thinkDetail = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(ChatUi.S6, 0, 0, ChatUi.S1) };
    private readonly StackPanel _steps = new() { Margin = new Thickness(0, ChatUi.S1, 0, 0) };
    private readonly TextBlock _summary = Brand.Text("", Brand.K.Text, ChatUi.SmallSize);
    private readonly WrapPanel _buttons = new() { Margin = new Thickness(0, ChatUi.S2, 0, 0) };
    private readonly Button _pause, _skip, _stop, _undo, _showSteps;
    private string _stepsShown = "";
    private bool _thinking, _stepsOpen;
    private JobState _control = JobState.Running;

    public JobCardModel Model { get; }

    /// <summary>True while the task runs (the buttons and the live lines show).</summary>
    public bool Live { get; private set; }

    /// <summary>A button was pressed: Pause, Resume, Skip or Stop.</summary>
    public event Action<JobAction>? Pressed;

    /// <summary>Undo all was pressed.</summary>
    public event Action? UndoPressed;

    public JobCardView(JobCardModel model, bool live)
    {
        Model = model;
        Live = live;
        Focusable = false;
        Margin = new Thickness(0, ChatUi.S3, 0, ChatUi.S1);

        _pause = Pill("Pause", "SpmChatPill", "Pauses at the next item or step boundary; Resume carries on. Finished items stay as they are.");
        _skip = Pill("Skip step", "SpmChatPill", "Ends the current step at its next item and moves on. What it finished is kept; a step that can only finish whole (a check or a report) keeps nothing.");
        _stop = Pill("Stop", "SpmChatDanger", "Stops the task: the item in progress is rolled back and finished items are kept (a step that can only finish whole keeps nothing); no more steps run.");
        _undo = Pill("Undo all", "SpmChatPill", "Undoes everything this task changed in one step (Revit's Undo). Offered only when nothing else changed since.");
        _showSteps = ChatUi.Ghost(ChatUi.GlyphRight, "Show steps", "Show the steps", "Shows each step and how it ended.");
        _pause.Click += (_, _) => Pressed?.Invoke(_control is JobState.PauseRequested or JobState.Paused ? JobAction.Resume : JobAction.Pause);
        _skip.Click += (_, _) => Pressed?.Invoke(JobAction.Skip);
        _stop.Click += (_, _) => Pressed?.Invoke(JobAction.Stop);
        _undo.Click += (_, _) => UndoPressed?.Invoke();
        _showSteps.Click += (_, _) =>
        {
            _stepsOpen = !_stepsOpen;
            ShowSteps();
        };

        var panel = new StackPanel();
        // The thin bar runs along the top of the card.
        var fill = new Border { CornerRadius = new CornerRadius(1.5), SnapsToDevicePixels = true };
        fill.SetResourceReference(Border.BackgroundProperty, Brand.K.Accent);
        var bar = new Grid();
        bar.ColumnDefinitions.Add(_filled);
        bar.ColumnDefinitions.Add(_rest);
        bar.Children.Add(fill);
        _bar.Child = bar;
        _bar.Margin = new Thickness(0, 0, 0, ChatUi.S2);
        _bar.SetResourceReference(Border.BackgroundProperty, Brand.K.BorderSubtle);
        panel.Children.Add(_bar);

        var head = new DockPanel { Margin = new Thickness(0, 0, 0, ChatUi.S1) };
        var icon = ChatUi.Icon(ChatUi.Paths.Sparkle, 14);
        icon.Margin = new Thickness(0, 0, ChatUi.S2, 0);
        DockPanel.SetDock(icon, Dock.Left);
        head.Children.Add(icon);
        _clock.Margin = new Thickness(ChatUi.S2, 0, 0, 0);
        DockPanel.SetDock(_clock, Dock.Right);
        head.Children.Add(_clock);
        Brand.Fg(_headline, Brand.K.Text);
        head.Children.Add(_headline);
        panel.Children.Add(head);

        _now.Margin = new Thickness(0, 0, 0, 2);
        AutomationProperties.SetLiveSetting(_now, AutomationLiveSetting.Polite);
        panel.Children.Add(_now);
        _progress.Margin = new Thickness(0, 0, 0, 2);
        panel.Children.Add(_progress);

        // "Thinking…": one line while it runs, the whole thought when opened.
        var thinkRow = new DockPanel();
        _thinkChevron.Margin = new Thickness(0, 0, ChatUi.S2, 0);
        DockPanel.SetDock(_thinkChevron, Dock.Left);
        thinkRow.Children.Add(_thinkChevron);
        Brand.Fg(_thinkLine, Brand.K.Muted);
        thinkRow.Children.Add(_thinkLine);
        _thinkToggle.Content = thinkRow;
        _thinkToggle.SetResourceReference(StyleProperty, "SpmChatDisclosure");
        _thinkToggle.Padding = new Thickness(ChatUi.S1, 2, ChatUi.S1, 2);
        _thinkToggle.MinHeight = 24;
        _thinkToggle.Visibility = Visibility.Collapsed;
        _thinkToggle.Checked += (_, _) => OpenThinking(true);
        _thinkToggle.Unchecked += (_, _) => OpenThinking(false);
        AutomationProperties.SetHelpText(_thinkToggle, "Press Space to show or hide what Sketch ProMax is thinking.");
        panel.Children.Add(_thinkToggle);
        _thinkText.FontStyle = FontStyles.Italic;
        _thinkDetail.Child = _thinkText;
        panel.Children.Add(_thinkDetail);

        _summary.Margin = new Thickness(0, 0, 0, ChatUi.S1);
        panel.Children.Add(_summary);
        panel.Children.Add(_steps);
        panel.Children.Add(_buttons);

        var card = ChatUi.Elevated(panel, out var frame, ChatUi.Radius);
        frame.Padding = new Thickness(ChatUi.S3 + 2, ChatUi.S3, ChatUi.S3 + 2, ChatUi.S2);
        frame.ClipToBounds = true;
        Content = card;
        AutomationProperties.SetName(this, "Task");
        Render(null, DateTime.UtcNow, "");
    }

    /// <summary>A card's button (also the parallel job card's).</summary>
    internal static Button Pill(string text, string style, string help)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, ChatUi.S2, ChatUi.S1), ToolTip = help, MinWidth = 64 };
        b.SetResourceReference(StyleProperty, style);
        AutomationProperties.SetName(b, text);
        AutomationProperties.SetHelpText(b, help);
        return b;
    }

    /// <summary>The thinking toggle's one-line text (for the field test).</summary>
    internal string ThinkingShown => _thinkLine.Text;

    /// <summary>The opened thinking's text (for the field test).</summary>
    internal string ThinkingFull => _thinkText.Text;

    internal ToggleButton ThinkingToggle => _thinkToggle;

    /// <summary>A thinking block started (Claude is thinking, maybe without showing it).</summary>
    public void ThinkingStarted()
    {
        _thinking = true;
        Model.Think("", newBlock: true);
    }

    /// <summary>Thinking text arrived.</summary>
    public void ThinkingText(string text, bool newBlock)
    {
        _thinking = true;
        Model.Think(text, newBlock);
    }

    /// <summary>Claude wrote or called a tool: the thinking line stays, no longer "…ing".</summary>
    public void ThinkingDone() => _thinking = false;

    /// <summary>
    /// Draws the card from the model, the current step's progress (<paramref name="snap"/>, null when none) and the
    /// control's state. Cheap when nothing changed; call it a few times a second while the task runs.
    /// </summary>
    public void Render(JobFeed.Snapshot? snap, DateTime now, string nowLine, JobState control = JobState.Running)
    {
        _control = control;
        var running = Live && Model.State == "running";

        // The bar: the running step's items when it counts them, otherwise the steps finished.
        double value;
        if (running && snap is { Running: true, Total: > 0 }) value = JobMath.Percent(snap.Done, snap.Total);
        else
        {
            var counted = Model.Steps.Where(s => s.State != "notrun").ToList();
            value = !running ? 100 : counted.Count == 0 ? 0 : 100.0 * counted.Count(s => s.State is not ("waiting" or "running")) / counted.Count;
        }
        if (Math.Abs(_value - value) > 0.1)
        {
            _value = value;
            _filled.Width = new GridLength(value, GridUnitType.Star);
            _rest.Width = new GridLength(100 - value, GridUnitType.Star);
            AutomationProperties.SetName(_bar, $"Task progress {(int)value} percent");
        }
        _bar.Visibility = running || Model.State == "done" ? Visibility.Visible : Visibility.Collapsed;

        // The headline and the time.
        var state = running
            ? control switch
            {
                JobState.PauseRequested => "Pausing at the next boundary…",
                JobState.Paused => "Paused",
                JobState.Skipping => "Skipping this step…",
                JobState.Stopping => "Stopping…",
                _ => Model.Headline(),
            }
            : Model.State switch { "stopped" => "Stopped", "failed" => "Ended with a problem", _ => "Done" };
        Set(_headline, "Task · " + state);
        var end = running ? now : Model.Ended == default ? now : Model.Ended;
        Set(_clock, Model.Started == default ? "" : JobMath.Duration(end - Model.Started));
        AutomationProperties.SetName(this, "Task: " + state);

        // Now, the step's progress line, the summary.
        Set(_now, running && nowLine.Length > 0 ? "Now: " + nowLine : "");
        _now.Visibility = _now.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var progress = running && snap is { Running: true } ? JobMath.Line(snap, now) : "";
        Set(_progress, progress);
        _progress.Visibility = progress.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Set(_summary, running ? "" : Model.FinalLine());
        _summary.Visibility = running ? Visibility.Collapsed : Visibility.Visible;

        // Thinking.
        var line = JobCardModel.ThinkingLine(Model.Thinking);
        var label = running && _thinking ? "Thinking…" + (line.Length > 0 ? " " + line : "") : line.Length > 0 ? "Thought: " + line : "";
        Set(_thinkLine, label);
        _thinkToggle.Visibility = label.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(_thinkToggle, running && _thinking ? "Sketch ProMax is thinking" : "What Sketch ProMax thought");
        if (_thinkToggle.IsChecked == true) Set(_thinkText, Model.Thinking.Trim());

        ShowSteps();
        ShowButtons(running, control);
    }

    private static void Set(TextBlock block, string text)
    {
        if (block.Text != text) block.Text = text;
    }

    private void OpenThinking(bool open)
    {
        _thinkChevron.Text = open ? ChatUi.GlyphDown : ChatUi.GlyphRight;
        AutomationProperties.SetItemStatus(_thinkToggle, open ? "Expanded" : "Collapsed");
        if (open) _thinkText.Text = Model.Thinking.Trim();
        ChatUi.Reveal(_thinkDetail, open);
    }

    private void ShowSteps()
    {
        var running = Live && Model.State == "running";
        var visible = running || _stepsOpen;
        _steps.Visibility = visible && Model.Steps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_showSteps.Content is StackPanel { Children.Count: > 1 } ghost && ghost.Children[1] is TextBlock t)
        {
            t.Text = _stepsOpen ? "Hide steps" : "Show steps";
            if (ghost.Children[0] is TextBlock g) g.Text = _stepsOpen ? ChatUi.GlyphDown : ChatUi.GlyphRight;
        }
        AutomationProperties.SetName(_showSteps, _stepsOpen ? "Hide the steps" : "Show the steps");
        if (!visible) return;
        // Rebuilt only when a step changed: a running step's spinner keeps turning between updates.
        var shown = string.Join("|", Model.Steps.Select(s => $"{s.Tool};{s.State};{s.Reason};{s.Done};{s.Total}"));
        if (shown == _stepsShown) return;
        _stepsShown = shown;
        _steps.Children.Clear();
        var n = 0;
        foreach (var step in Model.Steps) _steps.Children.Add(StepRow(step, ++n));
    }

    /// <summary>One step: its mark, its title (and "added" when Claude added it), and the reason it didn't finish.</summary>
    private static UIElement StepRow(JobStep step, int n)
    {
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2), MinHeight = 22 };
        var mark = new ContentControl { Width = 18, Height = 18, Focusable = false, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, ChatUi.S2, 0) };
        string said;
        switch (step.State)
        {
            case "running":
                var spin = Brand.Spinner();
                spin.Margin = new Thickness(3, 0, 0, 0);
                spin.SetResourceReference(TextElement.ForegroundProperty, Brand.K.Accent);
                mark.Content = spin;
                said = "in progress";
                break;
            case "done":
                mark.Content = ChatUi.Glyph(ChatUi.GlyphCheck, Brand.K.SuccessText, 12);
                said = "done";
                break;
            case "failed":
                mark.Content = ChatUi.Glyph(ChatUi.GlyphCross, Brand.K.DangerText, 11);
                said = "failed";
                break;
            case "skipped":
                mark.Content = ChatUi.Glyph(ChatUi.GlyphRight, Brand.K.WarningText, 11);
                said = "skipped";
                break;
            case "stopped":
                mark.Content = ChatUi.Glyph(ChatUi.GlyphStop, Brand.K.Muted, 11);
                said = "stopped";
                break;
            case "notrun":
                mark.Content = Brand.Fg(new TextBlock { Text = "–", FontSize = ChatUi.SmallSize, HorizontalAlignment = HorizontalAlignment.Center }, Brand.K.Muted);
                said = "not run";
                break;
            default:
                var ring = new Ellipse { Width = 10, Height = 10, StrokeThickness = 1.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                ring.SetResourceReference(Shape.StrokeProperty, Brand.K.Muted);
                mark.Content = ring;
                said = "waiting";
                break;
        }
        if (mark.Content is FrameworkElement inner) inner.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(mark, Dock.Left);
        row.Children.Add(mark);

        var text = new StackPanel();
        var title = new TextBlock { FontSize = ChatUi.SmallSize, TextWrapping = TextWrapping.Wrap };
        Brand.Fg(title, step.State is "waiting" or "notrun" ? Brand.K.Muted : Brand.K.Text);
        title.Inlines.Add(new Run(step.Title) { FontWeight = step.State == "running" ? FontWeights.SemiBold : FontWeights.Normal });
        if (!step.Planned)
        {
            var added = new Run("  added");
            added.SetResourceReference(TextElement.ForegroundProperty, Brand.K.Muted);
            title.Inlines.Add(added);
        }
        if (step.Total > 0 && step.State is not "running") title.Inlines.Add(new Run($"  {step.Done} of {step.Total}"));
        text.Children.Add(title);
        if (step.Reason.Length > 0)
        {
            var why = Brand.Text(step.Reason, step.State == "failed" ? Brand.K.DangerText : Brand.K.Muted, ChatUi.SmallSize);
            text.Children.Add(why);
        }
        row.Children.Add(text);
        AutomationProperties.SetName(row, $"Step {n}: {step.Title}, {said}." + (step.Reason.Length > 0 ? " " + step.Reason : ""));
        return row;
    }

    private void ShowButtons(bool running, JobState control)
    {
        var want = new List<Button>();
        if (running)
        {
            var paused = control is JobState.PauseRequested or JobState.Paused;
            _pause.Content = paused ? "Resume" : "Pause";
            AutomationProperties.SetName(_pause, paused ? "Resume" : "Pause");
            _pause.ToolTip = paused ? "Carries on from where it paused." : "Pauses at the next item or step boundary; Resume carries on. Finished items stay as they are.";
            _pause.IsEnabled = control is JobState.Running or JobState.PauseRequested or JobState.Paused;
            _skip.IsEnabled = JobControl.Can(control, JobAction.Skip);
            _stop.IsEnabled = JobControl.Can(control, JobAction.Stop);
            want.Add(_pause);
            want.Add(_skip);
            want.Add(_stop);
        }
        else
        {
            if (UndoPressed is not null && CanUndo) want.Add(_undo);
            if (Model.Steps.Count > 0) want.Add(_showSteps);
        }
        if (_buttons.Children.Count == want.Count && _buttons.Children.Cast<UIElement>().SequenceEqual(want)) return;
        _buttons.Children.Clear();
        foreach (var b in want) _buttons.Children.Add(b);
        _buttons.Visibility = want.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Whether Undo all is offered now (set by the pane from JobUndo; false after a reload).</summary>
    public bool CanUndo { get; set; }

    /// <summary>The task ended: no more live lines or task buttons; the card shows its summary.</summary>
    public void End()
    {
        Live = false;
        _thinking = false;
        Render(null, DateTime.UtcNow, "");
    }
}
