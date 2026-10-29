using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using SketchProMax.UI;

namespace SketchProMax.Chat;

/// <summary>
/// Autopilot in the pane (Autopilot): a chat With coordinator started from New chat › New autopilot chat. The user types
/// the goal; the coordinator answers with a plan and a card offers Approve (or the user sends changes and it plans
/// again). Approve saves a backup copy of the model in a Backup folder beside it and starts the run: job reports come back as review
/// turns, a turn that leaves nothing open gets a nudge, the final report ends it, usage near a limit pauses it until
/// the limit resets. A banner over the chat says where it is, with Stop (and Resume when paused without a reset time).
/// </summary>
internal sealed partial class ChatPane
{
    public const string AutopilotApprove = "autopilot:approve";

    private Border? _autoBanner;
    private readonly TextBlock _autoText = new() { FontSize = ChatUi.SmallSize, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private Button? _autoStop, _autoResume;
    private DispatcherTimer? _autoTimer;
    private bool _autoHooked;
    private DateTime _autoIdleSince;

    private AutopilotRun? Run => _conv.Autopilot;

    /// <summary>New chat › New autopilot chat: a chat With coordinator waiting for the goal.</summary>
    private void NewAutopilotChat()
    {
        NewChat(ChatTypes.Coordinator);
        if (_conv.Type != ChatTypes.Coordinator) return; // the new chat couldn't be opened (the status line says why)
        _conv.Autopilot = new AutopilotRun();
        Note("Autopilot: type the goal and send. The coordinator answers with a plan; once you approve it, he and the " +
             "workers carry on without asking you anything until the goal is met, a usage limit pauses them (they carry on " +
             "when it resets) or you press Stop. A backup copy of the model is saved first, in a Backup folder beside it.");
        Save();
        ShowAutopilot();
    }

    /// <summary>The banner, the unattended switch and the timer, for the chat shown now.</summary>
    private void ShowAutopilot()
    {
        if (!_autoHooked)
        {
            _autoHooked = true;
            Autopilot.Logged += (kind, text) => Dispatcher.BeginInvoke(new Action(() => AutopilotLog(kind, text)));
        }
        var run = _sample ? null : Run;
        Autopilot.Unattended = run is { Active: true };
        if (run is null)
        {
            if (_autoBanner is not null) _autoBanner.Visibility = Visibility.Collapsed;
            _autoTimer?.Stop();
            return;
        }
        if (_autoBanner is null) BuildAutopilotBanner();
        var done = _board?.Snapshot().Count(j => j.Number > run.JobsBefore && j.State == JobBoard.Done) ?? 0;
        _autoText.Text = Autopilot.Banner(run, done, DateTimeOffset.UtcNow);
        _autoBanner!.Visibility = Visibility.Visible;
        _autoStop!.Visibility = run.Active ? Visibility.Visible : Visibility.Collapsed;
        _autoResume!.Visibility = run.State == AutopilotRun.Paused && run.PausedUntil is null ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(_autoBanner, _autoText.Text);
        if (run.Active) _autoTimer!.Start();
        else _autoTimer!.Stop();
    }

    private void BuildAutopilotBanner()
    {
        var row = new DockPanel { Margin = new Thickness(ChatUi.S3, ChatUi.S1, ChatUi.S3, ChatUi.S1) };
        _autoStop = Pill("Stop", primary: false, "Stop the autopilot: open jobs stop and the report is written.", () => AutopilotStop("you pressed Stop"));
        _autoResume = Pill("Resume", primary: true, "Carry on now.", () => AutopilotResume("You pressed Resume."));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(_autoResume);
        buttons.Children.Add(_autoStop);
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        var icon = ChatUi.Glyph("", Brand.K.Accent, 12);
        icon.Margin = new Thickness(0, 0, ChatUi.S2, 0);
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(_autoText);
        _autoBanner = new Border { Child = row, BorderThickness = new Thickness(0, 0, 0, 1) };
        _autoBanner.SetResourceReference(Border.BackgroundProperty, Brand.K.Raised);
        _autoBanner.SetResourceReference(Border.BorderBrushProperty, Brand.K.BorderSubtle);
        Brand.Fg(_autoText, Brand.K.Text);
        SetDock(_autoBanner, Dock.Top);
        var at = Children.IndexOf(_switcherHome);
        Children.Insert(at < 0 ? Children.Count - 1 : at, _autoBanner);
        _autoTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(30) };
        _autoTimer.Tick += (_, _) =>
        {
            try { AutopilotTick(); }
            catch (Exception) { /* the next tick tries again */ }
        };

        Button Pill(string text, bool primary, string help, Action click)
        {
            var b = new Button { Content = text, Margin = new Thickness(ChatUi.S2, 0, 0, 0), ToolTip = help };
            b.SetResourceReference(StyleProperty, primary ? "SpmChatPrimary" : "SpmChatPill");
            AutomationProperties.SetName(b, text);
            AutomationProperties.SetHelpText(b, help);
            b.Click += (_, _) =>
            {
                try { click(); }
                catch (Exception ex) { Status($"That didn't work ({ex.Message}).", spin: false); }
            };
            return b;
        }
    }

    /// <summary>
    /// Send while the autopilot waits for its goal (or its plan's approval): the goal (or the change to it) goes to the
    /// coordinator to plan. False when the chat isn't an autopilot waiting for a goal.
    /// </summary>
    private bool AutopilotGoal(string text, List<Attachment> attachments)
    {
        if (Run is not { State: AutopilotRun.Planning or AutopilotRun.Approval } run || text.Length == 0) return false;
        run.Goal = run.State == AutopilotRun.Approval && run.Goal.Length > 0 ? run.Goal + "\nChange from the user: " + text : text;
        run.State = AutopilotRun.Planning;
        Save();
        ShowAutopilot();
        _ = RunTurn(Autopilot.PlanRequest(run.Goal), attachments, "");
        return true;
    }

    /// <summary>A coordinator turn ended (EndTurn): the plan waits for approval, the run carries on, or it is finished.</summary>
    private void AutopilotTurnEnded(Turn? ended, bool stopped)
    {
        if (Run is not { } run || ended is null || ended.Planning || ended.Shrinking) return;
        var last = _conv.Items.LastOrDefault(i => i.Kind == "assistant")?.Text ?? "";
        if (run.State == AutopilotRun.Planning)
        {
            if (stopped || ended.Outcome != "done" || last.Trim().Length == 0) return;
            run.Plan = last.Trim();
            run.State = AutopilotRun.Approval;
            Add(new ChatItem { Kind = "note", Name = AutopilotApprove, Text = "Approve this plan to start the autopilot. To change it, type what to change and send." });
            Save();
            ShowAutopilot();
            return;
        }
        if (run.State != AutopilotRun.Running) return;
        if (last.Contains(Autopilot.DoneMark))
        {
            AutopilotFinish(AutopilotRun.Done, "");
            return;
        }
        _autoIdleSince = DateTime.UtcNow;
        // Jobs still working or reports on their way: they bring the next turn.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(AutopilotNudgeIfIdle));
    }

    /// <summary>Nothing open, nothing to report, no final report: tell the coordinator to carry on (a few times at most).</summary>
    private void AutopilotNudgeIfIdle()
    {
        if (Run is not { State: AutopilotRun.Running } run || _turn is not null || JobsOpen || _reports.Count > 0 || _stopped) return;
        run.Idle++;
        if (run.Idle > Autopilot.MaxIdle)
        {
            AutopilotFinish(AutopilotRun.Stopped, "the coordinator stopped making progress without a final report");
            return;
        }
        _ = RunTurn(Autopilot.Nudge(run), [], "");
    }

    /// <summary>The card's Approve: a backup copy of the model in a Backup folder beside it, then the run starts.</summary>
    private async void ApproveAutopilot()
    {
        try
        {
            if (Run is not { State: AutopilotRun.Approval } run) return;
            if (_turn is not null)
            {
                Status("Wait for the coordinator to finish answering, then approve.", spin: false);
                return;
            }
            run.State = AutopilotRun.Running;
            run.Started = DateTime.UtcNow;
            run.Idle = 0;
            run.JobsBefore = _board?.Snapshot().Select(j => j.Number).DefaultIfEmpty(0).Max() ?? 0;
            ShowAutopilot();
            Status("Saving a backup copy of the model…", spin: true);
            run.Backup = await AutopilotBackup();
            Note(run.Backup.Length > 0 ? $"Autopilot started. Backup copy of the model: {run.Backup}"
                : "Autopilot started. No backup copy could be saved (the model has never been saved to a file).");
            Save();
            ShowAutopilot();
            await RunTurn(Autopilot.Approved(run), [], "");
        }
        catch (Exception ex) { Status($"The autopilot couldn't start ({ex.Message}).", spin: false); }
    }

    /// <summary>Copies the model's file into a Backup folder beside it ("Backup\Tower autopilot backup 2026-10-29 2310.rvt"); its path, or "" when it has none.</summary>
    private static async Task<string> AutopilotBackup()
    {
        string path;
        try
        {
            // Saved first, so the copy has the model as it is now (the user approved anything the goal needs).
            path = await SketchProMax.App.Dispatcher.InvokeAsync(app =>
            {
                if (app.ActiveUIDocument?.Document is not { } doc) return "";
                if (doc.PathName.Length > 0 && doc.IsModified && !doc.IsReadOnly)
                {
                    try { doc.Save(); }
                    catch (Exception) { /* the copy is then of the last save */ }
                }
                return doc.PathName;
            });
        }
        catch (Exception) { return ""; }
        if (path.Length == 0) return "";
        return await Task.Run(() =>
        {
            try
            {
                // In a Backup folder beside the model.
                var folder = Path.Combine(Path.GetDirectoryName(path) ?? "", "Backup");
                Directory.CreateDirectory(folder);
                var copy = Path.Combine(folder, Path.GetFileNameWithoutExtension(path) + " autopilot backup " + DateTime.Now.ToString("yyyy-MM-dd HHmm", System.Globalization.CultureInfo.InvariantCulture) + Path.GetExtension(path));
                File.Copy(path, copy, overwrite: true);
                return copy;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return ""; }
        });
    }

    /// <summary>Every 30 s: carry on after a pause whose limit has reset; nudge a run that went quiet.</summary>
    private void AutopilotTick()
    {
        if (Run is not { Active: true } run) return;
        var now = DateTimeOffset.UtcNow;
        if (run.State == AutopilotRun.Paused)
        {
            if (run.PausedUntil is { } until && until <= now) AutopilotResume("The usage limit has reset.");
            ShowAutopilot();
            return;
        }
        if (_turn is null && !JobsOpen && _reports.Count == 0 && DateTime.UtcNow - _autoIdleSince > TimeSpan.FromMinutes(2))
        {
            _autoIdleSince = DateTime.UtcNow;
            AutopilotNudgeIfIdle();
        }
        ShowAutopilot();
    }

    /// <summary>The usage changed: near a limit the run pauses (running jobs finish their step; no new ones start).</summary>
    private void AutopilotUsage()
    {
        if (Run is not { State: AutopilotRun.Running } run) return;
        var resumeWeekly = true;
        try { resumeWeekly = Settings.AgentSettings.Load().AutopilotResumeWeekly; } catch (Exception) { }
        var (pause, until, why) = Autopilot.ShouldPause(_usage, DateTimeOffset.UtcNow, resumeWeekly);
        if (!pause) return;
        run.State = AutopilotRun.Paused;
        run.PausedUntil = until;
        run.PauseWhy = why;
        AutopilotLog(AutopilotEntry.Paused, why + (until is { } u ? $" Paused until {u.ToLocalTime():HH:mm}." : " Paused until you press Resume."));
        Note("Autopilot paused: " + why + (until is { } at ? $" It carries on at {at.ToLocalTime():HH:mm}, when the limit resets." : " Press Resume to carry on."));
        Save();
        ShowAutopilot();
    }

    private void AutopilotResume(string why)
    {
        if (Run is not { State: AutopilotRun.Paused } run) return;
        run.State = AutopilotRun.Running;
        run.PausedUntil = null;
        run.Idle = 0;
        AutopilotLog(AutopilotEntry.Resumed, why);
        Save();
        ShowAutopilot();
        PumpJobs();
        if (_turn is null) _ = RunTurn(Autopilot.Resume(run, why), [], "");
    }

    /// <summary>Stop on the banner: the open jobs stop and the report is written.</summary>
    private void AutopilotStop(string why)
    {
        if (Run is not { Active: true }) return;
        foreach (var job in _board?.Snapshot().Where(j => j.Active || j.State == JobBoard.Queued).ToList() ?? []) StopJob(job.Number);
        AutopilotFinish(AutopilotRun.Stopped, why);
    }

    private void AutopilotFinish(string state, string why)
    {
        if (Run is not { } run) return;
        run.State = state;
        run.PauseWhy = why;
        run.Ended = DateTime.UtcNow;
        Autopilot.Unattended = false;
        Add(new ChatItem { Kind = "assistant", Text = Autopilot.Report(run, _board?.Snapshot() ?? [], DateTime.UtcNow) });
        Save();
        ShowAutopilot();
    }

    /// <summary>Something answered or allowed for the user while away: kept in the run's log for the report.</summary>
    private void AutopilotLog(string kind, string text)
    {
        if (Run is not { } run) return;
        run.Log.Add(new AutopilotEntry { Time = DateTime.UtcNow, Kind = kind, Text = text });
        if (run.Log.Count > 500) run.Log.RemoveRange(0, run.Log.Count - 500);
        SaveSoon();
    }

    /// <summary>The plan's card line: Approve while the plan waits for it.</summary>
    private UIElement AutopilotApproveLine(ChatItem item)
    {
        var panel = new StackPanel { Margin = new Thickness(0, ChatUi.S3, 0, ChatUi.S1) };
        panel.Children.Add(NoteLine(item));
        if (Run is not { State: AutopilotRun.Approval } || !ReferenceEquals(_conv.Items.LastOrDefault(i => i.Name == AutopilotApprove), item)) return panel;
        var approve = new Button { Content = "Approve and start", Margin = new Thickness(ChatUi.S4, ChatUi.S2, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip = "Starts the autopilot: a backup copy of the model is saved, then the coordinator and workers carry on without asking you." };
        approve.SetResourceReference(StyleProperty, "SpmChatPrimary");
        AutomationProperties.SetName(approve, "Approve the plan and start the autopilot");
        approve.Click += (_, _) =>
        {
            approve.IsEnabled = false;
            ApproveAutopilot();
        };
        panel.Children.Add(approve);
        return panel;
    }
}
