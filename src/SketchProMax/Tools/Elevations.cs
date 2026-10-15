using Autodesk.Revit.DB;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

/// <summary>
/// Absolute elevations as the user reads them in Revit. A level type's "Elevation Base" (Project Base Point or
/// Survey Point) decides what Revit displays, which is generally not the internal Z: displayed = Z - base Z.
/// Tools that take or return absolute elevations go through here so they agree with what Revit shows
/// (and with get_model_context, which reports Level.Elevation).
/// </summary>
internal static class Elevations
{
    /// <summary>The level type new levels get (the model's default level type, else the first one).</summary>
    public static ElementId DefaultLevelTypeId(Document doc)
    {
        var id = doc.GetDefaultElementTypeId(ElementTypeGroup.LevelType);
        if (id != ElementId.InvalidElementId && doc.GetElement(id) is LevelType) return id;
        return new FilteredElementCollector(doc).OfClass(typeof(LevelType)).FirstElementId();
    }

    /// <summary>
    /// Internal Z of the elevation base used by levels of this type (internal units). Revit's own answer
    /// (ProjectElevation - Elevation of an existing level of the type) is used when there is one; otherwise the
    /// type's Elevation Base parameter picks the project base point or the shared (survey) origin.
    /// </summary>
    public static double BaseZ(Document doc, ElementId? levelTypeId = null)
    {
        var typeId = levelTypeId is null || levelTypeId == ElementId.InvalidElementId ? DefaultLevelTypeId(doc) : levelTypeId;
        var sample = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
            .FirstOrDefault(l => typeId == ElementId.InvalidElementId || l.GetTypeId() == typeId);
        if (sample != null) return sample.ProjectElevation - sample.Elevation;

        var basis = doc.GetElement(typeId)?.get_Parameter(BuiltInParameter.LEVEL_RELATIVE_BASE_TYPE)?.AsInteger() ?? 0;
        try
        {
            if (basis == 1) // Survey Point: displayed elevations are shared-coordinate elevations
                return Coordinates.InternalToShared(doc).Inverse.OfPoint(XYZ.Zero).Z;
            return BasePoint.GetProjectBasePoint(doc)?.Position.Z ?? 0;
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            return 0;
        }
    }

    /// <summary>User elevation (project units, as Revit displays it) to internal Z.</summary>
    public static double ToInternalZ(Document doc, double elevation, ElementId? levelTypeId = null) =>
        RevitHelpers.ToInternal(doc, elevation) + BaseZ(doc, levelTypeId);

    /// <summary>Internal Z to the elevation Revit displays (project units).</summary>
    public static double FromInternalZ(Document doc, double z, ElementId? levelTypeId = null) =>
        RevitHelpers.FromInternal(doc, z - BaseZ(doc, levelTypeId));

    /// <summary>The elevation Revit displays for this level (project units).</summary>
    public static double Displayed(Document doc, Level level) => RevitHelpers.FromInternal(doc, level.Elevation);
}
