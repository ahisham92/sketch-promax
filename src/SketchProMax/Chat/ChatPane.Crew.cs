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

    private bool CrewShown => _big is not null && IsCoordinator && !_sample && !_stopped;

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
        var events = _crewPlan.Update(jobs, DateTime.UtcNow, CrewActivity);
        _crew.Show(_crewPlan.Seats, events);
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

    private void OnCrewLinger(object? sender, EventArgs e)
    {
        if (!_crewPlan.Lingering || _board is null) _crewLinger?.Stop();
        else ShowCrew(_board.Snapshot());
    }

    /// <summary>What a running job does now: thinking, reading the model, waiting to apply or applying.</summary>
    private static string CrewActivity(ChatJob job) => "";

    /// <summary>You sent the coordinator a message: it nods.</summary>
    private void CrewHeard()
    {
        if (_viewJob is null && _crew is { IsVisible: true }) _crew.Heard();
    }
}
