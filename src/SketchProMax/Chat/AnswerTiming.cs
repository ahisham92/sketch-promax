using System.Globalization;
using System.Text;

namespace SketchProMax.Chat;

/// <summary>
/// Where an answer's time went, for the activity log (one "answer_time" line per answer of the main chat and of each job):
/// in all, the model's thinking (Claude Code's own figure), the tool calls (from each call's start to its result, the wait
/// for Revit included; the longest named), and the rest (starting the engine, reading the stream). It says where a slow
/// answer was slow, so the next speed-up goes where the time is. Not thread-safe: the UI thread only.
/// </summary>
internal sealed class AnswerTiming
{
    private readonly Dictionary<string, (string Tool, DateTime At)> _open = new(StringComparer.Ordinal);
    private TimeSpan _tools;
    private int _calls;
    private (string Tool, TimeSpan Took) _longest;

    public DateTime Started { get; private set; }

    public AnswerTiming(DateTime started) => Started = started;

    /// <summary>The next answer starts (a job carrying on with a new message in the same process).</summary>
    public void Restart(DateTime now)
    {
        Started = now;
        _open.Clear();
        _tools = TimeSpan.Zero;
        _calls = 0;
        _longest = ("", TimeSpan.Zero);
    }

    public void ToolStarted(string id, string tool, DateTime now)
    {
        if (!_open.ContainsKey(id)) _open[id] = (tool, now);
    }

    public void ToolEnded(string id, DateTime now)
    {
        if (!_open.TryGetValue(id, out var call)) return;
        _open.Remove(id);
        var took = now - call.At;
        if (took < TimeSpan.Zero) took = TimeSpan.Zero;
        _tools += took;
        _calls++;
        if (took > _longest.Took) _longest = (call.Tool, took);
    }

    /// <summary>
    /// The log line, e.g. "Job 4 answer: 42.0 s in all; thinking 30.1 s; 6 tool calls 9.5 s (longest create_walls 4.0 s);
    /// other 2.4 s". <paramref name="apiMs"/> is Claude Code's duration_api_ms (0 when it didn't say).
    /// </summary>
    public string Line(string who, DateTime now, long apiMs)
    {
        var all = now - Started;
        if (all < TimeSpan.Zero) all = TimeSpan.Zero;
        var thinking = TimeSpan.FromMilliseconds(Math.Max(0, apiMs));
        var sb = new StringBuilder();
        sb.Append(who).Append(" answer: ").Append(S(all)).Append(" in all");
        if (apiMs > 0) sb.Append("; thinking ").Append(S(thinking));
        if (_calls > 0)
        {
            sb.Append("; ").Append(_calls).Append(_calls == 1 ? " tool call " : " tool calls ").Append(S(_tools));
            if (_calls > 1 && _longest.Tool.Length > 0) sb.Append(" (longest ").Append(_longest.Tool).Append(' ').Append(S(_longest.Took)).Append(')');
        }
        // Thinking and tool calls can overlap (calls side by side): the rest is never below zero.
        if (apiMs > 0)
        {
            var other = all - thinking - _tools;
            if (other > TimeSpan.Zero) sb.Append("; other ").Append(S(other));
        }
        return sb.ToString();
    }

    private static string S(TimeSpan t) => t.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
}
