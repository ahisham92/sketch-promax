using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SketchProMax.Tools;

/// <summary>
/// Cost plan arithmetic, with no Revit: the firm's rate table (region and currency chosen, other currencies converted
/// at the rates given), the documented template, the add-ons (prelims, contingency, escalation by date, VAT), the rates
/// per unit (m² GFA by level and zone, linear m, m³ of concrete, tonne of steel and rebar), options side by side, the
/// what-if delta, a rough frame programme and the chat summary card. cost_estimate and what_if measure the model and
/// call these; the tests run them directly.
/// </summary>
internal static class Cost
{
    // ---------------------------------------------------------------- the rate table

    /// <summary>One rate of the table, as chosen for this run (in the run's currency).</summary>
    public sealed class RateRow
    {
        public string Ref = "", Description = "", Unit = "", Category = "", Type = "", Level = "", Material = "", Region = "", Currency = "", Notes = "";
        public double Rate;
        /// <summary>The row in the rate table's first sheet (the header is row 1), or 0 for rates given directly.</summary>
        public int SourceRow;
        /// <summary>Where the rate came from, for the basis column: "Rates row 5", "given", "Rates row 7 (USD × 3.75)".</summary>
        public string Source = "";
        public string Key => (Ref.Length > 0 ? Ref : Description + "|" + Unit).Trim().ToLowerInvariant();
    }

    /// <summary>The template's columns, in order. Description, Unit and Rate are needed; the rest are optional.</summary>
    public static readonly string[] Columns = ["Ref", "Description", "Unit", "Rate", "Currency", "Region", "Category", "Type", "Level", "Material", "Notes"];

    /// <summary>A number from a table cell: "1,234.5", "1.234,5", "12,5" (decimal comma), "USD 150"; null when there is none.</summary>
    public static double? Num(string? s, char separator = '\0')
    {
        var t = Regex.Replace(s ?? "", @"[^\d.,\-]", "");
        if (t.Length == 0 || t == "-") return null;
        if (Regex.IsMatch(t, @"^-?\d{1,3}(\.\d{3})*,\d+$") || Regex.IsMatch(t, @"^-?\d+,\d{1,2}$") || (separator == ';' && t.Contains(',')))
            t = t.Replace(".", "").Replace(",", ".");
        else t = t.Replace(",", "");
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>What <see cref="SelectRates"/> chose, and why.</summary>
    public sealed class Selection
    {
        public List<RateRow> Rows = [];
        public string Region = "", Currency = "";
        public List<string> Regions = [], Currencies = [];
        public List<string> Notes = [];
    }

    private static string Col(Dictionary<string, string> r, params string[] names)
    {
        foreach (var n in names) if (r.TryGetValue(n, out var v)) return v.Trim();
        foreach (var n in names)
            foreach (var kv in r)
                if (kv.Key.StartsWith(n + " (", StringComparison.OrdinalIgnoreCase) || kv.Key.StartsWith(n + "(", StringComparison.OrdinalIgnoreCase)) return kv.Value.Trim();
        return "";
    }

    /// <summary>
    /// The rates for one region and currency from the table's rows (as Table.Read gives them: header names lower case).
    /// Region: rows with that region, or with none (they apply everywhere); a region's own row replaces the general row
    /// of the same item (same Ref, else same Description and Unit). With several regions in the table, one must be
    /// chosen. Currency: rows in it (or with none) as they are; rows in another currency are converted when
    /// <paramref name="exchange"/> gives its rate (1 unit of that currency = rate units of the chosen one), else
    /// left out and listed. With several currencies and none chosen, the most common one is used and said.
    /// </summary>
    public static Selection SelectRates(IEnumerable<Dictionary<string, string>> table, string? region, string? currency,
        IReadOnlyDictionary<string, double>? exchange = null, char separator = '\0')
    {
        var all = new List<RateRow>();
        var n = 1;
        foreach (var r in table)
        {
            n++;
            var rate = Num(Col(r, "rate", "unit rate", "unit price", "price"), separator);
            var d = Col(r, "description", "item description", "desc", "item");
            if (rate is null || d.Length == 0) continue;
            all.Add(new RateRow
            {
                Ref = Col(r, "ref", "code", "item no", "no"), Description = d, Unit = Col(r, "unit", "uom"), Rate = rate.Value,
                Currency = Col(r, "currency", "ccy").ToUpperInvariant(), Region = Col(r, "region", "location", "city"),
                Category = Col(r, "category", "revit category"), Type = Col(r, "type", "revit type"), Level = Col(r, "level", "floor"),
                Material = Col(r, "material"), Notes = Col(r, "notes", "note", "remarks"), SourceRow = n, Source = $"Rates row {n}",
            });
        }
        var s = new Selection
        {
            Regions = all.Select(x => x.Region).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
            Currencies = all.Select(x => x.Currency).Where(x => x.Length > 0).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList(),
        };
        if (all.Count == 0) throw new ArgumentException("The rate table has no rows with a description and a rate. Columns: " + string.Join(", ", Columns) + " (make one with cost_estimate template: true).");

        // Region
        if (!string.IsNullOrWhiteSpace(region))
        {
            var match = s.Regions.FirstOrDefault(x => x.Equals(region!.Trim(), StringComparison.OrdinalIgnoreCase))
                        ?? throw new ArgumentException($"No region '{region}' in the rate table." + (s.Regions.Count > 0 ? " Regions: " + string.Join(", ", s.Regions) + "." : " It has no Region column; leave region out."));
            s.Region = match;
        }
        else if (s.Regions.Count > 0 && all.All(x => x.Region.Length > 0))
        {
            if (s.Regions.Count > 1) throw new ArgumentException("The rate table has rates for several regions; choose one with region: " + string.Join(", ", s.Regions) + ".");
            s.Region = s.Regions[0];
            s.Notes.Add($"Region {s.Region} (the only one in the table).");
        }
        else if (s.Regions.Count > 0) s.Notes.Add("No region chosen: the general rates (no region) were used. Regions in the table: " + string.Join(", ", s.Regions) + ".");
        var inRegion = all.Where(x => x.Region.Length == 0 || x.Region.Equals(s.Region, StringComparison.OrdinalIgnoreCase)).ToList();

        // Currency
        if (!string.IsNullOrWhiteSpace(currency)) s.Currency = currency!.Trim().ToUpperInvariant();
        else
        {
            var common = inRegion.Where(x => x.Currency.Length > 0).GroupBy(x => x.Currency).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).FirstOrDefault();
            s.Currency = common?.Key ?? "";
            if (s.Currencies.Count > 1) s.Notes.Add($"Currency {s.Currency}: the most common in the table (give currency to choose another).");
        }
        var kept = new List<RateRow>();
        var dropped = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var x in inRegion)
        {
            if (x.Currency.Length == 0 || x.Currency == s.Currency || s.Currency.Length == 0) { kept.Add(x); continue; }
            if (exchange != null && exchange.TryGetValue(x.Currency, out var fx) && fx > 0)
            {
                kept.Add(new RateRow
                {
                    Ref = x.Ref, Description = x.Description, Unit = x.Unit, Category = x.Category, Type = x.Type, Level = x.Level, Material = x.Material,
                    Region = x.Region, Notes = x.Notes, SourceRow = x.SourceRow, Currency = s.Currency, Rate = x.Rate * fx,
                    Source = $"{x.Source} ({x.Currency} {F(x.Rate, 2)} × {F(fx, 4)})",
                });
            }
            else dropped[x.Currency] = (dropped.TryGetValue(x.Currency, out var c) ? c : 0) + 1;
        }
        foreach (var kv in dropped)
            s.Notes.Add($"{kv.Value} rate(s) in {kv.Key} left out: give exchange_rates {{\"{kv.Key}\": <{s.Currency} per {kv.Key}>}} to convert them.");

        // One row per item: the region's own row, then a row in the chosen currency, over a general or converted one.
        foreach (var g in kept.GroupBy(x => x.Key))
        {
            var best = g.OrderBy(x => x.Region.Length > 0 ? 0 : 1).ThenBy(x => x.Source.Contains(" × ") ? 1 : 0).ThenBy(x => x.SourceRow).First();
            s.Rows.Add(best);
        }
        s.Rows = s.Rows.OrderBy(x => x.SourceRow).ToList();
        if (s.Rows.Count == 0) throw new ArgumentException($"No rates left for region '{s.Region}' in {s.Currency}." + string.Join(" ", s.Notes));
        return s;
    }

    /// <summary>
    /// The documented rate table template: sheet 1 "Rates" (what cost_estimate reads) with example rows, sheet 2 "Read
    /// me" explaining each column, the matching and the add-ons. The example rates are round placeholders, not prices.
    /// </summary>
    public static List<(string Name, IList<object?[]> Rows)> Template()
    {
        object?[] R(string r, string d, string u, double rate, string cur, string region, string cat = "", string type = "", string notes = "") =>
            [r, d, u, rate, cur, region, cat, type, "", "", notes];
        var rates = new List<object?[]>
        {
            Columns,
            R("C-01", "Concrete C32/40 in suspended slabs", "m3", 160, "USD", "", "Floors", notes: "Example rate: replace with the firm's"),
            R("C-01", "Concrete C32/40 in suspended slabs", "m3", 175, "USD", "Region B", "Floors", notes: "Region B's own rate for the same item"),
            R("C-02", "Concrete in beams", "m3", 180, "USD", "", "Structural Framing"),
            R("C-03", "Concrete in columns", "m3", 190, "USD", "", "Structural Columns"),
            R("C-04", "Concrete in walls", "m3", 170, "USD", "", "Walls", notes: "Masonry is measured separately"),
            R("C-05", "Concrete in foundations", "m3", 150, "USD", "", "Structural Foundations"),
            R("R-01", "Reinforcement to slabs, beams, columns and walls", "t", 1300, "USD", "", notes: "Modelled bars only; add an allowance line for the rest"),
            R("S-01", "Structural steel sections", "t", 2800, "USD", "", "Structural Framing", notes: "Steel members are weighed at 7850 kg/m³"),
            R("F-01", "Formwork to soffits of slabs", "m2", 40, "USD", "", "Floors"),
            R("P-01", "Bored piles", "m", 120, "USD", "", "Structural Foundations"),
            R("B-01", "Railings and balustrades", "m", 250, "USD", ""),
            R("D-01", "Doors", "nr", 900, "USD", ""),
            R("W-01", "Windows", "nr", 700, "USD", ""),
        };
        var readMe = new List<object?[]>
        {
            new object?[] { "Sketch ProMax rate table: how to fill it" },
            new object?[] { "Sheet 1 (Rates) is what cost_estimate reads; keep it first. One row per rate; the header names matter, the order does not." },
            new object?[] { "" },
            new object?[] { "Column", "Needed", "What it is" },
            new object?[] { "Ref", "no", "The item code. Rows with the same Ref are the same item in other regions or currencies." },
            new object?[] { "Description", "yes", "Words that say what is measured: concrete, reinforcement, steel, formwork, piles, walls, beams, columns, slabs, foundations, railings, doors, windows. Without a Category the words choose the elements." },
            new object?[] { "Unit", "yes", "m3, m2, m, nr, kg or t (cum, sqm, lm, no, each and tonne are read too). Lump sums can't be measured from the model; add them as prelims or contingency." },
            new object?[] { "Rate", "yes", "The rate per unit, a number (1,234.5 or 1.234,5)." },
            new object?[] { "Currency", "no", "e.g. USD, EUR, AED. Choose one with currency; rows in another are converted with exchange_rates, else left out." },
            new object?[] { "Region", "no", "Blank: applies everywhere. With regions in the table, choose one with region: its own rows replace the general ones." },
            new object?[] { "Category", "no", "Revit category (Floors, Walls, Structural Framing, Structural Columns, Structural Foundations, Railings ...), several with commas." },
            new object?[] { "Type, Level, Material", "no", "Narrow the elements: part of the type or material name (* wildcards), the exact level name." },
            new object?[] { "Notes", "no", "Shown in the cost plan's basis." },
            new object?[] { "" },
            new object?[] { "Measuring: each row is measured as boq_check measures a BOQ item (net Revit volumes; NRM2, POMI or CESMM4 void rules on request). The cost plan's Basis sheet lists every element with its working; each line links to its rows." },
            new object?[] { "Add-ons, in this order: prelims % of the measured works; contingency % of works plus prelims; escalation at an annual % compounded from the base date to the target date; VAT % of everything above." },
            new object?[] { "The table is remembered for the model, with its region and currency, so later runs use it until another is given." },
        };
        return [("Rates", rates), ("Read me", readMe)];
    }

    /// <summary>
    /// A small rate table for the tests and the field test: two regions (North with its own slab rate, South), a general
    /// row, walls priced in EUR (converted with an exchange rate) and the common units. Round numbers, not prices.
    /// </summary>
    public static List<object?[]> SampleTable() =>
    [
        ["Ref", "Description", "Unit", "Rate", "Currency", "Region", "Category", "Notes"],
        ["T-01", "Concrete in suspended slabs", "m3", 100, "USD", "", "Floors", "general"],
        ["T-01", "Concrete in suspended slabs", "m3", 120, "USD", "North", "Floors", "North's own rate"],
        ["T-01", "Concrete in suspended slabs", "m3", 90, "USD", "South", "Floors", "South's own rate"],
        ["T-02", "Concrete in walls", "m3", 200, "EUR", "", "Walls", "priced in EUR"],
        ["T-03", "Concrete in beams", "m3", 150, "USD", "", "Structural Framing", ""],
        ["T-04", "Formwork to soffits of slabs", "m2", 30, "USD", "", "Floors", ""],
        ["T-05", "Reinforcement", "t", 1000, "USD", "", "", "modelled bars"],
        ["T-06", "Railings", "m", 250, "USD", "", "", ""],
        ["T-07", "Doors", "nr", 500, "USD", "", "", ""],
    ];

    // ---------------------------------------------------------------- add-ons

    public sealed class AddOns
    {
        public double PrelimsPct, ContingencyPct, VatPct, EscalationPctPerYear;
        public DateTime? BaseDate, TargetDate;
    }

    public sealed record Breakdown(double Measured, double Prelims, double Contingency, double Escalation, double Vat, double Years, double EscalationFactor)
    {
        public double BeforeVat => Measured + Prelims + Contingency + Escalation;
        public double Total => BeforeVat + Vat;
    }

    /// <summary>Years between two dates (365.25 days a year).</summary>
    public static double Years(DateTime from, DateTime to) => (to.Date - from.Date).TotalDays / 365.25;

    /// <summary>Compound escalation: (1 + annual %)^years from the base date to the target date; 1 without a rate or dates, or when the target is not later.</summary>
    public static double EscalationFactor(double pctPerYear, DateTime? from, DateTime? to)
    {
        if (pctPerYear == 0 || from is null || to is null) return 1;
        var y = Years(from.Value, to.Value);
        return y <= 0 ? 1 : Math.Pow(1 + pctPerYear / 100, y);
    }

    /// <summary>Checks the add-ons: percentages from 0 to 100 (escalation -20 to 50 a year), and both dates with an escalation rate.</summary>
    public static void Validate(AddOns a)
    {
        void Pct(string name, double v, double min = 0, double max = 100)
        {
            if (double.IsNaN(v) || v < min || v > max) throw new ArgumentException($"{name} is a percentage from {F(min, 0)} to {F(max, 0)} (got {F(v, 2)}).");
        }
        Pct("prelims_percent", a.PrelimsPct);
        Pct("contingency_percent", a.ContingencyPct);
        Pct("vat_percent", a.VatPct);
        Pct("escalation_percent_per_year", a.EscalationPctPerYear, -20, 50);
        if (a.EscalationPctPerYear != 0 && a.TargetDate is null) throw new ArgumentException("Escalation needs escalation_to (the target date, e.g. the mid-point of construction, yyyy-mm-dd).");
    }

    /// <summary>
    /// The add-ons on the measured works: prelims = works × prelims %; contingency = (works + prelims) × contingency %;
    /// escalation = (works + prelims + contingency) × (factor − 1); VAT = everything before it × VAT %.
    /// </summary>
    public static Breakdown Apply(double measured, AddOns a)
    {
        var prelims = measured * a.PrelimsPct / 100;
        var contingency = (measured + prelims) * a.ContingencyPct / 100;
        var factor = EscalationFactor(a.EscalationPctPerYear, a.BaseDate, a.TargetDate);
        var escalation = (measured + prelims + contingency) * (factor - 1);
        var vat = (measured + prelims + contingency + escalation) * a.VatPct / 100;
        var years = a.BaseDate is { } b && a.TargetDate is { } t ? Math.Max(0, Years(b, t)) : 0;
        return new Breakdown(measured, prelims, contingency, escalation, vat, years, factor);
    }

    /// <summary>The add-ons as summary rows: label, amount, basis.</summary>
    public static List<(string Label, double Amount, string Basis)> AddOnRows(Breakdown b, AddOns a, string currency) =>
    [
        ("Measured works", b.Measured, "the cost plan lines (model quantities × rates)"),
        ($"Preliminaries {F(a.PrelimsPct, 1)}%", b.Prelims, "of the measured works"),
        ($"Contingency {F(a.ContingencyPct, 1)}%", b.Contingency, "of works and preliminaries"),
        ($"Escalation {F(a.EscalationPctPerYear, 2)}% a year", b.Escalation,
            a.BaseDate is { } bd && a.TargetDate is { } td ? $"{bd:yyyy-MM-dd} to {td:yyyy-MM-dd}, {F(b.Years, 2)} years compounded (× {F(b.EscalationFactor, 4)})" : "none"),
        ("Total before VAT", b.BeforeVat, ""),
        ($"VAT {F(a.VatPct, 1)}%", b.Vat, "of the total before VAT"),
        ($"Total {currency}".Trim(), b.Total, ""),
    ];

    // ---------------------------------------------------------------- lines and rates per unit

    /// <summary>One cost plan line: a rate measured in the model.</summary>
    public sealed class Line
    {
        public string Ref = "", Description = "", Unit = "", RateSource = "", Notes = "";
        public double Qty, Rate, Cost;
        /// <summary>concrete, rebar, steel, formwork or other (for the rates per m³ and per tonne).</summary>
        public string Group = "other";
        /// <summary>Its cost by level and by zone.</summary>
        public Dictionary<string, double> ByLevel = new(StringComparer.OrdinalIgnoreCase), ByZone = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Its cost by kind of element (wall, beam, pile, railing ...), for the rates per linear metre.</summary>
        public Dictionary<string, double> ByKind = new(StringComparer.OrdinalIgnoreCase);
        public int Elements;
        /// <summary>Its rows on the Basis sheet (Excel row numbers), 0 when none.</summary>
        public int BasisFirst, BasisLast;
        public string? Error;
    }

    private const string RebarWords = @"\breinforcement\b|\brebars?\b|\bbars?\b|\bmesh\b|\bfabric\b";

    /// <summary>The line's group from its unit (m3, m2, m, nr, kg, t) and description.</summary>
    public static string GroupOf(string description, string unit)
    {
        bool Has(string p) => Regex.IsMatch(description ?? "", p, RegexOptions.IgnoreCase);
        return unit switch
        {
            "kg" or "t" => Has(RebarWords) ? "rebar" : "steel",
            "m3" => Has(@"block|masonry|brick|excavat|\bfill(ing)?\b|backfill|screed|asphalt|timber|\bsand\b|gravel|aggregate(?! concrete)|\brock\b|\bstone\b|\bsoil\b|earth|insulation|polystyrene") ? "other" : "concrete",
            "m2" => Has(@"formwork|shuttering|\bforms?\b") ? "formwork" : "other",
            _ => "other",
        };
    }

    /// <summary>The kind of element for the rates per linear metre, from its Revit category name (and whether it is a pile).</summary>
    public static string KindOf(string category, bool pile = false)
    {
        if (pile) return "pile";
        var c = (category ?? "").ToLowerInvariant();
        return c switch
        {
            "walls" => "wall",
            "structural framing" => "beam",
            "structural columns" or "columns" => "column",
            "floors" => "slab",
            "structural foundations" => "foundation",
            "railings" or "top rails" or "handrails" => "railing",
            "stairs" => "stair",
            "doors" => "door",
            "windows" => "window",
            _ => "other",
        };
    }

    /// <summary>The model's measures the rates are divided by, with where each came from.</summary>
    public sealed class Basis
    {
        public double GfaM2;
        public string GfaSource = "";
        public Dictionary<string, double> GfaByLevel = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, double> ZoneAreaM2 = new(StringComparer.OrdinalIgnoreCase);
        public string ZoneSource = "";
        /// <summary>Length in m of the measured elements by kind (wall, beam, pile, railing), and of the quay or berth.</summary>
        public Dictionary<string, double> LengthM = new(StringComparer.OrdinalIgnoreCase);
        public double QuayM;
        public string QuaySource = "";
    }

    public sealed record UnitRate(string Section, string What, string Per, double Cost, double Quantity, double Rate, string Basis);

    private static readonly (string Kind, string Label)[] PerMetre = [("wall", "Walls"), ("beam", "Beams"), ("pile", "Piles"), ("railing", "Railings")];

    /// <summary>
    /// Rates per unit from the lines (measured works, before add-ons, unless said all-in): per m² GFA in total, by level
    /// and by zone; per linear m of walls, beams, piles and railings (their lines' cost over their length) and of the quay
    /// or berth (all-in and works); per m³ of concrete (concrete lines, and all works over the concrete); per tonne of
    /// steel and of rebar. A measure that is zero gives no row.
    /// </summary>
    public static List<UnitRate> UnitRates(IReadOnlyList<Line> lines, Breakdown b, Basis basis)
    {
        var list = new List<UnitRate>();
        var works = b.Measured;
        void Add(string section, string what, string per, double cost, double qty, string from)
        {
            if (qty > 1e-9 && Math.Abs(cost) > 1e-9) list.Add(new UnitRate(section, what, per, cost, qty, cost / qty, from));
        }
        Add("Per m² GFA", "All-in (with add-ons)", "m² GFA", b.Total, basis.GfaM2, basis.GfaSource);
        Add("Per m² GFA", "Measured works", "m² GFA", works, basis.GfaM2, basis.GfaSource);
        var byLevel = Sum(lines.Select(l => l.ByLevel));
        foreach (var kv in byLevel.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            Add("Per m² GFA by level", kv.Key.Length > 0 ? kv.Key : "(no level)", "m² GFA", kv.Value, basis.GfaByLevel.TryGetValue(kv.Key, out var a) ? a : 0, basis.GfaSource);
        var byZone = Sum(lines.Select(l => l.ByZone));
        foreach (var kv in byZone.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            Add("Per m² by zone", kv.Key, "m² of zone", kv.Value, basis.ZoneAreaM2.TryGetValue(kv.Key, out var z) ? z : 0, basis.ZoneSource);
        var byKind = Sum(lines.Select(l => l.ByKind));
        foreach (var (kind, label) in PerMetre)
            Add("Per linear m", label, "m", byKind.TryGetValue(kind, out var c) ? c : 0, basis.LengthM.TryGetValue(kind, out var m) ? m : 0, "their lines' cost over their measured length");
        Add("Per linear m", "Quay or berth, all-in", "m of quay", b.Total, basis.QuayM, basis.QuaySource);
        Add("Per linear m", "Quay or berth, measured works", "m of quay", works, basis.QuayM, basis.QuaySource);
        var concrete = lines.Where(l => l.Group == "concrete" && l.Error is null).ToList();
        var m3 = concrete.Sum(l => l.Qty);
        Add("Per m³ of concrete", "Concrete items", "m³", concrete.Sum(l => l.Cost), m3, "the concrete lines");
        Add("Per m³ of concrete", "All measured works over the concrete", "m³", works, m3, "frame rate: every line over the concrete volume");
        Add("Per tonne", "Structural steel", "t", lines.Where(l => l.Group == "steel" && l.Error is null).Sum(l => l.Cost), Tonnes(lines, "steel"), "the steel lines");
        Add("Per tonne", "Reinforcement", "t", lines.Where(l => l.Group == "rebar" && l.Error is null).Sum(l => l.Cost), Tonnes(lines, "rebar"), "the reinforcement lines");
        return list;
    }

    /// <summary>Tonnes in a group's lines (kg lines divided by 1000).</summary>
    public static double Tonnes(IEnumerable<Line> lines, string group) =>
        lines.Where(l => l.Group == group && l.Error is null).Sum(l => l.Unit == "kg" ? l.Qty / 1000 : l.Qty);

    private static Dictionary<string, double> Sum(IEnumerable<Dictionary<string, double>> parts)
    {
        var d = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in parts) foreach (var kv in p) d[kv.Key] = (d.TryGetValue(kv.Key, out var v) ? v : 0) + kv.Value;
        return d;
    }

    /// <summary>Whether a plan point lies inside a polygon (even-odd rule; points on an edge may go either way).</summary>
    public static bool Inside(double x, double y, IReadOnlyList<(double X, double Y)> poly)
    {
        var inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            var (xi, yi) = poly[i];
            var (xj, yj) = poly[j];
            if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) inside = !inside;
        }
        return inside;
    }

    /// <summary>Area of a plan polygon (shoelace), always positive.</summary>
    public static double Area(IReadOnlyList<(double X, double Y)> poly)
    {
        var a = 0.0;
        for (var i = 0; i < poly.Count; i++) { var p = poly[i]; var q = poly[(i + 1) % poly.Count]; a += p.X * q.Y - q.X * p.Y; }
        return Math.Abs(a / 2);
    }

    // ---------------------------------------------------------------- programme, options and the what-if delta

    /// <summary>Output of one crew per trade in a week, for the rough frame programme. Rules of thumb; replace with the contractor's.</summary>
    public sealed class Outputs
    {
        public double ConcreteM3PerWeek = 150, RebarTPerWeek = 25, SteelTPerWeek = 40, PrecastM3PerWeek = 250;
    }

    /// <summary>
    /// A rough frame programme in weeks: concrete, reinforcement, steel and precast at one crew's weekly output each,
    /// one after the other. An index to compare options with, not a programme.
    /// </summary>
    public static double FrameWeeks(double concreteM3, double rebarT, double steelT, double precastM3, Outputs? o = null)
    {
        o ??= new Outputs();
        double W(double q, double rate) => rate > 0 ? Math.Max(0, q) / rate : 0;
        return W(concreteM3, o.ConcreteM3PerWeek) + W(rebarT, o.RebarTPerWeek) + W(steelT, o.SteelTPerWeek) + W(precastM3, o.PrecastM3PerWeek);
    }

    /// <summary>One option's figures: cost (measured works, before add-ons), A1-A3 carbon and frame weeks.</summary>
    public sealed class Figures
    {
        public string Name { get; set; } = "";
        public string Source { get; set; } = "";
        public double Cost { get; set; }
        public double CarbonKg { get; set; }
        public double Weeks { get; set; }
        public double ConcreteM3 { get; set; }
        public double RebarT { get; set; }
        public double SteelT { get; set; }
        public string Notes { get; set; } = "";
    }

    public sealed record OptionRow(Figures Option, double Total, double CostChange, double? CostPercent, double CarbonChangeKg, double? CarbonPercent, double WeeksChange,
        bool Cheapest, bool LowestCarbon, bool Fastest);

    /// <summary>2 or 3 options side by side, each against the first: total with the add-ons, change in cost, carbon and weeks, and the best of each.</summary>
    public static List<OptionRow> Compare(IReadOnlyList<Figures> options, AddOns a)
    {
        if (options.Count is < 2 or > 3) throw new ArgumentException($"Compare 2 or 3 options (got {options.Count}).");
        var totals = options.Select(o => Apply(o.Cost, a).Total).ToList();
        var first = options[0];
        var t0 = totals[0];
        double? Pct(double before, double after) => Math.Abs(before) > 1e-9 ? (after - before) / Math.Abs(before) * 100 : null;
        var minCost = totals.Min();
        var minCarbon = options.Min(o => o.CarbonKg);
        var minWeeks = options.Min(o => o.Weeks);
        return options.Select((o, i) => new OptionRow(o, totals[i], totals[i] - t0, Pct(t0, totals[i]), o.CarbonKg - first.CarbonKg, Pct(first.CarbonKg, o.CarbonKg),
            o.Weeks - first.Weeks, Math.Abs(totals[i] - minCost) < 1e-6, Math.Abs(o.CarbonKg - minCarbon) < 1e-6, Math.Abs(o.Weeks - minWeeks) < 1e-9)).ToList();
    }

    public sealed record DeltaRow(string Metric, string Unit, double Current, double WhatIf, double Change, double? Percent);

    /// <summary>The what-if against the current model, metric by metric: change and change % (none when the current value is 0).</summary>
    public static List<DeltaRow> Delta(IEnumerable<(string Metric, string Unit, double Current, double WhatIf)> rows) =>
        rows.Select(r => new DeltaRow(r.Metric, r.Unit, r.Current, r.WhatIf, r.WhatIf - r.Current,
            Math.Abs(r.Current) > 1e-9 ? (r.WhatIf - r.Current) / Math.Abs(r.Current) * 100 : null)).ToList();

    /// <summary>The standard what-if metrics from two sets of figures.</summary>
    public static List<DeltaRow> Delta(Figures current, Figures whatIf, string currency) => Delta(
    [
        ("Concrete", "m³", current.ConcreteM3, whatIf.ConcreteM3),
        ("Reinforcement", "t", current.RebarT, whatIf.RebarT),
        ("Structural steel", "t", current.SteelT, whatIf.SteelT),
        ("Cost (measured works)", currency.Length > 0 ? currency : "cost", current.Cost, whatIf.Cost),
        ("Embodied carbon A1-A3", "tCO2e", current.CarbonKg / 1000, whatIf.CarbonKg / 1000),
        ("Frame programme (rough)", "weeks", current.Weeks, whatIf.Weeks),
    ]);

    /// <summary>
    /// New positions for a row of grid lines: the first (lowest) stays; with spacing each next one is that far from the
    /// one before; with scale every distance from the first is multiplied. Returned in the input order.
    /// </summary>
    public static List<double> GridPositions(IReadOnlyList<double> current, double? spacing, double? scale)
    {
        if ((spacing is null) == (scale is null)) throw new ArgumentException("Give spacing_m or scale for grid_spacing (one of them).");
        if (spacing is <= 0 || scale is <= 0) throw new ArgumentException("spacing_m and scale must be above 0.");
        if (current.Count < 2) throw new ArgumentException("grid_spacing needs two or more parallel grids.");
        var order = current.Select((p, i) => (p, i)).OrderBy(x => x.p).ToList();
        var origin = order[0].p;
        var result = new double[current.Count];
        for (var k = 0; k < order.Count; k++)
            result[order[k].i] = spacing is { } s ? origin + k * s : origin + (order[k].p - origin) * scale!.Value;
        return result.ToList();
    }

    // ---------------------------------------------------------------- text

    public static string F(double v, int dp = 1) => v.ToString("0." + new string('#', Math.Max(0, dp)), CultureInfo.InvariantCulture);

    /// <summary>Money with thousands separators and no decimals.</summary>
    public static string Money(double v, string currency = "") => v.ToString("#,##0", CultureInfo.InvariantCulture) + (currency.Length > 0 ? " " + currency : "");

    /// <summary>
    /// The short chat summary: a title, the total with its add-ons, up to three key rates and the largest lines, then
    /// the options or what-if line. Plain Markdown, at most about ten lines, so it sits in a card.
    /// </summary>
    public static string Card(string title, string currency, Breakdown b, AddOns a, IEnumerable<UnitRate> rates, IEnumerable<Line> lines, string? extra = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"**{title}**");
        var adds = new List<string>();
        if (a.PrelimsPct > 0) adds.Add($"prelims {F(a.PrelimsPct)}%");
        if (a.ContingencyPct > 0) adds.Add($"contingency {F(a.ContingencyPct)}%");
        if (b.Escalation != 0) adds.Add($"escalation {F(a.EscalationPctPerYear, 2)}%/yr to {a.TargetDate:yyyy-MM}");
        if (a.VatPct > 0) adds.Add($"VAT {F(a.VatPct)}%");
        sb.AppendLine($"Total **{Money(b.Total, currency)}** = works {Money(b.Measured)}" + (adds.Count > 0 ? " + " + string.Join(" + ", adds) : ""));
        var key = rates.Where(r => r.Section is "Per m² GFA" or "Per m³ of concrete" or "Per tonne" && r.What is "All-in (with add-ons)" or "Concrete items" or "Reinforcement" or "Structural steel").Take(4)
            .Select(r => $"{Money(r.Rate)}/{r.Per}{(r.What == "All-in (with add-ons)" ? " all-in" : r.What == "Concrete items" ? " concrete" : r.What == "Reinforcement" ? " rebar" : " steel")}").ToList();
        if (key.Count > 0) sb.AppendLine("- " + string.Join(" · ", key));
        var ok = lines.Where(l => l.Error is null && l.Cost > 0).OrderByDescending(l => l.Cost).Take(3).ToList();
        if (ok.Count > 0 && b.Measured > 0)
            sb.AppendLine("- Largest: " + string.Join("; ", ok.Select(l => $"{Short(l.Description, 32)} {Money(l.Cost)} ({F(l.Cost / b.Measured * 100, 0)}%)")));
        var bad = lines.Count(l => l.Error is not null || l.Elements == 0);
        if (bad > 0) sb.AppendLine($"- {bad} line(s) measured nothing or failed: see the cost plan");
        if (!string.IsNullOrWhiteSpace(extra)) sb.AppendLine(extra!.Trim());
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Why what_if won't try changes on a copy of the open model, or null when it can: the copy is a file copy of the saved
    /// model, opened as a document of its own, so the model must be saved and not workshared. A workshared model (a local
    /// file, a central, or a copy detached with its worksets kept) is refused: its copy could reach the central, and a
    /// detached one with worksets not yet saved has no file to copy. Detach and discard worksets, then save, makes a plain model.
    /// </summary>
    public static string? CopyRefusal(bool workshared, bool detached, bool saved)
    {
        const string how = "Open the model with Detach from Central and choose Detach and discard worksets, save it under a new name " +
                           "(e.g. a _detached file), and run what_if there; or use target design_option.";
        if (workshared && detached)
            return "This copy is detached but still has its worksets" + (saved ? "" : " and is not saved") + ", so what_if won't copy it. " + how;
        if (workshared)
            return "This model is workshared, so what_if won't copy or open it (that could act on the central file or on the model you have open). " + how;
        if (!saved) return "Save the model first (File > Save As, under a new name for a detached copy): what_if makes its copy from the saved file.";
        return null;
    }

    public static string Short(string s, int n) => s.Length > n ? s.Substring(0, n - 1) + "…" : s;
}
