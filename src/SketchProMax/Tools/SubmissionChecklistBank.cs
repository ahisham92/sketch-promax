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
        Revet = "revetment", Dolphins = "dolphins", Trestle = "trestle", RoRo = "roro", Pontoon = "pontoon", DWall = "diaphragm-wall",
        Dredge = "dredging", Rails = "crane-rails", Yard = "pavement";

    private const string Quays = Piled + "," + Blocks + "," + Caissons + "," + Sheets + "," + DWall;
    private const string Piles = Piled + "," + Dolphins + "," + Trestle + "," + Pontoon + "," + RoRo;
    private const string GravityLike = Blocks + "," + Caissons + "," + Walls;
    private const string Embedded = Sheets + "," + DWall;
    private const string InWater = Piled + "," + Blocks + "," + Caissons + "," + Sheets + "," + Revet + "," + Dolphins + "," + Trestle + "," + RoRo + "," + Pontoon + "," + DWall;
    private const string Berths = Piled + "," + Blocks + "," + Caissons + "," + Sheets + "," + Dolphins + "," + DWall + "," + Pontoon + "," + RoRo;

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
        Q("loads.thermal", "loads", "Temperature, shrinkage and creep allowed for?",
            "In long decks and walls: joint spacing and restraint forces."),
        Q("loads.combinations", "loads", "Load combinations and factors as the code (ULS, SLS, accidental)?",
            "Every combination the code asks for is run, and the governing one is shown."),

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
            "Slip circles through and under the structure, for each load case and water level.", GravityLike + "," + Embedded + "," + Revet + "," + Piled + "," + Dredge, "Geotechnical"),
        Q("geo.liquefaction", "geotech", "Liquefaction of fill and soils checked?",
            "Where the seismic design applies: liquefaction potential and the measures taken.", Quays + "," + Dredge + "," + Yard, "Geotechnical"),
        Q("geo.improvement", "geotech", "Ground improvement or soil replacement designed where needed?",
            "Dredging of soft soils, replacement, vibro or drains, with their limits shown.", GravityLike + "," + Dredge + "," + Yard + "," + Revet, "Geotechnical"),
        Q("geo.drivability", "geotech", "Pile or sheet drivability checked?",
            "Hammer, refusal criteria and stresses while driving; pre-boring where needed.", Piles + "," + Sheets, "Geotechnical"),

        // ---- Structure checks: deck on piles
        Q("piled.layout", TopicsTypes, "Pile layout, sizes and rakes as the calculation?", "Bents, spacing, rakes and pile sizes on the drawings match the analysis model.", Piled, diagram: Piled),
        Q("piled.model", TopicsTypes, "Analysis model checked (supports, fixity depth, springs)?", "Point of fixity or soil springs from geotechnical; free length for buckling and P-delta.", Piled, "Geotechnical", Piled),
        Q("piled.deck", TopicsTypes, "Deck slab and beams designed for every load case?", "Bending, shear and punching under wheel and outrigger loads; crack width as the exposure.", Piled, diagram: Piled),
        Q("piled.connection", TopicsTypes, "Pile to deck connection detailed?", "Embedment, dowels or plug and its reinforcement, for compression, tension and moment.", Piled, diagram: Piled),
        Q("piled.uplift", TopicsTypes, "Deck soffit above wave crest, or designed for uplift?", "Air gap between the extreme wave crest and the soffit; otherwise uplift and slamming loads.", Piled, "Marine", "levels"),
        Q("piled.slope", TopicsTypes, "Slope under the deck stable and protected?", "Armour and filter under the deck; slope stability with the piles.", Piled + "," + Revet, "Geotechnical, Marine", Piled),
        Q("piled.joints", TopicsTypes, "Expansion joints spaced and detailed?", "Joint spacing from temperature and shrinkage; the joint detail carries the shear across.", Piled + "," + Trestle, diagram: Piled),
        Q("piled.precast", TopicsTypes, "Precast elements and their lifting checked?", "Precast beams or slabs: lifting, temporary stages and stitch details.", Piled + "," + Trestle + "," + Dolphins),

        // ---- Structure checks: gravity blocks
        Q("blocks.stability", TopicsTypes, "Sliding, overturning and bearing checked at every block layer?", "Each joint between block courses, not only the base, for every load case and water level.", Blocks, diagram: Blocks),
        Q("blocks.friction", TopicsTypes, "Friction between blocks and on the bed as agreed?", "Concrete on concrete and concrete on rubble friction values from the design basis.", Blocks, diagram: Blocks),
        Q("blocks.keys", TopicsTypes, "Shear keys or interlocks between blocks detailed?", "Keys, their tolerances and how the blocks seat.", Blocks, diagram: Blocks),
        Q("blocks.bed", TopicsTypes, "Rubble bed thickness, levelling and tolerance shown?", "Bedding layer, screeding tolerance and the toe of the bed.", Blocks + "," + Caissons, "Marine", Blocks),
        Q("blocks.backfill", TopicsTypes, "Backfill and filter behind the wall stop loss of fines?", "Graded filter or geotextile behind the joints between blocks.", Blocks + "," + Caissons, diagram: Blocks),
        Q("blocks.weight", TopicsTypes, "Heaviest block within the placing crane's capacity?", "Block weights listed with the crane and radius they need.", Blocks, diagram: Blocks),
        Q("blocks.cope", TopicsTypes, "Cope beam designed for fender, bollard and crane loads?", "Cope beam reinforcement and its joints with the blocks.", Blocks + "," + Caissons + "," + Sheets + "," + DWall, diagram: Blocks),
        Q("blocks.toe", TopicsTypes, "Toe protection against scour shown?", "Rock or mattress at the toe, its extent and grading.", Blocks + "," + Caissons + "," + Walls, "Marine", Blocks),

        // ---- Structure checks: caissons
        Q("caissons.float", TopicsTypes, "Floating stability during tow and sinking checked?", "Metacentric height in each ballast stage; draft against the water depth on the route.", Caissons, "Marine", Caissons),
        Q("caissons.stability", TopicsTypes, "Sliding, overturning and bearing checked in place?", "With the cell fill, the backfill and the residual water.", Caissons, diagram: Caissons),
        Q("caissons.walls", TopicsTypes, "Cell walls and base slab designed for every stage?", "Casting, launching, towing, sinking, filling and in service.", Caissons, diagram: Caissons),
        Q("caissons.joints", TopicsTypes, "Joints between caissons sealed against loss of fill?", "Joint detail, its filter and how it is closed after placing.", Caissons, diagram: Caissons),
        Q("caissons.fill", TopicsTypes, "Cell fill material, density and placing specified?", "Fill weight used in the stability calculation is the one specified.", Caissons, diagram: Caissons),

        // ---- Structure checks: sheet pile quay
        Q("sheets.embedment", TopicsTypes, "Embedment depth with its factor of safety?", "Free or fixed earth support, with the factor or partial factors in the design basis.", Sheets, "Geotechnical", Sheets),
        Q("sheets.section", TopicsTypes, "Section and steel grade enough after corrosion?", "Bending and shear on the section left at the end of the design life.", Sheets, diagram: Sheets),
        Q("sheets.corrosion", TopicsTypes, "Corrosion allowance or protection per zone?", "Loss rates for the splash, tidal, low water and buried zones; coating or cathodic protection.", Sheets + "," + Piled + "," + Dolphins + "," + Trestle, diagram: "levels"),
        Q("sheets.anchor", TopicsTypes, "Tie rods and anchor wall designed and placed outside the active wedge?", "Tie rod force, size, spacing and the anchor wall far enough back.", Sheets + "," + DWall, "Geotechnical", Sheets),
        Q("sheets.waling", TopicsTypes, "Waling and tie rod connections detailed?", "Waling size, bolts, and the connection at the anchor wall.", Sheets, diagram: Sheets),
        Q("sheets.tielevel", TopicsTypes, "Tie rod level workable above the tide?", "Tie rods installed in the dry, or the method for installing them below water.", Sheets, "Marine", "levels"),
        Q("sheets.combi", TopicsTypes, "Combi-wall king piles and infill sheets checked?", "Where a combi-wall is used: king pile capacity and the infill sheets' arching.", Sheets, diagram: Sheets),

        // ---- Structure checks: retaining walls
        Q("walls.stability", TopicsTypes, "Sliding, overturning and bearing checked?", "With surcharge and water behind, and the passive resistance taken only where it is safe.", Walls, "Geotechnical", Walls),
        Q("walls.stem", TopicsTypes, "Stem, heel and toe designed for bending, shear and cracks?", "Crack width as the exposure; the starter bars at the base.", Walls, diagram: Walls),
        Q("walls.drainage", TopicsTypes, "Drainage behind the wall (weep holes, drain, filter)?", "So water can't build up behind the wall beyond what the design takes.", Walls + "," + Yard, "Civil", Walls),
        Q("walls.joints", TopicsTypes, "Movement joints and waterstops spaced and detailed?", "Joint spacing and the waterstop where water must not pass.", Walls, diagram: Walls),

        // ---- Structure checks: revetments and breakwaters
        Q("revet.armour", TopicsTypes, "Armour unit size from the design wave (Hudson or Van der Meer)?", "Armour weight and grading, with the damage level allowed.", Revet, "Marine", Revet),
        Q("revet.filter", TopicsTypes, "Under-layers and filter rules met?", "Each layer's grading against the one under it, or the geotextile chosen.", Revet, diagram: Revet),
        Q("revet.toe", TopicsTypes, "Toe designed against scour and sliding?", "Toe berm or key, its level and width.", Revet, "Marine", Revet),
        Q("revet.crest", TopicsTypes, "Crest level for the overtopping allowed?", "Overtopping rate at the design storm against what the area behind can take.", Revet, "Marine", "levels"),
        Q("revet.slope", TopicsTypes, "Slope angle and stability agreed?", "Slope stability with the soils, and the armour stable at that angle.", Revet, "Geotechnical", Revet),
        Q("revet.overtopping", TopicsTypes, "Wave overtopping at the crest within the limits?", "Overtopping rate for the design storm against the limit for the use behind, and the crest drainage.", Revet + "," + Walls, "Marine", Revet),

        // ---- Structure checks: dolphins
        Q("dolphins.layout", TopicsTypes, "Dolphin layout fits the design vessels?", "Breasting dolphins within the parallel body of the smallest and largest vessel; mooring lines at good angles.", Dolphins, "Marine", Dolphins),
        Q("dolphins.deflection", TopicsTypes, "Pile group deflection within the fender's travel?", "Flexible dolphins: energy shared between piles and fender; deflection limits.", Dolphins, diagram: Dolphins),
        Q("dolphins.hooks", TopicsTypes, "Quick release hooks or bollards rated and fixed?", "Hook rating, number of hooks, and the cap reinforcement around the base.", Dolphins, "Marine", Dolphins),
        Q("dolphins.access", TopicsTypes, "Walkways and access between dolphins designed?", "Walkway spans, handrails and bearings.", Dolphins, diagram: Dolphins),

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
        Q("dwall.stages", TopicsTypes, "Every construction stage checked?", "Each excavation, anchor and dredging stage, not only the final one.", DWall + "," + Sheets, diagram: DWall),

        // ---- Structure checks: dredging and reclamation
        Q("dredge.slopes", TopicsTypes, "Dredged side slopes stable and agreed?", "Side slopes per soil layer, with the quay's toe kept clear.", Dredge, "Geotechnical, Marine", Dredge),
        Q("dredge.volumes", TopicsTypes, "Dredging volumes with overdredge and tolerance?", "Volumes from the survey to the design surface, split by soil type, with overdredge.", Dredge, "Marine", Dredge),
        Q("dredge.disposal", TopicsTypes, "Disposal or reuse of dredged material agreed?", "Disposal site or reuse as fill, with the approvals needed.", Dredge, "Marine"),
        Q("dredge.reclamation", TopicsTypes, "Reclamation level, fill and settlement allowed for?", "Fill material, its compaction, and the settlement before the pavement goes on.", Dredge + "," + Yard, "Geotechnical, Grading", Dredge),

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
        Q("dur.rock", "durability", "Rock and fill quality and grading specified?", "Density, strength and grading of armour, core and filter rock.", Revet + "," + Blocks + "," + Caissons + "," + Dredge),

        // ---- Marine furniture and safety
        Q("furn.fenders", "furniture", "Fenders chosen, spaced and checked for hull pressure?", "Energy, reaction, hull pressure, spacing and the panel; works at every tide.", Berths, "Marine", "levels"),
        Q("furn.bollards", "furniture", "Bollards rated and spaced for the vessels?", "Rating and spacing for the largest vessel, and the cope reinforcement around them.", Berths, "Marine"),
        Q("furn.ladders", "furniture", "Ladders, safety chains and grab rails at the spacing required?", "Spacing as the code (e.g. every 30 to 50 m) and down to low water.", Berths, "Marine", "levels"),
        Q("furn.lifesaving", "furniture", "Life-saving equipment and kerbs shown?", "Life buoys, kerbs or wheel stops along the edge.", Berths + "," + Trestle),
        Q("furn.navaids", "furniture", "Navigation lights and marks coordinated?", "Positions and supports agreed with the marine team.", InWater, "Marine"),
        Q("furn.edges", "furniture", "Cope edge protection and nosing detailed?", "Steel nosing or chamfers where equipment and mooring lines work.", Quays),
        Q("furn.services", "furniture", "Ship services (water, power, fuel) pits and ducts coordinated?", "Service pits in the cope, ducts through the deck or wall, agreed with the mechanical and electrical teams.", Berths, "Mechanical, Electrical"),
        Q("furn.maintenance", "furniture", "Fenders and fittings can be inspected and replaced?", "Access for inspection, fender and anode replacement, and the bolts' design life.", Berths),

        // ---- Calculations and reports
        Q("calcs.index", "calcs", "Calculation package complete, with an index?", "Every element on the drawings has its calculation, listed in the index."),
        Q("calcs.checked", "calcs", "Calculations checked and signed by the checker?", "Checker's name, date and comments closed on each calculation."),
        Q("calcs.software", "calcs", "Software models checked (input, units, supports, loads)?", "Input reports printed, and a hand check of the key results."),
        Q("calcs.match", "calcs", "Calculation results match the drawings (sizes, reinforcement, levels)?", "Every size and bar on the drawings is what the calculation needs or more."),
        Q("calcs.report", "calcs", "Design report summarises the design and its results?", "Short report: description, criteria, results and open items."),
        Q("calcs.construction", "calcs", "Construction stages and temporary works checked?", "Stages, temporary loads and the sequence on the drawings."),
        Q("calcs.risks", "calcs", "Design risks and safety in design recorded?", "Risk register or safety in design notes for this submission."),
        Q("calcs.inspection", "calcs", "Inspection and maintenance needs recorded?", "What must be inspected or replaced in the design life (fenders, anodes, coatings, joints), for the owner's manual."),

        // ---- Drawings
        Q("dwg.list", "drawings", "Drawing list matches the drawings issued?", "Numbers, titles and revisions on the list are those on the sheets."),
        Q("dwg.notes", "drawings", "General notes complete (datum, units, materials, codes)?", "Datum and level conversion, concrete and steel grades, covers, codes."),
        Q("dwg.levels", "drawings", "Levels and dimensions on the drawings match the model?", "Taken from the model, not typed over; no overridden dimensions.", diagram: "levels"),
        Q("dwg.sections", "drawings", "Sections and details cover every typical and special location?", "Joints, ends, corners, openings, fenders, bollards and ladders."),
        Q("dwg.rebar", "drawings", "Reinforcement drawings match the calculations?", "Bar sizes, spacings, laps and covers as the calculation and the code."),
        Q("dwg.clouds", "drawings", "Revision clouds and revision table updated?", "Every change since the last submission is clouded and described."),
        Q("dwg.titleblock", "drawings", "Title blocks complete (status, scale, revision, checker)?", "Status of the submission, scales, revision and who drew, checked and approved."),
        Q("dwg.markups", "drawings", "Drawings checked against the last mark-ups?", "Every mark-up from the last review is picked up."),

        // ---- Bills and costing
        Q("bills.method", "bills", "Bills follow the method of measurement in the contract?", "Item descriptions and units as the method of measurement (e.g. CESMM, POMI)."),
        Q("bills.model", "bills", "Quantities taken from the model and reconciled?", "Model quantities against the bills, with the differences explained."),
        Q("bills.backup", "bills", "Back-up calculations for every bill item?", "Each quantity traced to its drawing or model, with the measurement shown."),
        Q("bills.rates", "bills", "Rates and costing updated for this submission?", "Rates source and date; the costing matches the bills."),
        Q("bills.marine", "bills", "Dredging, rock and fill volumes in the bills?", "Volumes as measured, with bulking and overdredge as the contract.", Dredge + "," + Revet + "," + Blocks + "," + Caissons + "," + Yard),
        Q("bills.steel", "bills", "Reinforcement and steel tonnages checked?", "Rebar and steel tonnages against the drawings and a rate per cubic metre check."),
        Q("bills.piles", "bills", "Piles measured with their lengths and toe levels?", "Number, diameter and length per pile, including cut-off and the driven length.", Piles + "," + Sheets),

        // ---- Specifications
        Q("spec.cover", "specs", "Specification covers every material and work on the drawings?", "Concrete, steel, piling, dredging, rock, fenders, coatings and testing."),
        Q("spec.match", "specs", "Specification, drawings and bills agree?", "Grades, tolerances and testing are the same in all three."),
        Q("spec.marine", "specs", "Marine works, tolerances and testing specified?", "Placing tolerances, pile driving records, dredging survey and acceptance.", InWater + "," + Dredge),
        Q("spec.testing", "specs", "Testing and acceptance criteria specified?", "Pile load and integrity tests, concrete tests, rock tests and fender tests, with the pass criteria."),
    ];

    // The structure page's key, spelled here so the list above stays short.
    private const string TopicsTypes = SubmissionChecklistMath.TopicTypes;
}
