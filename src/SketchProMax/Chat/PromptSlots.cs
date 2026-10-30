using System.Text.RegularExpressions;

namespace SketchProMax.Chat;

/// <summary>
/// Blanks in the home screen's prompts ({level}, {grid}, {sheet_set}, {berth}, {selection}), filled from the open model:
/// the user taps one of the model's own levels, grids, sheet sets or berths instead of typing it. A blank with no
/// choice left in the prompt reads "[level]" for the user to type over. Pure text; Revit.PromptChoices reads the choices.
/// </summary>
internal static class PromptSlots
{
    /// <summary>One kind of blank: the question asked, and the words left when it isn't picked.</summary>
    internal sealed record Slot(string Name, string Question, string Unpicked);

    public const string Selection = "selection";

    public static readonly Slot[] Known =
    [
        new("level", "Which level?", "[level]"),
        new("grid", "Which grid line?", "[grid]"),
        new("sheet_set", "Which sheet set?", "[sheet set]"),
        new("berth", "Which berth?", "[berth]"),
        new(Selection, "", "the elements selected in Revit"),
    ];

    /// <summary>At most this many choices are offered for one blank (a model with hundreds of grids shows the first ones).</summary>
    public const int MaxChoices = 40;

    private static readonly Regex Blank = new(@"\{([a-z_]+)\}", RegexOptions.CultureInvariant);

    public static Slot? Find(string name) => Known.FirstOrDefault(s => s.Name == name);

    /// <summary>The known blanks in the prompt, in the order they first appear.</summary>
    public static List<Slot> In(string prompt) =>
        Blank.Matches(prompt ?? "").Cast<Match>().Select(m => Find(m.Groups[1].Value)).OfType<Slot>().Distinct().ToList();

    /// <summary>The blanks the user picks a value for (all but {selection}, which always reads the same).</summary>
    public static List<Slot> ToPick(string prompt) => In(prompt).Where(s => s.Name != Selection).ToList();

    /// <summary>The prompt with each known blank replaced by its picked value, else its unpicked words; other braces stay.</summary>
    public static string Fill(string prompt, IReadOnlyDictionary<string, string> picked) =>
        Blank.Replace(prompt ?? "", m =>
        {
            var slot = Find(m.Groups[1].Value);
            if (slot is null) return m.Value;
            return picked.TryGetValue(slot.Name, out var value) && value.Trim().Length > 0 ? value.Trim() : slot.Unpicked;
        });

    /// <summary>
    /// Names in a sensible order without repeats: numbers inside names count as numbers ("Level 2" before "Level 10",
    /// grid "9" before "10"), at most <see cref="MaxChoices"/>. Levels keep the order they come in (by elevation).
    /// </summary>
    public static List<string> Tidy(IEnumerable<string> names, bool keepOrder = false)
    {
        var list = names.Select(n => (n ?? "").Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
        if (!keepOrder) list = list.OrderBy(n => Regex.Replace(n, @"\d+", d => d.Value.PadLeft(8, '0')), StringComparer.OrdinalIgnoreCase);
        return list.Take(MaxChoices).ToList();
    }
}
