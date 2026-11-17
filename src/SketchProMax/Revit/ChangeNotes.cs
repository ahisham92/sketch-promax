using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Structure;
using M = SketchProMax.Revit.ChangeFileMath;

namespace SketchProMax.Revit;

/// <summary>
/// Annotations, rooms and reinforcement in the change file: text notes, tags (element, room, area and space tags),
/// dimensions, spot elevations and coordinates, revision clouds, detail items (any family drawn in a view), rooms and
/// spaces, rebar, and area and path reinforcement. Reading what makes each one, making it again, and setting it back.
/// Tags, dimensions and spots point at other elements through Revit's stable references; one pointing at an element
/// made again in the same replay is pointed at the new one.
/// </summary>
internal static class ChangeNotes
{
    /// <summary>What the change file calls the element; null when it isn't one of these kinds.</summary>
    public static string? Kind(Element e) => e switch
    {
        SpotDimension => "spot",
        Dimension => "dimension",
        IndependentTag => "tag",
        SpatialElementTag => "roomtag",
        TextNote => "text",
        RevisionCloud => "cloud",
        Room => "room",
        Space => "space",
        Rebar => "rebar",
        AreaReinforcement => "areareinf",
        PathReinforcement => "pathreinf",
        ImageInstance => "image",
        _ => null,
    };

    public static bool Owns(M.Line line) => line.What is "spot" or "dimension" or "tag" or "roomtag" or "text" or "cloud" or "room" or "space" or "rebar" or "areareinf" or "pathreinf" or "image";

    private static readonly HashSet<long> PartCategories = new[] { "OST_AreaReinBoundary", "OST_PathReinBoundary", "OST_AreaReinSketchOverrides" }
        .Select(n => Enum.TryParse<BuiltInCategory>(n, out var c) ? (long)c : 0).Where(c => c != 0).ToHashSet();

    /// <summary>Parts of another element's line (reinforcement boundaries, bars of an area or path system): no line of their own.</summary>
    public static bool IsPart(Element e) =>
        e is AreaReinforcementCurve or RebarInSystem || (e is CurveElement && e.Category is { } c && PartCategories.Contains(c.Id.Value));

    // ---- Reading -------------------------------------------------------------------------------------------------------

    /// <summary>Adds what makes the element to its line (after the location and sketch are read).</summary>
    public static void Read(Document doc, Element e, M.Line line)
    {
        if (line.View is null && e.OwnerViewId != ElementId.InvalidElementId) line.View = doc.GetElement(e.OwnerViewId)?.UniqueId;
        var approx = false;
        switch (e)
        {
            case SpotDimension spot:
                line.Refs = Refs(doc, spot.References);
                line.P = P(spot.Origin);
                try
                {
                    line.Leader = spot.HasLeader;
                    if (spot.HasLeader) { line.Q = P(spot.LeaderEndPosition); if (spot.LeaderHasShoulder) line.M = P(spot.LeaderShoulderPosition); }
                }
                catch (Exception) { }
                break;
            case Dimension dim:
                line.Refs = Refs(doc, dim.References);
                // Linear dimensions only: their line is P and a point one foot along it.
                if (dim.Curve is Line dl) { line.P = P(dl.Origin); line.Q = P(dl.Origin + dl.Direction); }
                else ReadArcDimension(dim, line, ref approx);
                break;
            case IndependentTag tag:
                line.Refs = Refs(doc, tag.GetTaggedReferences());
                line.P = P(tag.TagHeadPosition);
                line.Leader = tag.HasLeader;
                line.Orientation = (int)tag.TagOrientation;
                break;
            case SpatialElementTag roomTag:
                line.Host = roomTag switch { RoomTag rt => rt.Room?.UniqueId, AreaTag at => at.Area?.UniqueId, SpaceTag st => st.Space?.UniqueId, _ => null };
                line.Leader = roomTag.HasLeader;
                break;
            case TextNote text:
                line.Text = text.Text;
                line.P = P(text.Coord);
                line.Width = M.Round(text.Width);
                line.Leader = text.LeaderCount > 0;
                if (line.Leader) line.Leads = Leads(text);
                if (doc.GetElement(text.OwnerViewId) is View tv) line.Rotation = M.Round(Angle(tv, text.BaseDirection));
                break;
            case RevisionCloud cloud:
                line.Revision = doc.GetElement(cloud.RevisionId)?.UniqueId;
                line.Loops ??= [cloud.GetSketchCurves().SelectMany(c => ChangeShapes.Segs(c, ref approx)).ToList()];
                break;
            case Rebar rebar:
                ReadRebar(doc, rebar, line, ref approx);
                break;
            case AreaReinforcement area:
                line.Host = doc.GetElement(area.GetHostId())?.UniqueId;
                line.Loops ??= [area.GetBoundaryCurveIds().Select(doc.GetElement).OfType<AreaReinforcementCurve>().SelectMany(c => ChangeShapes.Segs(c.Curve, ref approx)).ToList()];
                line.Bar = new M.Bar { Dir = P(area.Direction), BarType = BarTypeOf(doc, area.GetRebarInSystemIds()) };
                break;
            case ImageInstance image:
                // A placed image: its middle, its width, and the file its type was read from.
                line.P = P(image.GetLocation(BoxPlacement.Center));
                line.Width = M.Round(image.Width);
                try { if (doc.GetElement(image.GetTypeId()) is ImageType { Path: { Length: > 0 } path }) (line.Extra ??= [])["path"] = path; } catch (Exception) { }
                break;
            case PathReinforcement path:
                line.Host = doc.GetElement(path.GetHostId())?.UniqueId;
                line.Loops ??= [path.GetCurveElementIds().Select(doc.GetElement).OfType<CurveElement>().SelectMany(c => ChangeShapes.Segs(c.GeometryCurve, ref approx)).ToList()];
                line.Bar = new M.Bar { BarType = BarTypeOf(doc, path.GetRebarInSystemIds()) };
                break;
        }
        if (approx) line.Approx = true;
    }

    /// <summary>An angle, radius or diameter dimension: which it is, and the arc its line is on (angle dimensions are made on it).</summary>
    private static void ReadArcDimension(Dimension dim, M.Line line, ref bool approx)
    {
        DimensionShape shape;
        try { shape = dim.DimensionShape; } catch (Exception) { return; }
        if (shape is not (DimensionShape.Angular or DimensionShape.Radial or DimensionShape.Diameter)) return;
        (line.Extra ??= [])["dim"] = shape.ToString();
        if (shape != DimensionShape.Angular || dim.Curve is not Arc arc) return;
        // Revit can hand the whole circle: a quarter of it says the same (its centre, radius and plane).
        if (!arc.IsBound) arc = Arc.Create(arc.Center, arc.Radius, 0, Math.PI / 2, arc.XDirection, arc.YDirection);
        line.Loops = [ChangeShapes.Segs(arc, ref approx).ToList()];
    }

    /// <summary>Null when a dimension that isn't linear can be made again, else why not.</summary>
    private static string? CanMakeArcDimension(M.Line line)
    {
        var shape = line.Extra is { } x && x.TryGetValue("dim", out var d) ? d : null;
        if (shape == nameof(DimensionShape.Angular) && line.Loops is { Count: > 0 } && line.Refs is { Count: >= 2 }) return null;
#if !REVIT2024
        if (shape is nameof(DimensionShape.Radial) or nameof(DimensionShape.Diameter)) return null;
#else
        if (shape is nameof(DimensionShape.Radial) or nameof(DimensionShape.Diameter)) return "a radius or diameter dimension made after the last save: Revit 2024 has no way for add-ins to make one, make it again by hand";
#endif
        return "an arc length dimension made after the last save: make it again by hand";
    }

    private static Dimension MakeArcDimension(Document doc, M.Line line, View view, ElementType? type, Func<string, Reference> reference)
    {
        var shape = line.Extra!["dim"];
        if (shape == nameof(DimensionShape.Angular))
        {
            var arc = ChangeShapes.ToCurve(line.Loops![0][0]) as Arc ?? throw new InvalidOperationException("the file doesn't have the dimension's arc");
            var style = type as DimensionType ?? new FilteredElementCollector(doc).OfClass(typeof(DimensionType)).Cast<DimensionType>().FirstOrDefault(t => t.StyleType == DimensionStyleType.Angular)
                        ?? throw new InvalidOperationException("there is no angular dimension type in this model");
            return AngularDimension.Create(doc, view, arc, line.Refs!.Take(2).Select(reference).ToList(), style)
                   ?? throw new InvalidOperationException("Revit couldn't make the angle dimension");
        }
#if !REVIT2024
        Dimension radial = RadialDimension.Create(doc, view, reference(line.Refs![0]), shape == nameof(DimensionShape.Diameter))
                           ?? throw new InvalidOperationException("Revit couldn't make the radius dimension");
        if (type is DimensionType wanted && radial.GetTypeId() != wanted.Id) try { radial.ChangeTypeId(wanted.Id); } catch (Exception) { }
        return radial;
#else
        throw new InvalidOperationException("Revit 2024 has no way for add-ins to make a radius dimension");
#endif
    }

    private static List<string>? Refs(Document doc, IEnumerable<Reference> references)
    {
        var list = new List<string>();
        foreach (var r in references)
        {
            try { list.Add(r.ConvertToStableRepresentation(doc)); } catch (Exception) { }
        }
        return list.Count > 0 ? list : null;
    }

    private static List<string>? Refs(Document doc, ReferenceArray array) => Refs(doc, array.Cast<Reference>());

    private static string? BarTypeOf(Document doc, IEnumerable<ElementId> bars) =>
        bars.Select(doc.GetElement).FirstOrDefault(b => b is not null) is { } bar ? doc.GetElement(bar.GetTypeId())?.UniqueId : null;

    private static void ReadRebar(Document doc, Rebar rebar, M.Line line, ref bool approx)
    {
        line.Host = doc.GetElement(rebar.GetHostId())?.UniqueId;
        var bar = new M.Bar { FreeForm = rebar.IsRebarFreeForm() };
        line.Bar = bar;
        if (bar.FreeForm)
        {
            // Free form: every bar of the set has its own curves (one loop each), made again as an unconstrained free form bar.
            var bars = new List<List<M.Seg>>();
            var count = Math.Min(rebar.NumberOfBarPositions, MaxFreeFormBars);
            for (var i = 0; i < count; i++)
            {
                var one = new List<M.Seg>();
                foreach (var c in rebar.GetCenterlineCurves(false, true, true, MultiplanarOption.IncludeAllMultiplanarCurves, i)) one.AddRange(ChangeShapes.Segs(c, ref approx));
                if (one.Count > 0) bars.Add(one);
            }
            line.Loops = bars;
            try { bar.Style = rebar.get_Parameter(BuiltInParameter.REBAR_ELEM_HOOK_STYLE)?.AsInteger() == 1 ? 1 : 0; } catch (Exception) { }
            return;
        }
        var loop = new List<M.Seg>();
        foreach (var c in rebar.GetCenterlineCurves(false, true, true, MultiplanarOption.IncludeOnlyPlanarCurves, 0)) loop.AddRange(ChangeShapes.Segs(c, ref approx));
        line.Loops = [loop];
        if (doc.GetElement(rebar.GetShapeId()) is RebarShape shape) bar.Style = shape.RebarStyle == RebarStyle.StirrupTie ? 1 : 0;
        bar.HookStart = doc.GetElement(rebar.GetHookTypeId(0))?.UniqueId;
        bar.HookEnd = doc.GetElement(rebar.GetHookTypeId(1))?.UniqueId;
#if REVIT2024 || REVIT2025
        bar.OrientStart = rebar.GetHookOrientation(0) == RebarHookOrientation.Left ? 1 : -1;
        bar.OrientEnd = rebar.GetHookOrientation(1) == RebarHookOrientation.Left ? 1 : -1;
#else
        bar.OrientStart = rebar.GetTerminationOrientation(0) == RebarTerminationOrientation.Left ? 1 : -1;
        bar.OrientEnd = rebar.GetTerminationOrientation(1) == RebarTerminationOrientation.Left ? 1 : -1;
#endif
        var accessor = rebar.GetShapeDrivenAccessor();
        line.PlaneNormal = P(accessor.Normal);
        bar.Rule = rebar.LayoutRule.ToString();
        if (rebar.LayoutRule != RebarLayoutRule.Single)
        {
            bar.N = rebar.NumberOfBarPositions;
            bar.Spacing = M.Round(rebar.MaxSpacing);
            bar.Length = M.Round(accessor.ArrayLength);
            bar.NormalSide = accessor.BarsOnNormalSide;
            bar.NoFirst = !rebar.IncludeFirstBar;
            bar.NoLast = !rebar.IncludeLastBar;
        }
    }

    // ---- Making again -----------------------------------------------------------------------------------------------------

    /// <summary>Null when the element can be made again from its line, else why not.</summary>
    public static string? CanMake(M.Line line) => line.What switch
    {
        "tag" or "dimension" or "spot" when line.Refs is null || line.View is null => "made after the last save, but the file doesn't say what it points at: make it again by hand",
        "dimension" when line.P is null || line.Q is null => CanMakeArcDimension(line),
        "spot" when line.CategoryId == (long)BuiltInCategory.OST_SpotSlopes => "a spot slope made after the last save: make it again by hand",
        "tag" or "spot" when line.P is null => "made after the last save, but the file has no place for it: make it again by hand",
        "tag" or "dimension" or "spot" => null,
        "roomtag" when line.Host is not null && line.P is not null && line.View is not null => null,
        "text" when line.Text is not null && line.P is not null && line.View is not null => null,
        "cloud" when line.Loops is { Count: > 0 } && line.View is not null && line.Revision is not null => null,
        "room" or "space" when line.P is not null && line.Level is not null => null,
        "rebar" when line.Bar is { FreeForm: true } => line.Loops is { Count: > 0 } && line.Host is not null && line.Type is not null ? null : "free form rebar made after the last save whose bars aren't in the file: make it again by hand",
        "rebar" when line.Loops is { Count: > 0 } && line.PlaneNormal is not null && line.Host is not null && line.Type is not null => null,
        "areareinf" when line.Loops is { Count: > 0 } && line.Bar?.Dir is not null && line.Host is not null && line.Type is not null => null,
        "pathreinf" when line.Loops is { Count: > 0 } && line.Host is not null && line.Type is not null => null,
        "image" when line.P is not null && line.View is not null => null,
        _ => "made after the last save, but the file doesn't have all it needs: make it again by hand",
    };

    /// <summary>
    /// Makes the element again; null when <paramref name="line"/> isn't one of these kinds. <paramref name="find"/> finds
    /// an element by its old UniqueId (one made again in this replay too); <paramref name="remap"/> points a stable
    /// reference at elements made again. Notes for the report go in <paramref name="notes"/>.
    /// </summary>
    public static Element? Make(Document doc, M.Line line, ElementType? type, Level? level, Func<string?, Element?> find, Func<string, string> remap, List<string> notes)
    {
        if (!Owns(line)) return null;
        View ViewOf() => find(line.View) as View ?? throw new InvalidOperationException("the view it was drawn in isn't in this model");
        Element HostOf() => find(line.Host) ?? throw new InvalidOperationException("what it was on isn't in this model");
        ElementId TypeId() => type?.Id ?? throw new InvalidOperationException($"the type {line.Type} isn't in this model");
        Reference Ref(string stable) => Reference.ParseFromStableRepresentation(doc, remap(stable))
                                        ?? throw new InvalidOperationException("what it pointed at isn't in this model");
        var label = M.Describe(line);
        switch (line.What)
        {
            case "tag":
            {
                var tag = IndependentTag.Create(doc, TypeId(), ViewOf().Id, Ref(line.Refs![0]), line.Leader, (TagOrientation)line.Orientation, X(line.P!));
                if (line.Refs.Count > 1)
                    try { tag.AddReferences(line.Refs.Skip(1).Select(Ref).ToList()); } catch (Exception) { notes.Add($"{label}: made again on its first element only: add the others by hand"); }
                return tag;
            }
            case "dimension":
            {
                // A grid or a level is dimensioned by the element itself: the reference the dimension reports for it
                // ("…:0:SURFACE") reads back as one Revit then refuses ("Invalid number of references").
                Reference DimRef(string stable)
                {
                    var r = Ref(stable);
                    return doc.GetElement(r.ElementId) is DatumPlane datum ? new Reference(datum) : r;
                }
                var refs = new ReferenceArray();
                foreach (var r in line.Refs!) refs.Append(DimRef(r));
                var view = ViewOf();
                if (line.P is null || line.Q is null) return MakeArcDimension(doc, line, view, type, DimRef);
                var dimLine = Line.CreateBound(X(line.P!), X(line.Q!));
                return type is DimensionType dt ? doc.Create.NewDimension(view, dimLine, refs, dt) : doc.Create.NewDimension(view, dimLine, refs);
            }
            case "spot":
            {
                var view = ViewOf();
                var origin = X(line.P!);
                var end = line.Q is { } q ? X(q) : origin;
                var bend = line.M is { } m ? X(m) : end;
                return line.CategoryId == (long)BuiltInCategory.OST_SpotCoordinates
                    ? doc.Create.NewSpotCoordinate(view, Ref(line.Refs![0]), origin, bend, end, origin, line.Leader)
                    : doc.Create.NewSpotElevation(view, Ref(line.Refs![0]), origin, bend, end, origin, line.Leader);
            }
            case "roomtag":
            {
                var view = ViewOf();
                var at = new UV(line.P!.X, line.P.Y);
                SpatialElementTag made = HostOf() switch
                {
                    Room room => doc.Create.NewRoomTag(new LinkElementId(room.Id), at, view.Id),
                    Area area when view is ViewPlan plan => doc.Create.NewAreaTag(plan, area, at),
                    Space space => doc.Create.NewSpaceTag(space, at, view),
                    _ => throw new InvalidOperationException("its room, area or space isn't in this model"),
                };
                return made;
            }
            case "text":
            {
                var view = ViewOf();
                var note = line.Width is > 0 ? TextNote.Create(doc, view.Id, X(line.P!), line.Width.Value, line.Text!, TypeId()) : TextNote.Create(doc, view.Id, X(line.P!), line.Text!, TypeId());
                if (line.Rotation is { } r && Math.Abs(r) > 1e-6)
                    ElementTransformUtils.RotateElement(doc, note.Id, Line.CreateBound(X(line.P!), X(line.P!) + view.ViewDirection), r);
                if (line.Leads is { Count: > 0 } leads) AddLeads(note, leads, label, notes);
                else if (line.Leader) notes.Add($"{label}: made again without its leaders: add them by hand");
                return note;
            }
            case "cloud":
            {
                var revision = find(line.Revision) ?? throw new InvalidOperationException("its revision isn't in this model");
                return RevisionCloud.Create(doc, ViewOf(), revision.Id, line.Loops!.SelectMany(l => l).Select(ChangeShapes.ToCurve).ToList());
            }
            case "room":
                return doc.Create.NewRoom(level ?? throw new InvalidOperationException("its level isn't in this model"), new UV(line.P!.X, line.P.Y));
            case "space":
                return doc.Create.NewSpace(level ?? throw new InvalidOperationException("its level isn't in this model"), new UV(line.P!.X, line.P.Y));
            case "rebar":
                return MakeRebar(doc, line, type as RebarBarType ?? throw new InvalidOperationException($"the bar type {line.Type} isn't in this model"), HostOf(), find);
            case "areareinf":
                return AreaReinforcement.Create(doc, HostOf(), Curves(line), X(line.Bar!.Dir!), TypeId(), BarType(doc, line, find), ElementId.InvalidElementId);
            case "image":
            {
                // Its image is in the model still (its type), or is read again from its file.
                var path = line.Extra is { } x && x.TryGetValue("path", out var from) ? from : null;
                var imageType = type as ImageType
                                ?? new FilteredElementCollector(doc).OfClass(typeof(ImageType)).Cast<ImageType>().FirstOrDefault(t => path is not null && string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase));
                if (imageType is null)
                {
                    if (path is null || !System.IO.File.Exists(path)) throw new InvalidOperationException($"its image file {path ?? "(not known)"} isn't there any more: place the image again by hand");
                    imageType = ImageType.Create(doc, new ImageTypeOptions(path, false, ImageTypeSource.Import));
                }
                var image = ImageInstance.Create(doc, ViewOf(), imageType.Id, new ImagePlacementOptions(X(line.P!), BoxPlacement.Center));
                if (line.Width is { } wide && wide > 0 && !M.Same(image.Width, wide)) try { image.Width = wide; image.SetLocation(X(line.P!), BoxPlacement.Center); } catch (Exception) { }
                return image;
            }
            case "pathreinf":
                return PathReinforcement.Create(doc, HostOf(), Curves(line), line.Bar?.Flip ?? false, TypeId(), BarType(doc, line, find), ElementId.InvalidElementId, ElementId.InvalidElementId);
            default:
                return null;
        }
    }

    private static List<Curve> Curves(M.Line line) => line.Loops!.SelectMany(l => l).Select(ChangeShapes.ToCurve).ToList();

    private static ElementId BarType(Document doc, M.Line line, Func<string?, Element?> find) =>
        find(line.Bar?.BarType)?.Id ?? new FilteredElementCollector(doc).OfClass(typeof(RebarBarType)).FirstElementId();

    /// <summary>The most bars of one free form set that are written (a set is rarely over a few hundred).</summary>
    private const int MaxFreeFormBars = 500;

    private static Rebar MakeRebar(Document doc, M.Line line, RebarBarType barType, Element host, Func<string?, Element?> find)
    {
        var bar = line.Bar ?? new M.Bar();
        var style = bar.Style == 1 ? RebarStyle.StirrupTie : RebarStyle.Standard;
        if (bar.FreeForm)
        {
            IList<IList<Curve>> bars = line.Loops!.Select(l => (IList<Curve>)l.Select(ChangeShapes.ToCurve).ToList()).ToList();
#if REVIT2024 || REVIT2025
            var free = Rebar.CreateFreeForm(doc, barType, host, bars, out var error);
            if (free is null) throw new InvalidOperationException($"Revit couldn't make the free form bar from its curves ({error})");
            return free;
#else
            var result = Rebar.CreateFreeForm(doc, barType, host, bars, style);
            return result?.Rebar ?? throw new InvalidOperationException($"Revit couldn't make the free form bar from its curves ({result?.Error})");
#endif
        }
        var normal = X(line.PlaneNormal!);
        var curves = Curves(line);
#if REVIT2024 || REVIT2025
        RebarHookOrientation O(int o) => o < 0 ? RebarHookOrientation.Right : RebarHookOrientation.Left;
        var rebar = Rebar.CreateFromCurves(doc, style, barType, find(bar.HookStart) as RebarHookType, find(bar.HookEnd) as RebarHookType, host, normal, curves,
            O(bar.OrientStart), O(bar.OrientEnd), true, true);
#else
        RebarTerminationOrientation O(int o) => o < 0 ? RebarTerminationOrientation.Right : RebarTerminationOrientation.Left;
        var ends = new BarTerminationsData(doc)
        {
            HookTypeIdAtStart = find(bar.HookStart)?.Id ?? ElementId.InvalidElementId,
            HookTypeIdAtEnd = find(bar.HookEnd)?.Id ?? ElementId.InvalidElementId,
            TerminationOrientationAtStart = O(bar.OrientStart),
            TerminationOrientationAtEnd = O(bar.OrientEnd),
        };
        var rebar = Rebar.CreateFromCurves(doc, style, barType, host, normal, curves, ends, true, true);
#endif
        if (rebar is null) throw new InvalidOperationException("Revit couldn't make the bar from its shape");
        Layout(rebar, bar);
        return rebar;
    }

    private static void Layout(Rebar rebar, M.Bar bar)
    {
        var a = rebar.GetShapeDrivenAccessor();
        bool first = !bar.NoFirst, last = !bar.NoLast;
        switch (bar.Rule)
        {
            case nameof(RebarLayoutRule.FixedNumber): a.SetLayoutAsFixedNumber(bar.N, bar.Length, bar.NormalSide, first, last); break;
            case nameof(RebarLayoutRule.MaximumSpacing): a.SetLayoutAsMaximumSpacing(bar.Spacing, bar.Length, bar.NormalSide, first, last); break;
            case nameof(RebarLayoutRule.NumberWithSpacing): a.SetLayoutAsNumberWithSpacing(bar.N, bar.Spacing, bar.NormalSide, first, last); break;
            case nameof(RebarLayoutRule.MinimumClearSpacing): a.SetLayoutAsMinimumClearSpacing(bar.Spacing, bar.Length, bar.NormalSide, first, last); break;
            default: a.SetLayoutAsSingle(); break;
        }
    }

    // ---- Setting back ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Sets the element back to its line; true when anything changed. A changed sketch (revision clouds, area and path
    /// reinforcement drawn as a sketch) is handed back in <paramref name="sketch"/> to edit after the transaction.
    /// What can't be set back goes in <paramref name="notes"/>.
    /// </summary>
    public static bool Set(Document doc, Element e, M.Line line, Func<string, string> remap, List<string> notes, out ElementId? sketch)
    {
        sketch = null;
        var now = new M.Line();
        ReadNow(doc, e, now);
        var label = M.Describe(line);
        var changed = false;
        switch (e)
        {
            case SpotDimension:
                if (line.P is { } sp && now.P is { } np && !M.Same(sp, np)) notes.Add($"{label}: the spot moved: check its place");
                break;
            case Dimension when line.P is { } p && line.Q is { } q && now.P is { } p0 && now.Q is { } q0:
                if (line.Refs is not null && !SameRefs(now.Refs, line.Refs.Select(remap).ToList())) notes.Add($"{label}: what it measures changed: redo its witness lines by hand");
                // Only the dimension line's offset can be set back: move it square to its direction.
                var dir = (X(q0) - X(p0)).Normalize();
                var delta = X(p) - X(p0);
                delta -= dir.Multiply(delta.DotProduct(dir));
                if (delta.GetLength() > 3.3e-4) { ElementTransformUtils.MoveElement(doc, e.Id, delta); changed = true; }
                var want = (X(q) - X(p)).Normalize();
                if (!want.IsAlmostEqualTo(dir) && !want.IsAlmostEqualTo(dir.Negate())) notes.Add($"{label}: its direction changed: redo it by hand");
                break;
            case IndependentTag tag:
                if (line.P is { } head && !M.Same(head, now.P)) { tag.TagHeadPosition = X(head); changed = true; }
                if (tag.HasLeader != line.Leader) { tag.HasLeader = line.Leader; changed = true; }
                if ((int)tag.TagOrientation != line.Orientation) { tag.TagOrientation = (TagOrientation)line.Orientation; changed = true; }
                if (line.Refs is not null && !SameRefs(now.Refs, line.Refs.Select(remap).ToList())) notes.Add($"{label}: it points at another element now: check what it tags");
                break;
            case SpatialElementTag roomTag:
                if (roomTag.HasLeader != line.Leader) { roomTag.HasLeader = line.Leader; changed = true; }
                break;
            case TextNote text:
                if (line.Text is { } t && text.Text.TrimEnd('\r', '\n') != t.TrimEnd('\r', '\n')) { text.Text = t; changed = true; }
                if (line.Leads is not null || (!line.Leader && text.LeaderCount > 0))
                {
                    if (!M.SameLeads(line.Leads, now.Leads))
                    {
                        text.RemoveLeaders();
                        AddLeads(text, line.Leads ?? [], label, notes);
                        changed = true;
                    }
                }
                if (line.P is { } c && !M.Same(c, now.P)) { text.Coord = X(c); changed = true; }
                if (line.Width is { } w && now.Width is { } w0 && !M.Same(w, w0)) { try { text.Width = w; changed = true; } catch (Exception) { } }
                if (line.Rotation is { } r && now.Rotation is { } r0 && !M.Same(r, r0) && doc.GetElement(text.OwnerViewId) is View tv)
                {
                    ElementTransformUtils.RotateElement(doc, text.Id, Line.CreateBound(text.Coord, text.Coord + tv.ViewDirection), r - r0);
                    changed = true;
                }
                break;
            case Rebar rebar when line.Loops is not null && now.Loops is not null:
                if (!M.SameLoops(now.Loops, line.Loops))
                {
                    if (Shift(now.Loops, line.Loops) is { } move) { ElementTransformUtils.MoveElement(doc, rebar.Id, move); changed = true; }
                    else notes.Add($"{label}: the bar's shape changed: reshape it by hand");
                }
                if (line.Bar is { FreeForm: false } bar && now.Bar is { } nb && !SameLayout(bar, nb)) { Layout(rebar, bar); changed = true; }
                break;
            case ImageInstance image:
                if (line.Width is { } iw && iw > 0 && !M.Same(image.Width, iw)) { try { image.Width = iw; changed = true; } catch (Exception) { } }
                if (line.P is { } ip && !M.Same(P(image.GetLocation(BoxPlacement.Center)), ip)) { image.SetLocation(X(ip), BoxPlacement.Center); changed = true; }
                break;
            case RevisionCloud or AreaReinforcement or PathReinforcement when line.Loops is not null && now.Loops is not null && !M.SameLoops(now.Loops, line.Loops):
                if (Shift(now.Loops, line.Loops) is { } shift) { ElementTransformUtils.MoveElement(doc, e.Id, shift); changed = true; }
                else if (ChangeShapes.SketchOf(doc, e) is { } s) sketch = s.Id;
                else notes.Add($"{label}: its outline changed: reshape it by hand");
                break;
        }
        return changed;
    }

    private static List<M.Lead> Leads(TextNote text)
    {
        var leads = new List<M.Lead>();
        foreach (Leader leader in text.GetLeaders())
        {
            var shape = leader.LeaderShape;
            leads.Add(new M.Lead
            {
                End = P(leader.End), Arc = shape == LeaderShape.Arc, Elbow = shape == LeaderShape.Kinked ? P(leader.Elbow) : null,
                Right = (leader.End - text.Coord).DotProduct(text.BaseDirection) > 0,
            });
        }
        return leads;
    }

    private static void AddLeads(TextNote text, List<M.Lead> leads, string label, List<string> notes)
    {
        var missed = 0;
        foreach (var lead in leads)
        {
            try
            {
                var leader = text.AddLeader(lead.Arc ? (lead.Right ? TextNoteLeaderTypes.TNLT_ARC_R : TextNoteLeaderTypes.TNLT_ARC_L) : (lead.Right ? TextNoteLeaderTypes.TNLT_STRAIGHT_R : TextNoteLeaderTypes.TNLT_STRAIGHT_L));
                leader.End = X(lead.End);
                if (lead.Elbow is { } elbow) try { leader.Elbow = X(elbow); } catch (Exception) { }
            }
            catch (Exception) { missed++; }
        }
        if (missed > 0) notes.Add($"{label}: {missed} of its leaders didn't come back: add them by hand");
    }

    /// <summary>The element as it is now, in the same terms as its line.</summary>
    private static void ReadNow(Document doc, Element e, M.Line now)
    {
        if (e is RevisionCloud or AreaReinforcement or PathReinforcement) try { ChangeShapes.Read(doc, e, now); } catch (Exception) { }
        Read(doc, e, now);
    }

    /// <summary>
    /// True when the element in the model can't be set to its line and is made again in its place instead: a dimension
    /// that measures other things now or runs another way, a bar whose shape changed.
    /// </summary>
    public static bool MustRemake(Document doc, Element e, M.Line line, Func<string, string> remap)
    {
        try
        {
            if (e is SpotDimension || CanMake(line) is not null) return false;
            var now = new M.Line();
            ReadNow(doc, e, now);
            switch (e)
            {
                case Dimension:
                    if (line.Refs is not null && !SameRefs(now.Refs, line.Refs.Select(remap).ToList())) return true;
                    if (line.P is { } p && line.Q is { } q && now.P is { } p0 && now.Q is { } q0)
                    {
                        var (want, dir) = ((X(q) - X(p)).Normalize(), (X(q0) - X(p0)).Normalize());
                        return !want.IsAlmostEqualTo(dir) && !want.IsAlmostEqualTo(dir.Negate());
                    }
                    return false;
                case Rebar when line.Loops is not null && now.Loops is not null:
                    return !M.SameLoops(now.Loops, line.Loops) && Shift(now.Loops, line.Loops) is null;
                default:
                    return false;
            }
        }
        catch (Exception) { return false; }
    }

    private static bool SameRefs(List<string>? a, List<string> b) => a is not null && a.Count == b.Count && a.All(b.Contains);

    private static bool SameLayout(M.Bar a, M.Bar b) =>
        a.Rule == b.Rule && a.N == b.N && M.Same(a.Spacing, b.Spacing) && M.Same(a.Length, b.Length) && a.NormalSide == b.NormalSide && a.NoFirst == b.NoFirst && a.NoLast == b.NoLast;

    /// <summary>The move that takes one outline onto the other, when it is only moved (same edges in the same order).</summary>
    private static XYZ? Shift(List<List<M.Seg>> now, List<List<M.Seg>> want)
    {
        var a = now.SelectMany(l => l).ToList();
        var b = want.SelectMany(l => l).ToList();
        if (a.Count != b.Count || a.Count == 0) return null;
        var move = X(b[0].P) - X(a[0].P);
        var moved = a.Select(s => new M.Seg(P(X(s.P) + move), P(X(s.Q) + move), s.M is { } m ? P(X(m) + move) : null)).ToList();
        return M.SameLoops([moved], [b]) ? move : null;
    }

    private static double Angle(View view, XYZ direction) => view.RightDirection.AngleOnPlaneTo(direction, view.ViewDirection);

    private static M.Xyz P(XYZ p) => ChangeShapes.P(p);

    private static XYZ X(M.Xyz p) => ChangeShapes.X(p);
}
