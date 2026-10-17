namespace SketchProMax.Tools;

/// <summary>
/// The wording and layout numbers of a static dimension stand-in (copy_dimensions static_fallback): a detail line with
/// ticks and text notes carrying the measured value, drawn where Revit refuses a real dimension. They never update, so
/// every one says so. No Revit here: the tests check the wording.
/// </summary>
public static class StaticDimensionText
{
    /// <summary>Put in Comments on the stand-in's lines, so they can be found (and replaced) later.</summary>
    public const string Comment = "STATIC dimension stand-in (Sketch ProMax): not a live dimension";

    /// <summary>The text next to one segment: its value, marked static.</summary>
    public static string Segment(string value) => $"{value.Trim()} (static)";

    /// <summary>The note under the stand-in saying what it is.</summary>
    public static string Note(long sourceDimension, string sourceView) =>
        $"STATIC - not a live dimension. Value measured from dimension {sourceDimension} in '{sourceView}'; it does not update when the model changes.";

    /// <summary>A length on paper (mm) in model feet at a view scale (1:<paramref name="scale"/>).</summary>
    public static double PaperToModel(double mm, int scale) => mm / 304.8 * Math.Max(1, scale);

    /// <summary>
    /// Where each segment starts and ends along the dimension line, from each segment's centre and value (Revit gives a
    /// segment's text origin at its middle). Same units in and out.
    /// </summary>
    public static List<(double Start, double End)> Spans(IReadOnlyList<(double Centre, double Value)> segments) =>
        segments.Select(s => (s.Centre - Math.Abs(s.Value) / 2, s.Centre + Math.Abs(s.Value) / 2)).ToList();
}
