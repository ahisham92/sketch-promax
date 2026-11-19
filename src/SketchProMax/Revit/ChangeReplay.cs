using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using SketchProMax.Tools;
using M = SketchProMax.Revit.ChangeFileMath;

namespace SketchProMax.Revit;

/// <summary>
/// Plays a change file (<see cref="ChangeFileMath"/>) onto the open model: each element found by its UniqueId is set
/// to the state in the file (type, place, rotation, flips, level elevation, values); one that isn't in the model is
/// made again when Revit allows it from what the file holds (family instances on a point or a line, straight walls,
/// grids, levels); a deleted one is deleted. Anything else is listed to redo by hand. Runs in small chunks (sized by
/// <see cref="ChangeFileMath.NextChunk"/>), one short Revit call and one undo step each, through
/// <see cref="ItemBatch"/> (one item failing doesn't stop the rest; what others own in a workshared model is skipped
/// and named; what needs borrowing is borrowed in one request per chunk). What Revit refused is tried once more at
/// the end (<see cref="Outcome.TakeFailed"/>): by then what it waited for (a view, a deletion that frees a name) is done.
/// What a replay made again is remembered next to the file (<see cref="ChangeFileMath.MadeMapName"/>), so playing the
/// file again sets those elements instead of making them twice.
/// </summary>
internal static class ChangeReplay
{
    /// <summary>Done: already as the file has it (a deletion of what isn't in the model), counted as already right.</summary>
    /// <summary>After: what is done once everything else is played (joins and cuts, a view's graphics, a changed shape put back from its copy).</summary>
    /// <summary>Regroup: a group type edited after the last save is given what its kept copy has (before its groups and members are played).</summary>
    public enum Act { Set, Make, Delete, Skip, Done, After, Regroup }

    public sealed record Step(M.Line Line, Act Act, string Why);

    public sealed class Outcome
    {
        public int Set, Made, Deleted, Same;
        public readonly List<string> NotDone = [];
        /// <summary>Changes Revit refused with an error (also in NotDone): bugs to look at, not just limits.</summary>
        public readonly List<string> Errors = [];
        /// <summary>Old UniqueId to the element made again for it, so what it hosts (later in the file) finds it.</summary>
        public readonly Dictionary<string, ElementId> MadeFor = new(StringComparer.Ordinal);
        /// <summary>Sketches to redraw after the chunk's transactions (a sketch edit scope can't sit inside one); Counted: already counted as set back.</summary>
        public readonly List<(ElementId Sketch, ElementId Owner, M.Line Line, bool Counted)> Sketches = [];
        /// <summary>The line each NotDone entry is about (by its place in NotDone), so the window can take you to it.</summary>
        public readonly Dictionary<int, M.Line> Place = [];
        /// <summary>Old UniqueId to the new UniqueId of what was made again (written next to the file after each chunk).</summary>
        public readonly Dictionary<string, string> MadeUnique = new(StringComparer.Ordinal);
        /// <summary>The steps Revit refused, each with its entry in NotDone: tried once more at the end.</summary>
        public readonly List<(Step Step, string Text)> Failed = [];
        /// <summary>Where the file's kept families are.</summary>
        public string FamiliesFolder = "";
        /// <summary>The kept copies of elements that can't be made from a line (<see cref="ChangeKeeper"/>): their folder, which copy is whose, and the project while a replay has it open.</summary>
        public string KeptFolder = "";
        public Dictionary<string, string> Kept = new(StringComparer.Ordinal);
        public Document? Keeper;
        /// <summary>The kept-elements project couldn't be opened (said once).</summary>
        public string? KeeperError;
        /// <summary>Groups whose type isn't in the model: their members are played one by one and grouped again at the end.</summary>
        public readonly HashSet<string> LateGroups = new(StringComparer.Ordinal);
        /// <summary>The lines with joins and cuts, by UniqueId (a join is only taken off when both elements' lines agree).</summary>
        public readonly Dictionary<string, M.Line> Relations = new(StringComparer.Ordinal);
        /// <summary>Group types a replay gives back what their kept copy has (edited after the last save), with the lines of their groups: the members' own lines aren't played.</summary>
        public readonly Dictionary<ElementId, List<M.Line>> Regrouped = [];
        /// <summary>Where each NotDone entry is, in words (by its place in NotDone), once <see cref="ChangeWhere.Find"/> has looked.</summary>
        public readonly Dictionary<int, string> Where = [];
        /// <summary>Lines whose own step found nothing to change: one that changes in its After step is counted as set back then.</summary>
        public readonly HashSet<M.Line> Unchanged = [];

        /// <summary>Notes that <paramref name="old"/> is now <paramref name="made"/>.</summary>
        public void MadeAgain(string? old, Element made)
        {
            if (old is not { Length: > 0 }) return;
            MadeFor[old] = made.Id;
            try { MadeUnique[old] = made.UniqueId; } catch (Exception) { }
        }

        public void Unmake(string? old)
        {
            if (old is not { Length: > 0 }) return;
            MadeFor.Remove(old);
            MadeUnique.Remove(old);
        }

        /// <summary>Takes the NotDone entries at <paramref name="drop"/> out (the lines the others are about stay right).</summary>
        public void Drop(HashSet<int> drop)
        {
            if (drop.Count == 0) return;
            var kept = new List<string>();
            var place = new Dictionary<int, M.Line>();
            for (var i = 0; i < NotDone.Count; i++)
            {
                if (drop.Contains(i)) continue;
                if (Place.TryGetValue(i, out var line)) place[kept.Count] = line;
                kept.Add(NotDone[i]);
            }
            NotDone.Clear();
            NotDone.AddRange(kept);
            Place.Clear();
            foreach (var p in place) Place[p.Key] = p.Value;
        }

        /// <summary>The steps Revit refused, to try once more; their entries leave NotDone and Errors (a second refusal puts them back).</summary>
        public List<Step> TakeFailed()
        {
            var drop = new HashSet<int>();
            foreach (var (step, text) in Failed)
            {
                for (var i = 0; i < NotDone.Count; i++)
                    if (!drop.Contains(i) && NotDone[i] == text && Place.TryGetValue(i, out var line) && ReferenceEquals(line, step.Line)) { drop.Add(i); break; }
                Errors.Remove(text);
            }
            Drop(drop);
            var steps = Failed.Select(f => f.Step).ToList();
            Failed.Clear();
            return steps;
        }

        public void Missed(M.Line line, string text)
        {
            Place[NotDone.Count] = line;
            NotDone.Add(text);
        }
    }

    /// <summary>What earlier replays of the file made again (<see cref="ChangeFile.ReadMade"/>), as elements of <paramref name="doc"/>; ones since deleted are left out.</summary>
    public static void Remember(Document doc, IReadOnlyDictionary<string, string> made, Outcome outcome)
    {
        foreach (var m in made)
            if (doc.GetElement(m.Value) is { } element) outcome.MadeAgain(m.Key, element);
    }

    /// <summary>What each line of the file will do to <paramref name="doc"/> (read only). <paramref name="outcome"/> knows what earlier replays made and where the kept families are.</summary>
    public static List<Step> Plan(Document doc, M.Parsed file, Outcome? outcome = null)
    {
        outcome ??= new Outcome();
        Element? Find(Document d, M.Line l) => ChangeReplay.Find(d, l, outcome.MadeFor);
        var steps = new List<Step>();
        var ordered = M.PlayOrder(file);
        // Groups made again bring their members: the members' own lines aren't played.
        // (A group whose type isn't in the model is put together again from its members, at the end.)
        var groupsMissing = ordered.Where(l => l.What == "group" && l.Kind != M.Kinds.Deleted && l.Unique is not null && Find(doc, l) is null && CanMake(l) is null).ToList();
        var groupsMade = new HashSet<string>(groupsMissing.Where(l => Type(doc, l, outcome) is GroupType).Select(l => l.Unique!), StringComparer.Ordinal);
        var late = new List<Step>();
        foreach (var l in groupsMissing.Where(l => !groupsMade.Contains(l.Unique!) && M.Members(l).Count > 0)) outcome.LateGroups.Add(l.Unique!);
        // Stairs made again bring their own railings.
        var stairsMade = new HashSet<string>(ordered.Where(l => l.What == "stairs" && l.Kind != M.Kinds.Deleted && l.Unique is not null
            && Find(doc, l) is null && CanMake(l) is null).Select(l => l.Unique!), StringComparer.Ordinal);
        // Group types edited after the last save (what a group is made of isn't what its line says): one step gives the
        // type what its kept copy has, and the members' own lines aren't played (they come with the type).
        outcome.Regrouped.Clear();
        var regroup = new List<Step>();
        var noCopy = new HashSet<ElementId>();
        foreach (var l in ordered.Where(l => l.Kind != M.Kinds.Deleted && M.GroupEdit(l) is not null))
        {
            var edit = M.GroupEdit(l)!.Value;
            if (Find(doc, l) is not Group { GroupType: { } edited } placed) continue;
            if (outcome.Regrouped.TryGetValue(edited.Id, out var others)) { others.Add(l); continue; }
            if (ChangeExtras.GroupShape(placed) is not { } now || now == edit.Shape) continue;
            outcome.Regrouped[edited.Id] = [l];
            // (Revit quietly undoes a change an add-in makes to one member of a group, so without the copy nothing can be done.)
            if (outcome.Kept.ContainsKey(edit.Key)) regroup.Add(new(l, Act.Regroup, ""));
            else if (noCopy.Add(edited.Id)) regroup.Add(new(l, Act.Skip, "the group was edited after the last save and no copy of the edited group was kept: edit the group again by hand"));
        }
        bool InRegrouped(Element? e)
        {
            try { return e is not null && e.GroupId != ElementId.InvalidElementId && doc.GetElement(e.GroupId) is Group g && outcome.Regrouped.ContainsKey(g.GetTypeId()); }
            catch (Exception) { return false; }
        }
        // Sketch lines deleted by themselves (see below): the elements the file has a line of, and each sketch's element.
        var inFile = new HashSet<string>(ordered.Where(l => l.Unique is { Length: > 0 }).Select(l => l.Unique!), StringComparer.Ordinal);
        var outlineSaid = new HashSet<string>(StringComparer.Ordinal);
        Dictionary<ElementId, ElementId>? sketchOf = null;
        Element? SketchOwner(Element sketchLine)
        {
            try
            {
                if (sketchOf is null)
                {
                    sketchOf = [];
                    foreach (var sketch in new FilteredElementCollector(doc).OfClass(typeof(Sketch)).Cast<Sketch>())
                        foreach (var id in sketch.GetAllElements()) sketchOf[id] = sketch.OwnerId;
                }
                return sketchOf.TryGetValue(sketchLine.Id, out var owner) ? doc.GetElement(owner) : null;
            }
            catch (Exception) { return null; }
        }
        foreach (var line in ordered)
        {
            // Older files can hold Revit's energy model (thousands of lines): Revit makes it again by itself.
            if (ChangeFile.IsAnalysis(line.CategoryId)) continue;
            // And what Revit makes with something else (a sketch's automatic dimensions, a title block's revision schedule...).
            if (M.IsRevitsOwn(line.CategoryId) || ChangeViews.IsRevitsOwn(line)) continue;
            // Made after the last save and deleted again before Lifeline read it: there is nothing to delete.
            if (line.Kind == M.Kinds.Deleted && line.New && line.Unique is not { Length: > 0 }) { steps.Add(new(line, Act.Done, "")); continue; }
            var found = Find(doc, line);
            // A line of a sketch (a floor's outline) deleted in Edit Sketch: it goes when its element's own line redraws
            // the outline, or with its element. Deleted by itself it leaves a broken sketch ("Floor sketch is invalid"),
            // so when the file has no line of its element (Revit closed during the edit), that is said once per element.
            if (line.Kind == M.Kinds.Deleted && line.Unique is { Length: > 0 } && found is not null && ChangeShapes.IsSketchLine(found))
            {
                var owner = SketchOwner(found);
                if (owner is not null && inFile.Contains(owner.UniqueId)) continue;
                if (!outlineSaid.Add(owner?.UniqueId ?? "")) continue;
                string whose;
                try { whose = owner is null ? "an element" : $"{owner.Category?.Name ?? "an element"} \"{owner.Name}\""; } catch (Exception) { whose = "an element"; }
                steps.Add(new(line, Act.Skip, $"the outline of {whose} was being edited when Revit closed, and its new outline wasn't written: edit its outline again by hand"));
                continue;
            }
            // A template, filter or pattern of that name is the same thing (one brought in by a copy, or made by hand since).
            if (found is null && line.Kind != M.Kinds.Deleted && M.IsDocItem(line) && ChangeKeeper.ByName(doc, line) is { } named)
            {
                outcome.MadeAgain(line.Unique, named);
                found = named;
            }
            // A group's or an image's type comes with the group or image.
            if (found is null && line.Kind != M.Kinds.Deleted && M.ComesWithInstance(line)) continue;
            // A member of a group whose type is given back from its copy: it comes (or goes) with the type.
            if (outcome.Regrouped.Count > 0 && line.What != "group" && (InRegrouped(found)
                || (line.Kind != M.Kinds.Deleted && line.Group is { } inGroup && doc.GetElement(inGroup) is Group g && outcome.Regrouped.ContainsKey(g.GetTypeId())))) continue;
            if (line.What == "family" && line.Kind != M.Kinds.Deleted)
            {
                // A family loaded after the last save: loaded again from its kept copy, unless one of that name is there.
                // One loaded again over itself (not new) is loaded again from its kept copy too.
                var kept = ChangeTypes.CanMakeFamily(line, outcome.FamiliesFolder) is null;
                if ((found is not null || ChangeTypes.FamilyThere(doc, line) is not null) && (line.New || !kept)) continue;
                steps.Add(ChangeTypes.CanMakeFamily(line, outcome.FamiliesFolder) is { } missing ? new(line, Act.Skip, missing) : new(line, Act.Make, ""));
                continue;
            }
            if (line.Kind == M.Kinds.Deleted && found is not null && line.Unique is not { Length: > 0 })
            {
                // Named by id only (deleted before Lifeline read it, often by another add-in or Revit itself): never deleted
                // automatically, as an id can't prove it is the same element. Listed with what it is now, with Show.
                // Revit's own bookkeeping elements (no category) and what it makes by itself are not listed.
                if (found.Category is null && found is not (View or ElementType)) continue;
                if (found.Category is { } own && M.IsRevitsOwn(own.Id.Value)) continue;
                if (found.Category is { } cat && ChangeFile.IsAnalysis(cat.Id.Value)) continue;
                string what;
                try { what = $"{found.Category?.Name ?? found.GetType().Name} \"{found.Name}\""; } catch (Exception) { what = "an element"; }
                steps.Add(new(line, Act.Skip, $"deleted by a step Lifeline couldn't read; in the model now it is {what}: delete it by hand if you deleted it"));
            }
            else if (line.Kind == M.Kinds.Deleted)
                steps.Add(found is null ? new(line, Act.Done, "") : new(line, Act.Delete, ""));
            else if (found is not null)
                steps.Add(new(line, Act.Set, ""));
            else if (line.Group is { } group && groupsMade.Contains(group))
                continue;
            else if (line.What == "group" && outcome.LateGroups.Contains(line.Unique!))
                late.Add(new(line, Act.Make, ""));
            else if (line.What == "railing" && line.Host is { } onStairs && stairsMade.Contains(onStairs))
                continue;
            else if (FromKept(line, outcome))
                steps.Add(new(line, Act.Make, ""));
            else if (CanMake(line) is { } why)
                steps.Add(new(line, Act.Skip, why));
            else
                steps.Add(new(line, Act.Make, ""));
        }
        steps.AddRange(late);
        // The edited group types come right after what they may need (levels, grids, types, families, materials).
        if (regroup.Count > 0)
        {
            var at = steps.FindIndex(s => s.Line.What is not ("level" or "grid" or "material" or "family" or "filter" or "linepattern" or "fillpattern" or "style" or "type"));
            steps.InsertRange(at < 0 ? steps.Count : at, regroup);
        }
        // Last: joins and cuts, views' graphics and shapes to check against their kept copies (everything they name is there by now).
        foreach (var line in M.AfterLines(file))
        {
            if (ChangeFile.IsAnalysis(line.CategoryId) || M.IsRevitsOwn(line.CategoryId)) continue;
            var x = line.Extra!;
            if (x.ContainsKey("joins")) outcome.Relations[line.Unique!] = line;
            // Joined and cut as its line says already (most elements): no step.
            if (!x.ContainsKey("vg") && !x.ContainsKey("shape") && Find(doc, line) is { } there
                && ChangeExtras.Related(doc, there, line, u => outcome.MadeFor.TryGetValue(u, out var id) ? doc.GetElement(id) : doc.GetElement(u))) continue;
            steps.Add(new(line, Act.After, ""));
        }
        return steps;
    }

    /// <summary>The element a line is about: by UniqueId (the one made again for it, when a replay made it); a deletion named by id only, by id when it was in central already (never someone's new element).</summary>
    private static Element? Find(Document doc, M.Line line, IReadOnlyDictionary<string, ElementId>? made = null)
    {
        if (line.Unique is { Length: > 0 } unique)
            return made is not null && made.TryGetValue(unique, out var again) && doc.GetElement(again) is { } now ? now : doc.GetElement(unique);
        if (line.Kind != M.Kinds.Deleted || line.Id == 0) return null;
        var element = doc.GetElement(new ElementId(line.Id));
        if (element is null) return null;
        try
        {
            if (doc.IsWorkshared && WorksharingUtils.GetModelUpdatesStatus(doc, element.Id) == ModelUpdatesStatus.NotYetInCentral) return null;
        }
        catch (Exception) { }
        return element;
    }

    /// <summary>The line's element is copied back from its kept copy (it can't be made from the line, or is an in-place family).</summary>
    private static bool FromKept(M.Line line, Outcome outcome) => M.IsKept(line) && line.Unique is { Length: > 0 } u && outcome.Kept.ContainsKey(u);

    /// <summary>Null when the element can be made again from the line, else why not.</summary>
    internal static string? CanMake(M.Line line) => line.What switch
    {
        "level" when line.Elevation is not null => null,
        "grid" when line.P is not null && line.Q is not null => null,
        "wall" when (line.Loops is { Count: > 0 } || (line.P is not null && line.Q is not null)) && line.Type is not null => null,
        "instance" when line.P is not null && line.Type is not null => null,
        "wall" or "instance" => "made after the last save, but its shape isn't a point or a straight line: make it again by hand",
        "type" => ChangeTypes.CanMake(line),
        "material" => ChangeTypes.CanMakeMaterial(line),
        "style" => ChangeExtras.CanMakeStyle(line),
        "filter" => "a view filter made after the last save whose copy wasn't kept: make it again by hand",
        "linepattern" or "fillpattern" => "a pattern made after the last save whose copy wasn't kept: make it again by hand",
        _ when line.What != "viewref" && line.CategoryId is (long)BuiltInCategory.OST_ReferenceViewer or (long)BuiltInCategory.OST_ReferenceViewerSymbol =>
            "a view reference made after the last save (written by an older version): place it again by hand",
        "stairs" => ChangeStairs.CanMake(line),
        "floor" or "ceiling" or "topo" or "region" or "railing" or "roof" or "roof-other" or "opening" or "modelcurve" or "detailcurve" => ChangeShapes.CanMake(line),
        _ when ChangeNotes.Owns(line) => ChangeNotes.CanMake(line),
        _ when ChangeViews.Owns(line) => ChangeViews.CanMake(line),
        _ => "made after the last save; this kind of element can't be made from the file: make it again by hand",
    };

    /// <summary>Plays <paramref name="steps"/> (one chunk) onto the model. Call with no transaction open.</summary>
    public static void Apply(Document doc, IReadOnlyList<Step> steps, Outcome outcome)
    {
        Element? Find(Document d, M.Line l) => ChangeReplay.Find(d, l, outcome.MadeFor);
        var work = steps.Where(s => s.Act is not (Act.Skip or Act.Done)).ToList();
        outcome.Same += steps.Count(s => s.Act == Act.Done);
        foreach (var s in steps.Where(s => s.Act == Act.Skip)) outcome.Missed(s.Line, $"{M.Describe(s.Line)}: {s.Why}");
        // Stairs first, each in a stairs edit scope of its own (it can't sit inside a transaction), so what is on them finds them.
        // The kept-elements project is opened (once per replay) before any transaction starts.
        if (outcome.Keeper is null && outcome.KeeperError is null && work.Any(s => NeedsKeeper(doc, s, outcome)))
            try { outcome.Keeper = ChangeKeeper.Open(doc.Application, outcome.KeptFolder); }
            catch (Exception ex) { outcome.KeeperError = ex.Message; }
        foreach (var s in work.Where(s => s.Act == Act.Make && s.Line.What == "stairs" && !FromKept(s.Line, outcome)).ToList())
        {
            work.Remove(s);
            try
            {
                var id = ChangeStairs.Make(doc, s.Line, u => u is null ? null : outcome.MadeFor.TryGetValue(u, out var m) ? doc.GetElement(m) : doc.GetElement(u));
                if (doc.GetElement(id) is { } stairs) outcome.MadeAgain(s.Line.Unique, stairs);
                outcome.Made++;
                using var t = new Transaction(doc, "Sketch ProMax: Replay change file (stairs values)");
                t.Start();
                try { SetState(doc, doc.GetElement(id), s.Line, outcome); t.Commit(); } catch (Exception) { t.RollBack(); }
            }
            catch (Exception ex)
            {
                var text = $"{M.Describe(s.Line)}: {ex.Message}";
                outcome.Missed(s.Line, text);
                outcome.Errors.Add(text);
                outcome.Failed.Add((s, text));
            }
        }
        if (work.Count == 0) return;
        // The notes and sketches each item adds, so those of an item Revit refused (and rolled back) are taken out again.
        var notesFrom = outcome.NotDone.Count;
        var result = ItemBatch.Run(doc, "Replay change file", work, s => M.Describe(s.Line),
            s => Find(doc, s.Line) is { } e ? [e.Id] : [],
            s =>
            {
                var notes = outcome.NotDone.Count;
                var did = "same";
                switch (s.Act)
                {
                    case Act.Delete:
                        // A line style or object style goes with its subcategory.
                        if (Find(doc, s.Line) is { } gone) doc.Delete(gone is GraphicsStyle { GraphicsStyleCategory: { } styled } ? styled.Id : gone.Id);
                        did = "deleted";
                        break;
                    case Act.Regroup:
                        Regroup(doc, s.Line, outcome);
                        did = "set";
                        break;
                    case Act.After:
                        did = "after";
                        if (Find(doc, s.Line) is { } settled && After(doc, settled, s.Line, outcome) && outcome.Unchanged.Remove(s.Line)) { outcome.Same--; outcome.Set++; }
                        break;
                    case Act.Set:
                        var sketches = outcome.Sketches.Count;
                        var set = SetState(doc, Find(doc, s.Line)!, s.Line, outcome);
                        did = set ? "set" : "same";
                        if (!set) outcome.Unchanged.Add(s.Line);
                        if (outcome.Sketches.Count > sketches) outcome.Sketches[outcome.Sketches.Count - 1] = outcome.Sketches[outcome.Sketches.Count - 1] with { Counted = set };
                        break;
                    case Act.Make:
                        var made = Make(doc, s.Line, outcome);
                        outcome.MadeAgain(s.Line.Unique, made);
                        // What was just made is worked out (its sketch, its faces) before its state is set and others point at it.
                        try { doc.Regenerate(); } catch (Exception) { }
                        // (A group put together again from its members is where they are: nothing to set.)
                        if (!(s.Line.What == "group" && outcome.LateGroups.Contains(s.Line.Unique!))) SetState(doc, made, s.Line, outcome, justMade: true);
                        did = "made";
                        // Only what didn't come back is said (a slope arrow that did is not a message).
                        if (s.Line.SlopeArrow && (s.Line.Arrow is null || s.Line.ArrowSlope is null))
                            outcome.NotDone.Add($"{M.Describe(s.Line)}: made again without its slope arrow: add the slope by hand");
                        break;
                }
                for (var i = notes; i < outcome.NotDone.Count; i++) outcome.Place[i] = s.Line;
                return did;
            });
        var drop = new HashSet<int>();
        for (var i = 0; i < work.Count && i < result.Items.Count; i++)
        {
            var (step, item) = (work[i], result.Items[i]);
            if (item.Ok)
            {
                switch (item.Value as string)
                {
                    case "deleted": outcome.Deleted++; break;
                    case "set": outcome.Set++; break;
                    case "made": outcome.Made++; break;
                    case "after": break;
                    default: outcome.Same++; break;
                }
                continue;
            }
            // Refused and rolled back: what it made isn't there, its notes and sketch don't count.
            if (step.Act == Act.Make) outcome.Unmake(step.Line.Unique);
            for (var n = notesFrom; n < outcome.NotDone.Count; n++)
                if (outcome.Place.TryGetValue(n, out var about) && ReferenceEquals(about, step.Line)) drop.Add(n);
            outcome.Sketches.RemoveAll(k => ReferenceEquals(k.Line, step.Line));
        }
        outcome.Drop(drop);
        for (var i = 0; i < work.Count && i < result.Items.Count; i++)
        {
            if (result.Items[i].Ok) continue;
            var text = $"{result.Items[i].Item}: {result.Items[i].Error}";
            outcome.Missed(work[i].Line, text);
            outcome.Errors.Add(text);
            outcome.Failed.Add((work[i], text));
        }
        foreach (var s in work.Where(s => s.Line.Approx && s.Act != Act.Delete))
            outcome.Missed(s.Line, $"{M.Describe(s.Line)}: its curved edges (a spline or ellipse) came back as short straight lines: check its shape");

        // Changed sketches, each in a sketch edit scope of its own, after the chunk's transactions.
        foreach (var (sketch, owner, line, counted) in outcome.Sketches)
        {
            var why = doc.GetElement(owner) is { } element ? ChangeShapes.EditSketch(doc, sketch, element, line) : "it isn't in the model any more";
            if (why is not null) outcome.Missed(line, $"{M.Describe(line)}: the outline didn't come back: {why}");
            else if (!counted) { outcome.Same--; outcome.Set++; }
        }
        outcome.Sketches.Clear();
    }

    /// <summary>
    /// After the last chunk: names that were taken when their element was played (by an element the file deletes later)
    /// are set now, in one more undo step. Call with no transaction open.
    /// </summary>
    public static void Settle(Document doc, Outcome outcome)
    {
        const string taken = "is taken: rename it by hand";
        var waiting = Enumerable.Range(0, outcome.NotDone.Count)
            .Where(i => outcome.NotDone[i].EndsWith(taken, StringComparison.Ordinal) && outcome.Place.TryGetValue(i, out var l) && l.Name is { Length: > 0 }).ToList();
        if (waiting.Count == 0 || doc.IsModifiable) return;
        var drop = new HashSet<int>();
        using var t = new Transaction(doc, "Sketch ProMax: Replay change file (names)");
        t.Start();
        foreach (var i in waiting)
        {
            var line = outcome.Place[i];
            try
            {
                if (Find(doc, line, outcome.MadeFor) is not { } element) continue;
                if (element.Name != line.Name) element.Name = line.Name;
                drop.Add(i);
            }
            catch (Exception) { /* still taken: it stays listed */ }
        }
        if (drop.Count > 0 && t.Commit() == TransactionStatus.Committed) outcome.Drop(drop);
        else if (t.HasStarted() && !t.HasEnded()) t.RollBack();
    }

    // ---- Setting an element to its state --------------------------------------------------------------------------------

    /// <summary>Sets what differs; true when anything changed.</summary>
    private static bool SetState(Document doc, Element element, M.Line line, Outcome outcome, bool justMade = false)
    {
        var changed = false;
        if (line.What == "family") return false;
        Element? Known(string? unique) => unique is null ? null : outcome.MadeFor.TryGetValue(unique, out var id) ? doc.GetElement(id) : doc.GetElement(unique);
        if (element is GraphicsStyle style) return ChangeExtras.SetStyle(doc, style, line, u => Known(u));
        // A filter or pattern that was in the model gets what its kept copy has.
        if (!justMade && line.What is "filter" or "linepattern" or "fillpattern")
        {
            if (FromKept(line, outcome) && outcome.Keeper is { } keeper) changed |= ChangeKeeper.Refresh(keeper, doc, element, outcome.Kept[line.Unique!]);
            if (line.Name is { Length: > 0 } itemName && element.Name != itemName) try { element.Name = itemName; changed = true; } catch (Exception) { }
            return changed;
        }
        // A dimension that measures other things now, or a bar of another shape, can't be edited: made again in its place.
        if (!justMade && element is Dimension or Rebar && ChangeNotes.MustRemake(doc, element, line, Remap(doc, outcome)) && (element is Dimension || ChangeExtras.SafeToReplace(doc, element)))
        {
            try
            {
                if (ChangeNotes.Make(doc, line, Type(doc, line, outcome), LevelOf(doc, line), Known, Remap(doc, outcome), outcome.NotDone) is { } again)
                {
                    doc.Delete(element.Id);
                    try { doc.Regenerate(); } catch (Exception) { }
                    outcome.MadeAgain(line.Unique, again);
                    element = again;
                    changed = true;
                }
            }
            catch (Exception) { /* what it measured isn't there: set as far as it goes, and said below */ }
        }
        if (element is not (ElementType or Material) && line.Type is not null && Type(doc, line, outcome) is { } type && element.GetTypeId() != type.Id)
        {
            element.ChangeTypeId(type.Id);
            changed = true;
        }
        // A filled region's outline can't be edited by add-ins: one whose outline changed is drawn again in its place.
        if (element is FilledRegion region && line.Loops is { Count: > 0 } outline)
        {
            var now = new M.Line();
            try { ChangeShapes.Read(doc, region, now); } catch (Autodesk.Revit.Exceptions.ApplicationException) { now.Loops = outline; }
            if (!M.SameLoops(now.Loops, outline))
            {
                var fresh = region.IsMasking
                    ? FilledRegion.CreateMaskingRegion(doc, region.OwnerViewId, ChangeShapes.CurveLoops(outline))
                    : FilledRegion.Create(doc, region.GetTypeId(), region.OwnerViewId, ChangeShapes.CurveLoops(outline));
                doc.Delete(region.Id);
                try { doc.Regenerate(); } catch (Exception) { }
                outcome.MadeAgain(line.Unique, fresh);
                element = fresh;
                changed = true;
            }
        }
        switch (element)
        {
            case Material material:
                changed |= ChangeTypes.SetMaterial(doc, material, line, u => u is null ? null : outcome.MadeFor.TryGetValue(u, out var m) ? doc.GetElement(m) : doc.GetElement(u));
                break;
            case not null when ChangeViews.Owns(line):
                changed |= ChangeViews.Set(doc, element, line, outcome.NotDone);
                break;
            case ElementType elementType:
                changed |= ChangeTypes.Set(doc, elementType, line, u => u is null ? null : outcome.MadeFor.TryGetValue(u, out var m) ? doc.GetElement(m) : doc.GetElement(u), outcome.NotDone);
                break;
            case Autodesk.Revit.DB.Architecture.Stairs stairs:
                changed |= ChangeStairs.Set(doc, stairs, line, outcome.NotDone);
                break;
            case Level level when line.Elevation is { } elevation && !M.Same(level.Elevation, elevation):
                level.Elevation = elevation;
                changed = true;
                break;
            case Grid grid when line.P is { } gp && line.Q is { } gq && grid.Curve is Line now:
                // A grid can be moved as a whole; a changed length or angle is left (and listed).
                var move = X(gp) - now.GetEndPoint(0);
                if (move.GetLength() > 3.3e-5)
                {
                    if ((X(gq) - now.GetEndPoint(1) - move).GetLength() < 3.3e-4) ElementTransformUtils.MoveElement(doc, grid.Id, move);
                    else outcome.NotDone.Add($"{M.Describe(line)}: the grid's length or angle changed; drag its ends by hand");
                    changed = true;
                }
                break;
            default:
                if (element.Location is LocationPoint lp && line.P is { } p)
                {
                    if (!M.Same(P(lp.Point), p)) { lp.Point = X(p); changed = true; }
                    if (line.Rotation is { } r)
                    {
                        double now;
                        try { now = lp.Rotation; } catch (Exception) { now = r; }
                        if (!M.Same(now, r))
                        {
                            lp.Rotate(Line.CreateBound(X(p), X(p) + XYZ.BasisZ), r - now);
                            changed = true;
                        }
                    }
                }
                else if (element.Location is LocationCurve lc && line.P is { } a && line.Q is { } b && lc.Curve is Line or Arc { IsBound: true })
                {
                    var approx = false;
                    var now = ChangeShapes.Segs(lc.Curve, ref approx).ToList();
                    var want = new M.Seg(a, b, line.M);
                    if (now.Count != 1 || !M.SameLoops([now], [[want]])) { lc.Curve = ChangeShapes.ToCurve(want); changed = true; }
                }
                break;
        }
        if (ChangeNotes.Owns(line))
        {
            changed |= ChangeNotes.Set(doc, element, line, Remap(doc, outcome), outcome.NotDone, out var noteSketch);
            if (noteSketch is { } id) outcome.Sketches.Add((id, element.Id, line, false));
        }
        else if (element is not FilledRegion && (line.Loops is not null || line.Points is not null))
        {
            changed |= ChangeShapes.Set(doc, element, line, out var sketch);
            if (sketch is { } id) outcome.Sketches.Add((id, element.Id, line, false));
        }
        if (element is FamilyInstance fi)
        {
            try
            {
                if (fi.CanFlipFacing && fi.FacingFlipped != line.FacingFlipped) { fi.flipFacing(); changed = true; }
                if (fi.CanFlipHand && fi.HandFlipped != line.HandFlipped) { fi.flipHand(); changed = true; }
            }
            catch (Exception) { }
        }
        if (element is Level or Grid or Material && line.Name is { Length: > 0 } name && element.Name != name)
        {
            try { element.Name = name; changed = true; } catch (Exception) { /* the name is taken: left as it is */ }
        }
        foreach (var v in line.Values ?? [])
        {
            var p = Find(element, v);
            if (p is null || p.IsReadOnly) continue;
            try
            {
                switch (v.Kind)
                {
                    case "d" when v.Number is { } d && !M.Same(p.AsDouble(), d): changed |= p.Set(d); break;
                    case "i" when v.Whole is { } i && p.AsInteger() != i: changed |= p.Set(i); break;
                    case "s" when (p.AsString() ?? "") != (v.Text ?? ""): changed |= p.Set(v.Text ?? ""); break;
                    case "e":
                        var target = v.Ref is { Length: > 0 } r ? (outcome.MadeFor.TryGetValue(r, out var made) ? made : doc.GetElement(r)?.Id) : ElementId.InvalidElementId;
                        if (target is not null && p.AsElementId() != target) changed |= p.Set(target);
                        break;
                }
            }
            catch (Exception) { /* a value Revit won't take here (it depends on another one): left */ }
        }
        changed |= ChangeViews.Settle(doc, element, line);
        return changed;
    }

    private static Parameter? Find(Element element, M.Value v)
    {
        if (v.Bip != 0 && element.get_Parameter((BuiltInParameter)v.Bip) is { } builtIn) return builtIn;
        if (v.Guid is { } g && Guid.TryParse(g, out var guid) && element.get_Parameter(guid) is { } shared) return shared;
        return v.Bip == 0 && v.Name is { Length: > 0 } name ? element.LookupParameter(name) : null;
    }

    // ---- Making an element again ------------------------------------------------------------------------------------------

    private static Element Make(Document doc, M.Line line, Outcome outcome)
    {
        Element? Known(string? unique) => unique is null ? null : outcome.MadeFor.TryGetValue(unique, out var id) ? doc.GetElement(id) : doc.GetElement(unique);
        if (line.What == "family") return ChangeTypes.LoadFamily(doc, line, outcome.FamiliesFolder);
        if (FromKept(line, outcome))
            return ChangeKeeper.Make(outcome.Keeper ?? throw new InvalidOperationException("its kept copy couldn't be opened" + (outcome.KeeperError is { } e ? $" ({e})" : "")),
                doc, outcome.Kept[line.Unique!], line);
        if (line.What == "material") return ChangeTypes.MakeMaterial(doc, line);
        if (line.What == "style") return ChangeExtras.MakeStyle(doc, line);
        if (line.What == "group" && outcome.LateGroups.Contains(line.Unique!))
        {
            // The first group of a new type names the type; later ones of that type take it.
            var group = ChangeViews.Regroup(doc, line, Known, Type(doc, line, outcome) as GroupType);
            if (line.TypeUnique is { } typeUnique && !outcome.MadeFor.ContainsKey(typeUnique) && doc.GetElement(typeUnique) is null) outcome.MadeAgain(typeUnique, group.GroupType);
            return group;
        }
        if (ChangeShapes.Make(doc, line, Type(doc, line, outcome), LevelOf(doc, line), Known) is { } shaped) return shaped;
        if (ChangeNotes.Make(doc, line, Type(doc, line, outcome), LevelOf(doc, line), Known, Remap(doc, outcome), outcome.NotDone) is { } note) return note;
        if (ChangeViews.Make(doc, line, Type(doc, line, outcome), LevelOf(doc, line), Known, outcome.NotDone) is { } viewed) return viewed;
        if (line.What == "type") return ChangeTypes.Make(doc, line);
        switch (line.What)
        {
            case "level":
                var level = Level.Create(doc, line.Elevation!.Value);
                return level;
            case "grid":
                return Grid.Create(doc, Line.CreateBound(X(line.P!), X(line.Q!)));
            case "wall":
            {
                var type = Type(doc, line, outcome) ?? throw new InvalidOperationException($"the wall type {line.Type} isn't in this model");
                var lvl = LevelOf(doc, line) ?? throw new InvalidOperationException("its level isn't in this model");
                if (line.Loops is { Count: > 0 } profile)
                    return Wall.Create(doc, profile[0].Select(ChangeShapes.ToCurve).ToList(), type.Id, lvl.Id, false);
                return Wall.Create(doc, ChangeShapes.ToCurve(new M.Seg(line.P!, line.Q!, line.M)), type.Id, lvl.Id, 10, 0, false, false);
            }
            default:
            {
                if (Type(doc, line, outcome) is not FamilySymbol symbol) throw new InvalidOperationException($"the type {line.Type} isn't loaded in this model");
                if (!symbol.IsActive) symbol.Activate();
                // Detail items and other families drawn in a view.
                if (line.View is not null)
                {
                    if (Known(line.View) is not View view) throw new InvalidOperationException("the view it was drawn in isn't in this model");
                    return line.Q is { } vq ? doc.Create.NewFamilyInstance(Line.CreateBound(X(line.P!), X(vq)), symbol, view) : doc.Create.NewFamilyInstance(X(line.P!), symbol, view);
                }
                var lvl = LevelOf(doc, line);
                var structural = Enum.TryParse<StructuralType>(line.Structural, out var st) ? st : StructuralType.NonStructural;
                if (line.Q is { } q)
                {
                    if (lvl is null) throw new InvalidOperationException("its level isn't in this model");
                    return doc.Create.NewFamilyInstance(ChangeShapes.ToCurve(new M.Seg(line.P!, q, line.M)), symbol, lvl, structural);
                }
                // A face-based family: on its face again, pointing the same way along it.
                if (line.Extra is { } fx && fx.TryGetValue("face", out var stable) && fx.TryGetValue("hand", out var hand))
                    try
                    {
                        var n = hand.Split(' ').Select(t => double.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                        if (Reference.ParseFromStableRepresentation(doc, Remap(doc, outcome)(stable)) is { } face) return doc.Create.NewFamilyInstance(face, X(line.P!), new XYZ(n[0], n[1], n[2]), symbol);
                    }
                    catch (Exception) { /* the face isn't there as it was: on its host or level, below */ }
                var host = line.Host is { } h ? (outcome.MadeFor.TryGetValue(h, out var madeHost) ? doc.GetElement(madeHost) : doc.GetElement(h)) : null;
                if (host is not null && lvl is not null) return doc.Create.NewFamilyInstance(X(line.P!), symbol, host, lvl, structural);
                if (host is not null) return doc.Create.NewFamilyInstance(X(line.P!), symbol, host, structural);
                if (line.Host is not null) throw new InvalidOperationException("its host isn't in this model");
                return lvl is not null ? doc.Create.NewFamilyInstance(X(line.P!), symbol, lvl, structural) : doc.Create.NewFamilyInstance(X(line.P!), symbol, structural);
            }
        }
    }

    // ---- After everything else -------------------------------------------------------------------------------------------

    /// <summary>
    /// A group type edited after the last save gets what its kept copy has (<see cref="ChangeKeeper.Regroup"/>); the
    /// members its groups had are then the new ones in the same places, so what points at them later in the file finds them.
    /// </summary>
    private static void Regroup(Document doc, M.Line line, Outcome outcome)
    {
        if (Find(doc, line, outcome.MadeFor) is not Group group) throw new InvalidOperationException("the group isn't in the model any more");
        var keeper = outcome.Keeper ?? throw new InvalidOperationException("the kept copy of the edited group couldn't be opened" + (outcome.KeeperError is { } e ? $" ({e})" : ""));
        var type = group.GroupType.Id;
        outcome.Regrouped.TryGetValue(type, out var lines);
        // The members as they are now, group by group, and the tags and dimensions on them: Revit takes those out with
        // the old members, so each is read first and made again on the member that takes the old one's place.
        var noted = new LogicalOrFilter(new ElementClassFilter(typeof(IndependentTag)), new ElementClassFilter(typeof(Dimension)));
        var groups = new List<(Group Group, List<(string Unique, string Kind, XYZ? A, XYZ? B)> Members)>();
        var notes = new Dictionary<ElementId, M.Line>();
        foreach (var placed in new FilteredElementCollector(doc).OfClass(typeof(Group)).Where(g => g.GetTypeId() == type).Cast<Group>().ToList())
        {
            var members = new List<(string, string, XYZ?, XYZ?)>();
            foreach (var id in placed.GetMemberIds())
            {
                if (doc.GetElement(id) is not { } member) continue;
                var (a, b) = Ends(member);
                members.Add((member.UniqueId, MemberKind(member), a, b));
                foreach (var on in member.GetDependentElements(noted))
                {
                    if (on == id || notes.ContainsKey(on) || doc.GetElement(on) is not { } note || note.GroupId != ElementId.InvalidElementId) continue;
                    if (ChangeFile.Capture(doc, note) is not { } noteLine || CanMake(noteLine) is not null)
                        throw new InvalidOperationException("putting the group's edit back would take out a tag or dimension on its members that can't be made again: edit the group again by hand");
                    notes[on] = noteLine;
                }
            }
            groups.Add((placed, members));
        }
        ChangeKeeper.Regroup(keeper, doc, group, outcome.Kept[M.GroupEdit(line)!.Value.Key], () =>
        {
            // Which new member took which old one's place: by the group's line first (its members as the edit left them, in order)...
            foreach (var l in lines ?? [])
            {
                if (Find(doc, l, outcome.MadeFor) is not Group placed) continue;
                var (were, are) = (M.Members(l), placed.GetMemberIds().ToList());
                if (were.Count != are.Count) continue;
                for (var i = 0; i < were.Count; i++)
                    if (doc.GetElement(were[i]) is null && doc.GetElement(are[i]) is { } member) outcome.MadeAgain(were[i], member);
            }
            // ...then, for a member the lines don't name, the new member of its kind in the same place.
            foreach (var (placed, members) in groups)
            {
                var now = placed.GetMemberIds().Select(doc.GetElement).Where(m => m is not null).ToList();
                var taken = new HashSet<ElementId>(members.Select(m => outcome.MadeFor.TryGetValue(m.Unique, out var id) ? id : ElementId.InvalidElementId));
                foreach (var (unique, kind, a, b) in members)
                {
                    if (doc.GetElement(unique) is not null || outcome.MadeFor.ContainsKey(unique) || a is null) continue;
                    var match = now.FirstOrDefault(n => !taken.Contains(n!.Id) && MemberKind(n) == kind && Ends(n) is var (na, nb) && na is not null
                        && ((na.DistanceTo(a) < 0.003 && (b is null || nb is null || nb.DistanceTo(b) < 0.003)) || (b is not null && nb is not null && na.DistanceTo(b) < 0.003 && nb.DistanceTo(a) < 0.003)));
                    if (match is null) continue;
                    taken.Add(match.Id);
                    outcome.MadeAgain(unique, match);
                }
            }
            try { doc.Regenerate(); } catch (Exception) { }
            var again = new HashSet<ElementId>();
            foreach (var note in notes.Where(n => doc.GetElement(n.Key) is null))
            {
                Element made;
                try { made = Make(doc, note.Value, outcome); }
                catch (Exception) { continue; /* counted as lost: the edit isn't put back */ }
                outcome.MadeAgain(note.Value.Unique, made);
                try { doc.Regenerate(); } catch (Exception) { }
                try { SetState(doc, made, note.Value, outcome, justMade: true); } catch (Exception) { }
                again.Add(note.Key);
            }
            return again;
        });
    }

    private static string MemberKind(Element e) => $"{e.Category?.Id.Value ?? 0}|{e.GetTypeId().Value}";

    private static (XYZ? A, XYZ? B) Ends(Element e)
    {
        try
        {
            return e.Location switch
            {
                LocationPoint { Point: { } at } => (at, null),
                LocationCurve { Curve: { IsBound: true } curve } => (curve.GetEndPoint(0), curve.GetEndPoint(1)),
                _ => (null, null),
            };
        }
        catch (Exception) { return (null, null); }
    }

    /// <summary>True when the step needs the kept-elements project open (it must be opened before any transaction starts).</summary>
    private static bool NeedsKeeper(Document doc, Step s, Outcome outcome)
    {
        if (s.Act == Act.Regroup) return true;
        if (!FromKept(s.Line, outcome)) return false;
        switch (s.Act)
        {
            case Act.Make: return true;
            case Act.Set: return s.Line.What is "filter" or "linepattern" or "fillpattern";
            case Act.After:
                try { return s.Line.Extra!.TryGetValue("shape", out var shape) && Find(doc, s.Line, outcome.MadeFor) is { } e && !M.SameSignature(ChangeExtras.Signature(e), shape); }
                catch (Exception) { return false; }
            default: return false;
        }
    }

    /// <summary>
    /// What a line has to do once everything is played: its element's joins, cuts and attachments, a view's graphics,
    /// and an element whose shape isn't its line's put back from its kept copy. True when anything changed.
    /// Call inside a transaction.
    /// </summary>
    private static bool After(Document doc, Element element, M.Line line, Outcome outcome)
    {
        Element? Known(string unique) => outcome.MadeFor.TryGetValue(unique, out var id) ? doc.GetElement(id) : doc.GetElement(unique);
        M.Line? LineOf(string unique)
        {
            if (outcome.Relations.TryGetValue(unique, out var own)) return own;
            // The element may have been made again in this replay: its line is under its old UniqueId.
            foreach (var made in outcome.MadeUnique) if (made.Value == unique && outcome.Relations.TryGetValue(made.Key, out var old)) return old;
            return null;
        }
        var x = line.Extra!;
        var changed = false;
        if (element is View view && x.TryGetValue("vg", out var graphics)) changed |= ChangeExtras.SetGraphics(doc, view, graphics, Known);
        changed |= ChangeExtras.Relate(doc, element, line, Known, LineOf);
        if (x.TryGetValue("shape", out var shape))
        {
            if (changed) try { doc.Regenerate(); } catch (Exception) { }
            if (!M.SameSignature(ChangeExtras.Signature(element), shape))
            {
                if (FromKept(line, outcome) && outcome.Keeper is { } keeper && ChangeExtras.SafeToReplace(doc, element))
                {
                    var fresh = ChangeKeeper.Make(keeper, doc, outcome.Kept[line.Unique!], line);
                    doc.Delete(element.Id);
                    try { doc.Regenerate(); } catch (Exception) { }
                    outcome.MadeAgain(line.Unique, fresh);
                    ChangeExtras.Relate(doc, fresh, line, Known, LineOf);
                    changed = true;
                }
                else outcome.NotDone.Add($"{M.Describe(line)}: its shape was changed after the last save and can't be put back from the file: reshape it by hand");
            }
        }
        return changed;
    }

    /// <summary>Points a stable reference at the elements made again so far in this replay.</summary>
    private static Func<string, string> Remap(Document doc, Outcome outcome)
    {
        var now = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var made in outcome.MadeFor)
            if (doc.GetElement(made.Value)?.UniqueId is { } unique) now[made.Key] = unique;
        return r => M.Remap(r, now);
    }

    private static ElementType? Type(Document doc, M.Line line, Outcome outcome)
    {
        if (line.TypeUnique is { } u && (outcome.MadeFor.TryGetValue(u, out var again) ? doc.GetElement(again) : doc.GetElement(u)) is ElementType byId) return byId;
        if (line.Type is not { } name) return null;
        // By name: of the element's category (text and dimension types have no category of their own).
        return new FilteredElementCollector(doc).WhereElementIsElementType().Cast<ElementType>()
            .FirstOrDefault(t => $"{t.FamilyName}: {t.Name}" == name && (line.CategoryId == 0 || t.Category is null || t.Category.Id.Value == line.CategoryId));
    }

    private static Level? LevelOf(Document doc, M.Line line)
    {
        if (line.LevelUnique is { } u && doc.GetElement(u) is Level byId) return byId;
        return line.Level is { } name ? new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault(l => l.Name == name) : null;
    }

    private static XYZ X(M.Xyz p) => new(p.X, p.Y, p.Z);

    private static M.Xyz P(XYZ p) => new(p.X, p.Y, p.Z);
}
