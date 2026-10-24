namespace SketchProMax.Sharing;

/// <summary>
/// Where the TritoRevit tab goes on Revit's ribbon (2026-10-24): right after the Sketch ProMax tab; without
/// Sketch ProMax, right after the SketchPro add-in's tab; without either, first of the add-in tabs (right after Revit's
/// Add-Ins tab, else after Manage). Pure, so the tests check it; UI/TritoTabOrder moves the tab.
/// </summary>
internal static class TritoTabMath
{
    public const string Trito = "TritoRevit", ProMax = "Sketch ProMax";

    /// <summary>
    /// The index TritoRevit's tab should move to in <paramref name="tabs"/> (ids and titles, in ribbon order), or -1 when
    /// it is where it belongs, or there is no TritoRevit tab or nothing to place it by.
    /// </summary>
    public static int Target(IReadOnlyList<(string Id, string Title)> tabs)
    {
        int Find(Func<string, bool> match)
        {
            for (var i = 0; i < tabs.Count; i++)
                if (match(tabs[i].Id) || match(tabs[i].Title)) return i;
            return -1;
        }
        var trito = Find(t => Is(t, Trito));
        if (trito < 0) return -1;
        var anchor = Find(t => Is(t, ProMax));
        if (anchor < 0) anchor = Find(IsSketchPro);
        if (anchor < 0) anchor = Find(t => Is(t, "Add-Ins"));
        if (anchor < 0) anchor = Find(t => Is(t, "Manage"));
        if (anchor < 0 || trito == anchor + 1) return -1;
        // Taken out first, so a tab before the anchor moves the anchor one place left.
        return trito < anchor ? anchor : anchor + 1;
    }

    private static bool Is(string? a, string b) => a is not null && string.Equals(a.Trim(), b, StringComparison.OrdinalIgnoreCase);

    /// <summary>"SketchPro", "Sketch Pro", "Sketch-Pro 2" and the like; not "Sketch ProMax" (as the loader's TabOrder).</summary>
    public static bool IsSketchPro(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var plain = new string(text!.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (!plain.StartsWith("sketchpro", StringComparison.Ordinal)) return false;
        var rest = plain.Substring("sketchpro".Length);
        return rest.Length == 0 || rest.All(char.IsDigit);
    }
}
