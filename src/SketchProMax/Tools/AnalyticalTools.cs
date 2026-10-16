using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using SketchProMax.Revit;

namespace SketchProMax.Tools;

/// <summary>The analytical model as nodes, members and panels, nodes merged within a tolerance.</summary>
internal sealed class AnalyticalGraph
{
    public sealed record Member(AnalyticalMember E, int I, int J, Element? Physical);
    public sealed record Panel(AnalyticalPanel E, List<int> Nodes, Element? Physical);

    public readonly List<XYZ> Nodes = [];
    public readonly List<Member> Members = [];
    public readonly List<Panel> Panels = [];
    public readonly List<string> Skipped = [];
    private readonly Dictionary<(long, long, long), List<int>> _cells = new();
    private readonly double _tol;

    public AnalyticalGraph(Document doc, double tol)
    {
        _tol = tol;
        var links = AnalyticalToPhysicalAssociationManager.GetAnalyticalToPhysicalAssociationManager(doc);
        Element? PhysicalOf(Element a) => links?.GetAssociatedElementId(a.Id) is { } id && id != ElementId.InvalidElementId ? doc.GetElement(id) : null;
        foreach (var m in new FilteredElementCollector(doc).OfClass(typeof(AnalyticalMember)).Cast<AnalyticalMember>())
        {
            if (m.GetCurve() is not Line l) { Skipped.Add($"member {m.Id.Value}: curved members are not exported"); continue; }
            int i = Node(l.GetEndPoint(0)), j = Node(l.GetEndPoint(1));
            if (i == j) { Skipped.Add($"member {m.Id.Value}: shorter than the node tolerance"); continue; }
            Members.Add(new Member(m, i, j, PhysicalOf(m)));
        }
        foreach (var p in new FilteredElementCollector(doc).OfClass(typeof(AnalyticalPanel)).Cast<AnalyticalPanel>())
        {
            var loop = p.GetOuterContour();
            if (loop is null || loop.Any(c => c is not Line)) { Skipped.Add($"panel {p.Id.Value}: curved edges are not exported"); continue; }
            var nodes = new List<int>();
            foreach (var n in loop.Select(c => Node(c.GetEndPoint(0))))
                if (nodes.Count == 0 || nodes[nodes.Count - 1] != n) nodes.Add(n);
            if (nodes.Count > 1 && nodes[0] == nodes[nodes.Count - 1]) nodes.RemoveAt(nodes.Count - 1);
            if (nodes.Count < 3) { Skipped.Add($"panel {p.Id.Value}: fewer than 3 distinct corners"); continue; }
            if (p.GetAnalyticalOpeningsIds().Count > 0) Skipped.Add($"panel {p.Id.Value}: has openings, exported without them (add them in the analysis program)");
            Panels.Add(new Panel(p, nodes, PhysicalOf(p)));
        }
    }

    public int Node(XYZ p)
    {
        (long, long, long) Cell(XYZ q) => ((long)Math.Floor(q.X / _tol), (long)Math.Floor(q.Y / _tol), (long)Math.Floor(q.Z / _tol));
        var c = Cell(p);
        for (var x = -1; x <= 1; x++)
            for (var y = -1; y <= 1; y++)
                for (var z = -1; z <= 1; z++)
                    if (_cells.TryGetValue((c.Item1 + x, c.Item2 + y, c.Item3 + z), out var list))
                        foreach (var i in list) if (Nodes[i].DistanceTo(p) <= _tol) return i;
        Nodes.Add(p);
        if (!_cells.TryGetValue(c, out var own)) _cells[c] = own = [];
        own.Add(Nodes.Count - 1);
        return Nodes.Count - 1;
    }

    /// <summary>Nodes within reach of a point (not merged), for gap checks.</summary>
    private Dictionary<(long, long, long), List<int>>? _coarse;
    private double _coarseCell;

    public IEnumerable<int> Near(XYZ p, double reach)
    {
        // A second grid with cells the size of the reach, built once, so each query looks at 27 cells.
        if (_coarse is null || Math.Abs(_coarseCell - reach) > 1e-9)
        {
            _coarseCell = reach; _coarse = new();
            for (var i = 0; i < Nodes.Count; i++)
            {
                var k = ((long)Math.Floor(Nodes[i].X / reach), (long)Math.Floor(Nodes[i].Y / reach), (long)Math.Floor(Nodes[i].Z / reach));
                if (!_coarse.TryGetValue(k, out var l)) _coarse[k] = l = [];
                l.Add(i);
            }
        }
        var c = ((long)Math.Floor(p.X / reach), (long)Math.Floor(p.Y / reach), (long)Math.Floor(p.Z / reach));
        for (var x = -1; x <= 1; x++)
            for (var y = -1; y <= 1; y++)
                for (var z = -1; z <= 1; z++)
                    if (_coarse.TryGetValue((c.Item1 + x, c.Item2 + y, c.Item3 + z), out var list))
                        foreach (var i in list) if (Nodes[i].DistanceTo(p) <= reach) yield return i;
    }
}

public sealed class AnalyticalCheckTool : IRevitTool
{
    public string Name => "analytical_check";
    public string Description =>
        "Check the analytical model before export to ETABS, SAP2000 or RAM: member ends that connect to nothing, small gaps " +
        "between member ends and nodes (not connected), very short members, analytical elements with no physical element, " +
        "and structural columns, beams, braces, floors and walls with no analytical element. Lists ids to fix (adjust the " +
        "analytical model in Revit, or set the physical elements' analytical alignment).";
    public string PropertiesJson => """
    {
      "node_tolerance": {"type": "number", "description": "Ends closer than this are one node, project units (default 10 mm)."},
      "gap_tolerance": {"type": "number", "description": "Report ends within this of another node but not connected (default 300 mm)."},
      "min_length": {"type": "number", "description": "Flag members shorter than this (default 300 mm)."},
      "folder": {"type": "string"}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var tol = args.OptDouble("node_tolerance") is { } nt ? RevitHelpers.ToInternal(doc, nt) : 10 / 304.8;
        var gap = args.OptDouble("gap_tolerance") is { } gt ? RevitHelpers.ToInternal(doc, gt) : 300 / 304.8;
        var minLen = args.OptDouble("min_length") is { } ml ? RevitHelpers.ToInternal(doc, ml) : 300 / 304.8;
        var g = new AnalyticalGraph(doc, tol);
        if (g.Members.Count == 0 && g.Panels.Count == 0) return ToolResult.Error("The model has no analytical elements. Turn on the analytical model for the structural elements (Revit 2023+ creates them for structural columns, framing, floors and walls).");
        var findings = new List<Checks.Finding>();
        void Add(string check, Element e, string issue) => findings.Add(new Checks.Finding(check, e.Id.Value, Checks.Describe(e), issue));

        var use = new int[g.Nodes.Count];
        foreach (var m in g.Members) { use[m.I]++; use[m.J]++; }
        foreach (var p in g.Panels) foreach (var n in p.Nodes.Distinct()) use[n]++;
        // Ends that land on a panel edge or face count as connected (beams on slabs, columns on walls).
        bool OnPanel(XYZ q) => g.Panels.Any(p =>
        {
            var pts = p.Nodes.Select(i => g.Nodes[i]).ToList();
            for (var i = 0; i < pts.Count; i++)
                if (pts[i].DistanceTo(pts[(i + 1) % pts.Count]) > doc.Application.ShortCurveTolerance && Line.CreateBound(pts[i], pts[(i + 1) % pts.Count]).Distance(q) < tol * 2) return true;
            return false;
        });
        var lowest = g.Nodes.Count > 0 ? g.Nodes.Min(n => n.Z) : 0;
        foreach (var m in g.Members)
        {
            var line = (Line)m.E.GetCurve();
            if (line.Length < minLen) Add("short_members", m.E, $"{Math.Round(line.Length * 304.8)} mm long");
            foreach (var (n, end) in new[] { (m.I, 1), (m.J, 2) })
            {
                if (use[n] > 1 || g.Nodes[n].Z <= lowest + tol || OnPanel(g.Nodes[n])) continue;
                var near = g.Near(g.Nodes[n], gap).Where(o => o != n && o != m.I && o != m.J).Select(o => g.Nodes[o].DistanceTo(g.Nodes[n])).DefaultIfEmpty(double.MaxValue).Min();
                Add(near < gap ? "gaps" : "free_ends", m.E, near < gap ? $"end {end} is {Math.Round(near * 304.8)} mm from another node but not connected" : $"end {end} connects to nothing (not at a support level)");
            }
            if (m.Physical is null) Add("no_physical", m.E, "analytical member with no physical element");
        }
        foreach (var p in g.Panels.Where(p => p.Physical is null)) Add("no_physical", p.E, "analytical panel with no physical element");

        var manager = AnalyticalToPhysicalAssociationManager.GetAnalyticalToPhysicalAssociationManager(doc);
        var physical = new[] { BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_StructuralFraming }
            .SelectMany(c => new FilteredElementCollector(doc).OfCategory(c).WhereElementIsNotElementType())
            .Concat(new FilteredElementCollector(doc).OfClass(typeof(Floor)).Cast<Floor>().Where(f => f.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL)?.AsInteger() == 1))
            .Concat(new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>().Where(w => w.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT)?.AsInteger() == 1));
        foreach (var e in physical)
            if (manager?.GetAssociatedElementId(e.Id) is not { } id || id == ElementId.InvalidElementId)
                Add("no_analytical", e, "structural element with no analytical element (it won't be in the analysis)");

        var file = Checks.Write(args, $"{doc.Title} - Analytical check", findings);
        return ToolResult.Ok(new
        {
            nodes = g.Nodes.Count, members = g.Members.Count, panels = g.Panels.Count, findings = findings.Count, file,
            byCheck = findings.GroupBy(f => f.Check).Select(x => new { check = x.Key, count = x.Count() }),
            sample = findings.GroupBy(f => f.Check).SelectMany(x => x.Take(15)).Select(f => new { f.Check, f.Id, f.What, f.Issue }),
            skipped = g.Skipped.Take(20),
        });
    }
}

public sealed class ExportAnalyticalTool : IRevitTool
{
    public string Name => "export_analytical";
    public string Description =>
        "Export the analytical model for analysis: a SAP2000 text file (.s2k: joints, frames with rectangular concrete or " +
        "I-section steel properties from the Revit types, shells with their thickness, materials, base supports) plus CSV " +
        "tables of nodes, members and panels. Run analytical_check first. For ETABS, the CSiXRevit link (free from CSI) is " +
        "the reliable route; for RAM Concept, export the slab plans to DWG (export_files) and import them as layers. Loads " +
        "and combinations are not exported; apply them in the analysis program (loading_plan gives the values).";
    public string PropertiesJson => """
    {
      "supports": {"type": "string", "enum": ["fixed", "pinned"], "description": "At the lowest nodes and nodes on foundations (default fixed)."},
      "concrete": {"type": "string", "description": "Concrete material name (default C30/37, E = 33 GPa)."},
      "concrete_e": {"type": "number", "description": "Concrete E in GPa (default 33)."},
      "steel": {"type": "string", "description": "Steel material name (default S355, E = 210 GPa)."},
      "node_tolerance": {"type": "number", "description": "Project units (default 10 mm)."},
      "folder": {"type": "string"},
      "file_name": {"type": "string"}
    }
    """;
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(UIApplication app, ToolArgs args)
    {
        var doc = app.ActiveUIDocument.Document;
        var tol = args.OptDouble("node_tolerance") is { } nt ? RevitHelpers.ToInternal(doc, nt) : 10 / 304.8;
        var g = new AnalyticalGraph(doc, tol);
        if (g.Members.Count == 0 && g.Panels.Count == 0) return ToolResult.Error("The model has no analytical elements to export.");
        var concrete = args.OptString("concrete") ?? "C30/37";
        var steel = args.OptString("steel") ?? "S355";
        var ec = (args.OptDouble("concrete_e") ?? 33) * 1e6; // kN/m2
        string N(double v) => v.ToString("0.#####", CultureInfo.InvariantCulture);
        string Q(string s) => s.IndexOfAny([' ', '/', '"']) >= 0 ? "\"" + s.Replace("\"", "'") + "\"" : s;
        const double M = 0.3048;

        // Sections from the physical types.
        var sections = new Dictionary<string, string>();
        var assign = new List<(int frame, string section, string material)>();
        var warnings = new List<string>();
        double? P(Element t, params string[] names) => names.Select(n => t.LookupParameter(n)).FirstOrDefault(p => p is { StorageType: StorageType.Double } && p.AsDouble() > 0)?.AsDouble();
        double? Prop(object o, string name) => o.GetType().GetProperty(name)?.GetValue(o) is double d && d > 0 ? d : null;
        for (var i = 0; i < g.Members.Count; i++)
        {
            var phys = g.Members[i].Physical as FamilyInstance;
            var sym = phys?.Symbol;
            var section = sym?.GetStructuralSection();
            var kind = section?.GetType().Name ?? "";
            var steelMember = sym != null && (Regex.IsMatch(Measure.MaterialName(doc, phys!), "steel|metal", RegexOptions.IgnoreCase)
                                              || Regex.IsMatch(kind, "General[ICLTHSF]|IWide|IParallel|ISloped|CParallel|CSloped|LAngle|Hollow|Pipe|Round", RegexOptions.IgnoreCase));
            double? Pr(params string[] names) => section is null ? null : names.Select(n => Prop(section, n)).FirstOrDefault(v => v != null);
            string name;
            string Steel(string shape, string dims) { var key = $"{sym!.FamilyName} {sym.Name}"; if (!sections.ContainsKey(key)) sections[key] = $"SectionName={Q(key)}   Material={Q(steel)}   Shape={shape}   {dims}"; return key; }
            if (sym is null) { name = "UNKNOWN"; warnings.Add($"member {g.Members[i].E.Id.Value} has no physical element; section UNKNOWN (300x300)"); if (!sections.ContainsKey(name)) sections[name] = $"SectionName=UNKNOWN   Material={Q(concrete)}   Shape=Rectangular   t3=0.3   t2=0.3"; }
            else if (steelMember && Pr("Height") is { } h && Pr("Width") is { } w && Pr("FlangeThickness") is { } tf && Pr("WebThickness") is { } tw && Regex.IsMatch(kind, "I|H", RegexOptions.None) && !Regex.IsMatch(kind, "C|LAngle|GeneralL|Hollow|Pipe|Round"))
                name = Steel("\"I/Wide Flange\"", $"t3={N(h * M)}   t2={N(w * M)}   tf={N(tf * M)}   tw={N(tw * M)}   t2b={N(w * M)}   tfb={N(tf * M)}");
            else if (steelMember && Regex.IsMatch(kind, "GeneralC|CParallel|CSloped") && Pr("Height") is { } ch && Pr("Width") is { } cw && Pr("FlangeThickness") is { } ctf && Pr("WebThickness") is { } ctw)
                name = Steel("Channel", $"t3={N(ch * M)}   t2={N(cw * M)}   tf={N(ctf * M)}   tw={N(ctw * M)}");
            else if (steelMember && Regex.IsMatch(kind, "GeneralL|LAngle") && Pr("Height") is { } lh && Pr("Width") is { } lw && Pr("FlangeThickness", "Thickness") is { } lt)
                name = Steel("Angle", $"t3={N(lh * M)}   t2={N(lw * M)}   tf={N(lt * M)}   tw={N((Pr("WebThickness") ?? lt) * M)}");
            else if (steelMember && Regex.IsMatch(kind, "Pipe|Round", RegexOptions.IgnoreCase) && Pr("Diameter") is { } pd && Pr("WallNominalThickness", "WallDesignThickness") is { } pt)
                name = Steel("Pipe", $"t3={N(pd * M)}   tw={N(pt * M)}");
            else if (steelMember && Regex.IsMatch(kind, "Hollow|Rectangle", RegexOptions.IgnoreCase) && Pr("Height") is { } bh && Pr("Width") is { } bw && Pr("WallNominalThickness", "WallDesignThickness") is { } bt)
                name = Steel("Box/Tube", $"t3={N(bh * M)}   t2={N(bw * M)}   tf={N(bt * M)}   tw={N(bt * M)}");
            else if (P(sym, "b", "Width", "B") is { } b && P(sym, "h", "Depth", "H", "Height") is { } d)
            {
                name = $"R{Math.Round(b * 304.8)}x{Math.Round(d * 304.8)}{(steelMember ? "-S" : "")}";
                if (!sections.ContainsKey(name)) sections[name] = $"SectionName={name}   Material={Q(steelMember ? steel : concrete)}   Shape=Rectangular   t3={N(d * M)}   t2={N(b * M)}";
            }
            else if (P(sym, "Diameter", "D") is { } dia)
            {
                name = $"C{Math.Round(dia * 304.8)}{(steelMember ? "-S" : "")}";
                if (!sections.ContainsKey(name)) sections[name] = $"SectionName={name}   Material={Q(steelMember ? steel : concrete)}   Shape=Circle   t3={N(dia * M)}";
            }
            else
            {
                name = "UNKNOWN";
                warnings.Add($"{sym.FamilyName}: {sym.Name} has no size parameters; exported as UNKNOWN (300x300), set it in the analysis program");
                if (!sections.ContainsKey(name)) sections[name] = $"SectionName=UNKNOWN   Material={Q(concrete)}   Shape=Rectangular   t3=0.3   t2=0.3";
            }
            assign.Add((i + 1, name, steelMember ? steel : concrete));
        }
        var areaSections = new Dictionary<string, string>();
        var areaAssign = new List<(int area, string section)>();
        for (var i = 0; i < g.Panels.Count; i++)
        {
            var phys = g.Panels[i].Physical;
            var t = phys is Floor f ? f.FloorType.GetCompoundStructure()?.GetWidth() : phys is Wall w ? w.Width : null;
            if (t is null) warnings.Add($"panel {g.Panels[i].E.Id.Value}: no physical thickness; 200 mm used");
            var th = t ?? 200 / 304.8;
            var name = $"{(phys is Wall ? "WALL" : "SLAB")}{Math.Round(th * 304.8)}";
            if (!areaSections.ContainsKey(name)) areaSections[name] = $"Section={name}   Material={Q(concrete)}   AreaType=Shell   Type=Shell-Thin   Thickness={N(th * M)}   BendThick={N(th * M)}";
            areaAssign.Add((i + 1, name));
        }

        var sb = new StringBuilder();
        sb.AppendLine($"$ File exported from Revit ({doc.Title}) by Sketch ProMax on {DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}");
        sb.AppendLine().AppendLine("TABLE:  \"PROGRAM CONTROL\"").AppendLine("   ProgramName=SAP2000   Version=22.0.0   CurrUnits=\"KN, m, C\"");
        sb.AppendLine().AppendLine("TABLE:  \"MATERIAL PROPERTIES 01 - GENERAL\"");
        sb.AppendLine($"   Material={Q(concrete)}   Type=Concrete   SymType=Isotropic");
        sb.AppendLine($"   Material={Q(steel)}   Type=Steel   SymType=Isotropic");
        sb.AppendLine().AppendLine("TABLE:  \"MATERIAL PROPERTIES 02 - BASIC MECHANICAL PROPERTIES\"");
        sb.AppendLine($"   Material={Q(concrete)}   UnitWeight=24.5   UnitMass=2.4983   E1={N(ec)}   G12={N(ec / 2.4)}   U12=0.2   A1=1E-05");
        sb.AppendLine($"   Material={Q(steel)}   UnitWeight=76.9729   UnitMass=7.849   E1=210000000   G12=80769231   U12=0.3   A1=1.2E-05");
        sb.AppendLine().AppendLine("TABLE:  \"JOINT COORDINATES\"");
        for (var i = 0; i < g.Nodes.Count; i++) sb.AppendLine($"   Joint={i + 1}   CoordSys=GLOBAL   CoordType=Cartesian   XorR={N(g.Nodes[i].X * M)}   Y={N(g.Nodes[i].Y * M)}   Z={N(g.Nodes[i].Z * M)}");
        if (g.Members.Count > 0)
        {
            sb.AppendLine().AppendLine("TABLE:  \"CONNECTIVITY - FRAME\"");
            for (var i = 0; i < g.Members.Count; i++) sb.AppendLine($"   Frame={i + 1}   JointI={g.Members[i].I + 1}   JointJ={g.Members[i].J + 1}");
            sb.AppendLine().AppendLine("TABLE:  \"FRAME SECTION PROPERTIES 01 - GENERAL\"");
            foreach (var s in sections.Values) sb.AppendLine("   " + s);
            sb.AppendLine().AppendLine("TABLE:  \"FRAME SECTION ASSIGNMENTS\"");
            foreach (var (frame, section, _) in assign) sb.AppendLine($"   Frame={frame}   AutoSelect=N.A.   AnalSect={Q(section)}   MatProp=Default");
            // Section rotation: columns from their plan rotation, beams from their cross-section rotation.
            var angles = new List<string>();
            for (var i = 0; i < g.Members.Count; i++)
            {
                if (g.Members[i].Physical is not FamilyInstance fi) continue;
                double deg = 0;
                if (fi.Category?.Id.Value == (long)BuiltInCategory.OST_StructuralColumns) deg = Math.Atan2(fi.HandOrientation.Y, fi.HandOrientation.X) * 180 / Math.PI;
                else if (fi.get_Parameter(BuiltInParameter.STRUCTURAL_BEND_DIR_ANGLE) is { StorageType: StorageType.Double } bend) deg = bend.AsDouble() * 180 / Math.PI;
                if (Math.Abs(deg) > 0.01) angles.Add($"   Frame={i + 1}   Angle={N(deg)}   AdvanceAxes=No");
            }
            if (angles.Count > 0) { sb.AppendLine().AppendLine("TABLE:  \"FRAME LOCAL AXES ASSIGNMENTS 1 - TYPICAL\""); foreach (var a in angles) sb.AppendLine(a); }
        }
        if (g.Panels.Count > 0)
        {
            sb.AppendLine().AppendLine("TABLE:  \"CONNECTIVITY - AREA\"");
            for (var i = 0; i < g.Panels.Count; i++)
                sb.AppendLine($"   Area={i + 1}   NumJoints={g.Panels[i].Nodes.Count}   " + string.Join("   ", g.Panels[i].Nodes.Select((n, k) => $"Joint{k + 1}={n + 1}")));
            sb.AppendLine().AppendLine("TABLE:  \"AREA SECTION PROPERTIES\"");
            foreach (var s in areaSections.Values) sb.AppendLine("   " + s);
            sb.AppendLine().AppendLine("TABLE:  \"AREA SECTION ASSIGNMENTS\"");
            foreach (var (area, section) in areaAssign) sb.AppendLine($"   Area={area}   Section={section}");
        }
        var lowest = g.Nodes.Min(n => n.Z);
        var fixedBase = (args.OptString("supports") ?? "fixed") == "fixed";
        // Supports: the lowest nodes, and nodes sitting on a foundation (stepped footings, pile caps).
        var footings = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StructuralFoundation).WhereElementIsNotElementType().Select(e => e.get_BoundingBox(null)).Where(b => b != null).ToList();
        var supports = new HashSet<int>(Enumerable.Range(0, g.Nodes.Count).Where(i => g.Nodes[i].Z <= lowest + tol
            || footings.Any(b => b!.Min.X - tol <= g.Nodes[i].X && g.Nodes[i].X <= b.Max.X + tol && b.Min.Y - tol <= g.Nodes[i].Y && g.Nodes[i].Y <= b.Max.Y + tol && Math.Abs(g.Nodes[i].Z - b.Max.Z) < 500 / 304.8)));
        sb.AppendLine().AppendLine("TABLE:  \"JOINT RESTRAINT ASSIGNMENTS\"");
        foreach (var i in supports) sb.AppendLine($"   Joint={i + 1}   U1=Yes   U2=Yes   U3=Yes   R1={(fixedBase ? "Yes" : "No")}   R2={(fixedBase ? "Yes" : "No")}   R3={(fixedBase ? "Yes" : "No")}");
        sb.AppendLine().AppendLine("END TABLE DATA");

        var folder = Checks.Folder(args);
        Directory.CreateDirectory(folder);
        var baseName = OutputPaths.Checked(args.OptString("file_name")) ?? $"{doc.Title} - Analytical {DateTime.Now.ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture)}";
        var s2k = Path.Combine(folder, baseName + ".s2k");
        File.WriteAllText(s2k, sb.ToString(), new UTF8Encoding(false));

        var nodes = new StringBuilder("Node,X (m),Y (m),Z (m),Support\n");
        for (var i = 0; i < g.Nodes.Count; i++) nodes.AppendLine($"{i + 1},{N(g.Nodes[i].X * M)},{N(g.Nodes[i].Y * M)},{N(g.Nodes[i].Z * M)},{(supports.Contains(i) ? (fixedBase ? "fixed" : "pinned") : "")}");
        var members = new StringBuilder("Frame,Node I,Node J,Section,Material,Revit analytical id,Revit physical id\n");
        for (var i = 0; i < g.Members.Count; i++) members.AppendLine($"{i + 1},{g.Members[i].I + 1},{g.Members[i].J + 1},{Checks.Csv(assign[i].section)},{Checks.Csv(assign[i].material)},{g.Members[i].E.Id.Value},{g.Members[i].Physical?.Id.Value}");
        var panels = new StringBuilder("Area,Nodes,Section,Revit analytical id,Revit physical id\n");
        for (var i = 0; i < g.Panels.Count; i++) panels.AppendLine($"{i + 1},{Checks.Csv(string.Join(" ", g.Panels[i].Nodes.Select(n => n + 1)))},{areaAssign[i].section},{g.Panels[i].E.Id.Value},{g.Panels[i].Physical?.Id.Value}");
        File.WriteAllText(Path.Combine(folder, baseName + " - nodes.csv"), nodes.ToString(), new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(folder, baseName + " - frames.csv"), members.ToString(), new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(folder, baseName + " - areas.csv"), panels.ToString(), new UTF8Encoding(true));
        return ToolResult.Ok(new
        {
            s2k, csvFolder = folder, joints = g.Nodes.Count, frames = g.Members.Count, areas = g.Panels.Count, supports = supports.Count,
            frameSections = sections.Keys, areaSections = areaSections.Keys, warnings = warnings.Distinct().Take(30), skipped = g.Skipped.Take(20),
            note = "Open in SAP2000 with File > Import > SAP2000 .s2k. Areas are not meshed; mesh them in SAP2000. Check sections, materials and supports, then add loads and combinations.",
        });
    }
}
