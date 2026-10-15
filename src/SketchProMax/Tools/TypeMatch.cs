using Autodesk.Revit.DB;

namespace SketchProMax.Tools;

/// <summary>
/// Finds a family type (or any element type) from the name a user typed. Order: "Family: Type" (spaces around the
/// colon optional), then the family name, then the type name, then a unique partial match of "Family: Type".
/// A name that fits more than one type is an error listing them, never a silent first pick.
/// </summary>
internal static class TypeMatch
{
    public static string Label(ElementType t) =>
        string.IsNullOrEmpty(t.FamilyName) ? t.Name : $"{t.FamilyName}: {t.Name}";

    /// <summary>The single type <paramref name="name"/> names, or throws ArgumentException (not found or ambiguous).</summary>
    public static T Find<T>(IEnumerable<T> types, string name, string what) where T : ElementType =>
        TryFind(types, name, what, out var match, out var error) ? match! : throw new ArgumentException(error);

    public static bool TryFind<T>(IEnumerable<T> types, string name, string what, out T? match, out string? error) where T : ElementType
    {
        var all = types.OrderBy(t => t.FamilyName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
        match = null;
        error = null;
        var wanted = (name ?? "").Trim();
        if (all.Count == 0) { error = $"No {what} type is loaded."; return false; }
        if (wanted.Length == 0) { error = $"Empty {what} name. Loaded: {Short(all)}"; return false; }

        List<T> hits = [];
        // Family and type names cannot contain ':', so the first colon splits them.
        int colon = wanted.IndexOf(':');
        if (colon > 0)
        {
            var fam = wanted.Substring(0, colon).Trim();
            var typ = wanted.Substring(colon + 1).Trim();
            hits = all.Where(t => Same(t.FamilyName, fam) && Same(t.Name, typ)).ToList();
        }
        if (hits.Count == 0) hits = all.Where(t => Same(t.FamilyName, wanted)).ToList();
        if (hits.Count == 0) hits = all.Where(t => Same(t.Name, wanted)).ToList();
        if (hits.Count == 0)
            hits = all.Where(t => Label(t).IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0
                                  || (t.Name ?? "").IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

        if (hits.Count == 1) { match = hits[0]; return true; }
        error = hits.Count == 0
            ? $"No {what} called '{name}'. Loaded: {Short(all)}"
            : $"'{name}' matches {hits.Count} {what} types: {Short(hits)}. Give it as 'Family: Type'.";
        return false;
    }

    /// <summary>A short list of type labels for error messages.</summary>
    public static string Short<T>(IReadOnlyList<T> types, int max = 15) where T : ElementType =>
        string.Join(", ", types.Take(max).Select(Label)) + (types.Count > max ? $" (+{types.Count - max} more)" : "");

    private static bool Same(string? a, string b) => string.Equals((a ?? "").Trim(), b, StringComparison.OrdinalIgnoreCase);
}
