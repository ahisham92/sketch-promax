using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

public sealed class ListRevisionsTool : IRevitTool
{
    public string Name => "list_revisions";
    public string Description => "List revisions (id, sequence, description, date, issued) and which sheets carry each.";
    public string PropertiesJson => "{}";
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().ToList();
        return ToolResult.Ok(Revision.GetAllRevisionIds(doc).Select(id => (Revision)doc.GetElement(id)).Select(r => new
        {
            id = r.Id.Value,
            sequence = r.SequenceNumber,
            description = r.Description,
            date = r.RevisionDate,
            issued = r.Issued,
            sheets = sheets.Where(s => s.GetAllRevisionIds().Contains(r.Id)).Select(s => s.SheetNumber),
        }));
    }
}

public sealed class CreateRevisionTool : IRevitTool
{
    public string Name => "create_revision";
    public string Description =>
        "Create a revision (e.g. a bulletin or ASI) and optionally add it to sheets' revision schedules " +
        "without needing a cloud on those sheets.";
    public string PropertiesJson => """
    {
      "description": {"type": "string"},
      "date": {"type": "string"},
      "issued_by": {"type": "string"},
      "issued_to": {"type": "string"},
      "sheet_ids": {"type": "array", "items": {"type": "integer"}}
    }
    """;
    public string[] Required => ["description"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        using var t = RevitHelpers.StartTransaction(doc, "Create revision");
        var rev = Revision.Create(doc);
        rev.Description = args.String("description");
        if (args.OptString("date") is { } d) rev.RevisionDate = d;
        if (args.OptString("issued_by") is { } by) rev.IssuedBy = by;
        if (args.OptString("issued_to") is { } to) rev.IssuedTo = to;

        var added = new List<string>();
        foreach (var id in args.LongList("sheet_ids"))
        {
            if (doc.GetElement(new ElementId(id)) is not ViewSheet sheet) continue;
            var extra = sheet.GetAdditionalRevisionIds().ToList();
            extra.Add(rev.Id);
            sheet.SetAdditionalRevisionIds(extra);
            added.Add(sheet.SheetNumber);
        }
        t.Commit();
        return ToolResult.Ok(new { revisionId = rev.Id.Value, sequence = rev.SequenceNumber, addedToSheets = added });
    }
}

public sealed class CloudElementsTool : IRevitTool
{
    public string Name => "cloud_elements";
    public string Description =>
        "Draw revision clouds around elements in a plan, section, elevation or drafting view, one cloud per element, " +
        "assigned to a revision.";
    public string PropertiesJson => """
    {
      "revision_id": {"type": "integer"},
      "element_ids": {"type": "array", "items": {"type": "integer"}},
      "view_id": {"type": "integer", "description": "Default: active view."},
      "margin": {"type": "number", "description": "Padding around each element, project units."}
    }
    """;
    public string[] Required => ["revision_id", "element_ids"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var view = args.OptLong("view_id") is { } vid ? (View)doc.GetElement(new ElementId(vid)) : doc.ActiveView;
        var revId = new ElementId(args.OptLong("revision_id") ?? -1);
        var margin = args.OptDouble("margin") is { } m ? RevitHelpers.ToInternal(doc, m) : 1.0;

        // Work in the view's own 2D frame so clouds lie on the view plane for any view direction.
        XYZ right = view.RightDirection, up = view.UpDirection, origin = view.Origin;
        var created = new List<long>();
        var skipped = new List<long>();
        using var t = RevitHelpers.StartTransaction(doc, "Revision clouds");
        foreach (var id in args.LongList("element_ids"))
        {
            var bb = doc.GetElement(new ElementId(id))?.get_BoundingBox(view);
            if (bb is null) { skipped.Add(id); continue; }
            var corners = Corners(bb).Select(p => (u: (p - origin).DotProduct(right), v: (p - origin).DotProduct(up))).ToList();
            double u0 = corners.Min(c => c.u) - margin, u1 = corners.Max(c => c.u) + margin;
            double v0 = corners.Min(c => c.v) - margin, v1 = corners.Max(c => c.v) + margin;
            XYZ P(double u, double v) => origin + right * u + up * v;
            var curves = new List<Curve>
            {
                Line.CreateBound(P(u0, v0), P(u1, v0)), Line.CreateBound(P(u1, v0), P(u1, v1)),
                Line.CreateBound(P(u1, v1), P(u0, v1)), Line.CreateBound(P(u0, v1), P(u0, v0)),
            };
            created.Add(RevisionCloud.Create(doc, view, revId, curves).Id.Value);
        }
        t.Commit();
        return ToolResult.Ok(new { clouds = created, skipped });
    }

    private static IEnumerable<XYZ> Corners(BoundingBoxXYZ bb)
    {
        foreach (var x in new[] { bb.Min.X, bb.Max.X })
            foreach (var y in new[] { bb.Min.Y, bb.Max.Y })
                foreach (var z in new[] { bb.Min.Z, bb.Max.Z })
                    yield return bb.Transform.OfPoint(new XYZ(x, y, z));
    }
}

public sealed class ListTextNotesTool : IRevitTool
{
    public string Name => "list_text_notes";
    public string Description =>
        "List text notes (id, view, text) in a view, on a sheet's views, or the whole model. Use with " +
        "update_text_notes to proofread, standardize or translate notes (e.g. English to Arabic).";
    public string PropertiesJson => """
    { "view_id": {"type": "integer", "description": "Omit for the whole model."}, "limit": {"type": "integer"} }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var collector = args.OptLong("view_id") is { } vid
            ? new FilteredElementCollector(doc, new ElementId(vid))
            : new FilteredElementCollector(doc);
        var notes = collector.OfClass(typeof(TextNote)).Cast<TextNote>().ToList();
        return ToolResult.Ok(new
        {
            total = notes.Count,
            notes = notes.Take((int)(args.OptLong("limit") ?? 300))
                .Select(n => new { id = n.Id.Value, view = doc.GetElement(n.OwnerViewId)?.Name, text = n.Text.TrimEnd('\r') }),
        });
    }
}

public sealed class UpdateTextNotesTool : IRevitTool
{
    public string Name => "update_text_notes";
    public string Description => "Replace the text of existing text notes, or create new ones at a point in a view.";
    public string PropertiesJson => """
    {
      "updates": {"type": "array", "items": {"type": "object",
        "properties": {"id": {"type": "integer"}, "text": {"type": "string"}}, "required": ["id", "text"]}},
      "create": {"type": "array", "items": {"type": "object",
        "properties": {"view_id": {"type": "integer"}, "x": {"type": "number"}, "y": {"type": "number"}, "text": {"type": "string"}},
        "required": ["view_id", "x", "y", "text"]}, "description": "x/y in project units, model coordinates (plans)."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        int updated = 0;
        var created = new List<long>();
        using var t = RevitHelpers.StartTransaction(doc, "Text notes");
        foreach (var u in args.Array("updates"))
        {
            if (doc.GetElement(new ElementId(u.GetProperty("id").GetInt64())) is TextNote n)
            {
                n.Text = u.GetProperty("text").GetString() ?? "";
                updated++;
            }
        }
        var typeId = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
        foreach (var c in args.Array("create"))
        {
            var p = new XYZ(RevitHelpers.ToInternal(doc, c.GetProperty("x").GetDouble()), RevitHelpers.ToInternal(doc, c.GetProperty("y").GetDouble()), 0);
            var note = TextNote.Create(doc, new ElementId(c.GetProperty("view_id").GetInt64()), p, c.GetProperty("text").GetString() ?? "", typeId);
            created.Add(note.Id.Value);
        }
        t.Commit();
        return ToolResult.Ok(new { updated, created });
    }
}
