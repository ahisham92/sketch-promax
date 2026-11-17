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
        // The preview Revit keeps of each loaded family type is in a category of its own (OST_PreviewLegendComponents): not this one.
        _ when e.Category?.Id.Value == (long)BuiltInCategory.OST_LegendComponents => "legendcomponent",
        // A view reference ("see sheet S-101"): an annotation with no class of its own.
        _ when e is not ElementType && e.Category?.Id.Value == (long)BuiltInCategory.OST_ReferenceViewer => "viewref",
        _ => null,
    };

    /// <summary>Elevation markers come with their elevations: no line of their own.</summary>
    public static bool IsPart(Element e) => e is ElevationMarker;

    /// <summary>The viewport's centre on its sheet, or null when it is not on a sheet (its sheet is being deleted).</summary>
    internal static XYZ? BoxCenter(Viewport viewport)
    {
        try { return viewport.SheetId == ElementId.InvalidElementId ? null : viewport.GetBoxCenter(); }
        catch (Autodesk.Revit.Exceptions.InvalidOperationException) { return null; }
    }

    public static bool Owns(M.Line line) => line.What is "sheet" or "view" or "viewport" or "schedule-on-sheet" or "group" or "link" or "cad" or "legendcomponent" or "viewref";

    /// <summary>
    /// What comes with something else and never gets a line: a title block's revision schedule (and its place on the
    /// sheet), Revit's internal keynote schedule, and a viewport that isn't on a sheet (Revit keeps one for each of those).
    /// </summary>
    public static bool IsRevitsOwn(Element e)
    {
        try
        {
            return e switch
            {
                ViewSchedule schedule => schedule.IsTitleblockRevisionSchedule || schedule.IsInternalKeynoteSchedule,
                ScheduleSheetInstance placed => placed.IsTitleblockRevisionSchedule,
                Viewport viewport => viewport.SheetId == ElementId.InvalidElementId,
                _ => false,
            };
        }
        catch (Exception) { return false; }
    }

    /// <summary>The same for a line of an older file (written before these were left out).</summary>
    public static bool IsRevitsOwn(M.Line line) =>
        (line.What is "view" or "schedule-on-sheet" && line.Name is { } name && name.StartsWith("<Revision Schedule>", StringComparison.Ordinal))
        || (line.What == "viewport" && line.Kind == M.Kinds.State && line.View is null);

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
                if (view.IsTemplate)
                {
                    // A view template: kept whole as a copy; what it leaves to each view is written here.
                    var x = line.Extra ??= [];
                    x["template"] = "1";
                    try { x["free"] = string.Join(";", view.GetNonControlledTemplateParameterIds().Select(i => i.Value)); } catch (Exception) { }
                }
                if (view.GenLevel is { } level) { line.Level = level.Name; line.LevelUnique = level.UniqueId; }
                // An area plan is made again in its area scheme.
                if (view is ViewPlan { AreaScheme: { } scheme }) (line.Extra ??= [])["scheme"] = scheme.Name;
                if (view.GetPrimaryViewId() is { } primary && primary != ElementId.InvalidElementId) line.Host = doc.GetElement(primary)?.UniqueId;
                if (view is View3D v3)
                {
                    var o = v3.GetOrientation();
                    line.Box = new M.Box { Eye = true, Origin = P(o.EyePosition), BasisY = P(o.UpDirection), BasisZ = P(o.ForwardDirection) };
                    if (v3.IsPerspective) line.Text = "perspective";
                    if (v3.IsSectionBoxActive && v3.GetSectionBox() is { } sb) line.Box.SectionBox = BoxOf(sb);
                }
                else if (view.ViewType is ViewType.Section or ViewType.Detail or ViewType.FloorPlan or ViewType.CeilingPlan or ViewType.EngineeringPlan or ViewType.Elevation)
                {
                    try { if (view.CropBox is { } crop) line.Box = BoxOf(crop); } catch (Exception) { }
                }
                if (view.ViewType == ViewType.Elevation)
                {
                    // Where it looks from and which way: a marker made again is turned and moved to match.
                    line.PlaneOrigin = P(view.Origin);
                    line.PlaneNormal = P(view.ViewDirection);
                }
                if (view is ViewSchedule schedule)
                {
                    try
                    {
                        var definition = schedule.Definition;
                        line.ScheduleCategory = definition.CategoryId.Value;
                        // The kind of schedule, for the call that makes it again.
                        line.Text = definition.IsKeySchedule ? "key" : definition.IsMaterialTakeoff ? "takeoff" : line.ScheduleCategory switch
                        {
                            (long)BuiltInCategory.OST_Sheets => "sheets",
                            (long)BuiltInCategory.OST_Views => "views",
                            (long)BuiltInCategory.OST_Revisions => "revisions",
                            (long)BuiltInCategory.OST_KeynoteTags => "keynotes",
                            _ => null,
                        };
                        line.Fields = Enumerable.Range(0, definition.GetFieldCount()).Select(i => definition.GetField(i).ParameterId.Value).ToList();
                        // Its headings, widths, hidden columns, sorting, grouping, filters and totals.
                        try { (line.Extra ??= [])["look"] = System.Text.Json.JsonSerializer.Serialize(ReadLook(definition)); } catch (Exception) { }
                    }
                    catch (Exception) { }
                }
                break;
            case Viewport viewport:
                line.Host = doc.GetElement(viewport.ViewId)?.UniqueId;
                line.View = doc.GetElement(viewport.SheetId)?.UniqueId;
                // A viewport that is coming off its sheet (its sheet deleted in the same change) has no box: Revit throws
                // "The viewport is not on a sheet". Nothing to keep then; it was logged as a Lifeline bug every time.
                if (BoxCenter(viewport) is { } center) line.P = P(center);
                break;
            case ScheduleSheetInstance placed:
                line.Host = doc.GetElement(placed.ScheduleId)?.UniqueId;
                line.View = doc.GetElement(placed.OwnerViewId)?.UniqueId;
                line.P = P(placed.Point);
                break;
            case Group group:
                if (group.Location is LocationPoint gp) line.P = P(gp.Point);
                // Its members, so a group of a type made after the last save is put together again from them.
                try
                {
                    var members = group.GetMemberIds().Take(MaxMembers).Select(doc.GetElement).Where(m => m is not null).Select(m => m!.UniqueId).ToList();
                    if (members.Count > 0) (line.Extra ??= [])["members"] = string.Join(";", members);
                }
                catch (Exception) { }
                break;
            case not null when line.What == "legendcomponent":
                if (doc.GetElement(e.OwnerViewId) is View legend && e.get_BoundingBox(legend) is { } bb) line.P = P((bb.Min + bb.Max) / 2);
                break;
            case not null when line.What == "viewref":
                if (doc.GetElement(e.OwnerViewId) is View drawn)
                {
                    line.View = drawn.UniqueId;
                    if (e.get_BoundingBox(drawn) is { } rb) line.P = P((rb.Min + rb.Max) / 2);
                }
                // The view it refers to.
                try { line.Host = doc.GetElement(ReferenceableViewUtils.GetReferencedViewId(doc, e.Id))?.UniqueId; } catch (Exception) { }
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

    private const int MaxMembers = 3000;

    public static string? CanMake(M.Line line) => line.What switch
    {
        "view" when M.IsTemplate(line) => "a view template made after the last save whose copy wasn't kept: make it again by hand",
        "view" when line.ViewKind is nameof(ViewType.Elevation) && (line.PlaneOrigin is null || line.PlaneNormal is null) => "an elevation made after the last save: make it again by hand",
        "view" when line.ViewKind is nameof(ViewType.Elevation) or nameof(ViewType.Legend) => null,
        "view" when line.ViewKind is nameof(ViewType.AreaPlan) && line.Host is null =>
            line.Extra is { } x && x.ContainsKey("scheme") && (line.LevelUnique is not null || line.Level is not null) ? null : "an area plan made after the last save whose area scheme isn't in the file: make it again by hand",
        "view" when line.Values?.Any(v => v.Bip == (int)BuiltInParameter.VIEW_TEMPLATE_FOR_SCHEDULE) == true && line.ViewKind is null => "a view template made after the last save: make it again by hand",
        "view" when line.Host is not null => null,
        "view" when line.ViewKind is nameof(ViewType.FloorPlan) or nameof(ViewType.CeilingPlan) or nameof(ViewType.EngineeringPlan) && line.LevelUnique is not null => null,
        "view" when line.ViewKind is nameof(ViewType.Section) or nameof(ViewType.Detail) && line.Box is { Min: not null, Max: not null } => null,
        "view" when line.ViewKind is nameof(ViewType.ThreeD) or nameof(ViewType.DraftingView) => null,
        "view" when line.ViewKind is nameof(ViewType.Schedule) && (line.ScheduleCategory != 0 || line.Fields is not null) => null,
        "view" => "a view made after the last save that the file can't make: make it again by hand",
        "sheet" => null,
        "viewport" or "schedule-on-sheet" when line.Host is not null && line.View is not null && line.P is not null => null,
        "group" when line.P is not null && line.Type is not null => null,
        "link" when line.Type is not null => null,
        "cad" when line.Name is { Length: > 0 } => null,
        "cad" => "CAD made after the last save whose file isn't known: link or import it again by hand",
        "legendcomponent" when line.View is not null && line.P is not null => null,
        "viewref" when line.View is not null && line.P is not null => null,
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
                // Revit gives nothing back for a view or sheet made a moment ago until the model is regenerated.
                var port = Viewport.Create(doc, sheet.Id, view.Id, X(line.P!));
                if (port is null) { doc.Regenerate(); port = Viewport.Create(doc, sheet.Id, view.Id, X(line.P!)); }
                return port ?? throw new InvalidOperationException("Revit didn't place the view on its sheet: place it by hand");
            }
            case "schedule-on-sheet":
            {
                if (find(line.View) is not ViewSheet sheet) throw new InvalidOperationException("its sheet isn't in this model");
                if (find(line.Host) is not ViewSchedule schedule) throw new InvalidOperationException("its schedule isn't in this model");
                var placed = ScheduleSheetInstance.Create(doc, sheet.Id, schedule.Id, X(line.P!));
                if (placed is null) { doc.Regenerate(); placed = ScheduleSheetInstance.Create(doc, sheet.Id, schedule.Id, X(line.P!)); }
                return placed ?? throw new InvalidOperationException("Revit didn't place the schedule on its sheet: place it by hand");
            }
            case "legendcomponent":
            {
                // Revit has no call to place a legend component: one already in the model is copied into the legend, then
                // set to the file's family type and view (its values) and moved into place.
                if (find(line.View) is not View legend) throw new InvalidOperationException("its legend isn't in this model");
                var source = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_LegendComponents).WhereElementIsNotElementType().FirstElement()
                             ?? throw new InvalidOperationException("there is no legend component in the model to copy: place it by hand");
                var copied = ElementTransformUtils.CopyElements(doc.GetElement(source.OwnerViewId) as View, [source.Id], legend, Transform.Identity, new CopyPasteOptions());
                return doc.GetElement(copied.First());
            }
            case "viewref":
            {
                // Revit has no call to place a view reference: one already in the model is copied into the view, then
                // pointed at the file's view, given its type and moved into place.
                if (find(line.View) is not View drawn) throw new InvalidOperationException("the view it was drawn in isn't in this model");
                var all = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_ReferenceViewer).WhereElementIsNotElementType().ToElements();
                var source = all.FirstOrDefault(r => r.OwnerViewId == drawn.Id) ?? all.FirstOrDefault(r => type is not null && r.GetTypeId() == type.Id) ?? all.FirstOrDefault()
                             ?? throw new InvalidOperationException("there is no view reference in the model to copy: place one by hand, then replay again");
                var copy = source.OwnerViewId == drawn.Id
                    ? doc.GetElement(ElementTransformUtils.CopyElement(doc, source.Id, XYZ.Zero).First())
                    : doc.GetElement(ElementTransformUtils.CopyElements(doc.GetElement(source.OwnerViewId) as View, [source.Id], drawn, Transform.Identity, new CopyPasteOptions()).First());
                if (find(line.Host) is View target)
                    try { ReferenceableViewUtils.ChangeReferencedView(doc, copy.Id, target.Id); }
                    catch (Exception) { notes.Add($"{label}: made again, but Revit didn't let it refer to {target.Name}: set its Target view by hand"); }
                if (type is not null && copy.GetTypeId() != type.Id) try { copy.ChangeTypeId(type.Id); } catch (Exception) { }
                return copy;
            }
            case "group":
            {
                if (type is GroupType groupType) return doc.Create.PlaceGroup(X(line.P!), groupType);
                return Regroup(doc, line, find, null);
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

    /// <summary>
    /// A group of a type that isn't in the model (made after the last save): its members, played one by one, are put
    /// together again and the new type gets the name. <paramref name="type"/>: the type an earlier group of the same
    /// kind got in this replay.
    /// </summary>
    public static Group Regroup(Document doc, M.Line line, Func<string?, Element?> find, GroupType? type)
    {
        var ids = M.Members(line).Select(u => find(u)).Where(m => m is not null && m.GroupId == ElementId.InvalidElementId).Select(m => m!.Id).Distinct().ToList();
        if (ids.Count == 0) throw new InvalidOperationException($"the group type {line.Type} isn't in this model, and its members aren't either: group them again by hand");
        var group = doc.Create.NewGroup(ids);
        var own = group.GroupType;
        if (type is not null)
        {
            // The same type as the first group of that kind, when Revit takes it (its own type then goes).
            try { group.GroupType = type; if (own.Id != type.Id) doc.Delete(own.Id); return group; } catch (Exception) { }
        }
        var name = line.Type is { } t && t.IndexOf(": ", StringComparison.Ordinal) is var cut and > 0 ? t.Substring(cut + 2) : line.Type;
        if (name is { Length: > 0 }) try { if (own.Name != name) own.Name = name; } catch (Exception) { }
        return group;
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
                case nameof(ViewType.AreaPlan):
                {
                    var wanted = line.Extra!["scheme"];
                    var scheme = new FilteredElementCollector(doc).OfClass(typeof(AreaScheme)).Cast<AreaScheme>().FirstOrDefault(a => a.Name == wanted)
                                 ?? throw new InvalidOperationException($"the area scheme {wanted} isn't in this model");
                    view = ViewPlan.CreateAreaPlan(doc, scheme.Id, (level ?? throw new InvalidOperationException("its level isn't in this model")).Id);
                    if (line.Box is { Min: not null, Max: not null } areaCrop) try { view.CropBox = Bounding(areaCrop); } catch (Exception) { }
                    break;
                }
                case nameof(ViewType.Elevation):
                    view = MakeElevation(doc, line, typeId());
                    break;
                case nameof(ViewType.Legend):
                {
                    // Revit has no call to make a legend: an empty copy of one already in the model.
                    var any = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().FirstOrDefault(v => v.ViewType == ViewType.Legend && !v.IsTemplate)
                              ?? throw new InvalidOperationException("there is no legend in the model to start from: make one legend by hand, then replay again");
                    view = (View)doc.GetElement(any.Duplicate(ViewDuplicateOption.Duplicate));
                    break;
                }
                case nameof(ViewType.Schedule):
                {
                    var category = new ElementId(line.ScheduleCategory);
                    var schedule = line.Text switch
                    {
                        "key" => ViewSchedule.CreateKeySchedule(doc, category),
                        "takeoff" => ViewSchedule.CreateMaterialTakeoff(doc, category),
                        "sheets" => ViewSchedule.CreateSheetList(doc),
                        "views" => ViewSchedule.CreateViewList(doc),
                        "revisions" => ViewSchedule.CreateRevisionSchedule(doc),
                        "keynotes" => ViewSchedule.CreateKeynoteLegend(doc),
                        // No category: a schedule of several categories.
                        _ => ViewSchedule.CreateSchedule(doc, line.ScheduleCategory == 0 || line.ScheduleCategory == -1 ? ElementId.InvalidElementId : category),
                    };
                    var definition = schedule.Definition;
                    var fields = definition.GetSchedulableFields();
                    var missing = 0;
                    var has = Enumerable.Range(0, definition.GetFieldCount()).Select(i => definition.GetField(i).ParameterId.Value).ToHashSet();
                    foreach (var id in line.Fields ?? [])
                    {
                        // A sheet list or the like comes with columns of its own.
                        if (has.Contains(id)) continue;
                        var field = fields.FirstOrDefault(f => f.ParameterId.Value == id);
                        if (field is null) { missing++; continue; }
                        try { definition.AddField(field); } catch (Exception) { missing++; }
                    }
                    // Its headings, widths, sorting, grouping, filters and totals, when the file has them (older files don't).
                    var looked = false;
                    if (line.Extra is { } extra && extra.TryGetValue("look", out var json))
                        try { SetLook(definition, System.Text.Json.JsonSerializer.Deserialize<M.Look>(json)!); looked = true; } catch (Exception) { }
                    if (!looked) notes.Add($"{label}: made again with its columns: redo its filters, sorting and formatting by hand" + (missing > 0 ? $" ({missing} column(s) couldn't be added)" : ""));
                    else if (missing > 0) notes.Add($"{label}: made again, but {missing} of its columns (calculated or combined ones) couldn't be added: add them by hand");
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

    /// <summary>An elevation on a new marker, turned and moved so it looks from where the file says, with its crop.</summary>
    private static View MakeElevation(Document doc, M.Line line, ElementId typeId)
    {
        var origin = X(line.PlaneOrigin!);
        var plan = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                       .Where(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan && v.GenLevel is not null)
                       .OrderBy(v => Math.Abs(v.GenLevel.Elevation - origin.Z)).FirstOrDefault()
                   ?? throw new InvalidOperationException("there is no floor plan to put its marker in");
        var at = new XYZ(origin.X, origin.Y, plan.GenLevel.Elevation);
        var marker = ElevationMarker.CreateElevationMarker(doc, typeId, at, 100);
        var view = marker.CreateElevation(doc, plan.Id, 0);
        doc.Regenerate();
        var want = new XYZ(line.PlaneNormal!.X, line.PlaneNormal.Y, 0);
        var now = new XYZ(view.ViewDirection.X, view.ViewDirection.Y, 0);
        if (want.GetLength() > 1e-6 && now.GetLength() > 1e-6)
        {
            var angle = now.AngleOnPlaneTo(want, XYZ.BasisZ);
            if (Math.Abs(angle) > 1e-6 && Math.Abs(angle - 2 * Math.PI) > 1e-6)
            {
                ElementTransformUtils.RotateElement(doc, marker.Id, Line.CreateBound(at, at + XYZ.BasisZ), angle);
                doc.Regenerate();
            }
        }
        var move = origin - view.Origin;
        move = new XYZ(move.X, move.Y, 0);
        if (move.GetLength() > 3.3e-5) { ElementTransformUtils.MoveElement(doc, marker.Id, move); doc.Regenerate(); }
        if (line.Box is { Min: not null, Max: not null } crop) try { view.CropBox = Bounding(crop); } catch (Exception) { }
        return view;
    }

    /// <summary>After its values are set: a legend component moved to where it was in its legend. True when moved.</summary>
    public static bool Settle(Document doc, Element e, M.Line line)
    {
        if (line.What is not ("legendcomponent" or "viewref") || line.P is not { } want || doc.GetElement(e.OwnerViewId) is not View legend) return false;
        doc.Regenerate();
        if (e.get_BoundingBox(legend) is not { } bb) return false;
        var move = X(want) - (bb.Min + bb.Max) / 2;
        // Only within the view's plane.
        XYZ? normal = null;
        try { normal = legend.ViewDirection; } catch (Exception) { }
        move = normal is null || normal.IsZeroLength() ? new XYZ(move.X, move.Y, 0) : move - normal.Multiply(move.DotProduct(normal));
        if (move.GetLength() < 3.3e-4) return false;
        ElementTransformUtils.MoveElement(doc, e.Id, move);
        return true;
    }

    // ---- A schedule's look -------------------------------------------------------------------------------------------------

    private static M.Look ReadLook(ScheduleDefinition d)
    {
        var look = new M.Look { Itemized = d.IsItemized, GrandTotal = d.ShowGrandTotal, Title = d.ShowTitle, Headers = d.ShowHeaders };
        long Param(ScheduleFieldId id) => d.GetField(id).ParameterId.Value;
        for (var i = 0; i < d.GetFieldCount(); i++)
        {
            var f = d.GetField(i);
            look.Cols.Add(new M.LookCol { P = f.ParameterId.Value, H = f.ColumnHeading, Hid = f.IsHidden, W = M.Round(f.GridColumnWidth) });
        }
        foreach (var s in d.GetSortGroupFields())
            look.Sorts.Add(new M.LookSort { P = Param(s.FieldId), Desc = s.SortOrder == ScheduleSortOrder.Descending, Header = s.ShowHeader, Footer = s.ShowFooter, Blank = s.ShowBlankLine });
        foreach (var f in d.GetFilters())
        {
            var filter = new M.LookFilter { P = Param(f.FieldId), T = f.FilterType.ToString() };
            var c = System.Globalization.CultureInfo.InvariantCulture;
            if (f.IsStringValue) (filter.K, filter.V) = ("s", f.GetStringValue());
            else if (f.IsDoubleValue) (filter.K, filter.V) = ("d", f.GetDoubleValue().ToString("R", c));
            else if (f.IsIntegerValue) (filter.K, filter.V) = ("i", f.GetIntegerValue().ToString(c));
            else if (f.IsElementIdValue) (filter.K, filter.V) = ("e", f.GetElementIdValue().Value.ToString(c));
            look.Filters.Add(filter);
        }
        return look;
    }

    /// <summary>Gives a schedule made again (its columns are there) the look of its line; what Revit refuses for one row is left.</summary>
    private static void SetLook(ScheduleDefinition d, M.Look look)
    {
        var byParam = new Dictionary<long, ScheduleField>();
        for (var i = 0; i < d.GetFieldCount(); i++)
        {
            var f = d.GetField(i);
            if (!byParam.ContainsKey(f.ParameterId.Value)) byParam[f.ParameterId.Value] = f;
        }
        try { d.IsItemized = look.Itemized; } catch (Exception) { }
        try { d.ShowGrandTotal = look.GrandTotal; } catch (Exception) { }
        try { d.ShowTitle = look.Title; } catch (Exception) { }
        try { d.ShowHeaders = look.Headers; } catch (Exception) { }
        foreach (var col in look.Cols)
        {
            if (!byParam.TryGetValue(col.P, out var f)) continue;
            try { if (col.H is not null && f.ColumnHeading != col.H) f.ColumnHeading = col.H; } catch (Exception) { }
            try { if (f.IsHidden != col.Hid) f.IsHidden = col.Hid; } catch (Exception) { }
            try { if (col.W > 0) f.GridColumnWidth = col.W; } catch (Exception) { }
        }
        try
        {
            d.ClearSortGroupFields();
            foreach (var s in look.Sorts)
            {
                if (!byParam.TryGetValue(s.P, out var f)) continue;
                var row = new ScheduleSortGroupField(f.FieldId, s.Desc ? ScheduleSortOrder.Descending : ScheduleSortOrder.Ascending) { ShowHeader = s.Header, ShowFooter = s.Footer, ShowBlankLine = s.Blank };
                try { d.AddSortGroupField(row); } catch (Exception) { }
            }
        }
        catch (Exception) { }
        try
        {
            d.ClearFilters();
            var c = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var want in look.Filters)
            {
                if (!byParam.TryGetValue(want.P, out var f) || !Enum.TryParse<ScheduleFilterType>(want.T, out var type)) continue;
                try
                {
                    d.AddFilter(want.K switch
                    {
                        "s" => new ScheduleFilter(f.FieldId, type, want.V ?? ""),
                        "d" => new ScheduleFilter(f.FieldId, type, double.Parse(want.V!, c)),
                        "i" => new ScheduleFilter(f.FieldId, type, int.Parse(want.V!, c)),
                        "e" => new ScheduleFilter(f.FieldId, type, new ElementId(long.Parse(want.V!, c))),
                        _ => new ScheduleFilter(f.FieldId, type),
                    });
                }
                catch (Exception) { }
            }
        }
        catch (Exception) { }
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
                changed |= SetFrame(view, line);
                break;
            case Viewport viewport when line.P is { } p && BoxCenter(viewport) is { } now && !M.Same(P(now), p):
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

    /// <summary>A view's crop, a 3D view's eye and section box, and what a template leaves to each view, as the line has them.</summary>
    private static bool SetFrame(View view, M.Line line)
    {
        var changed = false;
        try
        {
            if (view.IsTemplate)
            {
                if (line.Extra is { } x && x.TryGetValue("free", out var free))
                {
                    var want = free.Split([';'], StringSplitOptions.RemoveEmptyEntries).Select(t => new ElementId(long.Parse(t, System.Globalization.CultureInfo.InvariantCulture))).ToList();
                    var now = view.GetNonControlledTemplateParameterIds();
                    if (now.Count != want.Count || now.Except(want).Any()) { view.SetNonControlledTemplateParameterIds(want); changed = true; }
                }
                return changed;
            }
            if (view is View3D v3)
            {
                if (line.Box is { Eye: true, BasisY: { } up, BasisZ: { } forward } eye && !v3.IsLocked)
                {
                    var o = v3.GetOrientation();
                    if (!M.Same(P(o.EyePosition), eye.Origin) || !M.Same(P(o.UpDirection), up) || !M.Same(P(o.ForwardDirection), forward))
                    {
                        v3.SetOrientation(new ViewOrientation3D(X(eye.Origin), X(up), X(forward)));
                        changed = true;
                    }
                }
                if (line.Box is { Eye: true } box)
                {
                    if (box.SectionBox is { Min: not null, Max: not null } sb)
                    {
                        if (!v3.IsSectionBoxActive || !SameBox(BoxOf(v3.GetSectionBox()), sb)) { v3.SetSectionBox(Bounding(sb)); changed = true; }
                    }
                    else if (v3.IsSectionBoxActive) { v3.IsSectionBoxActive = false; changed = true; }
                }
            }
            else if (line.Box is { Eye: false, Min: not null, Max: not null } crop && view.CropBox is { } has && !SameBox(BoxOf(has), crop))
            {
                view.CropBox = Bounding(crop);
                changed = true;
            }
        }
        catch (Exception) { /* a view Revit won't frame this way (its template or scope box holds it): left */ }
        return changed;
    }

    private static bool SameBox(M.Box a, M.Box b) =>
        M.Same(a.Origin, b.Origin) && M.Same(a.BasisX, b.BasisX) && M.Same(a.BasisY, b.BasisY) && M.Same(a.BasisZ, b.BasisZ) && M.Same(a.Min, b.Min) && M.Same(a.Max, b.Max);

    private static M.Xyz P(XYZ p) => ChangeShapes.P(p);

    private static XYZ X(M.Xyz p) => ChangeShapes.X(p);
}
