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
        if (AutopilotHolds()) return; // one running here already
        NewChat(ChatTypes.Coordinator);
        if (_conv.Type != ChatTypes.Coordinator) return; // the new chat couldn't be opened (the status line says why)
        _conv.Autopilot = new AutopilotRun();
        Note("Autopilot: type the goal and send. The coordinator answers with a plan; once you approve it, he and the " +
             "workers carry on without asking you anything until the goal is met, a usage limit pauses them (they carry on " +
             "when it resets) or you press Stop. A backup copy of the model is saved first, in a Backup folder beside it.");
        Save();
        ShowAutopilot();
    }

    /// <summary>
    /// The autopilot runs (or is paused) in the chat shown: it can't be left, since its clock (progress updates to the phone, the review
    /// team, the connection watch, carrying on after a pause) runs only while its chat is shown. True when it refused.
    /// </summary>
    private bool AutopilotHolds()
    {
        if (_sample || Run is not { Active: true }) return false;
        Status("The autopilot runs in this chat, so it stays open: leaving it would stop its progress updates and checks. Press Stop on its banner first to open another chat.", spin: false);
        return true;
    }

    /// <summary>The banner, the unattended switch and the timer, for the chat shown now.</summary>
    private void ShowAutopilot()
    {
        if (!_autoHooked)
        {
            _autoHooked = true;
            Autopilot.Logged += (kind, text) => Dispatcher.BeginInvoke(new Action(() => AutopilotLog(kind, text)));
            AskUser.Logged += text => Dispatcher.BeginInvoke(new Action(() => AutopilotLog(AutopilotEntry.Asked, text)));
            AskUser.Answered += q => Dispatcher.BeginInvoke(new Action(() => PhoneAnswered(q)));
            AskUser.UserWrote += text => Dispatcher.BeginInvoke(new Action(() => FromPhone(text)));
            AskUser.Command += name => Dispatcher.BeginInvoke(new Action(() => PhoneCommand(name)));
            AskUser.Listen(); // a connected phone is read from now on: the user can write to the chat from it
        }
        var run = _sample ? null : Run;
        Autopilot.Here = run is { Active: true };
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
        if (run.Active && run.State != AutopilotRun.Approval) StartWatchdog();
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
        // A paused run: whatever the user types carries it on, with their message as the coordinator's next turn.
        if (Run is { State: AutopilotRun.Paused } && text.Length > 0)
        {
            AutopilotResume("You said to carry on.", turn: false);
            return false;
        }
        // A run that ended (or that the coordinator stopped short): writing in its chat starts it again, so the rules,
        // the free-worker rule and the review team apply to what the user asks now.
        if (Run is { State: AutopilotRun.Done or AutopilotRun.Stopped } ended && ended.Started != default && text.Length > 0)
        {
            AutopilotRestart(ended);
            return false;
        }
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
        PhoneTurnEnded(ended, stopped);
        if (Run is not { } run || ended is null || ended.Planning || ended.Shrinking) return;
        var last = _conv.Items.LastOrDefault(i => i.Kind == "assistant")?.Text ?? "";
        if (run.State == AutopilotRun.Planning)
        {
            // Reading jobs still out: their reports bring the next turn, and the plan comes once they all reported.
            if (JobsOpen || _reports.Count > 0) return;
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
            // Said to be met: the review team checks it in the models and files before the run ends.
            if (!JobsOpen) AutopilotReview(last);
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
            // The coordinator stopped short: the review team says what is still to do, and it goes back to him.
            if (GoalReview.MoreRounds(run)) AutopilotReview(_conv.Items.LastOrDefault(i => i.Kind == "assistant")?.Text ?? "");
            else AutopilotFinish(AutopilotRun.Stopped, "the coordinator stopped making progress without a final report");
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
            // The reading jobs before approval stay the run's (their findings are what it builds on), and may change now.
            run.JobsBefore = 0;
            _board?.AllowChanges();
            ShowAutopilot();
            Status("Saving a backup copy of the model…", spin: true);
            var (model, copy) = await AutopilotBackup();
            run.Backup = copy;
            // Other Revit windows the run reaches (a documentation model linking this one) answer for you too, and back
            // up their model before its first change.
            AutopilotShare.Begin(Autopilot.RunId(_conv.Id, run), copy.Length > 0 ? model : "", DateTime.UtcNow);
            AskUser.Goal = run.Goal;
            AskUser.Listen();
            Note(run.Backup.Length > 0 ? $"Autopilot started. Backup copy of the model: {run.Backup}"
                : "Autopilot started. No backup copy could be saved (the model has never been saved to a file).");
            Save();
            ShowAutopilot();
            await RunTurn(Autopilot.Approved(run), [], "");
        }
        catch (Exception ex) { Status($"The autopilot couldn't start ({ex.Message}).", spin: false); }
    }

    /// <summary>Saves the model and copies it into a Backup folder beside it: the model's file and the copy's path ("" when none).</summary>
    private static async Task<(string Model, string Copy)> AutopilotBackup()
    {
        string path;
        try
        {
            // Saved first, so the copy has the model as it is now (the user approved anything the goal needs).
            path = await SketchProMax.App.Dispatcher.InvokeAsync(app =>
            {
                if (app.ActiveUIDocument?.Document is not { } doc) return "";
                Revit.AutopilotBackups.SaveFirst(doc);
                return doc.PathName;
            });
        }
        catch (Exception) { return ("", ""); }
        if (path.Length == 0) return ("", "");
        return (path, await Task.Run(() => Revit.AutopilotBackups.Copy(path)));
    }

    /// <summary>Every 30 s: carry on after a pause whose limit has reset; nudge a run that went quiet.</summary>
    private void AutopilotTick()
    {
        if (Run is not { Active: true } run) return;
        var now = DateTimeOffset.UtcNow;
        if (AskUser.Goal.Length == 0) AskUser.Goal = run.Goal; // a run carried on after Revit started again
        AskUser.Listen(); // the phone is read for Stop/Resume while the run is on
        AutopilotUpdateTick(run);
        // The other Revit windows know the run is still on, and what they answered goes into its log.
        if (run.State != AutopilotRun.Approval && run.Started != default) AutopilotShare.Beat(Autopilot.RunId(_conv.Id, run), DateTime.UtcNow);
        TakeSharedLog(run);
        // Progress to the phone, and what came back from it (also while paused: the user's word carries a paused run on).
        AutopilotProgress(run);
        SendLateAnswers();
        AutopilotConnection(run);
        if (run.State == AutopilotRun.Paused)
        {
            if (run.PausedUntil is { } until && until <= now) AutopilotResume("The usage limit has reset.");
            ShowAutopilot();
            return;
        }
        if (_gapsWaiting && _turn is null && _reports.Count == 0) SendGaps(run);

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
        Settings.AgentSettings settings;
        try { settings = Settings.AgentSettings.Load(); } catch (Exception) { settings = new Settings.AgentSettings(); }
        var (pause, until, why, weekCap) = Autopilot.ShouldPause(_usage, DateTimeOffset.UtcNow, settings.AutopilotResumeWeekly,
            Math.Min(1, Math.Max(0.1, settings.AutopilotFiveHourMax / 100.0)), JobBudget.Share(settings.AutopilotWeekShare), run.WeekUsed);
        if (!pause) return;
        run.State = AutopilotRun.Paused;
        run.PausedUntil = until;
        run.PauseWhy = why;
        run.WeekCap = weekCap;
        AutopilotLog(AutopilotEntry.Paused, why + (until is { } u ? $" Paused until {u.ToLocalTime():ddd HH:mm}." : " Paused until you press Resume or type \"continue\"."));
        Note("Autopilot paused: " + why + (until is { } at ? $" It carries on at {at.ToLocalTime():ddd HH:mm}, when the limit resets." : " Press Resume, or type \"continue\", to carry on."));
        Save();
        ShowAutopilot();
    }

    /// <summary>The run's use of the week (its own share, Settings): the weekly figure's rises while it runs.</summary>
    private double? _weekSeen;
    private DateTimeOffset? _weekResets;

    private void AutopilotWeekUsage(PlanUsage reported)
    {
        var week = reported.Windows.FirstOrDefault(w => w.Key == PlanUsage.Weekly);
        var rise = JobBudget.Rise(ref _weekSeen, week?.Used, JobBudget.NewWindow(ref _weekResets, week?.ResetsAt));
        if (rise > 0 && Run is { Active: true } run) run.WeekUsed += rise;
    }

    private void AutopilotResume(string why, bool turn = true)
    {
        if (Run is not { State: AutopilotRun.Paused } run) return;
        run.State = AutopilotRun.Running;
        run.PausedUntil = null;
        run.Offline = "";
        run.Idle = 0;
        // A pause for the run's share of the week: carrying on starts its share again (a new week, or the user's say).
        if (run.WeekCap) run.WeekUsed = 0;
        run.WeekCap = false;
        AutopilotLog(AutopilotEntry.Resumed, why);
        Save();
        ShowAutopilot();
        PumpJobs();
        if (turn && _turn is null) _ = RunTurn(Autopilot.Resume(run, why), [], "");
    }

    private readonly ConnectionWatch _connection = new();
    private bool _probing;

    /// <summary>
    /// Every 30 s while a run is on: is the internet there, and the company network the central model is on? When either
    /// drops (two probes in a row) the run pauses: running jobs finish or fail their step, nothing new starts. When it is
    /// back the run carries on by itself, and the jobs the drop cut off are told to check the model and carry on.
    /// </summary>
    private void AutopilotConnection(AutopilotRun run)
    {
        if (_probing) return;
        _probing = true;
        var central = _modelKey;
        _ = Task.Run(async () => (internet: await ConnectionProbe.InternetAsync().ConfigureAwait(false), network: await ConnectionProbe.NetworkAsync(central).ConfigureAwait(false)))
            .ContinueWith(t => Dispatcher.BeginInvoke(new Action(() =>
            {
                _probing = false;
                if (t.Status != TaskStatus.RanToCompletion || Run is not { Active: true } now || !ReferenceEquals(now, run)) return;
                try { ConnectionChanged(run, t.Result.internet, t.Result.network); }
                catch (Exception) { /* the next probe tries again */ }
            })), TaskScheduler.Default);
    }

    private void ConnectionChanged(AutopilotRun run, bool internet, bool? network)
    {
        _connection.Record(internet, network);
        var down = _connection.Down;
        if (down.Length > 0)
        {
            if (run.State == AutopilotRun.Running)
            {
                // Only a running run: a usage pause stays as it is (this one takes over if the limit resets while still offline).
                run.State = AutopilotRun.Paused;
                run.PausedUntil = null;
                run.Offline = down;
                run.OfflineSince = DateTime.UtcNow;
                run.PauseWhy = ConnectionWatch.Why(down);
                var text = $"Paused: {run.PauseWhy}. It carries on by itself when the connection is back.";
                AutopilotLog(AutopilotEntry.Paused, text);
                Note("Autopilot " + char.ToLowerInvariant(text[0]) + text.Substring(1));
                Save();
                ShowAutopilot();
            }
            else if (ConnectionWatch.IsConnectionPause(run) && run.Offline != down)
            {
                run.Offline = down;
                run.PauseWhy = ConnectionWatch.Why(down);
                Save();
                ShowAutopilot();
            }
            return;
        }
        // Back only on a probe that worked (after Revit started again, the watch knows nothing yet).
        if (!ConnectionWatch.IsConnectionPause(run) || !internet || network == false) return;
        var back = ConnectionWatch.BackLine(run.Offline, run.OfflineSince, DateTime.UtcNow);
        var cut = _board is { } board ? ConnectionWatch.CutOff(board.Snapshot(), run.JobsBefore, run.OfflineSince) : [];
        foreach (var job in cut) _board!.Message(job.Number, ConnectionWatch.CarryOnLine, DateTime.UtcNow);
        if (cut.Count > 0)
        {
            back += $" Job{(cut.Count == 1 ? "" : "s")} {string.Join(", ", cut.Select(j => j.Number))}, cut off by the drop, " +
                    $"{(cut.Count == 1 ? "was" : "were")} told to check the model and carry on.";
            JobsChanged();
        }
        Note(back);
        AutopilotResume(back);
    }

    private bool _gapsWaiting;
    private readonly List<UserQuestion> _lateAnswers = [];

    /// <summary>
    /// The user answered a question on the phone after the asking job stopped waiting for it (AskUser): the job gets it as a
    /// message (it reopens if it had finished) to put its work right; the coordinator's own question comes as its next turn.
    /// </summary>
    private void PhoneAnswered(UserQuestion q)
    {
        AutopilotLog(AutopilotEntry.Asked, $"You answered Q{q.Id} on your phone: {q.Answer}");
        if (q.JobKey.Length > 0 && _board?.FindKey(q.JobKey) is { } job)
        {
            _board.Message(job.Number, AskRules.LateLine(q), DateTime.UtcNow);
            JobsChanged();
            return;
        }
        _lateAnswers.Add(q);
        SendLateAnswers();
    }

    private void SendLateAnswers()
    {
        if (_turn is not null || Run is not { State: AutopilotRun.Running or AutopilotRun.Paused }) return;
        if (_fromPhone.Count > 0)
        {
            var asks = string.Join("\n\n", _fromPhone);
            _fromPhone.Clear();
            _ = RunTurn(AskRules.FromPhone(asks), [], "");
            return;
        }
        if (_lateAnswers.Count == 0 || Run is not { State: AutopilotRun.Running }) return;
        var text = "[Autopilot, not typed by the user] " + string.Join("\n", _lateAnswers.Select(AskRules.LateLine));
        _lateAnswers.Clear();
        _ = RunTurn(text, [], "");
    }

    private readonly List<string> _fromPhone = [];

    /// <summary>
    /// The user wrote from the phone: shown in the chat as their message (from your phone). During a run the coordinator
    /// takes it in; otherwise the chat answers it as if typed here, and the answer goes back to the phone.
    /// </summary>
    private void FromPhone(string text)
    {
        if (_instance != this) return; // one chat takes it
        if (Run is not { Active: true } run)
        {
            _phoneAsks.Add(text);
            NextPhoneAsk();
            return;
        }
        const string how = "from your phone";
        Add(new ChatItem { Kind = "user", Text = text + $"\n\n({how})" });
        AutopilotLog(AutopilotEntry.Asked, $"You wrote {how}: " + (text.Length > 200 ? text.Substring(0, 199) + "…" : text));
        // A paused run: the user's word carries it on, as typing "continue" would.
        if (run.State == AutopilotRun.Paused) AutopilotResume($"You wrote {how}.", turn: false);
        _fromPhone.Add(text);
        Save();
        SendLateAnswers();
    }

    private readonly List<string> _phoneAsks = [];
    private Turn? _phoneTurn;

    /// <summary>The next message from the phone, once the chat is free: answered as a turn of its own (no brief card: nobody is here to choose).</summary>
    private void NextPhoneAsk()
    {
        if (_phoneAsks.Count == 0 || _turn is not null || _stopped || Run is { Active: true }) return;
        if (_setup != Setup.Ready || !Licensing.LicenseState.Allowed)
        {
            _phoneAsks.Clear();
            _ = AskUser.SendUpdateAsync("The chat on the PC isn't ready (it needs signing in or a license there), so your message wasn't taken in.");
            return;
        }
        var text = string.Join("\n\n", _phoneAsks);
        _phoneAsks.Clear();
        Add(new ChatItem { Kind = "user", Text = text + "\n\n(from your phone)" });
        _conv.Name = ChatHistory.NameFor(_conv);
        Save();
        ShowTitle();
        SupersedeBriefs();
        _ = RunPhoneTurn(text);
    }

    private async Task RunPhoneTurn(string text)
    {
        var run = RunTurn("[The user wrote this from their phone, away from the PC: do it as if typed here, and answer in a few plain sentences, as the answer is sent to the phone] " + text, [], "");
        _phoneTurn = _turn; // set before RunTurn's first wait
        await run;
    }

    /// <summary>A turn ended: when it answered a message from the phone, the answer goes back there; then the next one waiting.</summary>
    private void PhoneTurnEnded(Turn? ended, bool stopped)
    {
        if (ended is null || ended.Planning || ended.Shrinking) return;
        if (ReferenceEquals(ended, _phoneTurn))
        {
            _phoneTurn = null;
            var last = _conv.Items.LastOrDefault(i => i.Kind == "assistant")?.Text ?? "";
            _ = AskUser.SendUpdateAsync(AskRules.PhoneReply(last, stopped ? "stopped" : ended.Outcome));
        }
        if (_phoneAsks.Count > 0) Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(NextPhoneAsk));
    }

    /// <summary>Stop or Resume pressed on the phone (PhoneLink.Commands): as if pressed on the banner.</summary>
    private void PhoneCommand(string name)
    {
        if (Run is not { Active: true }) return;
        if (name.Equals("Stop", StringComparison.OrdinalIgnoreCase))
        {
            AutopilotLog(AutopilotEntry.Asked, "You pressed Stop on your phone.");
            AutopilotStop("you pressed Stop on your phone");
        }
        else if (name.Equals("Resume", StringComparison.OrdinalIgnoreCase)) AutopilotResume("You pressed Resume on your phone.");
    }

    private bool _updateSending;

    /// <summary>Every hour or so (Settings): a progress update to the phone; what the user writes back comes as their message.</summary>
    private void AutopilotProgress(AutopilotRun run)
    {
        if (_updateSending || run.State is not (AutopilotRun.Running or AutopilotRun.Paused)) return;
        var now = DateTime.UtcNow;
        if (run.LastUpdate == default) run.LastUpdate = run.Started;
        if (!AskRules.UpdateDue(run.LastUpdate, now, AskUser.UpdateMinutes)) return;
        var since = run.LastUpdate;
        run.LastUpdate = now;
        var body = AskRules.UpdateBody(run, _board?.Snapshot() ?? [], since, AskUser.Waiting(), now);
        _updateSending = true;
        _ = AskUser.SendUpdateAsync(body).ContinueWith(t => Dispatcher.BeginInvoke(new Action(() =>
        {
            _updateSending = false;
            var error = t.Status == TaskStatus.RanToCompletion ? t.Result : t.Exception?.GetBaseException().Message;
            AutopilotLog(AutopilotEntry.Asked, error is null ? "Sent a progress update to your phone." : $"Couldn't send the progress update to your phone ({error}).");
            Save();
        })), TaskScheduler.Default);
    }

    /// <summary>
    /// The coordinator says the goal is met (or stopped short): the review team, read-only jobs side by side, checks every
    /// item the user asked for in the models and files (GoalReview). Past the last round the run stops, listing what is
    /// still not done.
    /// </summary>
    private void AutopilotReview(string claims)
    {
        if (Run is not { State: AutopilotRun.Running } run || _board is not { } board) return;
        if (!GoalReview.MoreRounds(run))
        {
            AutopilotFinish(AutopilotRun.Stopped, $"the review team still found work not done after {GoalReview.MaxRounds} rounds");
            return;
        }
        run.ReviewRound++;
        run.Idle = 0;
        var now = DateTime.UtcNow;
        for (var part = 0; part < GoalReview.Parts.Length; part++)
        {
            var reviewer = board.StartGoalCheck(GoalReview.Parts[part].Title, GoalReview.Task(run, part, claims), run.ReviewRound, now);
            ChatJobs.Assign(reviewer, ChatAuto.Everyday);
        }
        var text = $"The coordinator says the goal is met. The review team ({GoalReview.Parts.Length} reviewers, round {run.ReviewRound}) " +
                   "is checking every item you asked for in the models and files before the run ends; anything not done goes back to the coordinator.";
        Note(text);
        AutopilotLog(AutopilotEntry.Note, text);
        Save();
        ShowJobCards();
        PumpJobs();
        ShowAutopilot();
    }

    /// <summary>A reviewer finished: once the whole round has, the run ends (all met) or the gaps go back to the coordinator.</summary>
    private void GoalCheckEnded(ChatJob reviewer)
    {
        if (Run is not { State: AutopilotRun.Running or AutopilotRun.Paused } run || _board is not { } board || reviewer.GoalCheck != run.ReviewRound) return;
        var round = board.Snapshot().Where(j => j.GoalCheck == run.ReviewRound).ToList();
        if (round.Any(j => j.Open)) return;
        var (met, gaps) = GoalReview.Combine(round);
        if (met)
        {
            run.Gaps = "";
            AutopilotLog(AutopilotEntry.Note, $"The review team checked the goal (round {run.ReviewRound}): met.");
            AutopilotFinish(AutopilotRun.Done, "");
            return;
        }
        run.Gaps = gaps;
        var count = gaps.Split('\n').Count(l => l.TrimStart().StartsWith("- ", StringComparison.Ordinal));
        AutopilotLog(AutopilotEntry.Note, $"The review team found the goal not met yet (round {run.ReviewRound}): {Math.Max(1, count)} item(s) went back to the coordinator.");
        Note($"The review team found {Math.Max(1, count)} item(s) not done yet. They went back to the coordinator, who carries on.");
        Save();
        _gapsWaiting = true;
        SendGaps(run);
    }

    /// <summary>What the review team found goes to the coordinator as soon as the chat is free (or at the next tick).</summary>
    private void SendGaps(AutopilotRun run)
    {
        if (run.State != AutopilotRun.Running || _turn is not null) return;
        _gapsWaiting = false;
        run.Idle = 0;
        _ = RunTurn(GoalReview.SendBack(run, run.Gaps), [], "");
    }

    /// <summary>The user wrote in the chat of a run that ended: it runs again, with what they wrote as the next turn.</summary>
    private void AutopilotRestart(AutopilotRun run)
    {
        TakeSharedLog(run);
        run.State = AutopilotRun.Running;
        run.Ended = default;
        run.PauseWhy = "";
        run.PausedUntil = null;
        run.Idle = 0;
        run.ReviewRound = 0;
        AutopilotShare.Begin(Autopilot.RunId(_conv.Id, run), "", DateTime.UtcNow);
        AskUser.Goal = run.Goal;
        AskUser.Listen();
        var text = "Autopilot started again with your message: the coordinator and workers carry on without asking you, and the review team checks the goal before it ends.";
        AutopilotLog(AutopilotEntry.Resumed, text);
        Note(text);
        Save();
        ShowAutopilot();
    }

    /// <summary>Stop on the banner: the open jobs stop and the report is written.</summary>
    private void AutopilotStop(string why)
    {
        if (Run is not { Active: true } run) return;
        run.State = AutopilotRun.Stopped; // first: the stopped reviewers must not send anything back
        foreach (var job in _board?.Snapshot().Where(j => j.Active || j.State == JobBoard.Queued).ToList() ?? []) StopJob(job.Number);
        AutopilotFinish(AutopilotRun.Stopped, why);
    }

    private void AutopilotFinish(string state, string why)
    {
        if (Run is not { } run) return;
        run.State = state;
        run.PauseWhy = why;
        run.Ended = DateTime.UtcNow;
        Autopilot.Here = false;
        TakeSharedLog(run);
        AutopilotShare.End(Autopilot.RunId(_conv.Id, run));
        AskUser.Clear();
        Add(new ChatItem { Kind = "assistant", Text = Autopilot.Report(run, _board?.Snapshot() ?? [], DateTime.UtcNow) });
        Save();
        ShowAutopilot();
    }

    private DateTime _updateRetryAt;

    /// <summary>
    /// Sketch ProMax updates during a run without losing work: once a newer version is ready, no new job starts and the
    /// running ones finish their step, so Revit's next quiet moment switches the version in (ChatPane.IsBusy lets queued
    /// jobs wait across it). In the new version the run carries on (<see cref="AutopilotUpdated"/>). A hold that lasts
    /// too long is let go, and tried again later.
    /// </summary>
    private void AutopilotUpdateTick(AutopilotRun run)
    {
        if (Autopilot.UpdateIn(run, Update.Updater.Current))
        {
            AutopilotUpdated(run);
            return;
        }
        if (run.State != AutopilotRun.Running) return;
        var now = DateTime.UtcNow;
        if (run.Updating.Length > 0)
        {
            if (now - run.HeldSince < Autopilot.UpdateHoldMax) return;
            AutopilotLog(AutopilotEntry.Note, $"Sketch ProMax {run.Updating} waited {Autopilot.UpdateHoldMax.TotalMinutes:0} minutes for a quiet moment; the work carried on without it, to try again in an hour.");
            run.Updating = "";
            _updateRetryAt = now.AddHours(1);
            Save();
            PumpJobs();
            return;
        }
        if (now < _updateRetryAt || Update.Updater.Staged is not { } ready) return;
        run.Updating = ready;
        run.HeldSince = now;
        AutopilotLog(AutopilotEntry.Note, $"Sketch ProMax {ready} is ready: no new job starts until it is in; the running jobs finish their step first.");
        Save();
    }

    /// <summary>The new version took over the run: the jobs the switch left waiting or stopped carry on, and the coordinator is told.</summary>
    private void AutopilotUpdated(AutopilotRun run)
    {
        var version = Update.Updater.Current.ToString();
        var since = run.HeldSince;
        run.Updating = "";
        AutopilotLog(AutopilotEntry.Resumed, $"Sketch ProMax updated to {version}; the autopilot carried on.");
        Note($"Autopilot: Sketch ProMax updated to {version}. The work carries on.");
        foreach (var job in _board?.Snapshot().Where(j => j.Number > run.JobsBefore && j.State == JobBoard.Stopped && !j.OverBudget && j.Ended >= since.AddMinutes(-1)).ToList() ?? [])
            _board!.Message(job.Number, "Sketch ProMax was updated. Carry on with your task where you left off (check in the model what is already done first).", DateTime.UtcNow);
        JobsChanged();
        Save();
        ShowAutopilot();
        if (run.State == AutopilotRun.Running && _turn is null) _ = RunTurn(Autopilot.Resume(run, $"Sketch ProMax was updated to {version}."), [], "");
    }

    private static bool _watching;

    /// <summary>
    /// While a run is on: a small watcher outside Revit (the connector program, --watch-revit) that starts Revit again on
    /// the same model if it ends without closing the normal way (a crash); the new Revit carries the run on.
    /// </summary>
    private static void StartWatchdog()
    {
        if (_watching) return;
        _watching = true;
        try
        {
            var exe = Update.ClaudeRegistration.ConnectorPath;
            using var me = System.Diagnostics.Process.GetCurrentProcess();
            var revit = me.MainModule?.FileName ?? "";
            if (!File.Exists(exe) || revit.Length == 0) return;
            var model = Revit.FreezeWatch.ModelToReopen ?? "";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, $"--watch-revit {me.Id} \"{revit}\" \"{model}\"")
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
            })?.Dispose();
        }
        catch (Exception) { /* no watcher: after a crash the run carries on when Revit is opened again */ }
    }

    private bool _recoverChecked;

    /// <summary>
    /// Once the chat is ready after Revit started: a run left behind by a Revit that went away without closing (a crash,
    /// or a restart after a hang) is taken over. Its chat opens, the jobs that were working carry on, and the coordinator
    /// is told what happened.
    /// </summary>
    private void AutopilotRecoverTick()
    {
        if (_recoverChecked || _stopped || _sample || _setup != Setup.Ready || _install is null) return;
        if (AutopilotShare.Orphan(DateTime.UtcNow) is not { } orphan)
        {
            _recoverChecked = true;
            return;
        }
        var chatId = orphan.Run.Split(':')[0];
        var conv = ChatStore.All().FirstOrDefault(c => c.Id == chatId && c.Autopilot is { Active: true });
        if (conv is null)
        {
            _recoverChecked = true;
            return;
        }
        if (!string.Equals(conv.Id, _conv.Id, StringComparison.Ordinal))
        {
            if (_turn is not null || JobsOpen) return; // tried again next minute
            ((IHistoryHost)this).OpenChat(ChatHistory.Summarize(conv));
            if (!string.Equals(_conv.Id, chatId, StringComparison.Ordinal)) return;
        }
        _recoverChecked = true;
        if (Run is not { Active: true } run) return;
        var why = orphan.Why.Length > 0 ? orphan.Why + "." : "Revit closed unexpectedly and was started again.";
        AutopilotShare.Beat(Autopilot.RunId(_conv.Id, run), DateTime.UtcNow); // this Revit runs it now
        TakeSharedLog(run);
        AutopilotLog(AutopilotEntry.Resumed, why + " The autopilot carried on.");
        Note("Autopilot: " + why + " It carries on where it was.");
        ShowAutopilot();
        // The jobs that were working when Revit went (stopped when the chat reopened) carry on.
        foreach (var job in _board?.Snapshot().Where(j => j.Number > run.JobsBefore && j.State == JobBoard.Stopped && !j.OverBudget
                                                          && j.Ended >= orphan.Beat.AddMinutes(-1)).ToList() ?? [])
            _board!.Message(job.Number, "Revit was restarted while you worked. Carry on with your task where you left off: first check in the " +
                                        "model what is already done (a change that was being applied may be missing).", DateTime.UtcNow);
        JobsChanged();
        Save();
        if (run.State == AutopilotRun.Running && _turn is null) _ = RunTurn(Autopilot.Recovered(run, why), [], "");
    }

    /// <summary>What the other Revit windows answered for this run (AutopilotShare), into its log.</summary>
    private void TakeSharedLog(AutopilotRun run)
    {
        foreach (var entry in AutopilotShare.TakeLog()) AutopilotLog(entry.Kind, entry.Text);
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
