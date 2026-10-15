using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

/// <summary>
/// Builders' work openings: finds where pipes, ducts, conduits and cable trays (in this model or in linked MEP
/// models) pass through walls, floors and beams, and cuts an opening there: a native opening, or an opening/sleeve
/// family sized to the service plus clearance.
/// </summary>
public sealed class CreateMepOpeningsTool : IRevitTool
{
    public string Name => "create_mep_openings";
    public string Description =>
        "Find where pipes, ducts, conduits and cable trays cross walls, floors and structural beams, in this model and " +
        "in linked MEP models, and cut openings there. method 'native' cuts rectangular wall and floor openings; " +
        "'family' places an opening or sleeve family (wall-hosted, face-based or level-based), sized from the service " +
        "plus clearance (needed for beams and round openings); 'report' only lists the crossings. Skips places that already have an opening.";
    public string PropertiesJson => """
    {
      "method": {"type": "string", "enum": ["native", "family", "report"]},
      "sources": {"type": "array", "items": {"type": "string"}, "description": "'this model' and/or parts of linked model names. Default: this model and every loaded link."},
      "services": {"type": "array", "items": {"type": "string", "enum": ["pipes", "ducts", "conduits", "cable_trays"]}, "description": "Default all."},
      "hosts": {"type": "array", "items": {"type": "string", "enum": ["walls", "floors", "beams"]}, "description": "Default all."},
      "clearance": {"type": "number", "description": "Added all round, project units. Default 50 mm."},
      "min_size": {"type": "number", "description": "Ignore services smaller than this (project units)."},
      "level": {"type": "string", "description": "Only hosts on this level."},
      "family": {"type": "string", "description": "family method: 'Family: Type' of the opening or sleeve family."},
      "round_family": {"type": "string", "description": "Optional family for round services (pipes, round ducts)."},
      "width_parameter": {"type": "string", "description": "Default 'Width'."},
      "height_parameter": {"type": "string", "description": "Default 'Height'."},
      "diameter_parameter": {"type": "string", "description": "Default 'Diameter'."},
      "depth_parameter": {"type": "string", "description": "Optional, set to the host thickness."},
      "limit": {"type": "integer", "description": "Max openings in one run (default 2000)."}
    }
    """;
    public string[] Required => ["method"];
    public bool ModifiesModel => true;

    private sealed record Crossing(Element Host, XYZ Center, XYZ Flow, XYZ WidthDir, XYZ HeightDir, double Width, double Height, bool Round, double Depth, string Service, string Source);

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var method = args.String("method");
        var clearance = args.OptDouble("clearance") is { } c ? RevitHelpers.ToInternal(doc, c) : UnitUtils.ConvertToInternalUnits(50, UnitTypeId.Millimeters);
        var minSize = args.OptDouble("min_size") is { } ms ? RevitHelpers.ToInternal(doc, ms) : 0;
        var services = args.StringList("services");
        var hosts = args.StringList("hosts");
        var levelFilter = args.OptString("level") is { } ln ? RevitHelpers.FindLevel(doc, ln).Id : null;

        var hostCats = new List<BuiltInCategory>();
        if (hosts.Count == 0 || hosts.Contains("walls")) hostCats.Add(BuiltInCategory.OST_Walls);
        if (hosts.Count == 0 || hosts.Contains("floors")) hostCats.Add(BuiltInCategory.OST_Floors);
        if (hosts.Count == 0 || hosts.Contains("beams")) hostCats.Add(BuiltInCategory.OST_StructuralFraming);
        var hostFilter = new ElementMulticategoryFilter(hostCats);
        var solids = new Dictionary<long, List<Solid>>();

        // In-place hosts: Revit gives add-ins no geometry for them, so crossings through them can't be found. Say so.
        var inPlaceHosts = new FilteredElementCollector(doc).WherePasses(hostFilter).OfClass(typeof(FamilyInstance)).Where(InPlace.Is)
            .Where(e => levelFilter is null || e.LevelId == levelFilter).Select(e => e.Id.Value).ToList();
        var inPlaceNote = inPlaceHosts.Count == 0 ? null : new
        {
            ids = inPlaceHosts.Take(50),
            note = $"{inPlaceHosts.Count} in-place host(s) not checked: {InPlace.Short}. Check services through them by hand, or remodel them as walls/floors/beams.",
        };

        var crossings = new List<Crossing>();
        foreach (var (sourceDoc, transform, sourceName) in Sources(doc, args.StringList("sources")))
            foreach (var mep in MepCurves(sourceDoc, services))
                crossings.AddRange(Find(doc, mep, transform, sourceName, hostFilter, solids, levelFilter, minSize));

        FamilySymbol? rect = null, round = null;
        if (method == "family")
        {
            rect = FindSymbol(doc, args.OptString("family") ?? throw new ArgumentException("Give the opening family as 'Family: Type'."));
            round = args.OptString("round_family") is { } rf ? FindSymbol(doc, rf) : null;
        }

        var existing = new FilteredElementCollector(doc).OfClass(typeof(Opening)).Select(e => e.get_BoundingBox(null)).Where(b => b != null).ToList();
        if (rect != null)
        {
            // Openings already placed with the opening families (any of their types) count too.
            var familyTypes = new[] { rect, round }.OfType<FamilySymbol>().SelectMany(sym => sym.Family.GetFamilySymbolIds()).Distinct()
                .Select(id => (ElementFilter)new FamilyInstanceFilter(doc, id)).ToList();
            existing.AddRange(new FilteredElementCollector(doc).WherePasses(familyTypes.Count == 1 ? familyTypes[0] : new LogicalOrFilter(familyTypes))
                .Select(e => e.get_BoundingBox(null)).Where(b => b != null));
        }
        bool AlreadyOpen(XYZ p) => existing.Any(b => p.X >= b.Min.X - 0.05 && p.X <= b.Max.X + 0.05 && p.Y >= b.Min.Y - 0.05 && p.Y <= b.Max.Y + 0.05 && p.Z >= b.Min.Z - 0.05 && p.Z <= b.Max.Z + 0.05);

        if (method == "report")
        {
            return ToolResult.Ok(new
            {
                crossings = crossings.Count,
                list = crossings.Take(300).Select(x => new
                {
                    host = x.Host.Id.Value, hostCategory = x.Host.Category?.Name, x.Service, x.Source,
                    at = Coordinates.Out(doc, x.Center), size = $"{RevitHelpers.FromInternal(doc, x.Width)} x {RevitHelpers.FromInternal(doc, x.Height)}",
                    alreadyOpen = AlreadyOpen(x.Center),
                }),
                inPlaceHostsNotChecked = inPlaceNote,
            });
        }

        var wParam = args.OptString("width_parameter") ?? "Width";
        var hParam = args.OptString("height_parameter") ?? "Height";
        var dParam = args.OptString("diameter_parameter") ?? "Diameter";
        var depthParam = args.OptString("depth_parameter");
        var limit = (int)(args.OptLong("limit") ?? 2000);

        var created = new List<long>();
        var skipped = 0;
        var problems = new List<string>();
        using var t = new Transaction(doc, "Sketch ProMax: MEP openings");
        t.Start();
        var failure = t.GetFailureHandlingOptions();
        var swallow = new TypeKit.SwallowWarnings(doc);
        failure.SetFailuresPreprocessor(swallow);
        t.SetFailureHandlingOptions(failure);
        if (rect is { IsActive: false }) rect.Activate();
        if (round is { IsActive: false }) round.Activate();

        foreach (var x in crossings)
        {
            if (created.Count >= limit) break;
            if (AlreadyOpen(x.Center)) { skipped++; continue; }
            try
            {
                Element? made = method == "native" ? Native(doc, x, clearance) : Family(doc, x, x.Round && round != null ? round : rect!, clearance, wParam, hParam, dParam, depthParam, solids);
                if (made is null) { problems.Add($"{x.Host.Category?.Name} {x.Host.Id.Value}: native openings can't cut this host; use method 'family'"); continue; }
                created.Add(made.Id.Value);
                var bb = made.get_BoundingBox(null);
                if (bb != null) existing.Add(bb);
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or ArgumentException or InvalidOperationException)
            {
                if (problems.Count < 40) problems.Add($"{x.Host.Category?.Name} {x.Host.Id.Value}: {ex.Message}");
            }
        }
        t.Commit();
        created.RemoveAll(id => swallow.Deleted.Contains(id));
        return ToolResult.Ok(new { crossings = crossings.Count, created = created.Count, alreadyOpen = skipped, ids = created.Take(50), removedByRevit = swallow.Deleted.Take(50), problems = problems.Distinct(), inPlaceHostsNotChecked = inPlaceNote });
    }

    private static IEnumerable<(Document Doc, Transform Transform, string Name)> Sources(Document doc, List<string> wanted)
    {
        var links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().ToList();
        var thisModel = wanted.Count == 0 || wanted.Any(w => w.Trim().Equals("this model", StringComparison.OrdinalIgnoreCase));
        // Each named link resolves to one link file (all its placed copies); an ambiguous name is reported, not guessed.
        var types = wanted.Count == 0
            ? links.Select(l => l.GetTypeId().Value).ToHashSet()
            : wanted.Where(w => !w.Trim().Equals("this model", StringComparison.OrdinalIgnoreCase))
                .Select(w => LinkMatch.Find(doc, links, w).GetTypeId().Value).ToHashSet();
        if (thisModel) yield return (doc, Transform.Identity, "this model");
        foreach (var link in links)
            if (types.Contains(link.GetTypeId().Value) && link.GetLinkDocument() is { } linked)
                yield return (linked, link.GetTotalTransform(), link.Name);
    }

    private static IEnumerable<MEPCurve> MepCurves(Document doc, List<string> services)
    {
        var cats = new List<BuiltInCategory>();
        if (services.Count == 0 || services.Contains("pipes")) cats.Add(BuiltInCategory.OST_PipeCurves);
        if (services.Count == 0 || services.Contains("ducts")) cats.Add(BuiltInCategory.OST_DuctCurves);
        if (services.Count == 0 || services.Contains("conduits")) cats.Add(BuiltInCategory.OST_Conduit);
        if (services.Count == 0 || services.Contains("cable_trays")) cats.Add(BuiltInCategory.OST_CableTray);
        return new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(cats)).WhereElementIsNotElementType().OfType<MEPCurve>();
    }

    private static IEnumerable<Crossing> Find(Document doc, MEPCurve mep, Transform transform, string source, ElementFilter hostFilter, Dictionary<long, List<Solid>> solids, ElementId? level, double minSize)
    {
        if (mep.Location is not LocationCurve { Curve: Line local }) yield break;
        var line = (Line)local.CreateTransformed(transform);
        var connector = mep.ConnectorManager.Connectors.Cast<Connector>().FirstOrDefault(cn => cn.ConnectorType == ConnectorType.End);
        if (connector is null) yield break;
        var round = connector.Shape == ConnectorProfileType.Round;
        double width, height;
        if (round)
        {
            var outer = mep.get_Parameter(BuiltInParameter.RBS_PIPE_OUTER_DIAMETER)?.AsDouble()
                        ?? mep.get_Parameter(BuiltInParameter.RBS_CONDUIT_OUTER_DIAM_PARAM)?.AsDouble() ?? 0;
            width = height = outer > 0 ? outer : connector.Radius * 2;
        }
        else
        {
            width = connector.Width;
            height = connector.Height;
        }
        if (Math.Max(width, height) < minSize) yield break;
        var widthDir = transform.OfVector(connector.CoordinateSystem.BasisX);
        var heightDir = transform.OfVector(connector.CoordinateSystem.BasisY);
        var service = mep.Category?.Name ?? "service";

        var pad = Math.Max(width, height);
        var min = new XYZ(Math.Min(line.GetEndPoint(0).X, line.GetEndPoint(1).X) - pad, Math.Min(line.GetEndPoint(0).Y, line.GetEndPoint(1).Y) - pad, Math.Min(line.GetEndPoint(0).Z, line.GetEndPoint(1).Z) - pad);
        var max = new XYZ(Math.Max(line.GetEndPoint(0).X, line.GetEndPoint(1).X) + pad, Math.Max(line.GetEndPoint(0).Y, line.GetEndPoint(1).Y) + pad, Math.Max(line.GetEndPoint(0).Z, line.GetEndPoint(1).Z) + pad);
        var candidates = new FilteredElementCollector(doc).WherePasses(hostFilter)
            .WherePasses(new BoundingBoxIntersectsFilter(new Outline(min, max))).WhereElementIsNotElementType();

        foreach (var host in candidates)
        {
            if (level != null && host.LevelId != level && host.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM)?.AsElementId() != level) continue;
            foreach (var solid in Solids(host, solids))
            {
                SolidCurveIntersection hit;
                try { hit = solid.IntersectWithCurve(line, new SolidCurveIntersectionOptions { ResultType = SolidCurveIntersectionMode.CurveSegmentsInside }); }
                catch (Autodesk.Revit.Exceptions.InvalidOperationException) { continue; }
                for (var i = 0; i < hit.SegmentCount; i++)
                {
                    var seg = hit.GetCurveSegment(i);
                    yield return new Crossing(host, seg.Evaluate(0.5, true), line.Direction, widthDir, heightDir, width, height, round, seg.Length, service, source);
                }
            }
        }
    }

    /// <summary>A host's solids in model coordinates, computed once per host.</summary>
    private static List<Solid> Solids(Element e, Dictionary<long, List<Solid>> cache)
    {
        if (cache.TryGetValue(e.Id.Value, out var cached)) return cached;
        var list = new List<Solid>();
        var geometry = e.get_Geometry(new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine });
        if (geometry != null)
            foreach (var obj in geometry)
            {
                if (obj is Solid { Volume: > 0 } s) list.Add(s);
                else if (obj is GeometryInstance gi) list.AddRange(gi.GetInstanceGeometry().OfType<Solid>().Where(x => x.Volume > 0));
            }
        return cache[e.Id.Value] = list;
    }

    /// <summary>
    /// Planar faces a face-based family can be hosted on, each with the transform from its coordinates to the model.
    /// Family instance geometry that is still shared with the type (an uncut beam) comes as a GeometryInstance; only
    /// its symbol geometry carries references Revit accepts as a host face, so those are used with the instance transform.
    /// </summary>
    private static IEnumerable<(PlanarFace Face, Transform Transform)> HostFaces(Element host, Dictionary<long, List<Solid>> cache)
    {
        if (host is not FamilyInstance)
        {
            foreach (var solid in Solids(host, cache))
                foreach (Face f in solid.Faces)
                    if (f is PlanarFace pf) yield return (pf, Transform.Identity);
            yield break;
        }
        var geometry = host.get_Geometry(new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine });
        if (geometry is null) yield break;
        foreach (var obj in geometry)
        {
            if (obj is Solid { Volume: > 0 } s)
            {
                foreach (Face f in s.Faces)
                    if (f is PlanarFace pf) yield return (pf, Transform.Identity);
            }
            else if (obj is GeometryInstance gi)
            {
                foreach (var inner in gi.GetSymbolGeometry().OfType<Solid>().Where(x => x.Volume > 0))
                    foreach (Face f in inner.Faces)
                        if (f is PlanarFace pf) yield return (pf, gi.Transform);
            }
        }
    }

    /// <summary>Half the size of the service's cross-section measured along a direction.</summary>
    private static double HalfAlong(Crossing x, XYZ dir) =>
        (Math.Abs(x.WidthDir.DotProduct(dir)) * x.Width + Math.Abs(x.HeightDir.DotProduct(dir)) * x.Height) / 2;

    private static Element? Native(Document doc, Crossing x, double clearance)
    {
        switch (x.Host)
        {
            case Wall { Location: LocationCurve { Curve: Line wallLine } } wall:
            {
                var along = wallLine.Direction;
                var skew = Math.Max(0.3, Math.Abs(x.Flow.DotProduct(wall.Orientation)));
                var halfU = HalfAlong(x, along) / skew + clearance;
                var halfZ = HalfAlong(x, XYZ.BasisZ) + clearance;
                return doc.Create.NewOpening(wall, x.Center - along * halfU - XYZ.BasisZ * halfZ, x.Center + along * halfU + XYZ.BasisZ * halfZ);
            }
            case Floor floor when x.Round && Math.Abs(x.Flow.Normalize().Z) > 0.9:
            {
                // A vertical round service gets a round opening.
                var r = x.Width / 2 + clearance;
                var c = x.Center;
                var circle = new CurveArray();
                circle.Append(Arc.Create(c, r, 0, Math.PI, XYZ.BasisX, XYZ.BasisY));
                circle.Append(Arc.Create(c, r, Math.PI, 2 * Math.PI, XYZ.BasisX, XYZ.BasisY));
                return doc.Create.NewOpening(floor, circle, true);
            }
            case Floor floor:
            {
                var hx = HalfAlong(x, XYZ.BasisX) + clearance;
                var hy = HalfAlong(x, XYZ.BasisY) + clearance;
                var c = x.Center;
                var pts = new[] { new XYZ(c.X - hx, c.Y - hy, c.Z), new XYZ(c.X + hx, c.Y - hy, c.Z), new XYZ(c.X + hx, c.Y + hy, c.Z), new XYZ(c.X - hx, c.Y + hy, c.Z) };
                var profile = new CurveArray();
                for (var i = 0; i < 4; i++) profile.Append(Line.CreateBound(pts[i], pts[(i + 1) % 4]));
                return doc.Create.NewOpening(floor, profile, true);
            }
            default:
                return null;
        }
    }

    private static Element Family(Document doc, Crossing x, FamilySymbol symbol, double clearance, string wParam, string hParam, string dParam, string? depthParam, Dictionary<long, List<Solid>> solids)
    {
        FamilyInstance instance;
        var placement = symbol.Family.FamilyPlacementType;
        var level = doc.GetElement(x.Host.LevelId) as Level
                    ?? new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => Math.Abs(l.ProjectElevation - x.Center.Z)).First();
        double w, h;
        if (x.Host is Wall { Location: LocationCurve { Curve: Line wallLine } } wall && placement == FamilyPlacementType.OneLevelBasedHosted)
        {
            instance = doc.Create.NewFamilyInstance(x.Center, symbol, wall, level, StructuralType.NonStructural);
            var skew = Math.Max(0.3, Math.Abs(x.Flow.DotProduct(wall.Orientation)));
            w = 2 * (HalfAlong(x, wallLine.Direction) / skew + clearance);
            h = 2 * (HalfAlong(x, XYZ.BasisZ) + clearance);
            // Wall-hosted openings are placed by their sill/base; centre them on the service.
            var sill = instance.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM);
            if (sill is { IsReadOnly: false }) sill.Set(x.Center.Z - level.ProjectElevation - h / 2);
        }
        else if (placement is FamilyPlacementType.WorkPlaneBased or FamilyPlacementType.OneLevelBasedHosted)
        {
            var (face, at) = NearestFace(x, solids) ?? throw new InvalidOperationException("no host face found for a face-based family");
            var refDir = Math.Abs(x.Flow.Z) > 0.7 ? XYZ.BasisX : new XYZ(0, 0, 1).CrossProduct(x.Flow).Normalize();
            if (refDir.IsZeroLength()) refDir = XYZ.BasisX;
            instance = doc.Create.NewFamilyInstance(face, at, refDir, symbol);
            w = 2 * (HalfAlong(x, refDir) + clearance);
            h = 2 * (HalfAlong(x, x.Flow.CrossProduct(refDir).Normalize()) + clearance);
        }
        else
        {
            instance = doc.Create.NewFamilyInstance(x.Center, symbol, level, StructuralType.NonStructural);
            var offset = instance.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM) ?? instance.get_Parameter(BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM);
            if (offset is { IsReadOnly: false }) offset.Set(x.Center.Z - level.ProjectElevation);
            w = x.Width + 2 * clearance;
            h = x.Height + 2 * clearance;
        }

        void Set(string name, double value)
        {
            if (instance.LookupParameter(name) is { IsReadOnly: false, StorageType: StorageType.Double } p) p.Set(value);
        }
        if (x.Round) Set(dParam, Math.Max(x.Width, x.Height) + 2 * clearance);
        Set(wParam, w);
        Set(hParam, h);
        if (depthParam != null) Set(depthParam, x.Depth);
        return instance;
    }

    private static (Reference Face, XYZ At)? NearestFace(Crossing x, Dictionary<long, List<Solid>> solids)
    {
        (Reference, XYZ)? best = null;
        var bestDistance = double.MaxValue;
        foreach (var (pf, toModel) in HostFaces(x.Host, solids))
        {
            if (pf.Reference is null || Math.Abs(toModel.OfVector(pf.FaceNormal).DotProduct(x.Flow)) < 0.7) continue;
            var hit = pf.Project(toModel.Inverse.OfPoint(x.Center));
            if (hit is null || hit.Distance >= bestDistance) continue;
            bestDistance = hit.Distance;
            best = (pf.Reference, toModel.OfPoint(hit.XYZPoint));
        }
        return best;
    }

    private static FamilySymbol FindSymbol(Document doc, string name) =>
        TypeMatch.Find(new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().ToList(), name, "family type");
}
