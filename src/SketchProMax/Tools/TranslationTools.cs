using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

/// <summary>
/// Translation into any language, or bilingual sheets: collect_text gathers the words on drawings, Claude translates
/// them, and apply_text writes them back. Each item has a key "elementId|field" so the round trip is exact.
/// Fields: text (text notes), name (views, sheets, rooms, levels, schedules), type_name, param:Name, heading:N (schedule column).
/// </summary>
public sealed class CollectTextTool : IRevitTool
{
    public string Name => "collect_text";
    public string Description =>
        "Collect the text that appears on drawings so it can be translated into any language (French, Portuguese, " +
        "German, Arabic, Chinese...) or made bilingual: text notes, sheet names, view names, room names, level names, " +
        "schedule titles and column headings, type names, and any text parameters you name (e.g. 'Comments', 'Description'). " +
        "Translate the returned items yourself, then call apply_text with the same keys.";
    public string PropertiesJson => """
    {
      "include": {"type": "array", "items": {"type": "string", "enum": ["text_notes", "sheet_names", "view_names", "room_names", "level_names", "schedules", "type_names"]}, "description": "Default: text_notes, sheet_names, view_names, room_names, schedules."},
      "parameters": {"type": "array", "items": {"type": "string"}, "description": "Text instance parameters to include, e.g. 'Comments'."},
      "parameter_categories": {"type": "array", "items": {"type": "string"}, "description": "Categories for 'parameters' (default: Rooms, Doors, Windows)."},
      "sheet_numbers": {"type": "array", "items": {"type": "string"}, "description": "Limit text notes and views to these sheets."},
      "limit": {"type": "integer", "description": "Default 1500 items."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var include = args.StringList("include");
        if (include.Count == 0) include = ["text_notes", "sheet_names", "view_names", "room_names", "schedules"];
        var items = new List<object>();
        void Add(Element e, string field, string? text)
        {
            if (!string.IsNullOrWhiteSpace(text)) items.Add(new { key = $"{e.Id.Value}|{field}", text });
        }

        HashSet<ElementId>? onSheets = null;
        var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().ToList();
        if (args.StringList("sheet_numbers") is { Count: > 0 } numbers)
        {
            sheets = sheets.Where(s => numbers.Contains(s.SheetNumber, StringComparer.OrdinalIgnoreCase)).ToList();
            onSheets = new HashSet<ElementId>(sheets.SelectMany(s => s.GetAllPlacedViews().Append(s.Id)));
        }

        if (include.Contains("text_notes"))
            foreach (var note in new FilteredElementCollector(doc).OfClass(typeof(TextNote)).Cast<TextNote>())
                if (onSheets is null || onSheets.Contains(note.OwnerViewId)) Add(note, "text", note.Text.TrimEnd('\r', '\n'));
        if (include.Contains("sheet_names"))
            foreach (var s in sheets) Add(s, "name", s.Name);
        if (include.Contains("view_names"))
            foreach (var v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                         .Where(v => !v.IsTemplate && v is not ViewSheet && v is not ViewSchedule && (onSheets is null || onSheets.Contains(v.Id))))
                Add(v, "name", v.Name);
        if (include.Contains("room_names"))
            foreach (var r in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).OfType<Room>())
                Add(r, "name", r.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString());
        if (include.Contains("level_names"))
            foreach (var l in new FilteredElementCollector(doc).OfClass(typeof(Level))) Add(l, "name", l.Name);
        if (include.Contains("schedules"))
            foreach (var s in new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>().Where(s => !s.IsTemplate && !s.IsTitleblockRevisionSchedule))
            {
                Add(s, "name", s.Name);
                var def = s.Definition;
                for (var i = 0; i < def.GetFieldCount(); i++) Add(s, $"heading:{i}", def.GetField(i).ColumnHeading);
            }
        if (include.Contains("type_names"))
            foreach (var t in new FilteredElementCollector(doc).WhereElementIsElementType().Where(t => t.Category is { CategoryType: CategoryType.Model }))
                Add(t, "type_name", t.Name);

        if (args.StringList("parameters") is { Count: > 0 } names)
        {
            var cats = args.StringList("parameter_categories");
            if (cats.Count == 0) cats = ["Rooms", "Doors", "Windows"];
            foreach (var cat in cats)
                foreach (var e in new FilteredElementCollector(doc).OfCategory(RevitHelpers.ParseCategory(doc, cat)).WhereElementIsNotElementType())
                    foreach (var n in names)
                        if (e.LookupParameter(n) is { StorageType: StorageType.String } p) Add(e, "param:" + n, p.AsString());
        }

        var limit = (int)(args.OptLong("limit") ?? 1500);
        return ToolResult.Ok(new { total = items.Count, items = items.Take(limit), truncated = items.Count > limit });
    }
}

public sealed class ApplyTextTool : IRevitTool
{
    public string Name => "apply_text";
    public string Description =>
        "Write translated (or corrected) text back, using the keys from collect_text. mode 'replace' swaps the text; " +
        "'bilingual' keeps the original and adds the translation (text notes: on a new line; names: 'Original / Translation').";
    public string PropertiesJson => """
    {
      "items": {"type": "array", "items": {"type": "object", "properties": {"key": {"type": "string"}, "text": {"type": "string"}}, "required": ["key", "text"]}},
      "mode": {"type": "string", "enum": ["replace", "bilingual"]},
      "separator": {"type": "string", "description": "For bilingual names, default ' / '."}
    }
    """;
    public string[] Required => ["items"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var bilingual = args.OptString("mode") == "bilingual";
        var separator = args.OptString("separator") ?? " / ";
        var done = 0;
        var problems = new List<string>();

        using var t = RevitHelpers.StartTransaction(doc, bilingual ? "Bilingual text" : "Translate text");
        foreach (var item in args.Array("items"))
        {
            var key = item.GetProperty("key").GetString() ?? "";
            var text = item.GetProperty("text").GetString() ?? "";
            var bar = key.IndexOf('|');
            if (bar < 0 || !long.TryParse(key.Substring(0, bar), out var id)) { problems.Add($"{key}: bad key"); continue; }
            var field = key.Substring(bar + 1);
            var e = doc.GetElement(new ElementId(id));
            if (e is null) { problems.Add($"{key}: element not found"); continue; }
            string Combine(string? original, string glue) => bilingual && !string.IsNullOrEmpty(original) ? original + glue + text : text;
            try
            {
                switch (field)
                {
                    case "text" when e is TextNote note:
                        note.Text = Combine(note.Text.TrimEnd('\r', '\n'), "\r");
                        break;
                    case "name" when e is Room room:
                        room.get_Parameter(BuiltInParameter.ROOM_NAME).Set(Combine(room.get_Parameter(BuiltInParameter.ROOM_NAME).AsString(), separator));
                        break;
                    case "name":
                        e.Name = Combine(e.Name, separator);
                        break;
                    case "type_name":
                        e.Name = Combine(e.Name, separator);
                        break;
                    default:
                        if (field.StartsWith("heading:", StringComparison.Ordinal) && e is ViewSchedule schedule)
                        {
                            var f = schedule.Definition.GetField(int.Parse(field.Substring(8)));
                            f.ColumnHeading = Combine(f.ColumnHeading, separator);
                        }
                        else if (field.StartsWith("param:", StringComparison.Ordinal) && e.LookupParameter(field.Substring(6)) is { IsReadOnly: false } p)
                            p.Set(Combine(p.AsString(), separator));
                        else { problems.Add($"{key}: can't write this field"); continue; }
                        break;
                }
                done++;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex) { problems.Add($"{key}: {ex.Message}"); }
        }
        t.Commit();
        return ToolResult.Ok(new { updated = done, problems });
    }
}
