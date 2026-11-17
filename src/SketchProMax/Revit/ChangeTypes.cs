using Autodesk.Revit.DB;
using M = SketchProMax.Revit.ChangeFileMath;

namespace SketchProMax.Revit;

/// <summary>
/// Types in the change file: a type made after the last save is duplicated again from a type of the same family (its
/// values then set as in the file), and a wall, floor, roof or ceiling type gets its layers back. A family loaded
/// after the last save is loaded again from its copy kept next to the change file; a material is made again with its
/// colours, patterns and assets.
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
        var source = kin.FirstOrDefault(e => family is not null && e.FamilyName == family);
        // A loadable family's type is only ever a copy of a type of that family (a type of another family is another thing).
        if (source is null && family is not null && kin.OfType<FamilySymbol>().Any())
            throw new InvalidOperationException($"the family {family} isn't in this model: load it, then replay again");
        source ??= kin.FirstOrDefault() ?? throw new InvalidOperationException("there is no type of this kind in the model to duplicate: make it again by hand");
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
            // The type's own structure with new layers keeps what else it holds (a floor's has no end caps, a wall's has).
            try
            {
                cs.SetLayers(layers);
                if (line.Shells is [var outer, var inner])
                {
                    cs.SetNumberOfShellLayers(ShellLayerType.Exterior, outer);
                    cs.SetNumberOfShellLayers(ShellLayerType.Interior, inner);
                }
                host.SetCompoundStructure(cs);
                return true;
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or ArgumentException) { /* a structure made from scratch is tried next */ }
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

    // ---- Families ---------------------------------------------------------------------------------------------------------

    /// <summary>The family of that name (and category) in the model, if it is there.</summary>
    public static Family? FamilyThere(Document doc, M.Line line) =>
        new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
            .FirstOrDefault(f => f.Name == line.Name && (line.CategoryId == 0 || f.FamilyCategoryId.Value == line.CategoryId));

    public static string? CanMakeFamily(M.Line line, string familiesFolder) =>
        line.File is { Length: > 0 } file && System.IO.File.Exists(System.IO.Path.Combine(familiesFolder, file)) ? null
        : "a family loaded after the last save whose copy wasn't kept: load it again, then replay again";

    /// <summary>Loads the family again from its copy kept next to the change file, under its own name.</summary>
    public static Family LoadFamily(Document doc, M.Line line, string familiesFolder)
    {
        var kept = System.IO.Path.Combine(familiesFolder, line.File!);
        // Revit names a family after its file: a copy under the family's name, in a folder of its own, is what is loaded.
        var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SketchProMax-lifeline-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        System.IO.Directory.CreateDirectory(folder);
        var bad = System.IO.Path.GetInvalidFileNameChars();
        var named = System.IO.Path.Combine(folder, new string((line.Name ?? "Family").Select(c => bad.Contains(c) ? '_' : c).ToArray()) + ".rfa");
        try
        {
            System.IO.File.Copy(kept, named, true);
            if (!doc.LoadFamily(named, new KeepValues(), out var family) || family is null)
                throw new InvalidOperationException($"Revit didn't load the family {line.Name} from its kept copy");
            if (line.Name is { Length: > 0 } name && family.Name != name) try { family.Name = name; } catch (Exception) { }
            return family;
        }
        finally
        {
            try { System.IO.Directory.Delete(folder, true); } catch (Exception) { }
        }
    }

    private sealed class KeepValues : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues) { overwriteParameterValues = false; return true; }

        public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
        {
            source = FamilySource.Family;
            overwriteParameterValues = false;
            return true;
        }
    }

    // ---- Materials ---------------------------------------------------------------------------------------------------------

    /// <summary>A material's colours, patterns and assets (its other values are in the line's values).</summary>
    public static void ReadMaterial(Document doc, Material m, M.Line line)
    {
        var x = new Dictionary<string, string>(StringComparer.Ordinal);
        void Colour(string key, Func<Color> get) { try { if (get() is { IsValid: true } c) x[key] = $"{c.Red},{c.Green},{c.Blue}"; } catch (Exception) { } }
        void Ref(string key, Func<ElementId> get) { try { if (doc.GetElement(get())?.UniqueId is { } u) x[key] = u; } catch (Exception) { } }
        void Whole(string key, Func<int> get) { try { x[key] = get().ToString(System.Globalization.CultureInfo.InvariantCulture); } catch (Exception) { } }
        Colour("color", () => m.Color);
        Whole("transparency", () => m.Transparency);
        Whole("shininess", () => m.Shininess);
        Whole("smoothness", () => m.Smoothness);
        Whole("shading", () => m.UseRenderAppearanceForShading ? 1 : 0);
        Colour("surfaceFgColor", () => m.SurfaceForegroundPatternColor);
        Colour("surfaceBgColor", () => m.SurfaceBackgroundPatternColor);
        Colour("cutFgColor", () => m.CutForegroundPatternColor);
        Colour("cutBgColor", () => m.CutBackgroundPatternColor);
        Ref("surfaceFg", () => m.SurfaceForegroundPatternId);
        Ref("surfaceBg", () => m.SurfaceBackgroundPatternId);
        Ref("cutFg", () => m.CutForegroundPatternId);
        Ref("cutBg", () => m.CutBackgroundPatternId);
        Ref("appearance", () => m.AppearanceAssetId);
        Ref("structural", () => m.StructuralAssetId);
        Ref("thermal", () => m.ThermalAssetId);
        try { if (m.MaterialClass is { Length: > 0 } c) x["class"] = c; } catch (Exception) { }
        if (x.Count > 0) line.Extra = x;
    }

    public static string? CanMakeMaterial(M.Line line) => line.Name is { Length: > 0 } ? null : "a material made after the last save whose name isn't in the file: make it again by hand";

    /// <summary>The material again: one of that name already there, else a new one.</summary>
    public static Material MakeMaterial(Document doc, M.Line line)
    {
        var there = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().FirstOrDefault(m => m.Name == line.Name);
        return there ?? (Material)doc.GetElement(Material.Create(doc, line.Name!));
    }

    /// <summary>Sets the colours, patterns and assets back; true when anything changed.</summary>
    public static bool SetMaterial(Document doc, Material m, M.Line line, Func<string?, Element?> find)
    {
        if (line.Extra is not { } x) return false;
        var now = new M.Line();
        ReadMaterial(doc, m, now);
        var changed = false;
        Color? Colour(string key) => x.TryGetValue(key, out var text) && text.Split(',') is [var r, var g, var b] && byte.TryParse(r, out var red) && byte.TryParse(g, out var green) && byte.TryParse(b, out var blue)
            ? new Color(red, green, blue) : null;
        bool Differs(string key) => x.TryGetValue(key, out var want) && !(now.Extra is { } n && n.TryGetValue(key, out var have) && have == want);
        void Do(string key, Action set) { if (!Differs(key)) return; try { set(); changed = true; } catch (Exception) { /* a value Revit won't take here: left */ } }
        int Whole(string key) => int.Parse(x[key], System.Globalization.CultureInfo.InvariantCulture);
        ElementId Id(string key) => find(x[key])?.Id ?? throw new InvalidOperationException("not in this model");
        Do("color", () => m.Color = Colour("color")!);
        Do("transparency", () => m.Transparency = Whole("transparency"));
        Do("shininess", () => m.Shininess = Whole("shininess"));
        Do("smoothness", () => m.Smoothness = Whole("smoothness"));
        Do("shading", () => m.UseRenderAppearanceForShading = Whole("shading") == 1);
        Do("surfaceFg", () => m.SurfaceForegroundPatternId = Id("surfaceFg"));
        Do("surfaceBg", () => m.SurfaceBackgroundPatternId = Id("surfaceBg"));
        Do("cutFg", () => m.CutForegroundPatternId = Id("cutFg"));
        Do("cutBg", () => m.CutBackgroundPatternId = Id("cutBg"));
        Do("surfaceFgColor", () => m.SurfaceForegroundPatternColor = Colour("surfaceFgColor")!);
        Do("surfaceBgColor", () => m.SurfaceBackgroundPatternColor = Colour("surfaceBgColor")!);
        Do("cutFgColor", () => m.CutForegroundPatternColor = Colour("cutFgColor")!);
        Do("cutBgColor", () => m.CutBackgroundPatternColor = Colour("cutBgColor")!);
        Do("appearance", () => m.AppearanceAssetId = Id("appearance"));
        Do("structural", () => m.StructuralAssetId = Id("structural"));
        Do("thermal", () => m.ThermalAssetId = Id("thermal"));
        Do("class", () => m.MaterialClass = x["class"]);
        return changed;
    }
}
