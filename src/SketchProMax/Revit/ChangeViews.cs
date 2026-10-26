using Autodesk.Revit.DB;
using M = SketchProMax.Revit.ChangeFileMath;

namespace SketchProMax.Revit;

/// <summary>
/// Views, sheets and what sits on sheets in the change file, and groups, Revit links and CAD: reading what makes each
/// one, making it again (plans, ceiling plans, sections, details, 3D views, drafting views, schedules with their fields,
/// dependent views, sheets, views and schedules placed on sheets, groups, Revit links, linked and imported CAD), and
/// setting it back. Views Revit can't make without more than the file holds (elevations, legends, area plans) are
/// listed to redo by hand.
/// </summary>
internal static class ChangeViews
{
    /// <summary>What the change file calls the element; null when it isn't one of these kinds.</summary>
    public static string? Kind(Element e) => e switch
    {
        ViewSheet => "sheet",
        View => "view",
        Viewport => "viewport",
        ScheduleSheetInstance => "schedule-on-sheet",
        Group => "group",
        RevitLinkInstance => "link",
        ImportInstance => "cad",
        _ => null,
    };

    public static bool Owns(M.Line line) => line.What is "sheet" or "view" or "viewport" or "schedule-on-sheet" or "group" or "link" or "cad";

    /// <summary>Views that are Revit's own (browsers, internal views) never get a line.</summary>
    public static bool Ignored(Element e) => e is View v && v.ViewType is ViewType.ProjectBrowser or ViewType.SystemBrowser or ViewType.Internal
        or ViewType.Undefined or ViewType.Report or ViewType.CostReport or ViewType.LoadsReport;

    // ---- Reading -------------------------------------------------------------------------------------------------------

    public static void Read(Document doc, Element e, M.Line line)
    {
        switch (e)
        {
            case ViewSheet:
                line.ViewKind = nameof(ViewType.DrawingSheet);
                break;
            case View view:
                line.ViewKind = view.ViewType.ToString();
                try { line.Name = view.Name; } catch (Exception) { }
                if (view.GenLevel is { } level) { line.Level = level.Name; line.LevelUnique = level.UniqueId; }
                if (view.GetPrimaryViewId() is { } primary && primary != ElementId.InvalidElementId) line.Host = doc.GetElement(primary)?.UniqueId;
                if (view is View3D v3)
                {
                    var o = v3.GetOrientation();
                    line.Box = new M.Box { Eye = true, Origin = P(o.EyePosition), BasisY = P(o.UpDirection), BasisZ = P(o.ForwardDirection) };
                    if (v3.IsPerspective) line.Text = "perspective";
                    if (v3.IsSectionBoxActive && v3.GetSectionBox() is { } sb) line.Box.SectionBox = BoxOf(sb);
                }
                else if (view.ViewType is ViewType.Section or ViewType.Detail or ViewType.FloorPlan or ViewType.CeilingPlan or ViewType.EngineeringPlan)
                {
                    try { if (view.CropBox is { } crop) line.Box = BoxOf(crop); } catch (Exception) { }
                }
                if (view is ViewSchedule schedule)
                {
                    try
                    {
                        var definition = schedule.Definition;
                        line.ScheduleCategory = definition.CategoryId.Value;
                        line.Fields = Enumerable.Range(0, definition.GetFieldCount()).Select(i => definition.GetField(i).ParameterId.Value).ToList();
                    }
                    catch (Exception) { }
                }
                break;
            case Viewport viewport:
                line.Host = doc.GetElement(viewport.ViewId)?.UniqueId;
                line.View = doc.GetElement(viewport.SheetId)?.UniqueId;
                line.P = P(viewport.GetBoxCenter());
                break;
            case ScheduleSheetInstance placed:
                line.Host = doc.GetElement(placed.ScheduleId)?.UniqueId;
                line.View = doc.GetElement(placed.OwnerViewId)?.UniqueId;
                line.P = P(placed.Point);
                break;
            case Group group:
                if (group.Location is LocationPoint gp) line.P = P(gp.Point);
                break;
            case RevitLinkInstance link:
                line.Box = Placement(link.GetTotalTransform());
                break;
            case ImportInstance cad:
                line.Box = Placement(cad.GetTotalTransform());
                line.Text = cad.IsLinked ? "linked" : "imported";
                try
                {
                    if (doc.GetElement(cad.GetTypeId()) is { } type && type.IsExternalFileReference())
                        line.Name = ModelPathUtils.ConvertModelPathToUserVisiblePath(type.GetExternalFileReference().GetAbsolutePath());
                }
                catch (Exception) { }
                break;
        }
        // A group member says which group it is in: a group made again brings its members.
        try { if (e.GroupId is { } g && g != ElementId.InvalidElementId) line.Group = doc.GetElement(g)?.UniqueId; } catch (Exception) { }
    }

    private static M.Box BoxOf(BoundingBoxXYZ box) => new()
    {
        Origin = P(box.Transform.Origin), BasisX = P(box.Transform.BasisX), BasisY = P(box.Transform.BasisY), BasisZ = P(box.Transform.BasisZ),
        Min = P(box.Min), Max = P(box.Max),
    };

    private static M.Box Placement(Transform t) => new() { Origin = P(t.Origin), BasisX = P(t.BasisX) };

    private static BoundingBoxXYZ Bounding(M.Box b)
    {
        var t = Transform.Identity;
        t.Origin = X(b.Origin);
        if (b.BasisX is { } bx && b.BasisY is { } by && b.BasisZ is { } bz) { t.BasisX = X(bx); t.BasisY = X(by); t.BasisZ = X(bz); }
        return new BoundingBoxXYZ { Transform = t, Min = X(b.Min ?? new M.Xyz(0, 0, 0)), Max = X(b.Max ?? new M.Xyz(1, 1, 1)) };
    }

    // ---- Making again -----------------------------------------------------------------------------------------------------

    public static string? CanMake(M.Line line) => line.What switch
    {
        "view" when line.ViewKind is nameof(ViewType.Elevation) => "an elevation made after the last save: make it again by hand (its marker needs a plan)",
        "view" when line.ViewKind is nameof(ViewType.Legend) => "a legend made after the last save: duplicate a legend and redo it by hand",
        "view" when line.ViewKind is nameof(ViewType.AreaPlan) => "an area plan made after the last save: make it again by hand (it needs its area scheme)",
        "view" when line.Values?.Any(v => v.Bip == (int)BuiltInParameter.VIEW_TEMPLATE_FOR_SCHEDULE) == true && line.ViewKind is null => "a view template made after the last save: make it again by hand",
        "view" when line.Host is not null => null,
        "view" when line.ViewKind is nameof(ViewType.FloorPlan) or nameof(ViewType.CeilingPlan) or nameof(ViewType.EngineeringPlan) && line.LevelUnique is not null => null,
        "view" when line.ViewKind is nameof(ViewType.Section) or nameof(ViewType.Detail) && line.Box is { Min: not null, Max: not null } => null,
        "view" when line.ViewKind is nameof(ViewType.ThreeD) or nameof(ViewType.DraftingView) => null,
        "view" when line.ViewKind is nameof(ViewType.Schedule) && line.ScheduleCategory != 0 => null,
        "view" => "a view made after the last save that the file can't make: make it again by hand",
        "sheet" => null,
        "viewport" or "schedule-on-sheet" when line.Host is not null && line.View is not null && line.P is not null => null,
        "group" when line.P is not null && line.Type is not null => null,
        "link" when line.Type is not null => null,
        "cad" when line.Name is { Length: > 0 } => null,
        "cad" => "CAD made after the last save whose file isn't known: link or import it again by hand",
        _ => "made after the last save, but the file doesn't have all it needs: make it again by hand",
    };

    /// <summary>Makes the element again; null when <paramref name="line"/> isn't one of these kinds.</summary>
    public static Element? Make(Document doc, M.Line line, ElementType? type, Level? level, Func<string?, Element?> find, List<string> notes)
    {
        if (!Owns(line)) return null;
        var label = M.Describe(line);
        ElementId TypeId() => type?.Id ?? throw new InvalidOperationException($"the type {line.Type} isn't in this model");
        switch (line.What)
        {
            case "sheet":
                // No title block here: the title block has a line of its own and is made again on the sheet.
                return ViewSheet.Create(doc, ElementId.InvalidElementId);
            case "view":
                return MakeView(doc, line, TypeId, level, find, notes, label);
            case "viewport":
            {
                if (find(line.View) is not ViewSheet sheet) throw new InvalidOperationException("its sheet isn't in this model");
                if (find(line.Host) is not View view) throw new InvalidOperationException("its view isn't in this model");
                return Viewport.Create(doc, sheet.Id, view.Id, X(line.P!));
            }
            case "schedule-on-sheet":
            {
                if (find(line.View) is not ViewSheet sheet) throw new InvalidOperationException("its sheet isn't in this model");
                if (find(line.Host) is not ViewSchedule schedule) throw new InvalidOperationException("its schedule isn't in this model");
                return ScheduleSheetInstance.Create(doc, sheet.Id, schedule.Id, X(line.P!));
            }
            case "group":
            {
                if (type is not GroupType groupType) throw new InvalidOperationException($"the group type {line.Type} isn't in this model");
                return doc.Create.PlaceGroup(X(line.P!), groupType);
            }
            case "link":
            {
                var link = RevitLinkInstance.Create(doc, TypeId());
                Place(doc, link, line);
                return link;
            }
            case "cad":
            {
                var path = line.Name!;
                if (!System.IO.File.Exists(path)) throw new InvalidOperationException($"its file {path} isn't there any more: link or import it again by hand");
                var view = find(line.View) as View;
                var options = new DWGImportOptions { ThisViewOnly = view is not null, Placement = ImportPlacement.Origin };
                ElementId id;
                var ok = line.Text == "linked" ? doc.Link(path, options, view ?? doc.ActiveView, out id) : doc.Import(path, options, view ?? doc.ActiveView, out id);
                if (!ok || doc.GetElement(id) is not { } cad) throw new InvalidOperationException("Revit didn't take the CAD file");
                Place(doc, cad, line);
                return cad;
            }
            default:
                return null;
        }
    }

    private static Element MakeView(Document doc, M.Line line, Func<ElementId> typeId, Level? level, Func<string?, Element?> find, List<string> notes, string label)
    {
        View view;
        if (line.Host is not null)
        {
            if (find(line.Host) is not View primary) throw new InvalidOperationException("the view it depends on isn't in this model");
            view = (View)doc.GetElement(primary.Duplicate(ViewDuplicateOption.AsDependent));
        }
        else
        {
            switch (line.ViewKind)
            {
                case nameof(ViewType.FloorPlan) or nameof(ViewType.CeilingPlan) or nameof(ViewType.EngineeringPlan):
                    view = ViewPlan.Create(doc, typeId(), (level ?? throw new InvalidOperationException("its level isn't in this model")).Id);
                    if (line.Box is { Min: not null, Max: not null } crop) try { view.CropBox = Bounding(crop); } catch (Exception) { }
                    break;
                case nameof(ViewType.Section):
                    view = ViewSection.CreateSection(doc, typeId(), Bounding(line.Box!));
                    break;
                case nameof(ViewType.Detail):
                    view = ViewSection.CreateDetail(doc, typeId(), Bounding(line.Box!));
                    break;
                case nameof(ViewType.ThreeD):
                {
                    var v3 = line.Text == "perspective" ? View3D.CreatePerspective(doc, typeId()) : View3D.CreateIsometric(doc, typeId());
                    if (line.Box is { Eye: true, BasisY: { } up, BasisZ: { } forward } eye)
                        try { v3.SetOrientation(new ViewOrientation3D(X(eye.Origin), X(up), X(forward))); } catch (Exception) { }
                    if (line.Box?.SectionBox is { Min: not null, Max: not null } sb) try { v3.SetSectionBox(Bounding(sb)); } catch (Exception) { }
                    view = v3;
                    break;
                }
                case nameof(ViewType.DraftingView):
                    view = ViewDrafting.Create(doc, typeId());
                    break;
                case nameof(ViewType.Schedule):
                {
                    var schedule = ViewSchedule.CreateSchedule(doc, new ElementId(line.ScheduleCategory));
                    var definition = schedule.Definition;
                    var fields = definition.GetSchedulableFields();
                    var missing = 0;
                    foreach (var id in line.Fields ?? [])
                    {
                        var field = fields.FirstOrDefault(f => f.ParameterId.Value == id);
                        if (field is null) { missing++; continue; }
                        try { definition.AddField(field); } catch (Exception) { missing++; }
                    }
                    notes.Add($"{label}: made again with its columns: redo its filters, sorting and formatting by hand" + (missing > 0 ? $" ({missing} column(s) couldn't be added)" : ""));
                    view = schedule;
                    break;
                }
                default:
                    throw new InvalidOperationException("this kind of view can't be made from the file: make it again by hand");
            }
        }
        if (line.Name is { Length: > 0 } name) try { view.Name = name; } catch (Exception) { notes.Add($"{label}: made again, but the name {name} is taken: rename it by hand"); }
        return view;
    }

    /// <summary>A link or CAD to its place in the file (moved, then turned about its origin).</summary>
    private static void Place(Document doc, Element e, M.Line line)
    {
        if (line.Box is not { } want || e is not Instance instance) return;
        var now = instance.GetTotalTransform();
        var move = X(want.Origin) - now.Origin;
        if (move.GetLength() > 3.3e-5) ElementTransformUtils.MoveElement(doc, e.Id, move);
        if (want.BasisX is { } bx)
        {
            var angle = now.BasisX.AngleOnPlaneTo(X(bx), XYZ.BasisZ);
            if (Math.Abs(angle) > 1e-6 && Math.Abs(angle - 2 * Math.PI) > 1e-6)
                ElementTransformUtils.RotateElement(doc, e.Id, Line.CreateBound(X(want.Origin), X(want.Origin) + XYZ.BasisZ), angle);
        }
    }

    // ---- Setting back ---------------------------------------------------------------------------------------------------

    public static bool Set(Document doc, Element e, M.Line line, List<string> notes)
    {
        var changed = false;
        switch (e)
        {
            case View view when e is not ViewSheet:
                if (line.Name is { Length: > 0 } name && view.Name != name) { try { view.Name = name; changed = true; } catch (Exception) { notes.Add($"{M.Describe(line)}: the name {name} is taken: rename it by hand"); } }
                break;
            case Viewport viewport when line.P is { } p && !M.Same(P(viewport.GetBoxCenter()), p):
                viewport.SetBoxCenter(X(p));
                changed = true;
                break;
            case ScheduleSheetInstance placed when line.P is { } sp && !M.Same(P(placed.Point), sp):
                placed.Point = X(sp);
                changed = true;
                break;
            case Group group when line.P is { } gp && group.Location is LocationPoint at && !M.Same(P(at.Point), gp):
                ElementTransformUtils.MoveElement(doc, group.Id, X(gp) - at.Point);
                changed = true;
                break;
            case RevitLinkInstance or ImportInstance when line.Box is { } box:
                var t = ((Instance)e).GetTotalTransform();
                if (!M.Same(P(t.Origin), box.Origin) || (box.BasisX is { } bx && !M.Same(P(t.BasisX), bx))) { Place(doc, e, line); changed = true; }
                break;
        }
        return changed;
    }

    private static M.Xyz P(XYZ p) => ChangeShapes.P(p);

    private static XYZ X(M.Xyz p) => ChangeShapes.X(p);
}
