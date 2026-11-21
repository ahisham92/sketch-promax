using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Threading;
using SketchProMax.UI;

namespace SketchProMax.Chat;

/// <summary>
/// Two buttons under the message box (the owner, 19 Nov 2026: "let the away mode be a button instead of saying", "a
/// button pausing everything so that I can sync or update, then I press continue"):
/// Away (Chat.Away: nothing pops up, Revit's pop-ups during the jobs' work are answered, a command left doing nothing is
/// ended) and Pause all / Continue (Chat.JobPause). Pause all ends each running job's process as soon as it has nothing
/// in Revit (never in the middle of a change), starts nothing new, and leaves Revit free for a sync or an update;
/// Continue sends every paused job on from where it stopped. The main chat keeps answering while paused.
/// </summary>
internal sealed partial class ChatPane
{
    private readonly TextBlock _pauseLabel = new() { FontSize = ChatUi.SmallSize, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _awayLabel = new() { FontSize = ChatUi.SmallSize, VerticalAlignment = VerticalAlignment.Center };
    private readonly Ellipse _awayDot = new() { Width = 7, Height = 7, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
    private Button? _pausePill, _awayPill;
    private DispatcherTimer? _pauseTimer;

    private Button PausePill()
    {
        var pill = new Button { Content = _pauseLabel, Padding = new Thickness(ChatUi.S1, 0, ChatUi.S1, 0), Margin = new Thickness(0, 0, ChatUi.S2, 0), VerticalAlignment = VerticalAlignment.Center };
        pill.SetResourceReference(StyleProperty, "SpmChatGhost");
        pill.Click += (_, _) =>
        {
            if (JobPause.On) ContinueAll(byUser: true);
            else PauseAll(JobPause.ForUser);
        };
        JobPause.Changed += () => Dispatcher.BeginInvoke(new Action(ShowPause));
        _pausePill = pill;
        _pauseTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _pauseTimer.Tick += (_, _) => PauseTick();
        _pauseTimer.Start();
        ShowPause();
        return pill;
    }

    private Button AwayPill()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_awayDot);
        row.Children.Add(_awayLabel);
        var pill = new Button { Content = row, Padding = new Thickness(ChatUi.S1, 0, ChatUi.S1, 0), Margin = new Thickness(0, 0, ChatUi.S2, 0), VerticalAlignment = VerticalAlignment.Center };
        pill.SetResourceReference(StyleProperty, "SpmChatGhost");
        pill.Click += (_, _) =>
        {
            var on = !Away.On;
            if (!on && Away.Take() is { Length: > 0 } meanwhile) Note(meanwhile);
            Away.Set(on, DateTime.UtcNow);
            var text = on
                ? $"Away mode until {Away.Until.ToLocalTime():HH:mm}: nothing pops up. Revit's pop-ups during the jobs' work are answered (Cancel, else No, else OK), " +
                  "a Revit command left doing nothing is ended, steps only you can do are skipped and risky changes (deletes) wait for you. Click Away again when you're back."
                : "Away mode is off: pop-ups and questions show again.";
            Note(text);
            Status(text, spin: false);
            Save();
        };
        Away.Changed += () => Dispatcher.BeginInvoke(new Action(ShowAway));
        _awayPill = pill;
        ShowAway();
        return pill;
    }

    private void ShowAway()
    {
        if (_awayPill is null) return;
        var on = Away.On;
        _awayLabel.Text = on ? $"Away until {Away.Until.ToLocalTime():HH:mm}" : "Away: off";
        Brand.Fg(_awayLabel, on ? Brand.K.Text : Brand.K.Muted);
        _awayDot.SetResourceReference(Shape.FillProperty, on ? Brand.K.WarningText : Brand.K.Muted);
        var tip = on
            ? "On: nothing pops up and the jobs carry on by themselves. Click when you're back."
            : "Off. Click before you leave Revit: nothing pops up, Revit's pop-ups during the jobs' work are answered and a command left open is ended, for up to 12 hours.";
        _awayPill.ToolTip = tip;
        AutomationProperties.SetName(_awayPill, _awayLabel.Text);
        AutomationProperties.SetHelpText(_awayPill, tip);
    }

    private void ShowPause()
    {
        if (_pausePill is null) return;
        var running = Sessions.Sum(s => s.Runs.Count);
        var paused = Sessions.Sum(s => s.Board?.PausedJobs().Count ?? 0);
        var open = Sessions.Sum(s => s.Board?.OpenCount ?? 0);
        string text, tip;
        if (JobPause.On && running > 0)
        {
            text = $"Pausing… ({running} finishing a step)";
            tip = "Each job stops as soon as it has nothing running in Revit. Click to carry on instead.";
        }
        else if (JobPause.On)
        {
            text = paused > 0 ? $"Continue ({paused} paused)" : "Continue";
            tip = JobPause.Why == JobPause.ForUpdate
                ? "Paused so the update goes in; they carry on by themselves right after. Click to carry on now."
                : "Everything is paused: Revit is free to sync or update. Click to carry on from where each job stopped.";
        }
        else
        {
            text = "Pause all";
            tip = "Pauses every job once its current step in Revit is done, so you can sync or update. Continue carries on from there; nothing is lost.";
        }
        _pauseLabel.Text = text;
        Brand.Fg(_pauseLabel, JobPause.On ? Brand.K.Link : Brand.K.Muted);
        _pausePill.Visibility = JobPause.On || running > 0 || open > 0 ? Visibility.Visible : Visibility.Collapsed;
        _pausePill.ToolTip = tip;
        AutomationProperties.SetName(_pausePill, text);
        AutomationProperties.SetHelpText(_pausePill, tip);
    }

    /// <summary>For the update window's "Pause jobs and update now": true when jobs are working or waiting to start.</summary>
    public static bool JobsWorking => _instance is { } pane && pane.Sessions.Any(s => s.Runs.Count > 0 || (s.Board?.OpenCount ?? 0) > 0);

    /// <summary>The update window's "Pause jobs and update now". UI thread.</summary>
    public static void PauseForUpdate()
    {
        if (_instance is not { } pane) return;
        pane.Note("The jobs pause so the update goes in now; they carry on by themselves right after. Nothing is lost.");
        pane.PauseAll(JobPause.ForUpdate);
    }

    private void PauseAll(string why)
    {
        JobPause.Pause(why, DateTime.UtcNow);
        if (!JobPause.On) return;
        if (why == JobPause.ForUser)
        {
            var text = "Pausing all jobs: each stops as soon as its current step in Revit is done, and nothing new starts. " +
                       "Sync or update when it says Continue; press Continue to carry on from where each stopped.";
            Note(text);
            Status(text, spin: false);
        }
        Tools.ActivityLog.Note("jobs_paused", why);
        PauseTick();
        Save();
    }

    private void ContinueAll(bool byUser)
    {
        var why = JobPause.Why;
        JobPause.Continue();
        var now = DateTime.UtcNow;
        var count = 0;
        foreach (var s in Sessions.ToList())
            In(s, () =>
            {
                if (_board is not { } board) return;
                // By itself (the update is in, or waited too long): only the jobs paused for the update; Continue: all.
                foreach (var job in board.PausedJobs(forUpdateOnly: !byUser))
                {
                    board.Message(job.Number, JobPause.CarryOn, now);
                    count++;
                }
                JobsChanged();
                PumpJobs();
            });
        Tools.ActivityLog.Note("jobs_continued", $"{(byUser ? "by the user" : why)}: {count} job(s)");
        if (byUser || count > 0)
        {
            var text = count == 0 ? "Carrying on: jobs start again." : $"Carrying on: {count} job{(count == 1 ? "" : "s")} pick up from where {(count == 1 ? "it" : "they")} stopped.";
            Note(text);
            Status(text, spin: false);
        }
        ShowPause();
        Save();
    }

    /// <summary>
    /// Every second: while paused, each job with nothing in Revit (no call of its own, nothing running or queued in
    /// Revit) has its process ended and is set aside as paused. Paused for an update: once it is in (nothing newer is
    /// staged any more, so this is the new version) or after JobPause.UpdateGiveUp, the jobs carry on by themselves.
    /// </summary>
    private void PauseTick()
    {
        if (_sample) return;
        var now = DateTime.UtcNow;
        if (JobPause.On && JobPause.Why == JobPause.ForUpdate)
        {
            string? staged;
            try { staged = Update.Updater.Staged; }
            catch (Exception) { staged = null; }
            if (staged is null)
            {
                ContinueAll(byUser: false);
                return;
            }
            if (JobPause.UpdateTooLong(now))
            {
                Note($"Sketch ProMax {staged} couldn't go in yet (Revit was never free long enough), so the jobs carry on without it; it is tried again in an hour.");
                foreach (var s in Sessions) s.UpdateRetryAt = now.AddHours(1);
                ContinueAll(byUser: false);
                return;
            }
        }
        if (JobPause.On)
        {
            var ended = false;
            foreach (var s in Sessions.ToList())
                In(s, () =>
                {
                    foreach (var run in _runs.Values.ToList())
                    {
                        if (JobPause.Calling(run.Job.Key) || App.RevitWorking) continue;
                        EndRun(run, kill: true);
                        JobPause.TookRefusal(run.Job.Key); // set aside here: its refusal is spent
                        _board?.Pause(run.Job.Number, now);
                        ShowJobCard(run.Job.Number);
                        ended = true;
                    }
                });
            if (ended)
            {
                Save();
                if (Sessions.All(s => s.Runs.Count == 0) && JobPause.Why == JobPause.ForUser)
                    Status("All jobs are paused: Revit is free to sync or update. Press Continue to carry on.", spin: false);
            }
        }
        ShowPause();
    }
}
