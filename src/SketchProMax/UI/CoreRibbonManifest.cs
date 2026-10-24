namespace SketchProMax.UI;

/// <summary>
/// The ribbon buttons, pane buttons and pane tabs this core declares, sent to the loader as Query("ribbon"). A loader
/// from 0.30 on adds them while Revit runs, right after an in-session update (see CoreRibbon in the loader), so a new
/// button needs no new loader. Older loaders never ask; the core then says a restart is needed (<see cref="LoaderCompat"/>).
///
/// To add a button that also appears after a live update:
///   1. add an entry below: Button(panel, id, text, command, glyph, tooltip, long description, needsModel, small, live);
///      panels appear in the order they are first named (a new one goes at the end of the tab); id is unique and never
///      reused; glyph is one of the loader's icon names (unknown names get the plain cube), or "logo" / "logo:green";
///   2. handle "command" in CoreEntry.Command ("tool:<name>" runs a tool through its options window, nothing to add).
/// Pulldown(panel, id, text, glyph, tooltip) and Split(...) make drop-downs; Entry(dropDownId, id, text, command, ...)
/// adds their items. small: true stacks consecutive small controls two or three high; breakBefore starts a new stack. live: "key" makes the text,
/// tooltip, icon and state follow CoreEntry.Query("live|key") ("text \t tooltip \t glyph \t state", see LiveLine).
/// Pane(section, id, text, command, tooltip) adds a button to the pane's Activity view; Tab(name, header, accessible
/// name) adds a pane tab whose content CoreEntry.PaneContent(name) returns. Hide(id) hides a loader-built button.
/// A button the loader already builds itself (same id) is left as it is. Moving an item to another panel is fine:
/// the loader hides the old one and makes a new one, live. See RibbonManifest in the loader for the line format.
/// </summary>
internal static class CoreRibbonManifest
{
    // The tab, by workflow, left to right (the loader makes the panels in this order and adds My tools itself):
    // Claude, Model, Documents, Check, Issue, (My tools), Sketch ProMax. Model, Documents, Check and Issue buttons run a
    // tool through its options window ("tool:<name>", see Commands/RibbonTools); an entry whose tool this core doesn't
    // have is left out, and a drop-down left empty goes with it, so tools are matched by name each time Revit asks.
    private static readonly (string Line, string? Tool)[] Items =
    [
        // ---- Claude (titled Assistant, see LoaderBrand): Chat, then the three project windows as large icons (0.35 redesign:
        // the ribbon is short; everything else stacks three high in small drop-downs), then Update, Job and Send view stacked.
        L(Button("Claude", "Chat", "Chat", "chat", "chat",
            "Chat with Sketch ProMax about this model, inside Revit.",
            "Opens the Sketch ProMax pane on its Chat tab. Replies stream in with every Sketch ProMax tool; attach images, PDFs " +
            "and drawings, or send the current view. The first time, it helps you install Claude Code and sign in.")),
        // Model checklist (the pre-submission checklist; 0.34.12: next to Chat, large, so it is always in view; named Model checklist in 0.35): the office's checklist Excel in stages, ending in a signed Word file (UI/ChecklistWindow).
        L(Button("Claude", "PreSubmission", "Model\nchecklist", "pre-submission", "field",
            "The office's pre-submission checklist, stage by stage, ending in the signed Word file.",
            "Goes through the office's pre-submission checklist stage by stage, with hints from the model, and makes the signed Word file to print. " +
            "Stages from your checklist Excel: project details, one per section, then review and sign. Each check has how to check it, " +
            "where it came from and what the model shows; answer Yes, No or N/A with a comment. The Word file carries the signatures " +
            "only when nothing is No or unanswered. Reads the model only; answers are kept per model.", needsModel: true)),
        // Submissions status (0.35, named Berth status until 0.34.60; id and command kept so the live ribbon renames it): next to the checklist. Each berth's status for its next submission (UI/BerthStatusWindow); the
        // model is read only when the user presses Update status there, in short steps.
        L(Button("Claude", "BerthStatus", "Submissions\nstatus", "berth-status", "list",
            "Each berth's percentage done for its next submission, and the expected finish.",
            "A percentage from modelling, coordination, clashes, drawings, details, annotation, schedules and deliverables. Set up once from an Excel file (models, berths, submission schedule, planned counts, deliverables, working week). Click a berth for its areas, " +
            "what to fix, hand entries and comments, and export its Excel. The model is read only when you press Update status, in short steps, so Revit never freezes.")),
        // Costing (0.35): the berth costing window, also opened from Submissions status (UI/BerthCostWindow).
        L(Button("Claude", "BerthCosting", "Costing", "berth-costing", "boq",
            "Bills, back-up and costing per berth, measured from the open model.",
            "Quantities follow the model as it changes; type a value by hand where the model can't say. Makes the BOQ Excel, the Word " +
            "back-up and the costing workbook in the built-in formats or your project's own templates.", needsModel: true)),
        // Update (0.33.1): in the first panel, which Revit never folds away on a narrow ribbon, so updating is always one click.
        // The only Update button since 0.35: its live label also pumps the update notice (CoreEntry.LiveLabel "update-badge").
        L(Button("Claude", "UpdateClaude", "Update", "update-button", "version",
            "The installed version and updates: Update ready (click to install now), Updated to <version> (click to check again), Up to date " +
            "with the time of the last check (click to check now), Couldn't check (why, in this tooltip).", small: true, live: "update-badge")),
        L(Button("Claude", "JobLive", "No job", "job-button", "job",
            "The tool running in Revit and its progress. While one runs, click to ask it to stop.", small: true, live: "job")),
        L(Button("Claude", "SendView", "Send view", "send-view", "view",
            "Attaches a picture of the active view to the chat, as Revit shows it now, ready to ask about.", needsModel: true, small: true)),

        // ---- Model
        L(Pulldown("Model", "Structure", "Structure", "structure", "Columns, beams, slabs, raft, piles and walls.", small: true)),
        T("Structure", "place_columns", "Columns", "column", "Places structural columns at points or grid intersections."),
        T("Structure", "place_beams", "Beams", "beam", "Places beams between points or along grid lines."),
        T("Structure", "create_floors", "Slabs", "slab", "Creates floor slabs from outlines or rooms."),
        T("Structure", "cut_slab", "Slab edge cuts", "slab", "Changes a slab's outline: cuts a region away at its edge, or adds one."),
        T("Structure", "create_raft", "Raft", "raft", "Models a raft foundation under a floor, following its outer edge."),
        T("Structure", "create_piles", "Piles", "pile", "Places piles at points, as a grid over an area, or in rows along an alignment."),
        T("Structure", "create_walls", "Walls", "wall", "Creates straight walls from segments on a level."),
        L(Pulldown("Model", "Steel", "Steel", "steel", "Steel sections, bracing, connections and the steel takeoff.", small: true)),
        T("Steel", "load_steel_sections", "Steel sections", "steel", "Loads steel section families and types into the model."),
        T("Steel", "place_bracing", "Bracing", "steel", "Places steel bracing between columns and beams."),
        T("Steel", "create_steel_connections", "Connections", "steel", "Adds steel connections where members meet."),
        T("Steel", "export_steel_takeoff", "Steel takeoff", "boq", "Steel tonnage per section size: count, length and weight."),
        L(Pulldown("Model", "Reinforcement", "Reinforcement", "rebar", "Rebar in 3D (beams, columns, footings, slabs, walls) and 2D details.", small: true)),
        T("Reinforcement", "reinforce_beam", "Reinforce beams (3D)", "rebar", "Places 3D rebar in beams."),
        T("Reinforcement", "reinforce_column", "Reinforce columns (3D)", "rebar", "Places 3D rebar in columns."),
        T("Reinforcement", "reinforce_footing", "Reinforce footings (3D)", "rebar", "Places 3D rebar in footings."),
        T("Reinforcement", "reinforce_slab_wall", "Reinforce slabs and walls (3D)", "rebar", "Places 3D area or path rebar in slabs and walls."),
        T("Reinforcement", "rebar_section", "Rebar section", "rebar", "Creates a section through reinforced elements, ready for a sheet."),
        T("Reinforcement", "rebar_3d_view", "Rebar 3D view", "rebar", "Creates a 3D view showing the rebar of chosen elements."),
        T("Reinforcement", "export_bar_schedule", "Bar schedule", "boq", "Bar bending schedule and rebar tonnage."),
        // 2D reinforcement (drawn with detail families). Tools are matched by name: any of these missing in a core is left out.
        T("Reinforcement", "rft_detail_families", "2D rebar families", "rebar", "Checks the detail families used for the 2D reinforcement drawings."),
        T("Reinforcement", "place_additional_reinforcement", "Additional bars (2D)", "rebar", "Draws additional top or bottom bars on a slab plan with the office's 2D bar family."),
        T("Reinforcement", "place_mesh_symbols", "Mesh symbols (2D)", "rebar", "Places mesh reinforcement symbols on slabs in a plan view."),
        T("Reinforcement", "place_column_section_details", "Column sections (2D)", "rebar", "Fills column schedule cells with the office's column section reinforcement detail."),
        L(Pulldown("Model", "Marine", "Marine", "marine", "Quays, jetties, port furniture and design vessels.", small: true, breakBefore: true)),
        T("Marine", "marine_structure", "Marine structure", "marine", "Models revetments, quays and other marine structures along an alignment."),
        T("Marine", "port_furniture", "Port furniture", "marine", "Places bollards, fenders, ladders and other port furniture along an edge."),
        T("Marine", "design_vessel", "Design vessel", "marine", "Models a design vessel at a berth or in a channel for layout and clearance checks."),
        T("Marine", "vessel_collision_check", "Vessel collision check", "clash", "Screens piles, piers and dolphins next to a channel or berth for vessel impact."),
        L(Pulldown("Model", "ArchToStructure", "From architect", "arch", "Turn an architect's model into a structural one: link it, convert it, monitor changes.", small: true)),
        T("ArchToStructure", "link_model", "Link a model", "arch", "Links an architect's or another discipline's Revit model."),
        T("ArchToStructure", "arch_to_structure", "Arch to structure", "arch", "Creates structural walls, slabs and columns from the architectural ones."),
        T("ArchToStructure", "copy_monitor", "Copy and monitor", "arch", "Copies grids and levels from a linked model and watches them for changes."),

        // ---- Documents
        L(Pulldown("Documents", "SheetsViews", "Sheets & views", "sheet", "Create views and sheets and put views on sheets.", small: true)),
        T("SheetsViews", "drawing_package", "Drawing package", "sheet", "From one sentence, makes the views, grid dimensions, tags, sheets with title blocks and a drawing register, checked after each stage. Shows the plan with its counts first."),
        T("SheetsViews", "create_plan_views", "Plan views", "sheet", "Creates floor or structural plans for levels."),
        T("SheetsViews", "create_section", "Section", "sheet", "Creates a section view along a line drawn in plan."),
        T("SheetsViews", "create_elevations", "Elevations", "sheet", "Creates elevation views."),
        T("SheetsViews", "create_3d_view", "3D view", "sheet", "Creates a 3D view, optionally boxed around elements or one level."),
        T("SheetsViews", "duplicate_views", "Duplicate views", "sheet", "Duplicates views, with or without detailing."),
        T("SheetsViews", "apply_view_template", "Apply view template", "sheet", "Applies a view template to views."),
        T("SheetsViews", "create_sheets", "Sheets", "sheet", "Creates sheets with a title block, numbers and names."),
        T("SheetsViews", "place_views_on_sheet", "Place views on sheets", "sheet", "Places views on sheets."),
        L(Pulldown("Documents", "TagsDims", "Tags & dimensions", "tag", "Tag elements and dimension grids, walls and openings.", small: true)),
        T("TagsDims", "tag_elements_in_view", "Tag elements", "tag", "Tags every untagged element of a category in a plan view."),
        T("TagsDims", "arrange_tags", "Arrange tags", "tag", "Tidies tags in a view: moves overlapping tags apart, or aligns them."),
        T("TagsDims", "add_keynote_tags", "Keynote tags", "tag", "Adds keynote tags to elements in a view."),
        T("TagsDims", "dimension_grids", "Dimension grids", "tag", "Dimensions the grids in a view."),
        T("TagsDims", "dimension_walls", "Dimension walls", "tag", "Adds dimension strings across parallel walls in a plan view."),
        T("TagsDims", "dimension_openings", "Dimension openings", "tag", "Locates doors and windows along each wall with a dimension string."),
        T("TagsDims", "add_spot_elevations", "Spot elevations", "tag", "Adds spot elevations to slabs, beams or other elements."),
        L(Pulldown("Documents", "TitleBlocks", "Title blocks", "titleblock", "Fill title blocks and manage revisions and clouds.", small: true)),
        T("TitleBlocks", "fill_title_blocks", "Fill title blocks", "titleblock", "Fills title block fields on sheets."),
        T("TitleBlocks", "set_sheet_title_block", "Change title block", "titleblock", "Changes the title block family or type on sheets."),
        T("TitleBlocks", "title_block_from_cad", "Title block from CAD", "titleblock", "Makes a title block family from a DWG or DXF title block."),
        T("TitleBlocks", "create_revision", "New revision", "titleblock", "Creates a revision and optionally adds it to sheets."),
        T("TitleBlocks", "set_sheet_revisions", "Revisions on sheets", "titleblock", "Sets which revisions show on sheets."),
        T("TitleBlocks", "cloud_elements", "Revision clouds", "titleblock", "Draws revision clouds around elements in a view."),

        // ---- Check: small buttons, stacked three and two high (0.35 redesign).
        S("Check", "check_model", "Model check", "check", "Checks the model is built correctly and scores it: warnings, duplicates, gaps, unsupported beams, off-grid columns."),
        S("Check", "find_clashes", "Clashes", "clash", "Finds clashes between two sets of elements, in this model or in linked models."),
        S("Check", "check_drawings", "Drawing check", "drawing-check", "Looks for mistakes across the drawings, such as grids that differ from the links and overridden dimensions."),
        S("Check", "boq_check", "BOQ check", "boq", "Checks a bill of quantities against the model.", breakBefore: true),
        L(Button("Check", "Cleanup", "Purge & cleanup", "cleanup", "refresh",
            "Purge unused families, types and materials, delete views on no sheet, remove imported CAD, or a model health clean. Each shows a preview first and asks before it deletes.",
            "Pick one of the four: Purge unused (repeated until nothing new is unused), Delete unused views (keeps WORKING and _keep views, open views and views others depend on), " +
            "Clean up imports (imported CAD and IMPORT- line styles), Model health clean (unused view templates, unplaced rooms, unused groups and filters, warnings by type). " +
            "Its options open, then the preview: nothing changes until you press Delete them, and Ctrl+Z undoes it in one step.", needsModel: true, small: true)),

        // ---- Issue (titled Submit, see LoaderBrand; 0.33: the submit check gates Submit drawings and Export PDF of sheets, see UI/IssueGateWindow)
        L(Button("Issue", "IssueCheck", "Submit\ncheck", "issue-check", "check",
            "The zero-mistake check before a submission: pass or fail per sheet.",
            "Drawing, spec, BOQ and model checks together. " +
            "It runs check_drawings, spec_check, boq_check and check_model on the issue set and lists each finding under its sheet " +
            "(model-wide ones under Model). Fix an item (it takes you there) or accept it with a reason; Submit stays off until " +
            "nothing is open. Reads the model only.", needsModel: true)),
        S("Issue", "issue_drawings", "Submit drawings", "pdf", "Submits sheets end to end after the submit check: revision, PDF/DWG, list of drawings and transmittal."),
        S("Issue", "redline_apply", "Redlines", "titleblock", "Applies a marked-up PDF or photo: each markup matched to its sheet and element, then clouded."),
        L(Pulldown("Issue", "Export", "Export", "download", "PDF, list of drawings, quantities and the project pack.", small: true)),
        T("Export", "export_pdf", "Export PDF", "pdf", "Prints sheets or views to PDF, as one combined file by default."),
        T("Export", "export_drawing_list", "List of drawings", "list", "Writes the list of drawings (numbers, names, current revisions) to a CSV that opens in Excel."),
        T("Export", "export_quantities", "Quantities / BOQ", "boq", "Writes quantities per category and type (count, length, area, volume) to a CSV that opens in Excel."),
        L(Entry("Export", "ExportPack", "Project pack", "export-pack", "pack",
            "Writes one small zip describing this project (data, quantities, sheets, revisions, schedules, the latest check " +
            "results) to upload to an AI chat to prepare a report.",
            "Read-only: nothing in the model changes. Runs here without the AI; usually a few MB at most. The folder opens " +
            "with the zip selected when it is done.", needsModel: true)),

        // ---- Sketch ProMax: Under development (tools that work but are still being proven), then stacks of three short
        // labels. A stack's widest label is the first Revit hides when the ribbon is narrow, so the labels are short and of
        // a length; RibbonLabels then asks Revit to keep them (it shrinks the panel by other means).
        L(Pulldown("Sketch ProMax", "UnderDevelopment", "Under\ndevelopment", "test",
            "Tools still being proven: embodied carbon and value engineering. Check their results before you use them.")),
        T("UnderDevelopment", "embodied_carbon", "Embodied carbon", "boq", "Total tCO2e and kgCO2e/m² with breakdowns, top contributors and assumptions, to Excel and CSV. Reads the model only."),
        T("UnderDevelopment", "carbon_compare", "Compare options", "boq", "Compares two options or snapshots (e.g. cement replacement or a grade swap) and shows the saving."),
        T("UnderDevelopment", "carbon_view", "Carbon 3D view", "boq", "Makes a new 3D view coloured green to red by carbon intensity. Existing views are not changed."),
        T("UnderDevelopment", "ve_opportunities", "VE opportunities", "check", "Ranks where cost or material could be saved (depths, sizes, grades, rebar, steel, formwork, spec), to Excel. Reads the model only."),
        T("UnderDevelopment", "ve_compare_options", "Compare systems", "structure", "Flat slab, beam and slab, PT, hollow-core, steel; raft or piles: quantities, cost, carbon, programme and risks side by side."),
        T("UnderDevelopment", "ve_view", "VE 3D view", "view", "Makes a new 3D view coloured by the ranked VE items. Existing views are not changed."),
        T("UnderDevelopment", "ve_apply", "Apply to a copy", "bolt", "Applies chosen VE changes to a saved copy or a design option, with before and after cost and carbon."),
        T("UnderDevelopment", "cost_estimate", "Cost plan", "boq", "Cost plan from the firm's rate table: total, rates per m² GFA, per m, per m³ and per tonne, add-ons, options side by side, to Excel. Reads the model only."),
        T("UnderDevelopment", "what_if", "What if", "bolt", "Tries a slab thickness, column size, grade or grid spacing on a copy and compares quantities, cost, carbon and the drawings affected. Previews first."),
        T("UnderDevelopment", "ve_register", "VE register", "list", "Exports the VE register and sets each item's status (proposed, accepted, rejected) and owner."),
        // Share tools (0.35): makes a zip that installs the chosen tools on the TritoRevit tab for colleagues with or without
        // Sketch ProMax (Sharing/ShareWindow).
        L(Button("Sketch ProMax", "ShareTools", "Share tools", "share-tools", "pack",
            "Share your tools with colleagues: one zip installs them on the TritoRevit tab, and they update by themselves.",
            "Pick tools (the checklist, ribbon tools, learned tools, recipes), name the pack and, optionally, a folder everyone can reach. " +
            "Colleagues extract the zip and double-click Install TritoRevit.cmd; it installs the TritoRevit add-in when they don't have it. Only the first " +
            "install needs a Revit restart: later changes reach them from that folder while Revit is open.", small: true, breakBefore: true)),
        L(Button("Sketch ProMax", "SettingsButton", "Settings", "settings", "settings",
            "Firm standards, whether Revit asks before Sketch ProMax changes the model, and automatic updates.", small: true)),
        L(Pulldown("Sketch ProMax", "HelpMenu", "Help", "help", "How to use Sketch ProMax, and diagnostics that test its tools.", small: true)),
        L(Entry("HelpMenu", "HelpPage", "Help and getting started", "help", "help", "Opens the guide: how to use Sketch ProMax, what it can do and what to try first.")),
        L(Entry("HelpMenu", "SelfTest", "Diagnostics: self-test", "self-test", "test",
            "Runs every Sketch ProMax tool once on the open model and reports what works. The quick check is read-only.")),
        L(Entry("HelpMenu", "FieldTest", "Diagnostics: field test", "field-test", "field",
            "Builds a throwaway test project, runs every tool family on it and writes one report. Your open models are not changed.",
            needsModel: true)),
        L(Entry("HelpMenu", "ConnectionStatus", "Desktop app connection", "status", "logo",
            "Whether the Claude desktop app is connected to this Revit window, the models it can see, and how to fix a missing connection.")),
        L(Entry("HelpMenu", "TogglePane", "Show or hide pane", "pane", "pane",
            "Shows or hides the Sketch ProMax pane: chat, the running job with a Cancel button, the last result and your tools.")),
        L(Entry("HelpMenu", "OpenClaude", "Open Claude app", "open-claude", "open",
            "Starts the Claude desktop app, or brings it to the front. Works with the normal installer and the Microsoft Store version.")),
        L(Button("Sketch ProMax", "ReportBug", "Report bug", "report-bug", "bug",
            "Writes a bug report for Sketch ProMax with what just happened, ready to send.", small: true, breakBefore: true)),
        // Bug folder left the ribbon in 0.35 (still on the pane's Bugs section) to free a loader slot for Share tools.
        // License (0.35) took Export chats' place, which stays on the pane's Bugs section, to keep the loader's free slots:
        // this PC's license, or on the owner's PC the License manager (UI/LicenseWindow, UI/LicenseManagerWindow).
        L(Button("Sketch ProMax", "License", "License", "license", "settings",
            "This PC's license, and its PC code to send for a key. On the owner's PC: the License manager, to make keys, change their dates and cancel them.", small: true)),
        // Time saved (0.32): CoreEntry.Command "time-saved" opens the summary for the active model (UI/TimeSavedWindow).
        L(Button("Sketch ProMax", "TimeSaved", "Time saved", "time-saved", "job",
            "Working time in this model (AI and by hand) and the time Sketch ProMax saved, per tool and per day. Exports to Excel.",
            "All users' totals are kept in the model (Project Information shows them too). Rates and the idle threshold are in Settings.",
            needsModel: true, small: true)),
        L(Entry("HelpMenu", "TestChecklist", "Test checklist", "test-checklist", "check",
            "Walks you through what the field test can't check by itself (chat, ribbon, carbon, VE, 2D rebar, Report bug, " +
            "exports and the fixed bugs), on a _detached copy. Mark each pass or fail; the result is saved in the Bugs folder.")),

        // ---- The pane
        L(Pane("Claude app", "CopyPrompt", "Copy prompt", "copy-prompt",
            "Copies a good first message to the clipboard, to paste into a new chat in the Claude desktop app.")),
        L(Pane("Bugs", "ReportBug", "Report bug", "report-bug", "Writes a bug report for Sketch ProMax with what just happened, ready to send.")),
        L(Pane("Bugs", "BugFolder", "Open bug folder", "bug-folder", "Opens the folder with the bug reports written so far.")),
        L(Pane("Bugs", "ExportChatsBugs", "Export chats & bugs", "export-chats-bugs",
            "Writes one zip with the chats of every model, all bug reports, the tool-call log and the versions, to send to the developer. Asks for the period first; nothing is sent.")),
        L(Pane("Bugs", "TestChecklist", "Test checklist", "test-checklist",
            "Walks you through the checks only you can do, on a _detached copy, and saves pass or fail for each in the Bugs folder.")),
        // Split PDF (0.34.29): a pane button (the ribbon's command slots are kept free). The chat splits a large PDF by itself.
        L(Pane("Files", "SplitPdf", "Split PDF", "split-pdf",
            "Cuts a large PDF into parts of at most 18 MB (or the size you set), in page order, so each part can be attached in the chat and read whole. The original is not changed.")),
        L(Tab("notes", "Notes", "Notes for this model")),
    ];


    /// <summary>
    /// Panel titles that differ from the loader's fixed panel names (the loader and the manifest find panels by name; the
    /// core retitles them while Revit runs, see LoaderBrand): Claude is shown as Assistant, Issue as Submit (0.35).
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> PanelTitles = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Claude"] = BrandText.PanelTitle,
        ["Issue"] = "Submit",
    };

    /// <summary>Every line, whether or not this core has the tools (for the tests).</summary>
    public static string Text => Build(_ => "");

    /// <summary>
    /// The manifest the loader is sent. <paramref name="describe"/> gives a tool's summary (its long tooltip), or null
    /// when this core has no tool of that name: its entry is left out. <paramref name="favourites"/>: what the My tools
    /// panel's favourite slots show at first (see <see cref="Favourites"/>; their live labels follow later changes).
    /// </summary>
    public static string Build(Func<string, string?> describe, IReadOnlyList<Favourites.Item>? favourites = null)
    {
        var lines = new List<string>();
        foreach (var (line, tool) in Items)
        {
            if (tool is null) { lines.Add(line); continue; }
            var more = describe(tool);
            if (more is null) continue;
            // The long description is the tool's own summary (field 7).
            var f = line.Split('\t');
            if (f.Length > 7 && f[7].Length == 0 && more.Length > 0) f[7] = Escape(more);
            lines.Add(string.Join("\t", f));
        }
        // A drop-down none of whose entries are left goes too.
        var used = new HashSet<string>(lines.Select(l => l.Split('\t')).Where(f => f[0] == "item" && f.Length > 1).Select(f => f[1]), StringComparer.Ordinal);
        lines.RemoveAll(l => l.Split('\t') is var f && f[0] is "pulldown" or "split" && f.Length > 2 && !used.Contains(f[2]));
        // My tools (between Issue and Sketch ProMax, as on the tab): the Favourites button and the favourite slots, next
        // to the loader's own My tools drop-down.
        var at = lines.FindIndex(l => l.Split('\t') is var f && f.Length > 1 && f[1] == "Sketch ProMax");
        lines.InsertRange(at < 0 ? lines.Count : at, Favourites.ManifestLines(favourites ?? []));
        return string.Join("\n", lines);
    }

    /// <summary>
    /// The tools the ribbon runs through their options window (tool name, one-line label, glyph, tooltip), in ribbon
    /// order, whether or not this core has them: what can be pinned besides recipes and learned tools.
    /// </summary>
    public static IEnumerable<(string Tool, string Text, string Glyph, string Tip)> Tools()
    {
        foreach (var (line, tool) in Items)
        {
            if (tool is null) continue;
            var f = line.Split('\t');
            if (f.Length < 7) continue;
            yield return (tool, Unescape(f[3]).Replace('\n', ' '), Unescape(f[5]), Unescape(f[6]));
        }
    }

    private static string Unescape(string value) => value.Replace("\\n", "\n").Replace("\\\\", "\\");

    private static (string, string?) L(string line) => (line, null);

    /// <summary>A drop-down entry that runs <paramref name="tool"/> through its options window.</summary>
    private static (string, string?) T(string dropDown, string tool, string text, string glyph, string tip) =>
        (Entry(dropDown, "Tool." + tool, text, "tool:" + tool, glyph, tip, needsModel: true), tool);

    /// <summary>A large button that runs <paramref name="tool"/> through its options window.</summary>
    private static (string, string?) B(string panel, string tool, string text, string glyph, string tip) =>
        (Button(panel, "Tool." + tool, text, "tool:" + tool, glyph, tip, needsModel: true), tool);

    /// <summary>A small (stacked) button that runs <paramref name="tool"/> through its options window.</summary>
    private static (string, string?) S(string panel, string tool, string text, string glyph, string tip, bool breakBefore = false) =>
        (Button(panel, "Tool." + tool, text, "tool:" + tool, glyph, tip, needsModel: true, small: true, breakBefore: breakBefore), tool);

    internal static string Button(string panel, string id, string text, string command, string glyph, string tip, string more = "",
        bool needsModel = false, bool small = false, string? live = null, bool breakBefore = false) =>
        Join("button", panel, id, text, command, glyph, tip, more, Flags(needsModel, small, live, breakBefore));

    internal static string Pulldown(string panel, string id, string text, string glyph, string tip, string more = "", bool small = false,
        string? live = null, bool breakBefore = false) =>
        Join("pulldown", panel, id, text, "", glyph, tip, more, Flags(false, small, live, breakBefore));

    internal static string Split(string panel, string id, string text, string glyph, string tip, string more = "", bool small = false) =>
        Join("split", panel, id, text, "", glyph, tip, more, Flags(false, small, null));

    internal static string Entry(string dropDown, string id, string text, string command, string glyph, string tip, string more = "", bool needsModel = false) =>
        Join("item", dropDown, id, text, command, glyph, tip, more, Flags(needsModel, false, null));

    internal static string Hide(string id) => Join("hide", "", id);

    private static string Flags(bool needsModel, bool small, string? live, bool breakBefore = false) =>
        string.Join(",", new[] { needsModel ? "model" : "", small ? "small" : "", breakBefore ? "break" : "", live is null ? "" : "live:" + live }.Where(f => f.Length > 0));

    /// <summary>A live label's answer: text, tooltip, glyph and "disabled" when it can't be clicked now.</summary>
    internal static string LiveLine(string text, string tip, string glyph, bool enabled = true) =>
        string.Join("\t", Escape(text), Escape(tip), Escape(glyph), enabled ? "" : "disabled");

    internal static string Pane(string section, string id, string text, string command, string tip) =>
        Join("pane", section, id, text, command, "", tip, "", "");

    internal static string Tab(string name, string header, string accessibleName) =>
        Join("tab", "", name, header, "", "", accessibleName, "", "");

    internal static string Join(params string[] fields) => string.Join("\t", fields.Select(Escape));

    /// <summary>Line breaks become "\n" and backslashes "\\" (the loader turns them back); tabs become spaces.</summary>
    internal static string Escape(string? value) =>
        (value ?? "").Replace("\\", "\\\\").Replace("\r", "").Replace("\n", "\\n").Replace("\t", " ");
}
