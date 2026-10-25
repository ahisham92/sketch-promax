using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using SketchProMax.UI;

namespace SketchProMax.Chat;

/// <summary>
/// The plan's usage in the chat's header, the way the Claude app shows it: a small meter ("Usage: 5h 43% · Week 5%", or
/// "5h 43% · Wk 5%" in a narrow pane) whose hover gives the session's time left and each reset, and a click opens a detail with each limit Claude Code reported (the current 5-hour session, the weekly limits), how much is used
/// and when it resets. The figures are only what Claude Code reports with each answer (rate_limit_event, PlanUsage):
/// nothing is estimated. Near a limit (80 %) the chat says so once per limit and reset, naming the jobs running then.
/// </summary>
internal sealed partial class ChatPane
{
    /// <summary>The latest usage reported in this Revit session (or the last one saved), shared by every chat.</summary>
    private static PlanUsage? _usage;
    /// <summary>Limits already warned about (PlanUsage.NewWarnings): once per limit and reset.</summary>
    private static readonly HashSet<string> _usageWarned = new(StringComparer.Ordinal);
    private static bool _usageLoaded;

    private readonly Popup _usageMenu = new() { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom };
    private readonly StackPanel _usageList = new() { Margin = new Thickness(ChatUi.S3) };
    private readonly TextBlock _usageText = new() { FontSize = ChatUi.SmallSize, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _usageBar = new() { Height = 4, Width = 28, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _usageFill = new() { Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left };
    private Button _usagePill = null!;
    private DateTime _usageClosed;

    /// <summary>The meter in the header and its detail popup.</summary>
    private void BuildUsageMeter()
    {
        if (!_usageLoaded && !_sample)
        {
            _usageLoaded = true;
            _usage = PlanUsage.Load(ClaudeCli.Folder);
        }
        _usageBar.Child = _usageFill;
        _usageBar.Margin = new Thickness(ChatUi.S1, 0, 0, 0);
        _usageBar.SetResourceReference(Border.BackgroundProperty, Brand.K.BorderSubtle);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_usageText);
        row.Children.Add(_usageBar);
        _usagePill = new Button { Content = row, Padding = new Thickness(ChatUi.S1, 0, ChatUi.S1, 0), Margin = new Thickness(-ChatUi.S1, 0, ChatUi.S2, 0), VerticalAlignment = VerticalAlignment.Center };
        _usagePill.SetResourceReference(StyleProperty, "SpmChatGhost");
        _usagePill.Click += (_, _) => ToggleUsage();
        AutomationProperties.SetItemStatus(_usagePill, "Collapsed");
        var card = ChatUi.Elevated(_usageList, out _, ChatUi.Radius);
        card.Margin = new Thickness(ChatUi.S3);
        card.MinWidth = 260;
        card.MaxWidth = 340;
        _usageMenu.Child = card;
        _usageMenu.PlacementTarget = _usagePill;
        _usageMenu.Closed += (_, _) =>
        {
            _usageClosed = DateTime.UtcNow;
            AutomationProperties.SetItemStatus(_usagePill, "Collapsed");
        };
        ShowUsage();
        // The session's time left counts down between answers.
        var tick = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(30) };
        tick.Tick += (_, _) =>
        {
            try { if (IsLoaded) ShowUsage(); }
            catch (Exception) { /* the next answer redraws it */ }
        };
        Loaded += (_, _) => tick.Start();
        Unloaded += (_, _) => tick.Stop();
        if (IsLoaded) tick.Start();
    }

    /// <summary>The meter: the session and weekly figures, coloured as they near the limit.</summary>
    private void ShowUsage()
    {
        if (_usagePill is null) return;
        var now = DateTimeOffset.UtcNow;
        var meter = _usage?.Meter(now) ?? "";
        // The header keeps it short: the session's time left and each reset show on hover (and in the detail).
        var shown = _usage?.Meter(now, timeLeft: false, narrow: _narrow) ?? "";
        _usageText.Text = shown.Length > 0 ? (_narrow ? shown : "Usage: " + shown) : _narrow ? "Usage" : "Usage: not reported yet";
        var high = _usage?.Highest(now);
        var key = high >= 1 ? Brand.K.DangerText : high >= PlanUsage.WarnAt ? Brand.K.WarningText : Brand.K.Muted;
        Brand.Fg(_usageText, key);
        _usageBar.Visibility = high is null ? Visibility.Collapsed : Visibility.Visible;
        _usageFill.Width = Math.Round(_usageBar.Width * Math.Min(1, high ?? 0));
        _usageFill.SetResourceReference(Border.BackgroundProperty, high >= 1 ? Brand.K.DangerText : high >= PlanUsage.WarnAt ? Brand.K.WarningText : Brand.K.Accent);
        _usagePill.ToolTip = meter.Length > 0
            ? _usage!.Hover(now) + "\nAs last reported with an answer. Click for the details."
            : "Your plan's usage shows after Sketch ProMax's next answer.";
        AutomationProperties.SetName(_usagePill, "Plan usage: " + (meter.Length > 0 ? meter.Replace("·", ",") : "not reported yet"));
        AutomationProperties.SetHelpText(_usagePill, (string)_usagePill.ToolTip);
        if (_usageMenu.IsOpen) FillUsage();
    }

    private void ToggleUsage()
    {
        if (_usageMenu.IsOpen)
        {
            _usageMenu.IsOpen = false;
            _usagePill.Focus();
            return;
        }
        if (DateTime.UtcNow - _usageClosed < TimeSpan.FromMilliseconds(250)) return;
        FillUsage();
        _usageMenu.IsOpen = true;
        AutomationProperties.SetItemStatus(_usagePill, "Expanded");
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => _usageList.Children.OfType<Button>().FirstOrDefault()?.Focus()));
    }

    /// <summary>The detail: each reported limit with its bar, % used and reset, and where the figures come from.</summary>
    private void FillUsage()
    {
        var now = DateTimeOffset.UtcNow;
        _usageList.Children.Clear();
        var heading = Brand.Text("Plan usage", Brand.K.Text, ChatUi.BodySize, bold: true);
        heading.Margin = new Thickness(0, 0, 0, ChatUi.S2);
        _usageList.Children.Add(heading);
        if (_usage is not { Windows.Count: > 0 } usage)
        {
            _usageList.Children.Add(Brand.Text("Not reported yet. Your plan's usage comes with each answer, so it shows here after Sketch ProMax's next answer.", Brand.K.Muted, ChatUi.SmallSize));
        }
        else
        {
            foreach (var w in usage.Windows)
            {
                var used = PlanUsage.Current(w, now);
                var block = new StackPanel { Margin = new Thickness(0, 0, 0, ChatUi.S3) };
                var line = new DockPanel();
                var figure = Brand.Text(used is { } u ? PlanUsage.Percent(u) + " used" : PlanUsage.HasReset(w, now) ? "Reset" : "No figure reported",
                    used >= 1 ? Brand.K.DangerText : used >= PlanUsage.WarnAt ? Brand.K.WarningText : Brand.K.Text, ChatUi.SmallSize, bold: true);
                DockPanel.SetDock(figure, Dock.Right);
                line.Children.Add(figure);
                line.Children.Add(Brand.Text(PlanUsage.Label(w.Key), Brand.K.Text, ChatUi.SmallSize));
                block.Children.Add(line);
                var bar = new Border { Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 3, 0, 3) };
                bar.SetResourceReference(Border.BackgroundProperty, Brand.K.BorderSubtle);
                var fill = new Border { Height = 6, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left };
                fill.SetResourceReference(Border.BackgroundProperty, used >= 1 ? Brand.K.DangerText : used >= PlanUsage.WarnAt ? Brand.K.WarningText : Brand.K.Accent);
                var share = Math.Min(1, used ?? 0);
                bar.SizeChanged += (_, e) => fill.Width = Math.Round(e.NewSize.Width * share);
                bar.Child = fill;
                block.Children.Add(bar);
                var reset = PlanUsage.ResetText(w, now);
                block.Children.Add(Brand.Text(reset, Brand.K.Muted, ChatUi.SmallSize));
                AutomationProperties.SetName(block, $"{PlanUsage.Label(w.Key)}: {(used is { } x ? PlanUsage.Percent(x) + " used" : "no figure")}. {reset}.");
                _usageList.Children.Add(block);
            }
            var state = usage.Status switch
            {
                "rejected" => "A limit is reached: requests wait until it resets" + (usage.UsingExtra ? "." : ", unless extra usage is on."),
                "allowed_warning" => "You are near a limit.",
                _ => "",
            };
            if (usage.UsingExtra) state = (state + " Extra usage is being used.").Trim();
            if (state.Length > 0)
            {
                var warn = Brand.Text(state, Brand.K.WarningText, ChatUi.SmallSize);
                warn.Margin = new Thickness(0, 0, 0, ChatUi.S2);
                _usageList.Children.Add(warn);
            }
            var age = now - usage.ReadAt;
            var when = usage.ReadAt.ToLocalTime().ToString(age < TimeSpan.FromHours(20) ? "HH:mm" : "ddd d MMM HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            _usageList.Children.Add(Brand.Text($"As reported with an answer at {when}. Only reported figures are shown, never an estimate: the weekly Opus or Sonnet limit appears only once it is the limit in force.",
                Brand.K.Muted, ChatUi.SmallSize));
        }
        var close = new Button { Content = "Close", Margin = new Thickness(0, ChatUi.S3, 0, 0), HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 64 };
        close.SetResourceReference(StyleProperty, "SpmChatPill");
        close.Click += (_, _) =>
        {
            _usageMenu.IsOpen = false;
            _usagePill.Focus();
        };
        _usageList.Children.Add(close);
    }

    /// <summary>
    /// Claude Code reported the plan's usage (the main chat or a job): the meter shows it, it is kept for the next Revit
    /// session, and a limit that just passed 80 % (or was reached) is said in the chat with the jobs running now.
    /// </summary>
    private void OnUsage(PlanUsage reported)
    {
        var usage = _usage = PlanUsage.Merge(_usage, reported);
        if (!_sample)
        {
            var folder = ClaudeCli.Folder;
            _ = Task.Run(() => usage.Save(folder));
        }
        ShowUsage();
        var now = DateTimeOffset.UtcNow;
        var warnings = usage.NewWarnings(_usageWarned, now);
        if (warnings.Count == 0 || _sample) return;
        var running = _board?.Snapshot().Where(j => j.Active).OrderBy(j => j.Number).Select(j => $"Job {j.Number} · {j.Title}").ToList() ?? [];
        var text = PlanUsage.Warning(warnings, running, now);
        Note(text);
        if (_viewJob is not null) Status(text, spin: false);
    }
}
