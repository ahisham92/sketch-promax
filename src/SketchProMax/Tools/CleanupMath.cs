using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SketchProMax.Tools;

/// <summary>
/// The pure parts of the purge and cleanup tools (purge_unused, delete_unused_views, cleanup_imports, cleanup_model):
/// name patterns for keep and only lists, the chat summary line, the file-size hint and the quick-fix hints for Revit
/// warnings. No Revit types, so the tests compile it.
/// </summary>
internal static class CleanupMath
{
    /// <summary>The cleanup tools: a preview unless apply is true (see <see cref="ApprovalPolicy"/>).</summary>
    public static readonly HashSet<string> Tools = new(StringComparer.Ordinal) { "purge_unused", "delete_unused_views", "cleanup_imports", "cleanup_model" };

    /// <summary>
    /// True when <paramref name="name"/> matches <paramref name="pattern"/>: with * or ? a wildcard over the whole name,
    /// otherwise the name contains it. Case-insensitive; an empty pattern matches nothing.
    /// </summary>
    public static bool Matches(string? name, string? pattern)
    {
        var p = (pattern ?? "").Trim();
        if (p.Length == 0) return false;
        var n = name ?? "";
        if (p.IndexOf('*') < 0 && p.IndexOf('?') < 0) return n.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0;
        var regex = "^" + Regex.Escape(p).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(n, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    }

    /// <summary>The first of <paramref name="patterns"/> that <paramref name="name"/> matches, or null.</summary>
    public static string? FirstMatch(string? name, IEnumerable<string>? patterns) =>
        patterns?.FirstOrDefault(p => Matches(name, p));

    /// <summary>Splits ids into chunks for deleting a few at a time (a refused chunk is retried one by one).</summary>
    public static List<List<T>> Chunks<T>(IEnumerable<T> items, int size)
    {
        var list = new List<List<T>>();
        foreach (var item in items)
        {
            if (list.Count == 0 || list[^1].Count >= Math.Max(1, size)) list.Add([]);
            list[^1].Add(item);
        }
        return list;
    }

    /// <summary>"148 families and types, 40 materials and 3 more kinds" style: the biggest groups first.</summary>
    public static string Groups(IReadOnlyDictionary<string, int> byGroup, int max = 4)
    {
        var parts = byGroup.Where(g => g.Value > 0).OrderByDescending(g => g.Value).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => $"{g.Value} {g.Key}").ToList();
        if (parts.Count == 0) return "nothing";
        var shown = parts.Take(max).ToList();
        if (parts.Count > max) shown.Add($"{parts.Count - max} more kind{(parts.Count - max == 1 ? "" : "s")}");
        return shown.Count == 1 ? shown[0] : string.Join(", ", shown.Take(shown.Count - 1)) + " and " + shown[^1];
    }

    /// <summary>A preview deletes nothing, so it finds one pass: said after its summary when applying repeats passes.</summary>
    public const string PreviewPassesNote =
        "The preview lists what is unused now and deletes nothing; applying repeats the passes, so what these leave unused goes too and the result lists it.";

    /// <summary>
    /// The one line the chat shows: what was found or deleted, what was left and why, and the next step.
    /// <paramref name="skipped"/> counts the reasons things stay ("kept by name", "owned by someone else"...).
    /// </summary>
    public static string Summary(string what, bool applied, int total, int passes, IReadOnlyDictionary<string, int> byGroup,
        IReadOnlyDictionary<string, int> skipped, int alsoDeletes, string? stopped = null)
    {
        var sb = new StringBuilder();
        if (total == 0) sb.Append(applied ? $"{what}: nothing was deleted." : $"{what}: nothing to delete.");
        else if (applied) sb.Append($"{what}: deleted {total} ({Groups(byGroup)})" + (passes > 1 ? $" in {passes} passes" : "") + ". Ctrl+Z undoes it in one step.");
        else sb.Append($"{what} preview: {total} to delete ({Groups(byGroup)})" + (passes > 1 ? $", found in {passes} passes" : "") + ". Nothing has changed yet.");
        if (alsoDeletes > 0)
            sb.Append(applied ? $" {alsoDeletes} other element(s) went with them." : $" {alsoDeletes} other element(s) would go with them (listed in alsoDeletes).");
        var left = skipped.Where(s => s.Value > 0).Select(s => $"{s.Value} {s.Key}").ToList();
        if (left.Count > 0) sb.Append(" Left alone: " + string.Join(", ", left) + ".");
        if (stopped is not null) sb.Append(' ').Append(stopped);
        else if (!applied && total > 0) sb.Append(" Run it again with apply true to delete them, as one Undo step.");
        return sb.ToString();
    }

    /// <summary>The model file's size and what to do to see it shrink. Null size: not saved yet.</summary>
    public static string FileHint(long? bytes, bool applied, int deleted)
    {
        var size = bytes is { } b ? (b / 1024.0 / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " MB" : null;
        if (size is null) return "The model has not been saved yet, so there is no file size to compare.";
        if (!applied || deleted == 0) return $"The file is {size} on disk now.";
        return $"The file was {size} on disk before this; it gets smaller once you save (tick Compact in Save As > Options for the most).";
    }

    /// <summary>
    /// A quick fix for a Revit warning, by its text (the common ones a model check finds), or a general hint. The text is
    /// the warning's description as Revit gives it.
    /// </summary>
    public static string WarningHint(string description)
    {
        var d = description ?? "";
        bool Has(string s) => d.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0;
        if (Has("identical instances in the same place")) return "Duplicates: delete one of each pair (select the warning's elements in Manage > Warnings, or ask in the chat to delete the duplicates after you confirm the list).";
        if (Has("walls overlap")) return "Overlapping walls: delete or shorten one of them; room areas can be wrong until then.";
        if (Has("floors overlap") || Has("roofs overlap") || Has("ceilings overlap")) return "Overlapping slabs: cut one with the other (join geometry), or trim the outline.";
        if (Has("lines overlap")) return "Overlapping lines: delete the duplicate line.";
        if (Has("not in a properly enclosed region")) return "Unbounded room or area: add the missing wall or a separation line, or delete it if it is left over.";
        if (Has("multiple rooms are in the same enclosed region") || Has("multiple areas")) return "Two rooms or areas in one space: delete the extra one, or split the space with a separation line.";
        if (Has("slightly off axis")) return "Slightly off axis: the element is a hair off 0° or 90°. Clear the off-axis warnings clears it with nothing moved (first choice, also for submitted models); if any stay, Straighten the off-axis lines turns it onto the axis, 1 mm at most at an end, then 2 mm at most. With Keep the model BIM-ready on, new ones are hidden as you model.";
        if (Has("duplicate") && Has("mark")) return "Duplicate Mark values: renumber them (rename or set_parameters).";
        if (Has("joined but do not intersect")) return "Joined but not touching: unjoin them (Modify > Join > Unjoin, or cut_geometry with unjoin).";
        if (Has("completely inside another")) return "One element inside another: delete the inner one or check the join.";
        if (Has("room tag is outside") || Has("tag is outside")) return "Tag outside its element: move the tag inside or turn its leader on.";
        if (Has("room separation line")) return "Room separation line on top of a wall: delete the line where the wall already bounds the room.";
        if (Has("can't keep") && Has("joined")) return "Revit unjoined elements it couldn't keep joined: check the geometry there.";
        if (Has("does not intersect") || Has("doesn't intersect")) return "An element misses what it is attached to: check its host, top or base.";
        return "Open Manage > Warnings, pick the warning and press Show to go to its elements.";
    }
}
