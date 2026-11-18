using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

/// <summary>
/// model_from_sections: a structure built from its section drawings, each stretch of the alignment with its own
/// section (as the drawings give them, never one typical section copied everywhere), each part an outline read from the
/// section (offset from the face line, level), gravity block courses with their setbacks, piles in rows, and the parts'
/// levels compared with a reference model (the new works it connects to). Made for the existing quay of a port from
/// its as-built drawings (2026-10-30: the existing model had one section for four berths, flat block layers, and a
/// bullnose deck that was guessed), and for any wall, revetment or deck that changes section along its length.
/// </summary>
public sealed class ModelFromSectionsTool : IRevitTool
{
    public string Name => "model_from_sections";
    public string Description =>
        "Model a structure from its section drawings along a plan alignment (quay wall, gravity block wall, deck on piles, " +
        "revetment, coping, existing structures from as-built drawings). Walk the alignment along the face line with the land " +
        "on your right: offsets are positive towards the land, negative towards the sea. Give every section the drawings " +
        "show (parts as outlines [offset, level] read from the section, or gravity block courses with height, width and " +
        "setback; piles in rows) and zones saying which section each stretch takes (chainages from the first alignment " +
        "point). Each part becomes its own element, named and marked per section, with the drawing it came from in Comments. " +
        "dry_run checks everything (gaps between zones, outlines, levels against the reference) without changing the model; " +
        "zones_only builds just those zones first. reference compares part levels with the model they connect to.";
    public string PropertiesJson => """
    {
      "alignment": {"type": "array", "items": {"type": "array", "items": {"type": "number"}}, "description": "Plan points [x, y] along the face line (cope line), land on the right."},
      "coordinates": {"type": "string", "enum": ["project", "shared"], "description": "Alignment points in project coordinates (default) or shared (survey: east, north)."},
      "sections": {"type": "array", "items": {"type": "object"}, "description": "[{name, source (drawing, page, section title), parts: [{name, category (walls|floors|framing|foundation|columns|generic), points [[offset, level], ...] in order around the outline OR courses [{height, width, setback}] bottom up with base_level, joint_length (block joints along the wall), match (top|bottom|both: compare with the reference), assumed (true only for what no drawing gives), note}], piles: [{name, offsets [...], spacing, start (from zone start), diameter or size [w, d], top_level, toe_level, category, match}]}]"},
      "zones": {"type": "array", "items": {"type": "object"}, "description": "[{section, from, to, name}]: chainages along the alignment; zones must not overlap. Stretches no zone covers are listed."},
      "phase": {"type": "string", "description": "Phase created for every element (e.g. \"Existing\")."},
      "prefix": {"type": "string", "description": "Mark prefix (default EX)."},
      "zones_only": {"type": "array", "items": {"type": "integer"}, "description": "Build only these zones (indices from 0 in zones), to show one first."},
      "dry_run": {"type": "boolean", "description": "Check and report only; nothing is made."},
      "reference": {"type": "object", "description": "{categories: [\"Floors\", \"Structural Framing\", ...], link: part of a linked model's name (leave out for this model), tolerance}: the reference levels found in each zone, and every part with match compared with them."}
    }
    """;
    public string[] Required => ["alignment", "sections", "zones"];
    public bool ModifiesModel => true;

    private sealed record PartSpec(string Name, BuiltInCategory Category, List<List<(double u, double v)>> Outlines, double JointLength, string? Match, bool Assumed, string? Note);
    private sealed record PileSpec(string Name, List<double> Offsets, double Spacing, double Start, double Diameter, double Width, double Depth, double Top, double Toe, BuiltInCategory Category, string? Match);
    private sealed record SectionSpec(string Name, string Source, List<PartSpec> Parts, List<PileSpec> Piles);

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var baseZ = Elevations.BaseZ(doc);
        double In(double v) => RevitHelpers.ToInternal(doc, v);
        double Lvl(double v) => baseZ + In(v);
        double Out(double feet) => RevitHelpers.FromInternal(doc, feet);
        double OutLvl(double z) => RevitHelpers.FromInternal(doc, z - baseZ);
        string Show(double feet) => Out(feet).ToString("0.###", CultureInfo.InvariantCulture);

        // Alignment, in internal coordinates.
        var shared = string.Equals(args.OptString("coordinates"), "shared", StringComparison.OrdinalIgnoreCase);
        var toInternal = shared ? Coordinates.InternalToShared(doc).Inverse : Transform.Identity;
        var alignment = args.Raw["alignment"].EnumerateArray().Select(p => toInternal.OfPoint(Modeling.Point(doc, p))).Select(p => new XYZ(p.X, p.Y, 0)).ToList();
        if (alignment.Count < 2) return ToolResult.Error("alignment needs at least two points.");
        var length = Shapes.Length(alignment);
        if (length < Shapes.Mm(100)) return ToolResult.Error("The alignment has no length.");

        // Sections.
        var sections = new Dictionary<string, SectionSpec>(StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();
        foreach (var s in args.Raw["sections"].EnumerateArray())
        {
            var name = Str(s, "name") ?? $"S{sections.Count + 1}";
            var parts = new List<PartSpec>(); var piles = new List<PileSpec>();
            if (s.TryGetProperty("parts", out var pj))
                foreach (var p in pj.EnumerateArray())
                {
                    var pname = Str(p, "name") ?? $"Part {parts.Count + 1}";
                    var outlines = new List<List<(double u, double v)>>();
                    if (p.TryGetProperty("points", out var pts))
                        outlines.Add(AsBuiltMath.Ordered(pts.EnumerateArray().Select(x => (In(x[0].GetDouble()), Lvl(x[1].GetDouble()))).ToList()));
                    else if (p.TryGetProperty("courses", out var cj))
                    {
                        if (!p.TryGetProperty("base_level", out var bl)) { problems.Add($"Section {name}, {pname}: courses need base_level."); continue; }
                        var courses = cj.EnumerateArray().Select(c => new AsBuiltMath.Course(In(Num(c, "height") ?? 0), In(Num(c, "width") ?? 0), In(Num(c, "setback") ?? 0))).ToList();
                        try { outlines.AddRange(AsBuiltMath.Courses(Lvl(bl.GetDouble()), courses)); }
                        catch (ArgumentException ex) { problems.Add($"Section {name}, {pname}: {ex.Message}"); continue; }
                    }
                    else { problems.Add($"Section {name}, {pname}: give points (its outline) or courses."); continue; }
                    for (var i = 0; i < outlines.Count; i++)
                        if (AsBuiltMath.ProfileProblem(outlines[i]) is { } why) problems.Add($"Section {name}, {pname}{(outlines.Count > 1 ? $" course {i + 1}" : "")}: the outline {why}.");
                    parts.Add(new PartSpec(pname, Category(Str(p, "category"), BuiltInCategory.OST_GenericModel), outlines,
                        Num(p, "joint_length") is { } jl ? In(jl) : 0, Str(p, "match"), p.TryGetProperty("assumed", out var a) && a.ValueKind == JsonValueKind.True, Str(p, "note")));
                }
            if (s.TryGetProperty("piles", out var rj))
                foreach (var r in rj.EnumerateArray())
                {
                    var rname = Str(r, "name") ?? $"Piles {piles.Count + 1}";
                    var offsets = r.TryGetProperty("offsets", out var oj) ? oj.EnumerateArray().Select(x => In(x.GetDouble())).ToList() : [];
                    double? size0 = null, size1 = null;
                    if (r.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Array && sz.GetArrayLength() == 2) { size0 = In(sz[0].GetDouble()); size1 = In(sz[1].GetDouble()); }
                    var dia = Num(r, "diameter") is { } d ? In(d) : 0;
                    if (offsets.Count == 0 || (Num(r, "spacing") ?? 0) <= 0 || (dia <= 0 && size0 is null) || Num(r, "top_level") is null || Num(r, "toe_level") is null)
                    { problems.Add($"Section {name}, {rname}: piles need offsets, spacing, diameter (or size), top_level and toe_level."); continue; }
                    var top = Lvl(Num(r, "top_level")!.Value); var toe = Lvl(Num(r, "toe_level")!.Value);
                    if (toe >= top) { problems.Add($"Section {name}, {rname}: toe_level must be below top_level."); continue; }
                    piles.Add(new PileSpec(rname, offsets, In(Num(r, "spacing")!.Value), In(Num(r, "start") ?? 0), dia, size0 ?? 0, size1 ?? 0, top, toe,
                        Category(Str(r, "category"), BuiltInCategory.OST_StructuralColumns), Str(r, "match")));
                }
            if (parts.Count == 0 && piles.Count == 0) problems.Add($"Section {name} has no parts and no piles.");
            sections[name] = new SectionSpec(name, Str(s, "source") ?? "", parts, piles);
        }

        // Zones.
        // Checked in project units (the numbers as given), built in internal units; a zone is named by what was given.
        var given = args.Raw["zones"].EnumerateArray().Select(z =>
        {
            double f = Num(z, "from") ?? 0, to = Num(z, "to") ?? 0; var sec = Str(z, "section") ?? "";
            return new AsBuiltMath.Zone(sec, f, to, Str(z, "name") ?? $"{sec} {f.ToString("0.###", CultureInfo.InvariantCulture)}-{to.ToString("0.###", CultureInfo.InvariantCulture)}");
        }).ToList();
        var (zoneErrors, gapText) = AsBuiltMath.CheckZones(given, new HashSet<string>(sections.Keys, StringComparer.OrdinalIgnoreCase), Out(length), Out(Shapes.Mm(1)));
        problems.AddRange(zoneErrors);
        var zones = given.Select(z => z with { From = In(z.From), To = In(z.To) }).ToList();
        if (problems.Count > 0)
            return ToolResult.Error("Nothing was made. Fix these first:\n- " + string.Join("\n- ", problems) +
                                    (gapText.Count > 0 ? "\nStretches no zone covers: " + string.Join("; ", gapText) : ""));

        // Which zones to build.
        var only = args.Has("zones_only") ? new HashSet<int>(args.Raw["zones_only"].EnumerateArray().Select(x => x.GetInt32())) : null;
        if (only != null && only.Any(i => i < 0 || i >= zones.Count)) return ToolResult.Error($"zones_only: zones are numbered 0 to {zones.Count - 1}.");
        var dry = args.OptBool("dry_run");
        var prefix = args.OptString("prefix") is { Length: > 0 } px ? px : "EX";

        // Levels of every part per zone.
        var partLevels = new List<AsBuiltMath.PartLevels>();
        for (var i = 0; i < zones.Count; i++)
        {
            var sec = sections[zones[i].Section];
            foreach (var p in sec.Parts)
            {
                var all = p.Outlines.SelectMany(o => o).ToList();
                partLevels.Add(new AsBuiltMath.PartLevels(zones[i].Label, p.Name, all.Max(x => x.v), all.Min(x => x.v), p.Match));
            }
            foreach (var r in sec.Piles) partLevels.Add(new AsBuiltMath.PartLevels(zones[i].Label, r.Name, r.Top, r.Toe, r.Match));
        }

        // The reference model's levels per zone.
        List<string> mismatches = []; object? referenceReport = null; string? referenceNote = null;
        if (args.Has("reference"))
        {
            var rf = args.Raw["reference"];
            var tol = rf.TryGetProperty("tolerance", out var tj) && tj.ValueKind == JsonValueKind.Number ? In(tj.GetDouble()) : Shapes.Mm(10);
            var cats = rf.TryGetProperty("categories", out var cj) ? cj.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList() : ["Floors", "Structural Framing"];
            var refs = ReferenceLevels(doc, Str(rf, "link"), cats, alignment, zones, sections, out referenceNote);
            // Compared as elevations in project units, so the lines read like the drawings.
            var shownParts = partLevels.Select(p => p with { Top = OutLvl(p.Top), Bottom = OutLvl(p.Bottom) }).ToList();
            var shownRefs = refs.Select(r => r with { Top = OutLvl(r.Top), Bottom = OutLvl(r.Bottom) }).ToList();
            mismatches = AsBuiltMath.LevelMismatches(shownParts, shownRefs, Out(tol) + 1e-9, v => v.ToString("0.###", CultureInfo.InvariantCulture));
            referenceReport = zones.Select(z => new
            {
                zone = z.Label,
                found = refs.Where(r => r.Zone == z.Label).GroupBy(r => r.Category).Select(g => new
                {
                    category = g.Key, count = g.Count(),
                    tops = AsBuiltMath.Distinct(g.Select(r => OutLvl(r.Top)), Out(Shapes.Mm(5))).Take(6),
                    bottoms = AsBuiltMath.Distinct(g.Select(r => OutLvl(r.Bottom)), Out(Shapes.Mm(5))).Take(6),
                }),
            }).ToList();
        }

        var levelsReport = partLevels.Select(p => new { zone = p.Zone, part = p.Part, top = Math.Round(OutLvl(p.Top), 3), bottom = Math.Round(OutLvl(p.Bottom), 3), match = p.Match }).ToList();
        var assumed = sections.Values.SelectMany(s => s.Parts.Where(p => p.Assumed).Select(p => $"{s.Name} {p.Name}")).ToList();

        if (dry)
            return ToolResult.Ok(new
            {
                dryRun = true, alignmentLength = Math.Round(Out(length), 3), zones = zones.Count, sections = sections.Count,
                notCovered = gapText, assumedParts = assumed, levels = levelsReport, reference = referenceReport, referenceNote, levelDifferences = mismatches,
                next = "Nothing was made. If the levels and zones are right, call again without dry_run (zones_only: [0] to show one zone first).",
            });

        // Build.
        Phase? phase = null;
        if (args.OptString("phase") is { Length: > 0 } phaseName)
        {
            phase = doc.Phases.Cast<Phase>().FirstOrDefault(ph => ph.Name.Equals(phaseName, StringComparison.OrdinalIgnoreCase));
            if (phase is null) return ToolResult.Error($"There is no phase \"{phaseName}\". Phases: {string.Join(", ", doc.Phases.Cast<Phase>().Select(ph => ph.Name))}.");
        }
        var made = new List<object>(); var failed = new List<string>();
        var stopped = false; var built = 0;
        using var t = RevitHelpers.StartTransaction(doc, "Model from sections");
        for (var i = 0; i < zones.Count; i++)
        {
            if (only != null && !only.Contains(i)) continue;
            if (CancelPoint.Requested) { stopped = true; break; }
            var z = zones[i]; var sec = sections[z.Section];
            var where = $"section {sec.Name}{(sec.Source.Length > 0 ? $" ({sec.Source})" : "")}; zone {z.Label} ch {Show(z.From)}-{Show(z.To)}";
            foreach (var p in sec.Parts)
            {
                try
                {
                    for (var k = 0; k < p.Outlines.Count; k++)
                    {
                        var outline = p.Outlines[k];
                        var solids = new List<Solid>();
                        if (p.JointLength > Shapes.Mm(100))
                        {
                            var stagger = k % 2 == 1 ? p.JointLength / 2 : 0;
                            for (var s = z.From - stagger; s < z.To; s += p.JointLength)
                                solids.AddRange(Shapes.Sweep(alignment, outline, false, Math.Max(z.From, s), Math.Min(z.To, s + p.JointLength - Shapes.Mm(20))));
                        }
                        else solids.AddRange(Shapes.Sweep(alignment, outline, false, z.From, z.To));
                        var partName = p.Outlines.Count > 1 ? $"{p.Name} {k + 1}" : p.Name;
                        var (bottom, top) = AsBuiltMath.Levels(outline);
                        var comments = $"{where}; part={partName}; top={Show(top - baseZ)}; bottom={Show(bottom - baseZ)}; volume={Shapes.Volume(solids):0.###} m3" +
                                       (p.Assumed ? "; ASSUMED (not on the drawings)" : "; from the drawings") + (p.Note is { Length: > 0 } n ? $"; {n}" : "");
                        var e = Shapes.Make(doc, p.Category, $"{sec.Name} {partName}", solids, comments, Mark(prefix, z, sec.Name, partName));
                        if (phase != null) e.get_Parameter(BuiltInParameter.PHASE_CREATED)?.Set(phase.Id);
                        made.Add(new { id = e.Id.Value, zone = z.Label, part = partName, volumeM3 = Math.Round(Shapes.Volume(solids), 2) });
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or Autodesk.Revit.Exceptions.ApplicationException or InvalidOperationException)
                {
                    failed.Add($"{z.Label} {p.Name}: {ex.Message}");
                }
            }
            foreach (var r in sec.Piles)
            {
                var n = 0;
                foreach (var s in AsBuiltMath.Stations(z.From, z.To, r.Start, r.Spacing))
                {
                    var (at, dir) = Shapes.At(alignment, s);
                    var right = dir.CrossProduct(XYZ.BasisZ);
                    foreach (var off in r.Offsets)
                    {
                        try
                        {
                            var c = new XYZ(at.X, at.Y, 0) + right * off;
                            var solid = r.Diameter > 0
                                ? Shapes.Cylinder(new XYZ(c.X, c.Y, r.Toe), XYZ.BasisZ, r.Diameter / 2, r.Top - r.Toe)
                                : Shapes.Block(c, dir, right, -r.Width / 2, r.Width / 2, -r.Depth / 2, r.Depth / 2, r.Toe, r.Top);
                            n++;
                            var comments = $"{where}; part={r.Name}; top={Show(r.Top - baseZ)}; toe={Show(r.Toe - baseZ)}; from the drawings";
                            var e = Shapes.Make(doc, r.Category, $"{sec.Name} {r.Name}", [solid], comments, $"{Mark(prefix, z, sec.Name, r.Name)}-{n:000}");
                            if (phase != null) e.get_Parameter(BuiltInParameter.PHASE_CREATED)?.Set(phase.Id);
                            made.Add(new { id = e.Id.Value, zone = z.Label, part = r.Name });
                        }
                        catch (Exception ex) when (ex is ArgumentException or Autodesk.Revit.Exceptions.ApplicationException or InvalidOperationException)
                        {
                            failed.Add($"{z.Label} {r.Name} at ch {Show(s)}: {ex.Message}");
                        }
                    }
                }
            }
            built++;
        }
        if (made.Count == 0) { t.RollBack(); return ToolResult.Error(stopped ? CancelPoint.Stopped + ". Nothing was made." : "Nothing was made:\n- " + string.Join("\n- ", failed)); }
        if (t.Commit() != TransactionStatus.Committed) return ToolResult.Error("Revit refused the elements when saving them.");
        return ToolResult.Ok(new
        {
            made = made.Count, zonesBuilt = built, zonesTotal = zones.Count, elements = made.Take(300), failed,
            notCovered = gapText, assumedParts = assumed, levels = levelsReport, reference = referenceReport, referenceNote, levelDifferences = mismatches,
            stopped = stopped ? CancelPoint.Stopped : null,
            next = only != null && built < zones.Count
                ? "Look at this zone (capture_view_image of a section through it next to the drawing) before the rest; then call again with the other zones."
                : "Cut a section through each zone and compare it with its drawing (capture_view_image and read_drawing side by side) before saying it is done.",
        });
    }

    private static string Mark(string prefix, AsBuiltMath.Zone z, string section, string part)
    {
        var p = new string(part.ToUpperInvariant().Where(ch => char.IsLetterOrDigit(ch) || ch == ' ').ToArray()).Trim().Replace(' ', '-');
        return $"{prefix}-{z.Label.ToUpperInvariant().Replace(' ', '-')}-{p}";
    }

    private static string? Str(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static double? Num(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static BuiltInCategory Category(string? name, BuiltInCategory fallback) => (name ?? "").Trim().ToLowerInvariant() switch
    {
        "walls" or "wall" => BuiltInCategory.OST_Walls,
        "floors" or "floor" or "slab" or "deck" => BuiltInCategory.OST_Floors,
        "framing" or "beam" or "beams" or "structural framing" => BuiltInCategory.OST_StructuralFraming,
        "foundation" or "foundations" or "structural foundations" => BuiltInCategory.OST_StructuralFoundation,
        "columns" or "column" or "piles" or "pile" or "structural columns" => BuiltInCategory.OST_StructuralColumns,
        "generic" or "generic models" => BuiltInCategory.OST_GenericModel,
        "" => fallback,
        _ => fallback,
    };

    /// <summary>Tops and bottoms of the reference elements (this model or a link) that overlap each zone's stretch and width.</summary>
    private static List<AsBuiltMath.RefLevels> ReferenceLevels(Document doc, string? link, List<string> categoryNames, List<XYZ> alignment,
        List<AsBuiltMath.Zone> zones, Dictionary<string, SectionSpec> sections, out string note)
    {
        var source = doc; var transform = Transform.Identity; note = "this model";
        if (!string.IsNullOrWhiteSpace(link))
        {
            var inst = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>()
                .FirstOrDefault(l => l.Name.IndexOf(link, StringComparison.OrdinalIgnoreCase) >= 0 && l.GetLinkDocument() != null);
            if (inst is null) { note = $"No loaded link named like \"{link}\": nothing to compare with."; return []; }
            source = inst.GetLinkDocument(); transform = inst.GetTotalTransform(); note = $"link {inst.Name}";
        }
        var cats = new List<BuiltInCategory>();
        foreach (Category c in source.Settings.Categories)
            if (categoryNames.Any(n => n.Equals(c.Name, StringComparison.OrdinalIgnoreCase)) && Enum.IsDefined(typeof(BuiltInCategory), (int)c.Id.Value))
                cats.Add((BuiltInCategory)(int)c.Id.Value);
        if (cats.Count == 0) { note += $"; none of the categories {string.Join(", ", categoryNames)} is in it"; return []; }
        var plan = alignment.Select(p => (p.X, p.Y)).ToList();
        var list = new List<AsBuiltMath.RefLevels>();
        foreach (var e in new FilteredElementCollector(source).WherePasses(new ElementMulticategoryFilter(cats)).WhereElementIsNotElementType())
        {
            if (e.get_BoundingBox(null) is not { } bb) continue;
            var corners = new[] { bb.Min, new XYZ(bb.Max.X, bb.Min.Y, bb.Min.Z), new XYZ(bb.Min.X, bb.Max.Y, bb.Min.Z), new XYZ(bb.Max.X, bb.Max.Y, bb.Min.Z), bb.Max }
                .Select(transform.OfPoint).ToList();
            var stations = corners.Select(c => AsBuiltMath.Station(plan, (c.X, c.Y))).ToList();
            double c0 = stations.Min(s => s.Chainage), c1 = stations.Max(s => s.Chainage), o0 = stations.Min(s => s.Offset), o1 = stations.Max(s => s.Offset);
            double top = corners.Max(c => c.Z), bottom = corners.Min(c => c.Z);
            foreach (var z in zones)
            {
                var sec = sections[z.Section];
                var us = sec.Parts.SelectMany(p => p.Outlines.SelectMany(o => o.Select(x => x.u))).Concat(sec.Piles.SelectMany(r => r.Offsets)).ToList();
                if (us.Count == 0) continue;
                double u0 = us.Min() - Shapes.Mm(5000), u1 = us.Max() + Shapes.Mm(5000);
                if (c1 < z.From || c0 > z.To || o1 < u0 || o0 > u1) continue;
                list.Add(new AsBuiltMath.RefLevels(z.Label, e.Category?.Name ?? "", top, bottom));
            }
        }
        return list;
    }
}
