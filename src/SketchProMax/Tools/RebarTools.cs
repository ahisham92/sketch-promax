using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

/// <summary>A host's own axes and its extents along them, measured from its solid geometry.</summary>
internal sealed class HostFrame
{
    public required XYZ Origin, D, W, U;
    public double D0, D1, W0, W1, U0, U1;

    public XYZ At(double d, double w, double u) => Origin + D * d + W * w + U * u;
    public double Length => D1 - D0;
    public double Width => W1 - W0;
    public double Height => U1 - U0;
    public XYZ Centre => At((D0 + D1) / 2, (W0 + W1) / 2, (U0 + U1) / 2);
}

/// <summary>Bar types, covers, host frames and bar sets shared by the reinforcement tools.</summary>
internal static class Rebars
{
    public static double Mm(double mm) => mm / 304.8;

    /// <summary>Finds a bar type by name ('T16', '16M', '#5', 'Ø16') or diameter, creating '16 mm' if none matches.</summary>
    public static RebarBarType Bar(Document doc, string spec)
    {
        var types = new FilteredElementCollector(doc).OfClass(typeof(RebarBarType)).Cast<RebarBarType>().ToList();
        var wanted = spec.Trim();
        if (types.FirstOrDefault(t => string.Equals(t.Name, wanted, StringComparison.OrdinalIgnoreCase)) is { } byName) return byName;
        var imperial = Regex.Match(wanted, @"^#\s*(\d+)$");
        var number = Regex.Match(wanted, @"(\d+(?:\.\d+)?)");
        if (!number.Success) throw new ArgumentException($"Bar '{spec}' isn't a bar type name or a diameter. Bar types: {string.Join(", ", types.Select(t => t.Name).Take(30))}.");
        var mm = imperial.Success ? int.Parse(imperial.Groups[1].Value) * 25.4 / 8
            : double.Parse(number.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var dia = Mm(mm);
        var same = types.Where(t => Math.Abs(t.BarNominalDiameter - dia) < Mm(0.2)).ToList();
        if ((same.FirstOrDefault(t => t.Name.Contains(number.Groups[1].Value)) ?? same.FirstOrDefault()) is { } match) return match;

        var created = RebarBarType.Create(doc);
        // Bend diameters may not be smaller than the bar, so widen them first, then set the bar, then the bends.
        created.StandardBendDiameter = Math.Max(4 * dia, created.StandardBendDiameter);
        created.StandardHookBendDiameter = Math.Max(4 * dia, created.StandardHookBendDiameter);
        created.StirrupTieBendDiameter = Math.Max(4 * dia, created.StirrupTieBendDiameter);
        created.BarNominalDiameter = dia;
        created.BarModelDiameter = dia;
        created.StandardBendDiameter = 4 * dia;
        created.StandardHookBendDiameter = 4 * dia;
        created.StirrupTieBendDiameter = 4 * dia;
        var name = $"{mm:0.#} mm";
        try { created.Name = name; } catch (Autodesk.Revit.Exceptions.ArgumentException) { created.Name = $"{name} (Sketch ProMax)"; }
        return created;
    }

    /// <summary>Sets the host's cover on every face, reusing a cover type of that distance or making one.</summary>
    public static void SetCover(Document doc, Element host, double cover)
    {
        if (RebarHostData.GetRebarHostData(host) is not { } data) return;
        var type = new FilteredElementCollector(doc).OfClass(typeof(RebarCoverType)).Cast<RebarCoverType>()
                       .FirstOrDefault(t => Math.Abs(t.CoverDistance - cover) < Mm(0.5))
                   ?? RebarCoverType.Create(doc, $"Cover {Math.Round(cover * 304.8)} mm", cover);
        data.SetCommonCoverType(type);
    }

    public static Element Host(Document doc, long id)
    {
        var e = doc.GetElement(new ElementId(id)) ?? throw new ArgumentException($"No element {id}.");
        if (InPlace.Is(e)) throw new ArgumentException(InPlace.Why(e));
        if (!RebarHostData.IsValidHost(e))
            throw new ArgumentException($"{e.Category?.Name} {id} can't hold rebar. It must be a structural concrete element (structural floor, wall, beam, column or foundation with a concrete material).");
        return e;
    }

    /// <summary>Measures the host's solid along three axes from the origin.</summary>
    public static HostFrame Measure(Element host, XYZ origin, XYZ d, XYZ w, XYZ u)
    {
        var f = new HostFrame { Origin = origin, D = d, W = w, U = u };
        double d0 = double.MaxValue, d1 = double.MinValue, w0 = double.MaxValue, w1 = double.MinValue, u0 = double.MaxValue, u1 = double.MinValue;
        var any = false;
        foreach (var p in Points(host.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine })))
        {
            any = true;
            var v = p - origin;
            double a = v.DotProduct(d), b = v.DotProduct(w), c = v.DotProduct(u);
            d0 = Math.Min(d0, a); d1 = Math.Max(d1, a); w0 = Math.Min(w0, b); w1 = Math.Max(w1, b); u0 = Math.Min(u0, c); u1 = Math.Max(u1, c);
        }
        if (!any) throw new ArgumentException(InPlace.Is(host) ? InPlace.Why(host) : $"Element {host.Id.Value} has no solid geometry.");
        (f.D0, f.D1, f.W0, f.W1, f.U0, f.U1) = (d0, d1, w0, w1, u0, u1);
        return f;
    }

    private static IEnumerable<XYZ> Points(GeometryElement? geometry)
    {
        if (geometry is null) yield break;
        foreach (var g in geometry)
        {
            if (g is GeometryInstance gi)
                foreach (var p in Points(gi.GetInstanceGeometry())) yield return p;
            else if (g is Solid { Volume: > 0 } solid)
                foreach (Edge e in solid.Edges)
                    foreach (var p in e.Tessellate()) yield return p;
        }
    }

    /// <summary>Beam frame: D along the beam, U up (square to the beam), W across.</summary>
    public static HostFrame BeamFrame(FamilyInstance beam)
    {
        if ((beam.Location as LocationCurve)?.Curve is not Line line)
            throw new ArgumentException($"Beam {beam.Id.Value} isn't straight; only straight beams are reinforced automatically.");
        var d = line.Direction.Normalize();
        var up = XYZ.BasisZ - d * XYZ.BasisZ.DotProduct(d);
        if (up.GetLength() < 1e-6) throw new ArgumentException($"Beam {beam.Id.Value} is vertical.");
        var u = up.Normalize();
        return Measure(beam, line.GetEndPoint(0), d, u.CrossProduct(d), u);
    }

    /// <summary>Column frame: D up the column, W and U along its faces.</summary>
    public static HostFrame ColumnFrame(FamilyInstance column)
    {
        var axis = Steel.ColumnAxis(column) ?? throw new ArgumentException($"Column {column.Id.Value} has no axis.");
        var d = (axis.GetEndPoint(1) - axis.GetEndPoint(0)).Normalize();
        if (d.Z < 0) d = -d;
        var bx = column.GetTransform().BasisX;
        var w = (bx - d * bx.DotProduct(d)).Normalize();
        return Measure(column, axis.GetEndPoint(0), d, w, d.CrossProduct(w));
    }

    /// <summary>Footing frame: D and W along the footing's sides, U up.</summary>
    public static HostFrame FootingFrame(FamilyInstance footing)
    {
        var tr = footing.GetTransform();
        var d = new XYZ(tr.BasisX.X, tr.BasisX.Y, 0).Normalize();
        return Measure(footing, tr.Origin, d, XYZ.BasisZ.CrossProduct(d), XYZ.BasisZ);
    }

    public static HostFrame FrameFor(Element host) => host switch
    {
        FamilyInstance f when f.Category?.Id.Value == (long)BuiltInCategory.OST_StructuralFraming => BeamFrame(f),
        FamilyInstance f when f.Category?.Id.Value == (long)BuiltInCategory.OST_StructuralColumns => ColumnFrame(f),
        FamilyInstance f => FootingFrame(f),
        Wall wall when (wall.Location as LocationCurve)?.Curve is Line l =>
            Measure(wall, l.GetEndPoint(0), l.Direction.Normalize(), XYZ.BasisZ.CrossProduct(l.Direction.Normalize()), XYZ.BasisZ),
        _ => Measure(host, XYZ.Zero, XYZ.BasisX, XYZ.BasisY, XYZ.BasisZ),
    };

    /// <summary>Bars without hooks from curves; Revit 2027 takes the bar ends as one object.</summary>
    private static Rebar FromCurves(Document doc, RebarStyle style, RebarBarType bar, Element host, XYZ normal, IList<Curve> curves) =>
#if REVIT2027
        Rebar.CreateFromCurves(doc, style, bar, host, normal, curves, new BarTerminationsData(doc), true, true)
#else
        Rebar.CreateFromCurves(doc, style, bar, null, null, host, normal, curves, RebarHookOrientation.Left, RebarHookOrientation.Left, true, true)
#endif
        ?? throw new InvalidOperationException(style == RebarStyle.StirrupTie
            ? "Revit couldn't match or make a closed link shape. Load the rebar shapes (e.g. from a structural template) and try again, or leave out the links."
            : "Revit couldn't match or make a straight bar shape. Load the rebar shapes from a structural template and try again.");

    /// <summary>A row of straight bars from a to b, spread over 'spread' along 'across' (count bars, or one bar).</summary>
    public static Rebar Straight(Document doc, Element host, RebarBarType bar, XYZ a, XYZ b, XYZ across, int count, double spread)
    {
        var rebar = FromCurves(doc, RebarStyle.Standard, bar, host, across, new List<Curve> { Line.CreateBound(a, b) });
        if (count > 1)
        {
            if (spread <= Mm(1)) throw new ArgumentException($"there is no room for {count} bars across this face; use fewer or smaller bars");
            rebar.GetShapeDrivenAccessor().SetLayoutAsFixedNumber(count, spread, true, true, true);
        }
        return rebar;
    }

    /// <summary>Straight bars from a to b repeated every 'spacing' (at most) over 'spread' along 'across'.</summary>
    public static Rebar Spaced(Document doc, Element host, RebarBarType bar, XYZ a, XYZ b, XYZ across, double spacing, double spread)
    {
        var rebar = FromCurves(doc, RebarStyle.Standard, bar, host, across, new List<Curve> { Line.CreateBound(a, b) });
        if (spread > spacing / 2) rebar.GetShapeDrivenAccessor().SetLayoutAsMaximumSpacing(spacing, spread, true, true, true);
        return rebar;
    }

    /// <summary>Closed links (stirrups or ties) round a rectangle, repeated along 'along' every 'spacing' over 'spread'.</summary>
    public static Rebar Links(Document doc, Element host, RebarBarType bar, XYZ[] corners, XYZ along, double spacing, double spread)
    {
        var curves = new List<Curve>();
        for (var i = 0; i < corners.Length; i++) curves.Add(Line.CreateBound(corners[i], corners[(i + 1) % corners.Length]));
        var rebar = FromCurves(doc, RebarStyle.StirrupTie, bar, host, along, curves);
        if (spread > spacing / 2) rebar.GetShapeDrivenAccessor().SetLayoutAsMaximumSpacing(spacing, spread, true, true, true);
        return rebar;
    }

    public static (RebarBarType bar, int count)? Row(Document doc, JsonElement? spec, int fallbackCount)
    {
        if (spec is not { ValueKind: JsonValueKind.Object } s) return null;
        var bar = Bar(doc, s.TryGetProperty("bar", out var b) ? b.ToString() : throw new ArgumentException("each bar row needs 'bar' (e.g. 'T16' or '16')"));
        var count = s.TryGetProperty("count", out var c) ? c.GetInt32() : fallbackCount;
        if (count < 1) throw new ArgumentException("count must be at least 1");
        return (bar, count);
    }

    public static (RebarBarType bar, double spacing)? Spacing(Document doc, JsonElement? spec, double fallbackSpacing)
    {
        if (spec is not { ValueKind: JsonValueKind.Object } s) return null;
        var bar = Bar(doc, s.TryGetProperty("bar", out var b) ? b.ToString() : throw new ArgumentException("each layer needs 'bar' (e.g. 'T10' or '10')"));
        var spacing = s.TryGetProperty("spacing", out var sp) ? RevitHelpers.ToInternal(doc, sp.GetDouble()) : fallbackSpacing;
        if (spacing < Mm(25)) throw new ArgumentException("spacing is too small (project units, e.g. 150 for 150 mm)");
        return (bar, spacing);
    }

    public static JsonElement? Opt(ToolArgs args, string key) => args.Has(key) ? args.Raw[key] : null;

    /// <summary>
    /// Runs one host's work in a sub-transaction, so a host Revit refuses leaves nothing half made. A stop request
    /// (cancel_job) takes effect between hosts: the rest are listed as not done and the finished ones are kept.
    /// </summary>
    public static void PerHost(Document doc, IEnumerable<long> ids, Action<Element, List<long>> work, List<object> done, List<object> errors, bool assumed = false)
    {
        var list = ids.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var id = list[i];
            if (CancelPoint.Stop("Placing rebar", i, list.Count)) { errors.Add(new { host = id, error = "not done: stopped on request (cancel_job)" }); continue; }
            using var sub = new SubTransaction(doc);
            sub.Start();
            try
            {
                var host = Host(doc, id);
                var made = new List<long>();
                work(host, made);
                // Typical values not from a design: say so on every bar, so schedules and tags show it.
                if (assumed)
                    foreach (var r in made)
                        doc.GetElement(new ElementId(r))?.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(AssumedText);
                sub.Commit();
                done.Add(new { host = id, rebarIds = made });
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Autodesk.Revit.Exceptions.ApplicationException)
            {
                if (sub.HasStarted() && !sub.HasEnded()) sub.RollBack();
                errors.Add(new { host = id, error = ex.Message.Trim() });
            }
        }
    }

    public const string AssumedText = "ASSUMED - typical reinforcement, to be confirmed by design";

    public static ToolResult Result(Transaction t, List<object> done, List<object> errors, string what, bool assumed = false)
    {
        if (done.Count == 0) { t.RollBack(); return ToolResult.Error($"No {what} was placed: {JsonSerializer.Serialize(errors)}"); }
        if (t.Commit() != TransactionStatus.Committed) return ToolResult.Error($"Revit refused the {what} when saving it, so none was placed.");
        return ToolResult.Ok(new
        {
            reinforced = done.Count, hosts = done, errors,
            assumed = assumed ? $"Bars are marked '{AssumedText}' in Comments. Put a note on the reinforcement sheets: 'Reinforcement shown is assumed typical; to be confirmed by the structural design.'" : null,
        });
    }
}

public sealed class ReinforceBeamTool : IRevitTool
{
    public string Name => "reinforce_beam";
    public string Description =>
        "Put rebar in concrete beams from their real shape: bottom and top bars (count + bar size), optional side bars, and " +
        "closed stirrups at a spacing, with the cover set on the beam. Bars run the beam's clear length between supports. " +
        "Bar sizes are bar type names ('T16', '16M', '#5') or diameters in mm ('16'); a missing size is created.";
    public string PropertiesJson => """
    {
      "element_ids": {"type": "array", "items": {"type": "integer"}, "description": "Concrete beams."},
      "bottom": {"type": "object", "properties": {"bar": {"type": "string"}, "count": {"type": "integer"}}, "description": "e.g. {bar:'T16', count:3}"},
      "top": {"type": "object", "properties": {"bar": {"type": "string"}, "count": {"type": "integer"}}},
      "side": {"type": "object", "properties": {"bar": {"type": "string"}, "count": {"type": "integer"}}, "description": "Bars on each side face between top and bottom (count per face)."},
      "stirrups": {"type": "object", "properties": {"bar": {"type": "string"}, "spacing": {"type": "number"}}, "description": "e.g. {bar:'T10', spacing:150} (project units)."},
      "cover": {"type": "number", "description": "Clear cover, project units (default 40 mm)."},
      "assumed": {"type": "boolean", "description": "Assumed typical values, not from a design: every bar's Comments says ASSUMED (tag/schedule it and note it on the sheet)."}
    }
    """;
    public string[] Required => ["element_ids"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var cover = args.OptDouble("cover") is { } cv ? RevitHelpers.ToInternal(doc, cv) : Rebars.Mm(40);
        var done = new List<object>(); var errors = new List<object>();
        using var t = RevitHelpers.StartTransaction(doc, "Reinforce beams");
        var bottom = Rebars.Row(doc, Rebars.Opt(args, "bottom"), 3);
        var top = Rebars.Row(doc, Rebars.Opt(args, "top"), 2);
        var side = Rebars.Row(doc, Rebars.Opt(args, "side"), 1);
        var links = Rebars.Spacing(doc, Rebars.Opt(args, "stirrups"), Rebars.Mm(200));
        if (bottom is null && top is null && links is null) throw new ArgumentException("Give at least one of bottom, top or stirrups.");

        Rebars.PerHost(doc, args.LongList("element_ids"), (host, made) =>
        {
            if (host is not FamilyInstance beam || beam.Category?.Id.Value != (long)BuiltInCategory.OST_StructuralFraming)
                throw new ArgumentException("not a structural beam");
            Rebars.SetCover(doc, host, cover);
            var f = Rebars.BeamFrame(beam);
            var ds = links?.bar.BarModelDiameter ?? 0;
            double end0 = f.D0 + cover, end1 = f.D1 - cover;
            void Face((RebarBarType bar, int count)? row, bool isTop)
            {
                if (row is not { } r) return;
                var inset = cover + ds + r.bar.BarModelDiameter / 2;
                var u = isTop ? f.U1 - inset : f.U0 + inset;
                var w = r.count == 1 ? (f.W0 + f.W1) / 2 : f.W0 + inset;
                made.Add(Rebars.Straight(doc, host, r.bar, f.At(end0, w, u), f.At(end1, w, u), f.W, r.count, f.Width - 2 * inset).Id.Value);
            }
            Face(bottom, false);
            Face(top, true);
            if (side is { } s)
            {
                var inset = cover + ds + s.bar.BarModelDiameter / 2;
                var gap = (f.Height - 2 * inset) / (s.count + 1);
                foreach (var w in new[] { f.W0 + inset, f.W1 - inset })
                    made.Add(Rebars.Straight(doc, host, s.bar, f.At(end0, w, f.U0 + inset + gap), f.At(end1, w, f.U0 + inset + gap), f.U, s.count, gap * (s.count - 1)).Id.Value);
            }
            if (links is { } l)
            {
                var inset = cover + ds / 2;
                var first = f.D0 + Rebars.Mm(50);
                var corners = new[] { f.At(first, f.W0 + inset, f.U0 + inset), f.At(first, f.W1 - inset, f.U0 + inset), f.At(first, f.W1 - inset, f.U1 - inset), f.At(first, f.W0 + inset, f.U1 - inset) };
                made.Add(Rebars.Links(doc, host, l.bar, corners, f.D, l.spacing, f.Length - Rebars.Mm(100)).Id.Value);
            }
        }, done, errors, args.OptBool("assumed"));
        return Rebars.Result(t, done, errors, "beam rebar", args.OptBool("assumed"));
    }
}

public sealed class ReinforceColumnTool : IRevitTool
{
    public string Name => "reinforce_column";
    public string Description =>
        "Put rebar in rectangular concrete columns: vertical bars round the perimeter (bars per face in each direction) " +
        "and closed ties at a spacing, with the cover set on the column. Bar sizes as in reinforce_beam.";
    public string PropertiesJson => """
    {
      "element_ids": {"type": "array", "items": {"type": "integer"}, "description": "Concrete columns."},
      "bar": {"type": "string", "description": "Vertical bar size, e.g. 'T20'."},
      "bars_x": {"type": "integer", "description": "Bars on each face along the column's width, corners included (default 3)."},
      "bars_y": {"type": "integer", "description": "Bars on each face along the column's depth, corners included (default 3)."},
      "ties": {"type": "object", "properties": {"bar": {"type": "string"}, "spacing": {"type": "number"}}, "description": "e.g. {bar:'T10', spacing:200}."},
      "cover": {"type": "number", "description": "Clear cover, project units (default 40 mm)."},
      "assumed": {"type": "boolean", "description": "Assumed typical values, not from a design: every bar's Comments says ASSUMED (tag/schedule it and note it on the sheet)."}
    }
    """;
    public string[] Required => ["element_ids", "bar"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var cover = args.OptDouble("cover") is { } cv ? RevitHelpers.ToInternal(doc, cv) : Rebars.Mm(40);
        var nx = (int)(args.OptLong("bars_x") ?? 3);
        var ny = (int)(args.OptLong("bars_y") ?? 3);
        if (nx < 2 || ny < 2) throw new ArgumentException("bars_x and bars_y count the corner bars, so each must be at least 2.");
        var done = new List<object>(); var errors = new List<object>();
        using var t = RevitHelpers.StartTransaction(doc, "Reinforce columns");
        var bar = Rebars.Bar(doc, args.String("bar"));
        var ties = Rebars.Spacing(doc, Rebars.Opt(args, "ties"), Rebars.Mm(200));

        Rebars.PerHost(doc, args.LongList("element_ids"), (host, made) =>
        {
            if (host is not FamilyInstance column || column.Category?.Id.Value != (long)BuiltInCategory.OST_StructuralColumns)
                throw new ArgumentException("not a structural column");
            Rebars.SetCover(doc, host, cover);
            var f = Rebars.ColumnFrame(column);
            var dt = ties?.bar.BarModelDiameter ?? 0;
            var inset = cover + dt + bar.BarModelDiameter / 2;
            double end0 = f.D0 + cover, end1 = f.D1 - cover;
            // Two faces with the corner bars, then the bars between the corners on the other two faces.
            foreach (var u in new[] { f.U0 + inset, f.U1 - inset })
                made.Add(Rebars.Straight(doc, host, bar, f.At(end0, f.W0 + inset, u), f.At(end1, f.W0 + inset, u), f.W, nx, f.Width - 2 * inset).Id.Value);
            if (ny > 2)
            {
                var gap = (f.Height - 2 * inset) / (ny - 1);
                foreach (var w in new[] { f.W0 + inset, f.W1 - inset })
                    made.Add(Rebars.Straight(doc, host, bar, f.At(end0, w, f.U0 + inset + gap), f.At(end1, w, f.U0 + inset + gap), f.U, ny - 2, gap * (ny - 3)).Id.Value);
            }
            if (ties is { } l)
            {
                var ti = cover + dt / 2;
                var first = f.D0 + Rebars.Mm(50);
                var corners = new[] { f.At(first, f.W0 + ti, f.U0 + ti), f.At(first, f.W1 - ti, f.U0 + ti), f.At(first, f.W1 - ti, f.U1 - ti), f.At(first, f.W0 + ti, f.U1 - ti) };
                made.Add(Rebars.Links(doc, host, l.bar, corners, f.D, l.spacing, f.Length - Rebars.Mm(100)).Id.Value);
            }
        }, done, errors, args.OptBool("assumed"));
        return Rebars.Result(t, done, errors, "column rebar", args.OptBool("assumed"));
    }
}

public sealed class ReinforceFootingTool : IRevitTool
{
    public string Name => "reinforce_footing";
    public string Description =>
        "Put bottom (and optionally top) mats of bars both ways in isolated concrete footings and pile caps, at a spacing, " +
        "with the cover set on the footing. For raft or strip footings and slabs use reinforce_slab_wall.";
    public string PropertiesJson => """
    {
      "element_ids": {"type": "array", "items": {"type": "integer"}, "description": "Isolated footings / pile caps."},
      "bottom": {"type": "object", "properties": {"bar": {"type": "string"}, "spacing": {"type": "number"}}, "description": "e.g. {bar:'T16', spacing:150}."},
      "top": {"type": "object", "properties": {"bar": {"type": "string"}, "spacing": {"type": "number"}}},
      "cover": {"type": "number", "description": "Clear cover, project units (default 75 mm)."},
      "assumed": {"type": "boolean", "description": "Assumed typical values, not from a design: every bar's Comments says ASSUMED (tag/schedule it and note it on the sheet)."}
    }
    """;
    public string[] Required => ["element_ids", "bottom"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var cover = args.OptDouble("cover") is { } cv ? RevitHelpers.ToInternal(doc, cv) : Rebars.Mm(75);
        var done = new List<object>(); var errors = new List<object>();
        using var t = RevitHelpers.StartTransaction(doc, "Reinforce footings");
        var bottom = Rebars.Spacing(doc, Rebars.Opt(args, "bottom"), Rebars.Mm(150)) ?? throw new ArgumentException("bottom must be an object like {bar:'T16', spacing:150}.");
        var top = Rebars.Spacing(doc, Rebars.Opt(args, "top"), Rebars.Mm(150));

        Rebars.PerHost(doc, args.LongList("element_ids"), (host, made) =>
        {
            if (host is not FamilyInstance footing || footing.Category?.Id.Value != (long)BuiltInCategory.OST_StructuralFoundation)
                throw new ArgumentException("not an isolated footing (use reinforce_slab_wall for slabs, rafts and wall footings)");
            Rebars.SetCover(doc, host, cover);
            var f = Rebars.FootingFrame(footing);
            void Mat((RebarBarType bar, double spacing) m, bool isTop)
            {
                var db = m.bar.BarModelDiameter;
                var e = cover + db / 2;
                // First direction outermost, second direction just inside it.
                var u1 = isTop ? f.U1 - e : f.U0 + e;
                var u2 = isTop ? u1 - db : u1 + db;
                made.Add(Rebars.Spaced(doc, host, m.bar, f.At(f.D0 + cover, f.W0 + e, u1), f.At(f.D1 - cover, f.W0 + e, u1), f.W, m.spacing, f.Width - 2 * e).Id.Value);
                made.Add(Rebars.Spaced(doc, host, m.bar, f.At(f.D0 + e, f.W0 + cover, u2), f.At(f.D0 + e, f.W1 - cover, u2), f.D, m.spacing, f.Length - 2 * e).Id.Value);
            }
            Mat(bottom, false);
            if (top is { } tp) Mat(tp, true);
        }, done, errors, args.OptBool("assumed"));
        return Rebars.Result(t, done, errors, "footing rebar", args.OptBool("assumed"));
    }
}

public sealed class ReinforceSlabWallTool : IRevitTool
{
    public string Name => "reinforce_slab_wall";
    public string Description =>
        "Area reinforcement over whole structural floors, foundation slabs/rafts and concrete walls: top and bottom mats " +
        "(slabs) or exterior and interior faces (walls), each both ways at a bar size and spacing, with the cover set. " +
        "Shown in plan as area reinforcement; bars are counted in export_bar_schedule.";
    public string PropertiesJson => """
    {
      "element_ids": {"type": "array", "items": {"type": "integer"}, "description": "Structural floors, foundation slabs or walls."},
      "top": {"type": "object", "properties": {"bar": {"type": "string"}, "spacing": {"type": "number"}}, "description": "Slabs: top mat, e.g. {bar:'T12', spacing:200}."},
      "bottom": {"type": "object", "properties": {"bar": {"type": "string"}, "spacing": {"type": "number"}}, "description": "Slabs: bottom mat."},
      "exterior": {"type": "object", "properties": {"bar": {"type": "string"}, "spacing": {"type": "number"}}, "description": "Walls: exterior face."},
      "interior": {"type": "object", "properties": {"bar": {"type": "string"}, "spacing": {"type": "number"}}, "description": "Walls: interior face."},
      "major_direction": {"type": "array", "items": {"type": "number"}, "description": "Slabs: [x, y] direction of the main bars (default along X)."},
      "cover": {"type": "number", "description": "Clear cover, project units (default 25 mm slabs, 40 mm walls, 75 mm foundations)."},
      "assumed": {"type": "boolean", "description": "Assumed typical values, not from a design: every bar's Comments says ASSUMED (tag/schedule it and note it on the sheet)."}
    }
    """;
    public string[] Required => ["element_ids"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var done = new List<object>(); var errors = new List<object>();
        using var t = RevitHelpers.StartTransaction(doc, "Area reinforcement");
        var layers = new Dictionary<string, (RebarBarType bar, double spacing)?>
        {
            ["TOP"] = Rebars.Spacing(doc, Rebars.Opt(args, "top"), Rebars.Mm(200)),
            ["BOTTOM"] = Rebars.Spacing(doc, Rebars.Opt(args, "bottom"), Rebars.Mm(200)),
            ["FRONT"] = Rebars.Spacing(doc, Rebars.Opt(args, "exterior"), Rebars.Mm(200)),
            ["BACK"] = Rebars.Spacing(doc, Rebars.Opt(args, "interior"), Rebars.Mm(200)),
        };
        if (layers.Values.All(v => v is null)) throw new ArgumentException("Give top/bottom (slabs) or exterior/interior (walls).");
        var areaType = new FilteredElementCollector(doc).OfClass(typeof(AreaReinforcementType)).FirstElementId() is { } at && at != ElementId.InvalidElementId
            ? at : AreaReinforcementType.CreateDefaultAreaReinforcementType(doc);
        var major = args.Has("major_direction") ? Modeling.Point(doc, args.Raw["major_direction"]) : XYZ.BasisX;
        major = new XYZ(major.X, major.Y, 0);
        if (major.GetLength() < 1e-9) major = XYZ.BasisX;

        Rebars.PerHost(doc, args.LongList("element_ids"), (host, made) =>
        {
            var isWall = host is Wall;
            var isFoundation = host.Category?.Id.Value == (long)BuiltInCategory.OST_StructuralFoundation;
            var faces = isWall ? new[] { "FRONT", "BACK" } : new[] { "TOP", "BOTTOM" };
            if (faces.All(k => layers[k] is null))
                throw new ArgumentException(isWall ? "walls take exterior/interior layers" : "slabs take top/bottom layers");
            var cover = args.OptDouble("cover") is { } cv ? RevitHelpers.ToInternal(doc, cv) : Rebars.Mm(isWall ? 40 : isFoundation ? 75 : 25);
            Rebars.SetCover(doc, host, cover);
            var direction = isWall && (host.Location as LocationCurve)?.Curve is Line wl ? wl.Direction.Normalize() : major.Normalize();
            var firstBar = faces.Select(k => layers[k]).First(v => v is not null)!.Value.bar;
            var area = AreaReinforcement.Create(doc, host, direction, areaType, firstBar.Id, ElementId.InvalidElementId);
            foreach (var face in faces)
                foreach (var dir in new[] { "1", "2" })
                {
                    var layer = layers[face];
                    // Walls name their faces FRONT/BACK; if this Revit uses TOP/BOTTOM for walls, fall back to those.
                    var name = face;
                    if (area.get_Parameter(Param($"REBAR_SYSTEM_ACTIVE_{face}_DIR_{dir}")) is null && isWall) name = face == "FRONT" ? "TOP" : "BOTTOM";
                    Set(area, $"REBAR_SYSTEM_ACTIVE_{name}_DIR_{dir}", layer is null ? 0 : 1);
                    if (layer is not { } l) continue;
                    Set(area, $"REBAR_SYSTEM_BAR_TYPE_{name}_DIR_{dir}", l.bar.Id);
                    Set(area, $"REBAR_SYSTEM_SPACING_{name}_DIR_{dir}", l.spacing);
                }
            made.Add(area.Id.Value);
        }, done, errors, args.OptBool("assumed"));
        return Rebars.Result(t, done, errors, "area reinforcement", args.OptBool("assumed"));
    }

    private static BuiltInParameter Param(string name) => (BuiltInParameter)Enum.Parse(typeof(BuiltInParameter), name);

    private static void Set(Element e, string builtIn, object value)
    {
        var p = e.get_Parameter(Param(builtIn));
        var ok = p is { IsReadOnly: false } && value switch
        {
            int i => p.Set(i),
            double d => p.Set(d),
            ElementId id => p.Set(id),
            _ => false,
        };
        if (!ok) throw new InvalidOperationException($"couldn't set the area reinforcement's {builtIn.Replace("REBAR_SYSTEM_", "").Replace('_', ' ').ToLowerInvariant()}");
    }
}

public sealed class RebarSectionTool : IRevitTool
{
    public string Name => "rebar_section";
    public string Description =>
        "Draw reinforcement: cut a cross or long section through each reinforced beam, column, wall or footing (or use an " +
        "existing plan/section view), show its rebar unobscured at Fine detail, tag the bars, add a note of the bars and " +
        "cover, and optionally place the sections on a sheet.";
    public string PropertiesJson => """
    {
      "element_ids": {"type": "array", "items": {"type": "integer"}, "description": "Reinforced hosts."},
      "cut": {"type": "string", "enum": ["cross", "long"], "description": "Beams: cross (default) or long section. Columns and walls always get a long section."},
      "view_id": {"type": "integer", "description": "Instead of new sections, show and tag the hosts' rebar in this existing view (e.g. a plan for slab rebar)."},
      "scale": {"type": "integer", "description": "Default 20 for cross sections, 50 otherwise."},
      "name_pattern": {"type": "string", "description": "e.g. 'RC {mark} {cut}' (tokens {id}, {mark}, {cut}; default 'Rebar {id} {cut}')."},
      "tag": {"type": "boolean", "description": "Tag each bar set (default true; needs a rebar tag family loaded)."},
      "note": {"type": "boolean", "description": "Add a text note listing bars and cover (default true)."},
      "sheet_id": {"type": "integer", "description": "Place the new sections on this sheet."}
    }
    """;
    public string[] Required => ["element_ids"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var cut = args.OptString("cut") ?? "cross";
        var tag = args.OptBool("tag", true);
        var note = args.OptBool("note", true);
        var existing = args.OptLong("view_id") is { } vid ? doc.GetElement(new ElementId(vid)) as View ?? throw new ArgumentException($"{vid} is not a view.") : null;
        var results = new List<object>(); var errors = new List<object>();
        var views = new List<View>();
        var tagNote = (string?)null;
        var sheet = args.OptLong("sheet_id") is { } sid ? doc.GetElement(new ElementId(sid)) as ViewSheet ?? throw new ArgumentException($"{sid} is not a sheet.") : null;
        var byHost = new FilteredElementCollector(doc).OfClass(typeof(Rebar)).Cast<Rebar>().Select(r => (host: r.GetHostId(), bar: (Element)r))
            .Concat(new FilteredElementCollector(doc).OfClass(typeof(RebarInSystem)).Cast<RebarInSystem>().Select(r => (host: r.GetHostId(), bar: (Element)r)))
            .ToLookup(x => x.host.Value, x => x.bar);

        using var t = RevitHelpers.StartTransaction(doc, "Rebar sections");
        foreach (var id in args.LongList("element_ids"))
        {
            using var sub = new SubTransaction(doc);
            sub.Start();
            try
            {
                var host = doc.GetElement(new ElementId(id)) ?? throw new ArgumentException("no such element");
                var bars = byHost[host.Id.Value].ToList();
                if (bars.Count == 0) throw new ArgumentException("has no rebar yet (use reinforce_beam, reinforce_column, reinforce_footing or reinforce_slab_wall)");
                var f = Rebars.FrameFor(host);
                var isBeam = host.Category?.Id.Value == (long)BuiltInCategory.OST_StructuralFraming;
                var thisCut = isBeam ? cut : "long";
                View view;
                if (existing != null) view = existing;
                else
                {
                    view = ViewSection.CreateSection(doc, ViewKit.TypeFor(doc, ViewFamily.Section).Id, Box(f, thisCut == "cross"));
                    var mark = host.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString();
                    var name = (args.OptString("name_pattern") ?? "Rebar {id} {cut}").Replace("{id}", id.ToString())
                        .Replace("{mark}", string.IsNullOrWhiteSpace(mark) ? id.ToString() : mark).Replace("{cut}", thisCut == "cross" ? "Section" : "Long section");
                    view.ViewTemplateId = ElementId.InvalidElementId; // a template from the view type would lock detail level and scale
                    ViewKit.Finish(doc, view, name, args.OptLong("scale") ?? (thisCut == "cross" ? 20 : 50), null);
                    view.DetailLevel = ViewDetailLevel.Fine;
                    views.Add(view);
                }
                foreach (var r in bars.OfType<Rebar>()) r.SetUnobscuredInView(view, true);
                foreach (var r in bars.OfType<RebarInSystem>()) r.SetUnobscuredInView(view, true);
                doc.Regenerate();
                var tagged = 0;
                if (tag)
                    foreach (var r in bars)
                    {
                        try
                        {
                            var bb = r.get_BoundingBox(view) ?? r.get_BoundingBox(null);
                            if (bb is null) continue;
                            IndependentTag.Create(doc, view.Id, new Reference(r), true, TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal,
                                (bb.Min + bb.Max) / 2 + view.RightDirection * Rebars.Mm(300) + view.UpDirection * Rebars.Mm(200 + 150 * tagged));
                            tagged++;
                        }
                        catch (Autodesk.Revit.Exceptions.ApplicationException ex) { tagNote = $"Bars weren't tagged: {ex.Message.Trim()} Load a rebar tag family (Structural Rebar Tag)."; break; }
                    }
                if (note)
                {
                    var text = Summary(doc, host, bars);
                    double Half(XYZ dir) => Math.Abs(dir.DotProduct(f.D)) * f.Length / 2 + Math.Abs(dir.DotProduct(f.W)) * f.Width / 2 + Math.Abs(dir.DotProduct(f.U)) * f.Height / 2;
                    var at = f.Centre - view.UpDirection * (Half(view.UpDirection) + Rebars.Mm(400)) - view.RightDirection * Half(view.RightDirection);
                    TextNote.Create(doc, view.Id, at, text, doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType));
                }
                sub.Commit();
                results.Add(new { host = id, viewId = view.Id.Value, view = view.Name, bars = bars.Count, tagged });
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Autodesk.Revit.Exceptions.ApplicationException)
            {
                if (sub.HasStarted() && !sub.HasEnded()) sub.RollBack();
                errors.Add(new { host = id, error = ex.Message.Trim() });
            }
        }
        if (results.Count == 0) { t.RollBack(); return ToolResult.Error($"Nothing was drawn: {JsonSerializer.Serialize(errors)}"); }

        var placed = new List<long>();
        if (sheet != null && views.Count > 0)
        {
            var outline = sheet.Outline;
            var cols = Math.Min(4, views.Count);
            var rows = (views.Count + cols - 1) / cols;
            double cw = (outline.Max.U - outline.Min.U) * 0.8 / cols, ch = (outline.Max.V - outline.Min.V) * 0.8 / rows;
            for (var i = 0; i < views.Count; i++)
            {
                var centre = new XYZ(outline.Min.U + (outline.Max.U - outline.Min.U) * 0.1 + cw * (i % cols + 0.5),
                                     outline.Max.V - (outline.Max.V - outline.Min.V) * 0.1 - ch * (i / cols + 0.5), 0);
                if (Viewport.CanAddViewToSheet(doc, sheet.Id, views[i].Id)) placed.Add(Viewport.Create(doc, sheet.Id, views[i].Id, centre).Id.Value);
            }
        }
        if (t.Commit() != TransactionStatus.Committed) return ToolResult.Error("Revit refused the rebar sections when saving them.");
        return ToolResult.Ok(new { drawn = results, errors, viewportsPlaced = placed.Count, note = tagNote });
    }

    /// <summary>A section box through the middle of the host: across it (looking along it) or along it (looking across).</summary>
    private static BoundingBoxXYZ Box(HostFrame f, bool cross)
    {
        var margin = Rebars.Mm(300);
        var tr = Transform.Identity;
        tr.Origin = f.Centre;
        XYZ x, y;
        double halfX, halfY, depth;
        // Deep enough to see at least one stirrup behind the cut at usual spacings.
        if (cross) { x = f.W; y = f.U; halfX = f.Width / 2; halfY = f.Height / 2; depth = Rebars.Mm(400); }
        else if (Math.Abs(f.D.Z) > 0.7) { x = f.W; y = f.D; halfX = f.Width / 2; halfY = f.Length / 2; depth = f.Height / 2 + margin; } // columns
        else { x = f.D; y = f.U; halfX = f.Length / 2; halfY = f.Height / 2; depth = f.Width / 2 + margin; }
        tr.BasisX = x;
        tr.BasisY = y;
        tr.BasisZ = x.CrossProduct(y);
        return new BoundingBoxXYZ { Transform = tr, Min = new XYZ(-halfX - margin, -halfY - margin, -depth), Max = new XYZ(halfX + margin, halfY + margin, 0) };
    }

    private static string Summary(Document doc, Element host, List<Element> bars)
    {
        var lines = bars.GroupBy(b => (doc.GetElement(b.GetTypeId())?.Name ?? "?", b is Rebar r ? r.Quantity : (b as RebarInSystem)?.Quantity ?? 0,
                                        b.get_Parameter(BuiltInParameter.REBAR_ELEM_BAR_SPACING)?.AsDouble() ?? 0))
            .Select(g => g.Key.Item3 > 0 && g.Key.Item2 > 2
                ? $"{g.Count() * g.Key.Item2} x {g.Key.Item1} @ {Math.Round(g.Key.Item3 * 304.8)} mm"
                : $"{g.Count() * g.Key.Item2} x {g.Key.Item1}");
        var cover = RebarHostData.GetRebarHostData(host)?.GetCommonCoverType() is { } ct ? $"Cover {Math.Round(ct.CoverDistance * 304.8)} mm" : "";
        return string.Join("\n", lines.Append(cover).Where(s => s.Length > 0));
    }
}

public sealed class Rebar3dViewTool : IRevitTool
{
    public string Name => "rebar_3d_view";
    public string Description =>
        "Make a 3D view of reinforcement: section box round the given hosts (or every reinforced element on a level), " +
        "Fine detail, bars shown unobscured through see-through concrete.";
    public string PropertiesJson => """
    {
      "element_ids": {"type": "array", "items": {"type": "integer"}, "description": "Reinforced hosts (default: all hosts with rebar)."},
      "level": {"type": "string", "description": "Only hosts on this level."},
      "name": {"type": "string"},
      "transparency": {"type": "integer", "description": "Concrete transparency 0-100 (default 70)."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var rebar = new FilteredElementCollector(doc).OfClass(typeof(Rebar)).Cast<Rebar>().ToList();
        var ids = args.LongList("element_ids").Select(i => new ElementId(i)).ToHashSet();
        var hostIds = ids.Count > 0 ? ids
            : rebar.Select(r => r.GetHostId()).Concat(new FilteredElementCollector(doc).OfClass(typeof(AreaReinforcement)).Cast<AreaReinforcement>().Select(a => a.GetHostId())).ToHashSet();
        var hosts = hostIds.Select(doc.GetElement).Where(e => e != null).ToList();
        if (args.OptString("level") is { } levelName)
        {
            var level = RevitHelpers.FindLevel(doc, levelName);
            hosts = hosts.Where(h => h.LevelId == level.Id || h.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM)?.AsElementId() == level.Id
                                     || h.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_PARAM)?.AsElementId() == level.Id).ToList();
        }
        if (hosts.Count == 0) return ToolResult.Error("No reinforced elements found.");
        var box = ViewKit.Extents(doc, hosts) ?? throw new ArgumentException("The hosts have no geometry.");
        var margin = Rebars.Mm(500);

        using var t = RevitHelpers.StartTransaction(doc, "Rebar 3D view");
        var view = View3D.CreateIsometric(doc, ViewKit.TypeFor(doc, ViewFamily.ThreeDimensional).Id);
        view.ViewTemplateId = ElementId.InvalidElementId;
        ViewKit.Finish(doc, view, args.OptString("name") ?? "Rebar 3D", null, null);
        view.SetSectionBox(new BoundingBoxXYZ { Min = box.Min - new XYZ(margin, margin, margin), Max = box.Max + new XYZ(margin, margin, margin) });
        view.DetailLevel = ViewDetailLevel.Fine;
        view.DisplayStyle = DisplayStyle.ShadingWithEdges;
        var see = new OverrideGraphicSettings().SetSurfaceTransparency((int)Math.Max(0, Math.Min(100, args.OptLong("transparency") ?? 70)));
        foreach (var cat in hosts.Select(h => h.Category).Where(c => c != null).GroupBy(c => c.Id.Value).Select(g => g.First()))
            if (view.IsCategoryOverridable(cat.Id)) view.SetCategoryOverrides(cat.Id, see);
        var shown = 0;
        foreach (var r in rebar.Where(r => hostIds.Contains(r.GetHostId()))) { r.SetUnobscuredInView(view, true); shown++; }
        foreach (var r in new FilteredElementCollector(doc).OfClass(typeof(RebarInSystem)).Cast<RebarInSystem>().Where(r => hostIds.Contains(r.GetHostId())))
        { r.SetUnobscuredInView(view, true); shown++; }
        if (t.Commit() != TransactionStatus.Committed) return ToolResult.Error("Revit refused the 3D view when saving it.");
        return ToolResult.Ok(new { viewId = view.Id.Value, name = view.Name, hosts = hosts.Count, barSets = shown });
    }
}

public sealed class ExportBarScheduleTool : IRevitTool
{
    public string Name => "export_bar_schedule";
    public string Description =>
        "Bar bending schedule and rebar tonnage: every rebar set and area reinforcement bar with host, mark, bar size, " +
        "shape, number, length per bar, total length and weight, plus totals per bar size, to a CSV that opens in Excel.";
    public string PropertiesJson => """
    {
      "element_ids": {"type": "array", "items": {"type": "integer"}, "description": "Only rebar in these hosts (default all)."},
      "folder": {"type": "string"},
      "file_name": {"type": "string"}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var only = args.LongList("element_ids").ToHashSet();
        var bars = new FilteredElementCollector(doc).OfClass(typeof(Rebar)).Cast<Element>()
            .Concat(new FilteredElementCollector(doc).OfClass(typeof(RebarInSystem)).Cast<Element>())
            .Where(b => only.Count == 0 || only.Contains(HostId(b).Value)).ToList();
        if (bars.Count == 0) return ToolResult.Error("No rebar found.");

        string N(double v) => Math.Round(v, 3).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string Csv(string s) => s.IndexOfAny([',', '"', '\n']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        var sb = new StringBuilder();
        sb.AppendLine("Host,Host id,Host mark,Bar mark,Bar size,Diameter (mm),Shape,Number,Length per bar (m),Total length (m),kg/m,Weight (kg)");
        var totals = new SortedDictionary<double, (string size, double m, double kg)>();
        foreach (var b in bars.OrderBy(b => HostId(b).Value).ThenBy(b => b.get_Parameter(BuiltInParameter.REBAR_ELEM_SCHEDULE_MARK)?.AsString()))
        {
            var host = doc.GetElement(HostId(b));
            var type = doc.GetElement(b.GetTypeId()) as RebarBarType;
            var diaMm = type is null ? 0 : type.BarNominalDiameter * 304.8;
            var number = b is Rebar r ? r.Quantity : ((RebarInSystem)b).Quantity;
            var total = UnitUtils.ConvertFromInternalUnits(b is Rebar r2 ? r2.TotalLength : ((RebarInSystem)b).TotalLength, UnitTypeId.Meters);
            var each = number > 0 ? total / number : 0;
            var kgPerM = diaMm * diaMm / 162.2; // steel 7850 kg/m³
            var shape = b.get_Parameter(BuiltInParameter.REBAR_SHAPE)?.AsElementId() is { } sid ? doc.GetElement(sid)?.Name ?? "" : "";
            sb.AppendLine(string.Join(",", Csv(host?.Category?.Name ?? ""), HostId(b).Value, Csv(host?.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? ""),
                Csv(b.get_Parameter(BuiltInParameter.REBAR_ELEM_SCHEDULE_MARK)?.AsString() ?? ""), Csv(type?.Name ?? ""), N(diaMm), Csv(shape),
                number, N(each), N(total), N(kgPerM), N(total * kgPerM)));
            var key = Math.Round(diaMm, 1);
            totals.TryGetValue(key, out var tt);
            totals[key] = (type?.Name ?? $"{key} mm", tt.m + total, tt.kg + total * kgPerM);
        }
        sb.AppendLine();
        sb.AppendLine("Bar size,Diameter (mm),Total length (m),Weight (kg),Weight (t)");
        foreach (var kv in totals) sb.AppendLine($"{Csv(kv.Value.size)},{N(kv.Key)},{N(kv.Value.m)},{N(kv.Value.kg)},{N(kv.Value.kg / 1000)}");

        var folder = OutputPaths.Checked(args.OptString("folder")) ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SketchProMax");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, (OutputPaths.Checked(args.OptString("file_name")) ?? $"{doc.Title} - Bar schedule {DateTime.Now:yyyy-MM-dd}") + ".csv");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        return ToolResult.Ok(new
        {
            file, barSets = bars.Count, totalTonnes = Math.Round(totals.Values.Sum(v => v.kg) / 1000, 3),
            bySize = totals.Select(kv => new { size = kv.Value.size, diameterMm = kv.Key, tonnes = Math.Round(kv.Value.kg / 1000, 3) }),
            note = "Weights use 7850 kg/m³ steel on the nominal diameter; laps and wastage are not added.",
        });
    }

    private static ElementId HostId(Element b) => b is Rebar r ? r.GetHostId() : ((RebarInSystem)b).GetHostId();
}
