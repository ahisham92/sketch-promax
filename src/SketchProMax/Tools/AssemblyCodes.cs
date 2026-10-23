using System.IO;
using Autodesk.Revit.DB;

namespace SketchProMax.Tools;

/// <summary>
/// Assembly Codes in one go (the checklist's UniFormat check and Submissions status, through UI/AssemblyCodesFlow): the
/// code list of the LOI Excel loaded into the model's Assembly Code table, every type in use with no code (or one that
/// isn't in the list) listed with the code the UniFormat rules give it, and the codes the user keeps written.
/// </summary>
internal static class AssemblyCodes
{
    /// <summary>Loads the codes of an LOI Excel (or a Revit assembly code file) as the model's Assembly Code table: written next to the other Sketch ProMax files in Revit's format, then loaded from there.</summary>
    public static string Load(Document doc, string? path)
    {
        if (path is null || !File.Exists(path)) return "Pick the LOI matrix Excel (or an assembly code .txt) to load the codes from.";
        List<(string Code, string Name)> codes;
        try { codes = AssemblyCodeText.Read(path); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Xml.XmlException) { return $"Couldn't read {Path.GetFileName(path)} ({ex.Message}). Close it in Excel and try again."; }
        if (codes.Count == 0) return $"No UniFormat codes (such as A1020.15) were found in {Path.GetFileName(path)}.";
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Sketch ProMax", "Assembly codes");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, OfficeDocs.SafeName(Path.GetFileNameWithoutExtension(doc.Title)) + " assembly codes.txt");
        File.WriteAllText(file, AssemblyCodeText.TableText(codes), System.Text.Encoding.Unicode);
        var results = new KeyBasedTreeEntriesLoadResults();
        using (var t = new Transaction(doc, "Sketch ProMax: import the Assembly Codes"))
        {
            t.Start();
            try
            {
                var source = ExternalResourceReference.CreateLocalResource(doc, ExternalResourceTypes.BuiltInExternalResourceTypes.AssemblyCodeTable,
                    ModelPathUtils.ConvertUserVisiblePathToModelPath(file), PathType.Absolute);
                AssemblyCodeTable.GetAssemblyCodeTable(doc).LoadFrom(source, results);
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or ArgumentException or InvalidOperationException)
            {
                t.RollBack();
                return $"Revit didn't take the table ({ex.Message}). The file is {file}: load it in Manage > Additional Settings > Assembly Code.";
            }
            t.Commit();
        }
        var problems = results.GetFileReadErrors().Count + results.GetFileSyntaxErrors().Count;
        return $"Loaded {codes.Count} Assembly Codes from {Path.GetFileName(path)} into the model ({codes.Count(c => c.Code.Contains('.'))} at the most detailed level, such as " +
               $"{codes.LastOrDefault(c => c.Code.Contains('.')).Code ?? codes[^1].Code}). Saved as {file}." + (problems > 0 ? $" Revit skipped {problems} lines it couldn't read." : "");
    }

    /// <summary>The codes in the model's Assembly Code table (empty when it has none or can't be read).</summary>
    public static HashSet<string> InTable(Document doc)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try { foreach (var e in AssemblyCodeTable.GetAssemblyCodeTable(doc).GetKeyBasedTreeEntries()) set.Add(e.Key); }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or InvalidOperationException) { }
        return set;
    }

    /// <summary>The type's code parameter: the classification parameter of the Checking rules, else Revit's own Assembly Code.</summary>
    public static Parameter? CodeOf(ElementType type, IReadOnlyList<string> names) =>
        names.Where(n => !n.Equals("Assembly Code", StringComparison.OrdinalIgnoreCase)).Select(type.LookupParameter).FirstOrDefault(p => p is not null)
#if REVIT2024 || REVIT2025
        ?? type.get_Parameter(BuiltInParameter.UNIFORMAT_CODE);
#else
        ?? type.GetParameter(ParameterTypeId.AssemblyCode);
#endif

    /// <summary>The classification parameters of the Checking rules ("Assembly Code" when none is set).</summary>
    public static List<string> Names(ChecklistMath.Definition? def) =>
        ChecklistMath.RuleList(ChecklistMath.Rule(def, "classification")) is { Count: > 0 } names ? names : ["Assembly Code"];

    /// <summary>Every model type placed in the model (not in a view only) that has a code parameter it can write, with how many elements use it.</summary>
    public static List<(ElementType Type, int Count)> TypesInUse(Document doc, IReadOnlyList<string> names)
    {
        var cats = new List<ElementId>();
        foreach (Category c in doc.Settings.Categories)
            if (c.CategoryType == CategoryType.Model) cats.Add(c.Id);
        if (cats.Count == 0) return [];
        var counts = new Dictionary<long, int>();
        foreach (var e in new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(cats)).WhereElementIsNotElementType().WhereElementIsViewIndependent())
        {
            var id = e.GetTypeId();
            if (id == ElementId.InvalidElementId) continue;
            counts[id.Value] = counts.TryGetValue(id.Value, out var n) ? n + 1 : 1;
        }
        var list = new List<(ElementType, int)>();
        foreach (var pair in counts)
            if (doc.GetElement(new ElementId(pair.Key)) is ElementType type && CodeOf(type, names) is { IsReadOnly: false, StorageType: StorageType.String })
                list.Add((type, pair.Value));
        return list;
    }

    /// <summary>
    /// The types to fill, one "type id, what it is, suggested code" line each (tab separated): those with no code, and
    /// those whose code isn't in the model's table when it has one. The suggestion comes from the UniFormat rules; empty when they give none.
    /// </summary>
    public static string Suggest(Document doc, ChecklistMath.Definition? def)
    {
        var names = Names(def);
        var map = ChecklistFixes.UniFormatMap(def);
        var table = InTable(doc);
        var lines = new List<(string Category, string Line)>();
        foreach (var (type, count) in TypesInUse(doc, names))
        {
            var now = (CodeOf(type, names)?.AsString() ?? "").Trim();
            var unknown = now.Length > 0 && table.Count > 0 && !table.Contains(now);
            if (now.Length > 0 && !unknown) continue;
            var suggested = ChecklistFixes.UniFormatFor(type.Category?.Name ?? "", type.FamilyName, type.Name, map) ?? (unknown ? now : "");
            var what = $"{ChecklistFacts.TypeLabel(type)} ({count} element{(count == 1 ? "" : "s")}{(unknown ? $", now {now}: not in the code list" : "")})";
            lines.Add((type.Category?.Name ?? "", $"{type.Id.Value}\t{what.Replace('\t', ' ')}\t{suggested}"));
        }
        return string.Join("\n", lines.OrderBy(l => l.Category, StringComparer.OrdinalIgnoreCase).ThenBy(l => l.Line, StringComparer.OrdinalIgnoreCase).Select(l => l.Line));
    }

    /// <summary>Writes the codes of "type id, code" lines (tab separated) to the types.</summary>
    public static string Apply(Document doc, ChecklistMath.Definition? def, string? answer)
    {
        var wanted = AssemblyCodeText.Picked(answer);
        if (wanted.Count == 0) return "No type was given a code.";
        var names = Names(def);
        var table = InTable(doc);
        int filled = 0, failed = 0;
        var notListed = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var t = new Transaction(doc, "Sketch ProMax: fill the Assembly Codes"))
        {
            t.Start();
            foreach (var (id, code) in wanted)
            {
                if (doc.GetElement(new ElementId(id)) is not ElementType type || CodeOf(type, names) is not { IsReadOnly: false } p) { failed++; continue; }
                try
                {
                    if (p.Set(code)) filled++; else failed++;
                    if (table.Count > 0 && !table.Contains(code)) notListed.Add(code);
                }
                catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or ArgumentException or InvalidOperationException) { failed++; }
            }
            t.Commit();
        }
        return $"Filled the Assembly Code of {filled} type{(filled == 1 ? "" : "s")}." +
               (failed > 0 ? $" {failed} couldn't be written (owned by someone else in a workshared model, or the type is gone): check them in Type Properties." : "") +
               (notListed.Count > 0 ? $" Not in the code list: {string.Join(", ", notListed.Take(8))}{(notListed.Count > 8 ? " and more" : "")}: check them against the LOI Excel." : "");
    }
}
