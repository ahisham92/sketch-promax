using System.IO;
using Autodesk.Revit.DB;

namespace SketchProMax.Tools;

/// <summary>
/// The hand-offs tools throw (<see cref="ManualStepException"/>) when only the user can unblock them, worded once here
/// so every tool says the same thing: what to do, where in Revit, why, and how Continue knows it is done.
/// </summary>
internal static class ManualSteps
{
    /// <summary>No legend view to start from (create_legend, save_legend_seed, place_legend_on_sheets).</summary>
    public static ManualStepException NoLegend(Document doc, string? lookedIn = null) => new(new HandoffStep
    {
        Kind = "api_limit",
        What = "Make one legend view by hand (any name and scale)",
        Where = "View tab > Create panel > Legends > Legend",
        Why = "Revit's API can't create a legend from nothing (API limit), only duplicate one. Afterwards, save_legend_seed keeps a copy " +
              "so later projects start from it" + (lookedIn is null ? "." : $". Looked in: {lookedIn}."),
        Recheck = () => LegendSeeds.Legends(doc).Count > 0,
    });

    /// <summary>No legend component anywhere to copy (add_legend_components).</summary>
    public static ManualStepException NoLegendComponent(Document doc, string? lookedIn) => new(new HandoffStep
    {
        Kind = "legend_component",
        What = "Place one legend component by hand in any legend (any family type)",
        Where = "Open a legend view, then Annotate tab > Detail panel > Component > Legend Component, pick a type on the Options Bar and click to place it",
        Why = "Revit's API can't create legend components (API limit); it can only copy one that exists and change its type. " +
              "One is enough: every other component is copied from it. A firm legend seed file with a component (Settings) also works" +
              (lookedIn is null ? "." : $". Looked in: {lookedIn}."),
        Recheck = () => LegendComponents.Any(doc),
    });

    /// <summary>A schedule the call names that the model doesn't have, and no close name either.</summary>
    public static ManualStepException MissingSchedule(Document doc, string name) => new(new HandoffStep
    {
        Kind = "missing_schedule",
        What = $"Create the schedule '{name}', or rename the one you meant to '{name}'",
        Where = "View tab > Create panel > Schedules > Schedules/Quantities (or right-click a schedule in the Project Browser > Rename)",
        Why = $"This model has no schedule named '{name}' and none with a similar name.",
        Recheck = () => new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
            .Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)),
    });

    /// <summary>A family the tool needs is not loaded.</summary>
    public static ManualStepException MissingFamily(string what, string problem, Func<bool> loaded) => new(new HandoffStep
    {
        Kind = "missing_family",
        What = $"Load the {what} family",
        Where = "Insert tab > Load from Library > Load Family, and pick its .rfa (or Insert > Transfer Project Standards / copy it from a project that has it)",
        Why = problem,
        Recheck = loaded,
    });

    /// <summary>A file the call names (a DWG, an image) is not there.</summary>
    public static ManualStepException MissingFile(string path, string what) => new(new HandoffStep
    {
        Kind = "missing_file",
        What = $"Put the {what} at {path}, or check the name and folder",
        Where = "Windows File Explorer (copy it there, or connect the network drive it is on)",
        Why = $"The {what} '{Path.GetFileName(path)}' was not found at that path, so it can't be brought into Revit.",
        Recheck = () => File.Exists(path),
    });

    /// <summary>A loadable family is needed where an in-place one was given (void cuts, family geometry).</summary>
    public static ManualStepException InPlace(string label, string why) => new(new HandoffStep
    {
        Kind = "in_place",
        What = $"Rebuild '{label}' as a loadable family with the same name, or pass a loadable family instead",
        Where = "Select it > Edit In-Place, copy its forms, then File > New > Family (the guide has every step)",
        Why = why,
        Guide = HelpGuides.InPlaceToLoadable,
    });
}

/// <summary>Legend components (the OST_LegendComponents elements placed in legend views).</summary>
internal static class LegendComponents
{
    public static IEnumerable<Element> In(Document doc, View legend) =>
        new FilteredElementCollector(doc, legend.Id).OfCategory(BuiltInCategory.OST_LegendComponents).WhereElementIsNotElementType()
            .Where(e => e.OwnerViewId == legend.Id);

    public static bool Any(Document doc) =>
        new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_LegendComponents).WhereElementIsNotElementType().Any();
}
