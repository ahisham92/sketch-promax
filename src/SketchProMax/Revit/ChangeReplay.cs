using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using SketchProMax.Tools;
using M = SketchProMax.Revit.ChangeFileMath;

namespace SketchProMax.Revit;

/// <summary>
/// Plays a change file (<see cref="ChangeFileMath"/>) onto the open model: each element found by its UniqueId is set
/// to the state in the file (type, place, rotation, flips, level elevation, values); one that isn't in the model is
/// made again when Revit allows it from what the file holds (family instances on a point or a line, straight walls,
/// grids, levels); a deleted one is deleted. Anything else is listed to redo by hand. Runs in chunks of
/// <see cref="ChangeFileMath.ReplayChunk"/> elements, one Revit call and one undo step each, through
/// <see cref="ItemBatch"/> (one item failing doesn't stop the rest; what others own in a workshared model is skipped
/// and named; what needs borrowing is borrowed in one request per chunk).
/// </summary>
internal static class ChangeReplay
{
    public enum Act { Set, Make, Delete, Skip }

    public sealed record Step(M.Line Line, Act Act, string Why);

    public sealed class Outcome
    {
        public int Set, Made, Deleted, Same;
        public readonly List<string> NotDone = [];
        /// <summary>Old UniqueId to the element made again for it, so what it hosts (later in the file) finds it.</summary>
        public readonly Dictionary<string, ElementId> MadeFor = new(StringComparer.Ordinal);
        /// <summary>Sketches to redraw after the chunk's transactions (a sketch edit scope can't sit inside one); Counted: already counted as set back.</summary>
        public readonly List<(ElementId Sketch, ElementId Owner, M.Line Line, bool Counted)> Sketches = [];
    }

    /// <summary>What each line of the file will do to <paramref name="doc"/> (read only).</summary>
    public static List<Step> Plan(Document doc, M.Parsed file)
    {
        var steps = new List<Step>();
        foreach (var line in M.PlayOrder(file))
        {
            var found = Find(doc, line);
            if (line.Kind == M.Kinds.Deleted)
                steps.Add(found is null ? new(line, Act.Skip, "already not in the model") : new(line, Act.Delete, ""));
            else if (found is not null)
                steps.Add(new(line, Act.Set, ""));
            else if (CanMake(line) is { } why)
                steps.Add(new(line, Act.Skip, why));
            else
                steps.Add(new(line, Act.Make, ""));
        }
        return steps;
    }

    /// <summary>The element a line is about: by UniqueId; a deletion named by id only, by id when it was in central already (never someone's new element).</summary>
    private static Element? Find(Document doc, M.Line line)
    {
        if (line.Unique is { Length: > 0 } unique) return doc.GetElement(unique);
        if (line.Kind != M.Kinds.Deleted || line.Id == 0) return null;
        var element = doc.GetElement(new ElementId(line.Id));
        if (element is null) return null;
        try
        {
            if (doc.IsWorkshared && WorksharingUtils.GetModelUpdatesStatus(doc, element.Id) == ModelUpdatesStatus.NotYetInCentral) return null;
        }
        catch (Exception) { }
        return element;
    }

    /// <summary>Null when the element can be made again from the line, else why not.</summary>
    private static string? CanMake(M.Line line) => line.What switch
    {
        "level" when line.Elevation is not null => null,
        "grid" when line.P is not null && line.Q is not null => null,
        "wall" when (line.Loops is { Count: > 0 } || (line.P is not null && line.Q is not null)) && line.Type is not null => null,
        "instance" when line.P is not null && line.Type is not null => null,
        "wall" or "instance" => "made after the last save, but its shape isn't a point or a straight line: make it again by hand",
        "type" => "a type made after the last save: duplicate it again by hand",
        "floor" or "ceiling" or "topo" or "region" or "railing" or "roof" or "roof-other" or "opening" or "modelcurve" or "detailcurve" => ChangeShapes.CanMake(line),
        _ => "made after the last save; this kind of element can't be made from the file: make it again by hand",
    };

    /// <summary>Plays <paramref name="steps"/> (one chunk) onto the model. Call with no transaction open.</summary>
    public static void Apply(Document doc, IReadOnlyList<Step> steps, Outcome outcome)
    {
        var work = steps.Where(s => s.Act != Act.Skip).ToList();
        foreach (var s in steps.Where(s => s.Act == Act.Skip)) outcome.NotDone.Add($"{M.Describe(s.Line)}: {s.Why}");
        if (work.Count == 0) return;
        var result = ItemBatch.Run(doc, "Replay change file", work, s => M.Describe(s.Line),
            s => Find(doc, s.Line) is { } e ? [e.Id] : [],
            s =>
            {
                switch (s.Act)
                {
                    case Act.Delete:
                        if (Find(doc, s.Line) is { } gone) doc.Delete(gone.Id);
                        outcome.Deleted++;
                        break;
                    case Act.Set:
                        var sketches = outcome.Sketches.Count;
                        var set = SetState(doc, Find(doc, s.Line)!, s.Line, outcome);
                        if (set) outcome.Set++; else outcome.Same++;
                        if (outcome.Sketches.Count > sketches) outcome.Sketches[outcome.Sketches.Count - 1] = outcome.Sketches[outcome.Sketches.Count - 1] with { Counted = set };
                        break;
                    case Act.Make:
                        var made = Make(doc, s.Line, outcome);
                        if (s.Line.Unique is { } u) outcome.MadeFor[u] = made.Id;
                        SetState(doc, made, s.Line, outcome);
                        outcome.Made++;
                        if (s.Line.SlopeArrow) outcome.NotDone.Add($"{M.Describe(s.Line)}: made again without its slope arrow: add the slope by hand");
                        break;
                }
                return null;
            });
        foreach (var f in result.Failed) outcome.NotDone.Add($"{f.Item}: {f.Error}");
        foreach (var s in work.Where(s => s.Line.Approx && s.Act != Act.Delete))
            outcome.NotDone.Add($"{M.Describe(s.Line)}: its curved edges (a spline or ellipse) came back as short straight lines: check its shape");

        // Changed sketches, each in a sketch edit scope of its own, after the chunk's transactions.
        foreach (var (sketch, owner, line, counted) in outcome.Sketches)
        {
            var why = doc.GetElement(owner) is { } element ? ChangeShapes.EditSketch(doc, sketch, element, line) : "it isn't in the model any more";
            if (why is not null) outcome.NotDone.Add($"{M.Describe(line)}: the outline didn't come back: {why}");
            else if (!counted) { outcome.Same--; outcome.Set++; }
        }
        outcome.Sketches.Clear();
    }

    // ---- Setting an element to its state --------------------------------------------------------------------------------

    /// <summary>Sets what differs; true when anything changed.</summary>
    private static bool SetState(Document doc, Element element, M.Line line, Outcome outcome)
    {
        var changed = false;
        if (element is not ElementType && line.Type is not null && Type(doc, line) is { } type && element.GetTypeId() != type.Id)
        {
            element.ChangeTypeId(type.Id);
            changed = true;
        }
        switch (element)
        {
            case Level level when line.Elevation is { } elevation && !M.Same(level.Elevation, elevation):
                level.Elevation = elevation;
                changed = true;
                break;
            case Grid grid when line.P is { } gp && line.Q is { } gq && grid.Curve is Line now:
                // A grid can be moved as a whole; a changed length or angle is left (and listed).
                var move = X(gp) - now.GetEndPoint(0);
                if (move.GetLength() > 3.3e-5)
                {
                    if ((X(gq) - now.GetEndPoint(1) - move).GetLength() < 3.3e-4) ElementTransformUtils.MoveElement(doc, grid.Id, move);
                    else outcome.NotDone.Add($"{M.Describe(line)}: the grid's length or angle changed; drag its ends by hand");
                    changed = true;
                }
                break;
            default:
                if (element.Location is LocationPoint lp && line.P is { } p)
                {
                    if (!M.Same(P(lp.Point), p)) { lp.Point = X(p); changed = true; }
                    if (line.Rotation is { } r)
                    {
                        double now;
                        try { now = lp.Rotation; } catch (Exception) { now = r; }
                        if (!M.Same(now, r))
                        {
                            lp.Rotate(Line.CreateBound(X(p), X(p) + XYZ.BasisZ), r - now);
                            changed = true;
                        }
                    }
                }
                else if (element.Location is LocationCurve lc && line.P is { } a && line.Q is { } b && lc.Curve is Line or Arc)
                {
                    var approx = false;
                    var now = ChangeShapes.Segs(lc.Curve, ref approx).ToList();
                    var want = new M.Seg(a, b, line.M);
                    if (now.Count != 1 || !M.SameLoops([now], [[want]])) { lc.Curve = ChangeShapes.ToCurve(want); changed = true; }
                }
                break;
        }
        if (line.Loops is not null || line.Points is not null)
        {
            changed |= ChangeShapes.Set(doc, element, line, out var sketch);
            if (sketch is { } id) outcome.Sketches.Add((id, element.Id, line, false));
        }
        if (element is FamilyInstance fi)
        {
            try
            {
                if (fi.CanFlipFacing && fi.FacingFlipped != line.FacingFlipped) { fi.flipFacing(); changed = true; }
                if (fi.CanFlipHand && fi.HandFlipped != line.HandFlipped) { fi.flipHand(); changed = true; }
            }
            catch (Exception) { }
        }
        if (element is Level or Grid && line.Name is { Length: > 0 } name && element.Name != name)
        {
            try { element.Name = name; changed = true; } catch (Exception) { /* the name is taken: left as it is */ }
        }
        foreach (var v in line.Values ?? [])
        {
            var p = Find(element, v);
            if (p is null || p.IsReadOnly) continue;
            try
            {
                switch (v.Kind)
                {
                    case "d" when v.Number is { } d && !M.Same(p.AsDouble(), d): changed |= p.Set(d); break;
                    case "i" when v.Whole is { } i && p.AsInteger() != i: changed |= p.Set(i); break;
                    case "s" when (p.AsString() ?? "") != (v.Text ?? ""): changed |= p.Set(v.Text ?? ""); break;
                    case "e":
                        var target = v.Ref is { Length: > 0 } r ? (outcome.MadeFor.TryGetValue(r, out var made) ? made : doc.GetElement(r)?.Id) : ElementId.InvalidElementId;
                        if (target is not null && p.AsElementId() != target) changed |= p.Set(target);
                        break;
                }
            }
            catch (Exception) { /* a value Revit won't take here (it depends on another one): left */ }
        }
        return changed;
    }

    private static Parameter? Find(Element element, M.Value v)
    {
        if (v.Bip != 0 && element.get_Parameter((BuiltInParameter)v.Bip) is { } builtIn) return builtIn;
        if (v.Guid is { } g && Guid.TryParse(g, out var guid) && element.get_Parameter(guid) is { } shared) return shared;
        return v.Bip == 0 && v.Name is { Length: > 0 } name ? element.LookupParameter(name) : null;
    }

    // ---- Making an element again ------------------------------------------------------------------------------------------

    private static Element Make(Document doc, M.Line line, Outcome outcome)
    {
        Element? Known(string? unique) => unique is null ? null : outcome.MadeFor.TryGetValue(unique, out var id) ? doc.GetElement(id) : doc.GetElement(unique);
        if (ChangeShapes.Make(doc, line, Type(doc, line), LevelOf(doc, line), Known) is { } shaped) return shaped;
        switch (line.What)
        {
            case "level":
                var level = Level.Create(doc, line.Elevation!.Value);
                return level;
            case "grid":
                return Grid.Create(doc, Line.CreateBound(X(line.P!), X(line.Q!)));
            case "wall":
            {
                var type = Type(doc, line) ?? throw new InvalidOperationException($"the wall type {line.Type} isn't in this model");
                var lvl = LevelOf(doc, line) ?? throw new InvalidOperationException("its level isn't in this model");
                if (line.Loops is { Count: > 0 } profile)
                    return Wall.Create(doc, profile[0].Select(ChangeShapes.ToCurve).ToList(), type.Id, lvl.Id, false);
                return Wall.Create(doc, ChangeShapes.ToCurve(new M.Seg(line.P!, line.Q!, line.M)), type.Id, lvl.Id, 10, 0, false, false);
            }
            default:
            {
                if (Type(doc, line) is not FamilySymbol symbol) throw new InvalidOperationException($"the type {line.Type} isn't loaded in this model");
                if (!symbol.IsActive) symbol.Activate();
                var lvl = LevelOf(doc, line);
                var structural = Enum.TryParse<StructuralType>(line.Structural, out var st) ? st : StructuralType.NonStructural;
                if (line.Q is { } q)
                {
                    if (lvl is null) throw new InvalidOperationException("its level isn't in this model");
                    return doc.Create.NewFamilyInstance(ChangeShapes.ToCurve(new M.Seg(line.P!, q, line.M)), symbol, lvl, structural);
                }
                var host = line.Host is { } h ? (outcome.MadeFor.TryGetValue(h, out var madeHost) ? doc.GetElement(madeHost) : doc.GetElement(h)) : null;
                if (host is not null && lvl is not null) return doc.Create.NewFamilyInstance(X(line.P!), symbol, host, lvl, structural);
                if (host is not null) return doc.Create.NewFamilyInstance(X(line.P!), symbol, host, structural);
                if (line.Host is not null) throw new InvalidOperationException("its host isn't in this model");
                return lvl is not null ? doc.Create.NewFamilyInstance(X(line.P!), symbol, lvl, structural) : doc.Create.NewFamilyInstance(X(line.P!), symbol, structural);
            }
        }
    }

    private static ElementType? Type(Document doc, M.Line line)
    {
        if (line.TypeUnique is { } u && doc.GetElement(u) is ElementType byId) return byId;
        if (line.Type is not { } name) return null;
        return new FilteredElementCollector(doc).WhereElementIsElementType().Cast<ElementType>()
            .FirstOrDefault(t => $"{t.FamilyName}: {t.Name}" == name && (line.CategoryId == 0 || t.Category?.Id.Value == line.CategoryId));
    }

    private static Level? LevelOf(Document doc, M.Line line)
    {
        if (line.LevelUnique is { } u && doc.GetElement(u) is Level byId) return byId;
        return line.Level is { } name ? new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault(l => l.Name == name) : null;
    }

    private static XYZ X(M.Xyz p) => new(p.X, p.Y, p.Z);

    private static M.Xyz P(XYZ p) => new(p.X, p.Y, p.Z);
}
