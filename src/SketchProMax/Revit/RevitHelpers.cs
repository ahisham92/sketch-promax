using Autodesk.Revit.DB;

namespace SketchProMax.Revit;

internal static class RevitHelpers
{
    /// <summary>Accepts "OST_Doors", "Doors" or "Structural Columns".</summary>
    public static BuiltInCategory ParseCategory(Document doc, string name)
    {
        var trimmed = name.Trim();
        if (Enum.TryParse<BuiltInCategory>(trimmed, true, out var bic)) return bic;
        if (Enum.TryParse("OST_" + trimmed.Replace(" ", ""), true, out bic)) return bic;

        foreach (Category c in doc.Settings.Categories)
        {
            if (string.Equals(c.Name, trimmed, StringComparison.OrdinalIgnoreCase))
                return (BuiltInCategory)c.Id.Value;
        }
        throw new ArgumentException($"Unknown category '{name}'.");
    }

    public static ForgeTypeId LengthUnit(Document doc) =>
        doc.GetUnits().GetFormatOptions(SpecTypeId.Length).GetUnitTypeId();

    public static string LengthUnitLabel(Document doc) => LabelUtils.GetLabelForUnit(LengthUnit(doc));

    /// <summary>Converts a length in the project's display unit to Revit internal feet.</summary>
    public static double ToInternal(Document doc, double value) =>
        UnitUtils.ConvertToInternalUnits(value, LengthUnit(doc));

    public static double FromInternal(Document doc, double feet) =>
        Math.Round(UnitUtils.ConvertFromInternalUnits(feet, LengthUnit(doc)), 3);

    public static Level FindLevel(Document doc, string? name)
    {
        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
        if (levels.Count == 0) throw new InvalidOperationException("The model has no levels.");
        if (string.IsNullOrWhiteSpace(name)) return levels[0];
        return levels.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase))
               ?? throw new ArgumentException($"No level named '{name}'. Levels: {string.Join(", ", levels.Select(l => l.Name))}");
    }

    public static string? ParamValue(Parameter p) => p.StorageType switch
    {
        StorageType.String => p.AsString(),
        StorageType.ElementId => p.AsValueString() ?? p.AsElementId().Value.ToString(),
        StorageType.None => null,
        _ => p.AsValueString(),
    };

    public static object ElementSummary(Document doc, Element e)
    {
        var typeId = e.GetTypeId();
        var type = typeId != ElementId.InvalidElementId ? doc.GetElement(typeId) : null;
        var level = e.LevelId != ElementId.InvalidElementId ? doc.GetElement(e.LevelId)?.Name : null;
        return new
        {
            id = e.Id.Value,
            category = e.Category?.Name,
            name = e.Name,
            family = (type as ElementType)?.FamilyName,
            type = type?.Name,
            level,
        };
    }

    public static Transaction StartTransaction(Document doc, string name)
    {
        var t = new Transaction(doc, $"Sketch ProMax: {name}");
        t.Start();
        return t;
    }
}
