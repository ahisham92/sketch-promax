using Autodesk.Revit.DB;
using SketchProMax.Tools;
using SketchProMax.UI;

namespace SketchProMax.Commands;

/// <summary>
/// Lists from the open model for a ribbon tool's options window (0.35), so nobody types an id or a level name: the
/// levels, lowest first, for every text field named like a level, and the types a tool's type_id takes for the tools
/// that place one kind of thing. A field whose list would be empty keeps its plain box. Revit API context only.
/// </summary>
internal static class RibbonChoices
{
    public const int MaxTypes = 300;

    /// <summary>The categories (or type classes) whose types each tool's type_id takes.</summary>
    private static readonly Dictionary<string, Func<Document, IEnumerable<ElementType>>> TypesFor = new(StringComparer.Ordinal)
    {
        ["place_columns"] = doc => OfCategories(doc, BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_Columns),
        ["place_beams"] = doc => OfCategories(doc, BuiltInCategory.OST_StructuralFraming),
        ["place_bracing"] = doc => OfCategories(doc, BuiltInCategory.OST_StructuralFraming),
        ["place_doors_windows"] = doc => OfCategories(doc, BuiltInCategory.OST_Doors, BuiltInCategory.OST_Windows),
        ["set_sheet_title_block"] = doc => OfCategories(doc, BuiltInCategory.OST_TitleBlocks),
        ["create_floors"] = doc => OfClass<FloorType>(doc),
        ["create_ceilings"] = doc => OfClass<CeilingType>(doc),
        ["create_roof"] = doc => OfClass<RoofType>(doc),
    };

    /// <summary>The fields with the model's lists added where they help.</summary>
    public static List<InputsWindow.Field> Add(Document doc, string tool, List<InputsWindow.Field> fields)
    {
        IReadOnlyList<string>? levels = null;
        IReadOnlyList<InputsWindow.Option>? types = null;
        var result = new List<InputsWindow.Field>(fields.Count);
        foreach (var f in fields)
        {
            var field = f;
            try
            {
                if (f.Choices is null && f.Type == "string" && ToolForm.IsLevel(f.Key))
                {
                    levels ??= Levels(doc);
                    if (levels.Count > 0) field = f with { Suggestions = levels };
                }
                else if (f.Key == "type_id" && f.Type == "integer" && TypesFor.TryGetValue(tool, out var of))
                {
                    types ??= Options(of(doc));
                    if (types.Count > 0) field = f with { Options = types };
                }
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or InvalidOperationException) { field = f; }
            result.Add(field);
        }
        return result;
    }

    private static List<string> Levels(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).Select(l => l.Name).Distinct().ToList();

    private static List<InputsWindow.Option> Options(IEnumerable<ElementType> types) =>
        types.Select(t => (Text: string.IsNullOrEmpty(t.FamilyName) ? t.Name : $"{t.FamilyName}: {t.Name}", Id: t.Id.Value))
            .OrderBy(t => t.Text, StringComparer.OrdinalIgnoreCase).Take(MaxTypes)
            .Select(t => new InputsWindow.Option(t.Text, t.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToList();

    private static IEnumerable<ElementType> OfCategories(Document doc, params BuiltInCategory[] categories) =>
        new FilteredElementCollector(doc).WhereElementIsElementType().WherePasses(new ElementMulticategoryFilter(categories)).Cast<ElementType>();

    private static IEnumerable<ElementType> OfClass<T>(Document doc) where T : ElementType =>
        new FilteredElementCollector(doc).OfClass(typeof(T)).Cast<ElementType>();
}
