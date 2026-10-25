using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SketchProMax.Settings;

namespace SketchProMax.Tools;

/// <summary>
/// Fills the element information the BEP's LOI matrix asks for (iLOD). For each model type in use whose classification
/// code (or category) the loimatrix rule names, every parameter it must have that is empty on the type and on some of
/// its elements gets a value: from the loivalues rule first, else read from the model (material, sizes, length, levels,
/// grade). A parameter neither the type nor its elements have is added as a project parameter, from the shared
/// parameter file when it has one of that name. Values already filled are never changed. Plan reads a batch of types
/// per call so the checklist window reads in steps; Apply writes in one transaction (Ctrl+Z undoes it). Revit API
/// context only.
/// </summary>
internal static class LoiFill
{
    /// <summary>The preview's value for a parameter each element takes from its own geometry.</summary>
    public const string EachOwn = "each element's own";

    private const string FromModel = "from the model";

    /// <summary>The checking rules the fill follows.</summary>
    public sealed record Spec(List<(string Key, List<string> Params)> Matrix, List<(string Key, string Param, string Value)> Values, List<string> Classification, string Diameter);

    /// <summary>A value: its text, and the number (internal units) or element it stands for when it has one.</summary>
    internal sealed record V(string Text, double? Number = null, ForgeTypeId? Spec = null, ElementId? Id = null);

    /// <summary>One parameter of one type to fill. Lacking: the elements of the type that have it empty.</summary>
    public sealed record Row(long TypeId, string Param, string Code, string Type, string Category, string Value, bool PerElement, int Lacking, bool NewParameter, string Source)
    {
        public string Key => TypeId.ToString(CultureInfo.InvariantCulture) + "|" + Param;

        public string Label => $"{Code} · {Type} · {Param}" + (NewParameter ? " (new parameter)" : "") + (Lacking > 1 ? $", {Lacking} elements" : "") +
                               (Source.Length > 0 ? $" ({Source})" : "");
    }

    public static Spec SpecOf(ChecklistMath.Definition? def, string? matrix = null, string? values = null, string? classification = null) =>
        new(ChecklistMath.ParseLoi(matrix ?? ChecklistMath.Rule(def, "loimatrix")),
            ChecklistMath.ParseLoiValues(values ?? ChecklistMath.Rule(def, "loivalues")),
            ChecklistMath.RuleList(classification ?? ChecklistMath.Rule(def, "classification")),
            ChecklistMath.RuleSlots(ChecklistMath.Rule(def, "pileparameters"), 3)[2]);

    /// <summary>The model types in use the matrix covers, by category and name: the first, whole-model step.</summary>
    public static List<long> TypeIds(Document doc, Spec spec) =>
        spec.Matrix.Count == 0 ? [] : ChecklistFacts.TypesOf(doc, ChecklistFacts.Instances(doc)).Where(t => Entry(t, spec).Params is not null)
            .OrderBy(t => t.Category?.Name).ThenBy(t => t.FamilyName).ThenBy(t => t.Name).Select(t => t.Id.Value).ToList();

    /// <summary>A type's code (as the LOI check reads it) and the matrix entry it falls under (Params null when none).</summary>
    private static (string Code, List<string>? Params) Entry(ElementType t, Spec spec)
    {
        var code = spec.Classification.Select(n => t.LookupParameter(n)).Where(ChecklistFacts.Filled).Select(p => p!.AsString() ?? p.AsValueString() ?? "").FirstOrDefault() ?? "";
        var category = t.Category?.Name ?? "";
        var entry = spec.Matrix.FirstOrDefault(m => code.Length > 0 && ChecklistMath.Like(code, m.Key) || category.Equals(m.Key, StringComparison.OrdinalIgnoreCase));
        return (code.Length > 0 ? code : category, entry.Params);
    }

    /// <summary>The elements of one type: filtered inside Revit by category and type.</summary>
    private static List<Element> ElementsOf(Document doc, ElementType t)
    {
        if (t.Category is null) return [];
        var rule = ParameterFilterRuleFactory.CreateEqualsRule(new ElementId(BuiltInParameter.ELEM_TYPE_PARAM), t.Id);
        return new FilteredElementCollector(doc).OfCategoryId(t.Category.Id).WhereElementIsNotElementType().WherePasses(new ElementParameterFilter(rule)).ToList();
    }

    /// <summary>What to fill for these types; one row per type and parameter that the LOI check would call missing.</summary>
    public static List<Row> Plan(Document doc, Spec spec, IEnumerable<long> typeIds)
    {
        var rows = new List<Row>();
        var baseZ = Elevations.BaseZ(doc);
        foreach (var id in typeIds)
        {
            if (doc.GetElement(new ElementId(id)) is not ElementType t) continue;
            var (code, wanted) = Entry(t, spec);
            if (wanted is null) continue;
            var elements = ElementsOf(doc, t);
            foreach (var param in wanted)
            {
                if (ChecklistFacts.Filled(t.LookupParameter(param))) continue;
                var lacking = elements.Where(e => !ChecklistFacts.Filled(e.LookupParameter(param))).ToList();
                if (elements.Count > 0 && lacking.Count == 0) continue;
                var onType = t.LookupParameter(param);
                var onElements = lacking.Select(e => e.LookupParameter(param)).FirstOrDefault(p => p is not null);
                var isNew = onType is null && onElements is null;
                // A built-in value Revit works out (and leaves empty) can't be written.
                if (!isNew && (onType?.IsReadOnly ?? true) && (onElements?.IsReadOnly ?? true)) continue;
                var (value, each, source) = Resolve(doc, t, lacking, param, code, spec, baseZ);
                if (each && onElements is null && !isNew)
                    (value, each, source) = ("", false, $"differs by element, but {param} is a type parameter: type one value or leave it empty");
                rows.Add(new Row(id, param, code, ChecklistFacts.TypeLabel(t), t.Category?.Name ?? "", value, each, lacking.Count, isNew, source));
            }
        }
        return rows;
    }

    /// <summary>The preview lines for these types, "type id|parameter\tlabel\tvalue", as ChecklistFixes.Suggest gives them.</summary>
    public static string Suggest(Document doc, Spec spec, IEnumerable<long> typeIds) =>
        string.Join("\n", Plan(doc, spec, typeIds).Select(r => $"{r.Key}\t{r.Label.Replace('\t', ' ')}\t{r.Value.Replace('\t', ' ')}"));

    /// <summary>The value for one parameter: the loivalues rule, else the model; per element when the elements differ.</summary>
    private static (string Value, bool PerElement, string Source) Resolve(Document doc, ElementType t, List<Element> lacking, string param, string code, Spec spec, double baseZ)
    {
        if (Fixed(t, lacking.FirstOrDefault(), param, code, spec) is { } set) return (set.Text, false, "Checking rules");
        var kind = ChecklistMath.LoiSource(param);
        if (kind.Length == 0) return ("", false, "nothing in the model gives it: type it, or leave it empty");
        var got = (lacking.Count == 0 ? [Get(doc, kind, t, null, spec, baseZ)] : lacking.Select(e => Get(doc, kind, t, e, spec, baseZ))).ToList();
        var texts = got.Where(v => v is not null).Select(v => v!.Text).Distinct(StringComparer.Ordinal).ToList();
        if (texts.Count == 0) return ("", false, $"the model has no {kind}: type it, or leave it empty");
        if (texts.Count == 1 && got.All(v => v is not null)) return (texts[0], false, FromModel);
        var numbers = got.Where(v => v?.Number is not null).Select(v => v!).OrderBy(v => v.Number).ToList();
        var range = texts.Count == 1 ? texts[0] : numbers.Count > 1 ? $"{numbers[0].Text} to {numbers[numbers.Count - 1].Text}" : $"{texts.Count} different values";
        var none = got.Count(v => v is null);
        return ($"{EachOwn} ({range})", true, FromModel + (none > 0 ? $"; {none} have none and stay empty" : ""));
    }

    /// <summary>A value the loivalues rule sets for this code or category, with {Parameter} copied from the type or the element.</summary>
    private static V? Fixed(ElementType t, Element? e, string param, string code, Spec spec)
    {
        var category = t.Category?.Name ?? "";
        var hit = spec.Values.FirstOrDefault(v => v.Param.Equals(param, StringComparison.OrdinalIgnoreCase) &&
                                                  (ChecklistMath.Like(code, v.Key) || category.Equals(v.Key, StringComparison.OrdinalIgnoreCase)));
        if (hit.Value is null) return null;
        var missing = false;
        var text = System.Text.RegularExpressions.Regex.Replace(hit.Value, @"\{([^{}]+)\}", m =>
        {
            var name = m.Groups[1].Value.Trim();
            var p = new[] { t.LookupParameter(name), e?.LookupParameter(name) }.FirstOrDefault(ChecklistFacts.Filled);
            var found = p is null ? name.Equals("Type Name", StringComparison.OrdinalIgnoreCase) ? t.Name : name.Equals("Family Name", StringComparison.OrdinalIgnoreCase) ? t.FamilyName : ""
                : p.StorageType == StorageType.String ? p.AsString() ?? "" : p.AsValueString() ?? "";
            if (found.Length == 0) missing = true;
            return found;
        });
        return missing ? null : new V(text.Trim());
    }

    /// <summary>A parameter's value as text, keeping the number for a number parameter.</summary>
    private static V? Read(Document doc, Parameter? p)
    {
        if (!ChecklistFacts.Filled(p)) return null;
        return p!.StorageType switch
        {
            StorageType.String => new V(p.AsString()!.Trim()),
            StorageType.Double => new V(p.AsValueString() ?? "", p.AsDouble(), p.Definition.GetDataType()),
            StorageType.ElementId => doc.GetElement(p.AsElementId()) is { } el ? new V(el.Name, Id: el.Id) : null,
            _ => new V(p.AsValueString() ?? ""),
        };
    }

    private static V Number(Document doc, double value, ForgeTypeId spec) =>
        new(UnitFormatUtils.Format(doc.GetUnits(), spec, value, false, new FormatValueOptions { AppendUnitSymbol = true }), value, spec);

    /// <summary>The first filled parameter of these names on the element, else on its type.</summary>
    private static V? Named(Document doc, ElementType t, Element? e, IEnumerable<string> names)
    {
        foreach (var name in names.Where(n => n.Length > 0))
            if ((Read(doc, e?.LookupParameter(name)) ?? Read(doc, t.LookupParameter(name))) is { } v) return v;
        return null;
    }

    private static V? BuiltIn(Document doc, Element? owner, BuiltInParameter bip) => owner is null ? null : Read(doc, owner.get_Parameter(bip));

    /// <summary>One value the model gives for this kind of parameter (LoiSource), for the element or, with no element, the type.</summary>
    internal static V? Get(Document doc, string kind, ElementType t, Element? e, Spec spec, double baseZ)
    {
        // b is a width, except on round sections, where it is the diameter.
        var name = (t.FamilyName + " " + t.Name).ToLowerInvariant();
        var round = new[] { "round", "circ", "pile", "chs", "pipe" }.Any(name.Contains);
        switch (kind)
        {
            case "material":
                return MaterialOf(doc, t, e) is { } m ? new V(m.Name, Id: m.Id) : null;
            case "grade":
            {
                var grade = new[] { MaterialOf(doc, t, e)?.Name ?? "", t.Name, t.FamilyName }.Select(ChecklistMath.GradeIn).FirstOrDefault(g => g.Length > 0);
                return grade is null ? null : new V(grade);
            }
            case "diameter":
                return Named(doc, t, e, new[] { spec.Diameter, "Diameter", "D", "d" }.Concat(round ? ["b"] : Array.Empty<string>()));
            case "thickness":
                return BuiltIn(doc, e, BuiltInParameter.FLOOR_ATTR_THICKNESS_PARAM) ?? BuiltIn(doc, t, BuiltInParameter.FLOOR_ATTR_DEFAULT_THICKNESS_PARAM)
                    ?? BuiltIn(doc, t, BuiltInParameter.WALL_ATTR_WIDTH_PARAM) ?? BuiltIn(doc, e, BuiltInParameter.STRUCTURAL_FOUNDATION_THICKNESS)
                    ?? (t is HostObjAttributes { } host && host.GetCompoundStructure() is { } cs && cs.GetWidth() > 0 ? Number(doc, cs.GetWidth(), SpecTypeId.Length) : null)
                    ?? Named(doc, t, e, ["Thickness", "Foundation Thickness", "Depth"]);
            case "width":
                return BuiltIn(doc, e, BuiltInParameter.STRUCTURAL_FOUNDATION_WIDTH) ?? Named(doc, t, e, ["Width", "Foundation Width", "b"]);
            case "height":
                return Named(doc, t, e, ["Height", "h"]);
            case "depth":
                return Named(doc, t, e, ["Depth", "Foundation Thickness", "h"]);
            case "volume":
                return BuiltIn(doc, e, BuiltInParameter.HOST_VOLUME_COMPUTED);
            case "area":
                return BuiltIn(doc, e, BuiltInParameter.HOST_AREA_COMPUTED);
            case "level":
            {
                if (e is null) return null;
                var id = e.LevelId != ElementId.InvalidElementId ? e.LevelId
                    : new[] { BuiltInParameter.FAMILY_LEVEL_PARAM, BuiltInParameter.FAMILY_BASE_LEVEL_PARAM, BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM, BuiltInParameter.SCHEDULE_LEVEL_PARAM }
                        .Select(b => e.get_Parameter(b)?.AsElementId()).FirstOrDefault(x => x is not null && x != ElementId.InvalidElementId);
                return id is not null && doc.GetElement(id) is Autodesk.Revit.DB.Level l ? new V(l.Name, Id: l.Id) : null;
            }
            case "length":
            {
                if (e is null) return null;
                foreach (var b in new[] { BuiltInParameter.STRUCTURAL_FRAME_CUT_LENGTH, BuiltInParameter.INSTANCE_LENGTH_PARAM, BuiltInParameter.CURVE_ELEM_LENGTH })
                    if (e.get_Parameter(b) is { HasValue: true, StorageType: StorageType.Double } p && p.AsDouble() > 1e-6) return Number(doc, p.AsDouble(), SpecTypeId.Length);
                if (End(doc, e, top: true) is { } top && End(doc, e, top: false) is { } bottom && top > bottom) return Number(doc, top - bottom, SpecTypeId.Length);
                return e.Location is LocationCurve lc && lc.Curve.Length > 1e-6 ? Number(doc, lc.Curve.Length, SpecTypeId.Length) : null;
            }
            case "top":
            case "bottom":
                return e is not null && (End(doc, e, kind == "top") ?? Box(e, kind == "top")) is { } z ? Number(doc, z - baseZ, SpecTypeId.Length) : null;
            default:
                return null;
        }
    }

    /// <summary>A column's top (top level + offset) or base (base level + offset), internal Z; null for anything else.</summary>
    private static double? End(Document doc, Element e, bool top)
    {
        var (level, offset) = top ? (BuiltInParameter.FAMILY_TOP_LEVEL_PARAM, BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM) : (BuiltInParameter.FAMILY_BASE_LEVEL_PARAM, BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM);
        return e is FamilyInstance && e.get_Parameter(level)?.AsElementId() is { } id && doc.GetElement(id) is Autodesk.Revit.DB.Level l ? l.ProjectElevation + (e.get_Parameter(offset)?.AsDouble() ?? 0) : null;
    }

    private static double? Box(Element e, bool top) => e.get_BoundingBox(null) is { } b ? top ? b.Max.Z : b.Min.Z : null;

    /// <summary>The structural material (element, then type), else the compound structure's structural or first core layer, else the element's first material.</summary>
    private static Material? MaterialOf(Document doc, ElementType t, Element? e)
    {
        foreach (var owner in new Element?[] { e, t })
            if (owner?.get_Parameter(BuiltInParameter.STRUCTURAL_MATERIAL_PARAM)?.AsElementId() is { } id && doc.GetElement(id) is Material m) return m;
        if (t is HostObjAttributes host && host.GetCompoundStructure() is { } cs && cs.LayerCount > 0)
        {
            var index = cs.StructuralMaterialIndex >= 0 ? cs.StructuralMaterialIndex : Math.Max(0, cs.GetFirstCoreLayerIndex());
            if (doc.GetElement(cs.GetMaterialId(index)) is Material layer) return layer;
        }
        if (e is not null)
        {
            try { return e.GetMaterialIds(false).Select(doc.GetElement).OfType<Material>().FirstOrDefault(); }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { }
        }
        return null;
    }

    /// <summary>The answer Apply takes with every value the model (or the loivalues rule) gives, as the preview suggests it; "" when none.</summary>
    public static string AutoAnswer(Document doc, Spec spec, IEnumerable<long> typeIds) =>
        string.Join("\n", Plan(doc, spec, typeIds).Where(r => r.Value.Trim().Length > 0).Select(r => r.Key + "\t" + r.Value));

    /// <summary>
    /// Fills what the user kept in the preview: lines "type id|parameter\tvalue" (EachOwn... takes each element's own).
    /// Missing parameters are added first. A sentence saying what changed.
    /// </summary>
    public static string Apply(Document doc, Spec spec, string? answer)
    {
        var picks = (answer ?? "").Split('\n').Select(l => l.Split('\t')).Where(p => p.Length == 2 && p[0].Contains('|') && p[1].Trim().Length > 0)
            .Select(p => (Key: p[0].Trim(), Value: p[1].Trim())).GroupBy(p => p.Key).Select(g => g.First()).ToList();
        if (picks.Count == 0) return "No LOI value was kept to fill.";
        var typeIds = picks.Select(p => long.TryParse(p.Key.Split('|')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : -1).Where(id => id > 0).Distinct().ToList();
        var rows = Plan(doc, spec, typeIds).ToDictionary(r => r.Key);
        var kept = picks.Where(p => rows.ContainsKey(p.Key)).Select(p => (Row: rows[p.Key], p.Value)).ToList();
        if (kept.Count == 0) return "Those LOI values are already filled: nothing to do.";
        var baseZ = Elevations.BaseZ(doc);
        int types = 0, elements = 0, failed = 0;
        var added = new List<string>();
        var left = new List<string>();
        using (var tx = new Transaction(doc, "Sketch ProMax: fill the LOI data"))
        {
            tx.Start();
            var wanted = kept.Where(k => k.Row.NewParameter).GroupBy(k => k.Row.Param, StringComparer.OrdinalIgnoreCase).Select(g =>
            {
                var each = g.Any(k => k.Value.StartsWith(EachOwn, StringComparison.Ordinal)) || ChecklistMath.LoiSource(g.Key) is "length" or "volume" or "area" or "level" or "top" or "bottom";
                var sample = g.Select(k => doc.GetElement(new ElementId(k.Row.TypeId)) as ElementType).Where(t => t is not null)
                    .Select(t => Get(doc, ChecklistMath.LoiSource(g.Key), t!, ElementsOf(doc, t!).FirstOrDefault(), spec, baseZ)).FirstOrDefault(v => v?.Number is not null);
                var cats = g.Select(k => doc.GetElement(new ElementId(k.Row.TypeId))?.Category).Where(c => c is not null).GroupBy(c => c!.Id.Value).Select(c => c.First()!).ToList();
                return (Name: g.Key, Instance: each, Spec: sample?.Spec ?? SpecTypeId.String.Text, Cats: cats);
            }).ToList();
            if (wanted.Count > 0)
            {
                added.AddRange(AddParameters(doc, wanted));
                doc.Regenerate();
            }
            foreach (var (row, value) in kept)
            {
                if (doc.GetElement(new ElementId(row.TypeId)) is not ElementType t) continue;
                var lacking = ElementsOf(doc, t).Where(e => !ChecklistFacts.Filled(e.LookupParameter(row.Param))).ToList();
                var each = value.StartsWith(EachOwn, StringComparison.Ordinal);
                // The preview's own value from the model keeps its number or material; a typed one is written as typed.
                V? single = null;
                if (!each)
                {
                    single = new V(value);
                    if (value == row.Value && row.Source == FromModel && Get(doc, ChecklistMath.LoiSource(row.Param), t, lacking.FirstOrDefault(), spec, baseZ) is { } got && got.Text == value) single = got;
                }
                var onType = t.LookupParameter(row.Param);
                if (single is not null && onType is { IsReadOnly: false } && !ChecklistFacts.Filled(onType))
                {
                    if (Put(doc, onType, single)) types++; else failed++;
                    continue;
                }
                var did = 0;
                foreach (var e in lacking)
                {
                    var p = e.LookupParameter(row.Param);
                    if (p is null || p.IsReadOnly) continue;
                    var v = single ?? Get(doc, ChecklistMath.LoiSource(row.Param), t, e, spec, baseZ);
                    if (v is null) continue;
                    if (Put(doc, p, v)) did++; else failed++;
                }
                if (did > 0) { elements += did; types++; }
                else left.Add($"{row.Type}: {row.Param}");
            }
            tx.Commit();
        }
        return $"Filled LOI data on {Plural(types, "type")}" + (elements > 0 ? $" ({Plural(elements, "element")} one by one)" : "") + "." +
               (added.Count > 0 ? " Added the project parameters " + string.Join(", ", added) + "." : "") +
               (left.Count > 0 ? $" Not filled (no parameter to write to): {string.Join(", ", left.Take(6))}{(left.Count > 6 ? " and more" : "")}." : "") +
               (failed > 0 ? $" {failed} values couldn't be written (someone else may own the elements, or Revit refused the value)." : "");
    }

    private static string Plural(int n, string what) => $"{n} {what}{(n == 1 ? "" : "s")}";

    /// <summary>Writes a value into a parameter of any storage type.</summary>
    internal static bool Put(Document doc, Parameter p, V v)
    {
        try
        {
            switch (p.StorageType)
            {
                case StorageType.String:
                    return p.Set(v.Text);
                case StorageType.Double:
                    if (v.Number is { } n && v.Spec is { } s && p.Definition.GetDataType().Equals(s)) return p.Set(n);
                    return p.SetValueString(v.Text);
                case StorageType.Integer:
                    return int.TryParse(v.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? p.Set(i) : p.SetValueString(v.Text);
                case StorageType.ElementId:
                    var id = v.Id ?? new FilteredElementCollector(doc).OfClass(typeof(Material)).FirstOrDefault(m => m.Name.Equals(v.Text, StringComparison.OrdinalIgnoreCase))?.Id;
                    return id is not null && p.Set(id);
                default:
                    return false;
            }
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or Autodesk.Revit.Exceptions.ArgumentException) { return false; }
    }

    /// <summary>
    /// Binds the missing parameters to their categories, inside the caller's transaction: an existing project parameter
    /// of that name gets the categories added; else the shared parameter file's definition of that name (the office's
    /// GUID); else a new one from a throwaway file. The names it added.
    /// </summary>
    private static List<string> AddParameters(Document doc, List<(string Name, bool Instance, ForgeTypeId Spec, List<Category> Cats)> wanted)
    {
        var made = new List<string>();
        var app = doc.Application;
        var original = app.SharedParametersFilename;
        var office = new Dictionary<string, ExternalDefinition>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (app.OpenSharedParameterFile() is { } file)
                foreach (DefinitionGroup g in file.Groups)
                    foreach (Definition d in g.Definitions)
                        if (d is ExternalDefinition x && !office.ContainsKey(x.Name)) office[x.Name] = x;
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or IOException) { }
        string? temp = null;
        DefinitionGroup? group = null;
        try
        {
            // The office's definitions first: the throwaway file replaces the shared parameter file while it is used.
            foreach (var w in wanted.OrderBy(w => office.ContainsKey(w.Name) ? 0 : 1))
            {
                var cats = app.Create.NewCategorySet();
                foreach (var c in w.Cats.Where(c => c.AllowsBoundParameters)) cats.Insert(c);
                if (cats.IsEmpty) continue;
                try
                {
                    if (Bound(doc, w.Name) is { } existing)
                    {
                        foreach (Category c in cats) if (!existing.Binding.Categories.Contains(c)) existing.Binding.Categories.Insert(c);
                        if (doc.ParameterBindings.ReInsert(existing.Def, existing.Binding, GroupTypeId.Data)) made.Add(w.Name);
                        continue;
                    }
                    Definition? def = office.TryGetValue(w.Name, out var known) ? known : null;
                    if (def is null)
                    {
                        if (group is null)
                        {
                            temp = Path.Combine(Path.GetTempPath(), "SketchProMax", $"LoiParameters-{Guid.NewGuid():N}.txt");
                            Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
                            File.WriteAllText(temp, "", new UTF8Encoding(false));
                            app.SharedParametersFilename = temp;
                            group = app.OpenSharedParameterFile()?.Groups.Create("Sketch ProMax LOI");
                        }
                        def = group?.Definitions.Create(new ExternalDefinitionCreationOptions(w.Name, w.Spec));
                    }
                    ElementBinding binding = w.Instance ? app.Create.NewInstanceBinding(cats) : app.Create.NewTypeBinding(cats);
                    if (def is not null && doc.ParameterBindings.Insert(def, binding, GroupTypeId.Data)) made.Add(w.Name);
                }
                catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or Autodesk.Revit.Exceptions.ArgumentException or IOException) { }
            }
        }
        finally
        {
            if (temp is not null)
            {
                try { app.SharedParametersFilename = original ?? ""; } catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException) { }
                try { File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        return made;
    }

    private static (Definition Def, ElementBinding Binding)? Bound(Document doc, string name)
    {
        var it = doc.ParameterBindings.ForwardIterator();
        while (it.MoveNext())
            if (it.Key is { } def && def.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && it.Current is ElementBinding b) return (def, b);
        return null;
    }
}

public sealed class FillLoiDataTool : IRevitTool
{
    public string Name => "fill_loi_data";
    public string Description =>
        "Fill the element information the BEP's LOI matrix asks for (iLOD), so a BIM submission needs no typing by hand. For each model type in use whose UniFormat code " +
        "(or category) the matrix names, each parameter it must have that is empty gets a value: from values first, else from the model (material, diameter, thickness, " +
        "length, width, height, depth, volume, area, level, concrete or steel grade, cut-off/top and toe/bottom elevation). A parameter the model lacks is added as a project " +
        "parameter (the shared parameter file's definition when it has that name). Filled values are never changed. Without apply it previews (rows with key, value, source; " +
        "rows with an empty value need one in set, or stay empty); with apply true it fills every row that has a value, or only keys, in one Undo step, set overriding the " +
        "preview's values. matrix, values and classification default to the pre-submission checklist's Checking rules for this model's project. Fill the UniFormat codes " +
        "first (the checklist's Fill the UniFormat codes): the matrix goes by code.";
    public string PropertiesJson => """
    {
      "matrix": {"type": "string", "description": "Code or category>parameters, entries separated by ; e.g. A1020*>Material, Length, Diameter; B1010*>Material, Thickness (default: the checklist's LOI matrix rule)."},
      "values": {"type": "string", "description": "Code or category>Parameter=value for what the model can't give, entries separated by ; e.g. *>Status=For construction; {Parameter} copies another parameter (default: the checklist's LOI values rule)."},
      "classification": {"type": "string", "description": "The type parameter holding the UniFormat code (default: the checklist's Classification parameter rule)."},
      "apply": {"type": "boolean", "description": "true fills them; default false previews."},
      "keys": {"type": "array", "items": {"type": "string"}, "description": "Only these rows (key from the preview)."},
      "set": {"type": "object", "additionalProperties": {"type": "string"}, "description": "Values to write instead of the preview's, by key (\"each element's own\" fills each element from its geometry)."},
    """ + Paging.Properties + "}";
    public string[] Required => [];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        string file;
        try { file = string.IsNullOrEmpty(doc.PathName) ? doc.Title : doc.PathName; }
        catch (Exception) { file = doc.Title; }
        var (def, _) = ChecklistStore.Load(AgentSettings.Load(), ChecklistProjects.ProjectKey(file));
        var spec = LoiFill.SpecOf(def, args.OptString("matrix"), args.OptString("values"), args.OptString("classification"));
        if (spec.Matrix.Count == 0) return ToolResult.Error("No LOI matrix: pass matrix, or set LOI matrix in the pre-submission checklist's Checking rules.");
        var rows = LoiFill.Plan(doc, spec, LoiFill.TypeIds(doc, spec));
        var keys = new HashSet<string>(args.StringList("keys"), StringComparer.OrdinalIgnoreCase);
        if (keys.Count > 0) rows = rows.Where(r => keys.Contains(r.Key)).ToList();
        var set = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (args.Has("set") && args.Raw["set"].ValueKind == JsonValueKind.Object)
            foreach (var p in args.Raw["set"].EnumerateObject()) set[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.ToString();
        string Value(LoiFill.Row r) => set.TryGetValue(r.Key, out var v) ? v.Trim() : r.Value;
        if (!args.OptBool("apply"))
        {
            var (page, info) = Paging.Page(Paging.Named(rows, args, r => r.Label), args, 50);
            var empty = rows.Count(r => Value(r).Length == 0);
            return ToolResult.Ok(new
            {
                preview = true,
                summary = rows.Count == 0 ? "Nothing to fill: every type the LOI matrix covers has its parameters filled."
                    : $"{rows.Count} LOI values to fill on {rows.Select(r => r.TypeId).Distinct().Count()} types" +
                      (empty > 0 ? $"; {empty} have no value from the model or the rules (give them in set, or they stay empty)" : "") +
                      (rows.Any(r => r.NewParameter) ? "; adds the project parameters " + string.Join(", ", rows.Where(r => r.NewParameter).Select(r => r.Param).Distinct(StringComparer.OrdinalIgnoreCase)) : "") + ".",
                rows = page.Select(r => new { key = r.Key, code = r.Code, type = r.Type, parameter = r.Param, value = Value(r), per_element = r.PerElement, elements = r.Lacking, new_parameter = r.NewParameter, source = r.Source }),
                page = info,
            });
        }
        var answer = string.Join("\n", rows.Where(r => Value(r).Length > 0).Select(r => r.Key + "\t" + Value(r)));
        return ToolResult.Ok(new { applied = true, summary = answer.Length == 0 ? "Nothing to fill: no row has a value." : LoiFill.Apply(doc, spec, answer) });
    }
}
