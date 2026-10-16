using System.Globalization;
using Autodesk.Revit.DB;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

/// <summary>
/// The part of a sheet views may use: inside the title block's frame and clear of its title panel. In order:
///   1. a drawing area given once for the title block type and remembered in the model (<see cref="ProjectStore"/>);
///   2. the title block's own lines on the sheet (respects its visibility settings);
///   3. the lines of the title block family itself (frames drawn as filled regions, nested families or CAD imports
///      that the sheet geometry does not show), read once per session with the family opened read-only.
/// Areas are kept relative to the title block's origin, so they follow a title block that is not at the sheet origin.
/// </summary>
internal static class TitleBlockArea
{
    private const string KeyPrefix = "titleblock.drawing_area:";

    // Family-derived areas read this session, by model and type. A null value means the family was read and has no frame.
    private static readonly Dictionary<string, LayoutMath.Rect?> FromFamilyCache = new();

    private static string CacheKey(Document doc, FamilySymbol s) => (doc.PathName.Length > 0 ? doc.PathName : doc.Title) + "|" + s.UniqueId;

    public static LayoutMath.Rect? Remembered(Document doc, FamilySymbol symbol)
    {
        var text = ProjectStore.Get(doc, KeyPrefix + symbol.UniqueId);
        var parts = text?.Split(',').Select(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN).ToArray();
        return parts is { Length: 4 } && parts.All(v => !double.IsNaN(v)) ? new LayoutMath.Rect(parts[0], parts[1], parts[2], parts[3]) : null;
    }

    /// <summary>Remembers a drawing area (sheet coordinates) for the instance's type. Needs an open transaction.</summary>
    public static void Remember(Document doc, FamilyInstance titleBlock, LayoutMath.Rect sheetArea)
    {
        var o = titleBlock.GetTransform().Origin;
        var r = sheetArea.Move(-o.X, -o.Y);
        ProjectStore.Set(doc, KeyPrefix + titleBlock.Symbol.UniqueId,
            string.Join(",", new[] { r.X0, r.Y0, r.X1, r.Y1 }.Select(v => v.ToString("R", CultureInfo.InvariantCulture))));
    }

    /// <summary>The drawing area on this sheet, in sheet coordinates (feet), and where it came from.</summary>
    public static LayoutMath.Rect? Find(Document doc, ViewSheet sheet, FamilyInstance titleBlock, out string source)
    {
        var o = titleBlock.GetTransform().Origin;
        if (Remembered(doc, titleBlock.Symbol) is { } kept)
        {
            source = "remembered for this title block type";
            return kept.Move(o.X, o.Y);
        }
        if (FromSheet(sheet, titleBlock) is { } seen)
        {
            source = "title block lines on the sheet";
            return seen;
        }
        if (FromFamilyCache.TryGetValue(CacheKey(doc, titleBlock.Symbol), out var fam) && fam is { } f)
        {
            source = "title block family";
            return Place(titleBlock, f);
        }
        source = "";
        return null;
    }

    /// <summary>
    /// Reads the title block family when nothing cheaper will find the area: nothing remembered, not read before this
    /// session, and no instance of the type on a sheet whose lines show a frame. Opening a family needs no open
    /// transaction, so call this before starting one; it does nothing when the model is being modified.
    /// </summary>
    public static void Prepare(Document doc, FamilySymbol? symbol)
    {
        if (symbol is null || doc.IsModifiable || doc.IsReadOnly) return;
        if (Remembered(doc, symbol) is not null || FromFamilyCache.ContainsKey(CacheKey(doc, symbol))) return;
        var placedOne = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType()
            .OfType<FamilyInstance>().FirstOrDefault(fi => fi.Symbol.Id == symbol.Id && doc.GetElement(fi.OwnerViewId) is ViewSheet);
        if (placedOne is not null && doc.GetElement(placedOne.OwnerViewId) is ViewSheet sheet && FromSheet(sheet, placedOne) is not null) return;
        FromFamilyCache[CacheKey(doc, symbol)] = ReadFamily(doc, symbol.Family);
    }

    private static LayoutMath.Rect Place(FamilyInstance titleBlock, LayoutMath.Rect local)
    {
        var t = titleBlock.GetTransform();
        XYZ a = t.OfPoint(new XYZ(local.X0, local.Y0, 0)), b = t.OfPoint(new XYZ(local.X1, local.Y1, 0));
        return new LayoutMath.Rect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
    }

    public static LayoutMath.Rect? FromSheet(ViewSheet sheet, Element titleBlock)
    {
        var segments = new List<LayoutMath.Seg>();
        try { Collect(titleBlock.get_Geometry(new Options { View = sheet }), segments); }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { return null; }
        return LayoutMath.DrawingArea(segments, out _);
    }

    /// <summary>The drawing area in family coordinates, from every line the family draws; null when there is no frame.</summary>
    private static LayoutMath.Rect? ReadFamily(Document doc, Family family)
    {
        if (!family.IsEditable) return null;
        Document? fdoc = null;
        try
        {
            fdoc = doc.EditFamily(family);
            var segments = new List<LayoutMath.Seg>();
            foreach (var ce in new FilteredElementCollector(fdoc).OfClass(typeof(CurveElement)).Cast<CurveElement>())
                Add(ce.GeometryCurve, segments);
            foreach (var fr in new FilteredElementCollector(fdoc).OfClass(typeof(FilledRegion)).Cast<FilledRegion>())
                foreach (var loop in fr.GetBoundaries())
                    foreach (var c in loop) Add(c, segments);
            foreach (var e in new FilteredElementCollector(fdoc).WherePasses(new ElementMulticlassFilter([typeof(ImportInstance), typeof(FamilyInstance)])))
            {
                var options = fdoc.GetElement(e.OwnerViewId) is View v ? new Options { View = v } : new Options();
                try { Collect(e.get_Geometry(options), segments); }
                catch (Autodesk.Revit.Exceptions.ApplicationException) { }
            }
            return LayoutMath.DrawingArea(segments, out _);
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or InvalidOperationException or ArgumentException) { return null; }
        finally { try { fdoc?.Close(false); } catch (Autodesk.Revit.Exceptions.ApplicationException) { } }
    }

    private static void Add(Curve? c, List<LayoutMath.Seg> segments)
    {
        if (c is Line { IsBound: true } l)
        {
            XYZ a = l.GetEndPoint(0), b = l.GetEndPoint(1);
            segments.Add(new LayoutMath.Seg(a.X, a.Y, b.X, b.Y));
        }
    }

    private static void Collect(GeometryElement? geometry, List<LayoutMath.Seg> segments)
    {
        if (geometry is null) return;
        foreach (var g in geometry)
        {
            switch (g)
            {
                case Line l: Add(l, segments); break;
                case PolyLine pl:
                    var pts = pl.GetCoordinates();
                    for (int i = 0; i + 1 < pts.Count; i++) segments.Add(new LayoutMath.Seg(pts[i].X, pts[i].Y, pts[i + 1].X, pts[i + 1].Y));
                    break;
                // Frames drawn as filled regions come back as flat solids: their edges are the frame lines.
                case Solid s when s.Edges.Size > 0:
                    foreach (Edge e in s.Edges) Add(e.AsCurve(), segments);
                    break;
                case GeometryInstance gi: Collect(gi.GetInstanceGeometry(), segments); break;
            }
        }
    }
}
