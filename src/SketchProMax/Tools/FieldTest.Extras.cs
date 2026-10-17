using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using RevitApp = Autodesk.Revit.ApplicationServices.Application;

namespace SketchProMax.Tools;

/// <summary>
/// The parts of the field test's project that close skips: families made in memory from Revit's family templates
/// when the project template and content library have none (a concrete beam, a cladding column, detail items, a
/// cutting void, mechanical equipment), and a curtain wall, a model group and a placeholder sheet. Each is its own
/// building step; one that can't be made is noted in the report and the tools needing it skip.
/// </summary>
internal static partial class FieldTest
{
    /// <summary>The subfolder of the test folder the made families are saved in (load_family's case loads one back).</summary>
    internal const string FamiliesFolder = "families";

    /// <summary>Subfolders of the test folder: steel section families with a type catalog, and the DWG exported for title_block_from_cad.</summary>
    internal const string SteelFolder = "steel", CadFolder = "cad";

    /// <summary>The steel catalog folder and a type in it that isn't loaded yet (its second type when it has two), from the cases' output folder.</summary>
    internal static (string Folder, string Type)? SteelSections(string outputFolder)
    {
        var folder = Path.Combine(Path.GetDirectoryName(outputFolder) ?? outputFolder, SteelFolder);
        if (!Directory.Exists(folder)) return null;
        foreach (var catalog in Directory.GetFiles(folder, "*.txt", SearchOption.AllDirectories))
        {
            var types = File.ReadLines(catalog).Skip(1).Select(l => l.Split(',')[0].Trim().Trim('"')).Where(n => n.Length > 0).ToList();
            if (types.Count > 0) return (folder, types[Math.Min(1, types.Count - 1)]);
        }
        return null;
    }

    /// <summary>The DWG the field test exported from a drafting view, from the cases' output folder.</summary>
    internal static string? TitleBlockDwg(string outputFolder)
    {
        var folder = Path.Combine(Path.GetDirectoryName(outputFolder) ?? outputFolder, CadFolder);
        return Directory.Exists(folder) ? Directory.GetFiles(folder, "*.dwg").FirstOrDefault() : null;
    }

    /// <summary>The families the field test saved, from the cases' output folder (a sibling of the families folder).</summary>
    internal static IEnumerable<string> FamilyFiles(string outputFolder)
    {
        var folder = Path.Combine(Path.GetDirectoryName(outputFolder) ?? outputFolder, FamiliesFolder);
        return Directory.Exists(folder) ? Directory.GetFiles(folder, "*.rfa").OrderBy(f => f, StringComparer.OrdinalIgnoreCase) : [];
    }

    /// <summary>
    /// Revit's family templates (.rft), listed once per run: the folder set in Revit's options first, then every
    /// language under %ProgramData%\Autodesk\RVT {version}\Family Templates.
    /// </summary>
    private sealed class FamilyTemplates(RevitApp app)
    {
        private List<string>? _files;
        private readonly List<string> _searched = [];

        public string Searched => _searched.Count == 0 ? "no family template folder exists on this PC" : string.Join("; ", _searched.Take(4)) + (_searched.Count > 4 ? "; ..." : "");

        private List<string> Files()
        {
            if (_files is not null) return _files;
            _files = [];
            var roots = new List<string>();
            try { if (!string.IsNullOrEmpty(app.FamilyTemplatePath)) roots.Add(app.FamilyTemplatePath); }
            catch (Exception) { }
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Autodesk", $"RVT {app.VersionNumber}", "Family Templates"));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots.Select(r => r.TrimEnd('\\', '/')).Where(Directory.Exists))
            {
                _searched.Add(root);
                try { _files.AddRange(Directory.EnumerateFiles(root, "*.rft", SearchOption.AllDirectories).Where(seen.Add)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            }
            return _files;
        }

        /// <summary>The first template named one of <paramref name="names"/> (also "Metric name" and "M_name"), in that order.</summary>
        public string? Find(IEnumerable<string> names)
        {
            foreach (var name in names)
            {
                var hit = Files().FirstOrDefault(f => Path.GetFileNameWithoutExtension(f) is { } stem &&
                    (stem.Equals(name, StringComparison.OrdinalIgnoreCase) || stem.Equals("Metric " + name, StringComparison.OrdinalIgnoreCase) || stem.Equals("M_" + name, StringComparison.OrdinalIgnoreCase)));
                if (hit is not null) return hit;
            }
            return null;
        }
    }

    /// <summary>
    /// A family made from a template, saved in the test folder and loaded into <paramref name="target"/>, which must
    /// have no transaction open. Returns its first type, or null with a note saying why tools needing it will skip.
    /// </summary>
    private static FamilySymbol? MakeFamily(RevitApp a, Document target, State s, RunReport result, string what, string name, string[] templateNames,
        BuiltInCategory? category, Func<Document, string?> body)
    {
        var templates = s.Templates ??= new FamilyTemplates(a);
        var template = templates.Find(templateNames);
        if (template is null)
        {
            result.Sample.Add($"{what}: no family template {string.Join(" or ", templateNames.Select(n => $"'{n}.rft'"))} on this PC (searched {templates.Searched}), so tools needing one will skip");
            return null;
        }
        Document? fam = null;
        try
        {
            fam = a.NewFamilyDocument(template);
            string? note = null;
            var built = Step(fam, result, $"{what}: family from {Path.GetFileName(template)}", () =>
            {
                if (category is { } bic && fam.Settings.Categories.get_Item(bic) is { } cat && fam.OwnerFamily.FamilyCategory?.Id != cat.Id)
                    fam.OwnerFamily.FamilyCategory = cat;
                note = body(fam);
                if (fam.FamilyManager.CurrentType is null) fam.FamilyManager.NewType("FT");
                return fam;
            });
            if (built is null) return null;
            var folder = Path.Combine(s.Folder, FamiliesFolder);
            Directory.CreateDirectory(folder);
            fam.SaveAs(Path.Combine(folder, name + ".rfa"), new SaveAsOptions { OverwriteExistingFile = true });
            var family = fam.LoadFamily(target, new FamilyEdit.Overwrite());
            var symbol = family?.GetFamilySymbolIds().Select(target.GetElement).OfType<FamilySymbol>().FirstOrDefault()
                         ?? throw new InvalidOperationException("Revit loaded it with no type");
            result.Sample.Add($"{what}: made {family!.Name} from {Path.GetFileName(template)}" + (note is null ? "" : $" ({note})"));
            return symbol;
        }
        catch (Exception ex)
        {
            result.Sample.Add($"{what}: could not be made from {Path.GetFileName(template)} ({ex.Message}), so tools needing one will skip");
            return null;
        }
        finally
        {
            try { fam?.Close(false); } catch (Exception) { }
        }
    }

    // ---- Family bodies (inside the family document's transaction) -------------------------------------------------------

    private static CurveArrArray Loop(params XYZ[] points)
    {
        var profile = new CurveArray();
        for (var i = 0; i < points.Length; i++) profile.Append(Line.CreateBound(points[i], points[(i + 1) % points.Length]));
        var loops = new CurveArrArray();
        loops.Append(profile);
        return loops;
    }

    /// <summary>A box centred on the origin in plan, from z0 up by height (mm).</summary>
    private static GenericForm Box(Document fam, double widthMm, double depthMm, double z0Mm, double heightMm, bool solid)
    {
        double x = widthMm / 2 * Mm, y = depthMm / 2 * Mm, z = z0Mm * Mm;
        var plane = SketchPlane.Create(fam, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, z)));
        return fam.FamilyCreate.NewExtrusion(solid, Loop(new XYZ(-x, -y, z), new XYZ(x, -y, z), new XYZ(x, y, z), new XYZ(-x, y, z)), plane, heightMm * Mm);
    }

    private static List<GenericForm> Solids(Document fam) =>
        new FilteredElementCollector(fam).OfClass(typeof(GenericForm)).Cast<GenericForm>().Where(f => f.IsSolid).ToList();

    private static View FamilyView(Document fam) =>
        new FilteredElementCollector(fam).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate && v.ViewType != ViewType.ThreeD)
            .OrderBy(v => v is ViewPlan ? 0 : 1).FirstOrDefault()
        ?? throw new InvalidOperationException("the template has no plan view to draw in");

    /// <summary>A 300 x 600 concrete beam whose ends follow the member's length when the template's end planes are found.</summary>
    private static string? ConcreteBeamBody(Document fam)
    {
        var notes = new List<string>();
        // Material for Model Behavior: what makes the beam concrete to rebar and to the tools that read Family.StructuralMaterialType.
        try
        {
            if (fam.OwnerFamily.get_Parameter(BuiltInParameter.FAMILY_STRUCT_MATERIAL_TYPE) is { IsReadOnly: false } behaviour) behaviour.Set((int)StructuralMaterialType.Concrete);
            else notes.Add("the template has no Material for Model Behavior setting");
        }
        catch (Exception ex) { notes.Add($"Material for Model Behavior not set ({ex.Message})"); }
        var concrete = new FilteredElementCollector(fam).OfClass(typeof(Material)).Cast<Material>().FirstOrDefault(m => m.Name == "FT Concrete")
                       ?? (Material)fam.GetElement(Material.Create(fam, "FT Concrete"));
        concrete.MaterialClass = "Concrete";
        var fm = fam.FamilyManager;
        if (fm.CurrentType is null) fm.NewType("300 x 600");
        try { if (fm.get_Parameter(BuiltInParameter.STRUCTURAL_MATERIAL_PARAM) is { } sm) fm.Set(sm, concrete.Id); }
        catch (Exception ex) { notes.Add($"structural material not set ({ex.Message})"); }

        var forms = Solids(fam);
        if (forms.Count > 0) notes.Add("kept the template's own geometry");
        else forms.Add(BeamExtrusion(fam, notes));
        foreach (var f in forms)
            try { f.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM)?.Set(concrete.Id); }
            catch (Exception) { /* bound to the family's material parameter, set above */ }
        return notes.Count == 0 ? null : string.Join("; ", notes);
    }

    private static GenericForm BeamExtrusion(Document fam, List<string> notes)
    {
        var planes = new FilteredElementCollector(fam).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>().ToList();
        ReferencePlane? Named(string name) => planes.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        static double X(ReferencePlane p) => (p.BubbleEnd.X + p.FreeEnd.X) / 2;
        var left = Named("Member Left");
        var right = Named("Member Right");
        double x0 = 0, x1 = 6000 * Mm;
        if (left is not null && right is not null && X(right) - X(left) > 1) { x0 = X(left); x1 = X(right); }
        else left = right = null;
        double w = 150 * Mm, h = 600 * Mm;
        var plane = SketchPlane.Create(fam, Plane.CreateByNormalAndOrigin(XYZ.BasisX, new XYZ(x0, 0, 0)));
        var beam = fam.FamilyCreate.NewExtrusion(true, Loop(new XYZ(x0, -w, -h), new XYZ(x0, w, -h), new XYZ(x0, w, 0), new XYZ(x0, -w, 0)), plane, x1 - x0);
        if (left is null || right is null)
        {
            notes.Add("6 m long: the template has no Member Left/Right planes to stretch it with");
            return beam;
        }
        try
        {
            fam.Regenerate();
            var view = FamilyView(fam);
            var faces = beam.get_Geometry(new Autodesk.Revit.DB.Options { ComputeReferences = true })?.OfType<Solid>()
                .SelectMany(sol => sol.Faces.Cast<Face>()).OfType<PlanarFace>().ToList() ?? [];
            var start = faces.First(f => f.FaceNormal.X < -0.99);
            var end = faces.First(f => f.FaceNormal.X > 0.99);
            fam.FamilyCreate.NewAlignment(view, left.GetReference(), start.Reference).IsLocked = true;
            fam.FamilyCreate.NewAlignment(view, right.GetReference(), end.Reference).IsLocked = true;
        }
        catch (Exception ex) { notes.Add($"ends not locked to the member ({ex.Message}), so it stays {Math.Round((x1 - x0) / Mm)} mm long"); }
        return beam;
    }

    private static string? ColumnBody(Document fam)
    {
        if (Solids(fam).Count > 0) return "kept the template's own geometry";
        Box(fam, 400, 400, 0, 3000, true);
        return "400 x 400, 3 m tall";
    }

    private static string? EquipmentBody(Document fam)
    {
        Box(fam, 1000, 800, 0, 1200, true);
        return null;
    }

    private static string? LineDetailBody(Document fam)
    {
        fam.FamilyCreate.NewDetailCurve(FamilyView(fam), Line.CreateBound(XYZ.Zero, new XYZ(1000 * Mm, 0, 0)));
        return null;
    }

    private static string? BreakLineBody(Document fam)
    {
        var view = FamilyView(fam);
        var points = new[] { (-300.0, 0.0), (-50.0, 0.0), (-25.0, 60.0), (25.0, -60.0), (50.0, 0.0), (300.0, 0.0) };
        for (var i = 0; i + 1 < points.Length; i++)
            fam.FamilyCreate.NewDetailCurve(view, Line.CreateBound(new XYZ(points[i].Item1 * Mm, points[i].Item2 * Mm, 0), new XYZ(points[i + 1].Item1 * Mm, points[i + 1].Item2 * Mm, 0)));
        return null;
    }

    /// <summary>An unattached 300 x 300 void reaching 200 mm into the host face, with Cut with Voids When Loaded on.</summary>
    private static string? VoidBody(Document fam)
    {
        var cut = fam.OwnerFamily.get_Parameter(BuiltInParameter.FAMILY_ALLOW_CUT_WITH_VOIDS);
        if (cut is null || cut.IsReadOnly) throw new InvalidOperationException("the template has no 'Cut with Voids When Loaded' setting");
        cut.Set(1);
        Box(fam, 300, 300, -200, 200, false);
        return null;
    }

    // ---- Building steps ---------------------------------------------------------------------------------------------------

    /// <summary>A concrete beam family made in memory, else a copy of any framing type given a concrete structural material.</summary>
    private static FamilySymbol? ConcreteBeam(RevitApp a, Document doc, State s, RunReport result) =>
        MakeFamily(a, doc, s, result, "concrete beam", "FT Concrete Beam", ["Structural Framing - Beams and Braces"], null, ConcreteBeamBody)
        ?? Step(doc, result, "concrete beam (copy of a framing type)", () =>
        {
            var any = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(BuiltInCategory.OST_StructuralFraming).Cast<FamilySymbol>().FirstOrDefault()
                      ?? throw new InvalidOperationException("the template has no structural framing type to copy, so the concrete beam tools will skip");
            var concrete = ConcreteTypes.Concrete(doc) ?? throw new InvalidOperationException("the template has no concrete material, so the concrete beam tools will skip");
            var copy = (FamilySymbol)any.Duplicate("FT Concrete Beam");
            var material = copy.get_Parameter(BuiltInParameter.STRUCTURAL_MATERIAL_PARAM);
            if (material is null || material.IsReadOnly)
                throw new InvalidOperationException($"{any.FamilyName} has no structural material type parameter, so the concrete beam tools will skip");
            material.Set(concrete.Id);
            result.Sample.Add($"concrete beam: copied {any.FamilyName}: {any.Name} with material {concrete.Name}; its family stays {any.Family.StructuralMaterialType}, so rebar tools may refuse it");
            return copy;
        });

    /// <summary>A 50 mm floor type named as a finish with no structural material: arch_to_structure must leave it out.</summary>
    private static FloorType FinishFloorType(Document doc)
    {
        const string name = "FT Tile Finish 50";
        var types = new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().ToList();
        if (types.FirstOrDefault(t => t.Name == name) is { } existing) return existing;
        var source = types.FirstOrDefault(t => !t.IsFoundationSlab && t.GetCompoundStructure() is not null) ?? throw new InvalidOperationException("no floor type to copy");
        var type = (FloorType)source.Duplicate(name);
        var cs = type.GetCompoundStructure();
        cs.SetLayers(new List<CompoundStructureLayer> { new(50 * Mm, MaterialFunctionAssignment.Structure, ElementId.InvalidElementId) });
        cs.StructuralMaterialIndex = 0;
        type.SetCompoundStructure(cs);
        return type;
    }

    private static FamilyInstance PlaceColumn(Document doc, FamilySymbol symbol, Shell shell, double x, double y, StructuralType kind)
    {
        if (!symbol.IsActive) symbol.Activate();
        var column = doc.Create.NewFamilyInstance(P(x, y, shell.Base.Elevation), symbol, shell.Base, kind);
        column.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM)?.Set(shell.Upper.Id);
        column.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM)?.Set(0.0);
        return column;
    }

    /// <summary>The link's columns: a concrete Structural Column arch_to_structure must convert, and an architectural (cladding) one it must skip.</summary>
    private static void BuildLinkColumns(RevitApp a, Document doc, State s, Shell shell, RunReport result)
    {
        var structural = Symbol(doc, result, s.Lib ??= new Library(a), BuiltInCategory.OST_StructuralColumns, StructuralMaterialType.Concrete, "link: concrete column");
        if (structural is not null)
            Step(doc, result, "link: structural column", () => PlaceColumn(doc, structural, shell, 9000, 9000, StructuralType.Column));
        var cladding = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(BuiltInCategory.OST_Columns).Cast<FamilySymbol>().FirstOrDefault()
                       ?? MakeFamily(a, doc, s, result, "link: cladding column", "FT Cladding Column", ["Column", "Generic Model"], BuiltInCategory.OST_Columns, ColumnBody);
        if (cladding is not null)
            Step(doc, result, "link: cladding column", () => PlaceColumn(doc, cladding, shell, 3000, 9000, StructuralType.NonStructural));
    }

    /// <summary>What the cases need that the template doesn't have, each its own step.</summary>
    private static void BuildExtras(RevitApp a, Document doc, State s, Shell shell, RunReport result)
    {
        // On the upper level, so the ground floor plan the cases dimension and tag doesn't show it.
        Step(doc, result, "curtain wall", () =>
        {
            var type = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().FirstOrDefault(w => w.Kind == WallKind.Curtain)
                       ?? throw new InvalidOperationException("the template has no curtain wall type, so set_curtain_grid will skip");
            var z = shell.Upper.Elevation;
            return Wall.Create(doc, Line.CreateBound(P(-1000, 16000, z), P(5000, 16000, z)), type.Id, shell.Upper.Id, 3000 * Mm, 0, false, false);
        });

        Step(doc, result, "model group", () =>
        {
            var z = shell.Base.Elevation;
            var plane = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, P(0, 0, z)));
            var line = doc.Create.NewModelCurve(Line.CreateBound(P(-1000, 18000, z), P(1000, 18000, z)), plane);
            return doc.Create.NewGroup(new List<ElementId> { line.Id });
        });

        Step(doc, result, "placeholder sheet", () => ViewSheet.CreatePlaceholder(doc));

        FamilySymbol? Loaded(BuiltInCategory category, Func<FamilySymbol, bool> where) =>
            new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(category).Cast<FamilySymbol>().FirstOrDefault(where);

        var equipment = Loaded(BuiltInCategory.OST_MechanicalEquipment, x => x.Family.FamilyPlacementType == FamilyPlacementType.OneLevelBased)
                        ?? MakeFamily(a, doc, s, result, "mechanical equipment", "FT Air Handling Unit", ["Mechanical Equipment", "Generic Model"], BuiltInCategory.OST_MechanicalEquipment, EquipmentBody);
        if (equipment is not null)
            Step(doc, result, "mechanical equipment", () =>
            {
                if (!equipment.IsActive) equipment.Activate();
                return doc.Create.NewFamilyInstance(P(3000, 9000, shell.Base.Elevation), equipment, shell.Base, StructuralType.NonStructural);
            });

        if (Loaded(BuiltInCategory.OST_DetailComponents, x => x.Family.FamilyPlacementType == FamilyPlacementType.CurveBasedDetail) is null)
            MakeFamily(a, doc, s, result, "line-based detail item", "FT Additional Bar RFT", ["Detail Item line based"], null, LineDetailBody);
        if (Loaded(BuiltInCategory.OST_DetailComponents, x => x.FamilyName.IndexOf("Break Line", StringComparison.OrdinalIgnoreCase) >= 0) is null)
            MakeFamily(a, doc, s, result, "break line detail item", "FT Break Line", ["Detail Item"], null, BreakLineBody);
        if (Loaded(BuiltInCategory.OST_GenericModel, x => x.Family.FamilyPlacementType == FamilyPlacementType.WorkPlaneBased
                                                        && x.Family.get_Parameter(BuiltInParameter.FAMILY_ALLOW_CUT_WITH_VOIDS)?.AsInteger() == 1) is null)
            MakeFamily(a, doc, s, result, "face-based cutting void", "FT Void Cut", ["Generic Model face based"], null, VoidBody);

        // A slab whose material names its mix, next to the structural slab whose material doesn't: embodied_carbon's case
        // checks one gets a GGBS factor and the other the CEM I one.
        Step(doc, result, "slab naming its mix", () =>
        {
            const string name = "FT Slab C32/40 50% GGBS";
            var material = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().FirstOrDefault(m => m.Name == "FT Concrete C32/40 50% GGBS")
                           ?? (Material)doc.GetElement(Material.Create(doc, "FT Concrete C32/40 50% GGBS"));
            material.MaterialClass = "Concrete";
            var source = new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().First(t => t.Id == FloorTypeId(doc));
            var type = (FloorType)source.Duplicate(name);
            var cs = type.GetCompoundStructure();
            cs.SetLayers(new List<CompoundStructureLayer> { new(200 * Mm, MaterialFunctionAssignment.Structure, material.Id) });
            cs.StructuralMaterialIndex = 0;
            type.SetCompoundStructure(cs);
            var floor = Floor.Create(doc, [Rectangle(-1000, 20000, 3000, 24000)], type.Id, shell.Base.Id);
            floor.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL)?.Set(1);
            return floor;
        });

        SteelCatalog(a, doc, s, result);
        ExportDwg(doc, s, result);
    }

    /// <summary>
    /// A folder of steel framing families with type catalogs for load_steel_sections: a copy of the content library's
    /// first one when it is installed, else a family made here with a two-type catalog written beside it.
    /// </summary>
    private static void SteelCatalog(RevitApp a, Document doc, State s, RunReport result)
    {
        var folder = Path.Combine(s.Folder, SteelFolder, "Structural Framing");
        try
        {
            Directory.CreateDirectory(folder);
            if ((s.Lib ??= new Library(a)).Find(BuiltInCategory.OST_StructuralFraming, StructuralMaterialType.Steel, null) is { } library
                && File.Exists(Path.ChangeExtension(library, ".txt")))
            {
                File.Copy(library, Path.Combine(folder, Path.GetFileName(library)), true);
                File.Copy(Path.ChangeExtension(library, ".txt"), Path.Combine(folder, Path.GetFileNameWithoutExtension(library) + ".txt"), true);
                result.Sample.Add($"steel sections: copied {Path.GetFileName(library)} and its type catalog from the content library");
                return;
            }
            if (MakeFamily(a, doc, s, result, "steel section family", "FT Steel Beam", ["Structural Framing - Beams and Braces"], null, ColumnBody) is null) return;
            File.Copy(Path.Combine(s.Folder, FamiliesFolder, "FT Steel Beam.rfa"), Path.Combine(folder, "FT Steel Beam.rfa"), true);
            File.WriteAllText(Path.Combine(folder, "FT Steel Beam.txt"), ",\r\nFT 200x100,\r\nFT 300x150,\r\n");
            result.Sample.Add("steel sections: no steel catalog in the content library, so the field test made FT Steel Beam with a two-type catalog");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.Sample.Add($"steel sections: the catalog folder could not be made ({ex.Message}), so load_steel_sections has nothing to load");
        }
    }

    /// <summary>A tiny DWG title block for title_block_from_cad: an A1 border and a title box drawn in a drafting view, exported.</summary>
    private static void ExportDwg(Document doc, State s, RunReport result)
    {
        var view = Step(doc, result, "drafting view for the DWG", () =>
        {
            var type = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().FirstOrDefault(v => v.ViewFamily == ViewFamily.Drafting)
                       ?? throw new InvalidOperationException("the template has no drafting view type");
            var drafting = ViewDrafting.Create(doc, type.Id);
            try { drafting.Name = "FT Title Block DWG"; } catch (Autodesk.Revit.Exceptions.ArgumentException) { }
            drafting.Scale = 1;
            void Box(double x0, double y0, double x1, double y1)
            {
                var c = new[] { P(x0, y0), P(x1, y0), P(x1, y1), P(x0, y1) };
                for (var i = 0; i < 4; i++) doc.Create.NewDetailCurve(drafting, Line.CreateBound(c[i], c[(i + 1) % 4]));
            }
            Box(0, 0, 841, 594);
            Box(661, 10, 831, 80);
            return drafting;
        });
        if (view is null) return;
        try
        {
            var folder = Path.Combine(s.Folder, CadFolder);
            Directory.CreateDirectory(folder);
            if (!doc.Export(folder, "FT Title Block", new List<ElementId> { view.Id }, new DWGExportOptions()))
                result.Sample.Add("DWG for title_block_from_cad: Revit did not export it");
            else result.Sample.Add("DWG for title_block_from_cad: exported an A1 border from a drafting view");
        }
        catch (Exception ex)
        {
            result.Sample.Add($"DWG for title_block_from_cad: not exported ({ex.Message})");
        }
    }
}
