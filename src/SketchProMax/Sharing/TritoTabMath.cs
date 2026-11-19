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

    // ---- The version on the tab (2026-11-19): the first panel's title and the Update button say which version runs.

    /// <summary>The first panel's name and the Update button's name, as the TritoRevit add-in makes them.</summary>
    public const string Panel = "TritoRevit", UpdateButton = "TritoRevit.Update";

    /// <summary>"0.35.80" for an assembly version; "" when there is none.</summary>
    public static string Short(Version? version) => version is null ? "" : $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";

    /// <summary>The first panel's title: "TritoRevit 0.35.80".</summary>
    public static string PanelTitle(string version) => version.Length > 0 ? $"{Trito} {version}" : Trito;

    /// <summary>The Update button's two lines: "Update" over the version that runs.</summary>
    public static string UpdateText(string version) => version.Length > 0 ? "Update\n" + version : "Update";

    /// <summary>The Update button's tip, starting with the version that runs.</summary>
    public static string UpdateTip(string version) =>
        (version.Length > 0 ? $"TritoRevit {version} is running. " : "") + "Click to check for updates now: TritoRevit itself and the shared tools. It says when everything is up to date; otherwise it updates, with Revit open and no restart.";

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
