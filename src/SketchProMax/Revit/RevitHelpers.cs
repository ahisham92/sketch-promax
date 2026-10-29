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

        if (Aliases.TryGetValue(trimmed, out bic)) return bic;

        // One more or one fewer "s": "Duct" for Ducts, "Pipe" for Pipes (2026-10-29: find_clashes refused 'Duct').
        var forms = new[] { trimmed + "s", trimmed + "es", trimmed.EndsWith("s", StringComparison.OrdinalIgnoreCase) ? trimmed.Substring(0, trimmed.Length - 1) : "" }
            .Where(f => f.Length > 0).ToList();
        Category? plural = null;
        var near = new List<string>();
        var words = trimmed.Split([' '], StringSplitOptions.RemoveEmptyEntries).Select(w => w.TrimEnd('s')).Where(w => w.Length >= 3).ToList();
        foreach (Category c in doc.Settings.Categories)
        {
            if (string.Equals(c.Name, trimmed, StringComparison.OrdinalIgnoreCase))
                return (BuiltInCategory)c.Id.Value;
            if (plural is null && forms.Any(f => string.Equals(c.Name, f, StringComparison.OrdinalIgnoreCase))) plural = c;
            if (words.Any(w => c.Name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0)) near.Add(c.Name);
        }
        if (plural is not null) return (BuiltInCategory)plural.Id.Value;
        throw new ArgumentException($"Unknown category '{name}'." + (near.Count > 0 ? $" Close ones: {string.Join(", ", near.OrderBy(n => n).Take(8))}." : ""));
    }

    /// <summary>Names users and the chat give that are neither the enum's nor Revit's category name (both were refused in chats).</summary>
    private static readonly Dictionary<string, BuiltInCategory> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Detail Groups"] = BuiltInCategory.OST_IOSDetailGroups,
        ["Detail Group"] = BuiltInCategory.OST_IOSDetailGroups,
        ["Model Groups"] = BuiltInCategory.OST_IOSModelGroups,
        ["Model Group"] = BuiltInCategory.OST_IOSModelGroups,
        ["Groups"] = BuiltInCategory.OST_IOSModelGroups,
        ["Slab Edges"] = BuiltInCategory.OST_EdgeSlab,
        ["Slab Edge"] = BuiltInCategory.OST_EdgeSlab,
        ["Floor Slab Edges"] = BuiltInCategory.OST_EdgeSlab,
    };

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
