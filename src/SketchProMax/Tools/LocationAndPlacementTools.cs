using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

internal static class Coordinates
{
    /// <summary>Internal-to-shared transform, checked against the survey point so the direction is always right.</summary>
    public static Transform InternalToShared(Document doc)
    {
        var total = doc.ActiveProjectLocation.GetTotalTransform();
        var survey = BasePoint.GetSurveyPoint(doc);
        if (survey != null)
        {
            var a = total.Inverse.OfPoint(survey.Position);
            if (a.DistanceTo(survey.SharedPosition) < 0.01) return total.Inverse;
            if (total.OfPoint(survey.Position).DistanceTo(survey.SharedPosition) < 0.01) return total;
        }
        return total.Inverse;
    }

    public static double[] Out(Document doc, XYZ p) =>
        [RevitHelpers.FromInternal(doc, p.X), RevitHelpers.FromInternal(doc, p.Y), RevitHelpers.FromInternal(doc, p.Z)];

    public static double Deg(double radians) => Math.Round(radians * 180 / Math.PI, 6);
    public static double Rad(double degrees) => degrees * Math.PI / 180;
}

public sealed class GetCoordinatesTool : IRevitTool
{
    public string Name => "get_coordinates";
    public string Description =>
        "Report the model's location and coordinates: site latitude/longitude and place name, the shared coordinate " +
        "position (east, north, elevation, angle to true north), project base point and survey point (internal and " +
        "shared), named locations, and linked models' positions.";
    public string PropertiesJson => "{}";
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var site = doc.SiteLocation;
        var position = doc.ActiveProjectLocation.GetProjectPosition(XYZ.Zero);
        var pbp = BasePoint.GetProjectBasePoint(doc);
        var sp = BasePoint.GetSurveyPoint(doc);
        var toShared = Coordinates.InternalToShared(doc);
        var links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>()
            .Select(l => new { name = l.Name, sharedOrigin = Coordinates.Out(doc, toShared.OfPoint(l.GetTotalTransform().Origin)) });
        return ToolResult.Ok(new
        {
            unit = RevitHelpers.LengthUnitLabel(doc),
            site = new { place = site.PlaceName, latitude = Coordinates.Deg(site.Latitude), longitude = Coordinates.Deg(site.Longitude), timeZone = site.TimeZone },
            sharedPosition = new
            {
                location = doc.ActiveProjectLocation.Name,
                east = RevitHelpers.FromInternal(doc, position.EastWest),
                north = RevitHelpers.FromInternal(doc, position.NorthSouth),
                elevation = RevitHelpers.FromInternal(doc, position.Elevation),
                angleToTrueNorthDegrees = Coordinates.Deg(position.Angle),
            },
            projectBasePoint = pbp is null ? null : new { @internal = Coordinates.Out(doc, pbp.Position), shared = Coordinates.Out(doc, pbp.SharedPosition) },
            surveyPoint = sp is null ? null : new { @internal = Coordinates.Out(doc, sp.Position), shared = Coordinates.Out(doc, sp.SharedPosition) },
            locations = doc.ProjectLocations.Cast<ProjectLocation>().Select(l => l.Name),
            links,
        });
    }
}

public sealed class SetCoordinatesTool : IRevitTool
{
    public string Name => "set_coordinates";
    public string Description =>
        "Set the model's location and coordinates: site latitude/longitude/place name; the shared coordinates of the " +
        "internal origin (east, north, elevation) and the angle to true north; or acquire coordinates from a linked " +
        "Revit or CAD model (so this model takes the link's shared coordinate system).";
    public string PropertiesJson => """
    {
      "latitude": {"type": "number", "description": "Degrees, north positive."},
      "longitude": {"type": "number", "description": "Degrees, east positive."},
      "place_name": {"type": "string"},
      "east": {"type": "number", "description": "Shared east/west of the internal origin, project units."},
      "north": {"type": "number"},
      "elevation": {"type": "number"},
      "true_north_angle": {"type": "number", "description": "Degrees, counterclockwise from project north to true north."},
      "acquire_from_link": {"type": "string", "description": "Part of a linked Revit or CAD file's name."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var done = new List<string>();
        using var t = RevitHelpers.StartTransaction(doc, "Set coordinates");
        var site = doc.SiteLocation;
        if (args.OptDouble("latitude") is { } lat) { site.Latitude = Coordinates.Rad(lat); done.Add("latitude"); }
        if (args.OptDouble("longitude") is { } lon) { site.Longitude = Coordinates.Rad(lon); done.Add("longitude"); }
        if (args.OptString("place_name") is { } place) { site.PlaceName = place; done.Add("place name"); }

        if (args.Has("east") || args.Has("north") || args.Has("elevation") || args.Has("true_north_angle"))
        {
            var location = doc.ActiveProjectLocation;
            var current = location.GetProjectPosition(XYZ.Zero);
            location.SetProjectPosition(XYZ.Zero, new ProjectPosition(
                args.OptDouble("east") is { } e ? RevitHelpers.ToInternal(doc, e) : current.EastWest,
                args.OptDouble("north") is { } n ? RevitHelpers.ToInternal(doc, n) : current.NorthSouth,
                args.OptDouble("elevation") is { } z ? RevitHelpers.ToInternal(doc, z) : current.Elevation,
                args.OptDouble("true_north_angle") is { } a ? Coordinates.Rad(a) : current.Angle));
            done.Add("shared position");
        }
        if (args.OptString("acquire_from_link") is { } linkName)
        {
            var link = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<Element>()
                .Concat(new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<Element>())
                .FirstOrDefault(l => (l.Name + " " + l.Category?.Name).IndexOf(linkName, StringComparison.OrdinalIgnoreCase) >= 0)
                ?? throw new ArgumentException($"No linked model matching '{linkName}'.");
            doc.AcquireCoordinates(link.Id);
            done.Add($"acquired coordinates from {link.Name}");
        }
        t.Commit();
        return ToolResult.Ok(new { done });
    }
}

public sealed class ConvertCoordinatesTool : IRevitTool
{
    public string Name => "convert_coordinates";
    public string Description =>
        "Convert points between the model's internal coordinates (what tools use) and shared/survey coordinates " +
        "(what surveyors and other consultants use). Use it before placing things from survey data.";
    public string PropertiesJson => """
    {
      "points": {"type": "array", "items": {"type": "array", "items": {"type": "number"}}, "description": "[[x, y, z], ...] in project units."},
      "from": {"type": "string", "enum": ["internal", "shared"]}
    }
    """;
    public string[] Required => ["points", "from"];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var toShared = Coordinates.InternalToShared(doc);
        var transform = args.String("from") == "internal" ? toShared : toShared.Inverse;
        var points = args.Array("points").Select(p => Coordinates.Out(doc, transform.OfPoint(ViewKit.Point(doc, p)))).ToList();
        return ToolResult.Ok(new { to = args.String("from") == "internal" ? "shared" : "internal", points });
    }
}

/// <summary>Reads placement tables from CSV or Excel (.xlsx, first sheet) into rows of column -> text.</summary>
internal static class Table
{
    public static List<Dictionary<string, string>> Read(string path)
    {
        var rows = Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase) ? Xlsx(path) : Csv(path);
        if (rows.Count < 2) return [];
        var header = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        return rows.Skip(1).Where(r => r.Any(c => c.Trim().Length > 0)).Select(r =>
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < header.Count && i < r.Count; i++) d[header[i]] = r[i].Trim();
            return d;
        }).ToList();
    }

    private static List<List<string>> Csv(string path)
    {
        var rows = new List<List<string>>();
        foreach (var line in File.ReadAllLines(path))
        {
            var cells = new List<string>();
            var cell = new StringBuilder();
            var quoted = false;
            var sep = line.Contains(';') && !line.Contains(',') ? ';' : line.Contains('\t') && !line.Contains(',') ? '\t' : ',';
            for (var i = 0; i < line.Length; i++)
            {
                var ch = line[i];
                if (ch == '"') { if (quoted && i + 1 < line.Length && line[i + 1] == '"') { cell.Append('"'); i++; } else quoted = !quoted; }
                else if (ch == sep && !quoted) { cells.Add(cell.ToString()); cell.Clear(); }
                else cell.Append(ch);
            }
            cells.Add(cell.ToString());
            rows.Add(cells);
        }
        return rows;
    }

    private static List<List<string>> Xlsx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var shared = new List<string>();
        if (zip.GetEntry("xl/sharedStrings.xml") is { } ss)
            using (var s = ss.Open())
                shared = XDocument.Load(s).Root!.Elements(ns + "si").Select(si => string.Concat(si.Descendants(ns + "t").Select(x => x.Value))).ToList();
        var sheet = zip.GetEntry("xl/worksheets/sheet1.xml") ?? throw new InvalidDataException("The workbook has no first sheet.");
        using var stream = sheet.Open();
        var rows = new List<List<string>>();
        foreach (var row in XDocument.Load(stream).Descendants(ns + "row"))
        {
            var cells = new List<string>();
            foreach (var c in row.Elements(ns + "c"))
            {
                var col = 0;
                foreach (var ch in ((string?)c.Attribute("r") ?? "A").TakeWhile(char.IsLetter)) col = col * 26 + (ch - 'A' + 1);
                while (cells.Count < col - 1) cells.Add("");
                var value = c.Element(ns + "v")?.Value ?? string.Concat(c.Descendants(ns + "t").Select(x => x.Value));
                if ((string?)c.Attribute("t") == "s" && int.TryParse(value, out var idx) && idx < shared.Count) value = shared[idx];
                cells.Add(value);
            }
            rows.Add(cells);
        }
        return rows;
    }
}

public sealed class PlaceManyTool : IRevitTool
{
    public string Name => "place_many";
    public string Description =>
        "Place one thing many times (hundreds or thousands) at different points, levels and rotations: a family type, " +
        "a model group, or a linked Revit model (each placement a new link instance). Positions come from a list or a " +
        "CSV/Excel file with columns x, y, and optionally z (offset above the level), level, rotation (degrees), mark, " +
        "plus any other columns, which are written to parameters of the same name. Coordinates can be internal or shared/survey.";
    public string PropertiesJson => """
    {
      "kind": {"type": "string", "enum": ["family", "group", "link"]},
      "source": {"type": "string", "description": "family: 'Family: Type' or type name. group: group type name. link: linked model name or a .rvt path to link."},
      "placements": {"type": "array", "items": {"type": "object", "properties": {"x": {"type": "number"}, "y": {"type": "number"}, "z": {"type": "number"}, "level": {"type": "string"}, "rotation": {"type": "number"}, "mark": {"type": "string"}, "parameters": {"type": "object"}}, "required": ["x", "y"]}},
      "file": {"type": "string", "description": "CSV or .xlsx path instead of placements."},
      "coordinates": {"type": "string", "enum": ["internal", "shared"], "description": "Default internal. 'shared' converts survey coordinates."},
      "default_level": {"type": "string"}
    }
    """;
    public string[] Required => ["kind", "source"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var rows = new List<Dictionary<string, string>>();
        if (args.OptString("file") is { } file)
        {
            if (!File.Exists(file)) return ToolResult.Error($"File not found: {file}");
            rows = Table.Read(file);
        }
        foreach (var p in args.Array("placements"))
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in p.EnumerateObject())
            {
                if (prop.Name == "parameters") foreach (var kv in prop.Value.EnumerateObject()) d[kv.Name] = kv.Value.ToString();
                else d[prop.Name] = prop.Value.ToString();
            }
            rows.Add(d);
        }
        if (rows.Count == 0) return ToolResult.Error("No placements given.");

        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
        var defaultLevel = RevitHelpers.FindLevel(doc, args.OptString("default_level"));
        var fromShared = args.OptString("coordinates") == "shared" ? Coordinates.InternalToShared(doc).Inverse : null;
        var kind = args.String("kind");
        var source = args.String("source");
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "x", "y", "z", "level", "rotation", "mark" };

        using var t = new Transaction(doc, $"Sketch ProMax: Place {rows.Count} x {source}");
        t.Start();
        var failure = t.GetFailureHandlingOptions();
        failure.SetFailuresPreprocessor(new TypeKit.SwallowWarnings());
        t.SetFailureHandlingOptions(failure);

        FamilySymbol? symbol = null;
        GroupType? group = null;
        RevitLinkType? linkType = null;
        switch (kind)
        {
            case "family":
                symbol = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                    .FirstOrDefault(s => string.Equals($"{s.FamilyName}: {s.Name}", source, StringComparison.OrdinalIgnoreCase))
                    ?? new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                        .FirstOrDefault(s => string.Equals(s.Name, source, StringComparison.OrdinalIgnoreCase) || string.Equals(s.FamilyName, source, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"No family type '{source}'. Use list_types or load_family.");
                if (!symbol.IsActive) symbol.Activate();
                break;
            case "group":
                group = new FilteredElementCollector(doc).OfClass(typeof(GroupType)).Cast<GroupType>()
                    .FirstOrDefault(g => string.Equals(g.Name, source, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"No group named '{source}'.");
                break;
            default:
                linkType = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>()
                    .FirstOrDefault(l => l.Name.IndexOf(Path.GetFileNameWithoutExtension(source), StringComparison.OrdinalIgnoreCase) >= 0);
                if (linkType is null)
                {
                    if (!File.Exists(source)) throw new ArgumentException($"No linked model '{source}', and it isn't a file path.");
                    var result = RevitLinkType.Create(doc, ModelPathUtils.ConvertUserVisiblePathToModelPath(source), new RevitLinkOptions(false));
                    linkType = (RevitLinkType)doc.GetElement(result.ElementId);
                }
                break;
        }

        var created = new List<long>();
        var problems = new List<string>();
        var rowNumber = 0;
        foreach (var row in rows)
        {
            rowNumber++;
            try
            {
                double Num(string key, double fallback = 0) =>
                    row.TryGetValue(key, out var s) && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
                var level = row.TryGetValue("level", out var ln) && ln.Length > 0
                    ? levels.FirstOrDefault(l => string.Equals(l.Name, ln, StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException($"no level '{ln}'")
                    : defaultLevel;
                var point = new XYZ(RevitHelpers.ToInternal(doc, Num("x")), RevitHelpers.ToInternal(doc, Num("y")), 0);
                if (fromShared != null) point = fromShared.OfPoint(new XYZ(point.X, point.Y, 0));
                var offset = RevitHelpers.ToInternal(doc, Num("z"));
                var at = new XYZ(point.X, point.Y, level.Elevation + offset);
                var angle = Coordinates.Rad(Num("rotation"));

                Element placed;
                if (symbol != null)
                {
                    var instance = doc.Create.NewFamilyInstance(new XYZ(point.X, point.Y, level.Elevation), symbol, level, StructuralType.NonStructural);
                    if (Math.Abs(offset) > 1e-9)
                        (instance.get_Parameter(BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM) ?? instance.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM))?.Set(offset);
                    placed = instance;
                }
                else if (group != null) placed = doc.Create.PlaceGroup(at, group);
                else
                {
                    var link = RevitLinkInstance.Create(doc, linkType!.Id);
                    ElementTransformUtils.MoveElement(doc, link.Id, at);
                    placed = link;
                }
                if (Math.Abs(angle) > 1e-9)
                    ElementTransformUtils.RotateElement(doc, placed.Id, Line.CreateBound(at, at + XYZ.BasisZ), angle);
                if (row.TryGetValue("mark", out var mark) && mark.Length > 0) placed.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set(mark);
                foreach (var kv in row.Where(kv => !reserved.Contains(kv.Key) && kv.Value.Length > 0))
                    if (placed.LookupParameter(kv.Key) is { IsReadOnly: false } p)
                        _ = p.StorageType switch
                        {
                            StorageType.String => p.Set(kv.Value),
                            StorageType.Integer when int.TryParse(kv.Value, out var i) => p.Set(i),
                            _ => p.SetValueString(kv.Value),
                        };
                created.Add(placed.Id.Value);
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or ArgumentException or InvalidOperationException)
            {
                if (problems.Count < 50) problems.Add($"row {rowNumber}: {ex.Message}");
            }
        }
        t.Commit();
        return ToolResult.Ok(new { placed = created.Count, of = rows.Count, firstIds = created.Take(20), problems });
    }
}
