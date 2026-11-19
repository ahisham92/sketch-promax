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
    public static bool Is(string path) => Path.GetFileName(path) is { } name && (string.Equals(name, M.KeptName, StringComparison.OrdinalIgnoreCase) || string.Equals(name, M.KeptSwapName, StringComparison.OrdinalIgnoreCase));

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
        string? swap = null;
        Dictionary<string, string>? swapMap = null;
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
                    // A group edited again: the copy before it goes with its type, so this copy brings the type as it is now.
                    if (e is Group edited)
                    {
                        var name = edited.GroupType.Name;
                        foreach (var stale in new FilteredElementCollector(keeper).OfClass(typeof(GroupType)).Where(t => t.Name == name).Select(t => t.Id).ToList())
                        {
                            foreach (var placed in new FilteredElementCollector(keeper).OfClass(typeof(Group)).Where(g => g.GetTypeId() == stale).Select(g => g.Id).ToList()) keeper.Delete(placed);
                            if (keeper.GetElement(stale) is not null) keeper.Delete(stale);
                        }
                    }
                    // Something drawn in a view (a detail group) is copied view to view, into a drafting view of the kept-elements project.
                    var copies = e.ViewSpecific && doc.GetElement(e.OwnerViewId) is View drawnIn
                        ? ElementTransformUtils.CopyElements(drawnIn, [e.Id], Details(keeper), Transform.Identity, Options())
                        : ElementTransformUtils.CopyElements(doc, [e.Id], keeper, Transform.Identity, Options());
                    var copy = (e is Group ? copies.Select(keeper.GetElement).OfType<Group>().FirstOrDefault(g => g.GroupId == ElementId.InvalidElementId) : null)
                        ?? Main(keeper, copies, e, e.Category?.Id.Value ?? 0) ?? throw new InvalidOperationException("Revit copied nothing");
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
            else
                try { keeper.Save(); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Revit couldn't save the project over itself ("Saving failed"): saved under another name, which takes the file's place once it is closed.
                    swap = Path.Combine(folder, M.KeptSwapName);
                    keeper.SaveAs(swap, new SaveAsOptions { OverwriteExistingFile = true, MaximumBackups = 1 });
                    swapMap = map;
                }
            if (swap is null) File.WriteAllLines(MapOf(folder), M.WriteMade(map));
        }
        finally
        {
            try { keeper?.Close(false); } catch (Exception) { }
            Backups(folder);
        }
        if (swap is not null && swapMap is not null)
        {
            File.Copy(swap, FileOf(folder), true);
            File.WriteAllLines(MapOf(folder), M.WriteMade(swapMap));
            try { File.Delete(swap); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
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

    /// <summary>
    /// A group whose type was edited after the last save: the type as its kept copy has it is brought in, every group
    /// of the old type is given it, and it takes the old type's name. Members come back as the copy has them (with new
    /// ids; Revit takes the tags and dimensions on the old members out, and <paramref name="notes"/> makes them again
    /// on the new ones). Refused when a tag or dimension is still missing after that, or when the members don't come
    /// back as they were. Returns how many groups were given the type. Call inside a transaction of <paramref name="doc"/>.
    /// </summary>
    public static int Regroup(Document keeper, Document doc, Group group, string keptUnique, Func<HashSet<ElementId>>? notes = null)
    {
        if (keeper.GetElement(keptUnique) is not Group source) throw new InvalidOperationException("the kept copy of the group as it was edited isn't there any more: edit the group again by hand");
        var want = ChangeExtras.GroupShape(source);
        var old = group.GroupType;
        var name = old.Name;
        var placed = new FilteredElementCollector(doc).OfClass(typeof(Group)).Where(g => g.GetTypeId() == old.Id).Cast<Group>().ToList();
        var before = ChangeExtras.Notes(doc);
        // The old type steps aside, so the copy comes in under the name with what it is made of.
        old.Name = (name.Length > 200 ? name.Substring(0, 200) : name) + " (before replay " + Guid.NewGuid().ToString("N").Substring(0, 6) + ")";
        // A detail group's copy sits in a drafting view of the kept-elements project: it comes back view to view, into the group's own view.
        var copies = source.ViewSpecific && keeper.GetElement(source.OwnerViewId) is View from
            ? ElementTransformUtils.CopyElements(from, [source.Id], doc.GetElement(group.OwnerViewId) as View ?? throw new InvalidOperationException("the view the group is drawn in isn't there: edit the group again by hand"), Transform.Identity, Options())
            : ElementTransformUtils.CopyElements(keeper, [source.Id], doc, Transform.Identity, Options());
        var pasted = copies.Select(doc.GetElement).OfType<Group>().FirstOrDefault(g => g.GroupId == ElementId.InvalidElementId)
            ?? throw new InvalidOperationException("Revit copied nothing back for the edited group: edit the group again by hand");
        var fresh = pasted.GroupType;
        if (fresh.Id == old.Id) throw new InvalidOperationException("Revit gave the copy of the edited group the model's own group type: edit the group again by hand");
        foreach (var one in placed) one.GroupType = fresh;
        doc.Delete(pasted.Id);
        doc.Regenerate();
        if (!new FilteredElementCollector(doc).OfClass(typeof(Group)).Any(g => g.GetTypeId() == old.Id)) doc.Delete(old.Id);
        if (fresh.Name != name) fresh.Name = name;
        doc.Regenerate();
        if (want is not null && ChangeExtras.GroupShape(group) is { } now && now != want) throw new InvalidOperationException("the group's members didn't come back as they were edited: edit the group again by hand");
        // Each tag or dimension that went with an old member is made again on the member in its place, or the edit isn't put back.
        var again = notes?.Invoke() ?? [];
        var lost = before.Count(id => doc.GetElement(id) is null && !again.Contains(id));
        if (lost > 0) throw new InvalidOperationException($"putting the group's edit back would take out {lost} tag{(lost == 1 ? "" : "s")} or dimension{(lost == 1 ? "" : "s")} on its members: edit the group again by hand");
        return placed.Count;
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
            foreach (var name in new[] { M.KeptName, M.KeptSwapName })
                foreach (var backup in Directory.GetFiles(folder, Path.GetFileNameWithoutExtension(name) + ".0*.rvt")) File.Delete(backup);
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

    /// <summary>The drafting view of the kept-elements project that holds copies of things drawn in a view (made the first time). Call inside a transaction of <paramref name="keeper"/>.</summary>
    private static View Details(Document keeper)
    {
        if (new FilteredElementCollector(keeper).OfClass(typeof(ViewDrafting)).Cast<View>().FirstOrDefault(v => !v.IsTemplate && v.Name == M.KeptDetailsView) is { } have) return have;
        var kind = new FilteredElementCollector(keeper).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().FirstOrDefault(k => k.ViewFamily == ViewFamily.Drafting)
            ?? throw new InvalidOperationException("the kept-elements project has no drafting view type");
        var made = ViewDrafting.Create(keeper, kind.Id);
        made.Name = M.KeptDetailsView;
        // The scale of 1:1 keeps what is drawn at its size whatever view it came from.
        try { made.Scale = 1; } catch (Exception) { }
        return made;
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
