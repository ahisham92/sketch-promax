using Autodesk.Revit.DB;

namespace SketchProMax.Tools;

/// <summary>
/// Draws a static stand-in for a linear dimension Revit won't recreate in another view (one on cut edges): a detail line
/// along the dimension line with a tick at each witness point, a text note with each segment's value marked "(static)",
/// and a note saying it is not a live dimension. Opt-in only (copy_dimensions static_fallback). Open transaction needed.
/// </summary>
internal static class StaticDimension
{
    /// <param name="map">From the source view's frame to the target view's (as copy_dimensions maps positions).</param>
    public static object Draw(Document doc, Dimension dim, View source, View target, Transform map)
    {
        if (dim.DimensionShape != DimensionShape.Linear || dim.Curve is not Line line)
            throw new InvalidOperationException("only straight (linear) dimensions get a static stand-in");
        var dir = line.Direction.Normalize();
        var segments = dim.NumberOfSegments > 1
            ? dim.Segments.Cast<DimensionSegment>().Select(s => (Origin: s.Origin, Value: s.Value ?? 0, Text: s.ValueString)).ToList()
            : [(Origin: dim.Origin, Value: dim.Value ?? 0, Text: dim.ValueString)];
        if (segments.Any(s => s.Origin is null || s.Value <= 0)) throw new InvalidOperationException("the dimension has no measured value to copy");

        var normal = target.ViewDirection.Normalize();
        XYZ Flat(XYZ p) => p - normal * (p - target.Origin).DotProduct(normal);
        XYZ At(double along, XYZ basePoint) => Flat(map.OfPoint(basePoint + dir * along));

        var basis = segments[0].Origin!;
        var spans = StaticDimensionText.Spans(segments.Select(s => ((s.Origin! - basis).DotProduct(dir), s.Value)).ToList());
        var mappedDir = (At(1, basis) - At(0, basis)).Normalize();
        var across = normal.CrossProduct(mappedDir).Normalize();
        var tick = StaticDimensionText.PaperToModel(1.5, target.Scale);
        var lift = StaticDimensionText.PaperToModel(1.0, target.Scale);
        var textType = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);

        var made = new List<ElementId>();
        void Curve(XYZ a, XYZ b)
        {
            var c = doc.Create.NewDetailCurve(target, Line.CreateBound(a, b));
            c.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(StaticDimensionText.Comment);
            made.Add(c.Id);
        }
        var first = At(spans.Min(s => s.Start), basis);
        var last = At(spans.Max(s => s.End), basis);
        Curve(first, last);
        foreach (var x in spans.SelectMany(s => new[] { s.Start, s.End }).Distinct())
        {
            var p = At(x, basis);
            Curve(p - across * tick, p + across * tick);
        }
        if (textType != ElementId.InvalidElementId)
        {
            for (var i = 0; i < spans.Count; i++)
                made.Add(TextNote.Create(doc, target.Id, At((spans[i].Start + spans[i].End) / 2, basis) + across * lift,
                    StaticDimensionText.Segment(segments[i].Text), textType).Id);
            made.Add(TextNote.Create(doc, target.Id, first - across * (lift * 3), StaticDimensionText.Note(dim.Id.Value, source.Name), textType).Id);
        }
        return new
        {
            dimension = dim.Id.Value, view = target.Name, elements = made.Select(i => i.Value).ToList(),
            values = segments.Select(s => s.Text).ToList(), live = false,
        };
    }
}
