using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SketchProMax.Tools;

/// <summary>
/// Berth costing (Berth costing button): for the berths the user picks (each with its exclusions, such as a berth
/// without its RoRo part), the quantities of every bill item measured from the model or typed by hand, their cost at the
/// user's unit costs (editable and saved), and the three files of a submission made in the user's own formats: the cost
/// comparison workbook, the BOQ Excel and the Word back-up calculation (BerthCostExports fills the user's templates).
///
/// The cost is worked out the way the comparison workbook does it: quantity × unit cost per item; bored piles at a rate
/// rebuilt for each berth from its pile diameter and kg/m³ (the given pile rate split into installation, concrete and
/// reinforcement); reinforcement tonnage from the concrete volumes × kg/m³. Nothing of a project is in this code: the
/// berths come from the berth status setup, the rates and templates from the user's files, and each item's rule (how
/// its elements are found in the model) is editable. Saved per project in %APPDATA%\SketchProMax\berth costing.
/// No Revit or WPF here, so the tests compile it.
/// </summary>
internal static class BerthCostMath
{
    // ---- Unit costs -------------------------------------------------------------------------------------------------

    /// <summary>A unit cost. <see cref="Code"/> is the defined name it has in the comparison workbook (R_PIPE).</summary>
    public sealed class Rate
    {
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public string Unit { get; set; } = "";
        public double? Value { get; set; }
        /// <summary>An assumed placeholder (yellow in the workbook), to be replaced by a priced rate.</summary>
        public bool Assumed { get; set; }
    }

    /// <summary>The unit costs of a quay wall bill, by their workbook names. No values: they come from the user's workbook or are typed.</summary>
    public static List<Rate> DefaultRates() =>
    [
        R("R_PIPE", "Steel pipes (combi-wall king piles)", "ton"), R("R_SHEET", "Steel sheet piles", "ton"),
        R("R_PILE_GIVEN", "Bored pile (rate basis diameter and kg/m³)", "lm"), R("R_CONC_COMBI", "Concrete in combi wall", "m³"),
        R("R_CONC_BEAM", "Concrete in beams", "m³"), R("R_CONC_SLAB", "Concrete in slabs", "m³"), R("R_REBAR", "Reinforcement steel", "ton"),
        R("R_CSS", "Cement stabilised sand filling", "m³"), R("R_RCA", "Recycled aggregate concrete filling", "m³"),
        R("R_CASING", "Permanent steel casing", "ton", true), R("R_BLIND", "Blinding concrete", "m³", true), R("R_POLY", "Polystyrene sheets", "m²", true),
        R("R_STEREO", "Stereoform (permanent joint formwork)", "m²", true), R("R_DRIVE", "Drivability analysis (combi wall)", "Nr", true),
        R("R_DLT", "Dynamic pile load test", "Nr", true), R("R_SLT", "Static pile load test", "Nr", true), R("R_SLT200", "Compression static load test to 200 % DWL", "Nr", true),
        R("R_LSIT", "Low-strain integrity test", "Nr", true), R("R_CSL", "Cross-hole sonic logging", "Nr", true), R("R_FENDER", "Fender system", "Nr", true),
        R("R_BOLLARD", "Bollard", "Nr", true), R("R_LADDER", "Steel ladder", "Nr", true), R("R_RING", "Mooring ring", "Nr", true), R("R_PVC", "PVC pipe sleeves", "lm", true),
        R("R_RAIL", "Crane rail incl. fixings", "m", true), R("R_STOPPER", "Crane stopper / buffer", "Nr", true), R("R_TIEDOWN", "Crane tie-down / storm anchor", "Nr", true),
        R("R_STOWPIN", "Crane stow pin", "Nr", true), R("R_PANZER", "Panzerbelt (cable trench cover belt)", "m", true), R("R_TURNPIT", "Turn-over pit", "Nr", true),
        R("R_COLDPIT", "Cold ironing pit", "Nr", true), R("R_WETPIT", "Wet utilities pit", "Nr", true), R("R_ICTPIT", "ICT pit", "Nr", true),
        R("R_DEMO_M3", "Demolition of RC slab (by volume)", "m³", true), R("R_DEMO_M2", "Demolition of RC slab (by area)", "m²", true), R("R_DEMO_PILE", "Demolition of RC pile", "Nr", true),
    ];

    private static Rate R(string code, string name, string unit, bool assumed = false) => new() { Code = code, Name = name, Unit = unit, Assumed = assumed };

    /// <summary>How the given bored pile rate is split, as on the workbook's Pile Rate &amp; Settings sheet.</summary>
    public sealed class PileBasis
    {
        /// <summary>The diameter (m) and reinforcement (kg/m³) the given pile rate is for.</summary>
        public double RefDiameter { get; set; } = 1.2;
        public double RefKg { get; set; } = 165;
        /// <summary>Installation scales with the pile's volume (D²), else with its diameter.</summary>
        public bool ByVolume { get; set; } = true;
    }

    // ---- Bill items ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// A bill item. <see cref="No"/> is its number on the comparison workbook's Quantities sheet. <see cref="Rule"/> finds
    /// its elements in the model (empty: typed by hand); <see cref="Boq"/> finds its row in the BOQ template.
    /// </summary>
    public sealed class Item
    {
        public int No { get; set; }
        public string Group { get; set; } = "";
        public string Name { get; set; } = "";
        public string Unit { get; set; } = "";
        /// <summary>The unit cost's code; "pile:middle", "pile:rear", "pile:barrette" for the rebuilt pile rates.</summary>
        public string RateCode { get; set; } = "";
        public string Rule { get; set; } = "";
        public string Boq { get; set; } = "";
        /// <summary>The heading of its back-up calculation section ("" for none; "+" to go under the previous heading).</summary>
        public string Backup { get; set; } = "";
        /// <summary>The sentence under the heading: how it is billed.</summary>
        public string BackupNote { get; set; } = "";
        /// <summary>Where its section comes in the back-up calculation (lowest first).</summary>
        public int BackupOrder { get; set; }
        /// <summary>Goes in the BOQ and the back-up (ticked); off leaves it out of the files, its cost still counts.</summary>
        public bool InFiles { get; set; } = true;
        /// <summary>The BOQ section it is billed under; "SECTION|Sub-heading" for a sub-heading ({n} is the berth number).</summary>
        public string Section { get; set; } = "";
        /// <summary>Its order in the BOQ (lowest first).</summary>
        public int BoqOrder { get; set; }
        /// <summary>The BOQ unit as the bill writes it (T, m3, m, m2, Nr).</summary>
        public string BoqUnit { get; set; } = "";
        /// <summary>
        /// The BOQ description; filled from the model: {count} {height} {length} {tips} {tip} {od} {id} {t} {dmm} {d} {rebar}
        /// {thicks} {type} {items} {n}. Empty: no row in the BOQ.
        /// </summary>
        public string BoqText { get; set; } = "";
    }

    public static readonly (string Code, string Name)[] Groups =
    [
        ("C1", "Combi-wall steel"), ("C2", "Combi-wall infill"), ("C3", "Bored piles & barrettes"), ("C4", "Superstructure concrete"),
        ("C5", "Superstructure reinforcement"), ("C6", "Minor structural items"), ("C7", "Geotechnical & testing"), ("C8", "Ancillary facilities"),
        ("C9", "Utility provisions"), ("C10", "Preliminaries & demolition"),
    ];

    /// <summary>The items worked out from others: infill reinforcement and superstructure reinforcement (volumes × kg/m³).</summary>
    public const int InfillRebar = 4, SuperRebar = 14;

    /// <summary>
    /// The items of a combi-wall quay bill, numbered as on the comparison workbook. The rules are a start, for models
    /// that mark piles with a Legend parameter (FKS steel, FKC fill, MP middle, RP rear, SP casing) and floors by
    /// thickness; change them in Items when a model is set up another way.
    /// </summary>
    public static List<Item> DefaultItems() =>
    [
        I(1, "C1", "Steel tube piles (king piles)", "ton", "R_PIPE", "cat=Structural Columns; Legend=FKS*; measure=weight; table=tube; group=Legend", "steel tube piles; diameter 1.6",
            "+Steel Combi Wall", "", 20),
        I(2, "C1", "Steel sheet piles AZ (intermediate)", "ton", "R_SHEET", "cat=Generic Models; Legend=SPW*; measure=weight", "steel sheet pile"),
        I(3, "C2", "Combi-wall RC infill", "m³", "R_CONC_COMBI", "cat=Structural Columns; Legend=FKC*; measure=volume; table=core; group=Legend", "Combi-wall reinforced concrete filling",
            "Front combi-wall reinforced concrete filling", "The RC fill occupies the inner bore of the king piles over the filled height from the underside of the capping beam to tip level.", 10),
        I(InfillRebar, "C2", "Combi-wall infill reinforcement (= m³ × kg/m³)", "ton", "R_REBAR", "", ""),
        I(5, "C3", "Middle bored piles", "lm", "pile:middle", "cat=Structural Columns; Legend=MP*; measure=length; table=pile; group=Legend", "Middle Piles",
            "Middle Piles", "Bored piles are billed by linear metre (length basis). Quantity = number of piles × pile length to tip.", 30),
        I(6, "C3", "Rear bored piles", "lm", "pile:rear", "cat=Structural Columns; Legend=RP*; measure=length; table=pile; group=Legend", "Rear Piles",
            "Rear Piles", "Bored piles are billed by linear metre (length basis). Quantity = number of piles × pile length to tip."),
        I(7, "C3", "Rear barrettes", "lm", "pile:barrette", "", ""),
        I(8, "C3", "Permanent steel casing", "ton", "R_CASING", "cat=Structural Columns; Legend=SP-*; measure=weight; table=tube; label=Casing", "steel tube piles; diameter 1.2",
            "+Steel Casing", "", 40),
        I(9, "C4", "Front capping beam", "m³", "R_CONC_BEAM", "cat=Floors; thick=2000,2500,3000; over=Legend=FKS*; measure=volume; table=slab; group=thickness", "Front capping beam",
            "Front Capping Beam", "Capping beams are billed by volume. Volume = Area on plan × thickness."),
        I(10, "C4", "Rear capping beam", "m³", "R_CONC_BEAM", "cat=Floors; thick=2300,3000; over=Legend=RP*; measure=volume; table=slab; group=thickness", "Rear capping beam",
            "Rear Capping Beam", "Capping beams are billed by volume. Volume = Area on plan × thickness."),
        I(11, "C4", "Transverse beams", "m³", "R_CONC_BEAM", "", "Transverse beam"),
        I(12, "C4", "Deck slab", "m³", "R_CONC_SLAB", "cat=Floors; thick=150,300,700,942; type!=*Precast*; Comments!=*PIT*; measure=volume; table=slab; group=number OR cat=Floors; Comments=SLAB THICK*; measure=volume; table=slab; label=Thickening", "=Slab",
            "Deck Slab", "Slabs are billed by volume = plan area × thickness. Due to the slope in the deck an average thickness was considered for each case."),
        I(13, "C4", "Approach slab", "m³", "R_CONC_SLAB", "cat=Floors; type=APPROACH SLAB*; thick=250; measure=volume; table=slab; group=number", "Approach Slab",
            "Approach Slab", "Slabs are billed by volume = plan area × thickness."),
        I(SuperRebar, "C5", "Superstructure reinforcement (= Σ m³ × kg/m³)", "ton", "R_REBAR", "", "Reinforcement Steel"),
        I(15, "C6", "Blinding", "m³", "R_BLIND", "cat=Floors; Comments=BLINDING; measure=volume", "Blinding"),
        I(16, "C6", "Polystyrene sheets", "m²", "R_POLY", "", "Polystrene|Polystyrene"),
        I(17, "C6", "Stereoform", "m²", "R_STEREO", "", "Steroform|Stereoform"),
        I(18, "C7", "Cement stabilised sand (between new & existing wall)", "m³", "R_CSS", "cat=Generic Models; Comments=CEMENT STABILIZED SAND; measure=volume", "Stabilized Sand|Stabilised Sand"),
        I(19, "C7", "Recycled aggregate filling / engineered backfill", "m³", "R_RCA", "", "Recycled aggregate"),
        I(20, "C7", "Drivability analysis - combi wall", "Nr", "R_DRIVE", "", "Drivability"),
        I(21, "C7", "Dynamic pile load test", "Nr", "R_DLT", "", "Dynamic pile load"),
        I(22, "C7", "Static pile load test", "Nr", "R_SLT", "", "^Static pile load"),
        I(23, "C7", "Compression static load test to 200 % DWL", "Nr", "R_SLT200", "", "Compression static"),
        I(24, "C7", "Low-strain integrity test", "Nr", "R_LSIT", "cat=Structural Columns; Legend=MP*,RP*; measure=count", "Low strain"),
        I(25, "C7", "Cross-hole sonic logging", "Nr", "R_CSL", "cat=Structural Columns; Legend=MP*,RP*; measure=count; factor=0.1; round=up", "Cross hole"),
        I(26, "C8", "Fenders", "Nr", "R_FENDER", "cat=Generic Models; Comments=FENDER*; measure=count", "^Fender"),
        I(27, "C8", "Bollards", "Nr", "R_BOLLARD", "cat=Generic Models; Comments=BOLLARD*; measure=count", "^Bollard"),
        I(28, "C8", "Ladders", "Nr", "R_LADDER", "cat=Generic Models; Comments=LADDER*; measure=count", "ladder"),
        I(29, "C8", "Mooring rings", "Nr", "R_RING", "cat=Generic Models; Comments=MOORING RING*; measure=count", "mooring ring"),
        I(30, "C8", "PVC pipes D500", "lm", "R_PVC", "", "PVC"),
        I(31, "C8", "Crane rail", "m", "R_RAIL", "", "crane rail"),
        I(32, "C8", "Crane stoppers / buffers", "Nr", "R_STOPPER", "", "stopper"),
        I(33, "C8", "Crane tie-downs / storm anchors", "Nr", "R_TIEDOWN", "", "tie-down|tie down|storm anchor"),
        I(34, "C8", "Crane stow pins", "Nr", "R_STOWPIN", "", "stow pin"),
        I(35, "C8", "Panzerbelt", "m", "R_PANZER", "", "Panzer"),
        I(36, "C9", "Turn-over pits", "Nr", "R_TURNPIT", "", "Turn-over|Turnover|Turn over"),
        I(37, "C9", "Cold ironing pits", "Nr", "R_COLDPIT", "", "Cold Ironing"),
        I(38, "C9", "Wet utilities pits", "Nr", "R_WETPIT", "", "Wet Utilities"),
        I(39, "C9", "ICT pits", "Nr", "R_ICTPIT", "", "ICT Pit"),
        I(40, "C10", "Demolition of RC slab", "m³", "R_DEMO_M3", "", ""),
        I(41, "C10", "Demolition of RC slab (area)", "m²", "R_DEMO_M2", "", ""),
        I(42, "C10", "Demolition of RC piles", "Nr", "R_DEMO_PILE", "", ""),
    ];

    /// <summary>The BOQ side of each item (section, order, unit, description): the base format, changed in Items.</summary>
    private static readonly Dictionary<int, (string Section, int Order, string Unit, string Text)> BoqBase = new()
    {
        [1] = (S, 10, "T", "Supply, delivery, installation, and quality control testing of steel tube piles of diameter {od} m and thickness {t} mm with minimum yield stress of 355 MPa for {count} piles of height {height} m. This shall include all the accepted works, fixations, connections, and materials needed to complete installation and completion of the steel tube piles as per the project documents, to ensure specified life to first maintenance."),
        [2] = (S, 20, "T", "Supply, delivery, installation, and quality control testing of the steel sheet pile wall of section {type} with minimum yield stress of 355 MPa as per the manufacturer's profile tables or equivalent for {count} steel sheets of length {height} m. This shall include all the accepted works, fixations, connections, and materials needed to complete installation and completion of the sheet piles wall as per the project documents and specifications."),
        [3] = (S, 30, "m3", "Front Combi-wall reinforced concrete filling, {tips} Including Reinforcement ({rebar} Ton)"),
        [15] = (S, 40, "m3", "Blinding"),
        [16] = (S, 50, "m2", "Polystyrene Sheets"),
        [5] = (S, 60, "m", "Middle Piles D={dmm}mm, {tips} Including Reinforcement ({rebar} Ton)"),
        [8] = (S, 70, "T", "Supply, delivery, installation, and quality control testing of steel tube piles of diameter {od} m and thickness {t} mm with minimum yield stress of 355 MPa for {count} piles of height {height} m. This shall include all the accepted works, fixations, connections, and materials needed to complete installation and completion of the steel tube piles as per the project documents, to ensure specified life to first maintenance."),
        [6] = (S, 80, "m", "Rear Piles D={dmm}mm, {tips} Including Reinforcement ({rebar} Ton)"),
        [7] = (S, 85, "m", "Rear Barrettes, {tips} Including Reinforcement ({rebar} Ton)"),
        [9] = (S, 90, "m3", "Front capping beam {thicks}"),
        [10] = (S, 100, "m3", "Rear capping beam {thicks}"),
        [11] = (S, 105, "m3", "Transverse beams"),
        [12] = (S, 110, "m3", "Slab"),
        [13] = (S, 120, "m3", "Approach Slab"),
        [14] = (S, 130, "T", "Reinforcement Steel Grade of 500 MPa For the following items ({items})"),
        [17] = (S, 140, "m2", "Stereoform"),
        [19] = (G, 200, "m3", "Engineered compacted backfilling from the existing level, after removing the existing deck, up to the bottom surface of the new slab"),
        [18] = (G, 210, "m3", "Cemented Stabilized Sand between new and existing front wall as per project specifications"),
        [20] = (G + "|Berth ({n}) Combi wall", 220, "Nr", "Drivability analysis for the Combi-wall to confirm the design capacity on site during construction"),
        [21] = (G + "|Berth ({n}) Combi wall", 230, "Nr", "Dynamic pile load test on the combi wall as per the project specifications"),
        [23] = (G + "|Berth ({n}) Bored piles", 240, "Nr", "Compression static pile load test to be tested to 200 % of the design working load as per project specifications."),
        [22] = (G + "|Berth ({n}) Bored piles", 250, "Nr", "Static pile load test as per the project specifications"),
        [24] = (G + "|Berth ({n}) Bored piles", 260, "Nr", "Low strain integrity testing as per the project specifications"),
        [25] = (G + "|Berth ({n}) Bored piles", 270, "Nr", "Cross hole sonic as per the project specifications"),
        [26] = (A, 300, "Nr", "Fenders"),
        [27] = (A, 310, "Nr", "Bollards"),
        [28] = (A, 320, "Nr", "Supply, delivery, installation, and quality control testing for steel ladder detail as per drawings and project documents, including all plates, anchors, welds, fittings and all components shown in the drawings."),
        [29] = (A, 330, "Nr", "Supply, delivery, installation, and quality control testing for mooring ring detail as per drawings and project documents, including all plates, anchors, welds, fittings and all components shown in the drawings."),
        [30] = (A, 340, "m", "PVC pipes D500"),
        [31] = (A, 350, "m", "Crane rail"),
        [32] = (A, 360, "Nr", "Crane stoppers / buffers"),
        [33] = (A, 370, "Nr", "Crane tie-downs / storm anchors"),
        [34] = (A, 380, "Nr", "Crane stow pins"),
        [35] = (A, 390, "m", "Panzerbelt"),
        [38] = (U, 400, "Nr", "Wet Utilities Pits"),
        [37] = (U, 410, "Nr", "Cold Ironing Pits"),
        [39] = (U, 420, "Nr", "ICT Pits"),
        [36] = (U, 430, "Nr", "Turn-over Pits"),
        [40] = (D, 500, "m3", "Demolition of the existing RC slab"),
        [41] = (D, 510, "m2", "Demolition of the existing RC slab"),
        [42] = (D, 520, "Nr", "Demolition of the existing RC piles"),
    };

    private const string S = "STRUCTURAL ITEMS", G = "GEOTECHNICAL ITEMS", A = "ANCILLARY FACILITIES", U = "UTILITIES PROVISIONS IN CONCRETE", D = "DEMOLITION";

    /// <summary>Gives an item its base BOQ section, order, unit and description (the infill's reinforcement is in the infill's row).</summary>
    internal static Item WithBoq(Item i)
    {
        if (BoqBase.TryGetValue(i.No, out var b)) { i.Section = b.Section; i.BoqOrder = b.Order; i.BoqUnit = b.Unit; i.BoqText = b.Text; }
        else { i.BoqOrder = 1000 + i.No; i.BoqUnit = i.Unit; }
        return i;
    }

    private static Item I(int no, string group, string name, string unit, string rate, string rule, string boq, string backup = "", string note = "", int order = 0) =>
        WithBoq(new() { No = no, Group = group, Name = name, Unit = unit, RateCode = rate, Rule = rule, Boq = boq, Backup = backup, BackupNote = note, BackupOrder = order > 0 ? order : no * 10 });

    // ---- Design parameters ----------------------------------------------------------------------------------------------

    /// <summary>A berth's design parameters, keyed as below; their labels are the comparison workbook's (Quantities, column B).</summary>
    public static readonly (string Key, string Label, string Unit)[] Params =
    [
        ("MiddleD", "Middle pile diameter", "m"), ("MiddleKg", "Middle pile reinforcement", "kg/m³"),
        ("RearD", "Rear pile diameter", "m"), ("RearKg", "Rear pile reinforcement", "kg/m³"),
        ("BarretteArea", "Barrette section area", "m²"), ("BarretteKg", "Barrette reinforcement", "kg/m³"),
        ("InfillKg", "Combi-wall infill reinforcement", "kg/m³"), ("FrontBeamKg", "Front capping beam reinforcement", "kg/m³"),
        ("RearBeamKg", "Rear capping beam reinforcement", "kg/m³"), ("TransverseKg", "Transverse beam reinforcement", "kg/m³"),
        ("DeckKg", "Deck slab reinforcement", "kg/m³"), ("ApproachKg", "Approach slab reinforcement", "kg/m³"),
    ];

    /// <summary>The kg/m³ parameter of each concrete item (superstructure, then the infill).</summary>
    public static readonly (int No, string Kg)[] RebarOf = [(9, "FrontBeamKg"), (10, "RearBeamKg"), (11, "TransverseKg"), (12, "DeckKg"), (13, "ApproachKg")];

    // ---- Berths -----------------------------------------------------------------------------------------------------------

    /// <summary>What was measured in the model for one item: one line per group (a pile mark, a slab thickness).</summary>
    public sealed class Line
    {
        public int No { get; set; }
        public string Label { get; set; } = "";
        public int Count { get; set; }
        /// <summary>Levels as Revit displays them (m), the height between them, diameters (m), area (m²: section or plan), thickness (m).</summary>
        public double Top { get; set; }
        public double Bottom { get; set; }
        public double Height { get; set; }
        public double Diameter { get; set; }
        public double Inner { get; set; }
        public double Area { get; set; }
        public double Thickness { get; set; }
        public double Length { get; set; }
        public double Volume { get; set; }
        public double Weight { get; set; }
        public double Quantity { get; set; }
        /// <summary>The back-up table it goes in: core, tube, pile, slab or none.</summary>
        public string Table { get; set; } = "";
    }

    public sealed class Measurement
    {
        public DateTime At { get; set; }
        public string Model { get; set; } = "";
        public Dictionary<int, double> Quantities { get; set; } = [];
        public List<Line> Lines { get; set; } = [];
        /// <summary>Per item: elements found, or why it couldn't be measured.</summary>
        public Dictionary<int, string> Notes { get; set; } = [];
        /// <summary>Element ids per item (to select them in Revit).</summary>
        public Dictionary<int, List<long>> Ids { get; set; } = [];
    }

    /// <summary>A berth's costing: what the user set (exclusions, length, parameters, hand quantities) and what was measured.</summary>
    public sealed class Berth
    {
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
        public string Site { get; set; } = "";
        public string Package { get; set; } = "";
        /// <summary>How its elements are found (from the berth status setup): Parameter, Scope box or Whole model, the parameter and its value.</summary>
        public string FindBy { get; set; } = BerthStatusMath.FindBy.Parameter;
        public string Parameter { get; set; } = "";
        public string Value { get; set; } = "";
        /// <summary>Scope boxes whose elements are left out (a berth without its RoRo part: "BERTH 9 - PART 1"), with ";" between.</summary>
        public string Exclude { get; set; } = "";
        /// <summary>Why: shown in the comparison's Remarks, the back-up note and the berth's name ("excl. RoRo").</summary>
        public string ExcludeNote { get; set; } = "";
        public double? Length { get; set; }
        public Dictionary<string, double> Params { get; set; } = new(StringComparer.Ordinal);
        /// <summary>Quantities typed by hand, by item number: they replace what the model says.</summary>
        public Dictionary<int, double> Hand { get; set; } = [];
        public Measurement? Measured { get; set; }
        /// <summary>When each file was last made: comparison, boq, backup.</summary>
        public Dictionary<string, DateTime> Made { get; set; } = new(StringComparer.Ordinal);

        /// <summary>The berth's number ("Berth 9" is 9), or "" when it has none.</summary>
        public string Number => Regex.Match(Name, @"\d+") is { Success: true } m ? m.Value.TrimStart('0') is { Length: > 0 } n ? n : "0" : "";
        /// <summary>The berth's name in the files: "Berth 09 (excl. RoRo)".</summary>
        public string Title => (Number.Length > 0 ? "Berth " + Number.PadLeft(2, '0') : Name) + (ExcludeNote.Trim().Length > 0 ? $" ({ExcludeNote.Trim()})" : "");
        public double Param(string key) => Params.TryGetValue(key, out var v) ? v : 0;
    }

    public static class Made
    {
        public const string Comparison = "comparison", Boq = "boq", Backup = "backup";
    }

    // ---- The project file -----------------------------------------------------------------------------------------------

    public sealed class Project
    {
        public List<Rate> Rates { get; set; } = DefaultRates();
        public PileBasis Pile { get; set; } = new();
        public List<Item> Items { get; set; } = DefaultItems();
        public Dictionary<string, Berth> Berths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>
        /// The project's own templates (exported from the base, edited in Excel or Word, imported back; kept copies in the
        /// project folder). Empty: the built-in base format.
        /// </summary>
        public string ComparisonTemplate { get; set; } = "";
        public string BoqTemplate { get; set; } = "";
        public string BackupTemplate { get; set; } = "";
        /// <summary>The project's words on the files (typed once; nothing of a project is in the code).</summary>
        public Details Details { get; set; } = new();
        public string Stage { get; set; } = "Detailed Design";
        public string Currency { get; set; } = "US$";
        /// <summary>Steel and reinforced concrete unit weights (t/m³) for the back-up calculation.</summary>
        public double SteelDensity { get; set; } = 7.85;
        public double ConcreteDensity { get; set; } = 2.5;
        public string OutputFolder { get; set; } = "";
        /// <summary>The window measures the open model again by itself a few seconds after it changes.</summary>
        public bool Follow { get; set; } = true;

        public Rate? RateOf(string code) => Rates.FirstOrDefault(r => string.Equals(r.Code, code, StringComparison.OrdinalIgnoreCase));
        public double RateValue(string code) => RateOf(code)?.Value ?? 0;
        public Item? ItemOf(int no) => Items.FirstOrDefault(i => i.No == no);
    }

    /// <summary>
    /// What the base formats print about the project: the page header lines (left and right), the structure title with
    /// {n} (berth number) and {length}, the document number with {n} / {nn}, and whether the BOQ shows the unit rates.
    /// </summary>
    public sealed class Details
    {
        public string HeaderLeft { get; set; } = "";
        public string HeaderRight { get; set; } = "";
        public string Title { get; set; } = "QUAY WALL - BERTH {n} - LENGTH {length} METER";
        public string DocNumber { get; set; } = "";
        public string Works { get; set; } = "STRUCTURAL WORKS";
        public bool BoqRates { get; set; }
    }

    [ThreadStatic] internal static string? FolderForTests;

    public static string Folder => FolderForTests ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax", "berth costing");

    public static string ProjectFolder(string project) => Path.Combine(Folder, ChecklistProjects.CleanName(project.Length == 0 ? "Project" : project));

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Project Load(string project)
    {
        try
        {
            if (SafeFile.Read(Path.Combine(ProjectFolder(project), "costing.json")) is { } text && JsonSerializer.Deserialize<Project>(text) is { } p)
            {
                // Rates and items added in an update reach projects saved before it.
                foreach (var r in DefaultRates()) if (p.RateOf(r.Code) is null) p.Rates.Add(r);
                foreach (var i in DefaultItems())
                    if (p.ItemOf(i.No) is not { } had) p.Items.Add(i);
                    else if (had.BoqOrder == 0) { had.Section = i.Section; had.BoqOrder = i.BoqOrder; had.BoqUnit = i.BoqUnit; had.BoqText = i.BoqText; }
                p.Details ??= new Details();
                p.Items.Sort((a, b) => a.No.CompareTo(b.No));
                return p;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException) { }
        return new Project();
    }

    public static void Save(string project, Project p) => SafeFile.Write(Path.Combine(ProjectFolder(project), "costing.json"), JsonSerializer.Serialize(p, Json));

    /// <summary>The text of the saved file (to notice a change the chat made), or "".</summary>
    public static string SavedText(string project)
    {
        try { return SafeFile.Read(Path.Combine(ProjectFolder(project), "costing.json")) ?? ""; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    /// <summary>
    /// The berths to cost, from the berth status setup: each berth of a package (a package of one value is its own berth),
    /// found by parameter or scope box. Whole-model packages are left out. Saved settings of a berth are kept.
    /// </summary>
    public static List<Berth> SyncBerths(Project p, BerthStatusMath.Setup? setup)
    {
        if (setup is not null)
            foreach (var unit in BerthStatusMath.Units(setup.Berths))
            {
                if (unit.FindBy is not (BerthStatusMath.FindBy.Parameter or BerthStatusMath.FindBy.ScopeBox) || unit.Value.Trim().Length == 0) continue;
                if (!p.Berths.TryGetValue(unit.Key, out var b)) p.Berths[unit.Key] = b = new Berth { Key = unit.Key };
                b.Name = unit.Name; b.Site = unit.Model; b.Package = unit.Parent.Length > 0 ? unit.Parent : unit.Name;
                b.FindBy = unit.FindBy; b.Parameter = unit.Parameter; b.Value = unit.Value;
            }
        return p.Berths.Values.OrderBy(b => b.Site, StringComparer.OrdinalIgnoreCase).ThenBy(b => int.TryParse(b.Number, out var n) ? n : 999).ThenBy(b => b.Name).ToList();
    }

    /// <summary>A berth added by hand (no berth status setup): found by the parameter's value.</summary>
    public static Berth AddBerth(Project p, string site, string parameter, string value)
    {
        var name = BerthStatusMath.ChildName(value);
        var key = BerthStatusMath.KeyOf(site, name);
        if (!p.Berths.TryGetValue(key, out var b)) p.Berths[key] = b = new Berth { Key = key };
        b.Name = name; b.Site = site; b.Package = name; b.FindBy = BerthStatusMath.FindBy.Parameter; b.Parameter = parameter; b.Value = value;
        return b;
    }

    // ---- Rules ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// One way of finding an item's elements: "cat=Floors; thick=2000,2500; over=Legend=FKS*; Comments!=*PIT*; measure=volume;
    /// table=slab; group=thickness". Other keys are parameters (instance, then type) whose value must match (* wildcards,
    /// several patterns with ","); "!=" must not match. A rule may give several ways with " OR " between.
    /// </summary>
    public sealed class Rule
    {
        public List<string> Categories = [];
        public List<(string Name, string Pattern, bool Not)> Filters = [];
        public List<double> Thick = [];
        public (string Name, string Pattern)? Over;
        public string Measure = "count", Table = "", Group = "", Label = "";
        public double Density = 7.85, Factor = 1;
        public bool RoundUp;
    }

    public static List<Rule> ParseRule(string text)
    {
        var rules = new List<Rule>();
        foreach (var part in Regex.Split(text ?? "", @"\s+OR\s+|\|\|"))
        {
            if (part.Trim().Length == 0) continue;
            var r = new Rule();
            foreach (var clause in part.Split(';'))
            {
                var c = clause.Trim();
                if (c.Length == 0) continue;
                var not = c.Contains("!=");
                var at = not ? c.IndexOf("!=", StringComparison.Ordinal) : c.IndexOf('=');
                if (at <= 0) throw new FormatException($"\"{c}\" isn't name=value.");
                var key = c.Substring(0, at).Trim();
                var value = c.Substring(at + (not ? 2 : 1)).Trim();
                switch (key.ToLowerInvariant())
                {
                    case "cat" or "category": r.Categories.AddRange(value.Split(',').Select(v => v.Trim()).Where(v => v.Length > 0)); break;
                    case "type": r.Filters.Add(("@type", value, not)); break;
                    case "family": r.Filters.Add(("@family", value, not)); break;
                    case "thick" or "thickness":
                        r.Thick.AddRange(value.Split(',').Select(v => double.TryParse(v.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? t : double.NaN).Where(t => !double.IsNaN(t)));
                        break;
                    case "over":
                        var eq = value.IndexOf('=');
                        if (eq <= 0) throw new FormatException($"over needs a parameter and its value: over=Legend=FKS*.");
                        r.Over = (value.Substring(0, eq).Trim(), value.Substring(eq + 1).Trim());
                        break;
                    case "measure": r.Measure = value.ToLowerInvariant(); break;
                    case "table": r.Table = value.ToLowerInvariant(); break;
                    case "group": r.Group = value; break;
                    case "label": r.Label = value; break;
                    case "density": r.Density = Num(value) ?? 7.85; break;
                    case "factor": r.Factor = Num(value) ?? 1; break;
                    case "round": r.RoundUp = value.Equals("up", StringComparison.OrdinalIgnoreCase); break;
                    default: r.Filters.Add((key, value, not)); break;
                }
            }
            if (r.Categories.Count == 0) throw new FormatException("give the category: cat=Structural Columns.");
            if (r.Measure is not ("count" or "length" or "volume" or "area" or "weight")) throw new FormatException($"measure is count, length, volume, area or weight (not {r.Measure}).");
            rules.Add(r);
        }
        return rules;
    }

    /// <summary>Whether the text matches one of the patterns ("FKS*,MP*"; * is any text; case ignored; a pattern without * must match whole).</summary>
    public static bool Like(string value, string patterns)
    {
        var v = (value ?? "").Trim();
        foreach (var raw in (patterns ?? "").Split(','))
        {
            var p = raw.Trim();
            if (p.Length == 0) continue;
            if (Regex.IsMatch(v, "^" + Regex.Escape(p).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase | RegexOptions.Singleline)) return true;
        }
        return false;
    }

    public static double? Num(string? text) =>
        double.TryParse((text ?? "").Trim().Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>A measured quantity after the rule's factor and rounding (counts are whole).</summary>
    public static double Finish(Rule r, double q)
    {
        q *= r.Factor;
        return r.RoundUp ? Math.Ceiling(q - 1e-9) : r.Measure == "count" && r.Factor == 1 ? Math.Round(q) : q;
    }

    /// <summary>A slab's group label: "2.00m THICK." by thickness, "#n" by its place among the thicknesses.</summary>
    public static string ThickLabel(double thicknessM) => thicknessM.ToString("0.00", CultureInfo.InvariantCulture) + "m THICK.";

    /// <summary>Numbers the "#" groups by thickness, thinnest first; other labels stay.</summary>
    public static void NumberGroups(List<Line> lines)
    {
        var numbered = lines.Where(l => l.Label == "#").OrderBy(l => l.Thickness).ToList();
        for (var i = 0; i < numbered.Count; i++) numbered[i].Label = "#" + (i + 1).ToString(CultureInfo.InvariantCulture);
    }

    // ---- Quantities and costs -------------------------------------------------------------------------------------------

    public enum Source { Model, Hand, Worked, None }

    /// <summary>An item's quantity for a berth: by hand, else measured, else worked out (reinforcement), else none.</summary>
    public static (double? Qty, Source From) Quantity(Project p, Berth b, int no)
    {
        if (b.Hand.TryGetValue(no, out var h)) return (h, Source.Hand);
        if (no == InfillRebar) return Q(3) is { } v ? (v * b.Param("InfillKg") / 1000, Source.Worked) : (null, Source.None);
        if (no == SuperRebar)
        {
            double? sum = null;
            foreach (var (n, kg) in RebarOf) if (Q(n) is { } c) sum = (sum ?? 0) + c * b.Param(kg) / 1000;
            return (sum, sum is null ? Source.None : Source.Worked);
        }
        if (b.Measured?.Quantities.TryGetValue(no, out var m) == true) return (m, Source.Model);
        return (null, Source.None);

        double? Q(int n) => Quantity(p, b, n).Qty;
    }

    /// <summary>The bored pile rate per lm for a diameter (m) and reinforcement (kg/m³), rebuilt from the given rate.</summary>
    public static double PileRate(Project p, double diameter, double kg)
    {
        if (diameter <= 0) return 0;
        var basis = p.Pile;
        var conc = p.RateValue("R_CONC_COMBI");
        var rebar = p.RateValue("R_REBAR");
        var refArea = Math.PI / 4 * basis.RefDiameter * basis.RefDiameter;
        var installLm = p.RateValue("R_PILE_GIVEN") - refArea * conc - refArea * basis.RefKg / 1000 * rebar;
        var area = Math.PI / 4 * diameter * diameter;
        var install = basis.ByVolume ? installLm / refArea * area : installLm * diameter / basis.RefDiameter;
        return install + area * (conc + kg / 1000 * rebar);
    }

    /// <summary>A barrette's rate per lm: installation per m³ × section area, plus its concrete and reinforcement.</summary>
    public static double BarretteRate(Project p, double area, double kg)
    {
        if (area <= 0) return 0;
        var basis = p.Pile;
        var conc = p.RateValue("R_CONC_COMBI");
        var rebar = p.RateValue("R_REBAR");
        var refArea = Math.PI / 4 * basis.RefDiameter * basis.RefDiameter;
        var installM3 = (p.RateValue("R_PILE_GIVEN") - refArea * conc - refArea * basis.RefKg / 1000 * rebar) / refArea;
        return area * (installM3 + conc + kg / 1000 * rebar);
    }

    public static double RateFor(Project p, Berth b, Item item) => item.RateCode switch
    {
        "pile:middle" => PileRate(p, b.Param("MiddleD"), b.Param("MiddleKg")),
        "pile:rear" => PileRate(p, b.Param("RearD"), b.Param("RearKg")),
        "pile:barrette" => BarretteRate(p, b.Param("BarretteArea"), b.Param("BarretteKg")),
        _ => p.RateValue(item.RateCode),
    };

    public sealed record CostLine(Item Item, double? Qty, Source From, double Rate, double Cost);

    public static List<CostLine> Costs(Project p, Berth b) =>
        p.Items.Select(i =>
        {
            var (q, from) = Quantity(p, b, i.No);
            var rate = RateFor(p, b, i);
            return new CostLine(i, q, from, rate, (q ?? 0) * rate);
        }).ToList();

    public sealed record Totals(double Structural, double Prelims, double PerLm, Dictionary<string, double> ByGroup);

    /// <summary>Structural works (C1-C9), preliminaries and demolition as billed (C10), US$ per linear metre of structural works.</summary>
    public static Totals Total(Project p, Berth b)
    {
        var lines = Costs(p, b);
        var groups = Groups.ToDictionary(g => g.Code, g => lines.Where(l => l.Item.Group == g.Code).Sum(l => l.Cost), StringComparer.Ordinal);
        var structural = groups.Where(g => g.Key != "C10").Sum(g => g.Value);
        return new Totals(structural, groups["C10"], b.Length is > 0 ? structural / b.Length.Value : 0, groups);
    }

    /// <summary>
    /// How complete a berth's costing is (%): the items that should have a quantity (a rule, a hand value or a worked one)
    /// against those that have it, with the length and the rates the berth uses. Null before anything was measured or typed.
    /// </summary>
    public static double? Completeness(Project p, Berth b)
    {
        if (b.Measured is null && b.Hand.Count == 0) return null;
        var wanted = p.Items.Where(i => i.Rule.Trim().Length > 0 || b.Hand.ContainsKey(i.No) || i.No is InfillRebar or SuperRebar).ToList();
        var done = wanted.Count(i => Quantity(p, b, i.No).Qty is not null && (RateFor(p, b, i) > 0));
        var total = wanted.Count + 1;
        if (b.Length is > 0) done++;
        return BerthStatusMath.Part(done, total);
    }

    /// <summary>What still stops a berth's costing from being complete, in plain words.</summary>
    public static List<string> Missing(Project p, Berth b)
    {
        var list = new List<string>();
        if (b.Length is not > 0) list.Add("the quay length");
        if (b.Measured is null) list.Add("a reading of the model (Measure)");
        foreach (var i in p.Items)
        {
            var (q, _) = Quantity(p, b, i.No);
            if (i.Rule.Trim().Length > 0 && q is null) list.Add($"{i.Name}: not found in the model" + (b.Measured?.Notes.TryGetValue(i.No, out var why) == true ? $" ({why})" : ""));
            if (q is > 0 && RateFor(p, b, i) <= 0) list.Add($"{i.Name}: no unit cost" + (i.RateCode.StartsWith("pile:", StringComparison.Ordinal) ? " (pile diameter and kg/m³, and the pile, concrete and rebar rates)" : ""));
        }
        return list;
    }

    /// <summary>The reinforcement of each concrete element of a berth (the back-up's last table and the BOQ's "(866 Ton)").</summary>
    public sealed record RebarRow(string Name, double Kg, double Volume, double Weight, int No);

    public static List<RebarRow> Rebar(Project p, Berth b)
    {
        var rows = new List<RebarRow>();
        void Add(string name, int no, double kg, double volume) { if (volume > 0) rows.Add(new(name, kg, volume, volume * kg / 1000, no)); }
        double Q(int no) => Quantity(p, b, no).Qty ?? 0;
        Add("CombiWall", 3, b.Param("InfillKg"), Q(3));
        Add("Middle Piles", 5, b.Param("MiddleKg"), Q(5) * Math.PI / 4 * Math.Pow(b.Param("MiddleD"), 2));
        Add("Rear Piles", 6, b.Param("RearKg"), Q(6) * Math.PI / 4 * Math.Pow(b.Param("RearD"), 2));
        Add("Front Capping Beam", 9, b.Param("FrontBeamKg"), Q(9));
        Add("Rear Capping Beam", 10, b.Param("RearBeamKg"), Q(10));
        Add("Transverse Beams", 11, b.Param("TransverseKg"), Q(11));
        Add("Deck Slab", 12, b.Param("DeckKg"), Q(12));
        Add("Approach Slab", 13, b.Param("ApproachKg"), Q(13));
        return rows;
    }

    // ---- BOQ descriptions --------------------------------------------------------------------------------------------------

    /// <summary>The short name of an element in a BOQ sentence ("MP1-DS01a" → "MP1").</summary>
    private static string Short(string label) { var t = label.Trim(); var dash = t.IndexOf('-'); return dash > 0 ? t.Substring(0, dash) : t; }

    private static string N(double v, int dp) => Math.Round(v, dp).ToString(dp == 0 ? "0" : "0." + new string('#', dp), CultureInfo.InvariantCulture);

    /// <summary>
    /// An item's BOQ description for a berth: its <see cref="Item.BoqText"/> with the numbers from the model (count,
    /// typical height, tip levels, diameters, wall thickness, reinforcement tonnage, thicknesses, type). A number the
    /// model doesn't give shows as "…" and is listed in <paramref name="unknown"/>.
    /// </summary>
    public static string Describe(Project p, Berth b, Item item, List<string>? unknown = null)
    {
        var lines = (b.Measured?.Lines ?? []).Where(l => l.No == item.No && l.Count > 0).ToList();
        string Val(string key)
        {
            switch (key)
            {
                case "n": return b.Number.Length > 0 ? b.Number : b.Name;
                case "count": return lines.Count > 0 ? lines.Sum(l => l.Count).ToString(CultureInfo.InvariantCulture) : "";
                case "height" or "length":
                    return lines.Count > 0 ? N(lines.GroupBy(l => Math.Round(l.Height, 1)).OrderByDescending(g => g.Sum(l => l.Count)).ThenByDescending(g => g.Key).First().Key, 1) : "";
                case "tip":
                    return lines.Count > 0 ? lines.GroupBy(l => Math.Round(l.Bottom, 2)).OrderByDescending(g => g.Sum(l => l.Count)).First().Key.ToString("0.00", CultureInfo.InvariantCulture) : "";
                case "tips":
                {
                    if (lines.Count == 0) return "";
                    var tips = lines.GroupBy(l => Math.Round(l.Bottom, 2)).OrderByDescending(g => g.Sum(l => l.Count)).ToList();
                    string Tip(double v) => "Tip Level (" + v.ToString("0.00", CultureInfo.InvariantCulture) + ") CD";
                    if (tips.Count == 1) return Tip(tips[0].Key);
                    // The others first by their elements, the most common last "for Other Piles".
                    var parts = tips.Skip(1).Select(g => Tip(g.Key) + " for " + string.Join(", ", g.Select(l => Short(l.Label)).Distinct()))
                        .Append(Tip(tips[0].Key) + " for Other " + (item.Unit == "m" || item.Name.IndexOf("pile", StringComparison.OrdinalIgnoreCase) >= 0 ? "Piles" : "Elements"));
                    return string.Join(", ", parts);
                }
                case "od": return lines.FirstOrDefault(l => l.Diameter > 0) is { } o ? N(o.Diameter, 3) : "";
                case "id": return lines.FirstOrDefault(l => l.Inner > 0) is { } i ? N(i.Inner, 3) : "";
                case "t": return lines.FirstOrDefault(l => l.Diameter > 0 && l.Inner > 0) is { } w ? N((w.Diameter - w.Inner) / 2 * 1000, 0) : "";
                case "d": return Mode(l => l.Diameter) is { } d ? N(d, 3) : "";
                case "dmm": return Mode(l => l.Diameter) is { } dm ? N(dm * 1000, 0) : "";
                case "thicks":
                {
                    var t = lines.Where(l => l.Thickness > 0).Select(l => Math.Round(l.Thickness * 1000)).Distinct().OrderBy(x => x).ToList();
                    return t.Count > 0 ? string.Join("/", t.Select(x => x.ToString("0", CultureInfo.InvariantCulture))) + "mm thick" : "";
                }
                case "type": return lines.GroupBy(l => l.Label).OrderByDescending(g => g.Sum(l => l.Count)).FirstOrDefault()?.Key ?? "";
                case "rebar":
                {
                    var rows = Rebar(p, b).Where(r => item.No == SuperRebar ? RebarOf.Any(x => x.No == r.No) : r.No == item.No).ToList();
                    if (item.No == SuperRebar && Quantity(p, b, SuperRebar).Qty is { } sq) return N(sq, 0);
                    return rows.Count > 0 ? N(rows.Sum(r => r.Weight), 0) : "";
                }
                case "items":
                {
                    var names = Rebar(p, b).Where(r => RebarOf.Any(x => x.No == r.No)).Select(r => r.No switch
                    {
                        9 => "Front Beam", 10 => "Rear Beam", 11 => "Transverse Beams", 12 => "Slab", 13 => "Approach Slab", _ => r.Name,
                    }).ToList();
                    return names.Count > 0 ? string.Join(" - ", names) : "";
                }
                default: return "{" + key + "}";
            }
        }
        double? Mode(Func<Line, double> of) =>
            lines.Where(l => of(l) > 0).GroupBy(l => Math.Round(of(l), 3)).OrderByDescending(g => g.Sum(l => l.Count)).Select(g => (double?)g.Key).FirstOrDefault();
        return Regex.Replace(item.BoqText ?? "", @"\{(\w+)\}", m =>
        {
            var v = Val(m.Groups[1].Value);
            if (v.Length > 0) return v;
            unknown?.Add($"{b.Title}: {item.Name} has no {m.Groups[1].Value} from the model");
            return "…";
        });
    }

    /// <summary>A project text with the berth's number and length: {n}, {nn} (two digits), {length}.</summary>
    public static string ForBerth(string text, Berth b) =>
        (text ?? "").Replace("{nn}", b.Number.PadLeft(2, '0')).Replace("{n}", b.Number.Length > 0 ? b.Number : b.Name)
            .Replace("{length}", b.Length is { } l ? N(l, 1) : "…");

    // ---- Deliverables in the berth status ----------------------------------------------------------------------------------

    /// <summary>The deliverable of the berth status setup a file is (by its words): costing, bills of quantities, back-up calculations.</summary>
    public static string? DeliverableFor(BerthStatusMath.Setup setup, string made)
    {
        bool Has(BerthStatusMath.Deliverable d, string word) => d.Name.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0;
        var list = setup.Deliverables;
        return made switch
        {
            Made.Comparison => list.FirstOrDefault(d => Has(d, "cost"))?.Name,
            Made.Backup => list.FirstOrDefault(d => Has(d, "back") && (Has(d, "bill") || Has(d, "calc")))?.Name,
            Made.Boq => list.FirstOrDefault(d => (Has(d, "bill") || Has(d, "boq")) && !Has(d, "back"))?.Name,
            _ => null,
        };
    }

    /// <summary>
    /// Marks the made file's deliverable done (100%) on each berth in the berth status, with a comment saying when and
    /// which file. Returns the deliverable's name, or null when the setup has none for it.
    /// </summary>
    public static string? MarkDelivered(BerthStatusMath.Setup setup, BerthStatusMath.State state, IEnumerable<Berth> berths, string made, string file, DateTime when)
    {
        if (DeliverableFor(setup, made) is not { } name) return null;
        foreach (var b in berths)
        {
            var entry = state.For(b.Key).DeliveredFor(name);
            entry.Percent = 100;
            entry.Comment = $"Made by Berth costing {when.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}: {Path.GetFileName(file)}";
        }
        return name;
    }

    // ---- Formatting -------------------------------------------------------------------------------------------------------

    public static string Money(double v, string currency = "US$") =>
        currency + " " + (Math.Abs(v) >= 1e6 ? (v / 1e6).ToString("0.00", CultureInfo.InvariantCulture) + " M" : Math.Round(v).ToString("#,0", CultureInfo.InvariantCulture));

    public static string Qty(double? v) => v is not { } q ? "–" : Math.Abs(q) >= 100 ? q.ToString("#,0", CultureInfo.InvariantCulture) : q.ToString("#,0.##", CultureInfo.InvariantCulture);
}
