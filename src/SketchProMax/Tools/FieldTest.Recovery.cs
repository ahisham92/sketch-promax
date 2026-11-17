using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SketchProMax.Revit;
using M = SketchProMax.Revit.ChangeFileMath;

namespace SketchProMax.Tools;

/// <summary>
/// The probes for the change file and Recover (Revit/ChangeFile, Revit/ChangeReplay): in the test project, with the
/// files sent to a folder of the test folder.
/// </summary>
internal static partial class FieldTest
{
    /// <summary>
    /// A new wall, then moved with a comment, then deleted: each reaches the change file as a line per element; saving
    /// moves the file to "before last save". Run from the external event (it saves the test project).
    /// </summary>
    private static (string, string) RecoveryFile(UIApplication app, State s)
    {
        if (TestDoc(app, s, out var why) is not { } doc) return Fail(why);
        if (BasicWallType(doc) is not { } type) return Fail("the test project has no basic wall type with layers");
        if (new FilteredElementCollector(doc).OfClass(typeof(Level)).FirstElement() is not Level level) return Fail("the test project has no level");
        var root = Path.Combine(s.Folder, "change-files");
        ChangeFile.RootForTest = root;
        ChangeFile.ForceForTest = true;
        var problems = new List<string>();
        try
        {
            ChangeFile.TrackForTest(doc);
            var file = ChangeFile.CurrentFor(doc.PathName);
            M.Parsed Read()
            {
                ChangeFile.ReadNow(doc);
                ChangeFile.Flush(TimeSpan.FromSeconds(5));
                return M.Read(ChangeFile.ReadLines(file));
            }
            Wall wall;
            using (var t = new Transaction(doc, "Field test: change file wall"))
            {
                t.Start();
                wall = Wall.Create(doc, Line.CreateBound(new XYZ(0, -40, 0), new XYZ(20, -40, 0)), type.Id, level.Id, 10, 0, false, false);
                t.Commit();
            }
            var unique = wall.UniqueId;
            var first = Read().Elements.FirstOrDefault(l => l.Unique == unique);
            if (first is not { Kind: M.Kinds.State, What: "wall" } || first.P is null || first.Q is null || first.Type is null || first.LevelUnique != level.UniqueId)
                problems.Add("the new wall's line is missing or lacks its type, level or place");
            using (var t = new Transaction(doc, "Field test: change file move"))
            {
                t.Start();
                ElementTransformUtils.MoveElement(doc, wall.Id, new XYZ(0, -5, 0));
                wall.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set("SPM change file");
                t.Commit();
            }
            var moved = Read().Elements.FirstOrDefault(l => l.Unique == unique);
            if (moved?.P is not { } p || !M.Same(p.Y, -45)) problems.Add("the moved wall's line doesn't have its new place");
            if (moved?.Values?.Any(v => v.Bip == (int)BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS && v.Text == "SPM change file") != true)
                problems.Add("the wall's comment isn't in the file");
            using (var t = new Transaction(doc, "Field test: change file delete"))
            {
                t.Start();
                doc.Delete(wall.Id);
                t.Commit();
            }
            var parsed = Read();
            if (parsed.Elements.FirstOrDefault(l => l.Unique == unique)?.Kind != M.Kinds.Deleted) problems.Add("the deletion isn't in the file by UniqueId");
            var size = File.Exists(file) ? new FileInfo(file).Length : 0;
            if (size == 0 || size > 64 * 1024) problems.Add($"the file is {size} bytes after three changes of one wall (want a few KB)");
            if (parsed.Model is null) problems.Add("the file doesn't say which model it is for");

            try { doc.Save(); }
            catch (Exception ex) { return Fail("couldn't save the test project to check the clearing: " + ex.Message); }
            ChangeFile.Flush(TimeSpan.FromSeconds(5));
            if (File.Exists(file)) problems.Add("saving didn't clear the file");
            if (!File.Exists(Path.Combine(ChangeFile.FolderFor(doc.PathName), M.LastSaveName))) problems.Add("saving didn't keep the cleared file as \"before last save\"");
            return problems.Count == 0 ? Pass($"one wall added, moved with a comment and deleted: {parsed.Elements.Count} line(s), {size} bytes; cleared at save") : Fail(string.Join("; ", problems));
        }
        finally
        {
            ChangeFile.ForgetForTest(doc);
            ChangeFile.RootForTest = null;
            ChangeFile.ForceForTest = false;
        }
    }

    /// <summary>
    /// A change file (a wall moved with a comment, a wall made after the save, a wall deleted, and an element the file
    /// can't make) played onto the test project as it was before those changes; all rolled back.
    /// </summary>
    private static (string, string) RecoveryReplay(UIApplication app, State s)
    {
        if (TestDoc(app, s, out var why) is not { } doc) return Fail(why);
        if (BasicWallType(doc) is not { } type) return Fail("the test project has no basic wall type with layers");
        if (new FilteredElementCollector(doc).OfClass(typeof(Level)).FirstElement() is not Level level) return Fail("the test project has no level");
        var problems = new List<string>();
        using var group = new TransactionGroup(doc, "Field test: replay");
        group.Start();
        try
        {
            Wall a, b;
            using (var t = new Transaction(doc, "Field test: replay walls"))
            {
                t.Start();
                a = Wall.Create(doc, Line.CreateBound(new XYZ(0, -60, 0), new XYZ(20, -60, 0)), type.Id, level.Id, 10, 0, false, false);
                b = Wall.Create(doc, Line.CreateBound(new XYZ(0, -70, 0), new XYZ(20, -70, 0)), type.Id, level.Id, 10, 0, false, false);
                t.Commit();
            }
            // The state the file holds: A moved with a comment, B as made.
            using (var t = new Transaction(doc, "Field test: replay change"))
            {
                t.Start();
                ElementTransformUtils.MoveElement(doc, a.Id, new XYZ(0, -3, 0));
                a.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set("SPM replay");
                t.Commit();
            }
            var lineA = ChangeFile.Capture(doc, a)!;
            var lineB = ChangeFile.Capture(doc, b)!;
            var bUnique = b.UniqueId;
            // The "older copy": A back where it was, B not made yet, C still there.
            Wall c;
            using (var t = new Transaction(doc, "Field test: replay older copy"))
            {
                t.Start();
                ElementTransformUtils.MoveElement(doc, a.Id, new XYZ(0, 3, 0));
                a.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set("");
                doc.Delete(b.Id);
                c = Wall.Create(doc, Line.CreateBound(new XYZ(0, -80, 0), new XYZ(20, -80, 0)), type.Id, level.Id, 10, 0, false, false);
                t.Commit();
            }
            var lines = new List<string>
            {
                M.Write(new M.Line { Kind = M.Kinds.Model, At = DateTime.Now, Path = doc.PathName }),
                M.Write(lineA), M.Write(lineB),
                M.Write(new M.Line { Kind = M.Kinds.Deleted, At = DateTime.Now, Unique = c.UniqueId, Id = c.Id.Value, Category = "Walls" }),
                M.Write(new M.Line { Kind = M.Kinds.State, At = DateTime.Now, Unique = Guid.NewGuid().ToString(), What = "other", Category = "Floors", Name = "SPM sketch" }),
            };
            var parsed = M.Read(lines);
            var steps = ChangeReplay.Plan(doc, parsed);
            var acts = string.Join(", ", steps.Select(st => st.Act));
            if (steps.Count(st => st.Act == ChangeReplay.Act.Set) != 1 || steps.Count(st => st.Act == ChangeReplay.Act.Make) != 1
                || steps.Count(st => st.Act == ChangeReplay.Act.Delete) != 1 || steps.Count(st => st.Act == ChangeReplay.Act.Skip) != 1)
                problems.Add("the plan isn't set 1, make 1, delete 1, skip 1: " + acts);
            var outcome = new ChangeReplay.Outcome();
            ChangeReplay.Apply(doc, steps, outcome);
            if (a.Location is not LocationCurve { Curve: Line la } || !M.Same(la.GetEndPoint(0).Y, -63)) problems.Add("wall A isn't back at the file's place");
            if (a.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() != "SPM replay") problems.Add("wall A's comment isn't back");
            if (!outcome.MadeFor.TryGetValue(bUnique, out var madeB) || doc.GetElement(madeB) is not Wall nb || nb.WallType.Id != type.Id || nb.LevelId != level.Id)
                problems.Add("wall B wasn't made again with its type on its level");
            if (c.IsValidObject && doc.GetElement(c.Id) is not null) problems.Add("wall C wasn't deleted");
            if (!outcome.NotDone.Any(n => n.Contains("SPM sketch"))) problems.Add("the element the file can't make isn't listed to redo by hand");
            return problems.Count == 0
                ? Pass($"{outcome.Set} set back, {outcome.Made} made again, {outcome.Deleted} deleted, {outcome.NotDone.Count} listed to redo by hand; rolled back")
                : Fail(string.Join("; ", problems));
        }
        finally
        {
            if (group.HasStarted()) group.RollBack();
        }
    }

    /// <summary>
    /// Floors, the main sketch case: a floor whose outline changed after the save is redrawn from the file (in a sketch
    /// edit scope, so not inside a transaction group), a floor deleted after the save is made again from its outline
    /// with its type and level. What it made is deleted afterwards.
    /// </summary>
    private static (string, string) RecoveryShapes(UIApplication app, State s)
    {
        if (TestDoc(app, s, out var why) is not { } doc) return Fail(why);
        if (new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().FirstOrDefault(t => !t.IsFoundationSlab) is not { } floorType) return Fail("the test project has no floor type");
        if (new FilteredElementCollector(doc).OfClass(typeof(Level)).FirstElement() is not Level level) return Fail("the test project has no level");
        var z = level.Elevation;
        List<M.Seg> Rect(double x0, double y0, double x1, double y1) =>
        [
            new(new(x0, y0, z), new(x1, y0, z)), new(new(x1, y0, z), new(x1, y1, z)),
            new(new(x1, y1, z), new(x0, y1, z)), new(new(x0, y1, z), new(x0, y0, z)),
        ];
        var problems = new List<string>();
        var made = new List<ElementId>();
        try
        {
            Floor edited, gone;
            using (var t = new Transaction(doc, "Field test: replay floors"))
            {
                t.Start();
                edited = Floor.Create(doc, ChangeShapes.CurveLoops([Rect(100, 0, 120, 20)]), floorType.Id, level.Id);
                gone = Floor.Create(doc, ChangeShapes.CurveLoops([Rect(130, 0, 140, 10)]), floorType.Id, level.Id);
                t.Commit();
            }
            made.Add(edited.Id);
            // The file: the first floor with a bigger outline (an L), the second as it was before it was deleted.
            var lineEdited = ChangeFile.Capture(doc, edited)!;
            if (lineEdited.Loops is not { Count: 1 } || lineEdited.Loops[0].Count != 4) problems.Add("a floor's outline isn't read as one loop of four edges");
            var l = new List<M.Seg>
            {
                new(new(100, 0, z), new(130, 0, z)), new(new(130, 0, z), new(130, 10, z)), new(new(130, 10, z), new(110, 10, z)),
                new(new(110, 10, z), new(110, 20, z)), new(new(110, 20, z), new(100, 20, z)), new(new(100, 20, z), new(100, 0, z)),
            };
            lineEdited.Loops = [l];
            var lineGone = ChangeFile.Capture(doc, gone)!;
            var goneUnique = gone.UniqueId;
            using (var t = new Transaction(doc, "Field test: replay delete floor"))
            {
                t.Start();
                doc.Delete(gone.Id);
                t.Commit();
            }
            var parsed = M.Read([M.Write(lineEdited), M.Write(lineGone)]);
            var outcome = new ChangeReplay.Outcome();
            ChangeReplay.Apply(doc, ChangeReplay.Plan(doc, parsed), outcome);
            if (outcome.MadeFor.TryGetValue(goneUnique, out var again)) made.Add(again);
            var now = new M.Line();
            ChangeShapes.Read(doc, edited, now);
            if (!M.SameLoops(now.Loops, [l])) problems.Add("the edited floor's outline didn't come back (" + string.Join("; ", outcome.NotDone) + ")");
            if (doc.GetElement(again) is not Floor remade || remade.FloorType.Id != floorType.Id || remade.LevelId != level.Id)
                problems.Add("the deleted floor wasn't made again with its type on its level (" + string.Join("; ", outcome.NotDone) + ")");
            else
            {
                var remadeLine = new M.Line();
                ChangeShapes.Read(doc, remade, remadeLine);
                if (!M.SameLoops(remadeLine.Loops, lineGone.Loops)) problems.Add("the floor made again doesn't have its outline");
            }
            return problems.Count == 0
                ? Pass($"a floor's changed outline redrawn and a deleted floor made again from its outline ({outcome.Set} set back, {outcome.Made} made again)")
                : Fail(string.Join("; ", problems));
        }
        finally
        {
            using var t = new Transaction(doc, "Field test: replay floors tidy up");
            t.Start();
            foreach (var id in made) try { if (doc.GetElement(id) is not null) doc.Delete(id); } catch (Exception) { }
            t.Commit();
        }
    }

    /// <summary>
    /// Annotations: in a plan view of the test project, a text note whose text changed after the save is set back, and
    /// a dimension between two grids and a text note deleted after the save are made again (the dimension on the same
    /// grids). What it made is deleted afterwards.
    /// </summary>
    private static (string, string) RecoveryNotes(UIApplication app, State s)
    {
        if (TestDoc(app, s, out var why) is not { } doc) return Fail(why);
        if (new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().FirstOrDefault(v => !v.IsTemplate && v.GenLevel is not null) is not { } plan) return Fail("the test project has no plan view");
        if (new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).FirstElementId() is not { } textType || textType == ElementId.InvalidElementId) return Fail("the test project has no text type");
        var problems = new List<string>();
        var made = new List<ElementId>();
        try
        {
            TextNote edited, goneText;
            Dimension goneDim;
            Grid a, b;
            using (var t = new Transaction(doc, "Field test: replay notes"))
            {
                t.Start();
                a = Grid.Create(doc, Line.CreateBound(new XYZ(200, 0, 0), new XYZ(200, 40, 0)));
                b = Grid.Create(doc, Line.CreateBound(new XYZ(230, 0, 0), new XYZ(230, 40, 0)));
                edited = TextNote.Create(doc, plan.Id, new XYZ(200, 50, 0), "Field test note", textType);
                goneText = TextNote.Create(doc, plan.Id, new XYZ(200, 60, 0), "Field test note made again", textType);
                // Grids made a moment ago have nothing to dimension to until the model is regenerated.
                doc.Regenerate();
                var refs = new ReferenceArray();
                refs.Append(new Reference(a));
                refs.Append(new Reference(b));
                goneDim = doc.Create.NewDimension(plan, Line.CreateBound(new XYZ(200, 20, 0), new XYZ(230, 20, 0)), refs);
                t.Commit();
            }
            made.AddRange([a.Id, b.Id, edited.Id]);
            var lineEdited = ChangeFile.Capture(doc, edited)!;
            var lineText = ChangeFile.Capture(doc, goneText)!;
            var lineDim = ChangeFile.Capture(doc, goneDim)!;
            if (lineDim.Refs is not { Count: 2 }) problems.Add("a dimension's two grids aren't in its line");
            using (var t = new Transaction(doc, "Field test: replay notes change"))
            {
                t.Start();
                edited.Text = "Changed after the save";
                doc.Delete(goneText.Id);
                doc.Delete(goneDim.Id);
                t.Commit();
            }
            var parsed = M.Read([M.Write(lineEdited), M.Write(lineText), M.Write(lineDim)]);
            var outcome = new ChangeReplay.Outcome();
            ChangeReplay.Apply(doc, ChangeReplay.Plan(doc, parsed), outcome);
            made.AddRange(outcome.MadeFor.Values);
            if (!edited.Text.StartsWith("Field test note", StringComparison.Ordinal)) problems.Add("the text note's text didn't come back (" + string.Join("; ", outcome.NotDone) + ")");
            if (!outcome.MadeFor.TryGetValue(lineText.Unique!, out var textAgain) || doc.GetElement(textAgain) is not TextNote again || !again.Text.StartsWith("Field test note made again", StringComparison.Ordinal) || again.OwnerViewId != plan.Id)
                problems.Add("the deleted text note wasn't made again in its view (" + string.Join("; ", outcome.NotDone) + ")");
            if (!outcome.MadeFor.TryGetValue(lineDim.Unique!, out var dimAgain) || doc.GetElement(dimAgain) is not Dimension dim || dim.References.Size != 2)
                problems.Add("the deleted dimension wasn't made again between its grids (" + string.Join("; ", outcome.NotDone) + ")");
            return problems.Count == 0
                ? Pass($"a text note's text set back, a deleted text note and dimension made again ({outcome.Set} set back, {outcome.Made} made again)")
                : Fail(string.Join("; ", problems));
        }
        finally
        {
            using var t = new Transaction(doc, "Field test: replay notes tidy up");
            t.Start();
            foreach (var id in made) try { if (doc.GetElement(id) is not null) doc.Delete(id); } catch (Exception) { }
            t.Commit();
        }
    }

    /// <summary>
    /// A drafting view on a new sheet and a text note with a leader, all made after the save and deleted: replay makes the
    /// view, the sheet and the viewport again, and the note with its leader; a grid that was there before and is gone (as
    /// if someone deleted it in central) is skipped and listed, not made again.
    /// </summary>
    private static (string, string) RecoveryViews(UIApplication app, State s)
    {
        if (TestDoc(app, s, out var why) is not { } doc) return Fail(why);
        if (new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().FirstOrDefault(t => t.ViewFamily == ViewFamily.Drafting) is not { } draftingType) return Fail("the test project has no drafting view type");
        if (new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).FirstElementId() is not { } textType || textType == ElementId.InvalidElementId) return Fail("the test project has no text type");
        var problems = new List<string>();
        var made = new List<ElementId>();
        try
        {
            ViewDrafting drafting;
            ViewSheet sheet;
            Viewport viewport;
            TextNote note;
            Grid grid;
            var name = "Field test drafting " + DateTime.Now.ToString("HHmmss");
            using (var t = new Transaction(doc, "Field test: replay views"))
            {
                t.Start();
                drafting = ViewDrafting.Create(doc, draftingType.Id);
                drafting.Name = name;
                note = TextNote.Create(doc, drafting.Id, new XYZ(0, 0, 0), "Field test leader note", textType);
                var leader = note.AddLeader(TextNoteLeaderTypes.TNLT_STRAIGHT_R);
                leader.End = new XYZ(3, -2, 0);
                sheet = ViewSheet.Create(doc, ElementId.InvalidElementId);
                // A view and a sheet made a moment ago can't be put together until the model is regenerated.
                doc.Regenerate();
                viewport = Viewport.Create(doc, sheet.Id, drafting.Id, new XYZ(1, 1, 0));
                grid = Grid.Create(doc, Line.CreateBound(new XYZ(300, 0, 0), new XYZ(300, 40, 0)));
                t.Commit();
            }
            // Their ids and names now: a deleted element can't be asked for them.
            var gridId = grid.Id;
            var (draftingUnique, sheetUnique, noteUnique) = (drafting.UniqueId, sheet.UniqueId, note.UniqueId);
            made.Add(gridId);
            var lines = new List<M.Line>();
            foreach (var e in new Element[] { drafting, sheet, viewport, note })
            {
                if (ChangeFile.Capture(doc, e) is not { } line) { problems.Add($"{e.GetType().Name} wasn't captured"); continue; }
                line.New = true;
                lines.Add(line);
            }
            var gridLine = ChangeFile.Capture(doc, grid)!;
            lines.Add(gridLine);
            if (lines.FirstOrDefault(l => l.What == "text") is not { Leads.Count: 1 }) problems.Add("the text note's leader isn't in its line");
            using (var t = new Transaction(doc, "Field test: replay views change"))
            {
                t.Start();
                doc.Delete(sheet.Id);
                doc.Delete(drafting.Id);
                doc.Delete(gridId);
                t.Commit();
            }
            made.Remove(gridId);
            var model = new M.Line { Kind = M.Kinds.Model, At = DateTime.Now, Path = doc.PathName, Format = M.FormatNew };
            var parsed = M.Read(lines.Prepend(model).Select(M.Write));
            var outcome = new ChangeReplay.Outcome();
            ChangeReplay.Apply(doc, ChangeReplay.Plan(doc, parsed), outcome);
            made.AddRange(outcome.MadeFor.Values);
            var notDone = " (" + string.Join("; ", outcome.NotDone) + ")";
            View? Again(string unique) => outcome.MadeFor.TryGetValue(unique, out var id) ? doc.GetElement(id) as View : null;
            if (Again(draftingUnique) is not ViewDrafting draftingAgain || draftingAgain.Name != name) problems.Add("the drafting view wasn't made again with its name" + notDone);
            if (Again(sheetUnique) is not ViewSheet sheetAgain || sheetAgain.GetAllViewports().Count != 1) problems.Add("the sheet wasn't made again with its viewport" + notDone);
            if (!outcome.MadeFor.TryGetValue(noteUnique, out var noteAgain) || doc.GetElement(noteAgain) is not TextNote noteBack || noteBack.LeaderCount != 1) problems.Add("the text note wasn't made again with its leader" + notDone);
            // Not marked as made in the session, yet gone: replay makes it again too (it never guesses someone else deleted it).
            if (!outcome.MadeFor.ContainsKey(gridLine.Unique!)) problems.Add("a grid gone from the model (not marked new) wasn't made again" + notDone);
            return problems.Count == 0
                ? Pass($"a drafting view, a sheet with its viewport, a text note with its leader and a grid not marked new made again ({outcome.Made} made again)")
                : Fail(string.Join("; ", problems));
        }
        finally
        {
            using var t = new Transaction(doc, "Field test: replay views tidy up");
            t.Start();
            foreach (var id in made) try { if (doc.GetElement(id) is not null) doc.Delete(id); } catch (Exception) { }
            t.Commit();
        }
    }

    /// <summary>
    /// A wall type duplicated with a changed layer, an elevation and straight stairs, all made after the save and deleted:
    /// replay makes the type again with its layers, the elevation looking the same way, and the stairs with their run.
    /// </summary>
    private static (string, string) RecoveryTypes(UIApplication app, State s)
    {
        if (TestDoc(app, s, out var why) is not { } doc) return Fail(why);
        if (BasicWallType(doc) is not { } wallType) return Fail("the test project has no basic wall type with layers");
        if (new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).FirstOrDefault() is not { } level) return Fail("the test project has no level");
        if (new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().FirstOrDefault(t => t.ViewFamily == ViewFamily.Elevation) is not { } elevationType) return Fail("the test project has no elevation type");
        if (new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().FirstOrDefault(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan && v.GenLevel is not null) is not { } plan) return Fail("the test project has no floor plan");
        var problems = new List<string>();
        var made = new List<ElementId>();
        try
        {
            ElementType copy;
            View elevation;
            ElevationMarker marker;
            Level top;
            using (var t = new Transaction(doc, "Field test: replay types"))
            {
                t.Start();
                copy = wallType.Duplicate("Field test type " + DateTime.Now.ToString("HHmmss"));
                var cs = ((HostObjAttributes)copy).GetCompoundStructure();
                cs.SetLayerWidth(0, cs.GetLayerWidth(0) + 0.1);
                ((HostObjAttributes)copy).SetCompoundStructure(cs);
                marker = ElevationMarker.CreateElevationMarker(doc, elevationType.Id, new XYZ(400, 0, level.Elevation), 100);
                doc.Regenerate();
                elevation = marker.CreateElevation(doc, plan.Id, 0);
                top = Level.Create(doc, level.Elevation + 10);
                t.Commit();
            }
            made.Add(top.Id);
            ElementId stairsId;
            using (var scope = new StairsEditScope(doc, "Field test: stairs"))
            {
                stairsId = scope.Start(level.Id, top.Id);
                using (var t = new Transaction(doc, "Field test: stairs run"))
                {
                    t.Start();
                    Autodesk.Revit.DB.Architecture.StairsRun.CreateStraightRun(doc, stairsId, Line.CreateBound(new XYZ(420, 0, level.Elevation), new XYZ(440, 0, level.Elevation)), Autodesk.Revit.DB.Architecture.StairsRunJustification.Center);
                    t.Commit();
                }
                scope.Commit(new ChangeShapes.Quiet());
            }
            var stairs = doc.GetElement(stairsId);
            var lines = new List<M.Line>();
            foreach (var e in new Element[] { copy, elevation, stairs })
            {
                if (ChangeFile.Capture(doc, e) is not { } line) { problems.Add($"{e.GetType().Name} wasn't captured"); continue; }
                line.New = true;
                lines.Add(line);
            }
            if (lines.FirstOrDefault(l => l.What == "type") is not { Layers.Count: > 0 }) problems.Add("the type's layers aren't in its line");
            if (lines.FirstOrDefault(l => l.What == "stairs") is not { Runs.Count: 1 }) problems.Add("the stairs' run isn't in its line");
            var direction = elevation.ViewDirection;
            var copyName = copy.Name;
            var width = ((HostObjAttributes)copy).GetCompoundStructure().GetLayerWidth(0);
            var uniques = new[] { copy.UniqueId, elevation.UniqueId, stairs.UniqueId };
            using (var t = new Transaction(doc, "Field test: replay types change"))
            {
                t.Start();
                doc.Delete(stairsId);
                doc.Delete(marker.Id);
                doc.Delete(copy.Id);
                t.Commit();
            }
            var model = new M.Line { Kind = M.Kinds.Model, At = DateTime.Now, Path = doc.PathName, Format = M.FormatNew };
            var parsed = M.Read(lines.Prepend(model).Select(M.Write));
            var outcome = new ChangeReplay.Outcome();
            ChangeReplay.Apply(doc, ChangeReplay.Plan(doc, parsed), outcome);
            foreach (var id in outcome.MadeFor.Values) made.Add(id);
            var notDone = " (" + string.Join("; ", outcome.NotDone) + ")";
            Element? Again(string unique) => outcome.MadeFor.TryGetValue(unique, out var id) ? doc.GetElement(id) : null;
            if (Again(uniques[0]) is not HostObjAttributes typeBack || ((ElementType)typeBack).Name != copyName || !M.Same(typeBack.GetCompoundStructure().GetLayerWidth(0), width))
                problems.Add("the wall type wasn't made again with its layers" + notDone);
            if (Again(uniques[1]) is not View elevationBack || !elevationBack.ViewDirection.IsAlmostEqualTo(direction))
                problems.Add("the elevation wasn't made again looking the same way" + notDone);
            if (Again(uniques[2]) is not Autodesk.Revit.DB.Architecture.Stairs stairsBack || stairsBack.GetStairsRuns().Count != 1)
                problems.Add("the stairs weren't made again with their run" + notDone);
            return problems.Count == 0
                ? Pass($"a wall type with its layers, an elevation and stairs made again ({outcome.Made} made again)")
                : Fail(string.Join("; ", problems));
        }
        finally
        {
            using var t = new Transaction(doc, "Field test: replay types tidy up");
            t.Start();
            // The elevations' markers first, then the rest (the type last, once nothing uses it).
            foreach (var id in made.ToList())
                if (doc.GetElement(id) is View v && new FilteredElementCollector(doc).OfClass(typeof(ElevationMarker)).Cast<ElevationMarker>().FirstOrDefault(m => Enumerable.Range(0, m.MaximumViewCount).Any(i => m.GetViewId(i) == v.Id)) is { } m)
                    try { doc.Delete(m.Id); } catch (Exception) { }
            foreach (var id in made.OrderBy(id => doc.GetElement(id) is ElementType ? 1 : 0).ToList()) try { if (doc.GetElement(id) is not null) doc.Delete(id); } catch (Exception) { }
            t.Commit();
        }
    }

    /// <summary>
    /// What a real replay missed (9 Nov 2026): a deletion of an element Lifeline never read names it; a type's line
    /// has no outline of its elements; a material and a loaded family made after the save come back (the family from
    /// its kept copy); a file played twice sets what the first play made instead of making it again. What it made is
    /// deleted afterwards.
    /// </summary>
    private static (string, string) RecoveryComplete(UIApplication app, State s)
    {
        if (TestDoc(app, s, out var why) is not { } doc) return Fail(why);
        if (BasicWallType(doc) is not { } type) return Fail("the test project has no basic wall type with layers");
        if (new FilteredElementCollector(doc).OfClass(typeof(Level)).FirstElement() is not Level level) return Fail("the test project has no level");
        var root = Path.Combine(s.Folder, "change-files-complete");
        ChangeFile.RootForTest = root;
        ChangeFile.ForceForTest = true;
        var problems = new List<string>();
        var notes = new List<string>();
        var made = new List<ElementId>();
        try
        {
            // 1. A wall that was in the model before Lifeline looked (never read, never selected), then deleted.
            Wall old;
            using (var t = new Transaction(doc, "Field test: lifeline old wall"))
            {
                t.Start();
                old = Wall.Create(doc, Line.CreateBound(new XYZ(0, -120, 0), new XYZ(20, -120, 0)), type.Id, level.Id, 10, 0, false, false);
                t.Commit();
            }
            var oldUnique = old.UniqueId;
            ChangeFile.TrackForTest(doc);
            ChangeFile.NoteAllForTest(doc);
            var file = ChangeFile.CurrentFor(doc.PathName);
            using (var t = new Transaction(doc, "Field test: lifeline delete old wall"))
            {
                t.Start();
                doc.Delete(old.Id);
                t.Commit();
            }
            ChangeFile.ReadNow(doc);
            ChangeFile.Flush(TimeSpan.FromSeconds(5));
            var deleted = M.Read(ChangeFile.ReadLines(file)).Elements.FirstOrDefault(l => l.Kind == M.Kinds.Deleted && l.Unique == oldUnique);
            if (deleted is null) problems.Add("the deletion of a wall Lifeline never read isn't named by its UniqueId");
            else if (deleted.Category is not { Length: > 0 }) problems.Add("the named deletion has no category");

            // 2. A type's line holds no outline (it used to take the sketch of an element of that type).
            if (new FilteredElementCollector(doc).OfClass(typeof(Floor)).FirstElement() is Floor anyFloor && doc.GetElement(anyFloor.GetTypeId()) is ElementType floorType)
            {
                if (ChangeFile.Capture(doc, floorType) is { } typeLine && (typeLine.Loops is not null || typeLine.SlopeArrow)) problems.Add("a floor type's line holds an outline");
            }
            else notes.Add("no floor in the test project (type outline not checked)");

            // 3. A material and a wall made after the save, then gone; a family kept as a copy, then gone.
            Material material;
            Wall fresh;
            using (var t = new Transaction(doc, "Field test: lifeline new things"))
            {
                t.Start();
                material = (Material)doc.GetElement(Material.Create(doc, "SPM lifeline " + DateTime.Now.ToString("HHmmss")));
                material.Color = new Color(12, 140, 200);
                material.Transparency = 35;
                fresh = Wall.Create(doc, Line.CreateBound(new XYZ(0, -130, 0), new XYZ(20, -130, 0)), type.Id, level.Id, 10, 0, false, false);
                t.Commit();
            }
            var lines = new List<M.Line>();
            foreach (var e in new Element[] { material, fresh })
            {
                if (ChangeFile.Capture(doc, e) is not { } line) { problems.Add($"{e.GetType().Name} wasn't captured"); continue; }
                line.New = true;
                lines.Add(line);
            }
            if (lines.FirstOrDefault(l => l.What == "material") is not { Extra: { } extra } || !extra.ContainsKey("color")) problems.Add("the material's colour isn't in its line");
            var (materialUnique, materialName, freshUnique) = (material.UniqueId, material.Name, fresh.UniqueId);
            // A loadable family nothing is placed of (so deleting it takes nothing else with it).
            var used = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().Select(i => i.Symbol?.Family?.Id).ToHashSet();
            var family = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                .FirstOrDefault(f => !f.IsInPlace && f.IsEditable && !used.Contains(f.Id) && f.FamilyCategory is { CategoryType: CategoryType.Model });
            var families = Path.Combine(ChangeFile.FolderFor(doc.PathName), M.FamiliesFolder);
            string? familyName = null;
            if (family is not null)
            {
                familyName = family.Name;
                if (ChangeFile.Capture(doc, family) is not { What: "family" } familyLine) problems.Add("the family wasn't captured as a family");
                else
                {
                    familyLine.New = true;
                    familyLine.File = M.FamilyFileName(family.UniqueId);
                    try
                    {
                        ChangeFile.SaveFamily(doc, family, families);
                        if (!File.Exists(Path.Combine(families, familyLine.File))) problems.Add("the family's copy isn't next to the change file");
                        else lines.Add(familyLine);
                    }
                    catch (Exception ex) { problems.Add("the family couldn't be copied next to the change file: " + ex.Message); }
                }
            }
            else notes.Add("no unused loadable family in the test project (family copy not checked)");
            using (var t = new Transaction(doc, "Field test: lifeline things gone"))
            {
                t.Start();
                doc.Delete(fresh.Id);
                doc.Delete(material.Id);
                if (family is not null && lines.Any(l => l.What == "family")) doc.Delete(family.Id);
                t.Commit();
            }
            var model = new M.Line { Kind = M.Kinds.Model, At = DateTime.Now, Path = doc.PathName, Format = M.FormatNew };
            var parsed = M.Read(lines.Prepend(model).Select(M.Write));
            var outcome = new ChangeReplay.Outcome { FamiliesFolder = families };
            ChangeReplay.Apply(doc, ChangeReplay.Plan(doc, parsed, outcome), outcome);
            made.AddRange(outcome.MadeFor.Values);
            var notDone = " (" + string.Join("; ", outcome.NotDone) + ")";
            if (!outcome.MadeFor.TryGetValue(materialUnique, out var materialId) || doc.GetElement(materialId) is not Material back || back.Name != materialName
                || back.Color.Red != 12 || back.Color.Blue != 200 || back.Transparency != 35)
                problems.Add("the material wasn't made again with its colour and transparency" + notDone);
            if (!outcome.MadeFor.TryGetValue(freshUnique, out var wallId) || doc.GetElement(wallId) is not Wall) problems.Add("the wall wasn't made again" + notDone);
            if (familyName is not null && lines.Any(l => l.What == "family")
                && !new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().Any(f => f.Name == familyName))
                problems.Add("the family wasn't loaded again from its kept copy" + notDone);

            // 4. The same file played again: what the first play made is found, nothing is made twice.
            var second = new ChangeReplay.Outcome { FamiliesFolder = families };
            ChangeReplay.Remember(doc, outcome.MadeUnique, second);
            var again = ChangeReplay.Plan(doc, parsed, second);
            if (again.Any(st => st.Act == ChangeReplay.Act.Make)) problems.Add("a second play of the file would make again: " + string.Join(", ", again.Where(st => st.Act == ChangeReplay.Act.Make).Select(st => st.Line.What)));
            return problems.Count == 0
                ? Pass($"a deletion named by UniqueId; a material, a wall{(familyName is not null ? " and a family" : "")} made again; a second play makes nothing twice" + (notes.Count > 0 ? "; " + string.Join("; ", notes) : ""))
                : Fail(string.Join("; ", problems));
        }
        finally
        {
            ChangeFile.ForgetForTest(doc);
            ChangeFile.RootForTest = null;
            ChangeFile.ForceForTest = false;
            using var t = new Transaction(doc, "Field test: lifeline tidy up");
            t.Start();
            // Walls first, the material last (once nothing uses it); a family loaded again stays (it was there before).
            foreach (var id in made.Where(id => doc.GetElement(id) is not Family).OrderBy(id => doc.GetElement(id) is Material ? 1 : 0).ToList())
                try { if (doc.GetElement(id) is not null) doc.Delete(id); } catch (Exception) { }
            t.Commit();
        }
    }

    /// <summary>
    /// Kept copies and a schedule's look: in the test project, a wall is copied into a kept-elements project in a test
    /// folder (as an in-place family or curved stairs would be), deleted, and copied back where it was by a replay; a
    /// wall schedule with a heading, a sorting row and a filter, deleted, is made again with them. The kept-elements
    /// project is closed again; what the test made is deleted.
    /// </summary>
    private static (string, string) RecoveryKept(UIApplication app, State s)
    {
        if (TestDoc(app, s, out var why) is not { } doc) return Fail(why);
        if (BasicWallType(doc) is not { } type) return Fail("the test project has no basic wall type with layers");
        if (new FilteredElementCollector(doc).OfClass(typeof(Level)).FirstElement() is not Level level) return Fail("the test project has no level");
        var folder = Path.Combine(s.Folder, "change-files-kept");
        var problems = new List<string>();
        var made = new List<ElementId>();
        ChangeReplay.Outcome? outcome = null;
        try
        {
            Directory.CreateDirectory(folder);
            Wall wall;
            ViewSchedule schedule;
            var sorted = false;
            using (var t = new Transaction(doc, "Field test: lifeline kept things"))
            {
                t.Start();
                wall = Wall.Create(doc, Line.CreateBound(new XYZ(0, -140, 0), new XYZ(20, -140, 0)), type.Id, level.Id, 10, 0, false, false);
                schedule = ViewSchedule.CreateSchedule(doc, new ElementId(BuiltInCategory.OST_Walls));
                schedule.Name = "SPM lifeline schedule " + DateTime.Now.ToString("HHmmss");
                var definition = schedule.Definition;
                if (definition.GetSchedulableFields().FirstOrDefault(f => f.ParameterId.Value == (long)BuiltInParameter.ALL_MODEL_MARK) is { } mark)
                {
                    var column = definition.AddField(mark);
                    column.ColumnHeading = "SPM mark";
                    definition.AddSortGroupField(new ScheduleSortGroupField(column.FieldId, ScheduleSortOrder.Descending));
                    definition.AddFilter(new ScheduleFilter(column.FieldId, ScheduleFilterType.NotEqual, "zz"));
                    sorted = true;
                }
                t.Commit();
            }
            var lines = new List<M.Line>();
            if (ChangeFile.Capture(doc, wall) is { } wallLine) { wallLine.New = true; (wallLine.Extra ??= [])["kept"] = "1"; lines.Add(wallLine); }
            else problems.Add("the wall wasn't captured");
            if (ChangeFile.Capture(doc, schedule) is { } scheduleLine)
            {
                scheduleLine.New = true;
                lines.Add(scheduleLine);
                if (scheduleLine.Extra is not { } x || !x.ContainsKey("look")) problems.Add("the schedule's sorting and filters aren't in its line");
            }
            else problems.Add("the schedule wasn't captured");
            var (wallUnique, scheduleUnique) = (wall.UniqueId, schedule.UniqueId);
            var kept = ChangeKeeper.Keep(doc, folder, [wall]);
            if (!kept.Contains(wallUnique) || !File.Exists(ChangeKeeper.FileOf(folder))) problems.Add("the wall wasn't copied into the kept-elements project");
            using (var t = new Transaction(doc, "Field test: lifeline kept things gone"))
            {
                t.Start();
                doc.Delete(wall.Id);
                doc.Delete(schedule.Id);
                t.Commit();
            }
            var model = new M.Line { Kind = M.Kinds.Model, At = DateTime.Now, Path = doc.PathName, Format = M.FormatNew };
            var parsed = M.Read(lines.Prepend(model).Select(M.Write));
            outcome = new ChangeReplay.Outcome { KeptFolder = folder, Kept = ChangeKeeper.ReadMap(folder) };
            ChangeReplay.Apply(doc, ChangeReplay.Plan(doc, parsed, outcome), outcome);
            made.AddRange(outcome.MadeFor.Values);
            var notDone = " (" + string.Join("; ", outcome.NotDone) + ")";
            if (!outcome.MadeFor.TryGetValue(wallUnique, out var wallId) || doc.GetElement(wallId) is not Wall back) problems.Add("the wall wasn't copied back from its kept copy" + notDone);
            else if (back.Location is not LocationCurve { Curve: { } curve } || curve.GetEndPoint(0).DistanceTo(new XYZ(0, -140, curve.GetEndPoint(0).Z)) > 0.01 && curve.GetEndPoint(1).DistanceTo(new XYZ(0, -140, curve.GetEndPoint(1).Z)) > 0.01)
                problems.Add("the wall copied back isn't where it was");
            if (!outcome.MadeFor.TryGetValue(scheduleUnique, out var scheduleId) || doc.GetElement(scheduleId) is not ViewSchedule again) problems.Add("the schedule wasn't made again" + notDone);
            else if (sorted)
            {
                var definition = again.Definition;
                if (definition.GetSortGroupFieldCount() != 1 || definition.GetFilterCount() != 1) problems.Add($"the schedule came back with {definition.GetSortGroupFieldCount()} sorting rows and {definition.GetFilterCount()} filters, not 1 and 1");
                if (!Enumerable.Range(0, definition.GetFieldCount()).Any(i => definition.GetField(i).ColumnHeading == "SPM mark")) problems.Add("the schedule's heading didn't come back");
            }
            if (outcome.NotDone.Count > 0) problems.Add("the replay listed" + notDone);
            return problems.Count == 0
                ? Pass("a wall copied into the kept-elements project and copied back where it was; a schedule made again with its heading, sorting and filter")
                : Fail(string.Join("; ", problems));
        }
        finally
        {
            ChangeKeeper.Close(outcome?.Keeper);
            if (app.Application.Documents.Cast<Document>().Any(d => { try { return ChangeKeeper.Is(d.PathName ?? ""); } catch (Exception) { return false; } }))
                LifelineBugs.Note("field test", "the kept-elements project was left open");
            using var t = new Transaction(doc, "Field test: lifeline tidy up");
            t.Start();
            foreach (var id in made)
                try { if (doc.GetElement(id) is not null) doc.Delete(id); } catch (Exception) { }
            t.Commit();
        }
    }

    /// <summary>Lifeline's bug list: a problem noted twice is one line under the header, in a test folder.</summary>
    private static (string, string) RecoveryBugs(State s)
    {
        LifelineBugs.FolderForTest = Path.Combine(s.Folder, "lifeline-bugs");
        try
        {
            LifelineBugs.ForgetForTest();
            if (File.Exists(LifelineBugs.FilePath)) File.Delete(LifelineBugs.FilePath);
            LifelineBugs.Note("field test", "a test problem", "Test model", new InvalidOperationException("test"));
            LifelineBugs.Note("field test", "a test problem", "Test model", new InvalidOperationException("test"));
            var lines = File.Exists(LifelineBugs.FilePath) ? File.ReadAllLines(LifelineBugs.FilePath) : [];
            return lines.Length == 2 && lines[0] == M.BugsHeader && lines[1].Contains("| field test | a test problem: InvalidOperationException: test")
                ? Pass("a problem noted twice is one line under the header in Lifeline bugs.txt")
                : Fail($"Lifeline bugs.txt has {lines.Length} lines: {string.Join(" / ", lines)}");
        }
        finally
        {
            LifelineBugs.FolderForTest = null;
            LifelineBugs.ForgetForTest();
        }
    }

    /// <summary>
    /// What a change file keeps besides values and shapes: in the test project, two joined walls, a line style, a 3D
    /// view with a hidden category, a hidden wall, a wall drawn halftone and a filter switched off, a spline drawn in
    /// a drafting view, and a group of a new type are captured; then the walls are unjoined, the style, the filter,
    /// the spline and the group with its type deleted and the view's graphics cleared; a replay puts all of it back.
    /// What the test made is deleted.
    /// </summary>
    private static (string, string) RecoveryExtras(UIApplication app, State s)
    {
        if (TestDoc(app, s, out var why) is not { } doc) return Fail(why);
        if (BasicWallType(doc) is not { } type) return Fail("the test project has no basic wall type with layers");
        if (new FilteredElementCollector(doc).OfClass(typeof(Level)).FirstElement() is not Level level) return Fail("the test project has no level");
        var view3 = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().FirstOrDefault(t => t.ViewFamily == ViewFamily.ThreeDimensional);
        var drafting = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().FirstOrDefault(t => t.ViewFamily == ViewFamily.Drafting);
        if (view3 is null || drafting is null) return Fail("the test project has no 3D or drafting view type");
        var folder = Path.Combine(s.Folder, "change-files-extras");
        var problems = new List<string>();
        var made = new List<ElementId>();
        ChangeReplay.Outcome? outcome = null;
        var stamp = DateTime.Now.ToString("HHmmss");
        var (styleName, filterName, groupName) = ("SPM lifeline style " + stamp, "SPM lifeline filter " + stamp, "SPM lifeline group " + stamp);
        var lines = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
        var wallsId = new ElementId(BuiltInCategory.OST_Walls);
        try
        {
            Directory.CreateDirectory(folder);
            Wall a, b, c, d;
            View3D view;
            ViewDrafting sheet;
            ParameterFilterElement filter;
            CurveElement spline;
            Group group;
            GraphicsStyle style;
            using (var t = new Transaction(doc, "Field test: lifeline extras"))
            {
                t.Start();
                a = Wall.Create(doc, Line.CreateBound(new XYZ(0, -180, 0), new XYZ(20, -180, 0)), type.Id, level.Id, 10, 0, false, false);
                b = Wall.Create(doc, Line.CreateBound(new XYZ(10, -190, 0), new XYZ(10, -170, 0)), type.Id, level.Id, 10, 0, false, false);
                c = Wall.Create(doc, Line.CreateBound(new XYZ(0, -220, 0), new XYZ(20, -220, 0)), type.Id, level.Id, 10, 0, false, false);
                d = Wall.Create(doc, Line.CreateBound(new XYZ(0, -230, 0), new XYZ(20, -230, 0)), type.Id, level.Id, 10, 0, false, false);
                doc.Regenerate();
                try { if (!JoinGeometryUtils.AreElementsJoined(doc, a, b)) JoinGeometryUtils.JoinGeometry(doc, a, b); } catch (Exception ex) { problems.Add("the two walls couldn't be joined: " + ex.Message); }
                var sub = doc.Settings.Categories.NewSubcategory(lines, styleName);
                sub.SetLineWeight(5, GraphicsStyleType.Projection);
                sub.LineColor = new Color(200, 40, 40);
                doc.Regenerate();
                style = sub.GetGraphicsStyle(GraphicsStyleType.Projection);
                filter = ParameterFilterElement.Create(doc, filterName, [wallsId]);
                view = View3D.CreateIsometric(doc, view3.Id);
                view.Name = "SPM lifeline 3D " + stamp;
                view.SetCategoryHidden(new ElementId(BuiltInCategory.OST_Floors), true);
                view.AddFilter(filter.Id);
                view.SetFilterVisibility(filter.Id, false);
                var half = new OverrideGraphicSettings();
                half.SetHalftone(true);
                view.SetElementOverrides(a.Id, half);
                view.HideElements([b.Id]);
                sheet = ViewDrafting.Create(doc, drafting.Id);
                sheet.Name = "SPM lifeline drafting " + stamp;
                // Revit gives nothing back for a spline through points drawn in a view: it is the one line in the view.
                doc.Create.NewDetailCurve(sheet, HermiteSpline.Create([new XYZ(0, 0, 0), new XYZ(2, 1, 0), new XYZ(4, -1, 0), new XYZ(6, 0, 0)], false));
                doc.Regenerate();
                spline = new FilteredElementCollector(doc, sheet.Id).OfClass(typeof(CurveElement)).Cast<CurveElement>().First();
                group = doc.Create.NewGroup([c.Id, d.Id]);
                group.GroupType.Name = groupName;
                t.Commit();
            }
            made.AddRange([a.Id, b.Id, c.Id, d.Id, view.Id, sheet.Id, filter.Id, group.Id, group.GroupType.Id]);
            var written = new List<M.Line>();
            M.Line? Take(Element e, bool isNew = true)
            {
                if (ChangeFile.Capture(doc, e) is not { } line) { problems.Add($"{e.GetType().Name} wasn't captured"); return null; }
                line.New = isNew;
                written.Add(line);
                return line;
            }
            var (la, lb) = (Take(a), Take(b));
            if (la is not null && M.Relations(la, "joins").Count != 1) problems.Add("the wall's join isn't in its line");
            if (Take(style) is not { What: "style" } styleLine || styleLine.Extra?["weight"] != "5") problems.Add("the line style's weight isn't in its line");
            if (Take(filter) is { } filterLine) (filterLine.Extra ??= [])["kept"] = "1";
            if (Take(view) is { } viewLine)
            {
                if (ChangeExtras.ReadGraphics(doc, view, [a.Id, b.Id]) is { } graphics) (viewLine.Extra ??= [])["vg"] = graphics;
                else problems.Add("the view's graphics couldn't be read");
            }
            Take(sheet);
            if (Take(spline) is { } splineLine && (splineLine.Approx || splineLine.Loops?.SelectMany(l => l).Any(g => g.C is not null) != true)) problems.Add("the spline isn't in its line as a spline");
            Take(group.GroupType);
            Take(c);
            Take(d);
            if (Take(group) is { } groupLine && M.Members(groupLine).Count != 2) problems.Add("the group's members aren't in its line");
            var uniques = new { A = a.UniqueId, B = b.UniqueId, Style = style.UniqueId, Filter = filter.UniqueId, View = view.UniqueId, Spline = spline.UniqueId, Group = group.UniqueId };
            var kept = ChangeKeeper.Keep(doc, folder, [filter]);
            if (!kept.Contains(uniques.Filter)) problems.Add("the view filter wasn't copied into the kept-elements project");
            using (var t = new Transaction(doc, "Field test: lifeline extras undone"))
            {
                t.Start();
                try { JoinGeometryUtils.UnjoinGeometry(doc, a, b); } catch (Exception) { }
                view.SetCategoryHidden(new ElementId(BuiltInCategory.OST_Floors), false);
                view.SetElementOverrides(a.Id, new OverrideGraphicSettings());
                view.UnhideElements([b.Id]);
                doc.Delete(filter.Id);
                doc.Delete(style.GraphicsStyleCategory.Id);
                doc.Delete(spline.Id);
                // Ids first: the group's walls go with it, and a deleted element can't be asked for its id.
                var (typeId, memberIds) = (group.GroupType.Id, new[] { c.Id, d.Id });
                doc.Delete(group.Id);
                foreach (var id in memberIds) if (doc.GetElement(id) is not null) doc.Delete(id);
                doc.Delete(typeId);
                t.Commit();
            }
            var model = new M.Line { Kind = M.Kinds.Model, At = DateTime.Now, Path = doc.PathName, Format = M.FormatNew };
            var parsed = M.Read(written.Prepend(model).Select(M.Write));
            outcome = new ChangeReplay.Outcome { KeptFolder = folder, Kept = ChangeKeeper.ReadMap(folder) };
            ChangeReplay.Apply(doc, ChangeReplay.Plan(doc, parsed, outcome), outcome);
            made.AddRange(outcome.MadeFor.Values);
            var notDone = " (" + string.Join("; ", outcome.NotDone) + ")";
            if (!JoinGeometryUtils.AreElementsJoined(doc, a, b)) problems.Add("the walls weren't joined again" + notDone);
            // Asked afresh: a category in hand from before doesn't list a subcategory made since.
            var subAgain = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines).SubCategories.Cast<Category>().FirstOrDefault(k => k.Name == styleName);
            if (subAgain is null) problems.Add("the line style wasn't made again" + notDone);
            else
            {
                made.Add(subAgain.Id);
                if (subAgain.GetLineWeight(GraphicsStyleType.Projection) != 5 || subAgain.LineColor.Red != 200) problems.Add("the line style came back without its weight or colour");
            }
            var filterAgain = new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement)).FirstOrDefault(f => f.Name == filterName);
            if (filterAgain is null) problems.Add("the view filter wasn't copied back" + notDone);
            else
            {
                made.Add(filterAgain.Id);
                if (!view.IsFilterApplied(filterAgain.Id) || view.GetFilterVisibility(filterAgain.Id)) problems.Add("the view's filter isn't on the view switched off again");
            }
            if (!view.GetCategoryHidden(new ElementId(BuiltInCategory.OST_Floors))) problems.Add("the view's hidden category isn't hidden again");
            if (!view.GetElementOverrides(a.Id).Halftone) problems.Add("the wall isn't drawn halftone in the view again");
            if (!b.IsHidden(view)) problems.Add("the wall hidden in the view isn't hidden again");
            if (!outcome.MadeFor.TryGetValue(uniques.Spline, out var splineId) || doc.GetElement(splineId) is not CurveElement { GeometryCurve: HermiteSpline }) problems.Add("the spline didn't come back as a spline" + notDone);
            if (!outcome.MadeFor.TryGetValue(uniques.Group, out var groupId) || doc.GetElement(groupId) is not Group again) problems.Add("the group of a new type wasn't put together again" + notDone);
            else
            {
                made.Add(again.GroupType.Id);
                if (again.GetMemberIds().Count != 2 || again.GroupType.Name != groupName) problems.Add($"the group came back with {again.GetMemberIds().Count} members as {again.GroupType.Name}");
            }
            if (outcome.NotDone.Count > 0) problems.Add("the replay listed" + notDone);
            return problems.Count == 0
                ? Pass("joined walls, a line style, a view's hidden category, hidden and halftone walls and switched-off filter, a spline and a group of a new type, undone, all came back in a replay")
                : Fail(string.Join("; ", problems));
        }
        finally
        {
            ChangeKeeper.Close(outcome?.Keeper);
            using var t = new Transaction(doc, "Field test: lifeline tidy up");
            t.Start();
            // Groups first (their members go with them), types and the style last.
            foreach (var id in made.Distinct().OrderBy(id => doc.GetElement(id) switch { Group => 0, ElementType => 2, null => 3, _ => 1 }).ToList())
                try { doc.Delete(id); } catch (Exception) { /* gone with its group or view already */ }
            t.Commit();
        }
    }
}
