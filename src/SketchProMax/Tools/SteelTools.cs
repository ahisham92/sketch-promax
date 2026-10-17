using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

/// <summary>Finding steel members and how they meet.</summary>
internal static class Steel
{
    public static List<FamilyInstance> Members(Document doc, ToolArgs args, BuiltInCategory category)
    {
        var ids = args.LongList("element_ids");
        IEnumerable<Element> source = ids.Count > 0
            ? ids.Select(i => doc.GetElement(new ElementId(i))).Where(e => e != null)!
            : new FilteredElementCollector(doc).OfCategory(category).WhereElementIsNotElementType();
        // Concrete beams and columns don't get steel connections.
        var list = source.OfType<FamilyInstance>().Where(f => f.Category?.Id.Value == (long)category
                                                         && f.StructuralMaterialType == StructuralMaterialType.Steel).ToList();
        if (args.OptString("level") is { } levelName)
        {
            var level = RevitHelpers.FindLevel(doc, levelName);
            list = list.Where(f => f.LevelId == level.Id || f.Host?.Id == level.Id
                                   || f.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM)?.AsElementId() == level.Id
                                   || f.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_PARAM)?.AsElementId() == level.Id).ToList();
        }
        return list;
    }

    public static Curve? Axis(FamilyInstance f) => (f.Location as LocationCurve)?.Curve;

    /// <summary>Column axis as a vertical (or slanted) line, for vertical columns from the bounding box.</summary>
    public static Line? ColumnAxis(FamilyInstance c)
    {
        if (Axis(c) is Line l) return l;
        if (c.Location is not LocationPoint lp) return null;
        var bb = c.get_BoundingBox(null);
        if (bb is null) return null;
        var a = new XYZ(lp.Point.X, lp.Point.Y, bb.Min.Z);
        var b = new XYZ(lp.Point.X, lp.Point.Y, bb.Max.Z);
        return a.DistanceTo(b) > 1e-3 ? Line.CreateBound(a, b) : null;
    }

    public static StructuralConnectionHandlerType? FindType(Document doc, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var types = new FilteredElementCollector(doc).OfClass(typeof(StructuralConnectionHandlerType)).Cast<StructuralConnectionHandlerType>().ToList();
        var wanted = name!.Trim();
        return types.FirstOrDefault(t => string.Equals(t.Name, wanted, StringComparison.OrdinalIgnoreCase))
               ?? types.FirstOrDefault(t => string.Equals($"{t.FamilyName}: {t.Name}", wanted, StringComparison.OrdinalIgnoreCase))
               ?? (types.Where(t => t.Name.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0).Take(2).ToList() is { Count: 1 } one ? one[0] : null);
    }

    public static string LoadedTypes(Document doc) =>
        string.Join(", ", new FilteredElementCollector(doc).OfClass(typeof(StructuralConnectionHandlerType)).Select(t => t.Name).Take(40));

    /// <summary>Connection groups already made, and every pair of members inside them, so re-running a tool (or a
    /// connection made by hand on more members) doesn't connect the same members twice.</summary>
    public static HashSet<string> ExistingGroups(Document doc)
    {
        var set = new HashSet<string>();
        foreach (var h in new FilteredElementCollector(doc).OfClass(typeof(StructuralConnectionHandler)).Cast<StructuralConnectionHandler>())
        {
            var ids = h.GetConnectedElementIds().ToList();
            set.Add(Key(ids));
            for (var a = 0; a < ids.Count; a++)
                for (var b = a + 1; b < ids.Count; b++) set.Add(Key([ids[a], ids[b]]));
        }
        return set;
    }

    /// <summary>True when the group, or each member's pair with the primary, is already connected.</summary>
    public static bool Connected(HashSet<string> existing, List<ElementId> ids) =>
        existing.Contains(Key(ids)) || (ids.Count > 1 && ids.Skip(1).All(i => existing.Contains(Key([ids[0], i]))));

    public static string Key(IEnumerable<ElementId> ids) => string.Join(",", ids.Select(i => i.Value).OrderBy(v => v));
}

public sealed class LoadSteelSectionsTool : IRevitTool
{
    public string Name => "load_steel_sections";
    public string Description =>
        "Load steel section sizes (e.g. 'UB 457x191x67', 'UC 254x254x73', 'W12x26', 'HSS6x6x1/4', 'SHS 100x100x6', 'IPE 300', " +
        "'HEA 200', 'CHS 168.3x6.3') from the Revit content library's structural framing and column families and their type " +
        "catalogs, so they can be used with place_beams, place_columns and place_bracing. Reports sizes it can't find.";
    public string PropertiesJson => """
    {
      "sections": {"type": "array", "items": {"type": "string"}},
      "use_as": {"type": "string", "enum": ["framing", "columns", "both"], "description": "Which families to search (default both)."},
      "folder": {"type": "string", "description": "Extra folder with the firm's steel families."}
    }
    """;
    public string[] Required => ["sections"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var useAs = args.OptString("use_as") ?? "both";
        var roots = app.Application.GetLibraryPaths().Values.ToList();
        if (args.OptString("folder") is { } extra) roots.Insert(0, extra);
        // Steel families sit in Structural Framing / Structural Columns folders with a .txt type catalog beside them.
        var families = new List<string>();
        foreach (var root in roots.Where(Directory.Exists))
        {
            try
            {
                families.AddRange(Directory.EnumerateFiles(root, "*.rfa", SearchOption.AllDirectories).Where(f =>
                {
                    var dir = Path.GetDirectoryName(f) ?? "";
                    var framing = dir.IndexOf("Framing", StringComparison.OrdinalIgnoreCase) >= 0;
                    var columns = dir.IndexOf("Column", StringComparison.OrdinalIgnoreCase) >= 0;
                    return (useAs != "columns" && framing) || (useAs != "framing" && columns);
                }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        // A catalog Revit can't parse makes LoadFamilySymbol show a modal dialog, so each one is checked first and a bad one skipped.
        var badCatalogs = new List<string>();
        var catalogs = families.Select(f => (family: f, catalog: Path.ChangeExtension(f, ".txt"))).Where(x => File.Exists(x.catalog))
            .Select(x => (x.family, types: CatalogTypes(x.catalog, badCatalogs))).Where(x => x.types.Count > 0).ToList();
        if (catalogs.Count == 0)
            return ToolResult.Error(badCatalogs.Count > 0
                ? $"The steel type catalogs found can't be read by Revit, so nothing was loaded: {string.Join("; ", badCatalogs.Take(3))}. Fix the catalog's first line or pass another folder."
                : $"No steel families with type catalogs found in: {string.Join("; ", roots)}. Install the Revit content library or pass folder.");

        var loaded = new List<object>();
        var notFound = new List<string>();
        using var t = RevitHelpers.StartTransaction(doc, "Load steel sections");
        foreach (var section in args.StringList("sections"))
        {
            var key = Normal(section);
            var hits = catalogs.SelectMany(c => c.types.Where(ty => Normal(ty) == key).Select(ty => (c.family, type: ty))).ToList();
            // 'UB 457x191x67' is '457x191x67UB' in UK catalogs: compare with the letters moved aside.
            if (hits.Count == 0)
                hits = catalogs.SelectMany(c => c.types.Where(ty => Canon(ty) == Canon(section)).Select(ty => (c.family, type: ty))).ToList();
            if (hits.Count == 0 && key.Length >= 5)
                hits = catalogs.SelectMany(c => c.types.Where(ty => Normal(ty).EndsWith(key)).Select(ty => (c.family, type: ty))).Take(3).ToList();
            if (hits.Count == 0) { notFound.Add(section); continue; }
            foreach (var (family, type) in hits.GroupBy(h => Path.GetDirectoryName(h.family)!.IndexOf("Column", StringComparison.OrdinalIgnoreCase) >= 0).Select(g => g.First()))
            {
                if (doc.LoadFamilySymbol(family, type, out var symbol) || symbol != null)
                {
                    symbol ??= new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                        .FirstOrDefault(s => s.Name == type && s.FamilyName == Path.GetFileNameWithoutExtension(family));
                    loaded.Add(new { section, typeId = symbol?.Id.Value, type, family = Path.GetFileNameWithoutExtension(family), category = symbol?.Category?.Name });
                }
                else
                {
                    var existing = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                        .FirstOrDefault(s => s.Name == type && s.FamilyName == Path.GetFileNameWithoutExtension(family));
                    if (existing != null) loaded.Add(new { section, typeId = existing.Id.Value, type, family = existing.FamilyName, category = existing.Category?.Name, alreadyLoaded = true });
                    else notFound.Add(section);
                }
            }
        }
        var skipped = badCatalogs.Count == 0 ? null : badCatalogs;
        if (loaded.Count == 0)
        {
            t.RollBack();
            return ToolResult.Error($"None of these sizes were found in the library: {string.Join(", ", notFound)}." +
                                    (skipped is null ? "" : $" Skipped catalogs Revit can't read: {string.Join("; ", skipped.Take(3))}."));
        }
        t.Commit();
        return ToolResult.Ok(new { loaded, notFound, skippedCatalogs = skipped });
    }

    private static string Normal(string s) => Regex.Replace(s.ToUpperInvariant().Replace('×', 'X'), @"[\s\-_]", "");

    private static string Canon(string s)
    {
        var m = Regex.Match(Normal(s), @"^([A-Z]*)(.*?)([A-Z]*)$");
        return $"{m.Groups[1].Value}{m.Groups[3].Value}|{m.Groups[2].Value}";
    }

    private static List<string> CatalogTypes(string catalog, List<string> bad)
    {
        try
        {
            var lines = File.ReadAllLines(catalog);
            if (TypeCatalog.Problem(lines) is { } problem) { bad.Add($"{Path.GetFileName(catalog)}: {problem}"); return []; }
            return TypeCatalog.Types(lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
}

public sealed class PlaceBracingTool : IRevitTool
{
    public string Name => "place_bracing";
    public string Description =>
        "Place steel bracing in bays between pairs of columns (X, single diagonal, V or inverted V / chevron) from one " +
        "level to the next, or along given lines. Use load_steel_sections or list_types 'Structural Framing' for the brace type.";
    public string PropertiesJson => """
    {
      "type_id": {"type": "integer", "description": "Structural framing type for the braces."},
      "bays": {"type": "array", "items": {"type": "object", "properties": {
        "column_ids": {"type": "array", "items": {"type": "integer"}, "description": "Two columns bounding the bay."},
        "base_level": {"type": "string"}, "top_level": {"type": "string"},
        "pattern": {"type": "string", "enum": ["x", "diagonal", "v", "chevron"]}
      }, "required": ["column_ids", "base_level", "top_level"]}},
      "lines": {"type": "array", "items": {"type": "object", "properties": {
        "start": {"type": "array", "items": {"type": "number"}}, "end": {"type": "array", "items": {"type": "number"}}, "level": {"type": "string"}
      }, "required": ["start", "end"]}, "description": "Free braces: [x, y, z] points, project units."}
    }
    """;
    public string[] Required => ["type_id"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var lines = new List<(XYZ a, XYZ b, Level level)>();
        var errors = new List<object>();
        var i = 0;
        foreach (var bay in args.Array("bays"))
        {
            i++;
            try
            {
                var cols = bay.GetProperty("column_ids").EnumerateArray().Select(x => doc.GetElement(new ElementId(x.GetInt64())) as FamilyInstance).ToList();
                if (cols.Count != 2 || cols.Any(c => c is null)) throw new ArgumentException("give two column ids");
                var baseLevel = RevitHelpers.FindLevel(doc, bay.GetProperty("base_level").GetString());
                var topLevel = RevitHelpers.FindLevel(doc, bay.GetProperty("top_level").GetString());
                if (topLevel.ProjectElevation <= baseLevel.ProjectElevation) throw new ArgumentException("top level must be above base level");
                var p = cols.Select(c => c!.Location switch
                {
                    LocationPoint lp => lp.Point,
                    LocationCurve lc => lc.Curve.GetEndPoint(0),
                    _ => throw new ArgumentException($"column {c.Id.Value} has no location"),
                }).ToList();
                double z0 = baseLevel.ProjectElevation, z1 = topLevel.ProjectElevation;
                XYZ A0 = new(p[0].X, p[0].Y, z0), A1 = new(p[0].X, p[0].Y, z1), B0 = new(p[1].X, p[1].Y, z0), B1 = new(p[1].X, p[1].Y, z1);
                var mid0 = (A0 + B0) / 2; var mid1 = (A1 + B1) / 2;
                var pattern = bay.TryGetProperty("pattern", out var pt) ? pt.GetString() : "x";
                switch (pattern)
                {
                    case "diagonal": lines.Add((A0, B1, baseLevel)); break;
                    case "v": lines.Add((A1, mid0, baseLevel)); lines.Add((B1, mid0, baseLevel)); break;
                    case "chevron": lines.Add((A0, mid1, baseLevel)); lines.Add((B0, mid1, baseLevel)); break;
                    default: lines.Add((A0, B1, baseLevel)); lines.Add((B0, A1, baseLevel)); break;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
            {
                errors.Add(new { bay = i, error = ex.Message });
            }
        }
        var n = 0;
        foreach (var l in args.Array("lines"))
        {
            n++;
            try
            {
                var a = Modeling.Point(doc, l.GetProperty("start"));
                var b = Modeling.Point(doc, l.GetProperty("end"));
                var level = l.TryGetProperty("level", out var lv) ? RevitHelpers.FindLevel(doc, lv.GetString())
                    : new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(x => Math.Abs(x.ProjectElevation - Math.Min(a.Z, b.Z))).FirstOrDefault()
                      ?? throw new InvalidOperationException("the model has no levels");
                lines.Add((a, b, level));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
            {
                errors.Add(new { line = n, error = ex.Message });
            }
        }
        if (lines.Count == 0) return ToolResult.Error($"No braces to place. {System.Text.Json.JsonSerializer.Serialize(errors)}");

        var created = new List<long>();
        using var t = RevitHelpers.StartTransaction(doc, "Place bracing");
        var symbol = Modeling.Symbol(doc, args.OptLong("type_id") ?? -1);
        foreach (var (a, b, level) in lines)
        {
            try
            {
                if (a.DistanceTo(b) < doc.Application.ShortCurveTolerance) { errors.Add(new { error = "brace start and end are the same point" }); continue; }
                created.Add(doc.Create.NewFamilyInstance(Line.CreateBound(a, b), symbol, level, StructuralType.Brace).Id.Value);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex) { errors.Add(new { error = ex.Message.Trim() }); }
        }
        if (created.Count == 0) { t.RollBack(); return ToolResult.Error($"No braces were placed: {System.Text.Json.JsonSerializer.Serialize(errors)}"); }
        if (t.Commit() != TransactionStatus.Committed) return ToolResult.Error("Revit refused the braces when saving them, so none were placed.");
        return ToolResult.Ok(new { created = created.Count, ids = created, errors });
    }
}

public sealed class ListSteelConnectionTypesTool : IRevitTool
{
    public string Name => "list_steel_connection_types";
    public string Description =>
        "List the steel connection types loaded in the model (shear plate, end plate, base plate, clip angle, gusset, splice, " +
        "...) and how many of each are placed. Connection types come from Revit's Steel Connections; load more in " +
        "Structure > Connection > Connection Settings (the API can't add Autodesk's types).";
    public string PropertiesJson => "{}";
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var used = new FilteredElementCollector(doc).OfClass(typeof(StructuralConnectionHandler)).Cast<StructuralConnectionHandler>()
            .GroupBy(h => h.GetTypeId().Value).ToDictionary(g => g.Key, g => g.Count());
        var types = new FilteredElementCollector(doc).OfClass(typeof(StructuralConnectionHandlerType)).Cast<StructuralConnectionHandlerType>()
            .Select(t => new { id = t.Id.Value, name = t.Name, family = t.FamilyName, placed = used.TryGetValue(t.Id.Value, out var n) ? n : 0 })
            .OrderBy(t => t.name).ToList();
        return ToolResult.Ok(new
        {
            types, connectionsInModel = used.Values.Sum(),
            note = types.Count <= 1
                ? "Only the generic type (or none) is loaded. In Revit: Structure tab > Connection panel > Connection Settings, add the connection types you use (once per project or template). Generic connections can be placed meanwhile."
                : null,
        });
    }
}

public sealed class CreateSteelConnectionsTool : IRevitTool
{
    public string Name => "create_steel_connections";
    public string Description =>
        "Find where steel members meet and add steel connections: beam to column, beam to beam, column base plates " +
        "(steel to concrete, to a footing or slab when one is there), beam to concrete wall, brace to beam/column gussets, " +
        "and column splices. Works on given members, a level, or the whole model. Run with dry_run first to see the joints. " +
        "Uses the named connection type (list_steel_connection_types); with allow_generic, a generic connection marks joints " +
        "whose type isn't loaded. Joints that already have a connection are skipped.";
    public string PropertiesJson => """
    {
      "kind": {"type": "string", "enum": ["beam_column", "beam_beam", "base_plate", "beam_wall", "brace", "splice"]},
      "connection_type": {"type": "string", "description": "Loaded connection type name, e.g. 'Shear plate', 'End plate', 'Base plate', 'Gusset plate'."},
      "element_ids": {"type": "array", "items": {"type": "integer"}, "description": "Limit to these members (default all)."},
      "level": {"type": "string", "description": "Limit to members on this level."},
      "tolerance": {"type": "number", "description": "How far a member end may be from the other member, project units (default ~300 mm)."},
      "allow_generic": {"type": "boolean", "description": "Use a generic connection when the type isn't loaded (default false)."},
      "dry_run": {"type": "boolean", "description": "Only report the joints found."},
      "limit": {"type": "integer", "description": "Max connections in one call (default 500)."}
    }
    """;
    public string[] Required => ["kind"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var kind = args.String("kind");
        var tol = args.OptDouble("tolerance") is { } tv ? RevitHelpers.ToInternal(doc, tv) : 300 / 304.8;
        var joints = kind switch
        {
            "beam_column" => BeamColumn(doc, args, tol),
            "beam_beam" => BeamBeam(doc, args, tol),
            "base_plate" => BasePlates(doc, args, tol),
            "beam_wall" => BeamWall(doc, args, tol),
            "brace" => Braces(doc, args, tol),
            "splice" => Splices(doc, args, tol),
            _ => throw new ArgumentException($"Unknown kind '{kind}'."),
        };
        var existing = Steel.ExistingGroups(doc);
        var fresh = joints.Where(j => !Steel.Connected(existing, j.ids)).GroupBy(j => Steel.Key(j.ids)).Select(g => g.First()).ToList();
        var limit = (int)(args.OptLong("limit") ?? 500);
        object Describe((List<ElementId> ids, string what) j) => new { ids = j.ids.Select(i => i.Value), j.what };

        if (args.OptBool("dry_run"))
            return ToolResult.Ok(new { kind, joints = fresh.Count, alreadyConnected = joints.Count - fresh.Count, sample = fresh.Take(30).Select(Describe) });
        if (fresh.Count == 0)
            return ToolResult.Ok(new { kind, created = 0, alreadyConnected = joints.Count, note = joints.Count == 0 ? "No joints of this kind were found. Check the members' ends meet (tolerance) and that they are structural columns/framing." : "Every joint already has a connection." });

        var type = Steel.FindType(doc, args.OptString("connection_type"));
        var generic = args.OptBool("allow_generic");
        if (type is null && !generic)
            return ToolResult.Error($"Connection type '{args.OptString("connection_type") ?? "(none given)"}' isn't loaded. Loaded: {Steel.LoadedTypes(doc)}. " +
                                    "Load it in Structure > Connection > Connection Settings, or pass allow_generic true to mark the joints with generic connections.");
        var created = new List<long>();
        var errors = new Dictionary<string, int>();
        void Fail(string m, int count = 1) => errors[m] = errors.TryGetValue(m, out var n) ? n + count : count;
        // Small batches: if Revit refuses one batch at commit, only that batch is undone.
        var todo = fresh.Take(limit).ToList();
        for (var start = 0; start < todo.Count; start += 50)
        {
            var batch = new List<long>();
            using var t = RevitHelpers.StartTransaction(doc, $"Steel connections ({kind})");
            foreach (var j in todo.Skip(start).Take(50))
            {
                try
                {
                    var h = type is null ? StructuralConnectionHandler.CreateGenericConnection(doc, j.ids) : StructuralConnectionHandler.Create(doc, j.ids, type.Id);
                    batch.Add(h.Id.Value);
                }
                catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or ArgumentException)
                {
                    Fail(ex.Message.Trim());
                }
            }
            if (batch.Count == 0) { t.RollBack(); continue; }
            if (t.Commit() == TransactionStatus.Committed) created.AddRange(batch);
            else Fail("Revit refused a batch of connections when saving it, so that batch was undone", batch.Count);
        }
        if (created.Count == 0) return ToolResult.Error($"No connections were made. Revit said: {string.Join("; ", errors.Select(e => $"{e.Value}× {e.Key}"))}");
        return ToolResult.Ok(new
        {
            kind, type = type?.Name ?? "Generic", created = created.Count, ids = created.Take(300),
            remaining = Math.Max(0, fresh.Count - limit), alreadyConnected = joints.Count - fresh.Count,
            failed = errors.Select(e => $"{e.Value}: {e.Key}"),
        });
    }

    private static IEnumerable<XYZ> Ends(FamilyInstance f) =>
        Steel.Axis(f) is { IsBound: true } c ? [c.GetEndPoint(0), c.GetEndPoint(1)] : [];

    /// <summary>Elements of some categories, indexed by their bounding boxes so each lookup checks only nearby ones.</summary>
    private static SpatialIndex<Element> Index(Document doc, double tol, params BuiltInCategory[] categories)
    {
        var index = new SpatialIndex<Element>(Math.Max(tol * 4, 10));
        var filter = categories.Length == 1 ? (ElementFilter)new ElementCategoryFilter(categories[0])
            : new LogicalOrFilter(categories.Select(c => (ElementFilter)new ElementCategoryFilter(c)).ToList());
        foreach (var e in new FilteredElementCollector(doc).WherePasses(filter).WhereElementIsNotElementType())
            if (e.get_BoundingBox(null) is { } bb) index.Add(e, bb.Min, bb.Max);
        return index;
    }

    private static List<(List<ElementId> ids, string what)> BeamColumn(Document doc, ToolArgs args, double tol)
    {
        var beams = Steel.Members(doc, args, BuiltInCategory.OST_StructuralFraming).Where(b => b.StructuralType == StructuralType.Beam).ToList();
        var columns = Index(doc, tol, BuiltInCategory.OST_StructuralColumns);
        var axes = new Dictionary<long, Line?>();
        Line? AxisOf(FamilyInstance c) => axes.TryGetValue(c.Id.Value, out var a) ? a : axes[c.Id.Value] = Steel.ColumnAxis(c);
        var result = new List<(List<ElementId>, string)>();
        foreach (var b in beams)
            foreach (var end in Ends(b))
            {
                FamilyInstance? best = null; var bestD = double.MaxValue;
                foreach (var c in columns.Near(end, tol).OfType<FamilyInstance>())
                {
                    if (AxisOf(c) is not { } axis) continue;
                    var d = new XYZ(end.X - axis.GetEndPoint(0).X, end.Y - axis.GetEndPoint(0).Y, 0).GetLength();
                    if (Math.Abs(axis.Direction.Z) < 0.99) d = axis.Project(end)?.Distance ?? double.MaxValue;
                    if (d <= tol && d < bestD) { best = c; bestD = d; }
                }
                if (best != null) result.Add(([best.Id, b.Id], $"beam {b.Id.Value} to column {best.Id.Value}"));
            }
        return result;
    }

    private static List<(List<ElementId> ids, string what)> BeamBeam(Document doc, ToolArgs args, double tol)
    {
        var beams = Steel.Members(doc, args, BuiltInCategory.OST_StructuralFraming).Where(b => b.StructuralType == StructuralType.Beam).ToList();
        var framing = Index(doc, tol, BuiltInCategory.OST_StructuralFraming);
        var result = new List<(List<ElementId>, string)>();
        foreach (var b in beams)
            foreach (var end in Ends(b))
            {
                // A secondary beam ends on the span of a primary beam (not at its ends).
                FamilyInstance? best = null; var bestD = double.MaxValue;
                foreach (var p in framing.Near(end, tol).OfType<FamilyInstance>())
                {
                    if (p.Id == b.Id || p.StructuralType != StructuralType.Beam || Steel.Axis(p) is not Line line) continue;
                    var r = line.Project(end);
                    if (r is null || r.Distance > tol || r.XYZPoint.DistanceTo(line.GetEndPoint(0)) <= tol || r.XYZPoint.DistanceTo(line.GetEndPoint(1)) <= tol) continue;
                    if (r.Distance < bestD) { best = p; bestD = r.Distance; }
                }
                if (best != null) result.Add(([best.Id, b.Id], $"beam {b.Id.Value} to beam {best.Id.Value}"));
            }
        return result;
    }

    private static List<(List<ElementId> ids, string what)> BasePlates(Document doc, ToolArgs args, double tol)
    {
        var columns = Steel.Members(doc, args, BuiltInCategory.OST_StructuralColumns);
        var allColumns = Index(doc, tol, BuiltInCategory.OST_StructuralColumns);
        var supports = Index(doc, tol, BuiltInCategory.OST_StructuralFoundation, BuiltInCategory.OST_Floors);
        var result = new List<(List<ElementId>, string)>();
        foreach (var c in columns)
        {
            if (Steel.ColumnAxis(c) is not { } axis) continue;
            var bottom = axis.GetEndPoint(0).Z <= axis.GetEndPoint(1).Z ? axis.GetEndPoint(0) : axis.GetEndPoint(1);
            // Only columns that don't sit on another column.
            if (allColumns.Near(bottom - new XYZ(0, 0, tol / 2), tol / 4).Any(e => e.Id != c.Id)) continue;
            var support = supports.Near(bottom, tol).FirstOrDefault();
            result.Add((support is null ? [c.Id] : [c.Id, support.Id], support is null ? $"base of column {c.Id.Value}" : $"column {c.Id.Value} to {support.Category?.Name} {support.Id.Value}"));
        }
        return result;
    }

    private static List<(List<ElementId> ids, string what)> BeamWall(Document doc, ToolArgs args, double tol)
    {
        var beams = Steel.Members(doc, args, BuiltInCategory.OST_StructuralFraming).Where(b => b.StructuralType == StructuralType.Beam).ToList();
        var walls = Index(doc, tol, BuiltInCategory.OST_Walls);
        var result = new List<(List<ElementId>, string)>();
        foreach (var b in beams)
            foreach (var end in Ends(b))
            {
                Wall? best = null; var bestD = double.MaxValue;
                foreach (var wall in walls.Near(end, tol).OfType<Wall>())
                {
                    if (wall.WallType.Kind != WallKind.Basic || (wall.Location as LocationCurve)?.Curve is not { } line) continue;
                    var bb = wall.get_BoundingBox(null);
                    if (bb is null || end.Z < bb.Min.Z - tol || end.Z > bb.Max.Z + tol) continue;
                    // Distance in plan from the beam end to the wall's centre line.
                    var flat = new XYZ(end.X, end.Y, line.GetEndPoint(0).Z);
                    var r = line.Project(flat);
                    if (r is null) continue;
                    var d = r.Distance - wall.Width / 2;
                    if (d <= tol && d < bestD) { best = wall; bestD = d; }
                }
                if (best != null) result.Add(([best.Id, b.Id], $"beam {b.Id.Value} to wall {best.Id.Value}"));
            }
        return result;
    }

    private static List<(List<ElementId> ids, string what)> Braces(Document doc, ToolArgs args, double tol)
    {
        var braces = Steel.Members(doc, args, BuiltInCategory.OST_StructuralFraming).Where(b => b.StructuralType == StructuralType.Brace).ToList();
        var members = Index(doc, tol, BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_StructuralFraming);
        var result = new List<(List<ElementId>, string)>();
        foreach (var br in braces)
            foreach (var end in Ends(br))
            {
                FamilyInstance? column = null, beam = null; double columnD = double.MaxValue, beamD = double.MaxValue;
                foreach (var f in members.Near(end, tol).OfType<FamilyInstance>())
                {
                    if (f.Id == br.Id || f.StructuralType == StructuralType.Brace) continue;
                    var isColumn = f.Category?.Id.Value == (long)BuiltInCategory.OST_StructuralColumns;
                    Curve? axis = isColumn ? Steel.ColumnAxis(f) : Steel.Axis(f);
                    var d = axis?.Project(end)?.Distance ?? double.MaxValue;
                    if (d > tol) continue;
                    if (isColumn) { if (d < columnD) { column = f; columnD = d; } }
                    else if (d < beamD) { beam = f; beamD = d; }
                }
                var near = new[] { column, beam }.Where(f => f != null).Select(f => f!.Id).ToList();
                if (near.Count > 0) result.Add((near.Concat([br.Id]).ToList(), $"brace {br.Id.Value} gusset"));
            }
        return result;
    }

    private static List<(List<ElementId> ids, string what)> Splices(Document doc, ToolArgs args, double tol)
    {
        var columns = Steel.Members(doc, args, BuiltInCategory.OST_StructuralColumns);
        var allColumns = Index(doc, tol, BuiltInCategory.OST_StructuralColumns);
        var result = new List<(List<ElementId>, string)>();
        var seen = new HashSet<string>();
        foreach (var c in columns)
        {
            if (Steel.ColumnAxis(c) is not { } axis) continue;
            var top = axis.GetEndPoint(0).Z > axis.GetEndPoint(1).Z ? axis.GetEndPoint(0) : axis.GetEndPoint(1);
            var above = allColumns.Near(top + new XYZ(0, 0, tol / 2), tol / 4).FirstOrDefault(e => e.Id != c.Id);
            if (above is null || !seen.Add(Steel.Key([c.Id, above.Id]))) continue;
            result.Add(([c.Id, above.Id], $"splice column {c.Id.Value} / {above.Id.Value}"));
        }
        return result;
    }
}

/// <summary>A plan grid of buckets over bounding boxes: lookups only test items whose box is near the point.</summary>
internal sealed class SpatialIndex<T>(double cell)
{
    private readonly Dictionary<(long, long), List<(T item, XYZ min, XYZ max)>> _cells = new();

    public void Add(T item, XYZ min, XYZ max)
    {
        for (var x = (long)Math.Floor(min.X / cell); x <= (long)Math.Floor(max.X / cell); x++)
            for (var y = (long)Math.Floor(min.Y / cell); y <= (long)Math.Floor(max.Y / cell); y++)
            {
                if (!_cells.TryGetValue((x, y), out var list)) _cells[(x, y)] = list = [];
                list.Add((item, min, max));
            }
    }

    /// <summary>Items whose box, grown by tol, contains the point; nearest box centre first.</summary>
    public IEnumerable<T> Near(XYZ p, double tol)
    {
        var hits = new List<(T item, double d)>();
        var seen = new HashSet<T>();
        for (var x = (long)Math.Floor((p.X - tol) / cell); x <= (long)Math.Floor((p.X + tol) / cell); x++)
            for (var y = (long)Math.Floor((p.Y - tol) / cell); y <= (long)Math.Floor((p.Y + tol) / cell); y++)
            {
                if (!_cells.TryGetValue((x, y), out var list)) continue;
                foreach (var (item, min, max) in list)
                    if (p.X >= min.X - tol && p.X <= max.X + tol && p.Y >= min.Y - tol && p.Y <= max.Y + tol && p.Z >= min.Z - tol && p.Z <= max.Z + tol
                        && seen.Add(item))
                        hits.Add((item, p.DistanceTo((min + max) / 2)));
            }
        return hits.OrderBy(h => h.d).Select(h => h.item);
    }
}

public sealed class ExportSteelTakeoffTool : IRevitTool
{
    public string Name => "export_steel_takeoff";
    public string Description =>
        "Steel tonnage: per section size, the count, total length and weight of steel beams, columns and braces (weight " +
        "from volume × the material's density, or the section's nominal weight per length), plus connections counted by " +
        "type, to a CSV that opens in Excel. Optionally per level.";
    public string PropertiesJson => """
    {
      "by_level": {"type": "boolean"},
      "folder": {"type": "string"},
      "file_name": {"type": "string"}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    /// <summary>A section's weight per metre from its type parameters, only when the parameter really is a mass (or weight) per length.</summary>
    private static double? NominalKgPerM(FamilySymbol symbol)
    {
        foreach (Parameter p in symbol.Parameters)
        {
            if (p.StorageType != StorageType.Double || p.AsDouble() <= 0) continue;
            var spec = p.Definition.GetDataType();
            if (spec == SpecTypeId.MassPerUnitLength) return UnitUtils.ConvertFromInternalUnits(p.AsDouble(), UnitTypeId.KilogramsPerMeter);
            if (spec == SpecTypeId.WeightPerUnitLength || spec == SpecTypeId.LinearForce)
                return UnitUtils.ConvertFromInternalUnits(p.AsDouble(), UnitTypeId.KilonewtonsPerMeter) * 1000 / 9.80665;
        }
        return null;
    }

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var byLevel = args.OptBool("by_level");
        var densityCache = new Dictionary<long, double?>();
        double? Density(ElementId materialId)
        {
            if (densityCache.TryGetValue(materialId.Value, out var d)) return d;
            double? result = null;
            if (doc.GetElement(materialId) is Material m && doc.GetElement(m.StructuralAssetId) is PropertySetElement pse && pse.GetStructuralAsset() is { } asset
                && asset.StructuralAssetClass == StructuralAssetClass.Metal)
                result = UnitUtils.ConvertFromInternalUnits(asset.Density, UnitTypeId.KilogramsPerCubicMeter);
            return densityCache[materialId.Value] = result;
        }

        var rows = new Dictionary<string, (string cat, string section, string level, int count, double lengthM, double kg, int estimated)>();
        var skippedNonSteel = 0;
        foreach (var bic in new[] { BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_StructuralColumns })
            foreach (var f in new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType().OfType<FamilyInstance>())
            {
                var materialId = f.StructuralMaterialId;
                var density = Density(materialId);
                var isSteel = f.StructuralMaterialType == StructuralMaterialType.Steel || density is not null;
                if (!isSteel) { skippedNonSteel++; continue; }
                var lengthFt = f.get_Parameter(BuiltInParameter.STRUCTURAL_FRAME_CUT_LENGTH)?.AsDouble() is > 0 and var cut ? cut
                    : (f.Location as LocationCurve)?.Curve.Length ?? (Steel.ColumnAxis(f)?.Length ?? 0);
                var lengthM = UnitUtils.ConvertFromInternalUnits(lengthFt, UnitTypeId.Meters);
                var volume = f.get_Parameter(BuiltInParameter.HOST_VOLUME_COMPUTED) is { StorageType: StorageType.Double } vp ? UnitUtils.ConvertFromInternalUnits(vp.AsDouble(), UnitTypeId.CubicMeters) : 0;
                double kg; var estimated = 0;
                if (volume > 0) kg = volume * (density ?? 7850);
                else if (NominalKgPerM(f.Symbol) is { } kgPerM) { kg = kgPerM * lengthM; estimated = 1; }
                else { kg = 0; estimated = 1; }
                var level = byLevel ? (doc.GetElement(f.LevelId) as Level ?? doc.GetElement(f.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM)?.AsElementId() ?? ElementId.InvalidElementId) as Level)?.Name ?? "" : "";
                var cat = f.StructuralType == StructuralType.Brace ? "Braces" : bic == BuiltInCategory.OST_StructuralColumns ? "Columns" : "Beams";
                var section = $"{f.Symbol.FamilyName}: {f.Symbol.Name}";
                var key = $"{cat}|{section}|{level}";
                rows.TryGetValue(key, out var r);
                rows[key] = (cat, section, level, r.count + 1, r.lengthM + lengthM, r.kg + kg, r.estimated + estimated);
            }
        var connections = new FilteredElementCollector(doc).OfClass(typeof(StructuralConnectionHandler))
            .GroupBy(h => doc.GetElement(h.GetTypeId())?.Name ?? "?").ToDictionary(g => g.Key, g => g.Count());
        if (rows.Count == 0 && connections.Count == 0) return ToolResult.Error("No steel framing, columns or connections found.");

        var folder = OutputPaths.Checked(args.OptString("folder")) ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SketchProMax");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, (OutputPaths.Checked(args.OptString("file_name")) ?? $"{doc.Title} - Steel takeoff {DateTime.Now:yyyy-MM-dd}") + ".csv");
        string Csv(string s) => s.IndexOfAny([',', '"', '\n']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        string N(double v) => Math.Round(v, 2).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.AppendLine(byLevel ? "Member,Section,Level,Count,Length (m),Weight (kg),Weight (t)" : "Member,Section,Count,Length (m),Weight (kg),Weight (t)");
        foreach (var r in rows.Values.OrderBy(r => r.cat).ThenBy(r => r.section).ThenBy(r => r.level))
            sb.AppendLine(string.Join(",", new[] { Csv(r.cat), Csv(r.section) }.Concat(byLevel ? [Csv(r.level)] : [])
                .Concat([r.count.ToString(), N(r.lengthM), N(r.kg), N(r.kg / 1000)])));
        sb.AppendLine();
        sb.AppendLine("Connection type,Count");
        foreach (var c in connections.OrderBy(c => c.Key)) sb.AppendLine($"{Csv(c.Key)},{c.Value}");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));

        return ToolResult.Ok(new
        {
            file, totalTonnes = Math.Round(rows.Values.Sum(r => r.kg) / 1000, 2),
            byMember = rows.Values.GroupBy(r => r.cat).Select(g => new { member = g.Key, count = g.Sum(r => r.count), tonnes = Math.Round(g.Sum(r => r.kg) / 1000, 2) }),
            connections, nonSteelSkipped = skippedNonSteel,
            weightFromNominalOrMissing = rows.Values.Sum(r => r.estimated),
            note = "Connection plates and bolts are not weighed; add an allowance (often 5-10%) if needed.",
        });
    }
}
