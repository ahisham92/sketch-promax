using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

/// <summary>
/// 2D reinforcement detailing with an office's own detail families instead of 3D rebar: additional-bar lines on slab
/// plans, mesh symbols per slab, and column section details for column schedules. Each kind of family has a role; the
/// family is the one named in the call, else the one remembered in the model for that role, else the best match by name
/// among the loaded detail and annotation families (<see cref="ModelRules.DetailRoleRule"/>). The family used is
/// remembered in the model for its role, so later calls can leave it out. Placement, the copy fallback and parameter
/// read-back are <see cref="DetailPlacement"/>'s.
/// </summary>
internal static class Rft2d
{
    public sealed record Role(string Key, string What, ModelRules.DetailRoleRule Rule);

    public static readonly Role AdditionalBars = new("additional_bars", "additional reinforcement (line-based detail item)", ModelRules.AdditionalBarsRule);
    public static readonly Role Mesh = new("mesh", "mesh symbol (detail item or generic annotation)", ModelRules.MeshRule);
    public static readonly Role ColumnSection = new("column_section", "column / beam section reinforcement (detail item)", ModelRules.ColumnSectionRule);
    public static readonly Role Ties = new("ties", "transversal bars / links (detail item)", ModelRules.TiesRule);
    public static readonly Role[] All = [AdditionalBars, Mesh, ColumnSection, Ties];

    private const string KeyPrefix = "rft2d.family.";
    private const string Found = "found by name among the loaded families";

    /// <summary>
    /// The family name for a role: given, else remembered in the model, else the best match among the loaded families
    /// (null when none fits); where it came from, with the other matches when it was found by name.
    /// </summary>
    public static (string? name, string from) FamilyName(Document doc, Role role, string? given)
    {
        if (!string.IsNullOrWhiteSpace(given)) return (given!.Trim(), "argument");
        if (ProjectStore.Get(doc, KeyPrefix + role.Key) is { Length: > 0 } kept) return (kept, "remembered in this model");
        var ranked = Candidates(doc, role);
        if (ranked.Count == 0) return (null, "nothing given, remembered or found");
        var others = ranked.Skip(1).Take(5).ToList();
        return (ranked[0], others.Count > 0 ? $"{Found}; other matches: {string.Join(", ", others)} (pass family to use one)" : Found);
    }

    /// <summary>The loaded families that fit a role, best first.</summary>
    public static List<string> Candidates(Document doc, Role role) =>
        ModelRules.RankDetailFamilies(role.Rule, Symbols(doc).Select(s => (s.FamilyName, s.Family.FamilyPlacementType == FamilyPlacementType.CurveBasedDetail)));

    /// <summary>
    /// What to remember for a role after a placement with it worked: the family named in the call or found by name
    /// (nothing when it was already the remembered one). Saved with <see cref="ProjectStore.TryRemember"/>.
    /// </summary>
    public static KeyValuePair<string, string?>? Remembering(Document doc, Role role, FamilySymbol used, string? given)
    {
        var name = string.IsNullOrWhiteSpace(given) ? used.FamilyName : given!.Trim();
        return ProjectStore.Get(doc, KeyPrefix + role.Key) == name ? null : new KeyValuePair<string, string?>(KeyPrefix + role.Key, name);
    }

    /// <summary>Saves what <see cref="Remembering"/> asked for, after the placement is committed; a note when it could not.</summary>
    public static string? SaveRemembered(Document doc, params KeyValuePair<string, string?>?[] entries)
    {
        var list = entries.Where(e => e is not null).Select(e => e!.Value).ToList();
        if (list.Count == 0) return null;
        return ProjectStore.TryRemember(doc, list) is { } why
            ? $"The family was used but not remembered for next time ({why}); pass family again in later calls."
            : null;
    }

    private static List<FamilySymbol> Symbols(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
            .Where(s => s.Category?.Id.Value is (long)BuiltInCategory.OST_DetailComponents or (long)BuiltInCategory.OST_GenericAnnotation)
            .OrderBy(s => s.FamilyName).ThenBy(s => s.Name).ToList();

    /// <summary>The type to place: "Family" (its first type) or "Family: Type"; numbered family copies ("X 1") are accepted.</summary>
    public static FamilySymbol? TryFind(Document doc, Role role, string? given, out string name, out string from, out string problem)
    {
        var (found, source) = FamilyName(doc, role, given);
        from = source;
        name = found ?? "";
        var symbols = Symbols(doc);
        if (found is null)
        {
            problem = $"No {role.What} family was given or remembered in this model, and no loaded detail or annotation family has a name " +
                      $"that looks like one (looked for {string.Join(" + ", role.Rule.Need.Select(g => string.Join("/", g)))}). Load your office's " +
                      "family (load_family with its .rfa, or copy_from_file from a project that has it), or pass family with its name; it is then remembered.";
            return null;
        }
        var wanted = found;
        string? type = null;
        var colon = wanted.IndexOf(':');
        if (colon > 0) { type = wanted.Substring(colon + 1).Trim(); wanted = wanted.Substring(0, colon).Trim(); }
        bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        var family = symbols.Where(s => Same(s.FamilyName, wanted)).ToList();
        if (family.Count == 0) family = symbols.Where(s => Same(DetailTypes.Base(s.FamilyName), DetailTypes.Base(wanted))).ToList();
        var hit = type is null ? family.FirstOrDefault() : family.FirstOrDefault(s => Same(s.Name, type));
        if (hit is not null) { problem = ""; return hit; }

        var similar = Candidates(doc, role).Take(15).ToList();
        problem = family.Count > 0
            ? $"Family '{wanted}' has no type '{type}'. Its types: {string.Join(", ", family.Select(s => s.Name))}."
            : $"The {role.What} family '{wanted}' ({from}) is not loaded in this model. Load it (load_family with its .rfa, or copy_from_file " +
              "from a project that has it), or pass family with the name your office uses. " +
              (similar.Count > 0 ? $"Loaded families that look like one: {string.Join(", ", similar)}." : "No loaded detail or annotation family has a name that looks like one.");
        return null;
    }

    public static FamilySymbol Find(Document doc, Role role, string? given, out string from) =>
        TryFind(doc, role, given, out _, out from, out var problem)
        ?? throw (problem.StartsWith("Family '", StringComparison.Ordinal) // loaded, but no such type: the caller fixes the name
            ? new ArgumentException(problem)
            : ManualSteps.MissingFamily(role.What, problem, () => TryFind(doc, role, given, out _, out _, out _) is not null));

    /// <summary>Outline rings of a slab's largest top face in plan (outer first, then holes), in model coordinates.</summary>
    public static List<IReadOnlyList<(double x, double y)>> SlabRings(Element slab)
    {
        if (slab is not HostObject host) return [];
        Face? top = null;
        foreach (var r in HostObjectUtils.GetTopFaces(host))
            if (slab.GetGeometryObjectFromReference(r) is Face f && (top is null || f.Area > top.Area)) top = f;
        if (top is null) return [];
        return top.GetEdgesAsCurveLoops()
            .Select(l => (IReadOnlyList<(double x, double y)>)l.SelectMany(c => { var t = c.Tessellate(); return t.Take(t.Count - 1); }).Select(p => (p.X, p.Y)).ToList())
            .Where(l => l.Count >= 3)
            .OrderByDescending(l => Math.Abs(Area(l))).ToList();
    }

    private static double Area(IReadOnlyList<(double x, double y)> ring)
    {
        double a = 0;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++) a += (ring[j].x + ring[i].x) * (ring[j].y - ring[i].y);
        return a / 2;
    }

    /// <summary>Common parameters overlaid with an item's own (the item's win).</summary>
    public static List<ParamEdits.Want> Merge(ToolArgs args, JsonElement item, string key = "parameters")
    {
        var common = args.Has(key) ? ParamEdits.FromJson(args.Raw[key]) : [];
        var own = item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var p) ? ParamEdits.FromJson(p) : [];
        return common.Where(c => !own.Any(o => string.Equals(o.Name, c.Name, StringComparison.OrdinalIgnoreCase))).Concat(own).ToList();
    }

    /// <summary>The result rows shared by the placement tools: what was placed, and every parameter written with its outcome.</summary>
    public static object Report(IEnumerable<DetailPlacement.Spec> specs, FamilySymbol symbol, string familyFrom)
    {
        var list = specs.ToList();
        var placed = list.Where(s => s.Created is not null).ToList();
        var parameterNames = list.SelectMany(s => s.Wants.Select(w => w.Name)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var problems = list.SelectMany(s => s.Params.Where(l => !l.Ok).Select(l => new { index = s.Index, l.parameter, l.value, l.status, l.now, l.note })).Take(60).ToList();
        return new
        {
            family = TypeMatch.Label(symbol),
            familyFrom,
            placed = placed.Count,
            ids = placed.Select(s => s.Created!.Value),
            parametersSet = parameterNames,
            parameterProblems = problems.Count > 0 ? problems : null,
            errors = list.Where(s => s.Created is null).Select(s => new { index = s.Index, error = s.Error ?? s.FallbackReason ?? "not placed" }).Take(60),
            copied = placed.Count(s => s.Method == "copy") is var c && c > 0 ? c : (int?)null,
        };
    }

    public static View PlanOrActive(Document doc, ToolArgs args, bool planOnly)
    {
        var view = ViewKit.ViewOrActive(doc, args.OptLong("view_id"));
        if (view.IsTemplate || view is View3D || view is ViewSchedule || view.ViewType == ViewType.DrawingSheet)
            throw new ArgumentException($"Detail items go in plan, section, drafting or detail views, not '{view.Name}' ({view.ViewType}).");
        if (planOnly && view is not ViewPlan) throw new ArgumentException($"'{view.Name}' is not a plan view; slab reinforcement is drawn on plans.");
        return view;
    }
}

public sealed class RftDetailFamiliesTool : IRevitTool
{
    public string Name => "rft_detail_families";
    public string Description =>
        "Check the detail families used for 2D reinforcement drawings (place_additional_reinforcement, place_mesh_symbols, " +
        "place_column_section_details): for each role (additional_bars, mesh, column_section, ties) which family is used " +
        "(given, remembered in this model, or the best match by name among the loaded families, with the other matches), whether it is loaded, its types, whether it is line- or point-based, " +
        "and its parameters (instance or type, formula-driven or not), so you know which names to pass in 'parameters'. " +
        "Missing families are listed with what to do. Opens each family read-only to read its parameters.";
    public string PropertiesJson => """
    {
      "families": {"type": "object", "description": "Optional role -> family name to check instead, e.g. {\"additional_bars\": \"My_Additional_Bars\"}. Roles: additional_bars, mesh, column_section, ties."},
      "parameters": {"type": "boolean", "description": "List each family's parameters (default true)."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var given = args.Has("families") && args.Raw["families"].ValueKind == JsonValueKind.Object
            ? args.Raw["families"].EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString(), StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var unknown = given.Keys.Where(k => Rft2d.All.All(r => r.Key != k)).ToList();
        var roles = new List<object>();
        var missing = new List<string>();
        foreach (var role in Rft2d.All)
        {
            given.TryGetValue(role.Key, out var name);
            var symbol = Rft2d.TryFind(doc, role, name, out var familyName, out var from, out var problem);
            if (symbol is null)
            {
                missing.Add($"{role.Key}: {problem}");
                roles.Add(new { role = role.Key, what = role.What, family = familyName.Length > 0 ? familyName : null, from, loaded = false });
                continue;
            }
            var family = symbol.Family;
            roles.Add(new
            {
                role = role.Key, what = role.What, family = family.Name, from, loaded = true,
                category = symbol.Category?.Name,
                placement = family.FamilyPlacementType == FamilyPlacementType.CurveBasedDetail ? "line-based (start, end)" : "point-based (at)",
                types = family.GetFamilySymbolIds().Select(id => doc.GetElement(id)?.Name).Take(40),
                parameters = args.OptBool("parameters", true) ? Parameters(doc, family) : null,
            });
        }
        return ToolResult.Ok(new
        {
            roles, missing,
            unknownRoles = unknown.Count > 0 ? unknown : null,
            note = "The family a placement tool uses (passed, or found by name) is remembered in this model for its role. Pass parameters by the names listed here; " +
                   "formula-driven and type parameters can't be set per item.",
        });
    }

    private static object Parameters(Document doc, Family family)
    {
        if (!family.IsEditable || doc.IsModifiable) return "could not open the family to read its parameters";
        Document? fdoc = null;
        try
        {
            fdoc = doc.EditFamily(family);
            return fdoc.FamilyManager.Parameters.Cast<FamilyParameter>().Where(p => p.Definition is not null)
                .OrderBy(p => !p.IsInstance).ThenBy(p => p.Definition.Name)
                .Select(p => new
                {
                    name = p.Definition.Name,
                    kind = p.IsInstance ? "instance" : "type",
                    storage = p.StorageType.ToString(),
                    formula = string.IsNullOrEmpty(p.Formula) ? null : p.Formula,
                    reporting = p.IsReporting ? true : (bool?)null,
                }).ToList();
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or InvalidOperationException) { return "could not read: " + ex.Message.Trim(); }
        finally { try { fdoc?.Close(false); } catch (Autodesk.Revit.Exceptions.ApplicationException) { } }
    }
}

public sealed class PlaceAdditionalReinforcementTool : IRevitTool
{
    public string Name => "place_additional_reinforcement";
    public string Description =>
        "Draw additional (top/bottom) reinforcement on a slab plan with the office's 2D bar detail family (role additional_bars: " +
        "a line-based detail item; by default the one remembered in this model, else the loaded one whose name fits best), instead of 3D rebar. Each bar is a line given by start/end, or " +
        "across a slab: slab_id with direction x or y and the coordinate it runs along (default the slab's middle), clipped to the " +
        "slab outline (openings split it; the longest span is used) and shortened by inset at both ends. The bar text values go " +
        "in 'parameters' (common) or per bar, by the family's own parameter names (rft_detail_families lists them); every value is read " +
        "back and the result lists which parameters were set and any that did not take. Bars with the same 'group' are made into one " +
        "detail group of that name. Missing families are reported with what to load.";
    public string PropertiesJson => DependentGuard.AddTo("""
    {
      "view_id": {"type": "integer", "description": "Plan view (default: active view)."},
      "family": {"type": "string", "description": "Family or 'Family: Type' (default: the one remembered in this model, else the best match by name among loaded line-based detail items, such as names with RFT/rebar/bar)."},
      "parameters": {"type": "object", "description": "Instance parameters for every bar, name -> value (e.g. bar text, diameter, spacing)."},
      "bars": {"type": "array", "items": {"type": "object", "properties": {
        "start": {"type": "array", "items": {"type": "number"}},
        "end": {"type": "array", "items": {"type": "number"}},
        "slab_id": {"type": "integer"},
        "direction": {"type": "string", "enum": ["x", "y"], "description": "With slab_id: x runs left-right, y bottom-top."},
        "at": {"type": "number", "description": "With slab_id: the y (direction x) or x (direction y) the bar runs along, project units. Default: the slab's middle."},
        "inset": {"type": "number", "description": "Shorten both ends by this, project units (default 0)."},
        "parameters": {"type": "object"},
        "group": {"type": "string", "description": "Detail group name; bars with the same name are grouped."}
      }}},
      "method": {"type": "string", "enum": ["auto", "new", "copy"], "description": "auto (default): new item, copying an existing one when Revit refuses."}
    }
    """);
    public string[] Required => ["bars"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var view = Rft2d.PlanOrActive(doc, args, planOnly: false);
        var guard = DependentGuard.For(doc, view, args.OptString("dependent_view"));
        var given = args.OptString("family");
        var symbol = Rft2d.Find(doc, Rft2d.AdditionalBars, given, out var from);
        if (symbol.Family.FamilyPlacementType != FamilyPlacementType.CurveBasedDetail)
            return ToolResult.Error($"'{symbol.FamilyName}' is not a line-based detail family, so it can't be drawn along a bar. Pass the line-based family with family.");
        var method = args.OptString("method") ?? "auto";
        var bars = args.Array("bars").ToList();
        if (bars.Count == 0) return ToolResult.Error("Give at least one bar.");
        if (bars.Count > DetailPlacement.MaxPerCall) return ToolResult.Error($"{bars.Count} bars in one call; the limit is {DetailPlacement.MaxPerCall}.");
        var z = (view as ViewPlan)?.GenLevel?.ProjectElevation ?? 0;

        var specs = new List<DetailPlacement.Spec>();
        var groups = new Dictionary<int, string>();
        for (var i = 0; i < bars.Count; i++)
        {
            var b = bars[i];
            var spec = new DetailPlacement.Spec { Index = i, FamilyName = symbol.FamilyName, Symbol = symbol, Wants = Rft2d.Merge(args, b) };
            try
            {
                if (b.TryGetProperty("start", out var st) && b.TryGetProperty("end", out var en))
                {
                    spec.Start = ViewPoints.ToModel(doc, view, st);
                    spec.End = ViewPoints.ToModel(doc, view, en);
                }
                else if (b.TryGetProperty("slab_id", out var sid))
                {
                    if (view is not ViewPlan) throw new ArgumentException("slab_id bars need a plan view.");
                    var slab = doc.GetElement(new ElementId(sid.GetInt64())) ?? throw new ArgumentException($"{sid.GetInt64()} is not an element.");
                    var rings = Rft2d.SlabRings(slab);
                    if (rings.Count == 0) throw new ArgumentException($"{slab.Id.Value} has no top face to read an outline from (is it a floor?).");
                    var horizontal = (b.TryGetProperty("direction", out var d) ? d.GetString() : "x") != "y";
                    var outer = rings[0];
                    var along = b.TryGetProperty("at", out var a) && a.ValueKind == JsonValueKind.Number
                        ? RevitHelpers.ToInternal(doc, a.GetDouble())
                        : horizontal ? (outer.Min(p => p.y) + outer.Max(p => p.y)) / 2 : (outer.Min(p => p.x) + outer.Max(p => p.x)) / 2;
                    var spans = LayoutMath.LineSpans(rings, horizontal, along);
                    if (spans.Count == 0) throw new ArgumentException($"A bar along {(horizontal ? "y" : "x")} = {RevitHelpers.FromInternal(doc, along)} does not cross slab {slab.Id.Value}.");
                    var inset = b.TryGetProperty("inset", out var ins) && ins.ValueKind == JsonValueKind.Number ? RevitHelpers.ToInternal(doc, ins.GetDouble()) : 0;
                    var (lo, hi) = (spans[0].from + inset, spans[0].to - inset);
                    if (hi - lo < 1e-3) throw new ArgumentException("The inset leaves nothing of the bar.");
                    spec.Start = horizontal ? new XYZ(lo, along, z) : new XYZ(along, lo, z);
                    spec.End = horizontal ? new XYZ(hi, along, z) : new XYZ(along, hi, z);
                }
                else throw new ArgumentException("Give start and end, or slab_id (with direction and at).");
                if (!guard.Keep((spec.Start + spec.End) / 2)) throw new ArgumentException("outside this dependent view's crop");
                if (b.TryGetProperty("group", out var g) && g.GetString() is { Length: > 0 } gn) groups[i] = gn;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException or Autodesk.Revit.Exceptions.ApplicationException)
            {
                spec.Error = ex.Message.Trim();
            }
            specs.Add(spec);
        }

        var grouped = new List<object>();
        var target = guard.Target;
        Batch.InGroup(doc, "Place additional reinforcement", () =>
        {
            DetailPlacement.Run(doc, target, specs, method, null);
            using var t = new Transaction(doc, "Group additional reinforcement");
            t.Start();
            foreach (var byName in groups.Where(kv => specs[kv.Key].Created is not null).GroupBy(kv => kv.Value))
            {
                try
                {
                    var g = doc.Create.NewGroup(byName.Select(kv => specs[kv.Key].Created!).ToList());
                    try { g.GroupType.Name = byName.Key; } catch (Autodesk.Revit.Exceptions.ArgumentException) { }
                    grouped.Add(new { group = g.GroupType.Name, id = g.Id.Value, bars = byName.Count() });
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex) { grouped.Add(new { group = byName.Key, error = ex.Message.Trim() }); }
            }
            t.Commit();
            return specs;
        }, s => s.Any(x => x.Created is not null));
        var remembered = specs.Any(s => s.Created is not null) ? Rft2d.SaveRemembered(doc, Rft2d.Remembering(doc, Rft2d.AdditionalBars, symbol, given)) : null;

        if (specs.All(s => s.Created is null))
            return ToolResult.Error($"No bars were placed. {JsonSerializer.Serialize(specs.Select(s => new { index = s.Index, error = s.Error ?? s.FallbackReason }).Take(10))}");
        return ToolResult.Ok(new { view = view.Name, result = Rft2d.Report(specs, symbol, from), groups = grouped.Count > 0 ? grouped : null, note = remembered, dependentView = guard.Report() });
    }
}

public sealed class PlaceMeshSymbolsTool : IRevitTool
{
    public string Name => "place_mesh_symbols";
    public string Description =>
        "Put the office's mesh symbol (role mesh: a point-based detail item or generic annotation; by default the one remembered in this model, else the loaded one with 'mesh' in its name) on " +
        "each slab in a plan: at a point well inside the slab's own outline, kept apart from other mesh symbols. Slabs default to " +
        "every floor shown in the view; slabs that already show this symbol are skipped. The mesh text values go in 'parameters' " +
        "(common) or per slab, by the family's own parameter names (rft_detail_families lists them); the result lists which " +
        "parameters were set and any that did not take. Missing families are reported with what to load.";
    public string PropertiesJson => DependentGuard.AddTo("""
    {
      "view_id": {"type": "integer", "description": "Plan view (default: active view)."},
      "family": {"type": "string", "description": "Family or 'Family: Type' (default: the one remembered in this model, else the best match by name, such as a family with 'mesh' in its name)."},
      "slab_ids": {"type": "array", "items": {"type": "integer"}, "description": "Default: every floor shown in the view."},
      "parameters": {"type": "object", "description": "Instance parameters for every symbol, name -> value."},
      "slabs": {"type": "array", "items": {"type": "object", "properties": {
        "slab_id": {"type": "integer"},
        "at": {"type": "array", "items": {"type": "number"}},
        "rotation_degrees": {"type": "number"},
        "parameters": {"type": "object"}
      }, "required": ["slab_id"]}, "description": "Per-slab position or values (also adds the slab)."},
      "rotation_degrees": {"type": "number"},
      "skip_existing": {"type": "boolean", "description": "Skip slabs that already show this family's symbol (default true)."}
    }
    """);
    public string[] Required => [];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var view = Rft2d.PlanOrActive(doc, args, planOnly: true);
        var guard = DependentGuard.For(doc, view, args.OptString("dependent_view"));
        var given = args.OptString("family");
        var symbol = Rft2d.Find(doc, Rft2d.Mesh, given, out var from);
        if (symbol.Family.FamilyPlacementType == FamilyPlacementType.CurveBasedDetail)
            return ToolResult.Error($"'{symbol.FamilyName}' is line-based; a mesh symbol is placed at a point. Pass the point-based family with family.");
        var z = (view as ViewPlan)?.GenLevel?.ProjectElevation ?? 0;

        var perSlab = args.Array("slabs").Where(s => s.TryGetProperty("slab_id", out _)).ToDictionary(s => s.GetProperty("slab_id").GetInt64(), s => s);
        var ids = args.LongList("slab_ids");
        if (ids.Count == 0 && perSlab.Count == 0)
            ids = new FilteredElementCollector(doc, view.Id).OfCategory(BuiltInCategory.OST_Floors).WhereElementIsNotElementType().Select(e => e.Id.Value).ToList();
        ids = ids.Concat(perSlab.Keys).Distinct().ToList();
        if (ids.Count == 0) return ToolResult.Error($"No floors are shown in '{view.Name}'; pass slab_ids.");

        // Symbols of this family already in the view: their slabs are skipped, and new ones keep clear of them.
        var existing = new FilteredElementCollector(doc, view.Id).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
            .Where(fi => fi.Symbol.Family.Id == symbol.Family.Id).Select(fi => (fi.Location as LocationPoint)?.Point).OfType<XYZ>().ToList();
        var clear = 20 / 304.8 * Math.Max(1, view.Scale); // 20 mm on paper between symbols
        var taken = existing.Select(p => (p.X, p.Y)).ToList();
        var specs = new List<DetailPlacement.Spec>();
        var slabOf = new Dictionary<int, long>();
        var skipped = new List<object>();
        foreach (var id in ids)
        {
            perSlab.TryGetValue(id, out var own);
            var slab = doc.GetElement(new ElementId(id));
            if (slab is null) { skipped.Add(new { slab_id = id, reason = "not found" }); continue; }
            var rings = Rft2d.SlabRings(slab);
            if (rings.Count == 0) { skipped.Add(new { slab_id = id, reason = "no top face (is it a floor?)" }); continue; }
            var holes = rings.Skip(1).ToList();
            bool OnSlab(double x, double y) => LayoutMath.InPolygon(rings[0], x, y) && !holes.Any(h => LayoutMath.InPolygon(h, x, y));
            if (args.OptBool("skip_existing", true) && own.ValueKind != JsonValueKind.Object && existing.Any(p => OnSlab(p.X, p.Y)))
            {
                skipped.Add(new { slab_id = id, reason = "already has this mesh symbol" });
                continue;
            }
            XYZ point;
            if (own.ValueKind == JsonValueKind.Object && own.TryGetProperty("at", out var at)) point = ViewPoints.ToModel(doc, view, at);
            else
            {
                var spots = LayoutMath.InteriorPoints(rings[0], holes);
                if (spots.Count == 0) { skipped.Add(new { slab_id = id, reason = "too small or narrow to find a point inside" }); continue; }
                var free = spots.Where(p => taken.All(q => Math.Abs(q.X - p.x) > clear || Math.Abs(q.Y - p.y) > clear)).ToList();
                var pick = free.Count > 0 ? free[0] : spots[0];
                point = new XYZ(pick.x, pick.y, z);
            }
            if (!guard.Keep(point)) { skipped.Add(new { slab_id = id, reason = "outside this dependent view's crop" }); continue; }
            taken.Add((point.X, point.Y));
            var spec = new DetailPlacement.Spec
            {
                Index = specs.Count, FamilyName = symbol.FamilyName, Symbol = symbol, Wants = Rft2d.Merge(args, own), At = point,
                RotationDeg = own.ValueKind == JsonValueKind.Object && own.TryGetProperty("rotation_degrees", out var r) && r.ValueKind == JsonValueKind.Number
                    ? r.GetDouble() : args.OptDouble("rotation_degrees"),
            };
            slabOf[spec.Index] = id;
            specs.Add(spec);
        }
        if (specs.Count == 0) return ToolResult.Error($"No mesh symbols to place: {JsonSerializer.Serialize(skipped.Take(10))}");

        // Generic annotations have no copy fallback (that path copies detail items), so they are always placed new.
        var method = symbol.Category?.Id.Value == (long)BuiltInCategory.OST_DetailComponents ? "auto" : "new";
        var target = guard.Target;
        Batch.InGroup(doc, "Place mesh symbols", () =>
        {
            DetailPlacement.Run(doc, target, specs, method, null);
            return specs;
        }, s => s.Any(x => x.Created is not null));
        var remembered = specs.Any(s => s.Created is not null) ? Rft2d.SaveRemembered(doc, Rft2d.Remembering(doc, Rft2d.Mesh, symbol, given)) : null;

        if (specs.All(s => s.Created is null))
            return ToolResult.Error($"No mesh symbols were placed. {JsonSerializer.Serialize(specs.Select(s => new { index = s.Index, error = s.Error ?? s.FallbackReason }).Take(10))}");
        return ToolResult.Ok(new
        {
            view = view.Name, result = Rft2d.Report(specs, symbol, from), skipped, note = remembered,
            slabs = specs.Where(sp => sp.Created is not null).Select(sp => new { slab_id = slabOf[sp.Index], symbol_id = sp.Created!.Value }).Take(200),
            dependentView = guard.Report(),
        });
    }
}

public sealed class PlaceColumnSectionDetailsTool : IRevitTool
{
    public string Name => "place_column_section_details";
    public string Description =>
        "Fill column schedule cells (in a drafting, legend, section or plan view) with the office's column section reinforcement " +
        "detail (role column_section; by default the one remembered in this model, else the loaded detail item whose name has column and " +
        "RFT/rebar), optionally with a transversal bar / link item (role ties; else a name with tie/stirrup/transversal) at the same point, and the column mark written below. Cells are placed at 'at', " +
        "or on a grid (origin, spacing, per_row). With column_id, the column's section size is written to width_parameter / " +
        "depth_parameter (the family's own names) and its Mark becomes the label. Bar values go in 'parameters' / 'ties_parameters' " +
        "(common) or per cell, by the family's own parameter names (rft_detail_families lists them); the result lists which " +
        "parameters were set and any that did not take. Missing families are reported with what to load.";
    public string PropertiesJson => DependentGuard.AddTo("""
    {
      "view_id": {"type": "integer", "description": "Default: active view."},
      "family": {"type": "string", "description": "Section family or 'Family: Type' (default: remembered in this model, else the best match by name among loaded detail items)."},
      "parameters": {"type": "object", "description": "Section item parameters for every cell, name -> value."},
      "ties": {"type": "boolean", "description": "Also place the ties family in each cell (default false)."},
      "ties_family": {"type": "string", "description": "Default: remembered in this model, else the best match by name (tie, stirrup, transversal)."},
      "ties_parameters": {"type": "object"},
      "width_parameter": {"type": "string", "description": "Section family parameter that takes the column's width (b) when a cell has column_id."},
      "depth_parameter": {"type": "string", "description": "Section family parameter that takes the column's depth (h)."},
      "cells": {"type": "array", "items": {"type": "object", "properties": {
        "at": {"type": "array", "items": {"type": "number"}},
        "column_id": {"type": "integer"},
        "label": {"type": "string", "description": "Text under the cell (default: the column's Mark)."},
        "parameters": {"type": "object"},
        "ties_parameters": {"type": "object"}
      }}},
      "grid": {"type": "object", "properties": {
        "origin": {"type": "array", "items": {"type": "number"}},
        "spacing": {"type": "array", "items": {"type": "number"}, "description": "[dx, dy] between cell centres, project units (dy downward)."},
        "per_row": {"type": "integer"}
      }, "description": "For cells without 'at'."},
      "label_offset": {"type": "number", "description": "How far below the cell point the label goes, project units (default: 0.45 x the grid's dy, else 60 mm on paper)."}
    }
    """);
    public string[] Required => ["cells"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var view = Rft2d.PlanOrActive(doc, args, planOnly: false);
        var guard = DependentGuard.For(doc, view, args.OptString("dependent_view"));
        var given = args.OptString("family");
        var symbol = Rft2d.Find(doc, Rft2d.ColumnSection, given, out var from);
        var withTies = args.OptBool("ties") || args.Has("ties_family");
        var tiesGiven = args.OptString("ties_family");
        var tiesFrom = "";
        FamilySymbol? tiesSymbol = withTies ? Rft2d.Find(doc, Rft2d.Ties, tiesGiven, out tiesFrom) : null;
        var cells = args.Array("cells").ToList();
        if (cells.Count == 0) return ToolResult.Error("Give at least one cell.");
        if (cells.Count * (withTies ? 2 : 1) > DetailPlacement.MaxPerCall) return ToolResult.Error($"Too many cells for one call (limit {DetailPlacement.MaxPerCall} items).");

        XYZ? origin = null; double dx = 0, dy = 0; var perRow = 1;
        if (args.Has("grid"))
        {
            var g = args.Raw["grid"];
            origin = g.TryGetProperty("origin", out var o) ? ViewPoints.ToModel(doc, view, o) : ViewPoints.ToModel(doc, view, JsonDocument.Parse("[0,0]").RootElement);
            if (g.TryGetProperty("spacing", out var sp) && sp.GetArrayLength() >= 2)
            {
                dx = RevitHelpers.ToInternal(doc, sp[0].GetDouble());
                dy = RevitHelpers.ToInternal(doc, sp[1].GetDouble());
            }
            perRow = g.TryGetProperty("per_row", out var pr) ? Math.Max(1, pr.GetInt32()) : Math.Max(1, cells.Count);
        }
        var labelOffset = args.OptDouble("label_offset") is { } lo ? RevitHelpers.ToInternal(doc, lo)
            : Math.Abs(dy) > 0 ? 0.45 * Math.Abs(dy) : 60 / 304.8 * Math.Max(1, view.Scale);
        var widthParam = args.OptString("width_parameter");
        var depthParam = args.OptString("depth_parameter");
        string Len(double ft) => RevitHelpers.FromInternal(doc, ft).ToString(CultureInfo.InvariantCulture);

        var sections = new List<DetailPlacement.Spec>();
        var ties = new List<DetailPlacement.Spec>();
        var labels = new List<(XYZ at, string text)>();
        var notes = new List<string>();
        for (var i = 0; i < cells.Count; i++)
        {
            var c = cells[i];
            var spec = new DetailPlacement.Spec { Index = i, FamilyName = symbol.FamilyName, Symbol = symbol, Wants = Rft2d.Merge(args, c) };
            try
            {
                if (c.TryGetProperty("at", out var at)) spec.At = ViewPoints.ToModel(doc, view, at);
                else if (origin is not null) spec.At = origin + view.RightDirection * (dx * (i % perRow)) - view.UpDirection * (dy * (i / perRow));
                else throw new ArgumentException("Give 'at' for the cell, or a grid.");
                string? label = c.TryGetProperty("label", out var lb) ? lb.GetString() : null;
                if (c.TryGetProperty("column_id", out var cid))
                {
                    var column = doc.GetElement(new ElementId(cid.GetInt64())) as FamilyInstance ?? throw new ArgumentException($"{cid.GetInt64()} is not a column.");
                    label ??= column.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString();
                    if (widthParam is not null || depthParam is not null)
                    {
                        var (b, h) = SectionSize(column);
                        if (widthParam is not null && !spec.Wants.Any(w => string.Equals(w.Name, widthParam, StringComparison.OrdinalIgnoreCase))) spec.Wants.Add(new ParamEdits.Want(widthParam, Len(b)));
                        if (depthParam is not null && !spec.Wants.Any(w => string.Equals(w.Name, depthParam, StringComparison.OrdinalIgnoreCase))) spec.Wants.Add(new ParamEdits.Want(depthParam, Len(h)));
                    }
                    else if (i == 0) notes.Add("column_id gives the label only: pass width_parameter and depth_parameter to also write the column size into the section.");
                }
                if (!guard.Keep(spec.At)) throw new ArgumentException("outside this dependent view's crop");
                if (!string.IsNullOrWhiteSpace(label)) labels.Add((spec.At - view.UpDirection * labelOffset, label!));
                if (tiesSymbol is not null)
                    ties.Add(new DetailPlacement.Spec
                    {
                        Index = i, FamilyName = tiesSymbol.FamilyName, Symbol = tiesSymbol, At = spec.At,
                        Start = tiesSymbol.Family.FamilyPlacementType == FamilyPlacementType.CurveBasedDetail ? spec.At : null,
                        End = tiesSymbol.Family.FamilyPlacementType == FamilyPlacementType.CurveBasedDetail ? spec.At + view.RightDirection * Math.Max(dx * 0.6, 300 / 304.8) : null,
                        Wants = Rft2d.Merge(args, c, "ties_parameters"),
                    });
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException or Autodesk.Revit.Exceptions.ApplicationException)
            {
                spec.Error = ex.Message.Trim();
            }
            sections.Add(spec);
        }

        var target = guard.Target;
        var labelled = 0;
        Batch.InGroup(doc, "Place column section details", () =>
        {
            DetailPlacement.Run(doc, target, sections, "auto", null);
            if (ties.Count > 0) DetailPlacement.Run(doc, target, ties, "auto", null);
            using var t = new Transaction(doc, "Label column sections");
            t.Start();
            var textType = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
            foreach (var (at, text) in labels)
            {
                try
                {
                    TextNote.Create(doc, target.Id, at, text, new TextNoteOptions(textType) { HorizontalAlignment = HorizontalTextAlignment.Center });
                    labelled++;
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException) { }
            }
            t.Commit();
            return sections;
        }, s => s.Any(x => x.Created is not null));
        var remembered = Rft2d.SaveRemembered(doc,
            sections.Any(s => s.Created is not null) ? Rft2d.Remembering(doc, Rft2d.ColumnSection, symbol, given) : null,
            tiesSymbol is not null && ties.Any(s => s.Created is not null) ? Rft2d.Remembering(doc, Rft2d.Ties, tiesSymbol, tiesGiven) : null);
        if (remembered is not null) notes.Add(remembered);

        if (sections.All(s => s.Created is null))
            return ToolResult.Error($"No column sections were placed. {JsonSerializer.Serialize(sections.Select(s => new { index = s.Index, error = s.Error ?? s.FallbackReason }).Take(10))}");
        return ToolResult.Ok(new
        {
            view = view.Name,
            sections = Rft2d.Report(sections, symbol, from),
            ties = tiesSymbol is null ? null : Rft2d.Report(ties, tiesSymbol, tiesFrom),
            labels = labelled,
            notes = notes.Count > 0 ? notes : null,
            dependentView = guard.Report(),
        });
    }

    /// <summary>A column's section width and depth: from its type's b/h-like parameters, else its plan extents along its own axes.</summary>
    private static (double b, double h) SectionSize(FamilyInstance column)
    {
        double? Read(params string[] names)
        {
            foreach (var n in names)
            {
                var p = column.Symbol.LookupParameter(n) ?? column.LookupParameter(n);
                if (p is { StorageType: StorageType.Double } && p.AsDouble() > 0) return p.AsDouble();
            }
            return null;
        }
        var b = Read("b", "Width", "B", "Column Width", "bf");
        var h = Read("h", "Depth", "H", "Column Depth", "d");
        if (b is not null && h is not null) return (b.Value, h.Value);
        var bb = column.get_BoundingBox(null);
        if (bb is null) return (b ?? 0, h ?? 0);
        XYZ ax = column.HandOrientation, ay = column.FacingOrientation;
        var corners = new[] { bb.Min, new XYZ(bb.Max.X, bb.Min.Y, 0), new XYZ(bb.Min.X, bb.Max.Y, 0), bb.Max };
        double Span(XYZ dir) => corners.Max(p => p.X * dir.X + p.Y * dir.Y) - corners.Min(p => p.X * dir.X + p.Y * dir.Y);
        return (b ?? Span(ax), h ?? Span(ay));
    }
}
