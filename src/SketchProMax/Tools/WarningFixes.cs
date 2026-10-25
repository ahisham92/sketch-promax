using Autodesk.Revit.DB;

namespace SketchProMax.Tools;

/// <summary>
/// Fixes for Revit warnings that clear them without deleting anything (Make it BIM-ready and the checklist's warnings
/// check): lines slightly off axis are turned onto the exact axis, and joins of elements that don't touch are undone.
/// </summary>
internal static class WarningFixes
{
    public const string OffAxisText = "slightly off axis";

    private static List<FailureMessage> Of(Document doc, string text) =>
        doc.GetWarnings().Where(w => (w.GetDescriptionText() ?? "").IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

    /// <summary>The element's straight line in plan, when it has one (walls, beams, grids, lines, reference planes).</summary>
    private static Line? LineOf(Element e) => e switch
    {
        Grid g => g.Curve as Line,
        CurveElement c => c.GeometryCurve as Line,
        ReferencePlane p => Line.CreateBound(p.BubbleEnd, p.FreeEnd),
        _ => (e.Location as LocationCurve)?.Curve as Line,
    };

    /// <summary>
    /// The element's line and the turn (degrees, about its middle) that puts it on its exact direction, when it is a
    /// straight line in plan, not pinned, and neither end moves more than <paramref name="maxMm"/> (at most BimReadyPlan.MaxMoveMm). Null otherwise.
    /// </summary>
    public static (Line Line, double Turn)? Straighten(Element e, double maxMm = BimReadyPlan.MaxMoveMm)
    {
        if (e.Pinned || LineOf(e) is not { } line) return null;
        var d = line.Direction;
        if (Math.Abs(d.Z) > 1e-6) return null;
        var turn = BimReadyPlan.OffAxisTurn(Math.Atan2(d.Y, d.X) * 180.0 / Math.PI);
        if (turn is null || BimReadyPlan.EndMoveMm(line.Length * 304.8, turn.Value) > Math.Min(maxMm, BimReadyPlan.MaxMoveMm)) return null;
        return (line, turn.Value);
    }

    /// <summary>
    /// Turns each element of a "slightly off axis" warning, about its middle, onto the exact 0° or 90° direction, when
    /// neither end moves more than <paramref name="maxMm"/> (1 mm first, then 2 mm at most); the rest are left for the user.
    /// </summary>
    /// <param name="only">These elements (the ones whose warning was hidden as they were drawn), not the warnings' elements.</param>
    public static string OffAxis(Document doc, IEnumerable<ElementId>? only = null, double maxMm = BimReadyPlan.MaxMoveMm)
    {
        var ids = (only ?? Of(doc, OffAxisText).SelectMany(w => w.GetFailingElements())).Distinct().ToList();
        if (ids.Count == 0) return "No slightly off axis warnings.";
        int turned = 0, skipped = 0;
        using var tx = new Transaction(doc, "Sketch ProMax: straighten off-axis lines");
        tx.Start();
        foreach (var id in ids)
        {
            var e = doc.GetElement(id);
            if (e is null || Straighten(e, maxMm) is not { } fix) { skipped++; continue; }
            var (line, turn) = fix;
            var start = (line.GetEndPoint(0) + line.GetEndPoint(1)) / 2;
            using var sub = new SubTransaction(doc);
            sub.Start();
            try
            {
                ElementTransformUtils.RotateElement(doc, id, Line.CreateBound(start, start + XYZ.BasisZ), turn * Math.PI / 180.0);
                sub.Commit();
                turned++;
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or ArgumentException or InvalidOperationException)
            {
                sub.RollBack();
                skipped++;
            }
        }
        tx.Commit();
        return $"Straightened {turned} slightly off axis element{(turned == 1 ? "" : "s")}" +
               (skipped > 0 ? $"; {skipped} left (an end would move more than {maxMm:0} mm, pinned, owned by someone else, or not a straight line): check them in Manage > Warnings." : ".");
    }

    /// <summary>
    /// Clears the "slightly off axis" warnings with no change to the geometry: each element is moved 1 mm and straight
    /// back in the same transaction (net zero), which makes Revit check it again, and the off-axis warning it raises again
    /// is dropped as the transaction commits (DropWarnings), so it is no longer kept in the model. The elements end exactly
    /// where they were. The first choice for a model already submitted; Straighten the off-axis lines (1 mm, then 2 mm at most) is the fallback.
    /// </summary>
    public static string ClearOffAxis(Document doc)
    {
        var ids = Of(doc, OffAxisText).SelectMany(w => w.GetFailingElements()).Distinct().Where(id => doc.GetElement(id) is { Pinned: false }).ToList();
        if (ids.Count == 0) return "No slightly off axis warnings (or only on pinned elements).";
        var before = Of(doc, OffAxisText).Count;
        using (var tx = new Transaction(doc, "Sketch ProMax: clear the off-axis warnings"))
        {
            var options = tx.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(new DropWarnings(OffAxisText));
            tx.SetFailureHandlingOptions(options);
            tx.Start();
            // 1 mm, not more: joins with neighbours follow a move this small, so they are as they were after the move back.
            var there = new XYZ(1 / 304.8, 0, 0);
            foreach (var id in ids)
            {
                using var sub = new SubTransaction(doc);
                sub.Start();
                try
                {
                    ElementTransformUtils.MoveElement(doc, id, there);
                    ElementTransformUtils.MoveElement(doc, id, there.Negate());
                    sub.Commit();
                }
                catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or ArgumentException or InvalidOperationException) { sub.RollBack(); }
            }
            tx.Commit();
        }
        var after = Of(doc, OffAxisText).Count;
        return $"Cleared {before - after} of {before} slightly off axis warning{(before == 1 ? "" : "s")} without moving anything" +
               (after > 0 ? $"; {after} stayed (pinned or owned by someone else): Straighten the off-axis lines (1 mm at most, then 2 mm at most) is the fallback." : ".");
    }

    /// <summary>Drops the warnings whose text contains <c>text</c> as a transaction commits (they are then not kept in the model).</summary>
    internal sealed class DropWarnings(string text) : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor fa)
        {
            foreach (var m in fa.GetFailureMessages())
                if (m.GetSeverity() == FailureSeverity.Warning && (m.GetDescriptionText() ?? "").IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
                    fa.DeleteWarning(m);
            return FailureProcessingResult.Continue;
        }
    }

    /// <summary>Unjoins the pairs Revit warns are joined but don't intersect.</summary>
    public static string UnjoinApart(Document doc)
    {
        var warnings = Of(doc, "joined but do not intersect");
        if (warnings.Count == 0) return "No joins of elements that don't touch.";
        int undone = 0, left = 0;
        using var tx = new Transaction(doc, "Sketch ProMax: unjoin elements that don't touch");
        tx.Start();
        foreach (var w in warnings)
        {
            var ids = w.GetFailingElements().Concat(w.GetAdditionalElements()).Distinct().ToList();
            for (var i = 0; i < ids.Count; i++)
                for (var j = i + 1; j < ids.Count; j++)
                {
                    var a = doc.GetElement(ids[i]);
                    var b = doc.GetElement(ids[j]);
                    if (a is null || b is null || !JoinGeometryUtils.AreElementsJoined(doc, a, b)) continue;
                    try { JoinGeometryUtils.UnjoinGeometry(doc, a, b); undone++; }
                    catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or ArgumentException) { left++; }
                }
        }
        tx.Commit();
        return $"Unjoined {undone} pair{(undone == 1 ? "" : "s")} that didn't touch" + (left > 0 ? $"; {left} couldn't be unjoined." : ".");
    }
}
