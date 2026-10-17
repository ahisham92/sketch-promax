using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SketchProMax.Tools;

/// <summary>
/// Guards tools that make a lot at once (drawing_package, redline_apply) against being run twice by a retry: a call with
/// the same inputs on the same model within <see cref="Window"/> of a finished run gets that run's result back, said as
/// such, instead of making everything again; force true runs it anyway. Kept in memory for this Revit session only.
/// Pure, so the tests run it.
/// </summary>
internal static class RecentRuns
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    /// <summary>Arguments that don't change what is made: left out of the key.</summary>
    private static readonly HashSet<string> Ignored = new(StringComparer.Ordinal) { "force", "approved", "dry_run" };

    private static readonly Dictionary<string, (DateTime At, string Text)> Done = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    /// <summary>The key of a call: the tool, the model and a hash of its inputs (property order and spacing don't matter).</summary>
    public static string Key(string tool, string modelKey, IReadOnlyDictionary<string, JsonElement> args)
    {
        var sb = new StringBuilder(tool).Append('\u001f').Append(modelKey).Append('\u001f');
        foreach (var p in args.Where(a => !Ignored.Contains(a.Key)).OrderBy(a => a.Key, StringComparer.Ordinal))
        {
            sb.Append(p.Key).Append('=');
            Canonical(p.Value, sb);
            sb.Append('\u001e');
        }
        using var sha = SHA256.Create();
        return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())).Take(16).Select(b => b.ToString("x2")));
    }

    private static void Canonical(JsonElement e, StringBuilder sb)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                sb.Append('{');
                foreach (var p in e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)) { sb.Append(JsonSerializer.Serialize(p.Name)).Append(':'); Canonical(p.Value, sb); sb.Append(','); }
                sb.Append('}');
                break;
            case JsonValueKind.Array:
                sb.Append('[');
                foreach (var x in e.EnumerateArray()) { Canonical(x, sb); sb.Append(','); }
                sb.Append(']');
                break;
            case JsonValueKind.String: sb.Append(JsonSerializer.Serialize(e.GetString())); break;
            case JsonValueKind.Number: sb.Append(e.TryGetDouble(out var d) ? d.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : e.GetRawText()); break;
            default: sb.Append(e.GetRawText()); break;
        }
    }

    /// <summary>A finished run's result under its key (only runs that made something are remembered).</summary>
    public static void Remember(string key, string resultText, DateTime now)
    {
        lock (Gate)
        {
            foreach (var old in Done.Where(kv => now - kv.Value.At > Window).Select(kv => kv.Key).ToList()) Done.Remove(old);
            Done[key] = (now, resultText);
        }
    }

    /// <summary>Forgets a key (the user asked for force, or the earlier result no longer applies).</summary>
    public static void Forget(string key)
    {
        lock (Gate) Done.Remove(key);
    }

    /// <summary>
    /// For a repeat within the window: the reply that hands back the earlier result (as JSON text), else null.
    /// </summary>
    public static string? Repeat(string tool, string key, DateTime now)
    {
        (DateTime At, string Text) hit;
        lock (Gate)
            if (!Done.TryGetValue(key, out hit) || now - hit.At > Window) return null;
        object earlier;
        try { using var j = JsonDocument.Parse(hit.Text); earlier = j.RootElement.Clone(); }
        catch (JsonException) { earlier = hit.Text; }
        var ago = Math.Max(0, (int)Math.Round((now - hit.At).TotalMinutes));
        return JsonSerializer.Serialize(new
        {
            alreadyDone = $"{tool} already ran with these same inputs on this model {(ago == 0 ? "less than a minute" : ago == 1 ? "1 minute" : $"{ago} minutes")} ago " +
                          "and finished, so nothing new was made: its result is below. Don't run it again for the same request. " +
                          "If the user really wants a second set, call it again with force true.",
            earlierResult = earlier,
        });
    }
}
