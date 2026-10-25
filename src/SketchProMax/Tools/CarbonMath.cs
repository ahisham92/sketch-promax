using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace SketchProMax.Tools;

/// <summary>
/// Embodied carbon arithmetic, with no Revit: the factor table (the built-in generic one and the user's own), matching
/// a material to a factor, units, the life cycle modules counted (EN 15978 / RICS: A1-A3 product, A4 transport, A5 site
/// waste), totals, intensity per m² and the comparison of two results. embodied_carbon, carbon_compare and carbon_view
/// measure the model and call these; the tests run them directly.
/// </summary>
internal static class Carbon
{
    public const string ResourceName = "SketchProMax.carbon-factors.csv";

    /// <summary>One row of a factor table: kgCO2e per unit of a material (or a transport mode, distance or waste rate).</summary>
    public sealed class Factor
    {
        public string Id { get; set; } = "";
        public string Class { get; set; } = "";
        public string Match { get; set; } = "";
        public string Grade { get; set; } = "";
        public string Replacement { get; set; } = "";
        public string Unit { get; set; } = "kg";
        public double Value { get; set; }
        public double? Density { get; set; }
        public string Stage { get; set; } = "A1-A3";
        public string Source { get; set; } = "";
        public string Note { get; set; } = "";
        public bool Custom { get; set; }
    }

    /// <summary>A parsed table: material factors and the A4/A5 scenario rows, with the rows that could not be read.</summary>
    public sealed class Table
    {
        public string Version = "";
        public List<Factor> Materials = [];
        public List<Factor> Modes = [];
        public List<Factor> Distances = [];
        public List<Factor> Waste = [];
        public List<string> Problems = [];
    }

    /// <summary>What the assessment counts and assumes. Defaults: A1-A3 only, CEM I (no replacement, the conservative value) where the name doesn't say.</summary>
    public sealed class Options
    {
        public bool A4, A5;
        public double? A4DistanceKm;
        public string A4Mode = "road";
        public double? WastePercent;
        /// <summary>Cement replacement variant for concrete whose name doesn't give one: none, fa25 or ggbs50.</summary>
        public string Replacement = "none";
        /// <summary>Use <see cref="Replacement"/> for all concrete, even where the name gives another.</summary>
        public bool ReplacementAll;
        public string DefaultGrade = "C32/40";
        /// <summary>Strength class swaps (from → to), by cylinder strength, e.g. C40/50 → C32/40.</summary>
        public Dictionary<double, double> GradeSwap = [];
    }

    // ---------------------------------------------------------------- tables

    /// <summary>The built-in generic table shipped inside the add-in.</summary>
    public static Table BuiltIn()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
                      ?? throw new InvalidOperationException("The built-in carbon factor table is missing from the add-in.");
        using var r = new StreamReader(s, Encoding.UTF8);
        return Parse(r.ReadToEnd(), custom: false);
    }

    public static string BuiltInText()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)!;
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    /// <summary>A table from CSV text (lines starting with # are comments; "# version: x" names the version).</summary>
    public static Table Parse(string csv, bool custom)
    {
        var lines = csv.Replace("\r\n", "\n").Split('\n');
        var version = lines.Select(l => Regex.Match(l, @"^\s*#\s*version\s*:\s*(.+)$", RegexOptions.IgnoreCase)).FirstOrDefault(m => m.Success)?.Groups[1].Value.Trim() ?? "";
        var rows = CsvRows(lines.Where(l => !l.TrimStart().StartsWith("#", StringComparison.Ordinal) && l.Trim().Length > 0));
        var t = FromRows(rows, custom);
        t.Version = version;
        return t;
    }

    /// <summary>Rows (header → cell, header lower-case) of CSV lines; the separator is whichever of , ; or tab the header uses most.</summary>
    public static List<Dictionary<string, string>> CsvRows(IEnumerable<string> lines)
    {
        var list = lines.ToList();
        if (list.Count < 2) return [];
        var sep = new[] { ',', ';', '\t' }.OrderByDescending(ch => list[0].Count(x => x == ch)).First();
        List<string> Cells(string line) => CsvCells(line, sep);
        var header = Cells(list[0]).Select(h => h.Trim().ToLowerInvariant()).ToList();
        return list.Skip(1).Select(l =>
        {
            var c = Cells(l);
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < header.Count && i < c.Count; i++) d[header[i]] = c[i].Trim();
            return d;
        }).ToList();
    }

    /// <summary>The cells of one CSV line split at <paramref name="sep"/> (quoted cells may hold it, and "" inside them is a quote).</summary>
    public static List<string> CsvCells(string line, char sep)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"') { if (quoted && i + 1 < line.Length && line[i + 1] == '"') { cell.Append('"'); i++; } else quoted = !quoted; }
            else if (ch == sep && !quoted) { cells.Add(cell.ToString()); cell.Clear(); }
            else cell.Append(ch);
        }
        cells.Add(cell.ToString());
        return cells;
    }

    /// <summary>
    /// A table from rows read from CSV or Excel. Columns are found by name: material/match/pattern, class, unit,
    /// kgCO2e per unit (or factor), stage, source, and optional density, grade, replacement, note, id.
    /// </summary>
    public static Table FromRows(IEnumerable<Dictionary<string, string>> rows, bool custom)
    {
        var t = new Table();
        var n = 0;
        foreach (var r in rows)
        {
            n++;
            string Col(params string[] names)
            {
                foreach (var name in names) if (r.TryGetValue(name, out var v) && v.Trim().Length > 0) return v.Trim();
                // "kgCO2e per unit (A1-A3)" and other decorated headers
                foreach (var name in names)
                    foreach (var kv in r)
                        if (kv.Key.StartsWith(name, StringComparison.OrdinalIgnoreCase) && kv.Value.Trim().Length > 0) return kv.Value.Trim();
                return "";
            }
            var match = Col("match", "match pattern", "pattern", "material", "material name", "name");
            var cls = Col("class", "material class").ToLowerInvariant();
            var unitText = Col("unit", "per unit", "declared unit", "functional unit");
            var valueText = Col("kgco2e_per_unit", "kgco2e per unit", "kgco2e/unit", "kgco2e", "factor", "ef", "carbon factor", "value");
            var label = custom ? $"row {n + 1}" : $"built-in row {n}";
            if (match.Length == 0 && cls.Length == 0) { if (r.Values.Any(v => v.Trim().Length > 0)) t.Problems.Add($"{label}: no material pattern or class"); continue; }
            var unit = NormalizeUnit(unitText);
            if (unit is null) { t.Problems.Add($"{label} ({match}{cls}): unit '{unitText}' isn't kg, t, m3, m2, t.km, km or %"); continue; }
            if (Number(valueText) is not { } value) { t.Problems.Add($"{label} ({match}{cls}): no kgCO2e per unit"); continue; }
            var stage = NormalizeStage(Col("stage", "module", "modules", "life cycle stage"));
            if (stage is null) { t.Problems.Add($"{label} ({match}{cls}): stage '{Col("stage", "module")}' isn't A1-A3, A4 or A5"); continue; }
            var f = new Factor
            {
                Id = Col("id", "ref", "code") is { Length: > 0 } id ? id : (custom ? $"custom-{n}" : $"row-{n}"),
                Class = cls, Match = match, Grade = Col("grade", "strength class"), Replacement = Col("replacement", "cement replacement").ToLowerInvariant(),
                Unit = unit, Value = value, Density = Number(Col("density_kg_m3", "density", "density (kg/m3)")) is { } dn && dn > 0 ? dn : null,
                Stage = stage, Source = Col("source", "reference", "epd"), Note = Col("note", "notes", "comment"), Custom = custom,
            };
            switch (f.Class)
            {
                case "a4_mode": t.Modes.Add(f); break;
                case "a4_distance": t.Distances.Add(f); break;
                case "a5_waste": t.Waste.Add(f); break;
                default:
                    // A material's quantity is kg, t, m3 or m2; transport and waste units belong on a4_mode, a4_distance and a5_waste rows.
                    if (f.Unit is "%" or "km" or "t.km") { t.Problems.Add($"{label} ({match}{cls}): unit '{unitText}' is for a4_mode, a4_distance or a5_waste rows, not a material (use kg, t, m3 or m2)"); continue; }
                    if (custom && f.Source.Length == 0) f.Source = "user table (no source given)";
                    t.Materials.Add(f);
                    break;
            }
        }
        return t;
    }

    /// <summary>A number from a cell: '0.138', '0,138' (decimal comma) or '1,234.5'.</summary>
    public static double? Number(string s)
    {
        var t = Regex.Replace(s ?? "", @"[^\d.,\-eE+]", "");
        if (t.Length == 0) return null;
        if (Regex.IsMatch(t, @"^-?\d+,\d+$")) t = t.Replace(",", ".");
        else if (Regex.IsMatch(t, @"^-?\d{1,3}(\.\d{3})+,\d+$")) t = t.Replace(".", "").Replace(",", ".");
        else t = t.Replace(",", "");
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>kg, t, m3, m2, t.km, km or % from the many ways a unit is written ("kgCO2e/m³", "per tonne"...), or null.</summary>
    public static string? NormalizeUnit(string unit)
    {
        var u = (unit ?? "").ToLowerInvariant().Replace("³", "3").Replace("²", "2");
        u = Regex.Replace(u, @"kg\s*co2\s*e?|co2\s*e?|\bper\b|/|\s|\(|\)", "");
        return u switch
        {
            "kg" or "kgs" or "" => "kg",
            "t" or "mt" or "ton" or "tons" or "tonne" or "tonnes" => "t",
            "m3" or "cum" or "cbm" => "m3",
            "m2" or "sqm" => "m2",
            "t.km" or "tkm" or "tonne.km" or "tonnekm" or "t-km" => "t.km",
            "km" => "km",
            "%" or "percent" => "%",
            _ => null,
        };
    }

    /// <summary>A1-A3 (also blank, "A1-3", "product"), A4 or A5, or null.</summary>
    public static string? NormalizeStage(string stage)
    {
        var s = Regex.Replace((stage ?? "").ToUpperInvariant(), @"\s|–|—", "-").Replace("--", "-");
        return s switch
        {
            "" or "A1-A3" or "A1-3" or "A1A3" or "A1TOA3" or "PRODUCT" or "CRADLE-TO-GATE" => "A1-A3",
            "A4" or "TRANSPORT" => "A4",
            "A5" or "A5W" or "WASTE" => "A5",
            _ => null,
        };
    }

    // ---------------------------------------------------------------- matching

    private static readonly (string cls, Regex re)[] ClassWords =
    [
        ("air", Rx(@"^\s*air\b|\bair (gap|space|layer|cavity)\b|\bcavity\b|\bvoid\b")),
        ("insulation", Rx(@"insulat|mineral wool|rock ?wool|glass ?wool|\bEPS\b|\bXPS\b|polystyrene|\bPIR\b|\bPUR\b")),
        ("plasterboard", Rx(@"plaster ?board|gypsum ?board|drywall|\bgyp\b")),
        ("aluminium", Rx(@"alumin")),
        ("rebar", Rx(@"\brebars?\b|reinforcing (bar|steel)|reinforcement (bar|steel)")),
        ("masonry", Rx(@"brick|\bblock|masonry|\bCMU\b|\bAAC\b|thermalite")),
        // The word concrete first ("steel fibre concrete"), then steel ("Metal - Steel 345 MPa"), then grade-only names ("CL_40MPa").
        ("concrete", Rx(@"concrete|\bRC\b|cast.in.(place|situ)|in.situ|precast")),
        ("steel", Rx(@"steel|\bmetal\b|\bS\d{3}\b|galvani[sz]ed|\biron\b")),
        ("concrete", Rx(@"\bC\d{2,3}/\d{2,3}\b|(?<![\d.])\d{2,3} ?MPa|\bgrout\b")),
        ("timber", Rx(@"timber|\bwood|glulam|\bCLT\b|lumber|plywood|softwood|hardwood|\bOSB\b|\bLVL\b")),
        ("glass", Rx(@"glass|glazing|glazed")),
        ("mortar", Rx(@"mortar|screed|render|plaster")),
        ("tile", Rx(@"\btiles?\b|ceramic|porcelain")),
        ("aggregate", Rx(@"gravel|aggregate|hardcore|\bsand\b|ballast|\bfill\b")),
        ("stone", Rx(@"stone|granite|marble|limestone|basalt")),
    ];

    private static Regex Rx(string p) => new(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The material class from its name, else from Revit's material class (Concrete, Metal, Wood...), or null.</summary>
    public static string? ClassOf(string name, string? revitClass)
    {
        foreach (var (cls, re) in ClassWords)
            if (re.IsMatch(name ?? "")) return cls;
        return (revitClass ?? "").Trim().ToLowerInvariant() switch
        {
            "concrete" => "concrete",
            "metal" => "steel",
            "wood" => "timber",
            "masonry" => "masonry",
            "glass" => "glass",
            "stone" => "stone",
            "ceramic" => "tile",
            "plaster" => "mortar",
            "gas" => "air",
            "insulation" => "insulation",
            _ => null,
        };
    }

    /// <summary>
    /// A name pattern: alternatives split by |; with * or ? a wildcard over the whole name, else contained in it (short
    /// codes such as CLT or SHS only as whole words). Ignores case.
    /// </summary>
    public static bool NameMatches(string name, string pattern)
    {
        if (string.IsNullOrEmpty(name)) return false;
        foreach (var raw in pattern.Split('|'))
        {
            var p = raw.Trim();
            if (p.Length == 0) continue;
            if (p == "*") return true;
            if (p.IndexOfAny(['*', '?']) >= 0) { if (ModelRules.Wildcard(name, p)) return true; continue; }
            if (p.Length <= 4 && p.All(char.IsLetterOrDigit))
            {
                if (Regex.IsMatch(name, @"(?<![A-Za-z0-9])" + Regex.Escape(p) + @"(?![A-Za-z0-9])", RegexOptions.IgnoreCase)) return true;
            }
            else if (name.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        return false;
    }

    /// <summary>The concrete's cylinder strength (MPa) from a name: C32/40, C40, 40MPa, fc'=40, 5000 psi; 40N/mm² is read as a cube strength.</summary>
    public static double? CylinderStrength(string name)
    {
        var s = name ?? "";
        var m = Regex.Match(s, @"\bC\s?(\d{2,3})\s?/\s?\d{2,3}\b", RegexOptions.IgnoreCase);
        if (m.Success) return double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        m = Regex.Match(s, @"(?<![\d.])(\d{2,3})\s?MPa", RegexOptions.IgnoreCase);
        if (m.Success) return double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        m = Regex.Match(s, @"\bf'?c'?\s?=?\s?(\d{2,3})\b", RegexOptions.IgnoreCase);
        if (m.Success) return double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        m = Regex.Match(s, @"(?<![\d.])(\d{4,5})\s?psi\b", RegexOptions.IgnoreCase);
        if (m.Success) return Math.Round(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 0.006895, 1);
        m = Regex.Match(s, @"(?<![\d.])(\d{2,3})\s?N(/mm2|/mm²)?(?![A-Za-z0-9])", RegexOptions.IgnoreCase);
        if (m.Success) return Math.Round(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) / 1.25, 1);
        m = Regex.Match(s, @"(?<![A-Za-z0-9])C(\d{2,3})(?![\d/])", RegexOptions.IgnoreCase);
        if (m.Success) return double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return null;
    }

    /// <summary>The cement replacement variant a name states (none, fa25, ggbs50), or null when it states none.</summary>
    public static string? ReplacementOf(string name)
    {
        var s = name ?? "";
        var m = Regex.Match(s, @"(\d{1,2})\s?%\s?(GGBS|GGBFS|slag)", RegexOptions.IgnoreCase);
        if (m.Success) return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) >= 35 ? "ggbs50" : "fa25";
        if (Regex.IsMatch(s, @"\d{1,2}\s?%\s?(PFA|FA|fly\s?ash)", RegexOptions.IgnoreCase)) return "fa25";
        if (Regex.IsMatch(s, @"\bGGBF?S\b|\bslag\b", RegexOptions.IgnoreCase)) return "ggbs50";
        if (Regex.IsMatch(s, @"\bPFA\b|fly\s?ash", RegexOptions.IgnoreCase)) return "fa25";
        if (Regex.IsMatch(s, @"\bCEM\s?I\b|\bOPC\b", RegexOptions.IgnoreCase)) return "none";
        return null;
    }

    public static double? GradeCylinder(string grade) => CylinderStrength(grade);

    /// <summary>The factor a material uses, with its class and any assumption made on the way.</summary>
    public sealed record Pick(Factor? Factor, string? Class, string? Note);

    /// <summary>
    /// The A1-A3 factor for a material: the user's rows first (in order; pattern on the material name or the type name,
    /// class if given), then the built-in rows of its class (concrete by strength class and cement replacement). Null
    /// factor: unmapped, with why.
    /// </summary>
    public static Pick Resolve(string material, string type, string? revitClass, IReadOnlyList<Factor> custom, Table builtIn, Options o, string? forcedClass = null)
    {
        var cls = forcedClass ?? ClassOf(material, revitClass) ?? ClassOf(type, null);
        foreach (var f in custom)
            if (f.Stage == "A1-A3" && Fits(f, material, type, cls)) return new Pick(f, cls ?? (f.Class.Length > 0 ? f.Class : null), null);
        if (cls is null) return new Pick(null, null, "no factor row matches its name, and its material class is not known");
        if (cls == "concrete") return Concrete(material, type, builtIn, o);
        var row = builtIn.Materials.FirstOrDefault(f => f.Class == cls && f.Match.Length > 0 && Fits(f, material, type, cls))
                  ?? builtIn.Materials.FirstOrDefault(f => f.Class == cls && f.Match.Length == 0);
        return row is null ? new Pick(null, cls, $"no built-in factor for {cls}; add one in a factor table") : new Pick(row, cls, null);
    }

    private static bool Fits(Factor f, string material, string type, string? cls)
    {
        if (f.Class.Length > 0 && f.Class != "*" && !string.Equals(f.Class, cls, StringComparison.OrdinalIgnoreCase)) return false;
        if (f.Match.Length == 0) return f.Class.Length > 0;
        return NameMatches(material, f.Match) || NameMatches(type, f.Match);
    }

    private static Pick Concrete(string material, string type, Table builtIn, Options o)
    {
        var notes = new List<string>();
        var cyl = CylinderStrength(material) ?? CylinderStrength(type);
        if (cyl is null)
        {
            cyl = GradeCylinder(o.DefaultGrade) ?? 32;
            notes.Add($"strength class not in the name: assumed {o.DefaultGrade}");
        }
        foreach (var kv in o.GradeSwap)
            if (Math.Abs(kv.Key - cyl.Value) < 0.5) { notes.Add($"strength class swapped from {cyl:0} to {kv.Value:0} MPa (cylinder)"); cyl = kv.Value; break; }
        var stated = ReplacementOf(material) ?? ReplacementOf(type);
        var rep = o.ReplacementAll || stated is null ? o.Replacement : stated;
        if (stated is null && !o.ReplacementAll)
            notes.Add($"cement replacement not in the name: assumed {ReplacementLabel(rep)}" + (rep == "none" ? ", the conservative ICE v3.0 value" : ""));
        var rows = builtIn.Materials.Where(f => f.Class == "concrete" && f.Replacement == rep && GradeCylinder(f.Grade) is not null)
            .OrderBy(f => GradeCylinder(f.Grade)).ToList();
        if (rows.Count == 0) return new Pick(null, "concrete", $"no built-in concrete factor for replacement '{rep}'");
        var row = rows.FirstOrDefault(f => GradeCylinder(f.Grade) >= cyl - 0.01);
        if (row is null) { row = rows.Last(); notes.Add($"above the table's range: used {row.Grade}"); }
        else if (Math.Abs(GradeCylinder(row.Grade)!.Value - cyl.Value) > 0.01) notes.Add($"no {cyl:0} MPa row: used the next class up, {row.Grade}");
        return new Pick(row, "concrete", notes.Count == 0 ? null : string.Join("; ", notes));
    }

    public static string ReplacementLabel(string rep) => rep switch
    {
        "none" => "CEM I (no replacement)",
        "fa25" => "25% fly ash",
        "ggbs50" => "50% GGBS",
        _ => rep,
    };

    /// <summary>The user's A4 or A5 row for a material, if any.</summary>
    public static Factor? StageRow(IReadOnlyList<Factor> custom, string stage, string material, string type, string? cls) =>
        custom.FirstOrDefault(f => f.Stage == stage && Fits(f, material, type, cls));

    // ---------------------------------------------------------------- quantities and stages

    /// <summary>Default density (kg/m³) of a class when the factor gives none.</summary>
    public static double DefaultDensity(string? cls) => cls switch
    {
        "concrete" => 2400, "rebar" or "steel" => 7850, "aluminium" => 2700, "timber" => 500, "masonry" => 1900, "glass" => 2500,
        "insulation" => 30, "plasterboard" => 800, "mortar" => 1900, "tile" => 2000, "aggregate" => 2000, "stone" => 2600, _ => 0,
    };

    /// <summary>Mass in kg: given, else volume × density.</summary>
    public static double Mass(double volumeM3, double? massKg, double density) =>
        massKg is { } m && m > 0 ? m : volumeM3 * density;

    /// <summary>The quantity in a factor's unit, or null when it can't be had (an area factor for something with no area).</summary>
    public static double? InUnit(string unit, double volumeM3, double areaM2, double? massKg, double density) => unit switch
    {
        "kg" => Mass(volumeM3, massKg, density) is var kg && kg > 0 ? kg : null,
        "t" => Mass(volumeM3, massKg, density) is var kg2 && kg2 > 0 ? kg2 / 1000 : null,
        "m3" => volumeM3 > 0 ? volumeM3 : massKg is { } m && m > 0 && density > 0 ? m / density : null,
        "m2" => areaM2 > 0 ? areaM2 : null,
        _ => null,
    };

    /// <summary>
    /// Why a part's emissions can't be worked out (so it is listed as unmapped rather than counted as 0), or null: an
    /// area factor with no area, a mass factor or A4 transport with no density, or a unit that isn't a material quantity.
    /// </summary>
    public static string? QuantityProblem(Factor f, string? cls, double volumeM3, double areaM2, double? massKg, Options o, Factor? a4Row, Factor? a5Row)
    {
        var density = f.Density ?? DefaultDensity(cls);
        string? Why(Factor row, double d, string what)
        {
            if (InUnit(row.Unit, volumeM3, areaM2, massKg, d) is not null) return null;
            return row.Unit switch
            {
                "m2" => $"{what} factor {row.Id} is per m² but Revit gives no area for it",
                "kg" or "t" when d <= 0 && volumeM3 > 0 => $"{what} factor {row.Id} is per {row.Unit} but there is no density for it: add a density column to the factor row",
                "kg" or "t" or "m3" => $"{what} factor {row.Id}: no volume or mass to measure",
                _ => $"{what} factor {row.Id} has unit '{row.Unit}', which isn't a material quantity (kg, t, m3 or m2)",
            };
        }
        if (Why(f, density, "A1-A3") is { } a) return a;
        if (o.A4)
        {
            if (a4Row != null) { if (Why(a4Row, a4Row.Density ?? density, "A4") is { } b) return b; }
            else if (Mass(volumeM3, massKg, density) <= 0) return $"A4 transport needs its mass, but factor {f.Id} gives no density: add a density column to the factor row";
        }
        if (o.A5 && a5Row != null && Why(a5Row, a5Row.Density ?? density, "A5") is { } c) return c;
        return null;
    }

    /// <summary>Emissions of one quantity of material by module, kgCO2e.</summary>
    public sealed record Stages(double A1A3, double A4, double A5)
    {
        public double Total => A1A3 + A4 + A5;
    }

    /// <summary>
    /// Transport distance (km) for a class: the option, else the first scenario row naming it (or *). Precast concrete
    /// is made nationally, not ready-mixed nearby: a row naming precast, else the national (*) row.
    /// </summary>
    public static double Distance(string? cls, Options o, Table builtIn, Table? custom, bool precast = false) =>
        o.A4DistanceKm ?? (custom?.Distances ?? []).Concat(builtIn.Distances)
            .FirstOrDefault(f => precast && cls == "concrete" ? ClassIn("precast", f.Match) : ClassIn(cls, f.Match))?.Value ?? 0;

    /// <summary>Whether a concrete material or type name says precast (precast, hollow-core, PC).</summary>
    public static bool IsPrecast(string material, string type) =>
        new[] { material, type }.Any(n => Regex.IsMatch(n ?? "", @"pre-?cast|hollow[\s_-]?core|(?<![A-Za-z0-9])PC(?![A-Za-z])", RegexOptions.IgnoreCase));

    /// <summary>kgCO2e per tonne-km of a transport mode.</summary>
    public static double ModeFactor(string mode, Table builtIn, Table? custom) =>
        (custom?.Modes ?? []).Concat(builtIn.Modes).FirstOrDefault(f => NameMatches(mode, f.Match))?.Value
        ?? throw new ArgumentException($"No transport factor for mode '{mode}'; use road, rail or sea, or add an a4_mode row.");

    /// <summary>Waste rate (%) for a class: the option, else the first scenario row naming it (or *).</summary>
    public static double WasteRate(string? cls, Options o, Table builtIn, Table? custom) =>
        o.WastePercent ?? (custom?.Waste ?? []).Concat(builtIn.Waste).FirstOrDefault(f => ClassIn(cls, f.Match))?.Value ?? 0;

    private static bool ClassIn(string? cls, string list) =>
        list.Split('|').Any(x => x.Trim() == "*" || string.Equals(x.Trim(), cls, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// RICS waste factor: material bought per unit installed is 1/(1 - w), so the wasted part is w/(1 - w) of what is
    /// installed. Rates of 100% or more are refused.
    /// </summary>
    public static double WasteFactor(double percent)
    {
        if (percent < 0 || percent >= 100) throw new ArgumentException($"Waste rate {percent}% must be from 0 to under 100%.");
        return percent / (100 - percent);
    }

    /// <summary>
    /// A1-A3 from the factor; A4 as mass × distance × mode factor (or the user's A4 row); A5 as the waste factor × (A1-A3
    /// + A4) (or the user's A5 row). End of life of the wasted material (C2-C4 of A5) is not counted.
    /// </summary>
    public static Stages Emissions(Factor f, string? cls, double volumeM3, double areaM2, double? massKg, Options o, Table builtIn, Table? custom,
        Factor? a4Row, Factor? a5Row, out double quantity, bool precast = false)
    {
        var density = f.Density ?? DefaultDensity(cls);
        quantity = InUnit(f.Unit, volumeM3, areaM2, massKg, density) ?? 0;
        var a13 = quantity * f.Value;
        double a4 = 0, a5 = 0;
        if (o.A4)
        {
            if (a4Row != null) a4 = (InUnit(a4Row.Unit, volumeM3, areaM2, massKg, a4Row.Density ?? density) ?? 0) * a4Row.Value;
            else a4 = Mass(volumeM3, massKg, density) / 1000 * Distance(cls, o, builtIn, custom, precast) * ModeFactor(o.A4Mode, builtIn, custom);
        }
        if (o.A5)
        {
            if (a5Row != null) a5 = (InUnit(a5Row.Unit, volumeM3, areaM2, massKg, a5Row.Density ?? density) ?? 0) * a5Row.Value;
            else a5 = WasteFactor(WasteRate(cls, o, builtIn, custom)) * (a13 + a4);
        }
        return new Stages(a13, a4, a5);
    }

    /// <summary>Assumed reinforcement rate (kg per m³ of concrete) by category, typical values to replace with the design's.</summary>
    public static readonly Dictionary<string, double> RebarRates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Floors"] = 110, ["Structural Framing"] = 200, ["Structural Columns"] = 200, ["Columns"] = 200, ["Walls"] = 100,
        ["Structural Foundations"] = 110, ["Stairs"] = 120, ["Roofs"] = 110,
    };

    public static double RebarRate(string category, IReadOnlyDictionary<string, double>? overrides)
    {
        if (overrides != null)
            foreach (var kv in overrides)
                if (string.Equals(kv.Key, category, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return RebarRates.TryGetValue(category, out var r) ? r : 100;
    }

    /// <summary>kgCO2e per m² of floor area, or null without an area.</summary>
    public static double? Intensity(double totalKg, double? areaM2) => areaM2 is { } a && a > 0 ? totalKg / a : null;

    /// <summary>Grade swaps from text such as "C40/50>C32/40, C50/60 > C40/50" (or "->", "=" or "to").</summary>
    public static Dictionary<double, double> ParseGradeSwap(string? text)
    {
        var map = new Dictionary<double, double>();
        foreach (var part in (text ?? "").Split(',', ';'))
        {
            var sides = Regex.Split(part, @"\s*(?:->|=>|>|=|\bto\b)\s*", RegexOptions.IgnoreCase);
            if (part.Trim().Length == 0) continue;
            if (sides.Length != 2 || CylinderStrength(sides[0]) is not { } from || CylinderStrength(sides[1]) is not { } to)
                throw new ArgumentException($"Grade swap '{part.Trim()}' isn't like 'C40/50>C32/40'.");
            map[from] = to;
        }
        return map;
    }

    // ---------------------------------------------------------------- results

    /// <summary>One group of elements of one material on one level (or its reinforcement), with its emissions.</summary>
    public sealed class Line
    {
        public string Source { get; set; } = "";
        public string Level { get; set; } = "";
        public string Category { get; set; } = "";
        public string Type { get; set; } = "";
        public string Material { get; set; } = "";
        public string Class { get; set; } = "";
        public string Factor { get; set; } = "";
        public string Unit { get; set; } = "";
        public double Quantity { get; set; }
        public double VolumeM3 { get; set; }
        public double MassKg { get; set; }
        public double A1A3 { get; set; }
        public double A4 { get; set; }
        public double A5 { get; set; }
        public int Elements { get; set; }
        public bool Assumed { get; set; }
        public string Note { get; set; } = "";
        public double Total => A1A3 + A4 + A5;
    }

    /// <summary>Material found in the model that no factor covers, so it is not in the totals.</summary>
    public sealed class Unmapped
    {
        public string Source { get; set; } = "";
        public string Category { get; set; } = "";
        public string Type { get; set; } = "";
        public string Material { get; set; } = "";
        public string RevitClass { get; set; } = "";
        public double VolumeM3 { get; set; }
        public int Elements { get; set; }
        public string Reason { get; set; } = "";
    }

    public sealed class FactorUse
    {
        public string Id { get; set; } = "";
        public string Class { get; set; } = "";
        public string Match { get; set; } = "";
        public string Grade { get; set; } = "";
        public string Replacement { get; set; } = "";
        public string Unit { get; set; } = "";
        public double Value { get; set; }
        public string Stage { get; set; } = "";
        public string Source { get; set; } = "";
        public string Note { get; set; } = "";
        public bool Custom { get; set; }
        public double KgCO2e { get; set; }
    }

    /// <summary>An assessment, saved as JSON so later runs and carbon_compare can compare with it.</summary>
    public sealed class Result
    {
        public string Model { get; set; } = "";
        public string Date { get; set; } = "";
        public string Name { get; set; } = "";
        public string FactorTable { get; set; } = "";
        public string? CustomTable { get; set; }
        public double? GiaM2 { get; set; }
        public string GiaSource { get; set; } = "";
        public List<string> Stages { get; set; } = [];
        public List<Line> Lines { get; set; } = [];
        public List<Unmapped> Unmapped { get; set; } = [];
        public List<FactorUse> Factors { get; set; } = [];
        public List<string> Assumptions { get; set; } = [];
        public List<string> NotIncluded { get; set; } = [];
        public double A1A3 => Lines.Sum(l => l.A1A3);
        public double A4 => Lines.Sum(l => l.A4);
        public double A5 => Lines.Sum(l => l.A5);
        public double Total => Lines.Sum(l => l.Total);
        public double? Intensity => Carbon.Intensity(Total, GiaM2);
    }

    /// <summary>Adds a quantity to the line with the same keys, or starts one.</summary>
    public static void Add(Dictionary<string, Line> lines, Line add)
    {
        var key = string.Join("\u0001", add.Source, add.Level, add.Category, add.Type, add.Material, add.Class, add.Factor, add.Assumed);
        if (!lines.TryGetValue(key, out var l)) { lines[key] = add; return; }
        l.Quantity += add.Quantity; l.VolumeM3 += add.VolumeM3; l.MassKg += add.MassKg;
        l.A1A3 += add.A1A3; l.A4 += add.A4; l.A5 += add.A5; l.Elements += add.Elements;
        if (l.Note.Length == 0) l.Note = add.Note;
    }

    /// <summary>One row of a breakdown: kgCO2e by module and its share of the total.</summary>
    public sealed record Row(string Key, double A1A3, double A4, double A5, double Total, double SharePercent, double VolumeM3, double MassKg, int Elements);

    /// <summary>Lines summed by a key, largest first.</summary>
    public static List<Row> By(IEnumerable<Line> lines, Func<Line, string> key)
    {
        var list = lines.ToList();
        var grand = list.Sum(l => l.Total);
        return list.GroupBy(l => key(l) is { Length: > 0 } k ? k : "(none)")
            .Select(g => new Row(g.Key, g.Sum(l => l.A1A3), g.Sum(l => l.A4), g.Sum(l => l.A5), g.Sum(l => l.Total),
                grand > 0 ? g.Sum(l => l.Total) / grand * 100 : 0, g.Sum(l => l.VolumeM3), g.Sum(l => l.MassKg), g.Sum(l => l.Elements)))
            .OrderByDescending(r => r.Total).ThenBy(r => r.Key, StringComparer.Ordinal).ToList();
    }

    /// <summary>The biggest contributors: element type and material together.</summary>
    public static List<Row> Top(IEnumerable<Line> lines, int n) =>
        By(lines, l => $"{l.Category}: {l.Type} / {l.Material}").Take(n).ToList();

    /// <summary>One row of a comparison: before, after and the change (negative is a saving).</summary>
    public sealed record Diff(string Key, double Before, double After, double Change, double? ChangePercent);

    public static List<Diff> Compare(IEnumerable<Line> before, IEnumerable<Line> after, Func<Line, string> key)
    {
        var a = By(before, key).ToDictionary(r => r.Key, r => r.Total);
        var b = By(after, key).ToDictionary(r => r.Key, r => r.Total);
        return a.Keys.Union(b.Keys).Select(k =>
            {
                var x = a.TryGetValue(k, out var av) ? av : 0;
                var y = b.TryGetValue(k, out var bv) ? bv : 0;
                return new Diff(k, x, y, y - x, x > 0 ? (y - x) / x * 100 : null);
            })
            .OrderBy(d => d.Change).ThenBy(d => d.Key, StringComparer.Ordinal).ToList();
    }

    // ---------------------------------------------------------------- colour bands for the 3D view

    /// <summary>Upper limits of <paramref name="n"/> bands holding about the same number of values (the last is the maximum).</summary>
    public static List<double> Bands(IEnumerable<double> values, int n)
    {
        var v = values.Where(x => x > 0 && !double.IsNaN(x) && !double.IsInfinity(x)).OrderBy(x => x).ToList();
        if (v.Count == 0 || n < 1) return [];
        var limits = new List<double>();
        for (var i = 1; i <= n; i++)
        {
            var at = (int)Math.Ceiling(v.Count * (double)i / n) - 1;
            var limit = v[Math.Max(0, Math.Min(v.Count - 1, at))];
            if (limits.Count == 0 || limit > limits[limits.Count - 1]) limits.Add(limit);
        }
        return limits;
    }

    /// <summary>The band (0-based) a value falls in.</summary>
    public static int BandOf(double value, IReadOnlyList<double> limits)
    {
        for (var i = 0; i < limits.Count; i++) if (value <= limits[i]) return i;
        return limits.Count - 1;
    }

    /// <summary><paramref name="n"/> colours from green (low) through yellow to red (high).</summary>
    public static List<(byte r, byte g, byte b)> Palette(int n)
    {
        (double r, double g, double b)[] stops = [(26, 150, 65), (166, 217, 106), (255, 255, 191), (253, 174, 97), (215, 25, 28)];
        var list = new List<(byte, byte, byte)>();
        for (var i = 0; i < n; i++)
        {
            var t = n == 1 ? 1 : (double)i / (n - 1) * (stops.Length - 1);
            var k = Math.Min(stops.Length - 2, (int)Math.Floor(t));
            var f = t - k;
            byte C(double x, double y) => (byte)Math.Round(x + (y - x) * f);
            list.Add((C(stops[k].r, stops[k + 1].r), C(stops[k].g, stops[k + 1].g), C(stops[k].b, stops[k + 1].b)));
        }
        return list;
    }

    // ---------------------------------------------------------------- text

    public static string F(double v, int dp = 1) => Math.Round(v, dp).ToString("0." + new string('#', Math.Max(1, dp)), CultureInfo.InvariantCulture);

    /// <summary>A CSV field: quoted when needed; text that a spreadsheet would read as a formula gets a leading ' (shown as text).</summary>
    public static string Csv(string s)
    {
        if (Xlsx.FormulaLike(s)) s = "'" + s;
        return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    /// <summary>What is outside the modules counted, for the assumptions section.</summary>
    public static List<string> NotIncluded(Options o)
    {
        var list = new List<string>();
        if (!o.A4) list.Add("A4 transport to site (not requested)");
        if (!o.A5) list.Add("A5 construction: site waste (not requested)");
        list.Add("A5 site activities (plant, energy, temporary works) and end of life of site waste (C2-C4 of A5)");
        list.Add("B1-B7 use stage (maintenance, repair, replacement, operational energy and water)");
        list.Add("C1-C4 end of life and module D (benefits beyond the system boundary)");
        list.Add("biogenic carbon stored in timber (no sequestration credit)");
        list.Add("anything not modelled in Revit (MEP services, finishes, fixings, connections, temporary works) and materials listed as unmapped");
        return list;
    }
}

/// <summary>A minimal .xlsx writer: sheets of text, numbers and formulas, header row in bold and frozen. Pure, so the tests run it.</summary>
internal static class Xlsx
{
    /// <summary>
    /// A formula cell (without the leading =), with the value it gives now so readers that don't calculate (and the
    /// CSV) still see it; Excel recalculates on open. Cached is a number or text.
    /// </summary>
    public sealed record Formula(string Expression, object? Cached);

    public static void Write(string path, IEnumerable<(string Name, IList<object?[]> Rows)> sheets)
    {
        var list = sheets.ToList();
        if (File.Exists(path)) File.Delete(path);
        using var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
        void Entry(string name, string text)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            w.Write(text);
        }
        var names = new List<string>();
        foreach (var s in list)
        {
            var n = new string(s.Name.Where(ch => "[]:*?/\\".IndexOf(ch) < 0).ToArray()).Trim();
            if (n.Length == 0) n = "Sheet";
            if (n.Length > 31) n = n.Substring(0, 31);
            var unique = n;
            for (var i = 2; names.Contains(unique, StringComparer.OrdinalIgnoreCase); i++) unique = n.Substring(0, Math.Min(n.Length, 28)) + " " + i;
            names.Add(unique);
        }
        Entry("[Content_Types].xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
            "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
            string.Concat(names.Select((_, i) => $"<Override PartName=\"/xl/worksheets/sheet{i + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>")) +
            "</Types>");
        Entry("_rels/.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
        Entry("xl/workbook.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
            "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>" +
            string.Concat(names.Select((n, i) => $"<sheet name=\"{Esc(n)}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>")) + "</sheets>" +
            (list.Any(x => x.Rows.Any(r => r.Any(c => c is Formula))) ? "<calcPr fullCalcOnLoad=\"1\"/>" : "") + "</workbook>");
        Entry("xl/_rels/workbook.xml.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            string.Concat(names.Select((_, i) => $"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>")) +
            $"<Relationship Id=\"rId{names.Count + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
        Entry("xl/styles.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
            "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
            "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>" +
            "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
            "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
            "<cellXfs count=\"4\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/><xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>" +
            // 2 and 3: text that looks like a formula (=, +, -, @), kept as text even when the cell is edited (Excel's ' prefix).
            "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" quotePrefix=\"1\"/><xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" quotePrefix=\"1\"/></cellXfs>" +
            "</styleSheet>");
        for (var i = 0; i < list.Count; i++)
        {
            var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
            sb.Append("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
            var columns = list[i].Rows.Count == 0 ? 0 : list[i].Rows.Max(r => r.Length);
            if (columns > 0) sb.Append($"<cols><col min=\"1\" max=\"{columns}\" width=\"18\" customWidth=\"1\"/></cols>");
            sb.Append("<sheetData>");
            for (var r = 0; r < list[i].Rows.Count; r++)
            {
                sb.Append($"<row r=\"{r + 1}\">");
                var row = list[i].Rows[r];
                for (var c = 0; c < row.Length; c++)
                {
                    var cell = row[c];
                    if (cell is null) continue;
                    var at = Column(c) + (r + 1);
                    var style = r == 0 ? " s=\"1\"" : "";
                    if (cell is Formula fm)
                    {
                        var expr = Esc(fm.Expression.TrimStart('='));
                        if (fm.Cached is double or float or int or long or decimal && Convert.ToDouble(fm.Cached, CultureInfo.InvariantCulture) is var fv && !double.IsNaN(fv) && !double.IsInfinity(fv))
                            sb.Append($"<c r=\"{at}\"{style}><f>{expr}</f><v>{fv.ToString("R", CultureInfo.InvariantCulture)}</v></c>");
                        else sb.Append($"<c r=\"{at}\"{style} t=\"str\"><f>{expr}</f><v>{Esc(Convert.ToString(fm.Cached, CultureInfo.InvariantCulture) ?? "")}</v></c>");
                    }
                    else if (cell is double or float or int or long or decimal)
                    {
                        var d = Convert.ToDouble(cell, CultureInfo.InvariantCulture);
                        if (double.IsNaN(d) || double.IsInfinity(d)) continue;
                        sb.Append($"<c r=\"{at}\"{style}><v>{d.ToString("R", CultureInfo.InvariantCulture)}</v></c>");
                    }
                    else
                    {
                        // Text from the model or a file is never a formula: an inline string is not calculated, and one that
                        // starts like a formula also gets the quote-prefix style, so editing the cell doesn't turn it into one.
                        var text = Convert.ToString(cell, CultureInfo.InvariantCulture) ?? "";
                        if (FormulaLike(text)) style = r == 0 ? " s=\"3\"" : " s=\"2\"";
                        sb.Append($"<c r=\"{at}\"{style} t=\"inlineStr\"><is><t xml:space=\"preserve\">{Esc(text)}</t></is></c>");
                    }
                }
                sb.Append("</row>");
            }
            sb.Append("</sheetData></worksheet>");
            Entry($"xl/worksheets/sheet{i + 1}.xml", sb.ToString());
        }
    }

    /// <summary>
    /// The cells of one sheet (default the first, in the workbook's tab order) as text, row by row: shared strings, inline
    /// strings, numbers and the cached value of formulas. Gaps are filled with empty cells (rows Excel left out as empty
    /// are not returned). Throws InvalidDataException when the file is not a workbook that can be read.
    /// </summary>
    public static List<List<string>> Read(string path, int sheet = 1)
    {
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(path);
            System.Xml.Linq.XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var shared = new List<string>();
            if (Entry(zip, "xl/sharedStrings.xml") is { } ss)
                using (var st = ss.Open())
                    shared = System.Xml.Linq.XDocument.Load(st).Root!.Elements(ns + "si")
                        .Select(si => string.Concat(si.Descendants(ns + "t").Where(x => !x.Ancestors(ns + "rPh").Any()).Select(x => x.Value))).ToList();
            var entry = SheetEntry(zip, sheet) ?? throw new InvalidDataException($"The workbook has no sheet {sheet}.");
            using var stream = entry.Open();
            var rows = new List<List<string>>();
            foreach (var row in System.Xml.Linq.XDocument.Load(stream).Descendants(ns + "row"))
            {
                var cells = new List<string>();
                foreach (var c in row.Elements(ns + "c"))
                {
                    var col = 0;
                    foreach (var ch in ((string?)c.Attribute("r") ?? "").ToUpperInvariant().TakeWhile(ch => ch is >= 'A' and <= 'Z'))
                        col = Math.Min(MaxColumns + 1, col * 26 + (ch - 'A' + 1));
                    if (col > MaxColumns) continue; // past Excel's last column: not a real cell
                    while (col > 0 && cells.Count < col - 1) cells.Add("");
                    var value = c.Element(ns + "v")?.Value ?? string.Concat(c.Descendants(ns + "t").Where(x => !x.Ancestors(ns + "rPh").Any()).Select(x => x.Value));
                    if ((string?)c.Attribute("t") == "s")
                        value = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx) && idx >= 0 && idx < shared.Count ? shared[idx] : "";
                    if (col > 0 && cells.Count >= col) cells[col - 1] = value; // a repeated reference: the last one wins
                    else cells.Add(value);
                }
                rows.Add(cells);
            }
            return rows;
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException or NullReferenceException or NotSupportedException)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} can't be read as an Excel workbook ({ex.Message}). Save it again from Excel as .xlsx, or as CSV.", ex);
        }
    }

    private const int MaxColumns = 16384;

    private static System.IO.Compression.ZipArchiveEntry? Entry(System.IO.Compression.ZipArchive zip, string name) =>
        zip.GetEntry(name) ?? zip.Entries.FirstOrDefault(e => e.FullName.TrimStart('/').Replace('\\', '/').Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The worksheet part of the nth sheet in tab order (workbook.xml and its relationships), else sheetN.xml.</summary>
    private static System.IO.Compression.ZipArchiveEntry? SheetEntry(System.IO.Compression.ZipArchive zip, int sheet)
    {
        try
        {
            System.Xml.Linq.XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            System.Xml.Linq.XNamespace rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            System.Xml.Linq.XNamespace pkg = "http://schemas.openxmlformats.org/package/2006/relationships";
            if (Entry(zip, "xl/workbook.xml") is { } wb && Entry(zip, "xl/_rels/workbook.xml.rels") is { } rels)
            {
                string? id;
                using (var st = wb.Open())
                    id = (string?)System.Xml.Linq.XDocument.Load(st).Descendants(ns + "sheet").Skip(sheet - 1).FirstOrDefault()?.Attribute(rel + "id");
                if (id is not null)
                {
                    string? target;
                    using (var st = rels.Open())
                        target = (string?)System.Xml.Linq.XDocument.Load(st).Root?.Elements(pkg + "Relationship").FirstOrDefault(r => (string?)r.Attribute("Id") == id)?.Attribute("Target");
                    if (target is not null)
                    {
                        var name = target.StartsWith("/", StringComparison.Ordinal) ? target.TrimStart('/') : "xl/" + target;
                        if (Entry(zip, name.Replace("/./", "/")) is { } found) return found;
                    }
                }
            }
        }
        catch (System.Xml.XmlException) { /* fall back to the usual part name */ }
        return Entry(zip, $"xl/worksheets/sheet{sheet}.xml");
    }

    /// <summary>
    /// Text a spreadsheet would read as a formula: it starts with =, +, -, @ (or a tab or return before one). A plain
    /// number such as -12.5 is not.
    /// </summary>
    public static bool FormulaLike(string? s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        var t = s!.TrimStart('\t', '\r', '\n', ' ');
        if (t.Length == 0 || "=+-@".IndexOf(t[0]) < 0) return false;
        return !(t.Length == s.Length && double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out _));
    }

    /// <summary>A, B, ... Z, AA, AB ... for a 0-based column.</summary>
    public static string Column(int index)
    {
        var s = "";
        for (var n = index + 1; n > 0; n = (n - 1) / 26) s = (char)('A' + (n - 1) % 26) + s;
        return s;
    }

    private static string Esc(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (ch == '&') sb.Append("&amp;");
            else if (ch == '<') sb.Append("&lt;");
            else if (ch == '>') sb.Append("&gt;");
            else if (ch == '"') sb.Append("&quot;");
            else if (ch < 0x20 && ch != '\t' && ch != '\n' && ch != '\r') continue;
            else sb.Append(ch);
        }
        return sb.ToString();
    }
}
