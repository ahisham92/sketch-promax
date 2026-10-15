using System.IO;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

public sealed class ImportImageTool : IRevitTool
{
    public string Name => "import_image";
    public string Description =>
        "Place a picture (PNG, JPG, BMP, TIF) or a PDF page into a view or sheet, e.g. a site photo, a logo, a " +
        "reference sketch or a scanned drawing to trace over. Needs the file's full path on this computer.";
    public string PropertiesJson => """
    {
      "path": {"type": "string"},
      "view_id": {"type": "integer", "description": "View or sheet; default active view."},
      "center": {"type": "array", "items": {"type": "number"}, "description": "[x, y] in project units; default view center."},
      "width": {"type": "number", "description": "Placed width in project units (proportions kept)."},
      "pdf_page": {"type": "integer", "description": "Page to import from a PDF (default 1)."}
    }
    """;
    public string[] Required => ["path"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var path = args.String("path");
        if (!File.Exists(path)) return ToolResult.Error($"File not found: {path}");
        var view = ViewKit.ViewOrActive(doc, args.OptLong("view_id"));
        if (view is ViewSheet { IsPlaceholder: true } ph)
            return ToolResult.Error($"Sheet {ph.SheetNumber} '{ph.Name}' is a placeholder sheet (an index entry with no paper), so it " +
                                    "cannot hold images or views. Pick a real sheet, or create one with create_sheets (placeholder=false).");

        XYZ center;
        if (args.Has("center")) center = Modeling.Point(doc, args.Raw["center"]);
        else
        {
            var box = view.CropBox;
            center = box is null ? XYZ.Zero : box.Transform.OfPoint((box.Min + box.Max) / 2);
        }

        using var t = RevitHelpers.StartTransaction(doc, $"Import {Path.GetFileName(path)}");
        var options = new ImageTypeOptions(path, false, ImageTypeSource.Import);
        if (path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) options.PageNumber = (int)(args.OptLong("pdf_page") ?? 1);
        var type = ImageType.Create(doc, options);
        var image = ImageInstance.Create(doc, view, type.Id, new ImagePlacementOptions(center, BoxPlacement.Center));
        if (args.OptDouble("width") is { } w)
        {
            image.LockProportions = true;
            image.Width = RevitHelpers.ToInternal(doc, w);
        }
        t.Commit();
        return ToolResult.Ok(new { imageId = image.Id.Value, view = view.Name });
    }
}

public sealed class CreateDraftingViewTool : IRevitTool
{
    public string Name => "create_drafting_view";
    public string Description =>
        "Create a drafting view and draw 2D linework and text in it: lines, polylines, rectangles, circles, arcs and " +
        "notes. Use this to turn a hand sketch or a detail image into clean Revit linework: read the sketch, decide " +
        "real-world dimensions (project units), then draw. Optionally choose a line style per item.";
    public string PropertiesJson => """
    {
      "name": {"type": "string"},
      "scale": {"type": "integer", "description": "e.g. 10 for 1:10 (default 20)."},
      "lines": {"type": "array", "items": {"type": "object", "properties": {
        "start": {"type": "array", "items": {"type": "number"}}, "end": {"type": "array", "items": {"type": "number"}},
        "style": {"type": "string"}}, "required": ["start", "end"]}},
      "polylines": {"type": "array", "items": {"type": "object", "properties": {
        "points": {"type": "array", "items": {"type": "array", "items": {"type": "number"}}},
        "closed": {"type": "boolean"}, "style": {"type": "string"}}, "required": ["points"]}},
      "circles": {"type": "array", "items": {"type": "object", "properties": {
        "center": {"type": "array", "items": {"type": "number"}}, "radius": {"type": "number"},
        "start_degrees": {"type": "number"}, "end_degrees": {"type": "number"}, "style": {"type": "string"}},
        "required": ["center", "radius"]}, "description": "Full circles, or arcs when start/end angles are given."},
      "texts": {"type": "array", "items": {"type": "object", "properties": {
        "point": {"type": "array", "items": {"type": "number"}}, "text": {"type": "string"}}, "required": ["point", "text"]}}
    }
    """;
    public string[] Required => ["name"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
            .FirstOrDefault(v => v.ViewFamily == ViewFamily.Drafting)
            ?? throw new InvalidOperationException("No drafting view type in this model.");
        var styles = new FilteredElementCollector(doc).OfClass(typeof(GraphicsStyle)).Cast<GraphicsStyle>()
            .Where(g => g.GraphicsStyleType == GraphicsStyleType.Projection
                        && g.GraphicsStyleCategory?.Parent?.Id.Value == (long)BuiltInCategory.OST_Lines).ToList();
        // Revit wraps its system line styles in angle brackets ("<Wide Lines>"); accept the name with or without them.
        static string Bare(string? n) => (n ?? "").Trim().TrimStart('<').TrimEnd('>').Trim();
        var styleCache = new Dictionary<string, GraphicsStyle?>(StringComparer.OrdinalIgnoreCase);
        GraphicsStyle? Style(JsonElement item)
        {
            if (!item.TryGetProperty("style", out var s)) return null;
            var wanted = s.GetString() ?? "";
            if (styleCache.TryGetValue(wanted, out var hit)) return hit;
            hit = styles.FirstOrDefault(g => string.Equals(g.Name, wanted.Trim(), StringComparison.OrdinalIgnoreCase))
                  ?? styles.FirstOrDefault(g => string.Equals(Bare(g.Name), Bare(wanted), StringComparison.OrdinalIgnoreCase));
            styleCache[wanted] = hit;
            return hit;
        }

        int curves = 0;
        var missingStyles = new HashSet<string>();
        using var t = RevitHelpers.StartTransaction(doc, $"Drafting view {args.String("name")}");
        var view = ViewDrafting.Create(doc, vft.Id);
        try { view.Name = args.String("name"); } catch (Autodesk.Revit.Exceptions.ArgumentException) { }
        view.Scale = (int)(args.OptLong("scale") ?? 20);

        void Draw(Curve c, JsonElement item)
        {
            var dc = doc.Create.NewDetailCurve(view, c);
            if (Style(item) is { } gs) dc.LineStyle = gs;
            else if (item.TryGetProperty("style", out var s)) missingStyles.Add(s.GetString() ?? "");
            curves++;
        }

        foreach (var l in args.Array("lines"))
        {
            XYZ a = Modeling.Point(doc, l.GetProperty("start")), b = Modeling.Point(doc, l.GetProperty("end"));
            if (a.DistanceTo(b) > doc.Application.ShortCurveTolerance) Draw(Line.CreateBound(a, b), l);
        }
        foreach (var pl in args.Array("polylines"))
        {
            var pts = pl.GetProperty("points").EnumerateArray().Select(p => Modeling.Point(doc, p)).ToList();
            var closed = pl.TryGetProperty("closed", out var c) && c.GetBoolean();
            for (int i = 0; i < pts.Count - (closed ? 0 : 1); i++)
            {
                var a = pts[i]; var b = pts[(i + 1) % pts.Count];
                if (a.DistanceTo(b) > doc.Application.ShortCurveTolerance) Draw(Line.CreateBound(a, b), pl);
            }
        }
        foreach (var ci in args.Array("circles"))
        {
            var center = Modeling.Point(doc, ci.GetProperty("center"));
            var r = RevitHelpers.ToInternal(doc, ci.GetProperty("radius").GetDouble());
            double s0 = ci.TryGetProperty("start_degrees", out var s) ? s.GetDouble() * Math.PI / 180 : 0;
            double s1 = ci.TryGetProperty("end_degrees", out var e) ? e.GetDouble() * Math.PI / 180 : 2 * Math.PI;
            if (s1 - s0 >= 2 * Math.PI - 1e-9)
            {
                Draw(Arc.Create(center, r, 0, Math.PI, XYZ.BasisX, XYZ.BasisY), ci);
                Draw(Arc.Create(center, r, Math.PI, 2 * Math.PI, XYZ.BasisX, XYZ.BasisY), ci);
            }
            else Draw(Arc.Create(center, r, s0, s1, XYZ.BasisX, XYZ.BasisY), ci);
        }
        var textType = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
        int texts = 0;
        foreach (var tx in args.Array("texts"))
        {
            TextNote.Create(doc, view.Id, Modeling.Point(doc, tx.GetProperty("point")), tx.GetProperty("text").GetString() ?? "", textType);
            texts++;
        }
        t.Commit();
        return ToolResult.Ok(new
        {
            viewId = view.Id.Value, name = view.Name, curves, texts,
            unknownLineStyles = missingStyles,
            availableLineStyles = missingStyles.Count > 0 ? SuggestStyles(styles.Select(s => s.Name).ToList(), missingStyles, Bare) : null,
        });
    }

    /// <summary>A short list of line style names: close matches to the unknown names first, then Revit's standard ones.</summary>
    private static List<string> SuggestStyles(List<string> all, HashSet<string> unknown, Func<string?, string> bare)
    {
        const int max = 12;
        var words = unknown.SelectMany(u => bare(u).Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries))
            .Where(w => w.Length > 2 && !w.Equals("lines", StringComparison.OrdinalIgnoreCase)).ToList();
        var close = all.Where(n => words.Any(w => n.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0));
        var standard = all.Where(n => n.StartsWith("<", StringComparison.Ordinal) || n is "Thin Lines" or "Medium Lines" or "Wide Lines");
        var list = close.Concat(standard).Concat(all).Distinct().Take(max).ToList();
        if (all.Count > list.Count) list.Add($"... and {all.Count - list.Count} more");
        return list;
    }
}

public sealed class CreateFamilyFromShapesTool : IRevitTool
{
    public string Name => "create_family_from_shapes";
    public string Description =>
        "Build a new loadable family from simple solids and load it into the project, e.g. from a product photo or a " +
        "manufacturer's PDF cut sheet: read the dimensions, break the object into boxes, cylinders and extruded " +
        "outlines, then call this. Coordinates and sizes in project units; the family origin is (0,0,0) at floor level. " +
        "Use place_family_instances afterwards to place it.";
    public string PropertiesJson => """
    {
      "name": {"type": "string", "description": "Family name, e.g. 'Lounge Chair - Brand X'."},
      "category": {"type": "string", "description": "Furniture, Generic Models, Casework, Specialty Equipment, Plumbing Fixtures, Furniture Systems... (default Generic Models)."},
      "shapes": {"type": "array", "items": {"type": "object", "properties": {
        "kind": {"type": "string", "enum": ["box", "cylinder", "extrusion"]},
        "origin": {"type": "array", "items": {"type": "number"}, "description": "box: min corner [x,y,z]; cylinder: base center [x,y,z]."},
        "size": {"type": "array", "items": {"type": "number"}, "description": "box: [width (x), depth (y), height (z)]."},
        "radius": {"type": "number"},
        "height": {"type": "number", "description": "cylinder/extrusion height."},
        "points": {"type": "array", "items": {"type": "array", "items": {"type": "number"}}, "description": "extrusion: plan outline [[x,y],...]."},
        "base": {"type": "number", "description": "extrusion: bottom elevation (z)."},
        "material": {"type": "string", "description": "Material name; created in the family if missing."}},
        "required": ["kind"]}},
      "type_name": {"type": "string", "description": "Name for the family type (default 'Standard')."}
    }
    """;
    public string[] Required => ["name", "shapes"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var uiapp = app;
        var doc = uiapp.ActiveUIDocument.Document;
        var name = args.String("name");
        var category = args.OptString("category") ?? "Generic Models";
        var template = FindTemplate(uiapp.Application.FamilyTemplatePath, IsMetric(doc), category)
            ?? throw new InvalidOperationException(
                $"No family template found under '{uiapp.Application.FamilyTemplatePath}'. Install the Revit family templates " +
                "(Options > File Locations > Family Template Files).");

        // Shapes are given in project units; the family document uses the same internal feet.
        var famDoc = uiapp.Application.NewFamilyDocument(template);
        try
        {
            int solids = 0;
            using (var t = new Transaction(famDoc, "Build family"))
            {
                t.Start();
                TrySetCategory(famDoc, doc, category);
                foreach (var s in args.Array("shapes"))
                {
                    var ext = MakeSolid(famDoc, doc, s);
                    if (ext is null) continue;
                    if (s.TryGetProperty("material", out var m) && m.GetString() is { Length: > 0 } matName)
                        ext.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM)?.Set(Material(famDoc, matName));
                    solids++;
                }
                var typeName = args.OptString("type_name") ?? "Standard";
                var fm = famDoc.FamilyManager;
                if (fm.CurrentType is null) fm.NewType(typeName); else fm.RenameCurrentType(typeName);
                t.Commit();
            }

            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SketchProMax", "Families");
            Directory.CreateDirectory(folder);
            var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var file = Path.Combine(folder, safe + ".rfa");
            famDoc.SaveAs(file, new SaveAsOptions { OverwriteExistingFile = true });

            var family = famDoc.LoadFamily(doc, new OverwriteFamily());
            using (var t = new Transaction(doc, "Sketch ProMax: Name family"))
            {
                t.Start();
                try { family.Name = name; } catch (Autodesk.Revit.Exceptions.ArgumentException) { }
                t.Commit();
            }
            var types = family.GetFamilySymbolIds().Select(id => new { id = id.Value, name = doc.GetElement(id).Name });
            return ToolResult.Ok(new { familyId = family.Id.Value, family = family.Name, solids, types, savedTo = file });
        }
        finally
        {
            famDoc.Close(false);
        }
    }

    private static bool IsMetric(Document doc) => doc.DisplayUnitSystem == DisplayUnit.METRIC;

    private static string? FindTemplate(string root, bool metric, string category)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return null;
        var all = Directory.GetFiles(root, "*.rft", SearchOption.AllDirectories);
        string Key(string f) => Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
        string[] wanted = category.ToLowerInvariant() switch
        {
            "furniture" => ["furniture"],
            "casework" => ["casework"],
            "specialty equipment" => ["specialty equipment"],
            "plumbing fixtures" => ["plumbing fixture"],
            "furniture systems" => ["furniture system"],
            _ => ["generic model"],
        };
        foreach (var w in wanted.Concat(["generic model"]))
        {
            var match = all.Where(f => Key(f).Contains(w) && !Key(f).Contains("face based") && !Key(f).Contains("hosted") && !Key(f).Contains("line based")
                                       && !Key(f).Contains("adaptive") && !Key(f).Contains("pattern") && !Key(f).Contains("two level"))
                .OrderByDescending(f => Key(f).Contains("metric") == metric)
                .ThenBy(f => f.Length)
                .FirstOrDefault();
            if (match != null) return match;
        }
        return null;
    }

    private static void TrySetCategory(Document famDoc, Document project, string category)
    {
        try
        {
            var bic = RevitHelpers.ParseCategory(project, category);
            var cat = famDoc.Settings.Categories.get_Item(bic);
            if (cat != null && famDoc.OwnerFamily.FamilyCategory?.Id != cat.Id) famDoc.OwnerFamily.FamilyCategory = cat;
        }
        catch (Exception ex) when (ex is ArgumentException or Autodesk.Revit.Exceptions.ApplicationException) { }
    }

    private static GenericForm? MakeSolid(Document fam, Document project, JsonElement s)
    {
        double U(JsonElement e) => RevitHelpers.ToInternal(project, e.GetDouble());
        var kind = s.GetProperty("kind").GetString();
        var profile = new CurveArray();
        double z, height;

        switch (kind)
        {
            case "box":
            {
                var o = s.TryGetProperty("origin", out var oe) ? oe.EnumerateArray().Select(U).ToArray() : [0, 0, 0];
                var size = s.GetProperty("size").EnumerateArray().Select(U).ToArray();
                z = o.Length > 2 ? o[2] : 0;
                height = size[2];
                XYZ p0 = new(o[0], o[1], z), p1 = new(o[0] + size[0], o[1], z), p2 = new(o[0] + size[0], o[1] + size[1], z), p3 = new(o[0], o[1] + size[1], z);
                profile.Append(Line.CreateBound(p0, p1)); profile.Append(Line.CreateBound(p1, p2));
                profile.Append(Line.CreateBound(p2, p3)); profile.Append(Line.CreateBound(p3, p0));
                break;
            }
            case "cylinder":
            {
                var c = s.TryGetProperty("origin", out var oe) ? oe.EnumerateArray().Select(U).ToArray() : [0, 0, 0];
                z = c.Length > 2 ? c[2] : 0;
                height = U(s.GetProperty("height"));
                var r = U(s.GetProperty("radius"));
                var center = new XYZ(c[0], c[1], z);
                profile.Append(Arc.Create(center, r, 0, Math.PI, XYZ.BasisX, XYZ.BasisY));
                profile.Append(Arc.Create(center, r, Math.PI, 2 * Math.PI, XYZ.BasisX, XYZ.BasisY));
                break;
            }
            case "extrusion":
            {
                z = s.TryGetProperty("base", out var b) ? U(b) : 0;
                height = U(s.GetProperty("height"));
                var raw = s.GetProperty("points").EnumerateArray()
                    .Select(p => p.EnumerateArray().Select(U).ToArray()).Select(a => new XYZ(a[0], a[1], z)).ToList();
                // Drop consecutive duplicates and a repeated closing point: the loop closes itself, and a zero-length edge is refused.
                var tol = fam.Application.ShortCurveTolerance;
                var pts = new List<XYZ>();
                foreach (var p in raw)
                    if (pts.Count == 0 || pts[pts.Count - 1].DistanceTo(p) > tol) pts.Add(p);
                while (pts.Count > 1 && pts[0].DistanceTo(pts[pts.Count - 1]) <= tol) pts.RemoveAt(pts.Count - 1);
                if (pts.Count < 3) throw new ArgumentException("An extrusion needs at least three distinct points.");
                for (int i = 0; i < pts.Count; i++) profile.Append(Line.CreateBound(pts[i], pts[(i + 1) % pts.Count]));
                break;
            }
            default:
                return null;
        }

        var loops = new CurveArrArray();
        loops.Append(profile);
        var plane = SketchPlane.Create(fam, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, z)));
        return fam.FamilyCreate.NewExtrusion(true, loops, plane, height);
    }

    private static ElementId Material(Document fam, string name)
    {
        var existing = new FilteredElementCollector(fam).OfClass(typeof(Material))
            .FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
        return existing?.Id ?? Autodesk.Revit.DB.Material.Create(fam, name);
    }

    private sealed class OverwriteFamily : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
        {
            overwriteParameterValues = true;
            return true;
        }

        public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
        {
            source = FamilySource.Family;
            overwriteParameterValues = true;
            return true;
        }
    }
}
