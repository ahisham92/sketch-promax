namespace SketchProMax.Tools;

/// <summary>
/// The built-in questions of the technical submission checklist (SubmissionChecklistMath). Each is short enough to read
/// at a glance; the details say what to look at. Ids never change once released (answers are kept by id): reword a
/// question freely, but give a question with a new meaning a new id. Questions for one structure type name it in Types;
/// questions on the "types" page name their type first. Teams name who the answer must be agreed with.
/// </summary>
internal static class SubmissionChecklistBank
{
    private static SubmissionChecklistMath.Question Q(string id, string topic, string text, string details = "", string types = "", string teams = "", string diagram = "") =>
        new(id, topic, text, details, types, teams, diagram);

    // Short names for the type keys, so the lists below stay readable.
    private const string Piled = "piled-deck", Blocks = "gravity-blocks", Caissons = "caissons", Sheets = "sheet-piles", Walls = "retaining-wall",
        Combi = "combi-wall", Relief = "relieving-platform", Dolphins = "dolphins", Trestle = "trestle", RoRo = "roro", Pontoon = "pontoon", DWall = "diaphragm-wall",
        Dredge = "dredging", Rails = "crane-rails", Yard = "pavement";

    private const string Quays = Piled + "," + Blocks + "," + Caissons + "," + Sheets + "," + Combi + "," + Relief + "," + DWall;
    private const string Piles = Piled + "," + Dolphins + "," + Trestle + "," + Pontoon + "," + RoRo + "," + Relief;
    private const string GravityLike = Blocks + "," + Caissons + "," + Walls;
    private const string Embedded = Sheets + "," + Combi + "," + Relief + "," + DWall;
    private const string InWater = Piled + "," + Blocks + "," + Caissons + "," + Sheets + "," + Combi + "," + Relief + "," + Dolphins + "," + Trestle + "," + RoRo + "," + Pontoon + "," + DWall;
    private const string Berths = Piled + "," + Blocks + "," + Caissons + "," + Sheets + "," + Combi + "," + Relief + "," + Dolphins + "," + DWall + "," + Pontoon + "," + RoRo;

    public static readonly SubmissionChecklistMath.Question[] Questions =
    [
        // ---- Design basis and inputs
        Q("basis.dbr", "basis", "Design basis report is the latest approved revision?",
            "The calculations, drawings and bills use the design basis (design life, codes, criteria) of the latest approved revision; any change since is listed."),
        Q("basis.codes", "basis", "Codes and standards are the ones in the design basis?",
            "Each calculation names its code and edition (e.g. BS 6349, Eurocodes, PIANC, EAU, ACI) and they match the design basis."),
        Q("basis.life", "basis", "Design life and importance level stated and used?",
            "Design life (e.g. 50 years) drives durability, corrosion allowance and return periods; check they are the same everywhere."),
        Q("basis.survey", "basis", "Latest topographic and bathymetric survey used?",
            "The survey date and revision are on the drawings; levels and the existing seabed come from it.", teams: "Marine, Survey"),
        Q("basis.geotech", "basis", "Latest geotechnical report and design parameters used?",
            "The soil parameters, layers and water table come from the latest interpretive report; the boreholes used are named.", teams: "Geotechnical"),
        Q("basis.metocean", "basis", "Met-ocean data (tides, waves, currents, wind) from the agreed study?",
            "Return periods and values match the met-ocean study and the design basis.", teams: "Marine", diagram: "levels"),
        Q("basis.vessel", "basis", "Design vessels agreed (largest and smallest)?",
            "Displacement, length, beam, draught, berthing speed and approach; smallest vessel for fender height and coverage.", Berths, "Marine"),
        Q("basis.seismic", "basis", "Seismic hazard and site class agreed?",
            "Peak ground acceleration, site class and the seismic approach (pseudo-static, response spectrum) are stated, or why seismic doesn't govern."),
        Q("basis.comments", "basis", "Every comment from the last submission closed?",
            "The comment response sheet lists each comment with its answer and where it is fixed; nothing is left open without agreement."),
        Q("basis.scope", "basis", "Scope matches the contract and the submission schedule?",
            "What this submission covers (berths, structures, stage) is what the schedule and the scope of work ask for."),
        Q("basis.assumptions", "basis", "Assumptions and open items listed?",
            "Anything assumed (missing data, client decisions) is listed in the report and on the drawings' notes."),
        Q("basis.environment", "basis", "Environmental permit conditions reflected in the design?",
            "Limits from the permit or the environmental study (dredging windows, turbidity, piling noise, disposal) are on the drawings and in the specification.", teams: "Marine, Environmental"),
        Q("basis.materials", "basis", "Material properties (concrete, rebar, steel, rock) as the design basis?",
            "Strengths, grades, unit weights and partial factors used in the calculations match the design basis."),
        Q("basis.criteria", "basis", "Serviceability limits agreed (deflection, settlement, crack width)?",
            "Limits for deck and wall deflection, crane rail tolerances, settlement and crack width, with their source."),
        Q("basis.changes", "basis", "Changes since the last submission listed?",
            "What changed in the design, the inputs or the scope since the last submission, and where it shows."),

        // ---- Levels and coordination
        Q("levels.grading", "levels", "Levels checked with the grading team?",
            "Cope, deck and finished yard levels, falls and the joints with the yard agree with the grading team's latest levels.", teams: "Grading", diagram: "levels"),
        Q("levels.marine", "levels", "Levels checked with the marine team?",
            "Tide levels, design water levels, dredge level, overdredge and tolerance agree with the marine team.", teams: "Marine", diagram: "levels"),
        Q("levels.datum", "levels", "Datum stated and the same everywhere (CD, LAT, MSL)?",
            "Every level says its datum; the conversion between chart datum and land datum is on the general notes.", teams: "Marine, Survey", diagram: "levels"),
        Q("levels.cope", "levels", "Cope or deck level the same in the model, calculations and drawings?",
            "One cope level, with the freeboard above the extreme water level and the wave crest.", Quays + "," + Trestle + "," + Dolphins + "," + RoRo, "Grading, Marine", "levels"),
        Q("levels.dredge", "levels", "Dredge level, overdredge and tolerance agreed and designed for?",
            "The design seabed is dredge level less overdredge less tolerance, plus scour where it applies.", InWater + "," + Dredge, "Marine", "levels"),
        Q("levels.scour", "levels", "Scour allowance agreed (propeller wash, currents)?",
            "Scour depth at the toe from propeller wash and currents, or scour protection that holds it.", InWater, "Marine"),
        Q("levels.grid", "levels", "Grid, coordinates and chainages match the survey and the other teams?",
            "Same coordinate system, quay line, berth chainages and grid in every drawing and model.", teams: "Survey, Grading"),
        Q("levels.civil", "levels", "Drainage outfalls, ducts and pits coordinated with civil?",
            "Openings and sleeves for outfalls, ducts and pits are in the structure where civil needs them.", teams: "Civil"),
        Q("levels.electrical", "levels", "Lighting, shore power and cable routes coordinated with electrical?",
            "Duct banks, draw pits, light pole bases and shore power pits are in the structure and the bills.", teams: "Electrical"),
        Q("levels.mechanical", "levels", "Water, fuel and fire lines coordinated with mechanical?",
            "Pipe supports, hydrant pits and pipe crossings are agreed and shown.", teams: "Mechanical"),
        Q("levels.neighbours", "levels", "Joints with the next berth or existing structure agreed?",
            "Movement joints, levels and loads at the joint with the next section, berth or existing quay are shown on both sides."),
        Q("levels.existing", "levels", "Existing structures and services found and kept clear?",
            "Existing piles, walls, cables and pipes from the surveys are shown and the new works miss them or deal with them."),
        Q("levels.sealevel", "levels", "Sea level rise and settlement allowed for in the cope level?",
            "Sea level rise over the design life and long-term settlement in the cope or deck level, as the design basis.", Quays + "," + Trestle + "," + Dolphins + "," + RoRo, "Marine", "levels"),
        Q("levels.structures", "levels", "Interfaces with buildings and other structural packages coordinated?",
            "Loads, levels and joints shared with buildings, crane rails, culverts or other packages next to this one.", "", "Structural"),

        // ---- Loads
        Q("loads.dead", "loads", "Dead and superimposed dead loads complete?",
            "Self weight, surfacing, services, crane rails, fenders and furniture."),
        Q("loads.live", "loads", "Yard live load (UDL) as the design basis?",
            "Container stacking, general cargo or bulk UDL, with the area it acts on.", Quays + "," + Trestle + "," + Yard + "," + Walls),
        Q("loads.equipment", "loads", "Handling equipment loads (RTG, straddle carrier, reach stacker, trucks)?",
            "Wheel or outrigger loads from the equipment schedule, with their footprints and dynamic factors.", Quays + "," + Trestle + "," + RoRo + "," + Yard),
        Q("loads.cranes", "loads", "Quay crane loads (operating, storm, stowed) from the crane maker?",
            "Corner and wheel loads per case, horizontal loads, tie-down and stowage pin loads.", Quays + "," + Rails),
        Q("loads.berthing", "loads", "Berthing energy and fender reactions calculated?",
            "Energy from the design vessel (factors for mass, eccentricity, softness, berth configuration) and the abnormal factor; reactions from the chosen fender.", Berths, "Marine"),
        Q("loads.mooring", "loads", "Mooring loads (bollard or hook pull) applied?",
            "Bollard rating for the largest vessel, pulled at the governing angles.", Berths, "Marine"),
        Q("loads.wind", "loads", "Wind loads on the structure, cranes and moored vessels?",
            "Wind speed and return period from the design basis; on the moored vessel it sets the bollard and fender loads.", Quays + "," + Dolphins + "," + Trestle + "," + Rails, "Marine"),
        Q("loads.impact", "loads", "Accidental vessel impact on exposed piles or structures checked?",
            "Where a vessel can strike piles, dolphins or a trestle outside the fender line: impact energy and the protection or the damage accepted.", Piles, "Marine"),
        Q("loads.waves", "loads", "Wave and current loads applied?",
            "Wave forces, uplift and slamming under decks, current drag on piles.", InWater, "Marine"),
        Q("loads.earth", "loads", "Earth pressures with surcharge (active, at rest, passive)?",
            "Pressures from the soil parameters, with the yard surcharge behind the wall.", GravityLike + "," + Embedded + "," + Piled),
        Q("loads.water", "loads", "Residual water pressure (tidal lag) behind the wall?",
            "The water level behind the wall stays higher as the tide falls; the difference used is stated.", GravityLike + "," + Embedded, "Marine", "levels"),
        Q("loads.seismic", "loads", "Seismic loads applied (inertia and dynamic earth and water pressure)?",
            "Pseudo-static or dynamic analysis per the design basis.", Quays + "," + Walls + "," + Trestle + "," + Dolphins),
        Q("loads.thermal", "loads", "Shrinkage and creep allowed for?",
            "In long decks and walls: shrinkage and creep strains, joint spacing and the restraint forces they cause."),
        Q("loads.combinations", "loads", "Load combinations and factors as the code (ULS, SLS, accidental)?",
            "Every combination the code asks for is run, and the governing one is shown."),
        Q("loads.temperature", "loads", "Uniform temperature range and gradient applied to the deck and beams?",
            "Temperature rise and fall from the installation temperature, and the top-to-soffit gradient, with the restraint of the raked piles.", Piled + "," + Relief + "," + Trestle + "," + RoRo + "," + Caissons + "," + DWall),
        Q("loads.modifiers", "loads", "Stiffness modifiers for cracked concrete set in the analysis model?",
            "Cracked-section factors on beams, slabs, walls and piles as the code (one set for strength, one for deflection and drift), shown in the calculation."),
        Q("loads.patterns", "loads", "Live and crane loads patterned for the worst effect?",
            "Yard load on alternate spans, crane positions and outrigger cases moved along the berth for the worst moment, shear and pile load."),
        Q("loads.sls", "loads", "SLS combinations run for crack width, deflection and settlement?",
            "Quasi-permanent and frequent combinations for crack width; characteristic ones for deflection, crane rail tolerances and settlement."),
        Q("loads.construction", "loads", "Construction stage and temporary loads combined?",
            "Loads while building: precast placing, wet concrete, piling rigs, partial backfill and the water levels while it is built."),
        Q("loads.reactions", "loads", "Load summary and pile or support reactions tabulated?",
            "One table of unfactored loads per case and the governing reactions, used by the geotechnical and the foundation checks.", "", "Geotechnical"),
        Q("loads.wheels", "loads", "Wheel and outrigger loads spread through the pavement and slab as the code?",
            "Contact areas, dispersal through the surfacing and slab, and dynamic factors on wheel loads.", Quays + "," + Trestle + "," + RoRo + "," + Yard),
        Q("loads.buoyancy", "loads", "Buoyancy and uplift at the lowest and highest water levels allowed for?",
            "Uplift on bases, caissons, pontoons and empty structures, with the water level that governs each check.", GravityLike + "," + Pontoon + "," + DWall + "," + Relief),

        // ---- Geotechnical and foundations
        Q("geo.profile", "geotech", "Soil profile and parameters per location agreed with geotechnical?",
            "The design soil profile at each section, from the nearest boreholes.", teams: "Geotechnical"),
        Q("geo.capacity", "geotech", "Pile capacities (compression, tension, lateral) checked?",
            "Capacities from the agreed method with the factors of safety in the design basis; group effects where piles are close.", Piles, "Geotechnical"),
        Q("geo.toe", "geotech", "Pile toe levels from the capacity calculation shown on the drawings?",
            "Toe levels on the drawings and in the model are the calculated ones, with the founding layer.", Piles, "Geotechnical", "levels"),
        Q("geo.negative", "geotech", "Negative skin friction and settlement of soft layers allowed for?",
            "Where fill is placed over soft soils, the drag load on piles and the settlement.", Piles + "," + Yard + "," + Dredge, "Geotechnical"),
        Q("geo.bearing", "geotech", "Bearing capacity and settlement under the base checked?",
            "Bearing pressure against the allowable, with eccentric and inclined loads; settlement within the limits.", GravityLike, "Geotechnical"),
        Q("geo.global", "geotech", "Overall slope stability (deep slip) checked?",
            "Slip circles through and under the structure, for each load case and water level.", GravityLike + "," + Embedded + "," + Piled + "," + Dredge, "Geotechnical"),
        Q("geo.liquefaction", "geotech", "Liquefaction of fill and soils checked?",
            "Where the seismic design applies: liquefaction potential and the measures taken.", Quays + "," + Dredge + "," + Yard, "Geotechnical"),
        Q("geo.improvement", "geotech", "Ground improvement or soil replacement designed where needed?",
            "Dredging of soft soils, replacement, vibro or drains, with their limits shown.", GravityLike + "," + Dredge + "," + Yard, "Geotechnical"),
        Q("geo.drivability", "geotech", "Pile or sheet drivability checked?",
            "Hammer, refusal criteria and stresses while driving; pre-boring where needed.", Piles + "," + Sheets + "," + Combi, "Geotechnical"),
        Q("geo.testing", "geotech", "Pile load tests and integrity testing specified?",
            "Static or dynamic load tests (number, load, timing) and integrity tests, with the acceptance criteria.", Piles + "," + Combi, "Geotechnical"),
        Q("geo.gaps", "geotech", "Gaps in the ground investigation flagged with further boreholes?",
            "Locations with no borehole close by, or with doubtful data, and the extra investigation asked for.", "", "Geotechnical"),

        // ---- Structure checks: deck on piles
        Q("piled.layout", TopicsTypes, "Pile layout, sizes and rakes as the calculation?", "Bents, spacing, rakes and pile sizes on the drawings match the analysis model.", Piled, diagram: Piled),
        Q("piled.model", TopicsTypes, "Analysis model checked (supports, fixity depth, springs)?", "Point of fixity or soil springs from geotechnical; free length for buckling and P-delta.", Piled, "Geotechnical", Piled),
        Q("piled.deck", TopicsTypes, "Deck slab and beams designed for every load case?", "Bending, shear and punching under wheel and outrigger loads; crack width as the exposure.", Piled, diagram: Piled),
        Q("piled.transverse", TopicsTypes, "Transverse beams designed for the bent's pile reactions and crane loads?", "Each transverse (cross) beam over its bent: bending and shear from the pile reactions, crane wheel and outrigger loads, with the raked piles' horizontal forces.", Piled + "," + Relief + "," + Trestle, diagram: Piled),
        Q("piled.cantilever", TopicsTypes, "Front of the transverse beams checked for fender and bollard loads?", "The seaward end or cantilever past the front pile: fender reaction, bollard pull and the cope's anchorage into the beam.", Piled + "," + Relief, diagram: Piled),
        Q("piled.longitudinal", TopicsTypes, "Longitudinal and crane rail beams continuous over the transverse beams?", "Longitudinal beams and the crane rail beams framing into the transverse beams: continuity, bearing and their joints.", Piled + "," + Relief, diagram: Piled),
        Q("piled.beamjoint", TopicsTypes, "Beam to beam and beam to pile head joints reinforced and buildable?", "Bar congestion where transverse and longitudinal beams cross over a pile head; laps, couplers and the pour sequence.", Piled + "," + Relief + "," + Trestle),
        Q("piled.connection", TopicsTypes, "Pile to deck connection detailed?", "Embedment, dowels or plug and its reinforcement, for compression, tension and moment.", Piled, diagram: Piled),
        Q("piled.uplift", TopicsTypes, "Deck soffit above wave crest, or designed for uplift?", "Air gap between the extreme wave crest and the soffit; otherwise uplift and slamming loads.", Piled, "Marine", "levels"),
        Q("piled.slope", TopicsTypes, "Slope under the deck stable and protected?", "Armour and filter under the deck; slope stability with the piles.", Piled, "Geotechnical, Marine", Piled),
        Q("piled.joints", TopicsTypes, "Expansion joints spaced and detailed?", "Joint spacing from temperature and shrinkage; the joint detail carries the shear across.", Piled + "," + Trestle, diagram: Piled),
        Q("piled.precast", TopicsTypes, "Precast elements and their lifting checked?", "Precast beams or slabs: lifting, temporary stages and stitch details.", Piled + "," + Trestle + "," + Dolphins),

        // ---- Structure checks: steel piles, sheet piles and other steel
        Q("steel.pipepile", TopicsTypes, "Steel pipe piles checked for axial load and bending after corrosion?", "Combined axial and bending on the section left at the end of the design life, per corrosion zone, with buckling over the free length.", Piled + "," + Dolphins + "," + Trestle + "," + RoRo + "," + Pontoon + "," + Relief + "," + Combi, diagram: Piled),
        Q("steel.localbuckling", TopicsTypes, "Pipe wall local buckling (D/t) and driving stresses checked?", "Diameter to wall thickness for local buckling, and the stresses from the hammer at the pile head and toe.", Piled + "," + Dolphins + "," + Trestle + "," + Relief + "," + Combi, "Geotechnical"),
        Q("steel.infill", TopicsTypes, "Pipe pile concrete plug and its bars into the deck designed?", "Plug length, the shear between the plug and the steel, and the bars that tie the pile into the deck or beam.", Piled + "," + Relief + "," + Trestle + "," + Dolphins, diagram: Piled),
        Q("steel.interlock", TopicsTypes, "Sheet pile interlocks and reduced modulus (oblique bending) allowed for?", "U-piles not crimped or welded in pairs lose modulus; interlock strength and declutching while driving.", Sheets + "," + Combi, diagram: Sheets),
        Q("steel.welds", TopicsTypes, "Welds, splices and pile extensions designed and specified?", "Butt welds for pile extensions, their inspection and testing, and where splices may go (not at the peak moment).", Piled + "," + Sheets + "," + Combi + "," + Dolphins + "," + Trestle + "," + Relief),
        Q("steel.tierods", TopicsTypes, "Tie rods and walings checked for grade, threads and corrosion?", "Tie rod net section at the thread, upset ends, sag from fill settlement, waling bolts and their protection.", Sheets + "," + Combi + "," + DWall, diagram: Sheets),
        Q("steel.members", TopicsTypes, "Steel members and connections (frames, bracing, walkways) designed?", "Other steel: walkway and catwalk frames, bracing, platforms and their bolted or welded connections.", Dolphins + "," + Trestle + "," + Pontoon + "," + RoRo),
        Q("steel.fatigue", TopicsTypes, "Fatigue checked where loads repeat (crane rails, ramps, hinges)?", "Stress ranges and detail categories for rails, rail clips, ramp hinges and pontoon guides.", Rails + "," + RoRo + "," + Pontoon),

        // ---- Structure checks: gravity blocks
        Q("blocks.stability", TopicsTypes, "Sliding, overturning and bearing checked at every block layer?", "Each joint between block courses, not only the base, for every load case and water level.", Blocks, diagram: Blocks),
        Q("blocks.friction", TopicsTypes, "Friction between blocks and on the bed as agreed?", "Concrete on concrete and concrete on rubble friction values from the design basis.", Blocks, diagram: Blocks),
        Q("blocks.keys", TopicsTypes, "Shear keys or interlocks between blocks detailed?", "Keys, their tolerances and how the blocks seat.", Blocks, diagram: Blocks),
        Q("blocks.bed", TopicsTypes, "Rubble bed thickness, levelling and tolerance shown?", "Bedding layer, screeding tolerance and the toe of the bed.", Blocks + "," + Caissons, "Marine", Blocks),
        Q("blocks.backfill", TopicsTypes, "Backfill and filter behind the wall stop loss of fines?", "Graded filter or geotextile behind the joints between blocks.", Blocks + "," + Caissons, diagram: Blocks),
        Q("blocks.weight", TopicsTypes, "Heaviest block within the placing crane's capacity?", "Block weights listed with the crane and radius they need.", Blocks, diagram: Blocks),
        Q("blocks.cope", TopicsTypes, "Cope beam designed for fender, bollard and crane loads?", "Cope beam reinforcement and its joints with the blocks.", Blocks + "," + Caissons + "," + Sheets + "," + Combi + "," + Relief + "," + DWall, diagram: Blocks),
        Q("blocks.toe", TopicsTypes, "Toe protection against scour shown?", "Rock or mattress at the toe, its extent and grading.", Blocks + "," + Caissons + "," + Walls, "Marine", Blocks),
        Q("blocks.joints", TopicsTypes, "Vertical joints staggered between block layers?", "Joints offset from layer to layer, and the joint width that stops fill washing through.", Blocks, diagram: Blocks),

        // ---- Structure checks: caissons
        Q("caissons.float", TopicsTypes, "Floating stability during tow and sinking checked?", "Metacentric height in each ballast stage; draft against the water depth on the route.", Caissons, "Marine", Caissons),
        Q("caissons.stability", TopicsTypes, "Sliding, overturning and bearing checked in place?", "With the cell fill, the backfill and the residual water.", Caissons, diagram: Caissons),
        Q("caissons.walls", TopicsTypes, "Cell walls and base slab designed for every stage?", "Casting, launching, towing, sinking, filling and in service.", Caissons, diagram: Caissons),
        Q("caissons.joints", TopicsTypes, "Joints between caissons sealed against loss of fill?", "Joint detail, its filter and how it is closed after placing.", Caissons, diagram: Caissons),
        Q("caissons.fill", TopicsTypes, "Cell fill material, density and placing specified?", "Fill weight used in the stability calculation is the one specified.", Caissons, diagram: Caissons),

        // ---- Structure checks: sheet pile quay
        Q("sheets.embedment", TopicsTypes, "Embedment depth with its factor of safety?", "Free or fixed earth support, with the factor or partial factors in the design basis.", Sheets + "," + Combi + "," + Relief, "Geotechnical", Sheets),
        Q("sheets.section", TopicsTypes, "Section and steel grade enough after corrosion?", "Bending and shear on the section left at the end of the design life.", Sheets + "," + Relief, diagram: Sheets),
        Q("sheets.corrosion", TopicsTypes, "Corrosion allowance or protection per zone?", "Loss rates for the splash, tidal, low water and buried zones; coating or cathodic protection.", Sheets + "," + Combi + "," + Relief + "," + Piled + "," + Dolphins + "," + Trestle, diagram: "levels"),
        Q("sheets.anchor", TopicsTypes, "Tie rods and anchor wall designed and placed outside the active wedge?", "Tie rod force, size, spacing and the anchor wall far enough back.", Sheets + "," + Combi + "," + DWall, "Geotechnical", Sheets),
        Q("sheets.waling", TopicsTypes, "Waling and tie rod connections detailed?", "Waling size, bolts, and the connection at the anchor wall.", Sheets + "," + Combi, diagram: Sheets),
        Q("sheets.tielevel", TopicsTypes, "Tie rod level workable above the tide?", "Tie rods installed in the dry, or the method for installing them below water.", Sheets + "," + Combi, "Marine", "levels"),
        Q("sheets.combi", TopicsTypes, "Combi-wall king piles and infill sheets checked?", "Where a combi-wall is used: king pile capacity and the infill sheets' arching.", Combi, diagram: Sheets),
        Q("sheets.deflection", TopicsTypes, "Wall deflection within the cope and crane rail limits?", "Head deflection at each stage and in service against the limits for the cope, the rails and the pavement behind.", Sheets + "," + Combi + "," + DWall, diagram: Sheets),

        // ---- Structure checks: combi-wall quay
        Q("combi.kingpile", TopicsTypes, "King piles checked for bending, axial load and buckling after corrosion?", "Tube or H king pile section left at the end of the design life, with the axial load from the cope and cranes.", Combi, diagram: Combi),
        Q("combi.infill", TopicsTypes, "Infill sheets checked for arching and their shorter embedment?", "Infill sheet section between the king piles, its toe above the king pile toe, and soil arching between them.", Combi, "Geotechnical", Combi),
        Q("combi.connectors", TopicsTypes, "Connectors between king piles and infill sheets specified?", "Connector type and strength, welded or loose, and the declutching risk while driving.", Combi, diagram: Combi),
        Q("combi.toe", TopicsTypes, "King pile toe level and plugging from the geotechnical report?", "Toe level for the axial and lateral capacity, with open-ended tube plugging assumed or checked.", Combi, "Geotechnical", "levels"),
        Q("combi.driving", TopicsTypes, "Driving sequence, guide frames and tolerances set?", "King piles first with a template, then infills; verticality and position tolerances on the drawings.", Combi, diagram: Combi),

        // ---- Structure checks: relieving platform quay
        Q("relief.platform", TopicsTypes, "Platform slab designed for the fill, surcharge and crane loads?", "Bending and shear in the platform with the fill above it, the yard surcharge and crane rail loads.", Relief, diagram: Relief),
        Q("relief.piles", TopicsTypes, "Bearing piles under the platform checked (capacity, rake, drag load)?", "Vertical and raked piles carrying the platform, with negative skin friction from the fill.", Relief, "Geotechnical", Relief),
        Q("relief.frontwall", TopicsTypes, "Front wall designed for the reduced earth pressure under the platform?", "Earth pressure shielded by the platform over the front wall's height, and the full pressure below.", Relief, "Geotechnical", Relief),
        Q("relief.connection", TopicsTypes, "Connection between the front wall and the platform detailed?", "How the platform holds the front wall's top: dowels, embedded head or bearing.", Relief, diagram: Relief),
        Q("relief.level", TopicsTypes, "Platform level workable above the tide?", "Platform cast in the dry at a level above the working tide, or the method below it.", Relief, "Marine", "levels"),

        // ---- Structure checks: retaining walls
        Q("walls.stability", TopicsTypes, "Sliding, overturning and bearing checked?", "With surcharge and water behind, and the passive resistance taken only where it is safe.", Walls, "Geotechnical", Walls),
        Q("walls.stem", TopicsTypes, "Stem, heel and toe designed for bending, shear and cracks?", "Crack width as the exposure; the starter bars at the base.", Walls, diagram: Walls),
        Q("walls.drainage", TopicsTypes, "Drainage behind the wall (weep holes, drain, filter)?", "So water can't build up behind the wall beyond what the design takes.", Walls + "," + Yard, "Civil", Walls),
        Q("walls.joints", TopicsTypes, "Movement joints and waterstops spaced and detailed?", "Joint spacing and the waterstop where water must not pass.", Walls, diagram: Walls),
        Q("walls.backfill", TopicsTypes, "Backfill material, compaction and surcharge behind the wall specified?", "Backfill type and compaction, with the surcharge used in the design shown on the drawings.", Walls, "Geotechnical", Walls),


        // ---- Structure checks: dolphins
        Q("dolphins.layout", TopicsTypes, "Dolphin layout fits the design vessels?", "Breasting dolphins within the parallel body of the smallest and largest vessel; mooring lines at good angles.", Dolphins, "Marine", Dolphins),
        Q("dolphins.deflection", TopicsTypes, "Pile group deflection within the fender's travel?", "Flexible dolphins: energy shared between piles and fender; deflection limits.", Dolphins, diagram: Dolphins),
        Q("dolphins.hooks", TopicsTypes, "Quick release hooks or bollards rated and fixed?", "Hook rating, number of hooks, and the cap reinforcement around the base.", Dolphins, "Marine", Dolphins),
        Q("dolphins.access", TopicsTypes, "Walkways and access between dolphins designed?", "Walkway spans, handrails and bearings.", Dolphins, diagram: Dolphins),
        Q("dolphins.cap", TopicsTypes, "Pile cap and its connection to the piles designed for berthing and mooring?", "Cap reinforcement, punching at the pile heads, and the fender and hook fixings into the cap.", Dolphins, diagram: Dolphins),

        // ---- Structure checks: approach trestle
        Q("trestle.spans", TopicsTypes, "Spans, bearings and bents as the analysis?", "Span lengths, bearing types and bent spacing match the model.", Trestle, diagram: Trestle),
        Q("trestle.pipes", TopicsTypes, "Pipeline and cable loads and supports agreed?", "Pipe rack loads, anchors and expansion loops.", Trestle, "Mechanical, Electrical", Trestle),
        Q("trestle.traffic", TopicsTypes, "Traffic loads, kerbs and barriers as the design basis?", "Vehicle loads and the parapet or barrier containment.", Trestle, diagram: Trestle),
        Q("trestle.abutment", TopicsTypes, "Abutment at the shore designed?", "Abutment, its approach slab and the joint to the land.", Trestle, "Civil", Trestle),

        // ---- Structure checks: Ro-Ro ramp
        Q("roro.gradient", TopicsTypes, "Ramp gradient workable at every tide?", "Gradient at the highest and lowest tide against the vessels' ramp limits.", RoRo, "Marine", RoRo),
        Q("roro.loads", TopicsTypes, "Ramp designed for the heaviest vehicle and the vessel ramp load?", "Axle loads, ship ramp landing loads and impact.", RoRo, diagram: RoRo),
        Q("roro.hinge", TopicsTypes, "Hinge, bearings or landing area detailed?", "Wearing plates and how the ship ramp lands.", RoRo, diagram: RoRo),

        // ---- Structure checks: floating pontoon
        Q("pontoon.freeboard", TopicsTypes, "Freeboard and stability with the design loads?", "Freeboard with live load and heel; stability when loaded on one side.", Pontoon, "Marine", Pontoon),
        Q("pontoon.guides", TopicsTypes, "Guide piles designed for every tide and the berthing loads?", "Pile length free at the lowest tide, and the restraint at the highest.", Pontoon, "Marine", Pontoon),
        Q("pontoon.gangway", TopicsTypes, "Gangway slope acceptable at the extreme tides?", "Gangway length and its slope at the highest and lowest tide.", Pontoon, "Marine", "levels"),

        // ---- Structure checks: diaphragm or secant pile wall
        Q("dwall.embedment", TopicsTypes, "Embedment and anchor or prop forces checked?", "With the construction stages (excavation, anchors, dredging).", DWall, "Geotechnical", DWall),
        Q("dwall.panels", TopicsTypes, "Panel or pile overlap, joints and waterstops detailed?", "Overlap with the verticality tolerance at the toe.", DWall, diagram: DWall),
        Q("dwall.stages", TopicsTypes, "Every construction stage checked?", "Each excavation, anchor and dredging stage, not only the final one.", DWall + "," + Sheets + "," + Combi, diagram: DWall),
        Q("dwall.trench", TopicsTypes, "Trench stability under the support fluid checked?", "Bentonite or polymer level above the tide and groundwater, guide walls, panel length and the surcharge next to the trench.", DWall, "Geotechnical", DWall),
        Q("dwall.cages", TopicsTypes, "Reinforcement cages buildable for tremie concreting?", "Cage lifting and splicing, bar spacing for concrete flow, cover and spacers, couplers for the capping beam and anchors.", DWall, diagram: DWall),
        Q("dwall.capping", TopicsTypes, "Capping beam and its tie to the panels detailed?", "Capping beam reinforcement, starter bars from the panels and the cope loads it carries.", DWall, diagram: DWall),
        Q("dwall.anchors", TopicsTypes, "Anchors or tie rods: type, test loads and corrosion protection set?", "Ground anchors (free and fixed length) or tie rods to an anchor wall; proof and suitability tests; protection for the design life.", DWall, "Geotechnical", DWall),
        Q("dwall.leakage", TopicsTypes, "Loss of fines through the panel joints prevented?", "Joint waterstops or a filter behind the joints, so tide cycles don't wash the backfill out.", DWall, "Geotechnical"),
        Q("dwall.secant", TopicsTypes, "Secant or contiguous piles: overlap, reinforcement and gaps checked?", "Primary and secondary piles, overlap with the drilling tolerance at the toe, and grouting of gaps.", DWall, diagram: DWall),

        // ---- Structure checks: dredging and reclamation
        Q("dredge.slopes", TopicsTypes, "Dredged side slopes stable and agreed?", "Side slopes per soil layer, with the quay's toe kept clear.", Dredge, "Geotechnical, Marine", Dredge),
        Q("dredge.volumes", TopicsTypes, "Dredging volumes with overdredge and tolerance?", "Volumes from the survey to the design surface, split by soil type, with overdredge.", Dredge, "Marine", Dredge),
        Q("dredge.disposal", TopicsTypes, "Disposal or reuse of dredged material agreed?", "Disposal site or reuse as fill, with the approvals needed.", Dredge, "Marine"),
        Q("dredge.reclamation", TopicsTypes, "Reclamation level, fill and settlement allowed for?", "Fill material, its compaction, and the settlement before the pavement goes on.", Dredge + "," + Yard, "Geotechnical, Grading", Dredge),
        Q("dredge.siltation", TopicsTypes, "Siltation and maintenance dredging allowed for?", "Expected siltation rate and the maintenance dredging it needs, kept within the overdredge allowance.", Dredge, "Marine", Dredge),

        // ---- Structure checks: crane rails
        Q("rails.gauge", TopicsTypes, "Rail gauge and position agreed with the crane maker?", "Gauge, distance from the cope edge, and the outreach.", Rails, diagram: Rails),
        Q("rails.beam", TopicsTypes, "Rail beams designed for wheel loads and deflection limits?", "Bending, shear and deflection under the wheel groups.", Rails, diagram: Rails),
        Q("rails.fixings", TopicsTypes, "Rail type, fixings, pads and tolerances specified?", "Rail section, clips, elastomeric pad and the alignment tolerances.", Rails, diagram: Rails),
        Q("rails.stops", TopicsTypes, "Stoppers, tie-downs and stowage pins placed and designed?", "Positions and their loads from the crane maker.", Rails, diagram: Rails),
        Q("rails.power", TopicsTypes, "Crane power supply, cable trench and earthing coordinated?", "Cable trench or festoon, pits, rail bonding and earthing agreed with the electrical team.", Rails, "Electrical", Rails),

        // ---- Structure checks: yard, pavement and drainage
        Q("yard.pavement", TopicsTypes, "Pavement designed for the equipment and the subgrade?", "Layers from the design wheel loads and the CBR of the fill.", Yard, "Civil", Yard),
        Q("yard.falls", TopicsTypes, "Falls and drainage agreed with the grading team?", "Falls, channel positions and outfalls through the quay.", Yard, "Grading, Civil", Yard),
        Q("yard.joint", TopicsTypes, "Joint between pavement and quay detailed?", "Transition slab or joint where the yard meets the quay.", Yard, "Civil", Yard),
        Q("yard.services", TopicsTypes, "Ducts, pits, lighting masts and reefer points coordinated?", "Positions and covers in the pavement agreed with the electrical and mechanical teams.", Yard, "Electrical, Mechanical"),

        // ---- Durability and materials
        Q("dur.exposure", "durability", "Exposure class per zone (splash, tidal, submerged, buried)?", "Each element's exposure class as the code and the design life.", diagram: "levels"),
        Q("dur.cover", "durability", "Concrete grade, cover and cement as the exposure?", "Grade, nominal cover, cement type and the w/c ratio per element."),
        Q("dur.cracks", "durability", "Crack width limits checked?", "Crack width against the exposure limit in each element."),
        Q("dur.steel", "durability", "Steel grades and their protection specified?", "Structural steel, piles and fittings: grade, coating, galvanising or cathodic protection.", Piles + "," + Sheets + "," + Rails),
        Q("dur.cathodic", "durability", "Cathodic protection designed where the steel needs it?", "Anodes or impressed current: design life, current density per zone, anode positions and bonding.", Piles + "," + Sheets, "Electrical", "levels"),
        Q("dur.rock", "durability", "Rock and fill quality and grading specified?", "Density, strength and grading of armour, core and filter rock.", Piled + "," + Blocks + "," + Caissons + "," + Dredge),
        Q("dur.detailing", "durability", "Laps, anchorage, bar spacing and minimum steel as the code?",
            "Detailing rules on the reinforcement drawings: lap and anchorage lengths, spacing, minimum and maximum steel."),
        Q("dur.massconcrete", "durability", "Early-age thermal cracking of thick pours controlled?",
            "Cement content, pour sizes, temperature limits and the crack control steel for thick caps, beams and blocks."),

        // ---- Marine furniture and safety
        Q("furn.fenders", "furniture", "Fenders chosen, spaced and checked for hull pressure?", "Energy, reaction, hull pressure, spacing and the panel; works at every tide.", Berths, "Marine", "levels"),
        Q("furn.bollards", "furniture", "Bollards rated and spaced for the vessels?", "Rating and spacing for the largest vessel, and the cope reinforcement around them.", Berths, "Marine"),
        Q("furn.ladders", "furniture", "Ladders, safety chains and grab rails at the spacing required?", "Spacing as the code (e.g. every 30 to 50 m) and down to low water.", Berths, "Marine", "levels"),
        Q("furn.lifesaving", "furniture", "Life-saving equipment and kerbs shown?", "Life buoys, kerbs or wheel stops along the edge.", Berths + "," + Trestle),
        Q("furn.navaids", "furniture", "Navigation lights and marks coordinated?", "Positions and supports agreed with the marine team.", InWater, "Marine"),
        Q("furn.edges", "furniture", "Cope edge protection and nosing detailed?", "Steel nosing or chamfers where equipment and mooring lines work.", Quays),
        Q("furn.services", "furniture", "Ship services (water, power, fuel) pits and ducts coordinated?", "Service pits in the cope, ducts through the deck or wall, agreed with the mechanical and electrical teams.", Berths, "Mechanical, Electrical"),
        Q("furn.maintenance", "furniture", "Fenders and fittings can be inspected and replaced?", "Access for inspection, fender and anode replacement, and the bolts' design life.", Berths),
        Q("furn.mooring", "furniture", "Mooring arrangement checked for the design vessels (lines, angles)?",
            "Bollard positions against the vessels' mooring lines, line angles and the line loads from the mooring study.", Berths, "Marine"),

        // ---- Calculations and reports
        Q("calcs.index", "calcs", "Calculation package complete, with an index?", "Every element on the drawings has its calculation, listed in the index."),
        Q("calcs.checked", "calcs", "Calculations checked and signed by the checker?", "Checker's name, date and comments closed on each calculation."),
        Q("calcs.software", "calcs", "Software models checked (input, units, supports, loads)?", "Input reports printed, and a hand check of the key results."),
        Q("calcs.match", "calcs", "Calculation results match the drawings (sizes, reinforcement, levels)?", "Every size and bar on the drawings is what the calculation needs or more."),
        Q("calcs.report", "calcs", "Design report summarises the design and its results?", "Short report: description, criteria, results and open items."),
        Q("calcs.construction", "calcs", "Construction stages and temporary works checked?", "Stages, temporary loads and the sequence on the drawings."),
        Q("calcs.risks", "calcs", "Design risks and safety in design recorded?", "Risk register or safety in design notes for this submission."),
        Q("calcs.inspection", "calcs", "Inspection and maintenance needs recorded?", "What must be inspected or replaced in the design life (fenders, anodes, coatings, joints), for the owner's manual."),
        Q("calcs.robustness", "calcs", "Robustness checked (loss of a pile, anchor or fender)?",
            "The structure stays up with one pile, anchor or tie rod lost, or one fender damaged, as the design basis."),
        Q("calcs.handcheck", "calcs", "Key results verified by independent hand checks?",
            "Pile loads, moments and reactions from the software checked by hand or a second model."),

        // ---- Drawings
        Q("dwg.list", "drawings", "Drawing list matches the drawings issued?", "Numbers, titles and revisions on the list are those on the sheets."),
        Q("dwg.notes", "drawings", "General notes complete (datum, units, materials, codes)?", "Datum and level conversion, concrete and steel grades, covers, codes."),
        Q("dwg.levels", "drawings", "Levels and dimensions on the drawings match the model?", "Taken from the model, not typed over; no overridden dimensions.", diagram: "levels"),
        Q("dwg.sections", "drawings", "Sections and details cover every typical and special location?", "Joints, ends, corners, openings, fenders, bollards and ladders."),
        Q("dwg.rebar", "drawings", "Reinforcement drawings match the calculations?", "Bar sizes, spacings, laps and covers as the calculation and the code."),
        Q("dwg.clouds", "drawings", "Revision clouds and revision table updated?", "Every change since the last submission is clouded and described."),
        Q("dwg.titleblock", "drawings", "Title blocks complete (status, scale, revision, checker)?", "Status of the submission, scales, revision and who drew, checked and approved."),
        Q("dwg.markups", "drawings", "Drawings checked against the last mark-ups?", "Every mark-up from the last review is picked up."),
        Q("dwg.setout", "drawings", "Setting-out coordinates and dimensions shown?",
            "Setting-out points with coordinates, the grid and chainages, and every dimension needed to build."),
        Q("dwg.bbs", "drawings", "Bar bending schedules match the reinforcement drawings?",
            "Bar marks, shapes, numbers and lengths in the schedules agree with the drawings."),

        // ---- Bills and costing
        Q("bills.method", "bills", "Bills follow the method of measurement in the contract?", "Item descriptions and units as the method of measurement (e.g. CESMM, POMI)."),
        Q("bills.model", "bills", "Quantities taken from the model and reconciled?", "Model quantities against the bills, with the differences explained."),
        Q("bills.backup", "bills", "Back-up calculations for every bill item?", "Each quantity traced to its drawing or model, with the measurement shown."),
        Q("bills.rates", "bills", "Rates and costing updated for this submission?", "Rates source and date; the costing matches the bills."),
        Q("bills.marine", "bills", "Dredging, rock and fill volumes in the bills?", "Volumes as measured, with bulking and overdredge as the contract.", Dredge + "," + Relief + "," + Blocks + "," + Caissons + "," + Yard),
        Q("bills.steel", "bills", "Reinforcement and steel tonnages checked?", "Rebar and steel tonnages against the drawings and a rate per cubic metre check."),
        Q("bills.piles", "bills", "Piles measured with their lengths and toe levels?", "Number, diameter and length per pile, including cut-off and the driven length.", Piles + "," + Sheets),
        Q("bills.provisional", "bills", "Provisional sums, contingency and exclusions stated?",
            "Provisional sums, the contingency and what the bills leave out, written in the preamble."),
        Q("bills.temporary", "bills", "Temporary works and marine plant included?",
            "Cofferdams, staging, guide frames, dewatering and the marine plant the works need."),

        // ---- Specifications
        Q("spec.cover", "specs", "Specification covers every material and work on the drawings?", "Concrete, steel, piling, dredging, rock, fenders, coatings and testing."),
        Q("spec.match", "specs", "Specification, drawings and bills agree?", "Grades, tolerances and testing are the same in all three."),
        Q("spec.marine", "specs", "Marine works, tolerances and testing specified?", "Placing tolerances, pile driving records, dredging survey and acceptance.", InWater + "," + Dredge),
        Q("spec.testing", "specs", "Testing and acceptance criteria specified?", "Pile load and integrity tests, concrete tests, rock tests and fender tests, with the pass criteria."),
        Q("spec.particular", "specs", "Particular specification updated for this project?",
            "Project-specific clauses (materials, tolerances, testing, sequence) updated from the general specification."),
    ];

    // The structure page's key, spelled here so the list above stays short.
    private const string TopicsTypes = SubmissionChecklistMath.TopicTypes;
}
