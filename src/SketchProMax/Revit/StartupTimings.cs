using System.Diagnostics;
using System.Text;

namespace SketchProMax.Revit;

/// <summary>
/// How long this core took to start and what a model open cost while it ran, for the activity log, the field test and
/// the self-test report (bug #18: opening a model was slow while Sketch ProMax loaded). Pure bookkeeping, no Revit API:
/// App feeds it from the start path, DocumentOpening/DocumentOpened and the dispatcher's Idling handler.
/// </summary>
internal static class StartupTimings
{
    private static readonly object Gate = new();
    private static readonly List<(string Phase, long Ms)> Phases = [];
    private static readonly List<string> Opens = [];
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private const int MaxOpens = 10;

    private static long _startTotal = -1;
    private static long _startedAt = -1;
    private static long _openingAt = -1;
    private static long _openedAt = -1;
    private static long _openMs = -1;
    private static long _openedHandlersMs;
    private static string _openTitle = "";

    /// <summary>True from DocumentOpened until the first Idling after it.</summary>
    public static bool AwaitingIdle { get; private set; }

    /// <summary>True while Revit is opening a document (DocumentOpening seen, DocumentOpened not yet).</summary>
    public static bool IsOpening => _openingAt >= 0 && _openedAt < _openingAt;

    /// <summary>The core start's own phases, in order; a phase reported twice (a second loader query) keeps the first.</summary>
    public static void Phase(string name, long ms)
    {
        lock (Gate)
        {
            if (Phases.Any(p => p.Phase == name)) return;
            Phases.Add((name, ms));
        }
    }

    /// <summary>Times <paramref name="work"/> as a phase of the start. Exceptions pass through (timed all the same).</summary>
    public static void Time(string name, Action work)
    {
        var sw = Stopwatch.StartNew();
        try { work(); }
        finally { Phase(name, sw.ElapsedMilliseconds); }
    }

    /// <summary>StartCore finished (the loader still asks for the ribbon right after).</summary>
    public static void StartDone(long totalMs)
    {
        lock (Gate)
        {
            _startTotal = totalMs;
            _startedAt = Clock.ElapsedMilliseconds;
        }
    }

    /// <summary>Milliseconds from the end of StartCore to now (the first Idling after start), or -1.</summary>
    public static long SinceStart => _startedAt < 0 ? -1 : Clock.ElapsedMilliseconds - _startedAt;

    /// <summary>One line: "core start 412 ms (dispatcher 3, tool registry 40, ...)".</summary>
    public static string StartLine()
    {
        lock (Gate)
        {
            if (_startTotal < 0) return "core start: not measured in this session";
            var parts = string.Join(", ", Phases.Select(p => $"{p.Phase} {p.Ms}"));
            return $"core start {_startTotal} ms" + (parts.Length > 0 ? $" ({parts}; ms)" : "");
        }
    }

    public static void Opening()
    {
        lock (Gate) _openingAt = Clock.ElapsedMilliseconds;
    }

    /// <summary>DocumentOpened: <paramref name="handlersMs"/> is what our own DocumentOpened handlers took.</summary>
    public static void Opened(string title, long handlersMs)
    {
        lock (Gate)
        {
            _openedAt = Clock.ElapsedMilliseconds;
            _openMs = _openingAt >= 0 ? _openedAt - _openingAt : -1;
            _openedHandlersMs = handlersMs;
            _openTitle = title;
            AwaitingIdle = true;
        }
    }

    /// <summary>
    /// The first Idling after a model opened: the line for the activity log, once per open; null otherwise. Cheap when
    /// nothing is waiting (called on every Idling).
    /// </summary>
    public static string? FirstIdle()
    {
        if (!AwaitingIdle) return null;
        lock (Gate)
        {
            if (!AwaitingIdle) return null;
            AwaitingIdle = false;
            var idle = Clock.ElapsedMilliseconds - _openedAt;
            var line = OpenLine(_openTitle, _openMs, _openedHandlersMs, idle);
            Opens.Add(line);
            if (Opens.Count > MaxOpens) Opens.RemoveAt(0);
            return line;
        }
    }

    internal static string OpenLine(string title, long openMs, long handlersMs, long idleMs) =>
        $"model open '{title}': Revit took {(openMs < 0 ? "?" : openMs.ToString(System.Globalization.CultureInfo.InvariantCulture))} ms " +
        $"(DocumentOpening to DocumentOpened), Sketch ProMax DocumentOpened handlers {handlersMs} ms, first Idling {idleMs} ms after open";

    /// <summary>The model opens measured in this session, oldest first.</summary>
    public static List<string> OpenLines()
    {
        lock (Gate) return Opens.ToList();
    }

    /// <summary>A Markdown section for the field test and self-test reports.</summary>
    public static string Describe()
    {
        var sb = new StringBuilder("## Startup and model-open timings\n\n");
        sb.Append("- ").Append(StartLine()).Append('\n');
        var opens = OpenLines();
        if (opens.Count == 0) sb.Append("- no model opened since Sketch ProMax started in this Revit session\n");
        foreach (var o in opens) sb.Append("- ").Append(o).Append('\n');
        return sb.ToString();
    }

    /// <summary>Forgets everything (tests).</summary>
    internal static void Reset()
    {
        lock (Gate)
        {
            Phases.Clear();
            Opens.Clear();
            _startTotal = _startedAt = _openingAt = _openedAt = _openMs = -1;
            _openedHandlersMs = 0;
            _openTitle = "";
            AwaitingIdle = false;
        }
    }
}
