using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

public sealed class ExportBcfTool : IRevitTool
{
    public string Name => "export_bcf";
    public string Description =>
        "Write coordination issues to a BCF 2.1 file (opens in ACC/BIM 360, Navisworks, Solibri, BIMcollab, Revizto...). " +
        "Each issue has a title, description, the elements involved and optionally a view for the snapshot and camera " +
        "(a 3D view gives the camera position). Use it after QA or clash reviews.";
    public string PropertiesJson => """
    {
      "issues": {"type": "array", "items": {"type": "object", "properties": {
        "title": {"type": "string"}, "description": {"type": "string"},
        "element_ids": {"type": "array", "items": {"type": "integer"}},
        "view_id": {"type": "integer"},
        "priority": {"type": "string"}, "assigned_to": {"type": "string"}}, "required": ["title"]}},
      "file_name": {"type": "string"},
      "folder": {"type": "string"},
      "author": {"type": "string", "description": "Default: the Revit user name."}
    }
    """;
    public string[] Required => ["issues"];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var folder = OutputPaths.Checked(args.OptString("folder"))
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SketchProMax");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, (OutputPaths.Checked(args.OptString("file_name")) ?? $"{doc.Title} issues {DateTime.Now:yyyy-MM-dd HHmm}") + ".bcf");
        var author = args.OptString("author") ?? app.Application.Username;
        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var temp = Path.Combine(Path.GetTempPath(), "SketchProMaxBcf_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var count = 0;

        try
        {
            using var zip = ZipFile.Open(file, ZipArchiveMode.Create);
            Write(zip, "bcf.version", new XDocument(new XElement("Version", new XAttribute("VersionId", "2.1"), new XElement("DetailedVersion", "2.1"))));

            foreach (var issue in args.Array("issues"))
            {
                var topic = Guid.NewGuid().ToString();
                var viewpoint = Guid.NewGuid().ToString();
                var ids = issue.TryGetProperty("element_ids", out var e) ? e.EnumerateArray().Select(x => new ElementId(x.GetInt64())).ToList() : [];
                View? view = issue.TryGetProperty("view_id", out var v) ? doc.GetElement(new ElementId(v.GetInt64())) as View : null;

                var topicXml = new XElement("Topic",
                    new XAttribute("Guid", topic), new XAttribute("TopicType", "Issue"), new XAttribute("TopicStatus", "Open"),
                    new XElement("Title", issue.GetProperty("title").GetString()),
                    issue.TryGetProperty("priority", out var pr) ? new XElement("Priority", pr.GetString()) : null,
                    new XElement("CreationDate", now),
                    new XElement("CreationAuthor", author),
                    issue.TryGetProperty("assigned_to", out var at) ? new XElement("AssignedTo", at.GetString()) : null,
                    issue.TryGetProperty("description", out var d) ? new XElement("Description", d.GetString()) : null);

                var snapshot = view != null ? Snapshot(doc, view, temp) : null;
                var markup = new XDocument(new XElement("Markup", topicXml,
                    new XElement("Viewpoints", new XAttribute("Guid", viewpoint),
                        new XElement("Viewpoint", "viewpoint.bcfv"),
                        snapshot != null ? new XElement("Snapshot", "snapshot.png") : null)));
                Write(zip, $"{topic}/markup.bcf", markup);

                var components = new XElement("Components",
                    new XElement("Selection", ids.Select(id => new XElement("Component",
                        new XAttribute("IfcGuid", IfcGuid(doc, id)),
                        new XElement("OriginatingSystem", "Autodesk Revit"),
                        new XElement("AuthoringToolId", id.Value.ToString())))));
                var visualization = new XElement("VisualizationInfo", new XAttribute("Guid", viewpoint), components, Camera(view));
                Write(zip, $"{topic}/viewpoint.bcfv", new XDocument(visualization));
                if (snapshot != null) zip.CreateEntryFromFile(snapshot, $"{topic}/snapshot.png");
                count++;
            }
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch (IOException) { }
        }
        return ToolResult.Ok(new { file, issues = count });
    }

    private static void Write(ZipArchive zip, string path, XDocument xml)
    {
        using var stream = zip.CreateEntry(path).Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(new XDeclaration("1.0", "utf-8", null) + "\n" + xml.Root);
    }

    private static string? Snapshot(Document doc, View view, string folder)
    {
        var options = new ImageExportOptions
        {
            ExportRange = ExportRange.SetOfViews,
            FilePath = Path.Combine(folder, view.Id.Value.ToString()),
            ZoomType = ZoomFitType.FitToPage,
            PixelSize = 1600,
            HLRandWFViewsFileType = ImageFileType.PNG,
            ShadowViewsFileType = ImageFileType.PNG,
        };
        options.SetViewsAndSheets([view.Id]);
        try { doc.ExportImage(options); }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { return null; }
        return Directory.GetFiles(folder, view.Id.Value + "*.png").FirstOrDefault();
    }

    private static XElement? Camera(View? view)
    {
        if (view is not View3D v3 || v3.IsTemplate) return null;
        var o = v3.GetOrientation();
        XElement Vec(string name, XYZ p, bool scale) => new(name,
            new XElement("X", Fmt(scale ? Meters(p.X) : p.X)), new XElement("Y", Fmt(scale ? Meters(p.Y) : p.Y)), new XElement("Z", Fmt(scale ? Meters(p.Z) : p.Z)));
        var body = new object[] { Vec("CameraViewPoint", o.EyePosition, true), Vec("CameraDirection", o.ForwardDirection, false), Vec("CameraUpVector", o.UpDirection, false) };
        return v3.IsPerspective
            ? new XElement("PerspectiveCamera", body, new XElement("FieldOfView", "60"))
            : new XElement("OrthogonalCamera", body, new XElement("ViewToWorldScale", "30"));
    }

    private static double Meters(double feet) => UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Meters);
    private static string Fmt(double d) => d.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The element's IFC GUID: the stored IfcGUID parameter if the model was exported before, otherwise Revit's export id.</summary>
    public static string IfcGuid(Document doc, ElementId id)
    {
        var stored = doc.GetElement(id)?.get_Parameter(BuiltInParameter.IFC_GUID)?.AsString();
        if (!string.IsNullOrEmpty(stored)) return stored!;
        return Compress(ExportUtils.GetExportId(doc, id));
    }

    private const string Chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz_$";

    /// <summary>Standard IFC 22-character GUID encoding.</summary>
    private static string Compress(Guid guid)
    {
        var b = guid.ToByteArray();
        var num = new uint[6];
        var first = BitConverter.ToUInt32(b, 0);
        num[0] = first / 16777216;
        num[1] = first % 16777216;
        num[2] = (uint)(BitConverter.ToUInt16(b, 4) * 256 + BitConverter.ToUInt16(b, 6) / 256);
        num[3] = (uint)(BitConverter.ToUInt16(b, 6) % 256 * 65536 + b[8] * 256 + b[9]);
        num[4] = (uint)(b[10] * 65536 + b[11] * 256 + b[12]);
        num[5] = (uint)(b[13] * 65536 + b[14] * 256 + b[15]);
        var sb = new StringBuilder();
        for (var i = 0; i < 6; i++)
        {
            var digits = i == 0 ? 2 : 4;
            var chunk = new char[digits];
            var n = num[i];
            for (var k = digits - 1; k >= 0; k--) { chunk[k] = Chars[(int)(n % 64)]; n /= 64; }
            sb.Append(chunk);
        }
        return sb.ToString();
    }
}

public sealed class AddSpotElevationsTool : IRevitTool
{
    public string Name => "add_spot_elevations";
    public string Description =>
        "Add spot elevations on the top (or bottom) surface of floors, roofs and slabs, and of family instances such as piles, " +
        "pile caps, footings, beams and columns, in a plan or section view. Family instance faces are resolved from the family " +
        "geometry with the instance's position applied, so the spot lies on its reference. With no element ids it finds every " +
        "floor and roof visible in the view (each level change gets one). Places at each face's center, at its corners, or at " +
        "given plan points. Each spot is its own step: one Revit refuses is reported with its reason and the rest are kept.";
    public string PropertiesJson => DependentGuard.AddTo("""
    {
      "view_id": {"type": "integer"},
      "element_ids": {"type": "array", "items": {"type": "integer"}, "description": "Default: all floors and roofs in the view."},
      "categories": {"type": "array", "items": {"type": "string"}, "description": "With no element_ids: categories to collect in the view instead of floors and roofs, e.g. ['Structural Foundations', 'Structural Framing']."},
      "face": {"type": "string", "enum": ["top", "bottom"], "description": "Default top."},
      "at_corners": {"type": "boolean", "description": "Also mark each top face's corners (e.g. sloped slabs, ramps)."},
      "points": {"type": "array", "items": {"type": "array", "items": {"type": "number"}}, "description": "Optional [x, y] plan points; each uses the element whose top face is under it."},
      "leader": {"type": "boolean", "description": "Default true."}
    }
    """);
    public string[] Required => [];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var view = ViewKit.ViewOrActive(doc, args.OptLong("view_id"));
        var guard = DependentGuard.For(doc, view, args.OptString("dependent_view"));
        var target = guard.Target;
        var sign = (args.OptString("face") ?? "top") switch
        {
            "top" => 1,
            "bottom" => -1,
            _ => throw new ArgumentException("face must be top or bottom."),
        };
        var elementIds = args.LongList("element_ids");
        if (elementIds.Count == 0)
        {
            var cats = args.StringList("categories").Select(c => RevitHelpers.ParseCategory(doc, c)).ToList();
            if (cats.Count == 0) cats = [BuiltInCategory.OST_Floors, BuiltInCategory.OST_Roofs];
            elementIds = new FilteredElementCollector(doc, view.Id)
                .WherePasses(new ElementMulticategoryFilter(cats))
                .WhereElementIsNotElementType().Select(e => e.Id.Value).ToList();
        }
        var leader = args.OptBool("leader", true);
        var faces = new List<RefResolver.RefFace>();
        var noFace = new List<long>();
        foreach (var id in elementIds)
        {
            var e = doc.GetElement(new ElementId(id));
            if (e is null) throw new ArgumentException($"No element with id {id}.");
            var found = RefResolver.TopOrBottom(e, view, sign);
            if (found.Count == 0) noFace.Add(id);
            faces.AddRange(found);
        }
        if (faces.Count == 0)
            return ToolResult.Error($"No {(sign > 0 ? "top" : "bottom")} faces found{(noFace.Count > 0 ? $" on {string.Join(", ", noFace.Take(20))}" : "")}. " +
                                    "Use floors, roofs, slabs or family instances with solid geometry (in-place families expose none to the API).");

        // In sections the spot goes where the face meets the section plane, not at the face's middle somewhere behind it.
        var vd = view.ViewDirection.Normalize();
        XYZ OnFace(RefResolver.RefFace f, XYZ p)
        {
            if (view is ViewPlan) return p;
            var onPlane = p - vd * (p - view.Origin).DotProduct(vd);
            return f.Closest(onPlane) ?? p;
        }

        var targets = new List<(RefResolver.RefFace Face, XYZ Point)>();
        if (args.Has("points"))
        {
            foreach (var p in args.Array("points"))
            {
                var xy = ViewKit.Point(doc, p);
                foreach (var f in faces)
                {
                    var onFace = f.Closest(new XYZ(xy.X, xy.Y, f.Middle().Z));
                    if (onFace != null && Math.Abs(onFace.X - xy.X) < 0.01 && Math.Abs(onFace.Y - xy.Y) < 0.01) { targets.Add((f, onFace)); break; }
                }
            }
        }
        else
        {
            foreach (var f in faces)
            {
                targets.Add((f, OnFace(f, f.Face is PlanarFace ? f.Middle() : f.Extreme(sign))));
                if (args.OptBool("at_corners"))
                    foreach (var loop in f.Face.GetEdgesAsCurveLoops())
                        foreach (var c in loop)
                            targets.Add((f, f.Tr.OfPoint(c.GetEndPoint(0))));
            }
        }
        targets = targets.Where(t => guard.Keep(t.Point)).ToList();
        if (targets.Count == 0) return ToolResult.Error("No spot positions left to place (none of the points lies on a face, or all are outside this view's crop).");

        var step = UnitUtils.ConvertToInternalUnits(1, UnitTypeId.Meters) * view.Scale / 100.0;
        var i = 0;
        var batch = ItemBatch.Run(doc, "Spot elevations", targets.Select(t => (t.Face, t.Point, n: ++i)),
            t => $"spot {t.n} on {t.Face.Element.Id.Value}", _ => [], t =>
            {
                var bend = t.Point + (target.RightDirection + target.UpDirection) * step * 0.5;
                var end = bend + target.RightDirection * step * 0.5;
                return doc.Create.NewSpotElevation(target, t.Face.Ref, t.Point, bend, end, t.Point, leader).Id.Value;
            }, borrow: false);
        var created = batch.Items.Where(x => x.Ok && x.Value is long).Select(x => (long)x.Value!).ToList();
        var payload = new
        {
            created = created.Count, ids = created,
            problems = batch.Failed.Select(f => $"{f.Item}: {f.Error}").Take(50),
            noFace = noFace.Count > 0 ? noFace.Take(50) : null,
            dependentView = guard.Report(),
        };
        return created.Count == 0
            ? new ToolResult("No spot elevations were placed. " + System.Text.Json.JsonSerializer.Serialize(payload), IsError: true)
            : ToolResult.Ok(payload);
    }
}

public sealed class SetCurtainGridTool : IRevitTool
{
    public string Name => "set_curtain_grid";
    public string Description =>
        "Lay out curtain wall grid lines on curtain walls at fixed spacings (vertical mullion spacing along the wall, " +
        "horizontal spacing up the wall), optionally clearing existing grid lines first. Mullions follow the wall type.";
    public string PropertiesJson => """
    {
      "wall_ids": {"type": "array", "items": {"type": "integer"}},
      "vertical_spacing": {"type": "number", "description": "Distance between vertical grid lines, project units."},
      "horizontal_spacing": {"type": "number", "description": "Distance between horizontal grid lines, project units."},
      "clear_existing": {"type": "boolean"}
    }
    """;
    public string[] Required => ["wall_ids"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var report = new List<object>();
        using var t = RevitHelpers.StartTransaction(doc, "Curtain grid");
        foreach (var id in args.LongList("wall_ids"))
        {
            if (doc.GetElement(new ElementId(id)) is not Wall { CurtainGrid: { } grid } wall || wall.Location is not LocationCurve { Curve: Line line })
            {
                report.Add(new { wall = id, error = "not a straight curtain wall" });
                continue;
            }

            var bottom = wall.get_BoundingBox(null).Min.Z;
            var top = wall.get_BoundingBox(null).Max.Z;
            var vertical = args.OptDouble("vertical_spacing") is { } vs ? RevitHelpers.ToInternal(doc, vs) : (double?)null;
            var horizontal = args.OptDouble("horizontal_spacing") is { } hs ? RevitHelpers.ToInternal(doc, hs) : (double?)null;
            // A zero, negative or tiny spacing would never finish (or bury the wall in lines); check before touching it.
            if (SpacingProblem(vertical, line.Length, horizontal, top - bottom) is { } problem)
            {
                report.Add(new { wall = id, error = problem });
                continue;
            }

            if (args.OptBool("clear_existing"))
            {
                // Lines set by the wall type can't be deleted; those stay.
                foreach (var g in grid.GetUGridLineIds().Concat(grid.GetVGridLineIds()).ToList())
                {
                    try { doc.Delete(g); }
                    catch (Autodesk.Revit.Exceptions.ApplicationException) { }
                }
            }

            var mid = (bottom + top) / 2;
            var start = line.GetEndPoint(0);
            var added = 0;

            if (vertical is { } step)
            {
                for (var d = step; d < line.Length - step * 0.1; d += step)
                    if (AddLine(doc, grid, new XYZ(start.X, start.Y, mid) + line.Direction * d, vertical: true)) added++;
            }
            if (horizontal is { } rise)
            {
                var centre = line.Evaluate(0.5, true);
                for (var z = bottom + rise; z < top - rise * 0.1; z += rise)
                    if (AddLine(doc, grid, new XYZ(centre.X, centre.Y, z), vertical: false)) added++;
            }
            report.Add(new { wall = id, added });
        }
        t.Commit();
        return ToolResult.Ok(new { walls = report });
    }

    private const double MinSpacingFeet = 50 / 304.8;
    private const int MaxLinesPerWall = 500;

    /// <summary>Why the spacings (feet) can't be used on a wall of this length and height, or null when they are fine.</summary>
    private static string? SpacingProblem(double? vertical, double length, double? horizontal, double height)
    {
        if (vertical is { } v && (double.IsNaN(v) || v < MinSpacingFeet)) return "vertical_spacing must be at least 50 mm";
        if (horizontal is { } h && (double.IsNaN(h) || h < MinSpacingFeet)) return "horizontal_spacing must be at least 50 mm";
        var lines = (vertical is { } vs ? length / vs : 0) + (horizontal is { } hs ? height / hs : 0);
        return lines > MaxLinesPerWall
            ? $"these spacings would add about {lines:N0} grid lines to this wall (at most {MaxLinesPerWall}); use larger spacings"
            : null;
    }

    /// <summary>Adds a grid line through a point; tries both grid directions and keeps the one with the wanted orientation.</summary>
    private static bool AddLine(Document doc, CurtainGrid grid, XYZ point, bool vertical)
    {
        foreach (var isU in new[] { true, false })
        {
            CurtainGridLine line;
            try { line = grid.AddGridLine(isU, point, false); }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { continue; }
            var dir = (line.FullCurve.GetEndPoint(1) - line.FullCurve.GetEndPoint(0)).Normalize();
            if (Math.Abs(dir.Z) > 0.9 == vertical) return true;
            doc.Delete(line.Id);
        }
        return false;
    }
}
