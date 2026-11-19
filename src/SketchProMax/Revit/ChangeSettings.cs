using System.Globalization;
using System.IO;
using System.Text;
using Autodesk.Revit.DB;
using M = SketchProMax.Revit.ChangeFileMath;

namespace SketchProMax.Revit;

/// <summary>
/// Lifeline's part for settings that live in the model and aren't drawn anywhere: revisions, global parameters,
/// project (shared) parameters, the starting view, shared sites, the site's place on Earth, the revision numbering,
/// the room area and volume settings and the project units. Each is written with what it
/// takes to make or set it again; a replay makes or sets it. (Print and export setups are kept as copies, see
/// <see cref="ChangeKeeper.SetupKind"/>; sheet sets are in <see cref="ChangeExtras"/>.)
/// </summary>
internal static class ChangeSettings
{
    private static string Get(Dictionary<string, string> x, string key) => x.TryGetValue(key, out var value) ? value : "";
    private static string R(double d) => M.Round(d).ToString("R", CultureInfo.InvariantCulture);
    private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    /// <summary>What kind of setting the element is (its line's What); null for anything else.</summary>
    public static string? Kind(Element e) => e switch
    {
        Revision => "revision", GlobalParameter => "global", StartingViewSettings => "startview", ParameterElement => "param", ProjectLocation => "site", SunAndShadowSettings sun when IsSunPreset(sun) => "sun",
        SiteLocation => "place", RevisionSettings => "revsetting", AreaVolumeSettings => "areavol", _ => null,
    };

    public static bool Owns(M.Line line) => M.IsSetting(line);

    /// <summary>
    /// What Revit gives no way to make from a line: a sun setting, and a project parameter that isn't shared. A new
    /// one is kept as a copy (it comes back with its categories); one that was in the model is set from its line.
    /// </summary>
    public static bool MadeFromCopy(Element e) => e is SunAndShadowSettings sun && IsSunPreset(sun) || e is ParameterElement and not SharedParameterElement and not GlobalParameter;

    /// <summary>
    /// A sun setting saved under a name (a preset). The unnamed ones ("&lt;In-session, …&gt;") are each view's own:
    /// Revit changes them by itself as views are opened, so they get no line.
    /// </summary>
    private static bool IsSunPreset(SunAndShadowSettings sun)
    {
        try { return sun.Name is { Length: > 0 } name && !name.StartsWith("<", StringComparison.Ordinal); } catch (Exception) { return false; }
    }

    /// <summary>A parameter that is a project parameter (bound to categories); one that only came with a family has no line.</summary>
    public static bool Bound(Document doc, ParameterElement shared)
    {
        try { return doc.ParameterBindings.get_Item(shared.GetDefinition()) is ElementBinding; } catch (Exception) { return false; }
    }

    /// <summary>Writes what it takes to make or set the setting again into the line.</summary>
    public static void Read(Document doc, Element element, M.Line line)
    {
        var x = line.Extra ??= [];
        try
        {
            switch (element)
            {
                case Revision revision:
                    x["rdesc"] = revision.Description ?? "";
                    x["rdate"] = revision.RevisionDate ?? "";
                    x["rby"] = revision.IssuedBy ?? "";
                    x["rto"] = revision.IssuedTo ?? "";
                    x["rissued"] = revision.Issued ? "1" : "0";
                    x["rvis"] = revision.Visibility.ToString();
                    line.Name ??= revision.Description;
                    break;
                case GlobalParameter global:
                    line.Name = global.GetDefinition().Name;
                    x["spec"] = global.GetDefinition().GetDataType().TypeId;
                    x["greport"] = global.IsReporting ? "1" : "0";
                    if (global.GetFormula() is { Length: > 0 } formula) x["gformula"] = formula;
                    else if (Value(doc, global.GetValue()) is { } value) x["gval"] = value;
                    break;
                case StartingViewSettings start:
                    line.Name = "Starting view";
                    x["view"] = doc.GetElement(start.ViewId)?.UniqueId ?? "";
                    break;
                case ParameterElement shared:
                    var definition = shared.GetDefinition();
                    line.Name = definition.Name;
                    if (shared is SharedParameterElement withGuid) x["pguid"] = withGuid.GuidValue.ToString();
                    x["spec"] = definition.GetDataType().TypeId;
                    x["pgroup"] = definition.GetGroupTypeId()?.TypeId ?? "";
                    x["pvary"] = definition.VariesAcrossGroups ? "1" : "0";
                    if (doc.ParameterBindings.get_Item(definition) is ElementBinding binding)
                    {
                        x["pinst"] = binding is InstanceBinding ? "1" : "0";
                        x["pcats"] = string.Join(";", binding.Categories.Cast<Category>().Select(c => c.Id.Value).OrderBy(c => c).Select(c => c.ToString(CultureInfo.InvariantCulture)));
                    }
                    break;
                case SunAndShadowSettings sun:
                    foreach (var pair in SunValues(doc, sun)) x[pair.Key] = pair.Value;
                    break;
                case ProjectLocation site:
                    var at = site.GetProjectPosition(XYZ.Zero);
                    x["pos"] = string.Join(";", R(at.EastWest), R(at.NorthSouth), R(at.Elevation), R(at.Angle));
                    break;
                case SiteLocation place:
                    line.Name = "Site location";
                    x["place"] = string.Join(";", place.Latitude.ToString("R", CultureInfo.InvariantCulture), place.Longitude.ToString("R", CultureInfo.InvariantCulture), place.TimeZone.ToString("R", CultureInfo.InvariantCulture));
                    x["placename"] = place.PlaceName ?? "";
                    break;
                case RevisionSettings revisions:
                    line.Name = "Revision settings";
                    x["rnumbering"] = revisions.RevisionNumbering.ToString();
                    x["rspacing"] = R(revisions.RevisionCloudSpacing);
                    break;
                case AreaVolumeSettings rooms:
                    line.Name = "Area and volume settings";
                    x["volumes"] = rooms.ComputeVolumes ? "1" : "0";
                    x["roomedge"] = rooms.GetSpatialElementBoundaryLocation(SpatialElementType.Room).ToString();
                    break;
            }
        }
        catch (Exception) { /* what could be read is written */ }
    }

    private static readonly string[] SunKeys = ["stype", "srel", "sground", "slevel", "sstart", "send", "sstep", "saz", "salt"];

    /// <summary>A sun setting's own values, each read by itself (which ones Revit has depends on the kind of study).</summary>
    private static List<KeyValuePair<string, string>> SunValues(Document doc, SunAndShadowSettings sun)
    {
        var values = new List<KeyValuePair<string, string>>();
        void Add(string key, Func<string?> read) { try { if (read() is { } value) values.Add(new(key, value)); } catch (Exception) { } }
        Add("stype", () => sun.SunAndShadowType.ToString());
        Add("srel", () => sun.RelativeToView ? "1" : "0");
        Add("sground", () => sun.UsesGroundPlane ? "1" : "0");
        Add("slevel", () => doc.GetElement(sun.GroundPlaneLevelId)?.UniqueId);
        Add("sstart", () => sun.StartDateAndTime.ToString("o", CultureInfo.InvariantCulture));
        Add("send", () => sun.EndDateAndTime.ToString("o", CultureInfo.InvariantCulture));
        Add("sstep", () => sun.TimeInterval.ToString());
        Add("saz", () => R(sun.Azimuth));
        Add("salt", () => R(sun.Altitude));
        return values;
    }

    private static string? Value(Document doc, ParameterValue? value) => value switch
    {
        DoubleParameterValue d => "d:" + R(d.Value),
        IntegerParameterValue i => "i:" + i.Value.ToString(CultureInfo.InvariantCulture),
        StringParameterValue s => "s:" + s.Value,
        ElementIdParameterValue e => "e:" + (doc.GetElement(e.Value)?.UniqueId ?? ""),
        _ => null,
    };

    /// <summary>Null when the setting can be made again from the line, else why not.</summary>
    public static string? CanMake(M.Line line)
    {
        var x = line.Extra;
        return line.What switch
        {
            "revision" => null,
            "global" => line.Name is { Length: > 0 } && x is not null && x.ContainsKey("spec") ? null : "a global parameter whose kind isn't in the file: make it again by hand",
            "param" => line.Name is { Length: > 0 } && x is not null && x.ContainsKey("pguid") && x.ContainsKey("pcats") ? null
                : x is not null && x.ContainsKey("pcats") ? "a project parameter that isn't shared, made after the last save, whose copy wasn't kept: add it again by hand"
                : "a project parameter whose categories aren't in the file: add it again by hand",
            "sun" => "a sun setting made after the last save whose copy wasn't kept: make it again by hand",
            "site" => line.Name is { Length: > 0 } && x is not null && x.ContainsKey("pos") ? null : "a shared site whose position isn't in the file: make it again by hand",
            _ => "this setting can't be made from the file: set it again by hand",
        };
    }

    /// <summary>Makes the setting of the line again. Call inside a transaction.</summary>
    public static Element Make(Document doc, M.Line line, Func<string?, Element?> known)
    {
        var x = line.Extra ?? [];
        switch (line.What)
        {
            case "revision":
            {
                var revision = Revision.Create(doc);
                Set(doc, revision, line, known);
                return revision;
            }
            case "global":
            {
                var name = line.Name!;
                if (GlobalParametersManager.FindByName(doc, name) is { } id && id != ElementId.InvalidElementId && doc.GetElement(id) is GlobalParameter there) { Set(doc, there, line, known); return there; }
                var global = GlobalParameter.Create(doc, name, new ForgeTypeId(x["spec"]));
                Set(doc, global, line, known);
                return global;
            }
            case "param":
            {
                var guid = Guid.Parse(x["pguid"]);
                if (SharedParameterElement.Lookup(doc, guid) is { } have) { Bind(doc, have.GetDefinition(), x, again: true); return have; }
                var app = doc.Application;
                var old = app.SharedParametersFilename;
                var temp = Path.Combine(Path.GetTempPath(), "spm-lifeline-" + System.Guid.NewGuid().ToString("N") + ".txt");
                try
                {
                    File.WriteAllText(temp, "");
                    app.SharedParametersFilename = temp;
                    var file = app.OpenSharedParameterFile() ?? throw new InvalidOperationException("Revit couldn't open a shared parameter file for it: add the project parameter again by hand");
                    var definition = file.Groups.Create("Lifeline").Definitions.Create(new ExternalDefinitionCreationOptions(line.Name!, new ForgeTypeId(x["spec"])) { GUID = guid });
                    Bind(doc, definition, x, again: false);
                }
                finally
                {
                    try { app.SharedParametersFilename = old ?? ""; } catch (Exception) { }
                    try { File.Delete(temp); } catch (Exception) { }
                }
                var made = SharedParameterElement.Lookup(doc, guid) ?? throw new InvalidOperationException("Revit didn't add the project parameter: add it again by hand");
                try { if (x.TryGetValue("pvary", out var vary) && vary == "1") made.GetDefinition().SetAllowVaryBetweenGroups(doc, true); } catch (Exception) { }
                return made;
            }
            case "site":
            {
                var site = new FilteredElementCollector(doc).OfClass(typeof(ProjectLocation)).Cast<ProjectLocation>().FirstOrDefault(l => l.Name == line.Name) ?? doc.ActiveProjectLocation.Duplicate(line.Name!);
                Set(doc, site, line, known);
                return site;
            }
        }
        throw new InvalidOperationException("this setting can't be made from the file: set it again by hand");
    }

    private static void Bind(Document doc, Definition definition, Dictionary<string, string> x, bool again)
    {
        var categories = doc.Application.Create.NewCategorySet();
        foreach (var id in x["pcats"].Split(';').Where(s => s.Length > 0))
            try { if (Category.GetCategory(doc, new ElementId(long.Parse(id, CultureInfo.InvariantCulture))) is { AllowsBoundParameters: true } category) categories.Insert(category); } catch (Exception) { }
        if (categories.IsEmpty) throw new InvalidOperationException("none of the project parameter's categories is in this model: add it again by hand");
        ElementBinding binding = x.TryGetValue("pinst", out var inst) && inst == "0" ? doc.Application.Create.NewTypeBinding(categories) : doc.Application.Create.NewInstanceBinding(categories);
        // A parameter under no group ("Other") is bound without one: Revit refuses an empty group.
        var group = x.TryGetValue("pgroup", out var g) && g.Length > 0 ? new ForgeTypeId(g) : null;
        bool Insert() => group is null ? doc.ParameterBindings.Insert(definition, binding) : doc.ParameterBindings.Insert(definition, binding, group);
        var done = !again ? Insert() : group is null ? doc.ParameterBindings.ReInsert(definition, binding) : doc.ParameterBindings.ReInsert(definition, binding, group);
        if (!done && !(again && Insert())) throw new InvalidOperationException("Revit refused the project parameter's categories: add it again by hand");
    }

    /// <summary>Sets the setting that is in the model to what its line says; true when anything changed. Call inside a transaction.</summary>
    public static bool Set(Document doc, Element element, M.Line line, Func<string?, Element?> known)
    {
        var x = line.Extra;
        if (x is null) return false;
        var changed = false;
        switch (element)
        {
            case Revision revision when x.ContainsKey("rdesc"):
            {
                var issued = x.TryGetValue("rissued", out var i) && i == "1";
                bool Differs() => revision.Description != x["rdesc"] || revision.RevisionDate != Get(x, "rdate") || revision.IssuedBy != Get(x, "rby") || revision.IssuedTo != Get(x, "rto");
                if (Differs())
                {
                    // An issued revision can't be edited: it is un-issued for the moment.
                    if (revision.Issued) revision.Issued = false;
                    revision.Description = x["rdesc"];
                    revision.RevisionDate = Get(x, "rdate");
                    revision.IssuedBy = Get(x, "rby");
                    revision.IssuedTo = Get(x, "rto");
                    changed = true;
                }
                if (x.TryGetValue("rvis", out var vis) && Enum.TryParse<RevisionVisibility>(vis, out var visibility) && revision.Visibility != visibility)
                {
                    if (revision.Issued) revision.Issued = false;
                    revision.Visibility = visibility;
                    changed = true;
                }
                if (revision.Issued != issued) { revision.Issued = issued; changed = true; }
                return changed;
            }
            case GlobalParameter global:
            {
                if (x.TryGetValue("gformula", out var formula) && formula.Length > 0)
                {
                    if (global.GetFormula() != formula) { global.SetFormula(formula); changed = true; }
                    return changed;
                }
                if (!x.TryGetValue("gval", out var text) || text.Length < 2) return false;
                if (Value(doc, global.GetValue()) == text) return false;
                if (global.GetFormula() is { Length: > 0 }) global.SetFormula(null);
                var body = text.Substring(2);
                ParameterValue? value = text[0] switch
                {
                    'd' => new DoubleParameterValue(D(body)),
                    'i' => new IntegerParameterValue(int.Parse(body, CultureInfo.InvariantCulture)),
                    's' => new StringParameterValue(body),
                    'e' => known(body) is { } target ? new ElementIdParameterValue(target.Id) : null,
                    _ => null,
                };
                if (value is null) return false;
                global.SetValue(value);
                return true;
            }
            case StartingViewSettings start when x.TryGetValue("view", out var unique):
            {
                var id = unique.Length == 0 ? ElementId.InvalidElementId : known(unique)?.Id;
                if (id is null || start.ViewId == id) return false;
                start.ViewId = id;
                return true;
            }
            case SunAndShadowSettings sun:
            {
                var now = SunValues(doc, sun).ToDictionary(v => v.Key, v => v.Value);
                // The kind of study first: it decides which of the others Revit takes.
                foreach (var key in SunKeys)
                {
                    if (!x.TryGetValue(key, out var want) || (now.TryGetValue(key, out var have) && have == want)) continue;
                    try
                    {
                        switch (key)
                        {
                            case "stype": if (Enum.TryParse<SunAndShadowType>(want, out var type)) sun.SunAndShadowType = type; break;
                            case "srel": sun.RelativeToView = want == "1"; break;
                            case "sground": sun.UsesGroundPlane = want == "1"; break;
                            case "slevel": if (known(want) is Level level) sun.GroundPlaneLevelId = level.Id; break;
                            case "sstart": sun.StartDateAndTime = DateTime.Parse(want, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind); break;
                            case "send": sun.EndDateAndTime = DateTime.Parse(want, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind); break;
                            case "sstep": if (Enum.TryParse<SunStudyTimeInterval>(want, out var step)) sun.TimeInterval = step; break;
                            case "saz": sun.Azimuth = D(want); break;
                            case "salt": sun.Altitude = D(want); break;
                        }
                        changed = true;
                    }
                    catch (Exception) { /* Revit takes this one only for another kind of study */ }
                }
                return changed;
            }
            case ParameterElement shared and not GlobalParameter when x.ContainsKey("pcats"):
            {
                var before = new M.Line { Extra = [] };
                Read(doc, shared, before);
                if (Get(before.Extra!, "pcats") == x["pcats"] && Get(before.Extra, "pinst") == Get(x, "pinst") && Get(before.Extra, "pgroup") == Get(x, "pgroup")) return false;
                Bind(doc, shared.GetDefinition(), x, again: true);
                return true;
            }
            case ProjectLocation site when x.TryGetValue("pos", out var pos):
            {
                var p = pos.Split(';');
                if (p.Length != 4) return false;
                var now = site.GetProjectPosition(XYZ.Zero);
                if (M.Same(now.EastWest, D(p[0])) && M.Same(now.NorthSouth, D(p[1])) && M.Same(now.Elevation, D(p[2])) && M.Same(now.Angle, D(p[3]))) return false;
                site.SetProjectPosition(XYZ.Zero, new ProjectPosition(D(p[0]), D(p[1]), D(p[2]), D(p[3])));
                return true;
            }
            case SiteLocation place when x.TryGetValue("place", out var where):
            {
                var p = where.Split(';');
                if (p.Length != 3) return false;
                if (place.Latitude != D(p[0])) { place.Latitude = D(p[0]); changed = true; }
                if (place.Longitude != D(p[1])) { place.Longitude = D(p[1]); changed = true; }
                if (place.TimeZone != D(p[2])) { place.TimeZone = D(p[2]); changed = true; }
                if (x.TryGetValue("placename", out var called) && called.Length > 0 && place.PlaceName != called) { place.PlaceName = called; changed = true; }
                return changed;
            }
            case RevisionSettings revisions:
            {
                if (x.TryGetValue("rnumbering", out var n) && Enum.TryParse<RevisionNumbering>(n, out var numbering) && revisions.RevisionNumbering != numbering) { revisions.RevisionNumbering = numbering; changed = true; }
                if (x.TryGetValue("rspacing", out var sp) && !M.Same(revisions.RevisionCloudSpacing, D(sp))) { revisions.RevisionCloudSpacing = D(sp); changed = true; }
                return changed;
            }
            case AreaVolumeSettings rooms:
            {
                if (x.TryGetValue("volumes", out var v) && rooms.ComputeVolumes != (v == "1")) { rooms.ComputeVolumes = v == "1"; changed = true; }
                if (x.TryGetValue("roomedge", out var edge) && Enum.TryParse<SpatialElementBoundaryLocation>(edge, out var location) && rooms.GetSpatialElementBoundaryLocation(SpatialElementType.Room) != location)
                {
                    rooms.SetSpatialElementBoundaryLocation(location, SpatialElementType.Room);
                    changed = true;
                }
                return changed;
            }
        }
        return false;
    }

    // ---- Project units ---------------------------------------------------------------------------------------------------

    /// <summary>The project units as text: the decimal and grouping symbols, then each unit's format (one per line).</summary>
    public static string? Units(Document doc)
    {
        try
        {
            var units = doc.GetUnits();
            var text = new StringBuilder();
            text.Append(units.DecimalSymbol).Append('|').Append(units.DigitGroupingSymbol).Append('|').Append(units.DigitGroupingAmount);
            foreach (var spec in UnitUtils.GetAllMeasurableSpecs().OrderBy(s => s.TypeId, StringComparer.Ordinal))
            {
                FormatOptions f;
                try { f = units.GetFormatOptions(spec); } catch (Exception) { continue; }
                text.Append('\n').Append(M.UnitFormat(spec.TypeId, f.GetUnitTypeId().TypeId, f.Accuracy, f.GetSymbolTypeId().TypeId,
                    (f.SuppressTrailingZeros ? 1 : 0) | (f.SuppressLeadingZeros ? 2 : 0) | (f.UsePlusPrefix ? 4 : 0) | (f.UseDigitGrouping ? 8 : 0) | (f.SuppressSpaces ? 16 : 0), f.RoundingMethod.ToString()));
            }
            return text.ToString();
        }
        catch (Exception) { return null; }
    }

    /// <summary>Sets the project units that differ from <paramref name="text"/>; true when anything changed. Call inside a transaction.</summary>
    public static bool SetUnits(Document doc, string text)
    {
        var now = Units(doc);
        if (now is null || now == text) return false;
        var have = new HashSet<string>(now.Split('\n'), StringComparer.Ordinal);
        var rows = text.Split('\n');
        var units = doc.GetUnits();
        var changed = false;
        var head = rows[0].Split('|');
        if (head.Length == 3 && !have.Contains(rows[0]))
        {
            try
            {
                if (Enum.TryParse<DecimalSymbol>(head[0], out var dec)) units.DecimalSymbol = dec;
                if (Enum.TryParse<DigitGroupingSymbol>(head[1], out var grouping)) units.DigitGroupingSymbol = grouping;
                if (Enum.TryParse<DigitGroupingAmount>(head[2], out var amount)) units.DigitGroupingAmount = amount;
                changed = true;
            }
            catch (Exception) { }
        }
        foreach (var row in rows.Skip(1).Where(r => !have.Contains(r)))
        {
            if (M.ParseUnitFormat(row) is not { } u) continue;
            try
            {
                var spec = new ForgeTypeId(u.Spec);
                var format = new FormatOptions(new ForgeTypeId(u.Unit)) { Accuracy = u.Accuracy };
                if (u.Symbol.Length > 0) try { format.SetSymbolTypeId(new ForgeTypeId(u.Symbol)); } catch (Exception) { }
                try { format.SuppressTrailingZeros = (u.Flags & 1) != 0; } catch (Exception) { }
                try { format.SuppressLeadingZeros = (u.Flags & 2) != 0; } catch (Exception) { }
                try { format.UsePlusPrefix = (u.Flags & 4) != 0; } catch (Exception) { }
                try { format.UseDigitGrouping = (u.Flags & 8) != 0; } catch (Exception) { }
                try { format.SuppressSpaces = (u.Flags & 16) != 0; } catch (Exception) { }
                if (Enum.TryParse<RoundingMethod>(u.Rounding, out var rounding)) try { format.RoundingMethod = rounding; } catch (Exception) { }
                units.SetFormatOptions(spec, format);
                changed = true;
            }
            catch (Exception) { }
        }
        if (changed) doc.SetUnits(units);
        return changed;
    }
}
