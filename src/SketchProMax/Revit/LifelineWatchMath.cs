using System.Globalization;

namespace SketchProMax.Revit;

/// <summary>
/// The rules of Lifeline's warnings and its saving of the local copy by itself (<see cref="LifelineWatch"/>), with no
/// Revit in them: when a sign is worth a hint, what the hint says, and when a save is due.
/// </summary>
internal static class LifelineWatchMath
{
    public const int DefaultMinutes = 20, MinMinutes = 5, MaxMinutes = 240;
    /// <summary>The same hint again only after this long, while what it warns of still holds.</summary>
    public const int RepeatMs = 15 * 60_000;
    public const int HandleLimit = 10_000, HandleWarn = 8_500;
    public const long LowMemoryMb = 1024, LowDiskMb = 2048;
    public const int ManyChanges = 500;
    public static readonly TimeSpan LongUnsaved = TimeSpan.FromMinutes(60), QuietBeforeSave = TimeSpan.FromSeconds(5), RetrySave = TimeSpan.FromMinutes(5);

    public static int ClampMinutes(int minutes) => minutes <= 0 ? DefaultMinutes : Math.Min(MaxMinutes, Math.Max(MinMinutes, minutes));

    /// <summary>Synchronizing with central by itself: every 60 minutes unless the user sets it, 10 to 480.</summary>
    public const int DefaultSyncMinutes = 60, MinSyncMinutes = 10, MaxSyncMinutes = 480;
    public static readonly TimeSpan RetrySync = TimeSpan.FromMinutes(10);

    public static int ClampSyncMinutes(int minutes) => minutes <= 0 ? DefaultSyncMinutes : Math.Min(MaxSyncMinutes, Math.Max(MinSyncMinutes, minutes));

    /// <summary>
    /// The local copy is synchronized with central now: it changed since its last synchronize, the time the user set
    /// has passed, the server answered at the last look, nobody has touched the mouse or keyboard for a moment, no tool
    /// is open, no chat step is in the middle of its work, and a synchronize that failed isn't tried again at once.
    /// </summary>
    public static bool SyncDue(bool changed, TimeSpan sinceSync, int minutes, TimeSpan sinceInput, bool toolOpen, bool working, bool serverAnswers, TimeSpan sinceFailed) =>
        changed && serverAnswers && !toolOpen && !working && sinceSync >= TimeSpan.FromMinutes(ClampSyncMinutes(minutes)) && sinceInput >= QuietBeforeSave && sinceFailed >= RetrySync;

    public static Warning Syncing(string model) => new("syncing", "Lifeline is synchronizing with central", $"{model}: Revit is busy for the time a synchronize takes.");

    public static Warning Synced(string model, TimeSpan took, int minutes) => new("synced", "Lifeline synchronized with central",
        $"{model}: synchronized in {Math.Max(1, (int)Math.Round(took.TotalSeconds))} s; what you borrowed is given back. Next one in {ClampSyncMinutes(minutes)} min if you change something.");

    public static Warning SyncFailed(string model, string why) => new("sync-failed", "Lifeline couldn't synchronize with central",
        $"{model}: {why} Your changes stay in your local. Lifeline tries again in {(int)RetrySync.TotalMinutes} min, or synchronize yourself.");

    /// <summary>The line under the synchronize choice in the Lifeline window.</summary>
    public static string AboutSync(bool on, int minutes) => on
        ? $"On: every {ClampSyncMinutes(minutes)} min a local copy with changes is synchronized with central, at a quiet moment with no tool open and nothing selected. It waits while the server can't be reached."
        : "Off: Sketch ProMax doesn't synchronize by itself here. (Settings > Chat has its own synchronize after a number of chat changes.)";

    /// <summary>What was read from the PC: memory and disk in MB (-1: couldn't be read), the drive's name, Revit's window handles (0: couldn't be read).</summary>
    public sealed record Signs(long RevitMemoryMb, long FreeMemoryMb, long TotalMemoryMb, long FreeDiskMb, string Disk, int Handles);

    /// <summary>A hint: its key (one hint per key at a time, muted by key), its headline and what to do.</summary>
    public sealed record Warning(string Key, string Main, string Detail);

    private static string Gb(long mb) => (mb / 1024.0).ToString("0.#", CultureInfo.CurrentCulture) + " GB";

    /// <summary>The signs that often come before a crash. Nothing here predicts one: each is a reason to save now.</summary>
    public static List<Warning> Judge(Signs s)
    {
        var warnings = new List<Warning>();
        if (s.FreeMemoryMb >= 0 && s.TotalMemoryMb > 0 && (s.FreeMemoryMb < LowMemoryMb || s.FreeMemoryMb * 100 / s.TotalMemoryMb < 6))
            warnings.Add(new("memory", "Your PC is almost out of memory",
                $"Revit uses {Gb(Math.Max(0, s.RevitMemoryMb))} and only {Gb(s.FreeMemoryMb)} is free. Save now. Closing other programs or views you don't need helps."));
        if (s.FreeDiskMb >= 0 && s.FreeDiskMb < LowDiskMb)
            warnings.Add(new("disk", $"Drive {s.Disk} is almost full",
                $"Only {s.FreeDiskMb:N0} MB is free where Revit saves. A save can fail: free some space, then save."));
        if (s.Handles >= HandleWarn)
            warnings.Add(new("handles", "Revit has been open for a long time",
                $"It uses {s.Handles:N0} of the {HandleLimit:N0} window handles Windows allows, a known cause of crashes. Save, then close and reopen Revit when you can."));
        return warnings;
    }

    /// <summary>A central model on a server or a shared folder, whose folder can be asked for; not a cloud model or Revit Server (Revit talks to those itself).</summary>
    public static bool IsServerPath(string? central, string? local)
    {
        if (central is not { Length: > 2 } path || string.Equals(path, local, StringComparison.OrdinalIgnoreCase)) return false;
        if (path.Contains("://") || path.StartsWith("RSN:", StringComparison.OrdinalIgnoreCase) || path.StartsWith("BIM 360:", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("Autodesk Docs:", StringComparison.OrdinalIgnoreCase) || path.StartsWith("cloud:", StringComparison.OrdinalIgnoreCase)) return false;
        return path.StartsWith(@"\\", StringComparison.Ordinal) || (path.Length > 2 && path[1] == ':' && path[2] is '\\' or '/');
    }

    /// <summary>
    /// One look at the server: lost after two looks in a row with no answer (one slow answer is not a lost server),
    /// back at the first answer. Says "lost" or "back" once, when it changes.
    /// </summary>
    public static (int Fails, bool Lost, string? Say) Server(int fails, bool lost, bool reachable)
    {
        if (reachable) return (0, false, lost ? "back" : null);
        fails++;
        return fails >= 2 && !lost ? (fails, true, "lost") : (fails, lost, null);
    }

    public static Warning ServerLost(string model) => new("server", "The server can't be reached",
        $"{model}: its central model doesn't answer. Save your local now and don't synchronize until it is back. Your changes stay in your local and in Lifeline.");

    public static Warning ServerBack(string model) => new("server-back", "The server is back", $"{model}: its central model answers again. You can synchronize.");

    /// <summary>A lot is unsaved (only when the local isn't saved by itself): many changes, or changes left unsaved for a long time.</summary>
    public static Warning? Unsaved(string model, TimeSpan sinceSave, int changes, bool autoSave)
    {
        if (autoSave || changes <= 0 || (sinceSave < LongUnsaved && changes < ManyChanges)) return null;
        return new("unsaved", $"{changes:N0} changes aren't saved",
            $"{model}: last saved {Math.Max(1, (int)sinceSave.TotalMinutes)} min ago. Save your local when you can (Ctrl+S).");
    }

    /// <summary>The line under the choice in the Lifeline window: what saving by itself does, and what it never does.</summary>
    public static string About(bool autoSave, int minutes) => autoSave
        ? $"On: every {ClampMinutes(minutes)} min a model with changes is saved, at a quiet moment with no tool open and nothing selected. It saves your local copy only and never synchronizes with central."
        : "Off: Sketch ProMax never saves by itself. Turn it on to have your local copy saved at the pace you set; it never synchronizes with central.";

    /// <summary>A hint of this key is shown now: never shown, or last shown long enough ago.</summary>
    public static bool Due(int nowTick, int? shownTick) => shownTick is not { } shown || unchecked(nowTick - shown) >= RepeatMs;

    /// <summary>
    /// The local copy is saved now: it has changes, the time the user set has passed since its last save, nobody has
    /// touched the mouse or keyboard for a moment, no tool is open, and a save that failed isn't tried again at once.
    /// </summary>
    public static bool SaveDue(bool modified, TimeSpan sinceSave, int minutes, TimeSpan sinceInput, bool toolOpen, TimeSpan sinceFailed) =>
        modified && !toolOpen && sinceSave >= TimeSpan.FromMinutes(ClampMinutes(minutes)) && sinceInput >= QuietBeforeSave && sinceFailed >= RetrySave;

    public static Warning Saving(string model) => new("saving", "Lifeline is saving your local copy", $"{model}: Revit is busy for the time a save takes. Nothing is synchronized.");

    public static Warning Saved(string model, TimeSpan took, int minutes) => new("saved", "Lifeline saved your local copy",
        $"{model}: saved in {Math.Max(1, (int)Math.Round(took.TotalSeconds))} s, not synchronized. Next save in {ClampMinutes(minutes)} min if you change something.");

    public static Warning SaveFailed(string model, string why) => new("save-failed", "Lifeline couldn't save your local copy",
        $"{model}: {why} Save it yourself when you can (Ctrl+S). Lifeline tries again in {(int)RetrySave.TotalMinutes} min.");
}
