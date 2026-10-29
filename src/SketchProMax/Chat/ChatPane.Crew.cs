using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace SketchProMax.Chat;

/// <summary>
/// The crew strip (CrewStrip) over the large chat With coordinator: who is working, drawn as a coordinator handing
/// sheets to workers at computers, and one Revit station that takes changes one at a time. Only in the large window and
/// only for a chat With coordinator; the side pane and Simple chats never show it. It follows the jobs through
/// ShowSwitcher, which runs on every change to them.
/// </summary>
internal sealed partial class ChatPane
{
    private CrewStrip? _crew;
    private readonly CrewPlan _crewPlan = new();
    private JobBoard? _crewBoard;
    private DispatcherTimer? _crewLinger;

    private bool CrewShown => IsCoordinator && !_sample && !_stopped;

    /// <summary>Shows the jobs as workers (or hides the strip when it doesn't belong).</summary>
    private void ShowCrew(List<ChatJob> jobs)
    {
        if (!CrewShown || _switcherHome is null)
        {
            if (_crew is not null && _crew.Visibility == Visibility.Visible)
            {
                _crew.Visibility = Visibility.Collapsed;
                _crew.Clear();
            }
            _crewPlan.Reset();
            _crewLinger?.Stop();
            return;
        }
        if (_crew is null)
        {
            _crew = new CrewStrip();
            _crew.WorkerPicked += ShowWorkerJobs;
            _crew.CoordinatorPicked += _ => ShowCoordinator(focus: true);
            _crew.WorkersChanged += SetWorkers;
            CrewStrip.NamesChanged += () =>
            {
                _switchShown = ""; // the jobs column names the workers too
                if (_board is not null && !_stopped) ShowSwitcher();
            };
            // Under the header, over the conversation, the whole width beside the jobs column.
            SetDock(_crew, Dock.Top);
            var at = Children.IndexOf(_switcherHome);
            Children.Insert(at < 0 ? Children.Count - 1 : at, _crew);
        }
        if (!ReferenceEquals(_crewBoard, _board))
        {
            // Another chat: its workers sit down without a show.
            _crewBoard = _board;
            _crewPlan.Reset();
            _crew.Clear();
        }
        _crew.Visibility = Visibility.Visible;
        _crew.Compact = _big is null;
        var workers = ChatJobs.Limit;
        var events = _crewPlan.Update(jobs, DateTime.UtcNow, JobBoard.ActivityOf, workers);
        _crew.Show(_crewPlan.Seats, events, workers, _crewPlan.Unassigned);
        // Each job keeps its worker with the chat, so the worker's list of jobs (and the Jobs column) still says who did it.
        var kept = false;
        foreach (var seat in _crewPlan.Seats)
            if (jobs.FirstOrDefault(j => j.Number == seat.Number) is { } job && job.Worker != seat.Slot)
            {
                _board?.SetWorker(seat.Number, seat.Slot);
                job.Worker = seat.Slot;
                kept = true;
            }
        if (kept) SaveSoon();
        // A finished job's worker leaves after a few seconds even when nothing else changes meanwhile.
        if (_crewPlan.Lingering)
        {
            _crewLinger ??= new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
            _crewLinger.Tick -= OnCrewLinger;
            _crewLinger.Tick += OnCrewLinger;
            _crewLinger.Start();
        }
        else _crewLinger?.Stop();
    }

    /// <summary>+ or − on the strip: that many workers from now on (Settings: jobs at once); a new one takes a waiting job at once.</summary>
    private void SetWorkers(int workers)
    {
        try
        {
            var settings = Settings.AgentSettings.Load();
            settings.ChatJobLimit = JobBoard.ClampLimit(workers);
            settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowStatus("Couldn't change the number of workers: " + ex.Message);
            return;
        }
        PumpJobs();
        _switchShown = "";
        if (_board is not null) ShowSwitcher();
    }

    private void OnCrewLinger(object? sender, EventArgs e)
    {
        if (!_crewPlan.Lingering || _board is null) _crewLinger?.Stop();
        else ShowCrew(_board.Snapshot());
    }

    /// <summary>The worker who has (or had) job <paramref name="number"/> ("Nova"), or null when none did or the strip isn't shown.</summary>
    private string? CrewWorker(ChatJob job) => !CrewShown ? null : _crewPlan.WorkerOf(job.Number, CrewStrip.Names) ?? (job.Worker >= 0 ? CrewStrip.Names.Worker(job.Worker) : null);

    /// <summary>A worker was picked in the strip: their jobs (the one now first, then the ones they did), each opening its chat.</summary>
    private void ShowWorkerJobs(int slot, Button anchor)
    {
        var name = CrewStrip.Names.Worker(slot);
        var jobs = (_board?.Snapshot() ?? []).Where(j => j.Worker == slot).OrderByDescending(j => j.Active).ThenByDescending(j => j.Number).ToList();
        var rows = jobs.Select(j => ($"Job {j.Number} · {JobBoard.OneLine(j.Title, 48)}", JobBoard.StateLabel(j), _viewJob == j.Number, (Action)(() => OpenJobView(j.Number)))).ToList();
        if (rows.Count == 0) rows.Add(("No jobs yet", $"{CrewStrip.Names.Coordinator} gives {name} the next job when one comes.", false, () => { }));
        ToggleChoice(anchor, new Choice($"{name}'s jobs", rows));
    }

    /// <summary>The main chat runs <paramref name="tool"/> (null: its turn ended): the coordinator in the strip shows it.</summary>
    private void CrewCoordinatorDoes(string? tool)
    {
        if (_crew is not null) _crew.CoordinatorDoes(CrewShown ? tool : null);
    }

    /// <summary>You sent the coordinator a message: a sheet to the coordinator, who nods.</summary>
    private void CrewHeard()
    {
        if (_viewJob is null && _crew is { IsVisible: true }) _crew.Heard();
    }
}
