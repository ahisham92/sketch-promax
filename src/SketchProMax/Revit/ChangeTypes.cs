using Autodesk.Revit.DB;
using M = SketchProMax.Revit.ChangeFileMath;

namespace SketchProMax.Revit;

/// <summary>
/// Types in the change file: a type made after the last save is duplicated again from a type of the same family (its
/// values then set as in the file), and a wall, floor, roof or ceiling type gets its layers back.
/// </summary>
internal static class ChangeTypes
{
    public static void Read(Document doc, ElementType type, M.Line line)
    {
        line.Type = $"{type.FamilyName}: {type.Name}";
        line.Class = type.GetType().Name;
        if (type is HostObjAttributes host && host.GetCompoundStructure() is { } cs) ReadLayers(doc, cs, line);
    }

    private static void ReadLayers(Document doc, CompoundStructure cs, M.Line line)
    {
        line.Layers = cs.GetLayers().Select(l => new M.Layer
        {
            Width = M.Round(l.Width), Function = (int)l.Function,
            Material = l.MaterialId == ElementId.InvalidElementId ? null : doc.GetElement(l.MaterialId)?.UniqueId,
        }).ToList();
        line.Shells = [cs.GetNumberOfShellLayers(ShellLayerType.Exterior), cs.GetNumberOfShellLayers(ShellLayerType.Interior)];
    }

    public static string? CanMake(M.Line line) =>
        line.Name is { Length: > 0 } && line.Class is not null ? null : "a type made after the last save: duplicate it again by hand";

    /// <summary>The type again: one of that name already there, else a duplicate of a type of the same family and kind.</summary>
    public static ElementType Make(Document doc, M.Line line)
    {
        var name = line.Name!;
        var family = line.Type is { } t && t.IndexOf(": ", StringComparison.Ordinal) is var cut and > 0 ? t.Substring(0, cut) : null;
        var kin = new FilteredElementCollector(doc).WhereElementIsElementType().Cast<ElementType>()
            .Where(e => e.GetType().Name == line.Class && (line.CategoryId == 0 || e.Category?.Id.Value == line.CategoryId)).ToList();
        if (kin.FirstOrDefault(e => e.Name == name && (family is null || e.FamilyName == family)) is { } there) return there;
        var source = kin.FirstOrDefault(e => family is not null && e.FamilyName == family) ?? kin.FirstOrDefault()
                     ?? throw new InvalidOperationException("there is no type of this kind in the model to duplicate: make it again by hand");
        return source.Duplicate(name);
    }

    /// <summary>Sets the layers back; true when they changed.</summary>
    public static bool Set(Document doc, ElementType type, M.Line line, Func<string?, Element?> find, List<string> notes)
    {
        if (type is not HostObjAttributes host || line.Layers is not { Count: > 0 } want || host.GetCompoundStructure() is not { } cs) return false;
        var now = new M.Line();
        ReadLayers(doc, cs, now);
        if (M.SameLayers(now.Layers, want) && (line.Shells is null || now.Shells!.SequenceEqual(line.Shells))) return false;
        try
        {
            var layers = want.Select(l => new CompoundStructureLayer(l.Width, (MaterialFunctionAssignment)l.Function,
                find(l.Material) is Material m ? m.Id : ElementId.InvalidElementId)).ToList();
            var made = CompoundStructure.CreateSimpleCompoundStructure(layers);
            if (line.Shells is [var exterior, var interior])
            {
                made.SetNumberOfShellLayers(ShellLayerType.Exterior, exterior);
                made.SetNumberOfShellLayers(ShellLayerType.Interior, interior);
            }
            host.SetCompoundStructure(made);
            return true;
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or ArgumentException)
        {
            notes.Add($"{M.Describe(line)}: its layers didn't come back ({ex.Message}): set them in Edit Type by hand");
            return false;
        }
    }
}
