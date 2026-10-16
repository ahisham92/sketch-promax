using System.IO;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

/// <summary>Opening a family for editing, then loading it back over the project's copy.</summary>
internal static class FamilyEdit
{
    public static Family FindFamily(Document doc, string name, BuiltInCategory? category = null)
    {
        var families = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
            .Where(f => category is null || f.FamilyCategory?.Id.Value == (long)category.Value).ToList();
        var wanted = name.Trim();
        var colon = wanted.IndexOf(':');
        if (colon > 0) wanted = wanted.Substring(0, colon).Trim();
        return families.FirstOrDefault(f => string.Equals(f.Name, wanted, StringComparison.OrdinalIgnoreCase))
               ?? families.SingleOrDefaultOrNull(f => f.Name.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0)
               ?? throw new ArgumentException($"No family called '{name}'. Families: {string.Join(", ", families.Take(30).Select(f => f.Name))}");
    }

    private static T? SingleOrDefaultOrNull<T>(this IEnumerable<T> items, Func<T, bool> where) where T : class
    {
        var hits = items.Where(where).Take(2).ToList();
        return hits.Count == 1 ? hits[0] : null;
    }

    public static Family LoadBack(Document famDoc, Document doc) => famDoc.LoadFamily(doc, new Overwrite());

    public sealed class Overwrite : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
        {
            overwriteParameterValues = false;
            return true;
        }

        public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
        {
            source = FamilySource.Family;
            overwriteParameterValues = false;
            return true;
        }
    }
}

public sealed class CreateProjectParametersTool : IRevitTool
{
    public string Name => "create_project_parameters";
    public string Description =>
        "Create shared parameters and bind them to categories (e.g. 'Drawn By 2' on Sheets, 'Client Ref' on Project " +
        "Information, 'LOD' on model categories). Uses the firm's shared parameter file when one is set, otherwise " +
        "Sketch ProMax's own (%APPDATA%\\SketchProMax\\SharedParameters.txt). An existing parameter with the same name is " +
        "reused and bound to the extra categories. With add_to_families, the parameter is also added to those families " +
        "(e.g. the title block) so it can be shown in a label; placing it in a label still needs Edit Label in the Family Editor.";
    public string PropertiesJson => """
    {
      "parameters": {"type": "array", "items": {"type": "object", "properties": {
        "name": {"type": "string"},
        "type": {"type": "string", "enum": ["text", "multiline_text", "integer", "number", "length", "area", "volume", "yes_no", "url", "material"]},
        "categories": {"type": "array", "items": {"type": "string"}, "description": "e.g. Sheets, Project Information, Title Blocks, Walls. Use 'model' for every model category."},
        "instance": {"type": "boolean", "description": "Default true (type parameter when false)."},
        "group": {"type": "string", "enum": ["identity_data", "text", "general", "data", "constraints", "dimensions", "other"], "description": "Properties palette group (default identity_data)."},
        "add_to_families": {"type": "array", "items": {"type": "string"}, "description": "Family names to add it to, e.g. the title block family."}
      }, "required": ["name", "categories"]}},
      "shared_parameter_file": {"type": "string", "description": "Use this shared parameter file instead."}
    }
    """;
    public string[] Required => ["parameters"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var revit = app.Application;
        var original = revit.SharedParametersFilename;
        var file = args.OptString("shared_parameter_file");
        if (file is null)
            file = !string.IsNullOrEmpty(original) && File.Exists(original) ? original
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax", "SharedParameters.txt");
        if (!File.Exists(file))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "", new UTF8Encoding(false));
        }

        var results = new List<object>();
        var familyWork = new List<(string family, ExternalDefinition def, ForgeTypeId group, bool instance)>();
        try
        {
            revit.SharedParametersFilename = file;
            var defFile = revit.OpenSharedParameterFile() ?? throw new InvalidOperationException($"Revit can't read the shared parameter file '{file}'.");
            using var t = RevitHelpers.StartTransaction(doc, "Create project parameters");
            foreach (var spec in args.Array("parameters"))
            {
                var name = spec.TryGetProperty("name", out var nm) ? nm.GetString()?.Trim() ?? "" : "";
                try
                {
                    if (name.Length == 0) throw new ArgumentException("name is empty");
                    var typeName = spec.TryGetProperty("type", out var ty) ? ty.GetString() ?? "text" : "text";
                    var instance = !spec.TryGetProperty("instance", out var inst) || inst.GetBoolean();
                    var group = Group(spec.TryGetProperty("group", out var g) ? g.GetString() : null);

                    var def = defFile.Groups.Cast<DefinitionGroup>().SelectMany(gr => gr.Definitions.Cast<Definition>())
                        .OfType<ExternalDefinition>().FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
                    var created = false;
                    if (def is null)
                    {
                        var grp = defFile.Groups.get_Item("Sketch ProMax") ?? defFile.Groups.Create("Sketch ProMax");
                        def = (ExternalDefinition)grp.Definitions.Create(new ExternalDefinitionCreationOptions(name, Spec(typeName)));
                        created = true;
                    }

                    var cats = revit.Create.NewCategorySet();
                    var catNames = spec.GetProperty("categories").EnumerateArray().Select(c => c.GetString() ?? "").ToList();
                    foreach (var c in Categories(doc, catNames)) cats.Insert(c);
                    if (cats.IsEmpty) throw new ArgumentException("none of the categories can take parameters");

                    // Keep categories it is already bound to.
                    var existing = Existing(doc, def.Name);
                    if (existing is { def: var oldDef } && (oldDef is not ExternalDefinition ext || ext.GUID != def.GUID))
                        throw new ArgumentException($"the project already has a parameter called '{def.Name}' that is not this shared parameter. Rename one of them first");
                    if (existing?.binding is ElementBinding old)
                        foreach (Category c in old.Categories) if (!cats.Contains(c)) cats.Insert(c);
                    Binding binding = instance ? revit.Create.NewInstanceBinding(cats) : revit.Create.NewTypeBinding(cats);
                    var ok = existing is null ? doc.ParameterBindings.Insert(def, binding, group) : doc.ParameterBindings.ReInsert(existing.Value.def, binding, group);
                    if (!ok) throw new InvalidOperationException("Revit refused the binding (a project parameter with this name but a different type may already exist)");

                    if (spec.TryGetProperty("add_to_families", out var fams))
                        foreach (var f in fams.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)))
                            familyWork.Add((f!, def, group, instance));
                    results.Add(new { name = def.Name, type = typeName, instance, categories = cats.Cast<Category>().Select(c => c.Name), newInSharedFile = created, alreadyInProject = existing is not null });
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Autodesk.Revit.Exceptions.ApplicationException)
                {
                    results.Add(new { name, error = ex.Message.Trim() });
                }
            }
            t.Commit();
        }
        catch
        {
            RestoreSharedFile(revit, original);
            throw;
        }

        // Families are edited after the project transaction, one at a time, while the definitions' file is still current.
        var families = new List<object>();
        foreach (var byFamily in familyWork.GroupBy(w => w.family, StringComparer.OrdinalIgnoreCase))
        {
            Document? famDoc = null;
            try
            {
                var family = FamilyEdit.FindFamily(doc, byFamily.Key);
                famDoc = doc.EditFamily(family);
                var added = new List<string>();
                using (var ft = new Transaction(famDoc, "Add parameters"))
                {
                    ft.Start();
                    foreach (var w in byFamily)
                    {
                        if (famDoc.FamilyManager.get_Parameter(w.def.Name) is not null) continue;
                        famDoc.FamilyManager.AddParameter(w.def, w.group, w.instance);
                        added.Add(w.def.Name);
                    }
                    ft.Commit();
                }
                if (added.Count > 0) FamilyEdit.LoadBack(famDoc, doc);
                families.Add(new { family = family.Name, added });
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Autodesk.Revit.Exceptions.ApplicationException)
            {
                families.Add(new { family = byFamily.Key, error = ex.Message.Trim() });
            }
            finally { famDoc?.Close(false); }
        }
        RestoreSharedFile(revit, original);

        return ToolResult.Ok(new
        {
            sharedParameterFile = file, parameters = results, families = families.Count > 0 ? families : null,
            note = families.Count > 0 ? "To show a new parameter on the title block, add it to a label (Family Editor > Edit Label); Revit's API can't create labels." : null,
        });
    }

    /// <summary>Puts back the user's own shared parameter file setting, even when it was empty.</summary>
    private static void RestoreSharedFile(Autodesk.Revit.ApplicationServices.Application revit, string? original)
    {
        try { revit.SharedParametersFilename = original ?? ""; }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or ArgumentException) { }
    }

    private static (Definition def, Binding binding)? Existing(Document doc, string name)
    {
        var it = doc.ParameterBindings.ForwardIterator();
        while (it.MoveNext())
            if (it.Key is { } d && string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)) return (d, (Binding)it.Current);
        return null;
    }

    private static IEnumerable<Category> Categories(Document doc, List<string> names)
    {
        var seen = new HashSet<long>();
        foreach (var n in names)
        {
            IEnumerable<Category> cats = n.Trim().Equals("model", StringComparison.OrdinalIgnoreCase)
                ? doc.Settings.Categories.Cast<Category>().Where(c => c.CategoryType == CategoryType.Model && c.AllowsBoundParameters && c.Parent is null)
                : [Category.GetCategory(doc, RevitHelpers.ParseCategory(doc, n)) ?? throw new ArgumentException($"Unknown category '{n}'.")];
            foreach (var c in cats)
            {
                if (!c.AllowsBoundParameters) throw new ArgumentException($"Category '{c.Name}' can't take parameters.");
                if (seen.Add(c.Id.Value)) yield return c;
            }
        }
    }

    private static ForgeTypeId Spec(string type) => type switch
    {
        "multiline_text" => SpecTypeId.String.MultilineText,
        "integer" => SpecTypeId.Int.Integer,
        "number" => SpecTypeId.Number,
        "length" => SpecTypeId.Length,
        "area" => SpecTypeId.Area,
        "volume" => SpecTypeId.Volume,
        "yes_no" => SpecTypeId.Boolean.YesNo,
        "url" => SpecTypeId.String.Url,
        "material" => SpecTypeId.Reference.Material,
        _ => SpecTypeId.String.Text,
    };

    private static ForgeTypeId Group(string? group) => group switch
    {
        "text" => GroupTypeId.Text,
        "general" => GroupTypeId.General,
        "data" => GroupTypeId.Data,
        "constraints" => GroupTypeId.Constraints,
        "dimensions" => GroupTypeId.Geometry,
        "other" => new ForgeTypeId(""),
        _ => GroupTypeId.IdentityData,
    };
}

public sealed class EditFamilyTextTool : IRevitTool
{
    public string Name => "edit_family_text";
    public string Description =>
        "List or change the fixed text inside a family such as a title block (company name, address, disclaimers, " +
        "stamps): replace text everywhere, set a note by id, or add new text, then reload the family into the project. " +
        "Labels (text that shows parameters) are listed by the Revit UI only; change their values with fill_title_blocks.";
    public string PropertiesJson => """
    {
      "family": {"type": "string", "description": "Family name, e.g. the title block family from list_title_blocks."},
      "replace": {"type": "array", "items": {"type": "object", "properties": {"find": {"type": "string"}, "with": {"type": "string"}}, "required": ["find", "with"]}},
      "set": {"type": "array", "items": {"type": "object", "properties": {"id": {"type": "integer"}, "text": {"type": "string"}}, "required": ["id", "text"]}},
      "add": {"type": "array", "items": {"type": "object", "properties": {"text": {"type": "string"}, "at": {"type": "array", "items": {"type": "number"}, "description": "[x, y] in mm on the sheet, from the family origin."}}, "required": ["text", "at"]}}
    }
    """;
    public string[] Required => ["family"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var family = FamilyEdit.FindFamily(doc, args.String("family"));
        if (!family.IsEditable) return ToolResult.Error($"'{family.Name}' is a system or in-place family and can't be opened for editing.");
        var famDoc = doc.EditFamily(family);
        try
        {
            var notes = new FilteredElementCollector(famDoc).OfClass(typeof(TextNote)).Cast<TextNote>().ToList();
            var changing = args.Has("replace") || args.Has("set") || args.Has("add");
            if (!changing)
                return ToolResult.Ok(new { family = family.Name, textNotes = notes.Select(n => new { id = n.Id.Value, text = n.Text.TrimEnd('\r', '\n') }) });

            var changed = 0;
            var problems = new List<string>();
            using (var t = new Transaction(famDoc, "Edit text"))
            {
                t.Start();
                foreach (var r in args.Array("replace"))
                {
                    var find = r.GetProperty("find").GetString() ?? "";
                    var with = r.GetProperty("with").GetString() ?? "";
                    if (find.Length == 0) continue;
                    var hits = 0;
                    foreach (var n in notes.Where(n => n.Text.IndexOf(find, StringComparison.Ordinal) >= 0))
                    {
                        n.Text = n.Text.Replace(find, with);
                        hits++;
                    }
                    if (hits == 0) problems.Add($"'{find}' was not found in any text");
                    changed += hits;
                }
                foreach (var s in args.Array("set"))
                {
                    var id = s.GetProperty("id").GetInt64();
                    if (famDoc.GetElement(new ElementId(id)) is TextNote n) { n.Text = s.GetProperty("text").GetString() ?? ""; changed++; }
                    else problems.Add($"{id} is not a text note in this family");
                }
                var view = new FilteredElementCollector(famDoc).OfClass(typeof(View)).Cast<View>().FirstOrDefault(v => !v.IsTemplate && v.ViewType is not ViewType.ProjectBrowser and not ViewType.SystemBrowser);
                var type = new FilteredElementCollector(famDoc).OfClass(typeof(TextNoteType)).FirstElementId();
                foreach (var a in args.Array("add"))
                {
                    if (view is null || type == ElementId.InvalidElementId) { problems.Add("this family has no view or text type to add text to"); break; }
                    var at = a.GetProperty("at").EnumerateArray().Select(x => x.GetDouble() / 304.8).ToArray();
                    if (at.Length < 2) { problems.Add("'at' needs [x, y]"); continue; }
                    TextNote.Create(famDoc, view.Id, new XYZ(at[0], at[1], 0), a.GetProperty("text").GetString() ?? "", type);
                    changed++;
                }
                t.Commit();
            }
            if (changed == 0) return ToolResult.Error($"Nothing changed in '{family.Name}': {string.Join("; ", problems)}");
            FamilyEdit.LoadBack(famDoc, doc);
            return ToolResult.Ok(new { family = family.Name, changed, problems });
        }
        finally { famDoc.Close(false); }
    }
}

public sealed class TitleBlockFromCadTool : IRevitTool
{
    public string Name => "title_block_from_cad";
    public string Description =>
        "Make a title block family from a DWG/DXF title block: starts from Revit's title block template for the sheet " +
        "size, imports the drawing at the origin, saves the .rfa and loads it. The CAD text stays as CAD; add labels for " +
        "sheet fields in the Family Editor, or keep fixed text. To copy a title block from another Revit project or an .rfa, use copy_from_file.";
    public string PropertiesJson => """
    {
      "path": {"type": "string", "description": "DWG or DXF file on this PC."},
      "name": {"type": "string", "description": "Family name, e.g. 'A1 Firm Title Block'."},
      "size": {"type": "string", "description": "Template to start from, e.g. A0, A1, A3, Arch D (default A1 / Arch D)."},
      "units": {"type": "string", "enum": ["auto", "mm", "cm", "m", "in", "ft"], "description": "Units of the DWG (default auto)."},
      "keep_template_border": {"type": "boolean", "description": "Keep the template's own lines (default false)."},
      "replace_existing": {"type": "boolean", "description": "Overwrite a loaded family with the same name (default false)."}
    }
    """;
    public string[] Required => ["path", "name"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var path = args.String("path");
        if (!File.Exists(path)) return ToolResult.Error($"File not found: {path}");
        var name = args.String("name").Trim();
        if (new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().Any(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) && !args.OptBool("replace_existing"))
            return ToolResult.Error($"A family called '{name}' is already loaded. Pick another name, or pass replace_existing true to overwrite it.");
        var metric = doc.GetUnits().GetFormatOptions(SpecTypeId.Length).GetUnitTypeId() != UnitTypeId.Feet
                     && doc.GetUnits().GetFormatOptions(SpecTypeId.Length).GetUnitTypeId() != UnitTypeId.FeetFractionalInches;
        var root = app.Application.FamilyTemplatePath;
        var templates = !string.IsNullOrEmpty(root) && Directory.Exists(root)
            ? Directory.GetFiles(root, "*.rft", SearchOption.AllDirectories).Where(f => f.IndexOf("titleblock", StringComparison.OrdinalIgnoreCase) >= 0
                || Path.GetDirectoryName(f)!.IndexOf("Title", StringComparison.OrdinalIgnoreCase) >= 0).ToList()
            : [];
        if (templates.Count == 0)
            return ToolResult.Error($"No title block templates found under '{root}'. Install Revit's family templates (Options > File Locations).");
        var size = (args.OptString("size") ?? (metric ? "A1" : "D")).Trim();
        var template = templates
            .Where(f => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileNameWithoutExtension(f), $@"(^|[\s_-]){System.Text.RegularExpressions.Regex.Escape(size)}([\s_-]|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .OrderByDescending(f => (f.IndexOf("metric", StringComparison.OrdinalIgnoreCase) >= 0) == metric).FirstOrDefault()
            ?? templates.OrderByDescending(f => (f.IndexOf("metric", StringComparison.OrdinalIgnoreCase) >= 0) == metric).First();

        var famDoc = app.Application.NewFamilyDocument(template);
        try
        {
            var view = new FilteredElementCollector(famDoc).OfClass(typeof(View)).Cast<View>()
                .First(v => !v.IsTemplate && v.ViewType is not ViewType.ProjectBrowser and not ViewType.SystemBrowser);
            int removed = 0;
            using (var t = new Transaction(famDoc, "Import title block"))
            {
                t.Start();
                if (!args.OptBool("keep_template_border"))
                {
                    var lines = new FilteredElementCollector(famDoc).OfClass(typeof(CurveElement)).ToElementIds();
                    removed = lines.Count;
                    if (lines.Count > 0) famDoc.Delete(lines);
                }
                var options = new DWGImportOptions
                {
                    Placement = ImportPlacement.Origin,
                    ThisViewOnly = true,
                    ColorMode = ImportColorMode.BlackAndWhite,
                    Unit = args.OptString("units") switch
                    {
                        "mm" => ImportUnit.Millimeter, "cm" => ImportUnit.Centimeter, "m" => ImportUnit.Meter,
                        "in" => ImportUnit.Inch, "ft" => ImportUnit.Foot, _ => ImportUnit.Default,
                    },
                };
                if (!famDoc.Import(path, options, view, out _)) throw new InvalidOperationException("Revit could not import that file.");
                t.Commit();
            }
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SketchProMax", "Families");
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ".rfa");
            famDoc.SaveAs(file, new SaveAsOptions { OverwriteExistingFile = true });
            var family = famDoc.LoadFamily(doc, new FamilyEdit.Overwrite());
            using (var t = RevitHelpers.StartTransaction(doc, "Name title block"))
            {
                try { family.Name = name; } catch (Autodesk.Revit.Exceptions.ArgumentException) { }
                t.Commit();
            }
            return ToolResult.Ok(new
            {
                family = family.Name, types = family.GetFamilySymbolIds().Select(id => doc.GetElement(id).Name),
                template = Path.GetFileName(template), templateLinesRemoved = removed, savedTo = file,
                next = "Use set_sheet_title_block to put it on sheets. Check that the drawing sits at the sheet origin and at the right scale with capture_view_image of a sheet.",
            });
        }
        finally { famDoc.Close(false); }
    }
}

public sealed class CopyFromFileTool : IRevitTool
{
    public string Name => "copy_from_file";
    public string Description =>
        "Bring title blocks, legends, drafting views (typical details, general notes) or schedules in from another Revit " +
        "project or template (.rvt/.rte), or load an .rfa family. The other file is opened in the background " +
        "(detached if workshared) and closed without saving. Types that already exist keep this project's version. " +
        "After copying title blocks, the shared parameters their labels use (drawing number parts, phase, zone...) are bound to " +
        "Sheets in this project with the same GUIDs, so the labels fill in from each sheet; the result lists what was bound.";
    public string PropertiesJson => """
    {
      "path": {"type": "string", "description": ".rvt, .rte or .rfa file on this PC."},
      "title_blocks": {"type": "array", "items": {"type": "string"}, "description": "Title block family or 'Family: Type' names to copy; ['*'] for all."},
      "bind_title_block_parameters": {"type": "string", "enum": ["sheets", "project_information", "none"], "description": "Where to bind the copied title blocks' shared label parameters (default sheets)."},
      "views": {"type": "array", "items": {"type": "string"}, "description": "Names of legends, drafting views or schedules to copy; ['*legends'] or ['*drafting'] for all of a kind."},
      "list_only": {"type": "boolean", "description": "Just list what the file has."}
    }
    """;
    public string[] Required => ["path"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var path = args.String("path");
        if (!File.Exists(path)) return ToolResult.Error($"File not found: {path}");
        if (path.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase))
        {
            using var t = RevitHelpers.StartTransaction(doc, "Load family");
            var loaded = doc.LoadFamily(path, new FamilyEdit.Overwrite(), out var fam);
            t.Commit();
            return ToolResult.Ok(new { loaded, family = fam?.Name ?? Path.GetFileNameWithoutExtension(path), note = loaded ? null : "Already loaded and unchanged." });
        }

        var open = app.Application.Documents.Cast<Document>().FirstOrDefault(d => string.Equals(d.PathName, path, StringComparison.OrdinalIgnoreCase));
        var source = open ?? app.Application.OpenDocumentFile(ModelPathUtils.ConvertUserVisiblePathToModelPath(path),
            new OpenOptions { DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets, Audit = false });
        try
        {
            var titleBlocks = new FilteredElementCollector(source).OfClass(typeof(FamilySymbol)).OfCategory(BuiltInCategory.OST_TitleBlocks)
                .Cast<FamilySymbol>().ToList();
            var views = new FilteredElementCollector(source).OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate && (v.ViewType is ViewType.Legend or ViewType.DraftingView || v is ViewSchedule { IsTitleblockRevisionSchedule: false }))
                .ToList();
            if (args.OptBool("list_only"))
                return ToolResult.Ok(new
                {
                    file = Path.GetFileName(path),
                    titleBlocks = titleBlocks.Select(s => $"{s.FamilyName}: {s.Name}"),
                    legends = views.Where(v => v.ViewType == ViewType.Legend).Select(v => v.Name),
                    draftingViews = views.Where(v => v.ViewType == ViewType.DraftingView).Select(v => v.Name).Take(300),
                    schedules = views.OfType<ViewSchedule>().Select(v => v.Name).Take(300),
                });

            var wantedTb = args.StringList("title_blocks");
            var pickTb = titleBlocks.Where(s => wantedTb.Contains("*") || wantedTb.Any(w =>
                string.Equals(w, s.FamilyName, StringComparison.OrdinalIgnoreCase) || string.Equals(w, $"{s.FamilyName}: {s.Name}", StringComparison.OrdinalIgnoreCase))).ToList();
            var wantedViews = args.StringList("views");
            var pickViews = views.Where(v => wantedViews.Any(w =>
                string.Equals(w, v.Name, StringComparison.OrdinalIgnoreCase)
                || (w == "*legends" && v.ViewType == ViewType.Legend)
                || (w == "*drafting" && v.ViewType == ViewType.DraftingView)
                || (w == "*schedules" && v is ViewSchedule))).ToList();
            var missing = wantedTb.Where(w => w != "*" && !pickTb.Any(s => string.Equals(w, s.FamilyName, StringComparison.OrdinalIgnoreCase) || string.Equals(w, $"{s.FamilyName}: {s.Name}", StringComparison.OrdinalIgnoreCase)))
                .Concat(wantedViews.Where(w => !w.StartsWith("*") && !pickViews.Any(v => string.Equals(w, v.Name, StringComparison.OrdinalIgnoreCase)))).ToList();
            if (pickTb.Count == 0 && pickViews.Count == 0)
                return ToolResult.Error($"Nothing to copy. {(missing.Count > 0 ? $"Not in the file: {string.Join(", ", missing)}. " : "")}Call with list_only to see what it has.");

            var options = new CopyPasteOptions();
            options.SetDuplicateTypeNamesHandler(new UseDestinationTypes());
            var copied = new List<object>();
            var errors = new List<object>();
            using var t = RevitHelpers.StartTransaction(doc, "Copy from file");
            if (pickTb.Count > 0)
            {
                try
                {
                    var ids = ElementTransformUtils.CopyElements(source, pickTb.Select(s => s.Id).ToList(), doc, Transform.Identity, options);
                    copied.Add(new { titleBlocks = pickTb.Select(s => $"{s.FamilyName}: {s.Name}"), elements = ids.Count });
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex) { errors.Add(new { what = "title blocks", error = ex.Message.Trim() }); }
            }
            foreach (var v in pickViews)
            {
                try
                {
                    var ids = ElementTransformUtils.CopyElements(source, [v.Id], doc, Transform.Identity, options);
                    var newView = ids.Select(doc.GetElement).OfType<View>().FirstOrDefault();
                    if (newView != null && v is not ViewSchedule)
                    {
                        // Legend and drafting view contents come across with a second, view-to-view copy.
                        var contents = new FilteredElementCollector(source, v.Id).WhereElementIsNotElementType()
                            .Where(e => e.Category != null && e.OwnerViewId == v.Id).Select(e => e.Id).ToList();
                        if (contents.Count > 0) ElementTransformUtils.CopyElements(v, contents, newView, Transform.Identity, options);
                    }
                    copied.Add(new { view = v.Name, kind = v.ViewType.ToString(), newId = newView?.Id.Value, newName = newView?.Name });
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex) { errors.Add(new { view = v.Name, error = ex.Message.Trim() }); }
            }
            if (copied.Count == 0) { t.RollBack(); return ToolResult.Error($"Nothing was copied: {JsonSerializer.Serialize(errors)}"); }
            t.Commit();

            // Labels show sheet values only when the project has the same shared parameters (same GUIDs) on Sheets.
            object? parameters = null;
            var bindTo = args.OptString("bind_title_block_parameters") ?? "sheets";
            if (pickTb.Count > 0 && bindTo != "none")
            {
                var families = pickTb.Select(s => s.FamilyName).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(n => new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().FirstOrDefault(f => string.Equals(f.Name, n, StringComparison.OrdinalIgnoreCase)))
                    .OfType<Family>().ToList();
                parameters = TitleBlockParameters.Bind(app, doc, families, bindTo == "project_information" ? BuiltInCategory.OST_ProjectInformation : BuiltInCategory.OST_Sheets);
            }
            return ToolResult.Ok(new { file = Path.GetFileName(path), copied, errors, notFound = missing, titleBlockParameters = parameters });
        }
        finally { if (open is null) source.Close(false); }
    }

    private sealed class UseDestinationTypes : IDuplicateTypeNamesHandler
    {
        public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args) => DuplicateTypeAction.UseDestinationTypes;
    }
}

public sealed class PlaceLegendOnSheetsTool : IRevitTool
{
    public string Name => "place_legend_on_sheets";
    public string Description =>
        "Place a legend (general notes, key plan, symbols) on many sheets at the same spot, e.g. just left of the title " +
        "block's bottom-right corner. Sheets that already show that legend are skipped. Legends can be on any number of sheets.";
    public string PropertiesJson => """
    {
      "legend": {"type": "string", "description": "Legend view name."},
      "sheet_numbers": {"type": "array", "items": {"type": "string"}, "description": "Default: every sheet."},
      "number_starts_with": {"type": "string"},
      "position": {"type": "string", "enum": ["top_right", "bottom_right", "top_left", "bottom_left"], "description": "Corner of the drawing area (default top_right)."},
      "margin": {"type": "number", "description": "Gap from the title block edge, mm on the sheet (default 15)."}
    }
    """;
    public string[] Required => ["legend"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var legends = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate && v.ViewType == ViewType.Legend).ToList();
        if (legends.Count == 0)
            return ToolResult.Error("The model has no legends. Make one with create_legend (it copies one from another open model or a saved legend seed), copy one in with copy_from_file, or make one by hand once (View > Legends > Legend).");
        var wanted = args.String("legend").Trim();
        var legend = legends.FirstOrDefault(v => string.Equals(v.Name, wanted, StringComparison.OrdinalIgnoreCase))
                     ?? (legends.Where(v => v.Name.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0).Take(2).ToList() is { Count: 1 } one ? one[0] : null)
                     ?? throw new ArgumentException($"No single legend matches '{wanted}'. Legends: {string.Join(", ", legends.Select(v => v.Name))}");
        var sheets = TitleBlocks.Sheets(doc, args);
        var bySheet = TitleBlocks.BySheet(doc);
        var margin = (args.OptDouble("margin") ?? 15) / 304.8;
        var position = args.OptString("position") ?? "top_right";

        var placed = new List<string>();
        var skipped = new List<object>();
        using var t = RevitHelpers.StartTransaction(doc, "Place legend on sheets");
        foreach (var sheet in sheets)
        {
            try
            {
                if (new FilteredElementCollector(doc, sheet.Id).OfClass(typeof(Viewport)).Cast<Viewport>().Any(vp => vp.ViewId == legend.Id))
                { skipped.Add(new { sheet = sheet.SheetNumber, reason = "already has it" }); continue; }
                BoundingBoxXYZ? area = bySheet.TryGetValue(sheet.Id.Value, out var tbs) ? tbs[0].get_BoundingBox(sheet) : null;
                if (area is null) { skipped.Add(new { sheet = sheet.SheetNumber, reason = "no title block to place against" }); continue; }
                var vp = Viewport.Create(doc, sheet.Id, legend.Id, (area.Min + area.Max) / 2);
                doc.Regenerate();
                var box = vp.GetBoxOutline();
                double w = box.MaximumPoint.X - box.MinimumPoint.X, h = box.MaximumPoint.Y - box.MinimumPoint.Y;
                var x = position.EndsWith("right") ? area.Max.X - margin - w / 2 : area.Min.X + margin + w / 2;
                var y = position.StartsWith("top") ? area.Max.Y - margin - h / 2 : area.Min.Y + margin + h / 2;
                vp.SetBoxCenter(new XYZ(x, y, 0));
                placed.Add(sheet.SheetNumber);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex) { skipped.Add(new { sheet = sheet.SheetNumber, reason = ex.Message.Trim() }); }
        }
        if (placed.Count == 0)
        {
            t.RollBack();
            return skipped.Count > 0 ? ToolResult.Ok(new { legend = legend.Name, placed = 0, skipped }) : ToolResult.Error("No sheets matched.");
        }
        t.Commit();
        return ToolResult.Ok(new
        {
            legend = legend.Name, placed = placed.Count, sheets = placed, skipped,
            note = "The legend is placed against the title block's outer edge. If it overlaps the title block's own panel, use a different position or margin.",
        });
    }
}

public sealed class SetSheetRevisionsTool : IRevitTool
{
    public string Name => "set_sheet_revisions";
    public string Description =>
        "Add or remove a revision on many sheets at once (shows in each sheet's revision schedule without a cloud), and " +
        "optionally mark the revision as issued. Pick the revision by its number, description or id (list_revisions).";
    public string PropertiesJson => """
    {
      "revision": {"type": "string", "description": "Revision number, description or id."},
      "sheet_numbers": {"type": "array", "items": {"type": "string"}, "description": "Default: every sheet."},
      "number_starts_with": {"type": "string"},
      "action": {"type": "string", "enum": ["add", "remove"], "description": "Default add."},
      "issue": {"type": "boolean", "description": "Mark the revision issued afterwards (locks it against clouds)."}
    }
    """;
    public string[] Required => ["revision"];
    public bool ModifiesModel => true;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var revisions = new FilteredElementCollector(doc).OfClass(typeof(Revision)).Cast<Revision>().ToList();
        var key = args.String("revision").Trim();
        var revision = revisions.FirstOrDefault(r => r.Id.Value.ToString() == key)
                       ?? revisions.FirstOrDefault(r => string.Equals(r.RevisionNumber, key, StringComparison.OrdinalIgnoreCase))
                       ?? revisions.FirstOrDefault(r => string.Equals(r.Description, key, StringComparison.OrdinalIgnoreCase))
                       ?? throw new ArgumentException($"No revision '{key}'. Revisions: {string.Join(", ", revisions.Select(r => $"{r.RevisionNumber} {r.Description}"))}");
        var remove = args.OptString("action") == "remove";
        var sheets = TitleBlocks.Sheets(doc, args);

        var changed = new List<string>();
        var skipped = new List<object>();
        using var t = RevitHelpers.StartTransaction(doc, remove ? "Remove revision from sheets" : "Add revision to sheets");
        // Issued revisions are locked; open it for the change and put the state back afterwards.
        var wasIssued = revision.Issued;
        if (wasIssued) revision.Issued = false;
        foreach (var sheet in sheets)
        {
            var ids = sheet.GetAdditionalRevisionIds().ToList();
            var has = ids.Contains(revision.Id);
            if (remove == !has) { skipped.Add(new { sheet = sheet.SheetNumber, reason = remove ? "not on this sheet (or only through a cloud)" : "already on it" }); continue; }
            if (remove) ids.Remove(revision.Id); else ids.Add(revision.Id);
            try { sheet.SetAdditionalRevisionIds(ids); changed.Add(sheet.SheetNumber); }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex) { skipped.Add(new { sheet = sheet.SheetNumber, reason = ex.Message.Trim() }); }
        }
        revision.Issued = args.Has("issue") ? args.OptBool("issue") : wasIssued;
        t.Commit();
        return ToolResult.Ok(new
        {
            revision = $"{revision.RevisionNumber} {revision.Description}".Trim(), action = remove ? "removed" : "added",
            sheets = changed.Count, changed = changed.Take(200), skipped = skipped.Take(100), issued = revision.Issued,
        });
    }
}

public sealed class ExportDrawingListTool : IRevitTool
{
    public string Name => "export_drawing_list";
    public string Description =>
        "Export the list of drawings (LOD / drawing register) to a CSV that opens in Excel: sheet number, name, current " +
        "revision with its date and description, plus any extra sheet fields (Drawn By, Checked By, scale, ...). With " +
        "check_folder it also checks that every sheet's exported PDF/DWG is there under the expected name (name_pattern) " +
        "and lists files that don't match any sheet. For a drawing list schedule inside Revit, use create_sheet_list.";
    public string PropertiesJson => """
    {
      "sheet_numbers": {"type": "array", "items": {"type": "string"}, "description": "Default: every sheet."},
      "number_starts_with": {"type": "string"},
      "fields": {"type": "array", "items": {"type": "string"}, "description": "Extra sheet or title block fields, e.g. Drawn By, Checked By, Sheet Issue Date, Scale."},
      "folder": {"type": "string"},
      "file_name": {"type": "string"},
      "check_folder": {"type": "string", "description": "Folder of exported drawings to check against the list."},
      "name_pattern": {"type": "string", "description": "Expected file name per sheet, as in export_pdf (default '{number} - {name}'). {date} means today, so leave it out when checking older exports."},
      "extensions": {"type": "array", "items": {"type": "string"}, "description": "File types to check (default ['pdf'])."}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var sheets = TitleBlocks.Sheets(doc, args);
        if (sheets.Count == 0) return ToolResult.Error("The model has no sheets (placeholders excluded).");
        var bySheet = TitleBlocks.BySheet(doc);
        var fields = args.StringList("fields");
        string Csv(string? s) => s is null ? "" : s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        string? Field(ViewSheet sheet, string name)
        {
            var p = sheet.LookupParameter(name);
            if (p is null && bySheet.TryGetValue(sheet.Id.Value, out var tbs)) p = tbs[0].LookupParameter(name) ?? tbs[0].Symbol.LookupParameter(name);
            return p is null ? null : RevitHelpers.ParamValue(p);
        }

        var rows = new List<Dictionary<string, string?>>();
        foreach (var sheet in sheets)
        {
            var current = doc.GetElement(sheet.GetCurrentRevision()) as Revision;
            var row = new Dictionary<string, string?>
            {
                ["Sheet Number"] = sheet.SheetNumber,
                ["Sheet Name"] = sheet.Name,
                ["Current Revision"] = sheet.get_Parameter(BuiltInParameter.SHEET_CURRENT_REVISION)?.AsString(),
                ["Revision Date"] = current?.RevisionDate,
                ["Revision Description"] = current?.Description,
            };
            foreach (var f in fields) row[f] = Field(sheet, f);
            rows.Add(row);
        }

        var folder = OutputPaths.Checked(args.OptString("folder")) ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SketchProMax");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, (OutputPaths.Checked(args.OptString("file_name")) ?? $"{doc.Title} - Drawing list {DateTime.Now:yyyy-MM-dd}") + ".csv");
        var columns = rows[0].Keys.ToList();
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", columns.Select(Csv)));
        foreach (var r in rows) sb.AppendLine(string.Join(",", columns.Select(c => Csv(r[c]))));
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));

        object? check = null;
        if (args.OptString("check_folder") is { } checkFolder)
        {
            if (!Directory.Exists(checkFolder)) return ToolResult.Error($"Folder not found: {checkFolder}");
            var pattern = args.OptString("name_pattern") ?? "{number} - {name}";
            var exts = args.StringList("extensions").Select(e => "." + e.Trim().TrimStart('.').ToLowerInvariant()).DefaultIfEmpty(".pdf").ToList();
            var present = Directory.GetFiles(checkFolder).Where(f => exts.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .Select(f => Path.GetFileName(f)!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var missing = new List<string>();
            foreach (var sheet in sheets)
                foreach (var ext in exts)
                {
                    var name = ExportNames.For(doc, sheet, pattern) + ext;
                    expected.Add(name);
                    if (!present.Contains(name)) missing.Add(name);
                }
            var extra = present.Where(p => !expected.Contains(p)).OrderBy(p => p).ToList();
            check = new { folder = checkFolder, pattern, expected = expected.Count, found = expected.Count - missing.Count, missing = missing.Take(200), notInList = extra.Take(200), allMatch = missing.Count == 0 && extra.Count == 0 };
        }
        return ToolResult.Ok(new { file, sheets = rows.Count, columns, check, first = rows.Take(20) });
    }
}

public sealed class ExportQuantitiesTool : IRevitTool
{
    public string Name => "export_quantities";
    public string Description =>
        "Bill of quantities: totals per category and type (count, length, area, volume, in project units) to a CSV that " +
        "opens in Excel, optionally per level or by a parameter such as a cost code or phase. For a live quantity schedule " +
        "in Revit, use create_schedule.";
    public string PropertiesJson => """
    {
      "categories": {"type": "array", "items": {"type": "string"}, "description": "Default: walls, floors, roofs, ceilings, structural framing, columns, foundations, doors, windows, pipes, ducts."},
      "group_by": {"type": "array", "items": {"type": "string"}, "description": "Extra grouping: 'level', 'phase' or any parameter name (e.g. Assembly Code, Mark)."},
      "folder": {"type": "string"},
      "file_name": {"type": "string"}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    private static readonly string[] DefaultCategories =
        ["Walls", "Floors", "Roofs", "Ceilings", "Structural Framing", "Structural Columns", "Columns", "Structural Foundations", "Doors", "Windows", "Pipes", "Ducts"];

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var names = args.StringList("categories");
        var explicitCats = names.Count > 0;
        var cats = new List<BuiltInCategory>();
        foreach (var n in explicitCats ? names : DefaultCategories.ToList())
        {
            try { cats.Add(RevitHelpers.ParseCategory(doc, n)); }
            catch (ArgumentException) when (!explicitCats) { }
        }
        var groupBy = args.StringList("group_by");
        var lengthUnit = RevitHelpers.LengthUnitLabel(doc);
        double Conv(double v, ForgeTypeId spec) =>
            Math.Round(UnitUtils.ConvertFromInternalUnits(v, doc.GetUnits().GetFormatOptions(spec).GetUnitTypeId()), 3);

        var rows = new Dictionary<string, (string cat, string fam, string type, string[] keys, int count, double len, double area, double vol)>();
        var inPlaceNoQuantity = new List<long>();
        foreach (var bic in cats)
        {
            foreach (var e in new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType())
            {
                if (e is Wall { IsStackedWallMember: true }) continue; // the stacked wall itself carries the totals
                var type = doc.GetElement(e.GetTypeId()) as ElementType;
                var keys = groupBy.Select(g => g.ToLowerInvariant() switch
                {
                    "level" => (doc.GetElement(e.LevelId) as Level)?.Name ?? e.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM)?.AsValueString() ?? "",
                    "phase" => e.get_Parameter(BuiltInParameter.PHASE_CREATED)?.AsValueString() ?? "",
                    _ => (e.LookupParameter(g) ?? type?.LookupParameter(g)) is { } p ? RevitHelpers.ParamValue(p) ?? "" : "",
                }).ToArray();
                double D(BuiltInParameter bip) => e.get_Parameter(bip) is { StorageType: StorageType.Double } p ? p.AsDouble() : 0;
                var len = e is FamilyInstance && bic is not (BuiltInCategory.OST_StructuralFraming) ? 0
                    : Math.Max(D(BuiltInParameter.CURVE_ELEM_LENGTH), D(BuiltInParameter.INSTANCE_LENGTH_PARAM));
                var area = D(BuiltInParameter.HOST_AREA_COMPUTED);
                var vol = D(BuiltInParameter.HOST_VOLUME_COMPUTED);
                if (vol == 0 && e.LookupParameter("Volume") is { StorageType: StorageType.Double } vp) vol = vp.AsDouble();
                if (vol == 0 && area == 0 && InPlace.Is(e)) inPlaceNoQuantity.Add(e.Id.Value);
                var catName = e.Category?.Name ?? bic.ToString();
                var k = string.Join("|", new[] { catName, type?.FamilyName ?? "", type?.Name ?? "" }.Concat(keys));
                rows.TryGetValue(k, out var r);
                rows[k] = (catName, type?.FamilyName ?? "", type?.Name ?? "", keys, r.count + 1, r.len + len, r.area + area, r.vol + vol);
            }
        }
        if (rows.Count == 0) return ToolResult.Error("No elements found in those categories.");

        var folder = OutputPaths.Checked(args.OptString("folder")) ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SketchProMax");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, (OutputPaths.Checked(args.OptString("file_name")) ?? $"{doc.Title} - Quantities {DateTime.Now:yyyy-MM-dd}") + ".csv");
        string Csv(string s) => s.IndexOfAny([',', '"', '\n']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        var areaUnit = LabelUtils.GetLabelForUnit(doc.GetUnits().GetFormatOptions(SpecTypeId.Area).GetUnitTypeId());
        var volUnit = LabelUtils.GetLabelForUnit(doc.GetUnits().GetFormatOptions(SpecTypeId.Volume).GetUnitTypeId());
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", new[] { "Category", "Family", "Type" }.Concat(groupBy)
            .Concat([$"Count", $"Length ({lengthUnit})", $"Area ({areaUnit})", $"Volume ({volUnit})"]).Select(Csv)));
        var ordered = rows.Values.OrderBy(r => r.cat).ThenBy(r => r.fam).ThenBy(r => r.type).ThenBy(r => string.Join("|", r.keys)).ToList();
        foreach (var r in ordered)
            sb.AppendLine(string.Join(",", new[] { r.cat, r.fam, r.type }.Concat(r.keys).Select(Csv)
                .Concat([r.count.ToString(), Conv(r.len, SpecTypeId.Length).ToString(System.Globalization.CultureInfo.InvariantCulture),
                         Conv(r.area, SpecTypeId.Area).ToString(System.Globalization.CultureInfo.InvariantCulture),
                         Conv(r.vol, SpecTypeId.Volume).ToString(System.Globalization.CultureInfo.InvariantCulture)])));
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));

        var totals = ordered.GroupBy(r => r.cat).Select(g => new
        {
            category = g.Key, count = g.Sum(r => r.count),
            length = Conv(g.Sum(r => r.len), SpecTypeId.Length), area = Conv(g.Sum(r => r.area), SpecTypeId.Area), volume = Conv(g.Sum(r => r.vol), SpecTypeId.Volume),
        });
        return ToolResult.Ok(new
        {
            file, rows = ordered.Count, units = new { length = lengthUnit, area = areaUnit, volume = volUnit }, totals,
            inPlace = inPlaceNoQuantity.Count == 0 ? null : new
            {
                count = inPlaceNoQuantity.Count, ids = inPlaceNoQuantity.Take(50),
                note = "These in-place families are counted but carry no volume or area: Revit doesn't expose in-place geometry to add-ins. Measure them by hand or remodel them as system/loadable elements.",
            },
        });
    }
}

public sealed class CheckScheduleTool : IRevitTool
{
    public string Name => "check_schedule";
    public string Description =>
        "Assess a schedule: empty cells per column, duplicate marks or numbers, rows the schedule leaves out compared " +
        "with the model (filters, phase, design options), hidden fields, and whether it lists each instance. Use before issuing.";
    public string PropertiesJson => """
    {
      "schedule": {"type": "string", "description": "Schedule name or id."},
      "unique_columns": {"type": "array", "items": {"type": "string"}, "description": "Columns that must not repeat (default: any column called Mark, Number, Tag or Ref)."}
    }
    """;
    public string[] Required => ["schedule"];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var key = args.String("schedule").Trim();
        var schedules = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
            .Where(s => !s.IsTemplate && !s.IsTitleblockRevisionSchedule).ToList();
        var schedule = schedules.FirstOrDefault(s => s.Id.Value.ToString() == key)
                       ?? schedules.FirstOrDefault(s => string.Equals(s.Name, key, StringComparison.OrdinalIgnoreCase))
                       ?? (schedules.Where(s => s.Name.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0).Take(2).ToList() is { Count: 1 } one ? one[0] : null)
                       ?? throw new ArgumentException($"No single schedule matches '{key}'.");
        var def = schedule.Definition;
        var fields = Enumerable.Range(0, def.GetFieldCount()).Select(def.GetField).ToList();
        var visible = fields.Where(f => !f.IsHidden).Select(f => f.GetName()).ToList();
        var hidden = fields.Where(f => f.IsHidden).Select(f => f.GetName()).ToList();

        var body = schedule.GetTableData().GetSectionData(SectionType.Body);
        var rows = new List<List<string>>();
        for (var r = body.FirstRowNumber; r <= body.LastRowNumber; r++)
        {
            var row = new List<string>();
            for (var c = body.FirstColumnNumber; c <= body.LastColumnNumber; c++) row.Add(schedule.GetCellText(SectionType.Body, r, c));
            rows.Add(row);
        }
        // Body rows start with the column headings; drop rows that repeat them and blank spacer rows.
        var headings = rows.FirstOrDefault(r => r.Count == visible.Count && r.Zip(visible, (a, b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase)).Count(x => x) >= Math.Max(1, visible.Count / 2));
        var columns = headings ?? visible;
        var data = rows.Where(r => r != headings && r.Any(c => !string.IsNullOrWhiteSpace(c))).ToList();

        var empty = new Dictionary<string, int>();
        for (var c = 0; c < columns.Count; c++)
        {
            var n = data.Count(r => c < r.Count && string.IsNullOrWhiteSpace(r[c]));
            if (n > 0) empty[columns[c]] = n;
        }
        var uniqueWanted = args.StringList("unique_columns");
        var uniqueCols = Enumerable.Range(0, columns.Count).Where(c => uniqueWanted.Count > 0
            ? uniqueWanted.Any(u => string.Equals(u, columns[c], StringComparison.OrdinalIgnoreCase))
            : System.Text.RegularExpressions.Regex.IsMatch(columns[c], @"\b(Mark|Number|Tag|Ref)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)).ToList();
        var duplicates = uniqueCols.Select(c => new
        {
            column = columns[c],
            repeated = data.Where(r => c < r.Count && !string.IsNullOrWhiteSpace(r[c])).GroupBy(r => r[c]).Where(g => g.Count() > 1)
                .Select(g => $"{g.Key} ×{g.Count()}").Take(50).ToList(),
        }).Where(d => d.repeated.Count > 0).ToList();

        int? inSchedule = null, inModel = null;
        if (def.CategoryId != ElementId.InvalidElementId)
        {
            inSchedule = new FilteredElementCollector(doc, schedule.Id).WhereElementIsNotElementType().GetElementCount();
            inModel = new FilteredElementCollector(doc).OfCategoryId(def.CategoryId).WhereElementIsNotElementType().GetElementCount();
        }
        var issues = new List<string>();
        if (empty.Count > 0) issues.Add($"{empty.Count} column(s) have empty cells");
        if (duplicates.Count > 0) issues.Add($"repeated values in {string.Join(", ", duplicates.Select(d => d.column))}");
        if (inSchedule is not null && inModel is not null && inSchedule < inModel)
            issues.Add($"the schedule covers {inSchedule} of {inModel} elements in its category (filters, phase or design options leave the rest out)");
        if (!def.IsItemized && duplicates.Count > 0) issues.Add("the schedule groups rows, so repeated values may be intended");
        return ToolResult.Ok(new
        {
            schedule = schedule.Name, rows = data.Count, columns, hiddenFields = hidden, itemized = def.IsItemized,
            emptyCells = empty, duplicates, elementsInSchedule = inSchedule, elementsInModel = inModel,
            filters = def.GetFilterCount(), issues, ok = issues.Count == 0,
        });
    }
}

/// <summary>
/// Binds the shared parameters a title block family uses (its labels' sheet parameters) to Sheets, or Project
/// Information, in the project, keeping their GUIDs so the labels pick the values up. The family is opened read-only;
/// the definitions come from a temporary shared parameter file holding exactly those GUIDs, and the user's own shared
/// parameter file setting is put back afterwards. Call with no transaction open.
/// </summary>
internal static class TitleBlockParameters
{
    private sealed record Wanted(string Name, Guid Guid, ForgeTypeId DataType, ForgeTypeId Group, string Family);

    public static object Bind(UIApplication app, Document doc, List<Family> families, BuiltInCategory target)
    {
        var wanted = new Dictionary<Guid, Wanted>();
        var unreadable = new List<string>();
        foreach (var family in families)
        {
            if (!family.IsEditable) { unreadable.Add($"{family.Name}: the family can't be opened"); continue; }
            Document? fdoc = null;
            try
            {
                fdoc = doc.EditFamily(family);
                foreach (FamilyParameter fp in fdoc.FamilyManager.Parameters)
                    if (fp.IsShared && fp.Definition is { } d)
                        wanted[fp.GUID] = new Wanted(d.Name, fp.GUID, d.GetDataType(), d.GetGroupTypeId(), family.Name);
                // Label parameters added from a shared parameter file are kept as shared parameter elements in the family.
                foreach (var spe in new FilteredElementCollector(fdoc).OfClass(typeof(SharedParameterElement)).Cast<SharedParameterElement>())
                    if (!wanted.ContainsKey(spe.GuidValue) && spe.GetDefinition() is { } d)
                        wanted[spe.GuidValue] = new Wanted(spe.Name, spe.GuidValue, d.GetDataType(), d.GetGroupTypeId(), family.Name);
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or InvalidOperationException)
            {
                unreadable.Add($"{family.Name}: {ex.Message.Trim()}");
            }
            finally { try { fdoc?.Close(false); } catch (Autodesk.Revit.Exceptions.ApplicationException) { } }
        }
        if (wanted.Count == 0)
            return new { bound = Array.Empty<string>(), note = "The copied title blocks use no shared parameters.", unreadable };

        var category = Category.GetCategory(doc, target);
        var bound = new List<string>();
        var already = new List<string>();
        var problems = new List<string>();
        var revit = app.Application;
        var original = revit.SharedParametersFilename;
        var file = Path.Combine(Path.GetTempPath(), "SketchProMax", $"TitleBlockParameters-{Guid.NewGuid():N}.txt");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "", new UTF8Encoding(false));
            revit.SharedParametersFilename = file;
            var defFile = revit.OpenSharedParameterFile() ?? throw new InvalidOperationException("Revit can't use a temporary shared parameter file.");
            var group = defFile.Groups.Create("Title block");
            using var t = RevitHelpers.StartTransaction(doc, "Bind title block parameters");
            foreach (var w in wanted.Values.OrderBy(w => w.Name))
            {
                try
                {
                    var existing = SharedParameterElement.Lookup(doc, w.Guid);
                    if (existing is null && Existing(doc, w.Name) is not null)
                    {
                        problems.Add($"{w.Name}: the project already has a different parameter with this name; rename it, then copy again");
                        continue;
                    }
                    var cats = revit.Create.NewCategorySet();
                    cats.Insert(category);
                    if (existing is not null && doc.ParameterBindings.get_Item(existing.GetDefinition()) is ElementBinding old)
                    {
                        if (old.Categories.Contains(category)) { already.Add(w.Name); continue; }
                        if (old is TypeBinding) { problems.Add($"{w.Name}: bound as a type parameter to other categories; bind it to {category.Name} by hand"); continue; }
                        foreach (Category c in old.Categories) cats.Insert(c);
                        if (doc.ParameterBindings.ReInsert(existing.GetDefinition(), revit.Create.NewInstanceBinding(cats), w.Group)) bound.Add($"{w.Name} (added {category.Name})");
                        else problems.Add($"{w.Name}: Revit refused the binding");
                        continue;
                    }
                    var def = group.Definitions.Create(new ExternalDefinitionCreationOptions(w.Name, w.DataType) { GUID = w.Guid });
                    if (doc.ParameterBindings.Insert(def, revit.Create.NewInstanceBinding(cats), w.Group)) bound.Add(w.Name);
                    else problems.Add($"{w.Name}: Revit refused the binding");
                }
                catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or ArgumentException or InvalidOperationException)
                {
                    problems.Add($"{w.Name}: {ex.Message.Trim()}");
                }
            }
            if (bound.Count > 0) t.Commit(); else t.RollBack();
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            problems.Add(ex.Message.Trim());
        }
        finally
        {
            try { revit.SharedParametersFilename = original ?? ""; }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or ArgumentException) { }
            try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return new
        {
            category = category.Name,
            bound,
            alreadyBound = already,
            problems,
            unreadable = unreadable.Count > 0 ? unreadable : null,
            guids = wanted.Values.OrderBy(w => w.Name).Select(w => new { w.Name, guid = w.Guid.ToString(), family = w.Family }),
            note = bound.Count > 0 ? $"Fill these {category.Name} parameters (set_parameters) and the title block labels show them." : null,
        };
    }

    private static (Definition def, Binding binding)? Existing(Document doc, string name)
    {
        var it = doc.ParameterBindings.ForwardIterator();
        while (it.MoveNext())
            if (it.Key is { } d && string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)) return (d, (Binding)it.Current);
        return null;
    }
}
