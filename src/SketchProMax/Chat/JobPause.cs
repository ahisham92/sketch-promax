using System.IO;

namespace SketchProMax.Chat;

/// <summary>
/// Pause all / Continue (the owner, 19 Nov 2026: "a button pausing everything so that I can sync or update, then I press
/// continue and it continues; saying pause irritates so much"). While paused no job starts, a job's next call is refused
/// (nothing runs) and each running job's process ends as soon as it has no call in Revit, so Revit is free for a sync and
/// a newer version switches in. Continue sends every paused job on from where it stopped (its session is kept).
/// A newer version waiting while jobs run pauses them the same way by itself (<see cref="ForUpdate"/>), and they carry on
/// by themselves once it is in (or after <see cref="UpdateGiveUp"/> if it couldn't go in). Kept in a file, so a version
/// switch or a restart keeps it. No Revit or WPF here (the tests compile it).
/// </summary>
internal static class JobPause
{
    /// <summary>Why the jobs are paused: the user pressed Pause all, or a newer version goes in.</summary>
    public const string ForUser = "user", ForUpdate = "update";

    /// <summary>How long jobs paused for an update wait for it before they carry on without it.</summary>
    public static readonly TimeSpan UpdateGiveUp = TimeSpan.FromMinutes(10);

    /// <summary>What a job's call gets while paused: nothing ran.</summary>
    public const string Refused =
        "Paused by the user (Pause all), so nothing was run. Stop here: end your turn now with one short line saying where you are " +
        "and what you were about to do. You will get a message to carry on.";

    /// <summary>What a paused job is told when it carries on.</summary>
    public const string CarryOn =
        "Carry on with your task from where you stopped: you were paused for a while (the user synced or Sketch ProMax updated). " +
        "First read back in the model what your last change did, then go on. Don't do anything twice.";

    private static readonly object Gate = new();
    private static readonly Dictionary<string, int> InCall = new(StringComparer.Ordinal);
    private static readonly HashSet<string> WasRefused = new(StringComparer.Ordinal);
    private static string _why = "";
    private static DateTime _since;

    /// <summary>The file it is kept in (set by the chat at start); null keeps it in memory only (the tests).</summary>
    public static string? StoreFile { get; set; }

    /// <summary>Raised (any thread) when it is paused or continued.</summary>
    public static event Action? Changed;

    public static bool On
    {
        get { lock (Gate) return _why.Length > 0; }
    }

    public static string Why
    {
        get { lock (Gate) return _why; }
    }

    public static DateTime Since
    {
        get { lock (Gate) return _since; }
    }

    /// <summary>Pauses (an update never overrides the user's own pause).</summary>
    public static void Pause(string why, DateTime now)
    {
        lock (Gate)
        {
            if (_why == ForUser && why == ForUpdate) return;
            if (_why == why) return;
            _why = why;
            _since = now;
            Write();
        }
        Changed?.Invoke();
    }

    /// <summary>Continues. True when it was paused.</summary>
    public static bool Continue()
    {
        lock (Gate)
        {
            if (_why.Length == 0) return false;
            _why = "";
            _since = default;
            Write();
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>Paused for an update that hasn't gone in after <see cref="UpdateGiveUp"/>: the jobs carry on without it.</summary>
    public static bool UpdateTooLong(DateTime now)
    {
        lock (Gate) return _why == ForUpdate && now - _since >= UpdateGiveUp;
    }

    /// <summary>A job's call starts (the bridge). Refused text while paused, else null and it counts as in Revit until <see cref="Exit"/>.</summary>
    public static string? Enter(string jobKey)
    {
        lock (Gate)
        {
            if (_why.Length > 0)
            {
                WasRefused.Add(jobKey);
                return Refused;
            }
            InCall[jobKey] = InCall.TryGetValue(jobKey, out var n) ? n + 1 : 1;
            return null;
        }
    }

    public static void Exit(string jobKey)
    {
        lock (Gate)
        {
            if (!InCall.TryGetValue(jobKey, out var n)) return;
            if (n <= 1) InCall.Remove(jobKey);
            else InCall[jobKey] = n - 1;
        }
    }

    /// <summary>
    /// The job's run ended (or starts afresh): true when a call of its own was refused by the pause since it was last
    /// asked, and the flag is cleared. Such a run ended because it was told to stop, not because its task is done, so it
    /// is set aside as paused rather than finished (bug 111: a job that stopped at the pause was marked done, so Continue
    /// never sent it on).
    /// </summary>
    public static bool TookRefusal(string jobKey)
    {
        lock (Gate) return WasRefused.Remove(jobKey);
    }

    /// <summary>True while the job has a call the bridge is working on.</summary>
    public static bool Calling(string jobKey)
    {
        lock (Gate) return InCall.ContainsKey(jobKey);
    }

    /// <summary>Reads it back from <see cref="StoreFile"/> (a version switch or a restart).</summary>
    public static void Restore()
    {
        try
        {
            if (StoreFile is not { } file || !File.Exists(file)) return;
            var parts = File.ReadAllText(file).Trim().Split('|');
            if (parts.Length != 2 || parts[0] is not (ForUser or ForUpdate)) return;
            lock (Gate)
            {
                _why = parts[0];
                _since = DateTime.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : DateTime.UtcNow;
            }
        }
        catch (Exception) { /* not paused */ }
    }

    private static void Write()
    {
        try
        {
            if (StoreFile is not { } file) return;
            if (_why.Length == 0)
            {
                if (File.Exists(file)) File.Delete(file);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, _why + "|" + _since.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception) { /* kept in memory */ }
    }

    /// <summary>For the tests.</summary>
    internal static void Reset()
    {
        lock (Gate)
        {
            _why = "";
            _since = default;
            InCall.Clear();
            WasRefused.Clear();
        }
    }
}
