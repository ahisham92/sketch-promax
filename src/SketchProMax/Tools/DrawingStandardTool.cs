using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

/// <summary>
/// drawing_standard: learns how issued drawings are annotated (DrawingStandard), shows it, and checks a view against
/// it. It only reads the model; what it learns is kept on this PC. tag_elements_in_view takes its tag types from it.
/// </summary>
public sealed class DrawingStandardTool : IRevitTool
{
    public const string ToolName = "drawing_standard";

    /// <summary>The most views read in one call: each is a handful of view-scoped reads, and Revit must not be held long.</summary>
    public const int MaxViews = 40;

    public string Name => ToolName;
    public string Description =>
        "The office's drawing standard, learned from drawings already issued, so new views are annotated the same way. " +
        "action=learn reads the views you name (view_ids, sheet_numbers, sheet_set, or name_contains; else the active view or sheet) and keeps, " +
        "for each kind of view (plan, section, elevation, detail): the scales and view templates, the tag type and leader each category gets and how much " +
        "of it is tagged, and the dimension, text and spot level types. add=true adds to what was learned before (default: start again). " +
        "action=show gives the standard this model uses. action=check compares views (same ways to name them) with it and lists what differs, each with " +
        "the fix, and names an issued example view of the same kind to compare with by eye (capture_view_image of both). action=use makes this model " +
        "use a standard learned in another model (name), for example a documentation model's. Reads only; nothing in the model changes. " +
        "Annotate with the standard's types (tag_elements_in_view takes them by itself), then check every view before saying it is done.";
    public string PropertiesJson => """
    {
      "action": {"type": "string", "enum": ["learn", "show", "check", "use"]},
      "view_ids": {"type": "array", "items": {"type": "integer"}, "description": "Views or sheets (a sheet stands for the views on it)."},
      "sheet_numbers": {"type": "array", "items": {"type": "string"}},
      "sheet_set": {"type": "string", "description": "Name of a saved View/Sheet Set."},
      "name_contains": {"type": "string", "description": "Views or sheets whose name or number contains this text (e.g. 'BERTH 12')."},
      "name": {"type": "string", "description": "The standard's name. Default: the one this model uses, else the model's name."},
      "add": {"type": "boolean", "description": "learn: add to the standard instead of starting again."},
      "attach": {"type": "boolean", "description": "learn: this model uses the standard from now on (default true)."}
    }
    """;
    public string[] Required => ["action"];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var model = Checks.ModelKey(doc);
        var action = args.String("action").Trim().ToLowerInvariant();
        var name = args.OptString("name")?.Trim();
        var all = DrawingStandard.Load();

        if (action == "show")
        {
            if (DrawingStandard.For(all, model, name) is not { } shown) return ToolResult.Error(None(all, name));
            return ToolResult.Ok(DrawingStandard.Summary(shown));
        }
        if (action == "use")
        {
            if (string.IsNullOrEmpty(name) || !all.ContainsKey(name!))
                return ToolResult.Error("Give name, one of: " + (all.Count == 0 ? "(none learned yet)" : string.Join(", ", all.Keys)) + ".");
            DrawingStandard.Change(a =>
            {
                foreach (var s in a.Values) s.Models.RemoveAll(m => string.Equals(m, model, StringComparison.OrdinalIgnoreCase));
                if (a.TryGetValue(name!, out var chosen)) chosen.Models.Add(model);
            });
            return ToolResult.Ok(new { standard = name, used_by_this_model = true });
        }
        if (action is not ("learn" or "check")) return ToolResult.Error("action is learn, show, check or use.");

        var (views, asked) = Views(doc, args);
        if (views.Count == 0) return ToolResult.Error($"No views to read in {asked}. Name them with view_ids, sheet_numbers, sheet_set or name_contains.");
        var left = Math.Max(0, views.Count - MaxViews);
        views = views.Take(MaxViews).ToList();

        if (action == "learn")
        {
            var facts = new List<DrawingStandard.ViewFacts>();
            foreach (var v in views)
            {
                CancelPoint.ThrowIfRequested();
                // A view Revit can't list elements in (not regenerated, or of a kind it refuses) is left out, not an error.
                try { facts.Add(Read(doc, v, null)); }
                catch (Autodesk.Revit.Exceptions.ApplicationException) { }
            }
            var useful = facts.Count(f => f.Marks > 0);
            if (useful == 0) return ToolResult.Error($"The {views.Count} view(s) in {asked} carry no tags, dimensions, text or levels, so there is nothing to learn from them.");
            var key = !string.IsNullOrEmpty(name) ? name! : DrawingStandard.For(all, model)?.Name ?? doc.Title;
            DrawingStandard.Standard? saved = null;
            DrawingStandard.Change(a =>
            {
                if (!args.OptBool("add") || !a.TryGetValue(key, out var s)) a[key] = s = new DrawingStandard.Standard { Name = key };
                if (args.OptBool("attach", true))
                {
                    foreach (var other in a.Values) if (other != s) other.Models.RemoveAll(m => string.Equals(m, model, StringComparison.OrdinalIgnoreCase));
                    if (!s.Models.Contains(model, StringComparer.OrdinalIgnoreCase)) s.Models.Add(model);
                }
                s.Learned = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                s.From = s.From.Length > 0 && args.OptBool("add") ? s.From + "; " + asked : asked;
                DrawingStandard.Learn(s, facts, model);
                saved = s;
            });
            return ToolResult.Ok(new
            {
                learned_from = $"{useful} view(s) in {asked}",
                not_read_yet = left > 0 ? $"{left} more view(s): call learn again with add=true and the rest" : null,
                standard = DrawingStandard.Summary(saved!),
            });
        }

        if (DrawingStandard.For(all, model, name) is not { } standard) return ToolResult.Error(None(all, name));
        var results = new List<object>();
        var clean = 0;
        foreach (var v in views)
        {
            CancelPoint.ThrowIfRequested();
            DrawingStandard.ViewFacts f;
            try { f = Read(doc, v, standard); }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                results.Add(new { view = v.Name, view_id = v.Id.Value, could_not_read = ex.Message.Trim() });
                continue;
            }
            var issues = DrawingStandard.Check(f, standard);
            if (issues.Count == 0) clean++;
            var example = standard.Kinds.TryGetValue(f.Kind, out var kind)
                ? kind.Examples.FirstOrDefault(e => e.Id != f.Id && string.Equals(e.Model, model, StringComparison.OrdinalIgnoreCase)) ?? kind.Examples.FirstOrDefault(e => e.Id != f.Id)
                : null;
            results.Add(new
            {
                view = f.Name,
                view_id = f.Id,
                kind = f.Kind,
                matches = issues.Count == 0,
                differs = issues.Select(i => new { what = i.What, fix = i.Fix }),
                compare_with = example is null ? null : new
                {
                    view = example.Name,
                    view_id = string.Equals(example.Model, model, StringComparison.OrdinalIgnoreCase) ? example.Id : (long?)null,
                    note = "Take a picture of both (capture_view_image) and compare tag positions, dimension strings and text by eye before saying the view is done.",
                },
            });
        }
        return ToolResult.Ok(new
        {
            standard = standard.Name,
            checked_views = views.Count,
            matching = clean,
            not_checked_yet = left > 0 ? $"{left} more view(s): call check again with the rest" : null,
            views = results,
        });
    }

    private static string None(Dictionary<string, DrawingStandard.Standard> all, string? name) =>
        !string.IsNullOrEmpty(name) ? $"There is no drawing standard named '{name}'. Learned: {(all.Count == 0 ? "none" : string.Join(", ", all.Keys))}."
        : all.Count == 0 ? "No drawing standard is learned yet. Ask the user which issued sheets or views are the reference (for example the berths already submitted), then call drawing_standard action=learn with them."
        : "This model has no drawing standard yet. Learned in other models: " + string.Join(", ", all.Keys) + ". Use one (action=use, name) or learn one from this model's issued views (action=learn).";

    /// <summary>The tag type and leader the model's standard gives a category in this view; null when it has none or the type isn't loaded here.</summary>
    internal static (FamilySymbol Type, bool Leader)? Preferred(Document doc, View view, string category)
    {
        try
        {
            var standard = DrawingStandard.For(DrawingStandard.Load(), Checks.ModelKey(doc));
            if (DrawingStandard.Preferred(standard, Kind(view), category) is not { } want) return null;
            var symbol = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .FirstOrDefault(s => s.Category?.CategoryType == CategoryType.Annotation && string.Equals(TypeName(s), want.Type, StringComparison.OrdinalIgnoreCase));
            return symbol is null ? null : (symbol, want.Leader);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or Autodesk.Revit.Exceptions.ApplicationException) { return null; }
    }

    /// <summary>A tag type by its "Family: Type" name (or the type's name alone when that is unique); null when not loaded.</summary>
    internal static FamilySymbol? TagType(Document doc, string name)
    {
        var tags = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().Where(s => s.Category?.CategoryType == CategoryType.Annotation).ToList();
        var exact = tags.FirstOrDefault(s => string.Equals(TypeName(s), name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        var byType = tags.Where(s => string.Equals(s.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        return byType.Count == 1 ? byType[0] : null;
    }

    internal static string TypeName(Element? type) => type switch
    {
        FamilySymbol s => $"{s.FamilyName}: {s.Name}",
        null => "",
        _ => type.Name,
    };

    internal static string Kind(View view) => view.ViewType switch
    {
        ViewType.FloorPlan or ViewType.CeilingPlan or ViewType.EngineeringPlan or ViewType.AreaPlan => "plan",
        ViewType.Section => "section",
        ViewType.Elevation => "elevation",
        ViewType.Detail => "detail",
        ViewType.DraftingView => "drafting",
        ViewType.Legend => "legend",
        ViewType.ThreeD => "3d",
        _ => "",
    };

    /// <summary>The views the call names (sheets stand for the views on them), and how to say it.</summary>
    private static (List<View> Views, string Asked) Views(Document doc, ToolArgs args)
    {
        var found = new List<View>();
        var seen = new HashSet<long>();
        void Add(View? v)
        {
            if (v is null || v.IsTemplate) return;
            if (v is ViewSheet sheet)
            {
                foreach (var id in sheet.GetAllPlacedViews()) Add(doc.GetElement(id) as View);
                return;
            }
            if (Kind(v).Length > 0 && seen.Add(v.Id.Value)) found.Add(v);
        }
        var asked = new List<string>();
        var ids = args.LongList("view_ids");
        if (ids.Count > 0)
        {
            foreach (var id in ids) Add(doc.GetElement(new ElementId(id)) as View);
            asked.Add($"{ids.Count} view(s) given");
        }
        var numbers = args.StringList("sheet_numbers").Where(n => n.Trim().Length > 0).Select(n => n.Trim()).ToList();
        if (numbers.Count > 0)
        {
            foreach (var sheet in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                         .Where(s => numbers.Contains(s.SheetNumber, StringComparer.OrdinalIgnoreCase)).OrderBy(s => s.SheetNumber, StringComparer.OrdinalIgnoreCase))
                Add(sheet);
            asked.Add("sheets " + string.Join(", ", numbers.Take(6)) + (numbers.Count > 6 ? "…" : ""));
        }
        if (args.OptString("sheet_set") is { Length: > 0 } setName)
        {
            var set = new FilteredElementCollector(doc).OfClass(typeof(ViewSheetSet)).Cast<ViewSheetSet>()
                .FirstOrDefault(s => string.Equals(s.Name, setName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (set is not null) foreach (View v in set.Views) Add(v);
            asked.Add($"the set '{setName.Trim()}'");
        }
        if (args.OptString("name_contains") is { Length: > 0 } text)
        {
            bool Has(string s) => s.IndexOf(text.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
            foreach (var v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                         .Where(v => !v.IsTemplate && (Has(v.Name) || v is ViewSheet s && Has(s.SheetNumber))).OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
                Add(v);
            asked.Add($"views and sheets named like '{text.Trim()}'");
        }
        if (asked.Count == 0)
        {
            Add(doc.ActiveView);
            asked.Add(doc.ActiveView is ViewSheet ? $"the open sheet '{doc.ActiveView.Name}'" : $"the open view '{doc.ActiveView.Name}'");
        }
        return (found, string.Join(" and ", asked));
    }

    /// <summary>What one view holds. With a <paramref name="standard"/>, the categories it tags are counted too, tagged here or not.</summary>
    private static DrawingStandard.ViewFacts Read(Document doc, View view, DrawingStandard.Standard? standard)
    {
        var facts = new DrawingStandard.ViewFacts
        {
            Id = view.Id.Value,
            Name = view.Name,
            Kind = Kind(view),
            Scale = view.Scale,
            Template = view.ViewTemplateId != ElementId.InvalidElementId ? doc.GetElement(view.ViewTemplateId)?.Name ?? "" : "",
        };
        XYZ right = view.RightDirection, up = view.UpDirection;
        var tagged = new Dictionary<string, HashSet<long>>(StringComparer.OrdinalIgnoreCase);
        var categories = new Dictionary<string, ElementId>(StringComparer.OrdinalIgnoreCase);

        foreach (var e in new FilteredElementCollector(doc, view.Id).WherePasses(new ElementMulticlassFilter([typeof(IndependentTag), typeof(SpatialElementTag)])))
        {
            var fact = new DrawingStandard.TagFact { Type = TypeName(doc.GetElement(e.GetTypeId())) };
            var text = "";
            try
            {
                if (e is IndependentTag tag)
                {
                    fact.Leader = tag.HasLeader;
                    text = tag.TagText ?? "";
                    foreach (var link in tag.GetTaggedElementIds())
                    {
                        var local = link.LinkInstanceId == ElementId.InvalidElementId;
                        var host = local ? doc.GetElement(link.HostElementId)
                            : (doc.GetElement(link.LinkInstanceId) as RevitLinkInstance)?.GetLinkDocument()?.GetElement(link.LinkedElementId);
                        if (host?.Category is not { } cat) continue;
                        if (fact.Category.Length == 0) fact.Category = cat.Name;
                        if (!local) continue; // linked elements are named, not counted
                        categories[cat.Name] = cat.Id;
                        if (!tagged.TryGetValue(cat.Name, out var set)) tagged[cat.Name] = set = [];
                        set.Add(host.Id.Value);
                    }
                }
                else if (e is SpatialElementTag spatial)
                {
                    fact.Leader = spatial.HasLeader;
                    text = spatial.TagText ?? "";
                    fact.Category = e.Category?.Name.Replace(" Tags", "s") ?? "";
                }
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { /* a tag whose host is gone: counted by its type only */ }
            if (fact.Category.Length == 0) fact.Category = e.Category?.Name ?? "";
            fact.Empty = text.Trim().Length == 0 || text.Trim() == "?";
            if (e.get_BoundingBox(view) is { } box)
            {
                var corners = new[] { box.Min, box.Max, new XYZ(box.Min.X, box.Max.Y, box.Min.Z), new XYZ(box.Max.X, box.Min.Y, box.Max.Z) };
                fact.U0 = corners.Min(c => c.DotProduct(right));
                fact.U1 = corners.Max(c => c.DotProduct(right));
                fact.V0 = corners.Min(c => c.DotProduct(up));
                fact.V1 = corners.Max(c => c.DotProduct(up));
                fact.HasBox = fact.U1 > fact.U0 && fact.V1 > fact.V0;
            }
            facts.Tags.Add(fact);
        }

        // The standard's categories too, so a category it tags and this view doesn't is seen.
        if (standard is not null && standard.Kinds.TryGetValue(facts.Kind, out var kind))
            foreach (var name in kind.Tags.Keys.Where(n => !categories.ContainsKey(n)))
                foreach (Category cat in doc.Settings.Categories)
                    if (string.Equals(cat.Name, name, StringComparison.OrdinalIgnoreCase)) { categories[name] = cat.Id; break; }
        foreach (var pair in categories)
        {
            facts.Visible[pair.Key] = new FilteredElementCollector(doc, view.Id).OfCategoryId(pair.Value).WhereElementIsNotElementType().GetElementCount();
            facts.Tagged[pair.Key] = tagged.TryGetValue(pair.Key, out var set) ? set.Count : 0;
        }

        void Count(Dictionary<string, int> into, Element e)
        {
            var name = TypeName(doc.GetElement(e.GetTypeId()));
            if (name.Length > 0) into[name] = (into.TryGetValue(name, out var n) ? n : 0) + 1;
        }
        foreach (var e in new FilteredElementCollector(doc, view.Id).OfClass(typeof(Dimension)))
            Count(e is SpotDimension ? facts.Spots : facts.Dimensions, e);
        foreach (var e in new FilteredElementCollector(doc, view.Id).OfClass(typeof(SpotDimension)))
            if (!facts.Spots.ContainsKey(TypeName(doc.GetElement(e.GetTypeId())))) Count(facts.Spots, e);
        foreach (var e in new FilteredElementCollector(doc, view.Id).OfClass(typeof(TextNote))) Count(facts.Text, e);
        return facts;
    }
}
