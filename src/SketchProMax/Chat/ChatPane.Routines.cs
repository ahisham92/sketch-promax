using System.Windows.Threading;

namespace SketchProMax.Chat;

/// <summary>
/// Scheduled routines (Routines): once a minute the pane looks for a routine whose time has come, or whose run was
/// missed while Revit or the PC was off (caught up once, at the next chance), and runs it as a job of a chat With
/// coordinator: the chat shown when it is one, else a new one when the pane is free. A run waits while a model it needs
/// isn't open, an autopilot runs, or the pane is busy with a Simple chat's answer. Each run is kept in the routine's
/// history with its outcome. The window (New chat › Routines) adds, edits and runs them.
/// </summary>
internal sealed partial class ChatPane
{
    private DispatcherTimer? _routineTimer;

    /// <summary>Revit opened a model: with routines set up, the chat is made (unseen until opened) so they can run and catch up.</summary>
    internal static void WakeForRoutines()
    {
        try
        {
            if (_instance is not null || !Routines.Load(ClaudeCli.Folder).Any(r => r.On)) return;
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                try { Get(); }
                catch (Exception) { /* the chat is made when it is opened; the routine catches up then */ }
            }));
        }
        catch (Exception) { /* routines are never worth a problem while a model opens */ }
    }

    private void StartRoutineClock()
    {
        if (_sample || _routineTimer is not null) return;
        _routineTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(45) };
        _routineTimer.Tick += (_, _) =>
        {
            _routineTimer.Interval = TimeSpan.FromMinutes(1);
            try { RoutineTick(); }
            catch (Exception) { /* on Revit's UI thread: a routine that can't start tries again next minute */ }
        };
        _routineTimer.Start();
    }

    private void RoutineTick()
    {
        if (_stopped || _sample || _setup != Setup.Ready || _install is null || !ClaudeCli.JobsAvailable || _modelKey.Length == 0) return;
        var folder = ClaudeCli.Folder;
        var list = Routines.Load(folder);
        var now = DateTime.Now;
        foreach (var routine in list)
        {
            if (Routines.Due(routine, now) is not { } due || !RoutineModelFits(routine)) continue;
            if (RunRoutine(routine, due, now)) Routines.Save(folder, list);
            return; // one a minute, the next goes after
        }
    }

    private static bool RoutineModelFits(Routine routine)
    {
        static string Bare(string title) => title.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase) ? title.Substring(0, title.Length - 4) : title;
        return routine.Model.Trim().Length == 0 || string.Equals(Bare(routine.Model.Trim()), Bare(_modelTitle), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Runs <paramref name="routine"/> for its time <paramref name="due"/> now, as a job; false when the pane can't take it yet.</summary>
    private bool RunRoutine(Routine routine, DateTime due, DateTime now)
    {
        if (Run is { Active: true }) return false; // an autopilot's chat isn't disturbed
        if (!IsCoordinator || _board is null)
        {
            // A new chat With coordinator, only when the pane is free (nothing answering, no job open).
            if (_turn is not null || JobsOpen) return false;
            NewChat(ChatTypes.Coordinator);
            if (!IsCoordinator || _board is null) return false;
            _conv.Name = "Routine · " + routine.Name;
        }
        var catchUp = now - due > Routines.Late;
        var job = _board.Start("Routine: " + routine.Name, Routines.JobTask(routine, due, catchUp), DateTime.UtcNow, force: true, out _);
        if (job is null) return false;
        job.Routine = routine.Id;
        ChatJobs.Assign(job, null);
        Routines.Started(routine, due, DateTime.UtcNow, now, _conv.Id, ChatHistory.NameFor(_conv), job.Number, _modelTitle);
        Note($"Routine \"{routine.Name}\" started as job {job.Number}" +
             (catchUp ? $", catching up the run of {due:ddd HH:mm} missed while Revit was closed or busy." : "."));
        ShowJobCards();
        PumpJobs();
        Save();
        return true;
    }

    /// <summary>A job that is a routine's run ended (done, a problem, stopped, paused): its history says how.</summary>
    private void RoutineEnded(ChatJob job)
    {
        if (job.Routine.Length == 0 || _sample) return;
        var state = job.OverBudget ? JobBoard.Stopped : job.State;
        var summary = (job.Summary + (JobReview.ReportLine(job) is { Length: > 0 } check ? "\n\n" + check : "")).Trim();
        var folder = ClaudeCli.Folder;
        var chat = _conv.Id;
        var id = job.Routine;
        var number = job.Number;
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var list = Routines.Load(folder);
                if (Routines.Ended(list, id, chat, number, state, summary, DateTime.UtcNow)) Routines.Save(folder, list);
            }
            catch (Exception) { /* the history keeps "running" for that run */ }
        });
    }

    /// <summary>The Routines window: add, edit, switch off and run routines, and see each one's runs.</summary>
    private void OpenRoutines()
    {
        var window = new UI.RoutinesWindow(ClaudeCli.Folder, _modelTitle);
        window.RunNow += id =>
        {
            var list = Routines.Load(ClaudeCli.Folder);
            if (list.FirstOrDefault(r => r.Id == id) is not { } routine) return "That routine is gone.";
            if (_setup != Setup.Ready || _install is null) return "The chat isn't ready yet (installed and signed in). Open Chat first.";
            if (_modelKey.Length == 0) return "Open a model first.";
            if (!RoutineModelFits(routine)) return $"It runs on {routine.Model}; open that model first.";
            // Run now: it counts as the run for now, so the next one is at its next time.
            var due = DateTime.Now;
            if (!RunRoutine(routine, due, due)) return "The chat is busy (an answer, open jobs or an autopilot). Try again when it is free.";
            Routines.Save(ClaudeCli.Folder, list);
            return null;
        };
        window.OpenRun += (chatId, job) =>
        {
            var conv = ChatStore.All().FirstOrDefault(c => c.Id == chatId);
            if (conv is null) return;
            if (!string.Equals(conv.File, _conv.File, StringComparison.OrdinalIgnoreCase)) ((IHistoryHost)this).OpenChat(ChatHistory.Summarize(conv));
            if (string.Equals(conv.Id, _conv.Id, StringComparison.Ordinal) && _board?.Find(job) is not null) OpenJobView(job);
        };
        window.Show();
    }
}
