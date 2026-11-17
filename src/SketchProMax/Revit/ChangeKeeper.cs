using System.IO;
using Autodesk.Revit.DB;
using M = SketchProMax.Revit.ChangeFileMath;

namespace SketchProMax.Revit;

/// <summary>
/// Lifeline's kept elements: Revit gives add-ins no way to make some elements again from a description (in-place
/// families, curved or sketched stairs, extrusion roofs, pipes and ducts, anything the change file has no maker for).
/// A copy of each such new element is kept in a small Revit project next to the change files (as a loaded family's
/// file is kept), and a replay copies it back to where it was. The project is opened without a window, written and
/// closed again at an idle moment; the user's model is only read.
/// </summary>
internal static class ChangeKeeper
{
    public static string FileOf(string folder) => Path.Combine(folder, M.KeptName);

    private static string MapOf(string folder) => Path.Combine(folder, M.KeptMapName);

    /// <summary>The kept-elements project itself (never watched, never offered).</summary>
    public static bool Is(string path) => string.Equals(Path.GetFileName(path), M.KeptName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Which copy is whose: the element's UniqueId to its copy's UniqueId in the kept-elements project.</summary>
    public static Dictionary<string, string> ReadMap(string folder)
    {
        try { return File.Exists(MapOf(folder)) ? M.ParseMade(File.ReadAllLines(MapOf(folder))) : new(StringComparer.Ordinal); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(StringComparer.Ordinal); }
    }

    /// <summary>A view template, a view filter, a line pattern or a fill pattern: kept whole as a copy, new or changed.</summary>
    public static bool IsDocItem(Element e) => e is View { IsTemplate: true } or ParameterFilterElement or LinePatternElement or FillPatternElement;

    /// <summary>An element that was in the model and can only be set back whole: an in-place family, or one with no point or line of its own to move (sketched stairs, an extrusion roof).</summary>
    public static bool OnlyByCopy(Element e)
    {
        try { return e is FamilyInstance { Symbol.Family.IsInPlace: true } || e.Location is not (LocationPoint or LocationCurve); }
        catch (Exception) { return false; }
    }

    /// <summary>The element of <paramref name="doc"/> that is the same thing as the line's by its kind and name (a template, filter or pattern of that name).</summary>
    public static Element? ByName(Document doc, M.Line line)
    {
        if (line.Name is not { Length: > 0 } name) return null;
        try
        {
            return line.What switch
            {
                "filter" => new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement)).FirstOrDefault(e => e.Name == name),
                "linepattern" => LinePatternElement.GetLinePatternElementByName(doc, name),
                "fillpattern" => new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).FirstOrDefault(e => e.Name == name),
                "view" => new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().FirstOrDefault(v => v.IsTemplate && v.Name == name && v.ViewType.ToString() == line.ViewKind),
                _ => null,
            };
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Gives a filter or pattern that is in the model what its kept copy has (its categories and rules, its dashes, its
    /// lines): the copy is brought in for a moment and taken out again. Call inside a transaction.
    /// </summary>
    public static bool Refresh(Document keeper, Document doc, Element existing, string keptUnique)
    {
        if (existing is not (ParameterFilterElement or LinePatternElement or FillPatternElement)) return false;
        if (keeper.GetElement(keptUnique) is not { } source) return false;
        var copies = ElementTransformUtils.CopyElements(keeper, [source.Id], doc, Transform.Identity, Options());
        var temp = copies.Select(doc.GetElement).FirstOrDefault(e => e is not null && e.GetType() == existing.GetType());
        // Revit used the one in the model for the copy: nothing came in.
        if (temp is null || temp.Id == existing.Id) return false;
        var changed = false;
        try
        {
            switch (existing)
            {
                case ParameterFilterElement filter when temp is ParameterFilterElement from:
                    var (was, want) = (filter.GetCategories(), from.GetCategories());
                    if (was.Count != want.Count || was.Except(want).Any()) { filter.SetCategories(want); changed = true; }
                    var rules = from.GetElementFilter();
                    if (rules is null) { if (filter.GetElementFilter() is not null) { filter.ClearRules(); changed = true; } }
                    else { filter.SetElementFilter(rules); changed = true; }
                    break;
                case LinePatternElement pattern when temp is LinePatternElement from:
                    var (a, b) = (pattern.GetLinePattern().GetSegments(), from.GetLinePattern().GetSegments());
                    if (a.Count != b.Count || a.Zip(b, (x, y) => x.Type != y.Type || !M.Same(x.Length, y.Length)).Any(d => d)) { pattern.SetLinePattern(from.GetLinePattern()); changed = true; }
                    break;
                case FillPatternElement fill when temp is FillPatternElement from:
                    if (!fill.GetFillPattern().IsEqual(from.GetFillPattern())) { fill.SetFillPattern(from.GetFillPattern()); changed = true; }
                    break;
            }
        }
        finally { try { doc.Delete(temp.Id); } catch (Exception) { } }
        return changed;
    }

    /// <summary>
    /// A new element that is kept as a copy: a model element (not drawn in a view, not a type, view, group, link or
    /// reinforcement) that the change file can't make again from its line, and every in-place family.
    /// </summary>
    public static bool Wants(Element e, M.Line line)
    {
        try
        {
            if (IsDocItem(e)) return true;
            if (e is View or ElementType or Family or Material or Level or Grid or Group or RevitLinkInstance or ImportInstance) return false;
            if (e.ViewSpecific || e.Category is not { CategoryType: CategoryType.Model }) return false;
            if (ChangeNotes.Owns(line) || ChangeViews.Owns(line)) return false;
            if (e.GroupId != ElementId.InvalidElementId) return false;
            if (e is FamilyInstance { Symbol.Family.IsInPlace: true }) return true;
            // A part of something else (a stairs run, a curtain panel, a railing's baluster) comes with its owner.
            if (e is FamilyInstance { SuperComponent: not null }) return false;
            return ChangeReplay.CanMake(line) is not null;
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Copies <paramref name="elements"/> of <paramref name="doc"/> into the folder's kept-elements project (made the
    /// first time), replacing earlier copies of the same elements. Returns the UniqueIds that now have a copy.
    /// Call at an idle moment, with no transaction open. <paramref name="doc"/> is only read.
    /// </summary>
    public static HashSet<string> Keep(Document doc, string folder, IReadOnlyList<Element> elements)
    {
        var kept = new HashSet<string>(StringComparer.Ordinal);
        if (elements.Count == 0) return kept;
        Document? keeper = null;
        try
        {
            Directory.CreateDirectory(folder);
            var file = FileOf(folder);
            var fresh = !File.Exists(file);
            keeper = fresh ? doc.Application.NewProjectDocument(UnitSystem.Metric) : doc.Application.OpenDocumentFile(file);
            var map = fresh ? new Dictionary<string, string>(StringComparer.Ordinal) : ReadMap(folder);
            Levels(doc, keeper);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            foreach (var e in elements)
            {
                // Out of time: the rest have no copy (their lines say why they can't be made, as before).
                if (clock.ElapsedMilliseconds > M.KeepLimitMs / 2) break;
                var unique = e.UniqueId;
                using var t = new Transaction(keeper, "Sketch ProMax: keep a copy");
                t.Start();
                Quiet(t);
                try
                {
                    if (map.TryGetValue(unique, out var old) && keeper.GetElement(old) is { } was) keeper.Delete(was.Id);
                    map.Remove(unique);
                    var copies = ElementTransformUtils.CopyElements(doc, [e.Id], keeper, Transform.Identity, Options());
                    var copy = Main(keeper, copies, e, e.Category?.Id.Value ?? 0) ?? throw new InvalidOperationException("Revit copied nothing");
                    var copyUnique = copy.UniqueId;
                    if (t.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Revit refused the copy");
                    map[unique] = copyUnique;
                    kept.Add(unique);
                }
                catch (Exception ex)
                {
                    if (t.GetStatus() == TransactionStatus.Started) t.RollBack();
                    LifelineBugs.Note("capture", $"a new element couldn't be kept as a copy ({Safe(() => e.Category?.Name) ?? "no category"})", Safe(() => doc.PathName), ex, "kept copy " + Safe(() => e.Category?.Name));
                }
            }
            if (fresh) keeper.SaveAs(file, new SaveAsOptions { OverwriteExistingFile = true, MaximumBackups = 1 });
            else keeper.Save();
            File.WriteAllLines(MapOf(folder), M.WriteMade(map));
        }
        finally
        {
            try { keeper?.Close(false); } catch (Exception) { }
            Backups(folder);
        }
        return kept;
    }

    /// <summary>For a replay: opens the folder's kept-elements project (no window); null when there is none. The caller closes it with <see cref="Close"/>.</summary>
    public static Document? Open(Autodesk.Revit.ApplicationServices.Application app, string folder)
    {
        var file = FileOf(folder);
        return File.Exists(file) ? app.OpenDocumentFile(file) : null;
    }

    public static void Close(Document? keeper)
    {
        try { if (keeper is { IsValidObject: true }) keeper.Close(false); } catch (Exception) { }
    }

    /// <summary>Copies the kept copy <paramref name="keptUnique"/> from <paramref name="keeper"/> back into <paramref name="doc"/>, where it was. Call inside a transaction of <paramref name="doc"/>.</summary>
    public static Element Make(Document keeper, Document doc, string keptUnique, M.Line line)
    {
        var source = keeper.GetElement(keptUnique) ?? throw new InvalidOperationException("its kept copy isn't there any more");
        var copies = ElementTransformUtils.CopyElements(keeper, [source.Id], doc, Transform.Identity, Options());
        var made = Main(doc, copies, source, line.CategoryId) ?? throw new InvalidOperationException("Revit copied nothing back");
        // A template, filter or pattern keeps its name (Revit adds a number when it thinks the name is taken).
        if (M.IsDocItem(line) && line.Name is { Length: > 0 } name) try { if (made.Name != name) made.Name = name; } catch (Exception) { }
        return made;
    }

    /// <summary>Deletes the folder's kept-elements project when no change file of the folder names a kept element any more.</summary>
    public static void Tidy(string folder, IEnumerable<string> texts)
    {
        try
        {
            if (!File.Exists(FileOf(folder)) || M.KeptInUse(texts)) return;
            File.Delete(FileOf(folder));
            if (File.Exists(MapOf(folder))) File.Delete(MapOf(folder));
            Backups(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Revit's own backup copies of the kept-elements project ("kept elements.0001.rvt") aren't needed.</summary>
    private static void Backups(string folder)
    {
        try
        {
            foreach (var backup in Directory.GetFiles(folder, Path.GetFileNameWithoutExtension(M.KeptName) + ".0*.rvt")) File.Delete(backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The copy itself among what Revit copied with it (its type, its family): of the element's category, not a type.</summary>
    private static Element? Main(Document doc, ICollection<ElementId> copies, Element like, long categoryId)
    {
        var elements = copies.Select(doc.GetElement).Where(e => e is not null and not ElementType and not Family).ToList();
        if (IsDocItem(like))
        {
            var kin = elements.Where(e => e!.GetType() == like.GetType()).ToList();
            return kin.FirstOrDefault(e => e!.Name == like.Name) ?? kin.FirstOrDefault();
        }
        return elements.FirstOrDefault(e => categoryId != 0 && e!.Category?.Id.Value == categoryId) ?? elements.FirstOrDefault();
    }

    /// <summary>The kept-elements project gets the model's levels (name and height), so a copy sits on the level it was on, both ways.</summary>
    private static void Levels(Document doc, Document keeper)
    {
        var theirs = new FilteredElementCollector(keeper).OfClass(typeof(Level)).Cast<Level>().ToList();
        var ours = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
        if (ours.All(l => theirs.Any(k => k.Name == l.Name && M.Same(k.Elevation, l.Elevation)))) return;
        using var t = new Transaction(keeper, "Sketch ProMax: levels");
        t.Start();
        Quiet(t);
        try
        {
            // The empty project's own levels step aside for the model's names.
            var n = 0;
            foreach (var k in theirs.Where(k => ours.Any(l => l.Name == k.Name && !M.Same(k.Elevation, l.Elevation))))
                try { k.Name = $"kept level {++n} {Guid.NewGuid():N}".Substring(0, 24); } catch (Exception) { }
            foreach (var l in ours.Where(l => !theirs.Any(k => k.Name == l.Name && M.Same(k.Elevation, l.Elevation))))
            {
                var made = Level.Create(keeper, l.Elevation);
                try { made.Name = l.Name; } catch (Exception) { }
            }
            t.Commit();
        }
        catch (Exception) { if (t.GetStatus() == TransactionStatus.Started) t.RollBack(); }
    }

    private static CopyPasteOptions Options()
    {
        var options = new CopyPasteOptions();
        options.SetDuplicateTypeNamesHandler(new UseDestinationTypes());
        return options;
    }

    private static void Quiet(Transaction t)
    {
        var failures = t.GetFailureHandlingOptions();
        failures.SetFailuresPreprocessor(new ChangeShapes.Quiet());
        failures.SetClearAfterRollback(true);
        failures.SetForcedModalHandling(false);
        t.SetFailureHandlingOptions(failures);
    }

    /// <summary>A type of the same name is already there: it is used, never a second "Name 2".</summary>
    private sealed class UseDestinationTypes : IDuplicateTypeNamesHandler
    {
        public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args) => DuplicateTypeAction.UseDestinationTypes;
    }

    private static string? Safe(Func<string?> get) { try { return get(); } catch (Exception) { return null; } }
}
