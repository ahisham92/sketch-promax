using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SketchProMax.UI;

namespace SketchProMax.Chat;

/// <summary>
/// A parallel job's card in the chat (JobBoard): "Job 2 · Sheets for levels 1-5", its state (waiting to start, working,
/// waiting its turn, done, stopped, ended with a problem) and time, what it is doing now or what it reported, and Open
/// job and Stop. Open job shows the job's own chat in the pane (ChatPane's job view), where the user talks to it.
/// Built once and drawn again from the job in place, so focus stays where it was. Needs no chat pane, so the field test
/// can build one.
/// </summary>
internal sealed class ParallelJobCard : ContentControl
{
    private readonly TextBlock _headline = new() { FontSize = ChatUi.SmallSize, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _state = ChatUi.Small("");
    private readonly TextBlock _clock = ChatUi.Small("");
    private readonly TextBlock _line = Brand.Text("", Brand.K.Text, ChatUi.SmallSize);
    private readonly TextBlock _runsOn = ChatUi.Small("");
    private readonly Button _open, _stop;

    public int Number { get; }

    /// <summary>Stop was pressed.</summary>
    public event Action<int>? StopPressed;

    /// <summary>Open job was pressed: the pane shows the job's own chat.</summary>
    public event Action<int>? Opened;

    /// <summary>The words on the open button (for the field test).</summary>
    public const string OpenText = "Open job";

    public ParallelJobCard(int number)
    {
        Number = number;
        Focusable = false;
        Margin = new Thickness(0, ChatUi.S3, 0, ChatUi.S1);

        _open = Pill(OpenText, "SpmChatPill", $"Shows job {number}'s own chat: everything it was asked and did. Messages you send there go to this job; Back to coordinator returns.");
        _stop = Pill("Stop", "SpmChatDanger", "Stops this job. A change it already started in Revit finishes on its own; send it a message later to carry on.");
        AutomationProperties.SetName(_open, $"Open job {number}");
        AutomationProperties.SetName(_stop, $"Stop job {number}");
        _open.Click += (_, _) => Opened?.Invoke(Number);
        _stop.Click += (_, _) => StopPressed?.Invoke(Number);

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
        _runsOn.Margin = new Thickness(0, 0, 0, ChatUi.S1);
        panel.Children.Add(_runsOn);

        _line.Margin = new Thickness(0, 0, 0, ChatUi.S1);
        panel.Children.Add(_line);

        var buttons = new WrapPanel { Margin = new Thickness(0, ChatUi.S1, 0, 0) };
        buttons.Children.Add(_open);
        buttons.Children.Add(_stop);
        panel.Children.Add(buttons);

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

    /// <summary>The model and effort as shown (for the field test).</summary>
    internal string RunsOnShown => _runsOn.Text;

    /// <summary>"Sonnet, medium effort": the model and effort a job runs with.</summary>
    public static string RunsOn(ChatJob job) =>
        ChatAuto.ModelLabel(job.Model) + (job.Effort.Length > 0 ? ", " + ChatAuto.EffortLabel(job.Effort).ToLowerInvariant() + " effort" : "");

    /// <summary>Presses Open job (for the field test).</summary>
    internal void PressOpen() => Opened?.Invoke(Number);

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
            JobBoard.Stopped => "Stopped. Open the job and send it a message to carry on.",
            _ => Cut(job.Summary.Trim(), 600),
        };
        Set(_runsOn, job.Model.Length > 0 ? RunsOn(job) + (job.Kind.Length > 0 ? " · " + ChatAuto.KindLabel(job.Kind).ToLowerInvariant() : "") : "");
        _runsOn.Visibility = _runsOn.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Set(_line, line);
        _line.Visibility = line.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Brand.Fg(_line, job.State == JobBoard.Failed ? Brand.K.DangerText : job.State is JobBoard.Queued or JobBoard.Stopped ? Brand.K.Muted : Brand.K.Text);

        var stoppable = job.Active || job.State == JobBoard.Queued;
        _stop.Visibility = stoppable ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(this, $"Job {job.Number}: {job.Title}, {state}" + (_runsOn.Text.Length > 0 ? ", " + _runsOn.Text : ""));
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text.Substring(0, max - 1).TrimEnd() + "…";

    private static void Set(TextBlock block, string text)
    {
        if (block.Text != text) block.Text = text;
    }
}
