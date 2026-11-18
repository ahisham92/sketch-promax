using Autodesk.Revit.DB;
using M = SketchProMax.Revit.ChangeFileMath;

namespace SketchProMax.Revit;

/// <summary>
/// Where what a replay couldn't do is: for each "not done" entry the view it shows in and the place (the grids it is
/// next to, its level), for the report and the window, and a revision cloud drawn there on request. The clouds sit on
/// one revision of their own (<see cref="ChangeFileMath.CloudRevision"/>), each with its reason as its comment, so
/// they are found in a schedule and taken out again in one go (<see cref="Uncloud"/>).
/// </summary>
internal static class ChangeWhere
{
    /// <summary>A place: the view to look in (null when none shows it) and the point (null when the line has none).</summary>
    public sealed record Spot(View? View, XYZ? At, BoundingBoxXYZ? Box, string Text);

    /// <summary>Fills <see cref="ChangeReplay.Outcome.Where"/> for every entry whose place is known. Read only.</summary>
    public static void Find(Document doc, ChangeReplay.Outcome outcome)
    {
        outcome.Where.Clear();
        var grids = Grids(doc);
        var plans = Plans(doc);
        for (var i = 0; i < outcome.NotDone.Count; i++)
        {
            if (!outcome.Place.TryGetValue(i, out var line)) continue;
            try { if (Of(doc, line, outcome, grids, plans).Text is { Length: > 0 } text) outcome.Where[i] = text; }
            catch (Exception) { /* its place stays unknown */ }
        }
    }

    /// <summary>Where a line's element is (or was): its own view when it is drawn in one, else a plan of its level.</summary>
    /// <remarks><paramref name="draw"/>: also ask a view for the box of what is drawn in it (Revit may have to draw the view first, which takes a while: only for clouds).</remarks>
    public static Spot Of(Document doc, M.Line line, ChangeReplay.Outcome outcome, List<(string Name, M.Xyz P, M.Xyz Q)>? grids = null, List<ViewPlan>? plans = null, bool draw = false)
    {
        grids ??= Grids(doc);
        plans ??= Plans(doc);
        Element? element = null;
        if (line.Unique is { Length: > 0 } unique) element = outcome.MadeFor.TryGetValue(unique, out var made) ? doc.GetElement(made) : doc.GetElement(unique);
        // A view or a sheet is its own place.
        if (element is View self) return new(self.IsTemplate ? null : self, null, null, M.WhereText(self.Name, null, null));
        View? view = null;
        XYZ? at = null;
        BoundingBoxXYZ? box = null;
        Level? level = null;
        if (element is not null)
        {
            if (element.ViewSpecific && doc.GetElement(element.OwnerViewId) is View owner) view = owner;
            at = element.Location switch
            {
                LocationPoint { Point: { } point } => point,
                LocationCurve { Curve: { IsBound: true } curve } => (curve.GetEndPoint(0) + curve.GetEndPoint(1)) / 2,
                _ => null,
            };
            if (view is null || draw) box = element.get_BoundingBox(view);
            if (box is not null) at = box.Transform.OfPoint((box.Min + box.Max) / 2);
            level = doc.GetElement(element.LevelId) as Level;
        }
        view ??= line.View is { } drawnIn ? doc.GetElement(drawnIn) as View : null;
        at ??= line.P is { } p ? (line.Q is { } q ? new XYZ((p.X + q.X) / 2, (p.Y + q.Y) / 2, (p.Z + q.Z) / 2) : new XYZ(p.X, p.Y, p.Z))
            : line.Loops is { Count: > 0 } loops && loops[0].Count > 0 && loops[0][0].P is { } first ? new XYZ(first.X, first.Y, first.Z)
            : line.Points is { Count: > 0 } points ? new XYZ(points[0].X, points[0].Y, points[0].Z) : null;
        level ??= line.LevelUnique is { } lu ? doc.GetElement(lu) as Level : null;
        level ??= line.Level is { } levelName ? new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault(l => l.Name == levelName) : null;
        if (view is { IsTemplate: true }) view = null;
        // Something of the model: a plan of its level, else the plan whose level is the nearest one under it.
        var model = view is null;
        if (view is null && at is not null)
        {
            view = level is not null ? plans.FirstOrDefault(v => v.GenLevel?.Id == level.Id) : null;
            view ??= plans.Where(v => v.GenLevel is { } l && l.Elevation <= at.Z + 0.01).OrderByDescending(v => v.GenLevel.Elevation).FirstOrDefault() ?? plans.FirstOrDefault();
        }
        // Grids only mean something in plan: for the model, and for what is drawn in a plan.
        var near = at is not null && (model || view is ViewPlan) ? M.NearGrids(new M.Xyz(at.X, at.Y, at.Z), grids) : "";
        return new(view, at, box, M.WhereText(view?.Name, near, level?.Name ?? (model ? view?.GenLevel?.Name : null)));
    }

    /// <summary>
    /// Draws a cloud at every "not done" entry whose view and place are known (one for a place, however many entries
    /// are there), on the revision <see cref="ChangeFileMath.CloudRevision"/>, each with its reason as its comment.
    /// Returns how many were drawn, in how many views, and how many entries have no place to cloud.
    /// Call with no transaction open; what Revit refuses (a view someone else has, a view that takes no clouds) is counted as no place.
    /// </summary>
    public static (int Drawn, int Views, int NoPlace) Cloud(Document doc, ChangeReplay.Outcome outcome)
    {
        var grids = Grids(doc);
        var plans = Plans(doc);
        var (drawn, noPlace) = (0, 0);
        var views = new HashSet<ElementId>();
        var done = new HashSet<string>(StringComparer.Ordinal);
        using var t = new Transaction(doc, "Sketch ProMax: clouds where the replay didn't do something");
        t.Start();
        var failures = t.GetFailureHandlingOptions();
        failures.SetFailuresPreprocessor(new ChangeShapes.Quiet());
        failures.SetClearAfterRollback(true);
        failures.SetForcedModalHandling(false);
        t.SetFailureHandlingOptions(failures);
        try
        {
            Revision? revision = null;
            for (var i = 0; i < outcome.NotDone.Count; i++)
            {
                if (!outcome.Place.TryGetValue(i, out var line)) { noPlace++; continue; }
                try
                {
                    var spot = Of(doc, line, outcome, grids, plans, draw: true);
                    if (spot.View is not { } view || spot.At is not { } at || !Takes(view)) { noPlace++; continue; }
                    // One cloud for a place: several entries about the same element (or the same point) share it.
                    if (!done.Add($"{view.Id.Value}|{Math.Round(at.X, 1)}|{Math.Round(at.Y, 1)}|{Math.Round(at.Z, 1)}")) continue;
                    revision ??= Revision(doc);
                    var cloud = RevisionCloud.Create(doc, view, revision.Id, Around(view, at, spot.Box));
                    var reason = M.Reason(outcome.NotDone[i]);
                    try { cloud.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(reason.Length > 240 ? reason.Substring(0, 240) : reason); } catch (Exception) { }
                    drawn++;
                    views.Add(view.Id);
                }
                catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or ArgumentException or InvalidOperationException) { noPlace++; }
            }
            if (drawn == 0) { t.RollBack(); return (0, 0, noPlace); }
            if (t.Commit() != TransactionStatus.Committed) return (0, 0, outcome.NotDone.Count);
        }
        catch (Exception)
        {
            if (t.GetStatus() == TransactionStatus.Started) t.RollBack();
            throw;
        }
        return (drawn, views.Count, noPlace);
    }

    /// <summary>Takes the "not done" clouds out again, and their revision when nothing else is on it. Returns how many clouds went. Call with no transaction open.</summary>
    public static int Uncloud(Document doc)
    {
        var revisions = new FilteredElementCollector(doc).OfClass(typeof(Revision)).Cast<Revision>().Where(r => r.Description == M.CloudRevision).Select(r => r.Id).ToList();
        if (revisions.Count == 0) return 0;
        var clouds = new FilteredElementCollector(doc).OfClass(typeof(RevisionCloud)).Cast<RevisionCloud>().Where(c => revisions.Contains(c.RevisionId)).Select(c => c.Id).ToList();
        using var t = new Transaction(doc, "Sketch ProMax: remove the replay's clouds");
        t.Start();
        try
        {
            if (clouds.Count > 0) doc.Delete(clouds);
            // The model keeps at least one revision: ours stays when it is the only one (empty, it shows nowhere).
            foreach (var id in revisions)
                try { if (new FilteredElementCollector(doc).OfClass(typeof(Revision)).GetElementCount() > 1) doc.Delete(id); } catch (Exception) { }
            t.Commit();
        }
        catch (Exception)
        {
            if (t.GetStatus() == TransactionStatus.Started) t.RollBack();
            throw;
        }
        return clouds.Count;
    }

    /// <summary>How many "not done" clouds the model has.</summary>
    public static int Clouds(Document doc)
    {
        var revisions = new FilteredElementCollector(doc).OfClass(typeof(Revision)).Cast<Revision>().Where(r => r.Description == M.CloudRevision).Select(r => r.Id).ToList();
        return revisions.Count == 0 ? 0 : new FilteredElementCollector(doc).OfClass(typeof(RevisionCloud)).Cast<RevisionCloud>().Count(c => revisions.Contains(c.RevisionId));
    }

    /// <summary>The views Revit draws revision clouds in.</summary>
    private static bool Takes(View view) => !view.IsTemplate && view.ViewType is ViewType.FloorPlan or ViewType.CeilingPlan or ViewType.EngineeringPlan or ViewType.AreaPlan
        or ViewType.Section or ViewType.Elevation or ViewType.Detail or ViewType.DraftingView or ViewType.DrawingSheet or ViewType.Legend;

    private static Revision Revision(Document doc)
    {
        if (new FilteredElementCollector(doc).OfClass(typeof(Revision)).Cast<Revision>().FirstOrDefault(r => r.Description == M.CloudRevision && !r.Issued) is { } there) return there;
        var revision = Autodesk.Revit.DB.Revision.Create(doc);
        revision.Description = M.CloudRevision;
        revision.RevisionDate = DateTime.Now.ToString("dd MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
        return revision;
    }

    /// <summary>A rectangle in the view's plane around the element's box (when it has one in this view) or around the point.</summary>
    private static List<Curve> Around(View view, XYZ at, BoundingBoxXYZ? box)
    {
        XYZ right = view.RightDirection, up = view.UpDirection, origin = view.Origin;
        var half = M.CloudHalf(view.Scale);
        double u0, u1, v0, v1;
        (double U, double V) Flat(XYZ p) => ((p - origin).DotProduct(right), (p - origin).DotProduct(up));
        if (box is not null)
        {
            var corners = new[] { box.Min, box.Max, new XYZ(box.Min.X, box.Max.Y, box.Min.Z), new XYZ(box.Max.X, box.Min.Y, box.Min.Z),
                new XYZ(box.Min.X, box.Min.Y, box.Max.Z), new XYZ(box.Max.X, box.Max.Y, box.Min.Z), new XYZ(box.Min.X, box.Max.Y, box.Max.Z), new XYZ(box.Max.X, box.Min.Y, box.Max.Z) }
                .Select(c => Flat(box.Transform.OfPoint(c))).ToList();
            var margin = half / 3;
            (u0, u1, v0, v1) = (corners.Min(c => c.U) - margin, corners.Max(c => c.U) + margin, corners.Min(c => c.V) - margin, corners.Max(c => c.V) + margin);
        }
        else
        {
            var (u, v) = Flat(at);
            (u0, u1, v0, v1) = (u - half, u + half, v - half, v + half);
        }
        XYZ P(double u, double v) => origin + right * u + up * v;
        return
        [
            Line.CreateBound(P(u0, v0), P(u1, v0)), Line.CreateBound(P(u1, v0), P(u1, v1)),
            Line.CreateBound(P(u1, v1), P(u0, v1)), Line.CreateBound(P(u0, v1), P(u0, v0)),
        ];
    }

    private static List<(string Name, M.Xyz P, M.Xyz Q)> Grids(Document doc)
    {
        var grids = new List<(string, M.Xyz, M.Xyz)>();
        foreach (var grid in new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>())
            try
            {
                if (grid.Curve is not { IsBound: true } curve) continue;
                var (p, q) = (curve.GetEndPoint(0), curve.GetEndPoint(1));
                grids.Add((grid.Name, new M.Xyz(p.X, p.Y, p.Z), new M.Xyz(q.X, q.Y, q.Z)));
            }
            catch (Exception) { }
        return grids;
    }

    private static List<ViewPlan> Plans(Document doc) =>
        [.. new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().Where(v => !v.IsTemplate && v.ViewType is ViewType.FloorPlan or ViewType.EngineeringPlan)];
}
