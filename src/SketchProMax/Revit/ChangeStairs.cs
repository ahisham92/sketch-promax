using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using M = SketchProMax.Revit.ChangeFileMath;

namespace SketchProMax.Revit;

/// <summary>
/// Stairs in the change file: their base and top levels and straight runs (path, justification, width, base height).
/// Stairs made after the last save are made again in a stairs edit scope (outside the replay's transactions), each run
/// in place with a landing between runs; stairs that only moved are moved back. Runs and landings have no line of their
/// own: a change to one is written as its stairs.
/// </summary>
internal static class ChangeStairs
{
    /// <summary>Parts of stairs (runs, landings): their stairs' line covers them.</summary>
    public static bool IsPart(Element e) => e is StairsRun or StairsLanding;

    /// <summary>The stairs a run or landing belongs to, else null.</summary>
    public static ElementId? StairsOf(Element? e)
    {
        try
        {
            return e switch
            {
                StairsRun run => run.GetStairs()?.Id,
                StairsLanding landing => landing.GetStairs()?.Id,
                _ => null,
            };
        }
        catch (Exception) { return null; }
    }

    public static void Read(Document doc, Stairs stairs, M.Line line)
    {
        if (doc.GetElement(stairs.get_Parameter(BuiltInParameter.STAIRS_BASE_LEVEL_PARAM)?.AsElementId() ?? ElementId.InvalidElementId) is Level baseLevel)
        { line.Level = baseLevel.Name; line.LevelUnique = baseLevel.UniqueId; }
        if (doc.GetElement(stairs.get_Parameter(BuiltInParameter.STAIRS_TOP_LEVEL_PARAM)?.AsElementId() ?? ElementId.InvalidElementId) is Level top)
            line.TopLevel = top.UniqueId;
        line.Runs = [];
        foreach (var id in stairs.GetStairsRuns())
        {
            if (doc.GetElement(id) is not StairsRun run) continue;
            if (run.StairsRunStyle != StairsRunStyle.Straight || run.GetStairsPath().FirstOrDefault() is not Line path) { line.Text = "curved"; continue; }
            line.Runs.Add(new M.Run
            {
                P = ChangeShapes.P(path.GetEndPoint(0)), Q = ChangeShapes.P(path.GetEndPoint(1)), Justify = (int)run.LocationLineJustification,
                Width = M.Round(run.ActualRunWidth), Base = M.Round(run.BaseElevation),
            });
        }
    }

    public static string? CanMake(M.Line line) =>
        line.Text == "curved" ? "stairs with a curved or sketched run, made after the last save: make them again by hand"
        : line.Runs is not { Count: > 0 } || line.LevelUnique is null || line.TopLevel is null ? "stairs made after the last save whose runs aren't in the file: make them again by hand"
        : null;

    /// <summary>Makes the stairs again (call with no transaction open); the stairs' id.</summary>
    public static ElementId Make(Document doc, M.Line line, Func<string?, Element?> find)
    {
        if (find(line.LevelUnique) is not Level baseLevel) throw new InvalidOperationException("its base level isn't in this model");
        if (find(line.TopLevel) is not Level top) throw new InvalidOperationException("its top level isn't in this model");
        using var scope = new StairsEditScope(doc, "Sketch ProMax: Replay change file (stairs)");
        try
        {
            var id = scope.Start(baseLevel.Id, top.Id);
            using (var t = new Transaction(doc, "Replay stairs"))
            {
                t.Start();
                StairsRun? before = null;
                foreach (var r in line.Runs!)
                {
                    var run = StairsRun.CreateStraightRun(doc, id, Line.CreateBound(ChangeShapes.X(r.P), ChangeShapes.X(r.Q)), (StairsRunJustification)r.Justify);
                    try { run.ActualRunWidth = r.Width; } catch (Exception) { }
                    try { if (!M.Same(run.BaseElevation, r.Base)) run.BaseElevation = r.Base; } catch (Exception) { }
                    if (before is not null) try { StairsLanding.CreateAutomaticLanding(doc, before.Id, run.Id); } catch (Exception) { }
                    before = run;
                }
                t.Commit();
            }
            scope.Commit(new ChangeShapes.Quiet());
            return id;
        }
        catch (Exception)
        {
            try { if (scope.IsActive) scope.Cancel(); } catch (Exception) { }
            throw;
        }
    }

    /// <summary>Stairs that only moved are moved back; a changed shape is listed. True when moved.</summary>
    public static bool Set(Document doc, Stairs stairs, M.Line line, List<string> notes)
    {
        if (line.Runs is not { Count: > 0 } want) return false;
        var now = new M.Line();
        Read(doc, stairs, now);
        if (now.Runs is not { } runs || runs.Count == want.Count && runs.Zip(want, (a, b) => M.Same(a.P, b.P) && M.Same(a.Q, b.Q) && M.Same(a.Width, b.Width)).All(x => x)) return false;
        if (M.RunsShift(runs, want) is { } move)
        {
            ElementTransformUtils.MoveElement(doc, stairs.Id, ChangeShapes.X(move));
            return true;
        }
        notes.Add($"{M.Describe(line)}: the stairs' runs changed: edit the stairs by hand");
        return false;
    }
}
