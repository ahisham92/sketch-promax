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
}
