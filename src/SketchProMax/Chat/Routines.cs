using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace SketchProMax.Chat;

/// <summary>One run of a routine, as its history lists it.</summary>
internal sealed class RoutineRun
{
    /// <summary>When it started (UTC), and the scheduled time it covered (local), later when it was caught up.</summary>
    public DateTime Started { get; set; }
    public DateTime Due { get; set; }
    /// <summary>It ran after its time, because Revit (or the PC) was off or busy then.</summary>
    public bool CatchUp { get; set; }
    /// <summary>running, done, failed or stopped (as its job ended).</summary>
    public string State { get; set; } = JobBoard.Running;
    public string Summary { get; set; } = "";
    public DateTime Ended { get; set; }
    /// <summary>The chat and job it ran as, to open it.</summary>
    public string ChatId { get; set; } = "";
    public string ChatTitle { get; set; } = "";
    public int Job { get; set; }
    /// <summary>The model it ran on.</summary>
    public string Model { get; set; } = "";
}

/// <summary>
/// A scheduled routine: work the crew does by itself at a set time (a nightly model check, a BIM-ready audit), as a job
/// of a chat With coordinator, with a history of its runs.
/// </summary>
internal sealed class Routine
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>What to do, as it goes to the job.</summary>
    public string Task { get; set; } = "";
    /// <summary>The time of day it runs, local, "HH:mm".</summary>
    public string Time { get; set; } = "02:00";
    /// <summary>The days it runs (DayOfWeek numbers, Sunday 0); empty: every day.</summary>
    public List<int> Days { get; set; } = [];
    /// <summary>Runs only on the model with this title (the file name without .rvt); "": whichever model is open.</summary>
    public string Model { get; set; } = "";
    public bool On { get; set; } = true;
    public DateTime Created { get; set; }
    /// <summary>The latest scheduled time (local) it ran for: an earlier time is never run again.</summary>
    public DateTime LastDue { get; set; }
    /// <summary>Its runs, latest first, at most <see cref="Routines.MaxRuns"/>.</summary>
    public List<RoutineRun> Runs { get; set; } = [];
}

/// <summary>
/// The routines and their rules (no WPF or Revit: the unit tests compile this file): when one is due, catching up a run
/// missed while the PC or Revit was off (once, at the next chance, never one per missed day), the schedule in words, the
/// ready-made ones, and the file they are kept in (routines.json in the chat folder).
/// </summary>
internal static class Routines
{
    public const int MaxRuns = 30;

    /// <summary>A run more than this late is a catch-up (it says so).</summary>
    public static readonly TimeSpan Late = TimeSpan.FromMinutes(15);

    /// <summary>Ready-made routines the window offers (name, what to do, time).</summary>
    public static readonly (string Name, string Task, string Time)[] Presets =
    [
        ("Nightly model check", "Run a model check of the open model: warnings, duplicates, unplaced or unused elements, missing parameters and anything " +
            "else Sketch ProMax's model check tools report. Don't change the model. Report what you found, worst first, with counts and how to fix each.", "02:00"),
        ("BIM-ready audit", "Audit the open model for BIM readiness: check it against the pre-submission checklist and the BIM-ready rules (naming, " +
            "levels, scope boxes, view templates, parameters, LOI data). Don't change the model. Report what fails, worst first, and what would fix it.", "03:00"),
    ];

    public static string File(string folder) => Path.Combine(folder, "routines.json");

    private static readonly object Lock = new();

    public static List<Routine> Load(string folder)
    {
        lock (Lock)
        {
            try
            {
                var file = File(folder);
                return System.IO.File.Exists(file) ? JsonSerializer.Deserialize<List<Routine>>(System.IO.File.ReadAllText(file)) ?? [] : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return []; }
        }
    }

    public static void Save(string folder, List<Routine> routines)
    {
        lock (Lock)
        {
            Directory.CreateDirectory(folder);
            var file = File(folder);
            var temp = file + ".tmp";
            System.IO.File.WriteAllText(temp, JsonSerializer.Serialize(routines, new JsonSerializerOptions { WriteIndented = true }));
            if (System.IO.File.Exists(file)) System.IO.File.Replace(temp, file, null);
            else System.IO.File.Move(temp, file);
        }
    }

    /// <summary>"HH:mm" as hours and minutes; null when it isn't a time of day.</summary>
    public static TimeSpan? ParseTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return TimeSpan.TryParseExact(text.Trim(), [@"h\:mm", @"hh\:mm"], CultureInfo.InvariantCulture, out var t) && t < TimeSpan.FromDays(1) ? t : null;
    }

    /// <summary>The latest scheduled time (local) at or before <paramref name="now"/> (local); null when it has none in the last 8 days.</summary>
    public static DateTime? LastScheduled(Routine routine, DateTime now)
    {
        if (ParseTime(routine.Time) is not { } time) return null;
        for (var back = 0; back <= 8; back++)
        {
            var day = now.Date.AddDays(-back);
            var at = day + time;
            if (at > now) continue;
            if (routine.Days.Count == 0 || routine.Days.Contains((int)day.DayOfWeek)) return at;
        }
        return null;
    }

    /// <summary>
    /// The scheduled time a routine should run for now (local), or null: it is on, its latest time has passed, it didn't
    /// run for that time yet, and the routine existed then. A run missed while Revit was off is caught up once.
    /// </summary>
    public static DateTime? Due(Routine routine, DateTime now)
    {
        if (!routine.On || LastScheduled(routine, now) is not { } at) return null;
        return at > routine.LastDue && at >= routine.Created ? at : null;
    }

    /// <summary>When it runs next (local), after <paramref name="now"/>; null without a valid time or day.</summary>
    public static DateTime? Next(Routine routine, DateTime now)
    {
        if (ParseTime(routine.Time) is not { } time) return null;
        for (var ahead = 0; ahead <= 8; ahead++)
        {
            var day = now.Date.AddDays(ahead);
            var at = day + time;
            if (at <= now) continue;
            if (routine.Days.Count == 0 || routine.Days.Contains((int)day.DayOfWeek)) return at;
        }
        return null;
    }

    /// <summary>"Every day at 02:00", "Weekdays at 02:00", "Mon, Thu at 02:00".</summary>
    public static string Schedule(Routine routine)
    {
        var days = routine.Days.Distinct().OrderBy(d => (d + 6) % 7).ToList();
        var when = days.Count is 0 or 7 ? "Every day"
            : days.SequenceEqual([1, 2, 3, 4, 5]) ? "Weekdays"
            : days.SequenceEqual([6, 0]) ? "Weekends"
            : string.Join(", ", days.Select(d => CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedDayName((DayOfWeek)d)));
        return $"{when} at {routine.Time}" + (routine.Model.Length > 0 ? $", on {routine.Model}" : "");
    }

    /// <summary>The task as the routine's job gets it: what to do, and that nobody may be there to answer.</summary>
    public static string JobTask(Routine routine, DateTime due, bool catchUp) =>
        $"This is the scheduled routine \"{routine.Name}\" ({Schedule(routine).ToLowerInvariant()}), " +
        (catchUp ? $"catching up the run of {due:ddd d MMM HH:mm} that was missed while Revit was closed or busy" : $"the run of {due:ddd d MMM HH:mm}") +
        ". The user may be away, so don't ask questions: do the work and report what you found.\n\n" + routine.Task.Trim();

    /// <summary>A run starts: it goes first in the history (the oldest go past <see cref="MaxRuns"/>) and the routine is up to date.</summary>
    public static RoutineRun Started(Routine routine, DateTime due, DateTime nowUtc, DateTime nowLocal, string chatId, string chatTitle, int job, string model)
    {
        var run = new RoutineRun
        {
            Started = nowUtc, Due = due, CatchUp = nowLocal - due > Late, ChatId = chatId, ChatTitle = chatTitle, Job = job, Model = model,
        };
        routine.LastDue = due;
        routine.Runs.Insert(0, run);
        if (routine.Runs.Count > MaxRuns) routine.Runs.RemoveRange(MaxRuns, routine.Runs.Count - MaxRuns);
        return run;
    }

    /// <summary>The run that job <paramref name="job"/> of chat <paramref name="chatId"/> is, ended: its state and report. False when none.</summary>
    public static bool Ended(List<Routine> routines, string routineId, string chatId, int job, string state, string summary, DateTime nowUtc)
    {
        var run = routines.FirstOrDefault(r => r.Id == routineId)?.Runs.FirstOrDefault(r => r.ChatId == chatId && r.Job == job);
        if (run is null) return false;
        run.State = state;
        run.Summary = summary.Trim();
        run.Ended = nowUtc;
        return true;
    }

    /// <summary>A run's line in the history: "Thu 30 Oct 02:00 · Done · caught up 08:12 · …".</summary>
    public static string Line(RoutineRun run)
    {
        var sb = new StringBuilder(run.Due.ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture));
        sb.Append(" · ").Append(run.State switch
        {
            JobBoard.Done => "Done",
            JobBoard.Failed => "Ended with a problem",
            JobBoard.Stopped => "Stopped",
            _ => "Running",
        });
        if (run.CatchUp) sb.Append(" · caught up ").Append(run.Started.ToLocalTime().ToString("ddd HH:mm", CultureInfo.InvariantCulture));
        if (run.Model.Length > 0) sb.Append(" · ").Append(run.Model);
        return sb.ToString();
    }
}
