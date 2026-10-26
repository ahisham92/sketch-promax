using System.Text;

namespace SketchProMax.Chat;

/// <summary>
/// The short note added to each chat message (never shown in the pane): what changed in the model since the chat's last
/// answer, the active view and what is selected in Revit. With it a follow-up doesn't read the model again to find out
/// whether anything moved or which elements "the selected ones" are. Pure text; Revit.ChatModelWatch gathers the facts.
/// </summary>
internal static class ModelNote
{
    /// <summary>What Revit.ChatModelWatch knows about one model.</summary>
    internal sealed class Facts
    {
        /// <summary>True once the chat answered at least once since Revit started watching (changes are counted from then).</summary>
        public bool Known { get; set; }
        /// <summary>Elements added or changed since the last answer, by category ("Structural Columns": 5).</summary>
        public Dictionary<string, int> Changed { get; set; } = new(StringComparer.Ordinal);
        public int Deleted { get; set; }
        /// <summary>The active view's name and id; "" when not known.</summary>
        public string View { get; set; } = "";
        public long ViewId { get; set; }
        /// <summary>True once Revit reported the selection (before that it is unknown, not empty).</summary>
        public bool SelectionKnown { get; set; }
        public int SelectedCount { get; set; }
        public List<long> SelectedIds { get; set; } = [];
        public Dictionary<string, int> SelectedKinds { get; set; } = new(StringComparer.Ordinal);
    }

    public const int MaxIds = 60, MaxKinds = 6;

    public const string Start = "[Model note from Sketch ProMax, not typed by the user: ";

    /// <summary>The note, starting with a blank line; "" when nothing is known.</summary>
    public static string Text(Facts? facts, bool changes = true)
    {
        if (facts is null) return "";
        var parts = new List<string>();
        if (changes && facts.Known)
        {
            var changed = facts.Changed.Values.Sum();
            if (changed == 0 && facts.Deleted == 0) parts.Add("the model has not changed since your last answer, so what you found earlier still holds");
            else
            {
                var sb = new StringBuilder("since your last answer the model changed (by the user or a job): ");
                if (changed > 0) sb.Append(changed).Append(" element").Append(changed == 1 ? "" : "s").Append(" added or changed (").Append(Kinds(facts.Changed)).Append(')');
                if (facts.Deleted > 0) sb.Append(changed > 0 ? ", " : "").Append(facts.Deleted).Append(" deleted");
                sb.Append("; re-read only those, the rest still holds");
                parts.Add(sb.ToString());
            }
        }
        if (facts.View.Length > 0) parts.Add($"active view: {facts.View}" + (facts.ViewId > 0 ? $" (id {facts.ViewId})" : ""));
        if (facts.SelectionKnown)
        {
            if (facts.SelectedCount == 0) parts.Add("nothing is selected in Revit");
            else
            {
                var ids = facts.SelectedIds.Take(MaxIds).ToList();
                var sb = new StringBuilder("selected in Revit: ").Append(facts.SelectedCount).Append(facts.SelectedCount == 1 ? " element" : " elements");
                if (facts.SelectedKinds.Count > 0) sb.Append(" (").Append(Kinds(facts.SelectedKinds)).Append(')');
                if (ids.Count > 0)
                    sb.Append(", ids ").Append(string.Join(", ", ids)).Append(facts.SelectedCount > ids.Count ? $" and {facts.SelectedCount - ids.Count} more" : "");
                parts.Add(sb.ToString());
            }
        }
        return parts.Count == 0 ? "" : "\n\n" + Start + string.Join("; ", parts) + ".]";
    }

    /// <summary>"Structural Columns 5, Dimensions 2, 3 other": the largest first.</summary>
    private static string Kinds(Dictionary<string, int> kinds)
    {
        var sorted = kinds.Where(k => k.Value > 0).OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal).ToList();
        var shown = sorted.Take(MaxKinds).Select(k => $"{k.Key} {k.Value}").ToList();
        var rest = sorted.Skip(MaxKinds).Sum(k => k.Value);
        if (rest > 0) shown.Add($"{rest} other");
        return string.Join(", ", shown);
    }
}
