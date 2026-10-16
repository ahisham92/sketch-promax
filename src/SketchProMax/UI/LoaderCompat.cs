namespace SketchProMax.UI;

/// <summary>
/// Whether the loader Revit started with (it only changes when Revit restarts) shows everything this core expects on
/// the ribbon and in the pane. Pure logic, no Revit or WPF types, so the tests compile it.
/// </summary>
internal static class LoaderCompat
{
    /// <summary>
    /// The release whose loader last added buttons or pane parts of its own (SketchProMax.Loader Ribbon.cs / Pane.cs).
    /// Raise it whenever the loader's own ribbon or pane gains something: a core running under an older loader then
    /// tells the user to restart Revit to see it. Buttons a core declares in its manifest (CoreRibbonManifest) don't
    /// need this; a loader from 0.30 on adds them live.
    /// </summary>
    public static readonly Version RibbonChangedIn = new(0, 30, 0);

    /// <summary>What the loader publishes as its release ("0.30.0"); loaders before 0.30 publish nothing.</summary>
    public const string ReleaseKey = "SketchProMax.Loader.Release";

    public const string OldLoaderNote =
        "Restart Revit to see the new buttons and pane. This version is already running; only the ribbon and the pane " +
        "around it are from the version Revit started with.";

    /// <summary>True when a loader of release <paramref name="loaderRelease"/> (null or empty: before 0.30) is older than <see cref="RibbonChangedIn"/>.</summary>
    public static bool RibbonOutdated(string? loaderRelease) =>
        !Version.TryParse(loaderRelease, out var v) || Normalize(v) < Normalize(RibbonChangedIn);

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
}
