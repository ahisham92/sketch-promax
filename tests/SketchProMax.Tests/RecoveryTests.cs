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
        // Lifeline's bug list: one line per problem, readable in Notepad, trimmed to the newest.
        var bug = M.BugLine(at, "0.35.13", "2025", "Port_local", "capture", "floor (Floors) couldn't be read\r\nInvalidOperationException: bad");
        check("lifeline bugs: a problem is one line with when, version, Revit, model and where", bug == "2026-10-25 17:40 | 0.35.13 | Revit 2025 | Port_local | capture | floor (Floors) couldn't be read InvalidOperationException: bad", bug);
        var many = Enumerable.Range(0, 2500).Select(i => "line " + i).Prepend(M.BugsHeader).ToList();
        var kept = M.TrimBugs(many, M.MaxBugLines);
        check("lifeline bugs: the file keeps its header and the newest lines", kept.Count == M.MaxBugLines + 1 && kept[0] == M.BugsHeader && kept[^1] == "line 2499" && kept[1] == "line 500", kept.Count.ToString());
        check("lifeline bugs: one example per reason a replay couldn't do", M.Reason("17:42 Walls: Basic Wall: 200 (added or changed, Move): its host isn't in this model") == "its host isn't in this model", "");
        check("lifeline bugs: in Documents, listed in Help > Open folders", SketchProMax.Bugs.UserFolders.List().Any(f => f.Title == "Lifeline bugs" && f.Path.EndsWith(Path.Combine("Sketch ProMax", M.BugsFolderName))), "");
        check("lifeline bugs: capture, write, start and replay problems are noted", Src("Revit", "ChangeFile.cs").Contains("LifelineBugs.Note(\"capture\"") && Src("Revit", "ChangeFile.cs").Contains("LifelineBugs.Note(\"write\"")
            && Src("App.cs").Contains("LifelineBugs.Note(\"start\"") && Src("UI", "RecoverWindow.cs").Contains("LifelineBugs.Note(\"replay error\""), "");
        // Central models: what others deleted stays deleted; a new local keeps last time's file; views, groups, links, CAD, leaders.
        var model2 = M.Write(new M.Line { Kind = M.Kinds.Model, At = at, Path = @"C:\Work\Port_local.rvt", Central = @"\\server\Port.rvt", Format = M.FormatNew });
        var made = new M.Line { Kind = M.Kinds.State, At = at, Unique = "n1", What = "detailcurve", New = true };
        var madeLater = new M.Line { Kind = M.Kinds.State, At = at.AddMinutes(1), Unique = "n1", What = "detailcurve" };
        var views = new M.Line { Kind = M.Kinds.Views, At = at.AddMinutes(2), Open = ["v1", "v2"], View = "v2" };
        var marked = M.Read([model2, M.Write(made), M.Write(madeLater), M.Write(views)]);
        check("lifeline: a file of this format marks what was made in the session", marked.MarksNew && !M.Read(lines).MarksNew, "");
        check("lifeline: made in the session stays marked on later lines", marked.Elements.Single().New, M.Write(madeLater));
        check("lifeline: the views that were open read back, with the active one", marked.Open.SequenceEqual(["v1", "v2"]) && marked.Active == "v2" && marked.Elements.Count == 1, string.Join(",", marked.Open));
        check("lifeline: open views alone are something to bring back", M.Read([model2, M.Write(views)]) is { Any: true, Elements.Count: 0 }, "");
        check("lifeline: a replayed file isn't offered again", M.LeftoverTime(M.ReplayedName(at)) is null && M.ReplayedName(at).StartsWith(M.ReplayedPrefix), M.ReplayedName(at));
        var sheet = new M.Line { Kind = M.Kinds.State, At = at, Unique = "s1", What = "sheet", New = true };
        var titleBlock = new M.Line { Kind = M.Kinds.State, At = at, Unique = "tb1", What = "instance", View = "s1", Type = "A1: A1", P = new(0, 0, 0), New = true };
        var viewport = new M.Line { Kind = M.Kinds.State, At = at, Unique = "vp1", What = "viewport", Host = "v1", View = "s1", P = new(1, 1, 0), New = true };
        var plan = new M.Line { Kind = M.Kinds.State, At = at.AddMinutes(1), Unique = "v1", What = "view", ViewKind = "FloorPlan", LevelUnique = "l1", Box = new M.Box { Origin = new(0, 0, 0), Min = new(-5, -5, 0), Max = new(5, 5, 0) }, New = true };
        var sheetOrder = M.PlayOrder(M.Read([model2, M.Write(viewport), M.Write(titleBlock), M.Write(sheet), M.Write(plan)])).Select(l => l.Unique).ToList();
        check("lifeline: sheets and views are made before their title blocks and viewports", sheetOrder.IndexOf("s1") < sheetOrder.IndexOf("tb1") && sheetOrder.IndexOf("v1") < sheetOrder.IndexOf("vp1"), string.Join(",", sheetOrder));
        var planBack = M.Read([M.Write(plan)]).Elements.Single();
        check("lifeline: a view's kind and crop read back", planBack is { ViewKind: "FloorPlan", Box.Min.X: -5, Box.Max.Y: 5, New: true }, M.Write(plan));
        var schedule = new M.Line { Kind = M.Kinds.State, At = at, Unique = "sc1", What = "view", ViewKind = "Schedule", ScheduleCategory = -2001330, Fields = [-1002050, -1010106] };
        check("lifeline: a schedule's category and columns read back", M.Read([M.Write(schedule)]).Elements.Single() is { ScheduleCategory: -2001330, Fields.Count: 2 }, M.Write(schedule));
        var cad = new M.Line { Kind = M.Kinds.State, At = at, Unique = "c1", What = "cad", Text = "linked", Name = @"C:\CAD\site.dwg", Box = new M.Box { Origin = new(10, 20, 0), BasisX = new(0, 1, 0) } };
        check("lifeline: a CAD link's file and placement read back", M.Read([M.Write(cad)]).Elements.Single() is { Text: "linked", Name: @"C:\CAD\site.dwg", Box.BasisX.Y: 1, Box.Origin.X: 10 }, M.Write(cad));
        var member = new M.Line { Kind = M.Kinds.State, At = at, Unique = "m1", What = "wall", Group = "g1" };
        check("lifeline: a group member names its group", M.Read([M.Write(member)]).Elements.Single().Group == "g1", "");
        var text = new M.Line { Kind = M.Kinds.State, At = at, Unique = "tx1", What = "text", Text = "NOTE", Leader = true,
            Leads = [new M.Lead { End = new(1, 2, 0), Elbow = new(0.5, 1, 0), Right = true }, new M.Lead { End = new(-1, -1, 0), Arc = true }] };
        var textBack = M.Read([M.Write(text)]).Elements.Single();
        check("lifeline: a text note's leaders read back", M.SameLeads(textBack.Leads, text.Leads) && textBack.Leads![0].Right && textBack.Leads[1].Arc && textBack.Leads[1].Elbow is null, M.Write(text));
        check("lifeline: a moved leader is a change", !M.SameLeads(text.Leads, [new M.Lead { End = new(1, 2.1, 0), Elbow = new(0.5, 1, 0) }, text.Leads![1]]) && !M.SameLeads(text.Leads, []), "");
        check("lifeline: what others deleted in central is skipped, not made again", Src("Revit", "ChangeReplay.cs").Contains("file.MarksNew && !line.New") && Src("Revit", "ChangeReplay.cs").Contains("deleted from the central model by someone else"), "");
        check("lifeline: a group made again brings its members (their lines aren't played)", Src("Revit", "ChangeReplay.cs").Contains("groupsMade.Contains(group)"), "");
        check("lifeline: a new local saved as it opens keeps last time's file", Src("Revit", "ChangeFile.cs").Contains("e.OriginalPath") && Src("Revit", "ChangeFile.cs").Contains("if (!watched) { Track(doc, opened: true); return; }"), "");
        check("lifeline: other locals of the same central are found", Src("Revit", "ChangeFile.cs").Contains("head.Central, model.Central") && Src("UI", "RecoverWindow.cs").Contains("ChangeFile.LeftoversFor(target.Path, target.Central)"), "");
        check("lifeline: replay makes views, sheets, viewports, groups, links and CAD", new[] { "ViewPlan.Create", "ViewSection.CreateSection", "View3D.CreateIsometric", "ViewDrafting.Create", "ViewSchedule.CreateSchedule", "ViewSheet.Create", "Viewport.Create", "ScheduleSheetInstance.Create", "PlaceGroup", "RevitLinkInstance.Create", "doc.Link(", "doc.Import(" }.All(Src("Revit", "ChangeViews.cs").Contains), "");
        check("lifeline: text leaders come back", Src("Revit", "ChangeNotes.cs").Contains("text.AddLeader(") && Src("Revit", "ChangeNotes.cs").Contains("text.RemoveLeaders()"), "");
        check("lifeline: what wasn't done has Show, and the views that were open open again", Src("UI", "RecoverWindow.cs").Contains("Brand.Button(\"Show\")") && Src("UI", "RecoverWindow.cs").Contains("ShowElements") && Src("UI", "RecoverWindow.cs").Contains("ZoomAndCenterRectangle") && Src("UI", "RecoverWindow.cs").Contains("OpenViews(parsed"), "");
        check("lifeline: work plane grids and view marks get no line (they flooded a real file)", Src("Revit", "ChangeFile.cs").Contains("BuiltInCategory.OST_IOSSketchGrid") && Src("Revit", "ChangeFile.cs").Contains("BuiltInCategory.OST_Viewers"), "");
        var wallType = new M.Line { Kind = M.Kinds.State, At = at, Unique = "wt1", What = "type", Name = "Wall 300", Class = "WallType", Type = "Basic Wall: Wall 300",
            Layers = [new M.Layer { Width = 0.0656, Function = 4, Material = "mat1" }, new M.Layer { Width = 0.9186, Function = 1 }], Shells = [1, 0], New = true };
        var typeBack = M.Read([M.Write(wallType)]).Elements.Single();
        check("lifeline: a new type's kind and layers read back", typeBack is { Class: "WallType", Shells: [1, 0] } && M.SameLayers(typeBack.Layers, wallType.Layers), M.Write(wallType));
        check("lifeline: a thicker layer is a change", !M.SameLayers(wallType.Layers, [new M.Layer { Width = 0.07, Function = 4, Material = "mat1" }, wallType.Layers![1]]), "");
        List<M.Run> Runs(double dx) => [new M.Run { P = new(dx, 0, 0), Q = new(dx + 10, 0, 0), Width = 4 }, new M.Run { P = new(dx + 12, 2, 5), Q = new(dx + 2, 2, 5), Width = 4, Base = 5 }];
        check("lifeline: stairs that only moved are moved back", M.RunsShift(Runs(0), Runs(3)) is { X: 3, Y: 0 }, "");
        check("lifeline: stairs whose runs changed aren't moved", M.RunsShift(Runs(0), [Runs(3)[0], new M.Run { P = new(15, 2, 5), Q = new(8, 2, 5), Width = 4 }]) is null, "");
        var stairsLine = new M.Line { Kind = M.Kinds.State, At = at, Unique = "st1", What = "stairs", Runs = Runs(0), LevelUnique = "l1", TopLevel = "l2" };
        check("lifeline: stairs' runs read back", M.Read([M.Write(stairsLine)]).Elements.Single().Runs is [{ Width: 4 }, { Base: 5 }], M.Write(stairsLine));
        check("lifeline: replay makes types, elevations, legends, legend components and stairs", Src("Revit", "ChangeTypes.cs").Contains(".Duplicate(name)") && Src("Revit", "ChangeTypes.cs").Contains("SetCompoundStructure")
            && Src("Revit", "ChangeViews.cs").Contains("ElevationMarker.CreateElevationMarker") && Src("Revit", "ChangeViews.cs").Contains("ViewDuplicateOption.Duplicate")
            && Src("Revit", "ChangeViews.cs").Contains("OST_LegendComponents") && Src("Revit", "ChangeStairs.cs").Contains("StairsRun.CreateStraightRun"), "");
        check("lifeline: stairs made again bring their railings (the railings' lines aren't played)", Src("Revit", "ChangeReplay.cs").Contains("stairsMade.Contains(onStairs)"), "");
        check("lifeline: the window shows only the open model's unsaved file", !Src("UI", "RecoverWindow.cs").Contains("Changes before the last save") && !Src("UI", "RecoverWindow.cs").Contains("Changes since the last save"), "");
        check("recovery: Help > Open folders lists the change files", SketchProMax.Bugs.UserFolders.List().Any(f => f.Title == "Change files" && f.Path.EndsWith(Path.Combine("Sketch ProMax", "Change files"))), "");
    }
}
