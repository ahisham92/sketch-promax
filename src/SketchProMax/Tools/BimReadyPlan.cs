namespace SketchProMax.Tools;

/// <summary>
/// Make it BIM-ready (UI/BimReadyWindow): which of the checklist's fixes run, in order. Only fixes that fill or set
/// things the BEP and LOI ask for: nothing here deletes, unloads or exports (those stay the user's call in the checklist).
/// </summary>
internal static class BimReadyPlan
{
    /// <param name="Key">The check and fix key (ChecklistFixes.Run).</param>
    /// <param name="What">What it fills, in a few words.</param>
    /// <param name="Rule">The Checking rule it needs set first; empty when it needs none.</param>
    /// <param name="Check">The checklist check that says whether it is needed, when not <paramref name="Key"/>.</param>
    public sealed record Step(string Key, string What, string Rule, string? Check = null)
    {
        public string CheckKey => Check ?? Key;
    }

    public static readonly IReadOnlyList<Step> Steps =
    [
        new("units", "the project units", "units"),
        new("uniformat", "the UniFormat codes", "uniformatmap"),
        new("worksets", "the worksets", "worksetmap"),
        new("loi", "the LOI data", "loimatrix"),
        new("scopeboxes", "the level and scope box names from the URS", ""),
        new("titleblock", "the title block on the sheets", ""),
        new("tbmodelname", "the model name on the sheets", "tbmodelname"),
        new("viewtemplates", "the view templates", "templatemap"),
        new("viewfolders", "the view folders", "viewfolders"),
        new("navisview", "the Navisworks view", ""),
        new("landing", "the starting view", ""),
        // Revit's warnings, toward zero: the ones a fix can clear without deleting anything.
        new("offaxis", "the slightly off axis lines (straightened)", "", "warnings"),
        new("unjoinapart", "joins of elements that don't touch (unjoined)", "", "warnings"),
    ];

    /// <summary>Whether Make it BIM-ready fixes this check.</summary>
    public static bool Fixes(string key) => Steps.Any(s => s.Key == key);

    /// <summary>
    /// The steps to run: those whose rule is set, unless the model check says they already pass. <paramref name="flagged"/>:
    /// true when the check found something to fix, false when it passes, null when it wasn't read (then it runs, since
    /// each fix leaves filled values alone). A step with no rule runs only when flagged, so nothing is made that isn't missing.
    /// </summary>
    public static List<Step> ToRun(Func<string, string> rule, Func<string, bool?> flagged) =>
        Steps.Where(s => flagged(s.CheckKey) != false && (s.Rule.Length > 0 ? rule(s.Rule).Trim().Length > 0 : flagged(s.CheckKey) == true)).ToList();

    /// <summary>
    /// How far to turn a line (degrees, anticlockwise) to put it exactly on the nearest 45° direction, when it is off by
    /// less than <paramref name="within"/> degrees: what Revit calls slightly off axis. Null when it is on axis or meant to be at an angle.
    /// </summary>
    public static double? OffAxisTurn(double angleDegrees, double within = 1.0)
    {
        var nearest = Math.Round(angleDegrees / 45.0) * 45.0;
        var turn = nearest - angleDegrees;
        return Math.Abs(turn) < 1e-9 || Math.Abs(turn) >= within ? null : turn;
    }

    /// <summary>The most an end of a line may move when it is straightened (millimetres).</summary>
    public const double MaxMoveMm = 2.0;

    /// <summary>How far each end of a line <paramref name="lengthMm"/> long moves when turned <paramref name="turnDegrees"/> about its middle.</summary>
    public static double EndMoveMm(double lengthMm, double turnDegrees) => lengthMm / 2 * Math.Abs(Math.Sin(turnDegrees * Math.PI / 180.0));
}
