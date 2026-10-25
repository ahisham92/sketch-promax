using SketchProMax.Revit;
using M = SketchProMax.Revit.ChangeFileMath;

/// <summary>The change file (Revit/ChangeFileMath): reading back, merging later lines, play order, small lines, and the wiring.</summary>
internal static class RecoveryTests
{
    public static void Run(string repoRoot, Action<string, bool, string> check)
    {
        var at = new DateTime(2026, 10, 25, 17, 40, 0);
        M.Value Comment(string text) => new() { Bip = -1010106, Kind = "s", Text = text };
        M.Value Mark(string text) => new() { Bip = -1001203, Kind = "s", Text = text };
        var wall = new M.Line { Kind = M.Kinds.State, At = at, Unique = "w1", Id = 10, What = "wall", Category = "Walls", Type = "Basic Wall: 200", P = new(0, 0, 0), Q = new(10, 0, 0), Values = [Comment("a"), Mark("M1")] };
        var moved = new M.Line { Kind = M.Kinds.State, At = at.AddMinutes(1), Unique = "w1", Id = 10, What = "wall", Category = "Walls", Type = "Basic Wall: 200", P = new(0, 5, 0), Q = new(10, 5, 0), Values = [Comment("b")] };
        var door = new M.Line { Kind = M.Kinds.State, At = at.AddMinutes(2), Unique = "d1", What = "instance", Host = "w1", Type = "Door: 900", P = new(5, 5, 0) };
        var level = new M.Line { Kind = M.Kinds.State, At = at.AddMinutes(3), Unique = "l1", What = "level", Elevation = 12 };
        var gone = new M.Line { Kind = M.Kinds.Deleted, At = at.AddMinutes(4), Unique = "x1", Id = 99 };
        var lines = new List<string> { M.HeaderComment, M.Write(new M.Line { Kind = M.Kinds.Model, At = at, Path = @"C:\Work\Port_local.rvt", Central = @"\\server\Port.rvt" }) };
        lines.AddRange(new[] { wall, door, moved, gone, level }.Select(M.Write));
        lines.Add("{\"k\":\"state\",\"t\":\"2026-10-25T17:4"); // the line being written when Revit closed
        var parsed = M.Read(lines);

        check("recovery: the file says its model and central", parsed.Model == @"C:\Work\Port_local.rvt" && parsed.Central == @"\\server\Port.rvt", parsed.Model ?? "");
        check("recovery: one entry per element, broken and comment lines skipped", parsed.Elements.Count == 4, parsed.Elements.Count.ToString());
        var w = parsed.Elements.First(e => e.Unique == "w1");
        check("recovery: an element's latest place wins", w.P is { Y: 5 } && w.Q is { Y: 5 }, "");
        check("recovery: a later line's values merge with the earlier ones", w.Values?.Count == 2 && w.Values.Any(v => v.Text == "b") && w.Values.Any(v => v.Text == "M1"),
            string.Join(",", w.Values?.Select(v => v.Text) ?? []));
        var order = M.PlayOrder(parsed).Select(l => l.Unique).ToList();
        check("recovery: levels first, a host before what it hosts, deletions last", order.SequenceEqual(["l1", "w1", "d1", "x1"]), string.Join(",", order));
        check("recovery: counts for the preview", M.Count(parsed) == (3, 1), M.Count(parsed).ToString());

        // Small lines: only what changed after an element's first line; empty text left out on the first.
        var first = M.Changed([Comment(""), Mark("M1"), new M.Value { Bip = -1, Kind = "d", Number = 3 }], null);
        check("recovery: a first line leaves out empty text", first.Count == 2 && first.All(v => v.Text != ""), first.Count.ToString());
        var written = new[] { Comment("a"), Mark("M1") }.ToDictionary(M.KeyOf, M.Print);
        var later = M.Changed([Comment("b"), Mark("M1")], written);
        check("recovery: a later line holds only the changed values", later.Count == 1 && later[0].Text == "b", later.Count.ToString());
        var lineSize = M.Write(new M.Line { Kind = M.Kinds.State, At = at, Unique = "6a1f2b3c-0000-4000-8000-000000000001-0004d2a1", Id = 315041, What = "instance", Category = "Structural Columns",
            Type = "Concrete-Rectangular-Column: 600 x 600mm", P = new(M.Round(12.3456789123), M.Round(-4.98765432), 0), Rotation = 0, Values = later }).Length;
        check("recovery: a moved element's line is a few hundred bytes", lineSize < 500, lineSize + " bytes");
        check("recovery: numbers are rounded short", M.Round(1.0 / 3) == 0.3333333, M.Round(1.0 / 3).ToString());

        string Src(params string[] parts) => File.ReadAllText(Path.Combine([repoRoot, "src", "SketchProMax", .. parts]));
        // Sketches: a floor's outline is kept as loops of edges; drawn in another order or direction it's the same outline.
        List<M.Seg> Rect(double x1, double y1) => [new(new(0, 0, 0), new(x1, 0, 0)), new(new(x1, 0, 0), new(x1, y1, 0)), new(new(x1, y1, 0), new(0, y1, 0)), new(new(0, y1, 0), new(0, 0, 0))];
        var outline = Rect(20, 10);
        var turned = Enumerable.Reverse(outline).Select(g => new M.Seg(g.Q, g.P)).ToList();
        check("recovery: an outline drawn in another order or direction is the same", M.SameLoops([outline], [turned]), "");
        check("recovery: a bigger outline is a change", !M.SameLoops([outline], [Rect(20, 12)]), "");
        check("recovery: a slope on a roof edge is a change", !M.SameLoops([outline], [[outline[0] with { Slope = 0.5 }, .. outline.Skip(1)]]), "");
        check("recovery: shape points compare by place", M.SamePoints([new(1, 2, 3)], [new(1, 2, 3)]) && !M.SamePoints([new(1, 2, 3)], [new(1, 2, 4)]), "");
        var floor = new M.Line { Kind = M.Kinds.State, At = at, Unique = "f1", What = "floor", Category = "Floors", Type = "Floor: 250", Level = "Level 1", Loops = [outline] };
        var floorText = M.Write(floor);
        var floorBack = M.Read([floorText]).Elements.Single();
        check("recovery: a floor's outline reads back", M.SameLoops(floorBack.Loops, [outline]) && floorBack.What == "floor", floorText);
        check("recovery: a four-edge floor's line stays small", floorText.Length < 600, floorText.Length + " bytes");
        check("recovery: replay can redraw sketches", Src("Revit", "ChangeShapes.cs").Contains("SketchEditScope") && Src("Revit", "ChangeShapes.cs").Contains("Floor.Create"), "");

        // Annotations and reinforcement: references follow elements made again; tags and dimensions play after what they point at.
        const string oldWall = "6a1f2b3c-0000-4000-8000-000000000001-0004d2a1", newWall = "9b2e4d5f-0000-4000-8000-000000000002-0004d2b7";
        check("recovery: a dimension's reference follows its wall made again", M.Remap(oldWall + ":0:SURFACE", new Dictionary<string, string> { [oldWall] = newWall }) == newWall + ":0:SURFACE", "");
        check("recovery: a reference to an element not made again stays", M.Remap("abc:0:SURFACE", new Dictionary<string, string> { [oldWall] = newWall }) == "abc:0:SURFACE", "");
        var tag = new M.Line { Kind = M.Kinds.State, At = at, Unique = "t1", What = "tag", Refs = ["w2:0"] };
        var wall2 = new M.Line { Kind = M.Kinds.State, At = at.AddMinutes(1), Unique = "w2", What = "instance", Type = "Beam: 300x600", P = new(0, 0, 0), Q = new(5, 0, 0) };
        var noteOrder = M.PlayOrder(M.Read([M.Write(tag), M.Write(wall2)])).Select(l => l.Unique).ToList();
        check("recovery: a tag plays after what it tags", noteOrder.SequenceEqual(["w2", "t1"]), string.Join(",", noteOrder));
        var bars = new M.Line { Kind = M.Kinds.State, At = at, Unique = "r1", What = "rebar", Host = "w2", Type = "Rebar Bar: 16M", PlaneNormal = new(0, 1, 0), Loops = [[new(new(0, 0, 0), new(5, 0, 0))]],
            Bar = new M.Bar { Rule = "MaximumSpacing", N = 12, Spacing = 0.65617, Length = 7.2, HookStart = "h1", OrientStart = -1 } };
        var barsBack = M.Read([M.Write(bars)]).Elements.Single();
        check("recovery: a rebar set's layout and hooks read back", barsBack.Bar is { Rule: "MaximumSpacing", N: 12, OrientStart: -1, HookStart: "h1" } b && M.Same(b.Spacing, 0.65617) && barsBack.Host == "w2", M.Write(bars));
        check("recovery: a rebar set's line stays small", M.Write(bars).Length < 600, M.Write(bars).Length + " bytes");
        check("recovery: replay makes annotations and rebar", Src("Revit", "ChangeNotes.cs").Contains("TextNote.Create") && Src("Revit", "ChangeNotes.cs").Contains("IndependentTag.Create")
            && Src("Revit", "ChangeNotes.cs").Contains("NewDimension") && Src("Revit", "ChangeNotes.cs").Contains("Rebar.CreateFromCurves") && Src("Revit", "ChangeReplay.cs").Contains("NewFamilyInstance(X(line.P!), symbol, view)"), "");

        // Files and names.
        check("recovery: folder names keep same-named models apart", M.FolderName(@"C:\A\Port.rvt") != M.FolderName(@"C:\B\Port.rvt") && M.FolderName(@"C:\A\Port.rvt").StartsWith("Port ("), M.FolderName(@"C:\A\Port.rvt"));
        var leftovers = Enumerable.Range(0, 8).Select(i => M.LeftoverName(at.AddHours(i))).Append(M.CurrentName).Append("notes.txt").ToList();
        var delete = M.LeftoversToDelete(leftovers);
        check("recovery: the newest five leftovers are kept, other files never touched", delete.Count == 3 && delete.All(f => f.StartsWith(M.LeftoverPrefix)) && !delete.Contains(M.LeftoverName(at.AddHours(7))), string.Join(",", delete));
        check("recovery: a leftover's time reads back", M.LeftoverTime(M.LeftoverName(at)) == at, "");
        check("recovery: one millimetre is a change, a hundredth of one isn't", !M.Same(0, 0.00328) && M.Same(0, 0.00001), "");

        // Wiring: started with the core, in the ribbon and pane, the setting on the Recovery tab, the folder listed.
        check("recovery: the change file starts and stops with the core", Src("App.cs").Contains("ChangeFile.Start(app)") && Src("App.cs").Contains("ChangeFile.Stop()"), "");
        check("recovery: Recover is on the pane", SketchProMax.UI.CoreRibbonManifest.Text.Split('\n').Count(l => l.StartsWith("pane\t") && l.Contains("\trecover\t")) == 1
            && Src("CoreEntry.cs").Contains("\"recover\" => RecoverWindow.Open()"), "");
        check("recovery: the window warns to check every change after a replay", Src("UI", "RecoverWindow.cs").Contains("check in the model that every change"), "");
        check("recovery: the change file never saves the model", !Src("Revit", "ChangeFile.cs").Contains(".Save(") && !Src("Revit", "ChangeFile.cs").Contains("SynchronizeWithCentral"), "");
        check("lifeline: named Lifeline on the pane and in Settings", SketchProMax.UI.CoreRibbonManifest.Text.Contains("\tLifeline\t") && Src("UI", "SettingsLayout.cs").Contains("\"Lifeline\""), "");
        check("lifeline: can go in a shared pack for colleagues", SketchProMax.Sharing.SharedPackMath.Shareable("recover"), "");
        check("lifeline: works without a license", Src("CoreEntry.cs").Contains("\"export-chats-bugs\", \"recover\","), "");
        var app = Src("App.cs");
        var startAt = app.IndexOf("ChangeFile.Start(app)", StringComparison.Ordinal);
        check("lifeline: the change file starts with TritoRevit alone too (outside the Sketch ProMax-only block)", startAt > app.IndexOf("Licensing.LicenseState.Allowed", StringComparison.Ordinal), "");
        check("recovery: Help > Open folders lists the change files", SketchProMax.Bugs.UserFolders.List().Any(f => f.Title == "Change files" && f.Path.EndsWith(Path.Combine("Sketch ProMax", "Change files"))), "");
    }
}
