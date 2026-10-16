using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

/// <summary>Concrete types by size, made when missing, for the structure built from the architecture.</summary>
internal static class ConcreteTypes
{
    public static double Mm(double ft) => Math.Round(ft * 304.8);

    public static Material? Concrete(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>()
            .OrderByDescending(m => m.MaterialClass?.IndexOf("Concrete", StringComparison.OrdinalIgnoreCase) >= 0)
            .ThenByDescending(m => m.Name.IndexOf("Cast-in-Place", StringComparison.OrdinalIgnoreCase) >= 0)
            .FirstOrDefault(m => m.Name.IndexOf("Concrete", StringComparison.OrdinalIgnoreCase) >= 0 || m.MaterialClass?.IndexOf("Concrete", StringComparison.OrdinalIgnoreCase) >= 0);

    private static CompoundStructure Single(CompoundStructure cs, double thickness, Material? material)
    {
        cs.SetLayers(new List<CompoundStructureLayer> { new(thickness, MaterialFunctionAssignment.Structure, material?.Id ?? cs.GetLayers().FirstOrDefault()?.MaterialId ?? ElementId.InvalidElementId) });
        cs.StructuralMaterialIndex = 0;
        return cs;
    }

    public static WallType Wall(Document doc, double thickness, string label = "RC Wall")
    {
        var name = $"{label} {Mm(thickness)}mm";
        var masonry = label.StartsWith("Masonry", StringComparison.OrdinalIgnoreCase);
        var types = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().Where(t => t.Kind == WallKind.Basic).ToList();
        if (types.FirstOrDefault(t => t.Name == name) is { } existing) return existing;
        var basis = types.FirstOrDefault(t => t.Name.IndexOf("Concrete", StringComparison.OrdinalIgnoreCase) >= 0) ?? types.FirstOrDefault()
                    ?? throw new InvalidOperationException("The model has no basic wall types to start from.");
        var type = (WallType)basis.Duplicate(name);
        var material = masonry
            ? new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().FirstOrDefault(m => new[] { "Masonry", "Block", "Brick", "Stone" }.Any(w => m.Name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0)) ?? Concrete(doc)
            : Concrete(doc);
        if (type.GetCompoundStructure() is { } cs) type.SetCompoundStructure(Single(cs, thickness, material));
        return type;
    }

    public static FloorType Slab(Document doc, double thickness, string label = "RC Slab", bool foundation = false)
    {
        var name = $"{label} {Mm(thickness)}mm";
        var all = new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().ToList();
        var types = all.Where(t => t.IsFoundationSlab == foundation).ToList();
        if (types.Count == 0) types = all;
        if (types.FirstOrDefault(t => t.Name == name) is { } existing) return existing;
        var basis = types.FirstOrDefault(t => t.Name.IndexOf("Concrete", StringComparison.OrdinalIgnoreCase) >= 0) ?? types.FirstOrDefault()
                    ?? throw new InvalidOperationException("The model has no floor types to start from.");
        var type = (FloorType)basis.Duplicate(name);
        if (type.GetCompoundStructure() is { } cs) type.SetCompoundStructure(Single(cs, thickness, Concrete(doc)));
        return type;
    }

    /// <summary>A concrete rectangular column type of this size, duplicated from a loaded concrete column family.</summary>
    public static FamilySymbol Column(Document doc, double b, double h)
    {
        var symbols = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(BuiltInCategory.OST_StructuralColumns).Cast<FamilySymbol>()
            .Where(s => s.Family.StructuralMaterialType == StructuralMaterialType.Concrete && SizeParams(s) is not null).ToList();
        var basis = symbols.FirstOrDefault() ?? throw new InvalidOperationException(
            "No rectangular concrete column family is loaded (e.g. 'M_Concrete-Rectangular-Column'). Load one with load_family, then run again.");
        var name = $"{Mm(b)} x {Mm(h)}mm";
        if (symbols.FirstOrDefault(s => s.Family.Id == basis.Family.Id && s.Name == name) is { } existing) return existing;
        var type = (FamilySymbol)basis.Duplicate(name);
        var (pb, ph) = SizeParams(type)!.Value;
        pb.Set(b); ph.Set(h);
        return type;
    }

    private static (Parameter b, Parameter h)? SizeParams(FamilySymbol s)
    {
        Parameter? P(params string[] names) => names.Select(n => s.LookupParameter(n)).FirstOrDefault(p => p is { StorageType: StorageType.Double, IsReadOnly: false });
        var b = P("b", "Width", "B");
        var h = P("h", "Depth", "H");
        return b != null && h != null ? (b, h) : null;
    }

    /// <summary>This model's level at an elevation (within 300 mm), else the nearest one below.</summary>
    public static Level LevelAt(Document doc, double z) =>
        new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => Math.Abs(l.ProjectElevation - z)).FirstOrDefault(l => Math.Abs(l.ProjectElevation - z) < 300 / 304.8)
        ?? new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().Where(l => l.ProjectElevation <= z + 1e-6).OrderByDescending(l => l.ProjectElevation).FirstOrDefault()
        ?? new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.ProjectElevation).First();

    /// <summary>Elements in this model already made from a source element (their Comments carry 'from &lt;uid&gt;').</summary>
    public static HashSet<string> MadeFrom(Document doc) =>
        new FilteredElementCollector(doc).WhereElementIsNotElementType()
            .WherePasses(new LogicalOrFilter(new List<ElementFilter> { new ElementCategoryFilter(BuiltInCategory.OST_Walls), new ElementCategoryFilter(BuiltInCategory.OST_Floors), new ElementCategoryFilter(BuiltInCategory.OST_StructuralColumns) }))
            .Select(e => e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "")
            .Where(c => c.StartsWith("from ", StringComparison.Ordinal)).Select(c => c.Substring(5).Trim()).ToHashSet();
}

public sealed class ArchToStructureTool : IRevitTool
{
    /// <summary>
    /// The architect's wall line moved onto its core centre (or the wall centre when it has no core), since the
    /// location line is often a finish face and the new wall is centred on the line it is given.
    /// </summary>
    private static Curve CentreLine(Wall w, Curve curve)
    {
        var cs = w.WallType.GetCompoundStructure();
        if (cs is null || w.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM)?.AsInteger() is not { } key) return curve;
        var target = cs.GetFirstCoreLayerIndex() >= 0 ? WallLocationLine.CoreCenterline : WallLocationLine.WallCenterline;
        // Offsets are measured from the wall centre; calibrate their sign against the exterior face (Orientation points outward).
        var sign = cs.GetOffsetForLocationLine(WallLocationLine.FinishFaceExterior) >= 0 ? 1 : -1;
        var shift = sign * (cs.GetOffsetForLocationLine(target) - cs.GetOffsetForLocationLine((WallLocationLine)key));
        return Math.Abs(shift) < 1e-6 ? curve : curve.CreateTransformed(Transform.CreateTranslation(w.Orientation * shift));
    }

    public string Name => "arch_to_structure";
    public string Description =>
        "Turn the architectural model (a link, or this model) into a structural model: load-bearing walls become " +
        "structural concrete walls, architectural columns become concrete structural columns of the same size, and floors " +
        "become structural slabs, on the matching levels of this model. Walls count as load-bearing when the architect set " +
        "them structural, their type name has Concrete/RC/Core/Shear (or your words), or they are at least min_wall_thickness. " +
        "Types are made by size (RC Wall 200mm, RC Slab 250mm, 400 x 600mm). Re-running skips what was already made. dry_run first.";
    public string PropertiesJson => """
    {
      "source": {"type": "string", "description": "Architectural link name, or 'model'."},
      "what": {"type": "array", "items": {"type": "string", "enum": ["walls", "columns", "floors", "openings"]}, "description": "Default all four. openings: door and window openings cut in the new structural walls (slab openings come with the floors)."},
      "wall_type_words": {"type": "array", "items": {"type": "string"}, "description": "Type name words meaning load-bearing (default Concrete, RC, Core, Shear, Retaining)."},
      "min_wall_thickness": {"type": "number", "description": "Also take walls at least this thick, project units (default: off)."},
      "wall_thickness": {"type": "number", "description": "Make structural walls this thick instead of the architect's thickness."},
      "slab_thickness": {"type": "number", "description": "Structural slab thickness, project units (default 200 mm)."},
      "slab_top_offset": {"type": "number", "description": "Slab top relative to the architect's floor top (e.g. -50 for screed), project units. Default 0."},
      "level": {"type": "string", "description": "Only elements on this level (the architect's level name)."},
      "dry_run": {"type": "boolean"}
    }
    """;
    public string[] Required => ["source"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        Document src = doc; var tr = Transform.Identity; var sourceName = "this model";
        var source = args.String("source");
        if (!source.Equals("model", StringComparison.OrdinalIgnoreCase))
        {
            var link = LinkMatch.Find(doc, new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().ToList(), source);
            src = link.GetLinkDocument() ?? throw new ArgumentException($"Link '{link.Name}' is not loaded.");
            tr = link.GetTotalTransform(); sourceName = link.Name;
        }
        var what = args.StringList("what");
        if (what.Count == 0) what = ["walls", "columns", "floors", "openings"];
        var openingsMade = 0;
        var hosted = what.Contains("openings")
            ? new FilteredElementCollector(src).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                .Where(f => f.Host is Wall && f.Category?.Id.Value is (long)BuiltInCategory.OST_Doors or (long)BuiltInCategory.OST_Windows)
                .GroupBy(f => f.Host.Id.Value).ToDictionary(g => g.Key, g => g.ToList())
            : new Dictionary<long, List<FamilyInstance>>();
        var words = args.StringList("wall_type_words");
        if (words.Count == 0) words = ["Concrete", "RC", "Core", "Shear", "Retaining"];
        var minWall = args.OptDouble("min_wall_thickness") is { } mw ? RevitHelpers.ToInternal(doc, mw) : double.MaxValue;
        var wallThickness = args.OptDouble("wall_thickness") is { } wt ? RevitHelpers.ToInternal(doc, wt) : (double?)null;
        var slab = args.OptDouble("slab_thickness") is { } st ? RevitHelpers.ToInternal(doc, st) : 200 / 304.8;
        var slabOffset = args.OptDouble("slab_top_offset") is { } so ? RevitHelpers.ToInternal(doc, so) : 0;
        var levelFilter = args.OptString("level");
        bool OnLevel(Element e) => levelFilter is null || (src.GetElement(e.LevelId) as Level)?.Name.Equals(levelFilter, StringComparison.OrdinalIgnoreCase) == true;
        var done = ConcreteTypes.MadeFrom(doc);
        var dry = args.OptBool("dry_run");
        var made = new Dictionary<string, int> { ["walls"] = 0, ["columns"] = 0, ["floors"] = 0 };
        var skipped = new Dictionary<string, int> { ["walls"] = 0, ["columns"] = 0, ["floors"] = 0, ["already made"] = 0 };
        var errors = new List<string>();
        var dz = tr.Origin.Z;

        using var t = dry ? null : RevitHelpers.StartTransaction(doc, "Structure from architecture");
        // One element Revit can't join or build is deleted at commit rather than undoing the whole run.
        var swallow = dry ? null : new TypeKit.SwallowWarnings(doc);
        if (t != null) { var fo = t.GetFailureHandlingOptions(); fo.SetFailuresPreprocessor(swallow); t.SetFailureHandlingOptions(fo); }
        var sloped = 0;
        if (what.Contains("walls"))
            foreach (var w in new FilteredElementCollector(src).OfClass(typeof(Wall)).Cast<Wall>().Where(OnLevel))
            {
                if (w.WallType.Kind != WallKind.Basic || (w.Location as LocationCurve)?.Curve is not { } curve) continue;
                var bearing = w.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT)?.AsInteger() == 1
                              || words.Any(x => System.Text.RegularExpressions.Regex.IsMatch(w.WallType.Name, $@"(^|[^A-Za-z]){System.Text.RegularExpressions.Regex.Escape(x)}([^A-Za-z]|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                              || w.Width >= minWall;
                if (!bearing) { skipped["walls"]++; continue; }
                if (src == doc && w.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT)?.AsInteger() == 1) { skipped["walls"]++; continue; }
                if (done.Contains(w.UniqueId)) { skipped["already made"]++; continue; }
                if (dry) { made["walls"]++; continue; }
                try
                {
                    var baseLevel = src.GetElement(w.LevelId) as Level;
                    var baseZ = (baseLevel?.ProjectElevation ?? 0) + dz + (w.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0);
                    var level = ConcreteTypes.LevelAt(doc, baseZ);
                    var height = w.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? 10;
                    var placed = CentreLine(w, curve).CreateTransformed(tr);
                    var flat = placed.CreateTransformed(Transform.CreateTranslation(new XYZ(0, 0, level.ProjectElevation - placed.GetEndPoint(0).Z)));
                    var wall = Wall.Create(doc, flat, ConcreteTypes.Wall(doc, wallThickness ?? w.Width).Id, level.Id, height, baseZ - level.ProjectElevation, false, true);
                    // Follow the architect's top level when this model has one at that height.
                    if (src.GetElement(w.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.AsElementId() ?? ElementId.InvalidElementId) is Level top)
                    {
                        var mine = ConcreteTypes.LevelAt(doc, top.ProjectElevation + dz);
                        if (Math.Abs(mine.ProjectElevation - top.ProjectElevation - dz) < 300 / 304.8 && mine.ProjectElevation > level.ProjectElevation)
                        {
                            wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.Set(mine.Id);
                            wall.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET)?.Set(w.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET)?.AsDouble() ?? 0);
                        }
                    }
                    wall.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set($"from {w.UniqueId}");
                    made["walls"]++;
                    // Door and window openings: each opening's box, measured along the new wall.
                    if (hosted.TryGetValue(w.Id.Value, out var inserts) && flat is Line wl)
                        foreach (var ins in inserts)
                        {
                            if (ins.get_BoundingBox(null) is not { } ib) continue;
                            var corners = new[] { ib.Min, ib.Max, new XYZ(ib.Min.X, ib.Max.Y, ib.Min.Z), new XYZ(ib.Max.X, ib.Min.Y, ib.Max.Z) }.Select(tr.OfPoint).ToList();
                            var along = corners.Select(c => (c - wl.GetEndPoint(0)).DotProduct(wl.Direction)).ToList();
                            double a0 = Math.Max(along.Min(), 0), a1 = Math.Min(along.Max(), wl.Length);
                            double z0 = corners.Min(c => c.Z), z1 = corners.Max(c => c.Z);
                            if (a1 - a0 < 50 / 304.8 || z1 - z0 < 50 / 304.8) continue;
                            try
                            {
                                doc.Create.NewOpening(wall, wl.GetEndPoint(0) + wl.Direction * a0 + XYZ.BasisZ * (z0 - wl.GetEndPoint(0).Z),
                                    wl.GetEndPoint(0) + wl.Direction * a1 + XYZ.BasisZ * (z1 - wl.GetEndPoint(0).Z));
                                openingsMade++;
                            }
                            catch (Autodesk.Revit.Exceptions.ApplicationException ex) { errors.Add($"opening for {ins.Id.Value}: {ex.Message.Trim()}"); }
                        }
                }
                catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or ArgumentException or InvalidOperationException) { errors.Add($"wall {w.Id.Value}: {ex.Message.Trim()}"); }
            }
        if (what.Contains("columns"))
            foreach (var c in new FilteredElementCollector(src).OfCategory(BuiltInCategory.OST_Columns).WhereElementIsNotElementType().OfType<FamilyInstance>().Where(OnLevel))
            {
                if (done.Contains(c.UniqueId)) { skipped["already made"]++; continue; }
                if (c.Location is not LocationPoint lp) { skipped["columns"]++; continue; }
                if (dry) { made["columns"]++; continue; }
                try
                {
                    var ct = c.GetTransform();
                    var f = Rebars.Measure(c, ct.Origin, ct.BasisX, ct.BasisY, XYZ.BasisZ);
                    var symbol = ConcreteTypes.Column(doc, f.Length, f.Width);
                    if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }
                    var centre = tr.OfPoint(ct.Origin + ct.BasisX * ((f.D0 + f.D1) / 2) + ct.BasisY * ((f.W0 + f.W1) / 2));
                    var baseZ = f.U0 + ct.Origin.Z + dz;
                    var level = ConcreteTypes.LevelAt(doc, baseZ);
                    var col = doc.Create.NewFamilyInstance(new XYZ(centre.X, centre.Y, level.ProjectElevation), symbol, level, StructuralType.Column);
                    // Top first, so raising the base never puts it above the top.
                    var topZ = f.U1 + ct.Origin.Z + dz;
                    var topLevel = ConcreteTypes.LevelAt(doc, topZ);
                    col.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM)?.Set(topLevel.Id);
                    col.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM)?.Set(topZ - topLevel.ProjectElevation);
                    col.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM)?.Set(baseZ - level.ProjectElevation);
                    var angle = Math.Atan2(tr.OfVector(ct.BasisX).Y, tr.OfVector(ct.BasisX).X);
                    if (Math.Abs(angle) > 1e-6)
                        ElementTransformUtils.RotateElement(doc, col.Id, Line.CreateBound(new XYZ(centre.X, centre.Y, 0), new XYZ(centre.X, centre.Y, 1)), angle);
                    col.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set($"from {c.UniqueId}");
                    made["columns"]++;
                }
                catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or ArgumentException or InvalidOperationException)
                {
                    errors.Add($"column {c.Id.Value}: {ex.Message.Trim()}");
                    if (ex is InvalidOperationException) break;
                }
            }
        if (what.Contains("floors"))
            foreach (var fl in new FilteredElementCollector(src).OfClass(typeof(Floor)).Cast<Floor>().Where(OnLevel))
            {
                if (fl.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL)?.AsInteger() == 1 && src == doc) { skipped["floors"]++; continue; }
                if (done.Contains(fl.UniqueId)) { skipped["already made"]++; continue; }
                if (fl.get_BoundingBox(null) is { } fb && fb.Max.Z - fb.Min.Z - (fl.FloorType.GetCompoundStructure()?.GetWidth() ?? 0) > 10 / 304.8) sloped++;
                if (dry) { made["floors"]++; continue; }
                try
                {
                    var levelSrc = src.GetElement(fl.LevelId) as Level;
                    var topZ = (levelSrc?.ProjectElevation ?? 0) + dz + (fl.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.AsDouble() ?? 0) + slabOffset;
                    var level = ConcreteTypes.LevelAt(doc, topZ);
                    var loops = Outlines.FloorProfile(src, fl).Select(l => CurveLoop.CreateViaTransform(l, tr))
                        .Select(l => CurveLoop.CreateViaTransform(l, Transform.CreateTranslation(new XYZ(0, 0, level.ProjectElevation - l.First().GetEndPoint(0).Z)))).ToList();
                    var floor = Floor.Create(doc, loops, ConcreteTypes.Slab(doc, slab).Id, level.Id, true, null, 0);
                    floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.Set(topZ - level.ProjectElevation);
                    floor.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set($"from {fl.UniqueId}");
                    made["floors"]++;
                }
                catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or ArgumentException or InvalidOperationException) { errors.Add($"floor {fl.Id.Value}: {ex.Message.Trim()}"); }
            }
        if (t != null)
        {
            if (made.Values.Sum() == 0) { t.RollBack(); return ToolResult.Error($"Nothing was made. {string.Join("; ", errors.Take(10))}"); }
            if (t.Commit() != TransactionStatus.Committed) return ToolResult.Error("Revit refused the structure when saving it.");
        }
        return ToolResult.Ok(new
        {
            source = sourceName, dryRun = dry, made, skipped, errors = errors.Take(40), moreErrors = Math.Max(0, errors.Count - 40),
            deletedAtSave = swallow?.Deleted.Count ?? 0, wallOpenings = openingsMade,
            slopedFloors = sloped > 0 ? $"{sloped} architectural floor(s) are sloped or shape-edited; their slabs were made flat. Re-slope them (create_ramp or shape editing)." : null,
            note = "Check wall joins and slab edges; beams over openings and transfer structure still need adding (place_beams).",
        });
    }
}

public sealed class CreateRampTool : IRevitTool
{
    public string Name => "create_ramp";
    public string Description =>
        "Model a ramp (car park, loading or accessible ramp) as a sloped structural slab between two points: start and end " +
        "of the ramp's centre line in plan, its width, and the rise (or the level it reaches). Optional flat landings at " +
        "each end. Reports the gradient (1:n) and warns when it is steeper than the limit you give.";
    public string PropertiesJson => """
    {
      "start": {"type": "array", "items": {"type": "number"}, "description": "[x, y] bottom of the ramp, project units."},
      "end": {"type": "array", "items": {"type": "number"}, "description": "[x, y] top of the ramp."},
      "width": {"type": "number"},
      "base_level": {"type": "string"},
      "top_level": {"type": "string", "description": "Level the ramp reaches. Or give rise."},
      "rise": {"type": "number", "description": "Height climbed, project units."},
      "base_offset": {"type": "number", "description": "Height of the ramp's bottom above base_level, project units (default 0)."},
      "thickness": {"type": "number", "description": "Slab thickness, default 200 mm."},
      "landing": {"type": "number", "description": "Length of a flat landing at each end, project units (default none)."},
      "max_gradient": {"type": "number", "description": "Warn above this rise/run, e.g. 0.083 (1:12 accessible) or 0.15 (car parks). Default 0.15."}
    }
    """;
    public string[] Required => ["start", "end", "width", "base_level"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var baseLevel = RevitHelpers.FindLevel(doc, args.String("base_level"));
        var baseOffset = args.OptDouble("base_offset") is { } bo ? RevitHelpers.ToInternal(doc, bo) : 0;
        var s = Modeling.Point(doc, args.Raw["start"]);
        var e = Modeling.Point(doc, args.Raw["end"]);
        var width = RevitHelpers.ToInternal(doc, args.Double("width"));
        double rise = args.OptString("top_level") is { } tl ? RevitHelpers.FindLevel(doc, tl).ProjectElevation - baseLevel.ProjectElevation - baseOffset
            : args.OptDouble("rise") is { } r ? RevitHelpers.ToInternal(doc, r) : throw new ArgumentException("Give top_level or rise.");
        var thickness = args.OptDouble("thickness") is { } th ? RevitHelpers.ToInternal(doc, th) : 200 / 304.8;
        var landing = args.OptDouble("landing") is { } ld ? RevitHelpers.ToInternal(doc, ld) : 0;
        var maxGradient = args.OptDouble("max_gradient") ?? 0.15;
        var dir = new XYZ(e.X - s.X, e.Y - s.Y, 0);
        var run = dir.GetLength();
        if (run < 1) throw new ArgumentException("The ramp is too short.");
        if (rise <= 0) throw new ArgumentException("The ramp must climb (rise above zero).");
        dir = dir.Normalize();
        var side = XYZ.BasisZ.CrossProduct(dir) * (width / 2);
        var z = baseLevel.ProjectElevation;
        CurveLoop Rect(XYZ a, XYZ b)
        {
            var pts = new[] { new XYZ(a.X, a.Y, z) - side, new XYZ(b.X, b.Y, z) - side, new XYZ(b.X, b.Y, z) + side, new XYZ(a.X, a.Y, z) + side };
            var loop = new CurveLoop();
            for (var i = 0; i < 4; i++) loop.Append(Line.CreateBound(pts[i], pts[(i + 1) % 4]));
            return loop;
        }
        var gradient = rise / run;

        using var t = RevitHelpers.StartTransaction(doc, "Ramp");
        var type = ConcreteTypes.Slab(doc, thickness);
        var arrow = Line.CreateBound(new XYZ(s.X, s.Y, z), new XYZ(e.X, e.Y, z));
        // Revit takes 'slope' as an angle.
        var ramp = Floor.Create(doc, [Rect(s, e)], type.Id, baseLevel.Id, true, arrow, Math.Atan(gradient));
        ramp.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.Set(baseOffset);
        doc.Regenerate();
        ramp.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set("RAMP");
        var ids = new List<long> { ramp.Id.Value };
        if (landing > 0)
        {
            var bottom = Floor.Create(doc, [Rect(s - dir * landing, s)], type.Id, baseLevel.Id, true, null, 0);
            bottom.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.Set(baseOffset);
            var top = Floor.Create(doc, [Rect(e, e + dir * landing)], type.Id, baseLevel.Id, true, null, 0);
            top.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.Set(baseOffset + rise);
            ids.Add(bottom.Id.Value); ids.Add(top.Id.Value);
        }
        var measured = Climb(ramp, thickness);
        if (t.Commit() != TransactionStatus.Committed) return ToolResult.Error("Revit refused the ramp when saving it.");
        return ToolResult.Ok(new
        {
            ids, gradient = $"1:{Math.Round(1 / gradient, 1)} ({Math.Round(gradient * 100, 1)}%)",
            rise = RevitHelpers.FromInternal(doc, rise), run = RevitHelpers.FromInternal(doc, run),
            measuredRise = measured is { } m ? RevitHelpers.FromInternal(doc, m) : (double?)null,
            warning = gradient > maxGradient + 1e-9 ? $"Steeper than the limit 1:{Math.Round(1 / maxGradient, 1)}; lengthen the ramp or add a landing." : null,
            check = measured is { } mr && Math.Abs(mr - rise) > Math.Max(0.02 * rise, 10 / 304.8) ? "The modelled rise differs from the requested rise; check the ramp in section." : null,
        });
    }

    private static double? Climb(Floor floor, double thickness) =>
        floor.get_BoundingBox(null) is { } bb ? bb.Max.Z - bb.Min.Z - thickness : null;
}

public sealed class EquipmentPadsTool : IRevitTool
{
    public string Name => "equipment_pads";
    public string Description =>
        "Connect MEP equipment to the structure: a concrete housekeeping pad (plinth) under each piece of mechanical or " +
        "electrical equipment in this model or a linked MEP model, sized to the equipment plus a margin, on the slab below " +
        "it. Re-running skips equipment that already has a pad.";
    public string PropertiesJson => """
    {
      "source": {"type": "string", "description": "'model' or the MEP link name."},
      "categories": {"type": "array", "items": {"type": "string"}, "description": "Default Mechanical Equipment."},
      "height": {"type": "number", "description": "Pad height, project units (default 100 mm)."},
      "margin": {"type": "number", "description": "Pad overhang past the equipment on each side, project units (default 100 mm)."},
      "level": {"type": "string", "description": "Only equipment on this level (the source's level name)."},
      "min_size": {"type": "number", "description": "Skip equipment smaller than this in plan, project units (default 300 mm)."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var cats = args.StringList("categories");
        if (cats.Count == 0) cats = ["Mechanical Equipment"];
        var items = Sources.Collect(doc, args.OptString("source") ?? "model", cats);
        var height = args.OptDouble("height") is { } h ? RevitHelpers.ToInternal(doc, h) : 100 / 304.8;
        var margin = args.OptDouble("margin") is { } m ? RevitHelpers.ToInternal(doc, m) : 100 / 304.8;
        var minSize = args.OptDouble("min_size") is { } ms ? RevitHelpers.ToInternal(doc, ms) : 300 / 304.8;
        var levelName = args.OptString("level");
        var done = new FilteredElementCollector(doc).OfClass(typeof(Floor)).Select(f => f.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "")
            .Where(c => c.StartsWith("pad for ", StringComparison.Ordinal)).Select(c => c.Substring(8)).ToHashSet();
        var made = new List<object>(); var skipped = 0; var errors = new List<string>();
        using var t = RevitHelpers.StartTransaction(doc, "Equipment pads");
        var fo = t.GetFailureHandlingOptions(); var swallow = new TypeKit.SwallowWarnings(doc); fo.SetFailuresPreprocessor(swallow); t.SetFailureHandlingOptions(fo);
        // Slabs the pads can sit on (not earlier pads).
        var slabs = new FilteredElementCollector(doc).OfClass(typeof(Floor)).Where(f => !(f.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "").StartsWith("pad for ", StringComparison.Ordinal))
            .Select(f => f.get_BoundingBox(null)).Where(b => b != null).ToList();
        var raise = 0;
        var type = ConcreteTypes.Slab(doc, height, "Housekeeping Pad");
        foreach (var p in items)
        {
            if (levelName != null && (p.Element.Document.GetElement(p.Element.LevelId) as Level)?.Name.Equals(levelName, StringComparison.OrdinalIgnoreCase) != true) continue;
            if (done.Contains(p.Element.UniqueId)) { skipped++; continue; }
            try
            {
                // The equipment's footprint along its own axes, so rotated units get a matching pad.
                var et = (p.Element as FamilyInstance)?.GetTransform() ?? Transform.Identity;
                var bx = new XYZ(et.BasisX.X, et.BasisX.Y, 0);
                bx = bx.GetLength() < 1e-6 ? XYZ.BasisX : bx.Normalize();
                var f = Rebars.Measure(p.Element, et.Origin, bx, XYZ.BasisZ.CrossProduct(bx), XYZ.BasisZ);
                if (f.Length < minSize && f.Width < minSize) { skipped++; continue; }
                var corners = new[] { (f.D0 - margin, f.W0 - margin), (f.D1 + margin, f.W0 - margin), (f.D1 + margin, f.W1 + margin), (f.D0 - margin, f.W1 + margin) }
                    .Select(c => p.Transform.OfPoint(f.At(c.Item1, c.Item2, f.U0))).ToList();
                var bottomZ = corners[0].Z;
                // Units usually sit on the slab: put the pad on the slab top under the unit's centre and say it needs raising.
                var mid = (corners[0] + corners[2]) / 2;
                var slabTop = slabs.Where(b => b.Min.X <= mid.X && mid.X <= b.Max.X && b.Min.Y <= mid.Y && mid.Y <= b.Max.Y && b.Max.Z <= bottomZ + 50 / 304.8)
                    .Select(b => b.Max.Z).DefaultIfEmpty(double.NaN).Max();
                if (!double.IsNaN(slabTop) && bottomZ - slabTop < height - 1 / 304.8) { bottomZ = slabTop + height; raise++; }
                var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().Where(l => l.ProjectElevation <= bottomZ - height + 300 / 304.8)
                    .OrderByDescending(l => l.ProjectElevation).FirstOrDefault() ?? throw new ArgumentException("no level below it");
                var loop = new CurveLoop();
                for (var i = 0; i < 4; i++)
                {
                    XYZ a = corners[i], b = corners[(i + 1) % 4];
                    loop.Append(Line.CreateBound(new XYZ(a.X, a.Y, level.ProjectElevation), new XYZ(b.X, b.Y, level.ProjectElevation)));
                }
                var pad = Floor.Create(doc, [loop], type.Id, level.Id, true, null, 0);
                pad.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.Set(bottomZ - level.ProjectElevation);
                pad.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set($"pad for {p.Element.UniqueId}");
                pad.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set("PAD");
                made.Add(new { pad = pad.Id.Value, equipment = p.Describe(), size = $"{Math.Round(RevitHelpers.FromInternal(doc, f.Length + 2 * margin))} x {Math.Round(RevitHelpers.FromInternal(doc, f.Width + 2 * margin))}" });
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or ArgumentException) { errors.Add($"{p.Describe()}: {ex.Message.Trim()}"); }
        }
        if (made.Count == 0) { t.RollBack(); return ToolResult.Error($"No pads made ({items.Count} equipment found, {skipped} skipped). {string.Join("; ", errors.Take(10))}"); }
        if (t.Commit() != TransactionStatus.Committed) return ToolResult.Error("Revit refused the pads when saving them.");
        return ToolResult.Ok(new
        {
            pads = made.Count, skipped, list = made.Take(80), errors = errors.Take(30), deletedAtSave = swallow.Deleted.Count,
            note = raise > 0 ? $"{raise} unit(s) sat on the slab, so their pads were placed on the slab top; raise those units by the pad height." : "Pads sit under each unit's lowest point.",
        });
    }
}

public sealed class SleeveScheduleTool : IRevitTool
{
    public string Name => "sleeve_schedule";
    public string Description =>
        "Schedule of sleeves, openings and penetrations through walls, slabs and beams (plain openings, shaft openings and " +
        "sleeve/opening families such as those from create_mep_openings): id, mark, host and its mark, level, size, centre " +
        "and elevation, to a CSV for coordination with MEP and site setting-out.";
    public string PropertiesJson => """
    {
      "family_words": {"type": "array", "items": {"type": "string"}, "description": "Family name words that mean a sleeve/opening (default Sleeve, Opening, Penetration, Void, Core)."},
      "level": {"type": "string"},
      "folder": {"type": "string"},
      "file_name": {"type": "string"}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var words = args.StringList("family_words");
        if (words.Count == 0) words = ["Sleeve", "Opening", "Penetration", "Void", "Core"];
        var rows = new List<(string kind, Element e, Element? host, string size, XYZ at)>();
        string L(double ft) => Math.Round(RevitHelpers.FromInternal(doc, ft)).ToString(CultureInfo.InvariantCulture);
        foreach (var o in new FilteredElementCollector(doc).OfClass(typeof(Opening)).Cast<Opening>())
        {
            var bb = o.get_BoundingBox(null);
            if (bb is null) continue;
            var size = o.IsRectBoundary && o.BoundaryRect is { Count: 2 } r
                ? Math.Abs(r[1].Z - r[0].Z) > 1e-6
                    ? $"{L(Math.Sqrt(Math.Pow(r[1].X - r[0].X, 2) + Math.Pow(r[1].Y - r[0].Y, 2)))} x {L(Math.Abs(r[1].Z - r[0].Z))}"
                    : $"{L(Math.Max(Math.Abs(r[1].X - r[0].X), Math.Abs(r[1].Y - r[0].Y)))} x {L(Math.Min(Math.Abs(r[1].X - r[0].X), Math.Abs(r[1].Y - r[0].Y)))}"
                : $"{L(bb.Max.X - bb.Min.X)} x {L(bb.Max.Y - bb.Min.Y)}";
            rows.Add((o.Host is null ? "Shaft opening" : "Opening", o, o.Host, size, (bb.Min + bb.Max) / 2));
        }
        foreach (var f in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>())
        {
            var family = f.Symbol.FamilyName;
            if (!words.Any(w => family.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
            if (f.Category?.Id.Value is (long)BuiltInCategory.OST_Doors or (long)BuiltInCategory.OST_Windows) continue;
            var bb = f.get_BoundingBox(null);
            if (bb is null) continue;
            double? P(params string[] names) => names.Select(n => f.LookupParameter(n) ?? f.Symbol.LookupParameter(n)).FirstOrDefault(p => p is { StorageType: StorageType.Double } && p.AsDouble() > 0)?.AsDouble();
            var size = P("Diameter", "Nominal Diameter", "D") is { } d ? $"Ø{L(d)}"
                : P("Width", "W", "b") is { } w && P("Height", "H", "Depth", "h") is { } hh ? $"{L(w)} x {L(hh)}"
                : $"{L(bb.Max.X - bb.Min.X)} x {L(bb.Max.Y - bb.Min.Y)} x {L(bb.Max.Z - bb.Min.Z)}";
            rows.Add(("Sleeve/opening family", f, f.Host, size, (bb.Min + bb.Max) / 2));
        }
        if (args.OptString("level") is { } ln)
        {
            var level = RevitHelpers.FindLevel(doc, ln);
            var next = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().Where(l => l.ProjectElevation > level.ProjectElevation + 0.1).Select(l => l.ProjectElevation).DefaultIfEmpty(double.MaxValue).Min();
            rows = rows.Where(r => r.at.Z >= level.ProjectElevation - 1 && r.at.Z < next - 1).ToList();
        }
        if (rows.Count == 0) return ToolResult.Error("No openings or sleeves found.");
        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.ProjectElevation).ToList();
        string LevelOf(XYZ p) => levels.LastOrDefault(l => l.ProjectElevation <= p.Z + 1)?.Name ?? "";
        string Csv(string s) => s.IndexOfAny([',', '"', '\n']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        var sb = new StringBuilder("Id,Kind,Mark,Family/type,Host,Host id,Host mark,Level,Size,X,Y,Z\n");
        foreach (var r in rows.OrderBy(r => levels.FindLastIndex(l => l.ProjectElevation <= r.at.Z + 1)).ThenBy(r => r.host?.Id.Value))
            sb.AppendLine(string.Join(",", r.e.Id.Value, Csv(r.kind), Csv(r.e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? ""),
                Csv(r.e is FamilyInstance fi ? $"{fi.Symbol.FamilyName}: {fi.Symbol.Name}" : r.e.Name), Csv(r.host?.Category?.Name ?? ""), r.host?.Id.Value,
                Csv(r.host?.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? ""), Csv(LevelOf(r.at)), Csv(r.size), L(r.at.X), L(r.at.Y), L(r.at.Z - Elevations.BaseZ(doc))));
        var folder = OutputPaths.Checked(args.OptString("folder")) ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SketchProMax");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, (OutputPaths.Checked(args.OptString("file_name")) ?? $"{doc.Title} - Sleeve schedule {DateTime.Now:yyyy-MM-dd}") + ".csv");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        return ToolResult.Ok(new
        {
            file, total = rows.Count,
            byHost = rows.GroupBy(r => r.host?.Category?.Name ?? "No host").Select(g => new { host = g.Key, count = g.Count() }),
            byLevel = rows.GroupBy(r => LevelOf(r.at)).Select(g => new { level = g.Key, count = g.Count() }),
        });
    }
}
