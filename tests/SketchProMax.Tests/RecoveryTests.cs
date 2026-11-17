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
        check("recovery: the window warns to check every change after a replay", M.ReplaySummary(3, 0, null).Contains("Check the model before you save") && M.ReplaySummary(3, 2, null).Contains("Check the model before you save"), "");
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
        check("lifeline: a missing element is always made again, never skipped as deleted by someone else", !Src("Revit", "ChangeReplay.cs").Contains("file.MarksNew") && !Src("Revit", "ChangeReplay.cs").Contains("by someone else"), "");
        check("lifeline: a replayed file's time is read back", M.ReplayedTime(M.ReplayedName(new DateTime(2026, 10, 27, 9, 30, 5))) == new DateTime(2026, 10, 27, 9, 30, 5) && M.ReplayedTime(M.CurrentName) is null, "");
        check("lifeline: replayed files stay in the window and are never renamed again", Src("Revit", "ChangeFile.cs").Contains("M.ReplayedTime(file) is null && !string.Equals") && Src("Revit", "ChangeFile.cs").Contains("|| M.ReplayedTime(file) is not null) return;"), "");
        check("lifeline: replay chunks stay short (start small, halve when slow, grow when quick, capped)",
            M.ReplayChunk <= 10 && M.NextChunk(5, TimeSpan.FromSeconds(3)) == 2 && M.NextChunk(1, TimeSpan.FromSeconds(9)) == 1
            && M.NextChunk(5, TimeSpan.FromMilliseconds(50)) == 10 && M.NextChunk(M.ReplayChunkMax, TimeSpan.Zero) == M.ReplayChunkMax
            && M.NextChunk(8, TimeSpan.FromMilliseconds(400)) == 8 && Src("UI", "RecoverWindow.cs").Contains("size = M.NextChunk(chunk.Count, clock.Elapsed)"), "");
        check("lifeline: a deletion named by id only is never played, only listed with Show", Src("Revit", "ChangeReplay.cs").Contains("line.Kind == M.Kinds.Deleted && found is not null && line.Unique is not { Length: > 0 }")
            && Src("UI", "RecoverWindow.cs").Contains("line.Id != 0 ? doc.GetElement(new ElementId(line.Id))"), "");
        check("lifeline: a deletion of what is already gone counts as already right, not as not done", Src("Revit", "ChangeReplay.cs").Contains("found is null ? new(line, Act.Done, \"\")")
            && Src("Revit", "ChangeReplay.cs").Contains("outcome.Same += steps.Count(s => s.Act == Act.Done)") && !Src("Revit", "ChangeReplay.cs").Contains("\"already not in the model\""), "");
        check("lifeline: Revit's energy model is never written or replayed", Src("Revit", "ChangeFile.cs").Contains("IsAnalysis(category.Id.Value)") && Src("Revit", "ChangeReplay.cs").Contains("ChangeFile.IsAnalysis(line.CategoryId)"), "");
        check("lifeline: lines marked new stay marked when a newer core carries on", Src("Revit", "ChangeFile.cs").Contains("else SeedAdded(model);"), "");
        check("lifeline: the replay alert shows the commonest reasons, and only when something needs the user", Src("UI", "RecoverWindow.cs").Contains("TopReasons(outcome.NotDone)")
            && Src("UI", "RecoverWindow.cs").Contains("if (outcome.NotDone.Count > 0 || stoppedBecause is not null)\n            MessageBox.Show(".Replace("\n", Environment.NewLine)) || Src("UI", "RecoverWindow.cs").Replace("\r\n", "\n").Contains("if (outcome.NotDone.Count > 0 || stoppedBecause is not null)\n            MessageBox.Show("), "");
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
        var clearedFile = M.Read([model2, M.Write(made), M.Write(new M.Line { Kind = M.Kinds.Cleared, At = at.AddMinutes(5), Text = "synchronized" })]);
        check("lifeline: a cleared file says which Revit event cleared it, and when", clearedFile.ClearedBy == "synchronized" && clearedFile.ClearedAt == at.AddMinutes(5) && clearedFile.Elements.Count == 1, "");
        check("lifeline: the window also offers the file cleared at the last save, newest first", Src("Revit", "ChangeFile.cs").Contains("M.LastSaveName, StringComparison.OrdinalIgnoreCase)) continue;") && Src("Revit", "ChangeFile.cs").Contains("OrderByDescending(File.GetLastWriteTime)"), "");
        check("update: a click while the automatic update runs follows it instead of saying another window is updating", Src("Update", "Updater.cs").Contains("Follow(running, progress)") && !Src("Update", "Updater.cs").Contains("Another Revit window is updating"), "");
        check("lifeline: the window lists the open model's files, each with View and Replay, nothing to pick", Src("UI", "RecoverWindow.cs").Contains("Brand.Button(\"View\")") && Src("UI", "RecoverWindow.cs").Contains("ChangeFile.CurrentFor(target.Path)") && !Src("UI", "RecoverWindow.cs").Contains("OpenFileDialog"), "");
        // ---- What a real replay missed (9 Nov 2026: 543 items not done of 2258) --------------------------------------
        // Deletions are named: each element's UniqueId is noted by its id, kept small.
        var known = new M.KnownIds();
        const string episode = "b2f3a85d-6910-4195-b411-8563b3bba103";
        known.Remember(438011, episode + "-0006aefb", "Viewports");
        known.Remember(438016, episode + "-0006af00", "Viewports");
        known.Remember(7, "74e46f31-7d48-47ee-9207-931708e57a52-00000007", null);
        known.Remember(8, "an-odd-id-WITHOUT-hex", "Walls");
        var got1 = known.TryGet(438011, out var u1, out var c1);
        var got7 = known.TryGet(7, out var u7, out var c7);
        var got8 = known.TryGet(8, out var u8, out var c8);
        check("lifeline: a noted element's UniqueId comes back whole, with its zeros and category",
            got1 && u1 == episode + "-0006aefb" && c1 == "Viewports" && got7 && u7 == "74e46f31-7d48-47ee-9207-931708e57a52-00000007" && c7 is null
            && got8 && u8 == "an-odd-id-WITHOUT-hex" && c8 == "Walls", u1 + " / " + u7 + " / " + u8);
        known.Forget(438016);
        check("lifeline: an element not noted has no UniqueId to give", !known.TryGet(438016, out _, out _) && !known.TryGet(99, out _, out _) && known.Count == 3, known.Count.ToString());
        var bulk = new M.KnownIds();
        var memoryBefore = GC.GetTotalMemory(true);
        for (var i = 0; i < 200_000; i++) bulk.Remember(i, episode + "-" + i.ToString("x8"), i % 2 == 0 ? "Walls" : "Structural Columns");
        var perElement = (GC.GetTotalMemory(true) - memoryBefore) / 200_000.0;
        check("lifeline: noting an element takes tens of bytes, not its whole UniqueId", perElement < 80 && bulk.TryGet(199_999, out var lastNoted, out _) && lastNoted == episode + "-00030d3f", perElement.ToString("0") + " bytes each");
        check("lifeline: the model's elements are noted in short slices, after what changed is read", M.IndexBudgetMs is > 0 and <= 20
            && Src("Revit", "ChangeFile.cs").Contains("if (!reading && For(doc) is { } model) more |= KeepFamily(doc, model) || KeepElements(doc, model) || NoteElements(doc, model);"), M.IndexBudgetMs.ToString());
        check("lifeline: a deletion takes its UniqueId from the noted elements; one made and deleted after the save is marked",
            Src("Revit", "ChangeFile.cs").Contains("model.Known.TryGet(id.Value, out var unique, out var category)") && Src("Revit", "ChangeFile.cs").Contains("New = made,")
            && Src("Revit", "ChangeReplay.cs").Contains("line.Kind == M.Kinds.Deleted && line.New && line.Unique is not { Length: > 0 }"), "");
        // What Revit makes by itself never gets a line, and isn't played from older files.
        check("lifeline: a sketch's automatic dimensions, cameras, section boxes, sun paths and type previews are Revit's own",
            new long[] { -2000261, -2000262, -2000500, -2000301, -2009609, -2000576 }.All(M.IsRevitsOwn) && !M.IsRevitsOwn(-2000575) && !M.IsRevitsOwn(-2000011) && !M.IsRevitsOwn(0), "");
        check("lifeline: Revit's own elements get no line and older lines of them aren't played", Src("Revit", "ChangeFile.cs").Contains("M.IsRevitsOwn(category.Id.Value)") && Src("Revit", "ChangeFile.cs").Contains("ChangeViews.IsRevitsOwn(element)")
            && Src("Revit", "ChangeReplay.cs").Contains("M.IsRevitsOwn(line.CategoryId) || ChangeViews.IsRevitsOwn(line)") && Src("Revit", "ChangeViews.cs").Contains("IsTitleblockRevisionSchedule")
            && Src("Revit", "ChangeViews.cs").Contains("viewport.SheetId == ElementId.InvalidElementId"), "");
        // A type or a view never takes the sketch of an element of that type or in that view.
        check("lifeline: only an element drawn from a sketch of its own has an outline", Src("Revit", "ChangeShapes.cs").Contains("if (e is not (RoofBase or RevisionCloud or")
            && Src("Revit", "ChangeShapes.cs").Contains("Wall w => w.SketchId"), "");
        check("lifeline: a slope arrow is told by its Specify value and drawn again with the floor", Src("Revit", "ChangeShapes.cs").Contains("BuiltInParameter.SPECIFY_SLOPE_OR_OFFSET")
            && Src("Revit", "ChangeShapes.cs").Contains("structural, floorArrow, line.ArrowSlope ?? 0)") && !Src("Revit", "ChangeShapes.cs").Contains("GetAllElements().Count > count"), "");
        var sloped = M.Read([M.Write(new M.Line { Kind = M.Kinds.State, At = at, Unique = "f2", What = "floor", Loops = [outline], SlopeArrow = true, Arrow = new(new(0, 0, 0), new(20, 0, 0)), ArrowSlope = 0.02 })]).Elements.Single();
        check("lifeline: a floor's slope arrow and slope read back", sloped.Arrow is { Q.X: 20 } && sloped.ArrowSlope == 0.02 && sloped.SlopeArrow, M.Write(sloped));
        // Text and dimension types have no category: they have lines, and are found by name.
        check("lifeline: text and dimension types are written and found by name", Src("Revit", "ChangeFile.cs").Contains("element is ElementType { Category: null } and not (TextElementType or DimensionType)")
            && Src("Revit", "ChangeReplay.cs").Contains("t.Category is null || t.Category.Id.Value == line.CategoryId"), "");
        // Families, materials: before the types and elements that use them.
        var famLine = new M.Line { Kind = M.Kinds.State, At = at, Unique = "fam1", What = "family", Name = "MS-SheetPile-AZ1", File = M.FamilyFileName("fam1"), New = true };
        var matLine = new M.Line { Kind = M.Kinds.State, At = at, Unique = "mat1", What = "material", Name = "MS - Rockfill", Extra = new() { ["color"] = "12,140,200" }, New = true };
        var symbolLine = new M.Line { Kind = M.Kinds.State, At = at, Unique = "sym1", What = "type", Name = "AZ14-770", Class = "FamilySymbol", New = true };
        var pileLine = new M.Line { Kind = M.Kinds.State, At = at, Unique = "pile1", What = "instance", Type = "MS-SheetPile-AZ1: AZ14-770", P = new(1, 2, 3), New = true };
        var loaded = M.Read(new[] { pileLine, symbolLine, matLine, famLine }.Select(M.Write));
        var loadedOrder = M.PlayOrder(loaded).Select(l => l.Unique).ToList();
        check("lifeline: materials and loaded families play before types, types before their elements", loadedOrder.SequenceEqual(["mat1", "fam1", "sym1", "pile1"]), string.Join(",", loadedOrder));
        var famBack = loaded.Elements.First(e => e.What == "family");
        check("lifeline: a loaded family's line names its kept copy; a material's colour reads back", famBack.File == "fam1.rfa" && loaded.Elements.First(e => e.What == "material").Extra?["color"] == "12,140,200", M.Write(famBack));
        var keptFamilies = M.FamiliesToDelete([@"C:\x\families\fam1.rfa", @"C:\x\families\fam2.rfa", @"C:\x\families\fam2.0001.rfa"], [M.Write(famLine)]);
        check("lifeline: kept families no change file names are deleted, the others stay", keptFamilies.Count == 2 && keptFamilies.All(f => f.Contains("fam2")), string.Join(",", keptFamilies));
        check("lifeline: a loaded family is copied next to the file and loaded again from the copy", Src("Revit", "ChangeFile.cs").Contains("doc.EditFamily(family)") && Src("Revit", "ChangeFile.cs").Contains("opened?.Close(false)")
            && Src("Revit", "ChangeTypes.cs").Contains("doc.LoadFamily(named, new KeepValues(), out var family)") && Src("Revit", "ChangeTypes.cs").Contains("Material.Create(doc, line.Name!)"), "");
        check("lifeline: a family's type is never a copy of another family's type", Src("Revit", "ChangeTypes.cs").Contains("isn't in this model: load it, then replay again"), "");
        check("lifeline: a type's layers go back into its own structure first", Src("Revit", "ChangeTypes.cs").Contains("cs.SetLayers(layers);"), "");
        // Playing a file again: what the first play made is found.
        var madeMap = new Dictionary<string, string> { ["old-1"] = "new-1", ["old-2"] = "new-2" };
        var madeBack = M.ParseMade(M.WriteMade(madeMap).Append("").Append("broken line"));
        check("lifeline: what a replay made again is remembered next to the file", madeBack.Count == 2 && madeBack["old-2"] == "new-2" && M.MadeMapName(@"C:\x\not saved 2026-11-09 043548.txt").EndsWith(".txt.made"), madeBack.Count.ToString());
        check("lifeline: a replay played again sets what it made before, and remembers after each chunk", Src("UI", "RecoverWindow.cs").Contains("ChangeReplay.Remember(doc, madeBefore, outcome)")
            && Src("UI", "RecoverWindow.cs").Contains("ChangeFile.WriteMade(file, outcome.MadeUnique)") && Src("Revit", "ChangeReplay.cs").Contains("made.TryGetValue(unique, out var again)"), "");
        // The chunks: every step is played (a growing chunk used to jump over steps: 45 of 2258, 17 of 72).
        check("lifeline: the next chunk starts where the last one ended, whatever its size", Src("UI", "RecoverWindow.cs").Contains("i += chunk.Count;") && !Src("UI", "RecoverWindow.cs").Contains("i += size"), "");
        var chunkSize = M.ReplayChunk;
        var played = 0;
        for (var i = 0; i < 72;) { var n = Math.Min(chunkSize, 72 - i); i += n; played += n; chunkSize = M.NextChunk(n, TimeSpan.FromMilliseconds(50)); }
        check("lifeline: 72 steps in growing chunks are 72 steps played", played == 72, played.ToString());
        check("lifeline: what Revit refused is tried once more at the end, and taken names are set then", Src("UI", "RecoverWindow.cs").Contains("outcome.TakeFailed()") && Src("UI", "RecoverWindow.cs").Contains("ChangeReplay.Settle(doc, outcome)")
            && Src("Revit", "ChangeReplay.cs").Contains("if (step.Act == Act.Make) outcome.Unmake(step.Line.Unique);"), "");
        check("lifeline: an element just made is worked out before its state is set", Src("Revit", "ChangeReplay.cs").Contains("try { doc.Regenerate(); } catch (Exception) { }\n                        SetState(doc, made, s.Line, outcome);".Replace("\n", Environment.NewLine))
            || Src("Revit", "ChangeReplay.cs").Replace("\r\n", "\n").Contains("try { doc.Regenerate(); } catch (Exception) { }\n                        SetState(doc, made, s.Line, outcome);"), "");
        check("lifeline: a filled region whose outline changed is drawn again in its place", Src("Revit", "ChangeReplay.cs").Contains("FilledRegion.Create(doc, region.GetTypeId(), region.OwnerViewId, ChangeShapes.CurveLoops(outline))"), "");
        check("lifeline: a close that is cancelled keeps the model watched", Src("Revit", "ChangeFile.cs").Contains("DocumentClosed += OnClosed") && Src("Revit", "ChangeFile.cs").Contains("if (e.Status == RevitAPIEventStatus.Succeeded) Models.Remove(path);"), "");
        check("lifeline: schedules of every kind are made by their own call", new[] { "CreateKeySchedule", "CreateMaterialTakeoff", "CreateSheetList", "CreateViewList", "CreateRevisionSchedule", "CreateKeynoteLegend" }.All(Src("Revit", "ChangeViews.cs").Contains), "");
        check("lifeline: a toposolid's shape editor is on before its points go back", Src("Revit", "ChangeShapes.cs").Replace("\r\n", "\n").Contains("if (!editor.IsEnabled) editor.Enable();\n        if (e is Toposolid)"), "");
        check("lifeline: a stopped replay says how to carry on", Src("UI", "RecoverWindow.cs").Contains("Press Replay again to carry on"), "");
        // Fewer messages: one line at the end, one row for each reason.
        check("lifeline: all back is one short line", M.ReplaySummary(350, 0, null) == "All 350 changes are back. Check the model before you save." && M.ReplaySummary(1, 0, null).StartsWith("The change is back."), M.ReplaySummary(350, 0, null));
        check("lifeline: some not back says how many need the user", M.ReplaySummary(338, 12, null).StartsWith("338 of 350 changes are back; 12 need you (listed below).") && M.ReplaySummary(9, 1, null).Contains("1 needs you"), M.ReplaySummary(338, 12, null));
        check("lifeline: a stopped replay's line says how to carry on", M.ReplaySummary(5, 0, "Stopped after 5 of 9 because you pressed Stop. Press Replay again to carry on.").Contains("Press Replay again to carry on. Check the model"), "");
        var notDone = new List<string>
        {
            "05:48 Generic Models: Pile A (added or changed): the type \"Pile A: 600\" isn't loaded in this model",
            "05:49 Generic Models: Pile B (added or changed): the type \"Pile B: 900\" isn't loaded in this model",
            "05:27 element 1 (deleted, Change Type): deleted by a step Lifeline couldn't read; in the model now it is Walls \"W1\": delete it by hand if you deleted it",
            "05:27 element 2 (deleted, Change Type): deleted by a step Lifeline couldn't read; in the model now it is Grids \"A\": delete it by hand if you deleted it",
            "05:28 element 3 (deleted, Change Type): deleted by a step Lifeline couldn't read; in the model now it is Grids \"B\": delete it by hand if you deleted it",
            "06:50 Walls: W2 (added or changed): its level isn't in this model",
        };
        var grouped = M.GroupReasons(notDone);
        check("lifeline: what wasn't done is one row for each reason, commonest first", grouped.Count == 3 && grouped[0].Items.SequenceEqual([2, 3, 4]) && grouped[1].Items.SequenceEqual([0, 1]) && grouped[2].Items.SequenceEqual([5]),
            string.Join(" | ", grouped.Select(g => g.Items.Count + " " + g.Reason)));
        check("lifeline: a reason is told without the element's own name", M.Generic("the type \"Pile A: 600\" isn't loaded in this model") == "the type … isn't loaded in this model"
            && M.Generic("deleted by a step Lifeline couldn't read; in the model now it is Walls \"W1\": delete it by hand if you deleted it") == "deleted by a step Lifeline couldn't read: delete it by hand if you deleted it", M.Generic(M.Reason(notDone[2])));
        var manyReasons = Enumerable.Range(0, 40).Select(i => $"05:00 Walls: W (added or changed): reason {(char)('a' + i % 20)}{(char)('a' + i / 20)}").ToList();
        var capped = M.GroupReasons(manyReasons, 12);
        check("lifeline: a long list of reasons ends with one row for the rest", capped.Count == 12 && capped[11].Reason == "other reasons" && capped.Sum(g => g.Items.Count) == 40, capped.Count.ToString());
        check("lifeline: the window lists reasons, and items only when asked", Src("UI", "RecoverWindow.cs").Contains("M.GroupReasons(outcome.NotDone)") && Src("UI", "RecoverWindow.cs").Contains("Brand.Button(\"List\")"), "");
        check("lifeline: a slope arrow that came back is not a message", !Src("Revit", "ChangeReplay.cs").Contains("check its slope") && !Src("Revit", "ChangeNotes.cs").Contains("check which side its bars are on"), "");
        // Kept copies: what can't be made from a line is copied next to the file and copied back.
        var keptLine = new M.Line { Kind = M.Kinds.State, At = at, Unique = "inplace1", What = "instance", Category = "Generic Models", New = true, Extra = new() { ["kept"] = "1" } };
        var keptBack = M.Read(new[] { keptLine }.Select(M.Write)).Elements[0];
        check("lifeline: a line says its element has a kept copy", M.IsKept(keptBack) && !M.IsKept(pileLine) && M.KeptInUse([M.Write(keptLine)]) && !M.KeptInUse([M.Write(pileLine), M.Write(matLine)]), M.Write(keptLine));
        check("lifeline: in-place families and what has no maker are kept as copies, the model only read", Src("Revit", "ChangeKeeper.cs").Contains("Symbol.Family.IsInPlace: true") && Src("Revit", "ChangeKeeper.cs").Contains("ChangeReplay.CanMake(line) is not null")
            && Src("Revit", "ChangeKeeper.cs").Contains("ElementTransformUtils.CopyElements(doc, [e.Id], keeper, Transform.Identity, Options())") && Src("Revit", "ChangeKeeper.cs").Contains("keeper?.Close(false)"), "");
        check("lifeline: a kept copy is copied back where it was", Src("Revit", "ChangeKeeper.cs").Contains("ElementTransformUtils.CopyElements(keeper, [source.Id], doc, Transform.Identity, Options())")
            && Src("Revit", "ChangeReplay.cs").Contains("else if (FromKept(line, outcome))") && Src("UI", "RecoverWindow.cs").Contains("ChangeKeeper.Close(outcome.Keeper)"), "");
        check("lifeline: the kept-elements project is never watched, and copies stop when one is slow", Src("Revit", "ChangeFile.cs").Contains("ChangeKeeper.Is(path)") && Src("Revit", "ChangeFile.cs").Contains("clock.ElapsedMilliseconds > M.KeepLimitMs")
            && M.KeepLimitMs is > 0 and <= 30000, "");
        check("lifeline: a family loaded again over itself is told by Revit's step", M.IsFamilyLoad("Load Family") && M.IsFamilyLoad("Reload Family") && M.IsFamilyLoad("Load into Project and Close") && !M.IsFamilyLoad("Modify element attributes") && !M.IsFamilyLoad("Move"), "");
        check("lifeline: angle and radius dimensions, free form bars, area plans and view references are made again", Src("Revit", "ChangeNotes.cs").Contains("AngularDimension.Create(doc, view, arc,")
            && Src("Revit", "ChangeNotes.cs").Contains("RadialDimension.Create(doc, view,") && Src("Revit", "ChangeNotes.cs").Contains("Rebar.CreateFreeForm(doc, barType, host, bars")
            && Src("Revit", "ChangeViews.cs").Contains("ViewPlan.CreateAreaPlan(doc, scheme.Id,") && Src("Revit", "ChangeViews.cs").Contains("ReferenceableViewUtils.ChangeReferencedView(doc, copy.Id, target.Id)"), "");
        var look = new M.Look { Itemized = false, GrandTotal = true, Cols = [new() { P = -1002050, H = "Mark", W = 0.2 }], Sorts = [new() { P = -1002050, Desc = true, Header = true }], Filters = [new() { P = -1002050, T = "Contains", K = "s", V = "P-" }] };
        var lookBack = System.Text.Json.JsonSerializer.Deserialize<M.Look>(System.Text.Json.JsonSerializer.Serialize(look))!;
        check("lifeline: a schedule's headings, sorting and filters are written in its line and read back", !lookBack.Itemized && lookBack.GrandTotal && lookBack.Title && lookBack.Cols[0].H == "Mark" && lookBack.Sorts[0].Desc && lookBack.Filters[0].V == "P-"
            && Src("Revit", "ChangeViews.cs").Contains("SetLook(definition,"), System.Text.Json.JsonSerializer.Serialize(look));
        check("recovery: Help > Open folders lists the change files", SketchProMax.Bugs.UserFolders.List().Any(f => f.Title == "Change files" && f.Path.EndsWith(Path.Combine("Sketch ProMax", "Change files"))), "");
    }
}
