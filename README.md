# Sketch ProMax

<p align="center"><img src="assets/logo/sketch-promax-wordmark.png" alt="Sketch ProMax" width="420"></p>

An AI agent for Autodesk Revit 2024, 2025, 2026 and 2027, powered by Claude. Ask in plain language; it reads, documents, audits and edits the open model through 204 tools, and it learns new ones as you use it. It asks for your approval before every change, and every change can be undone with Ctrl+Z.

See [docs/PLAN.md](docs/PLAN.md) for the comparison with SketchPro, the roadmap and the architecture.

## How you use it

You chat in the **Claude desktop app**, next to Revit, on your normal Claude plan: no API key and no extra payment. The work happens in Revit. There is no chat inside Revit, because that would need a paid API key; your Claude plan can only be used through Anthropic's own apps.

The **Sketch ProMax** ribbon tab, by workflow (it sits right after SketchPro's tab):
- **Claude**: **Chat** (Claude inside Revit), **Send view** (attach the active view to the chat), **Copy prompt**, the connection as a coloured dot (green: talked to it recently; amber: ready; gray: not running), and **Open Claude** / **Show or hide pane** under **More**.
- **Model**: **Structure** (columns, beams, slabs, slab edge cuts, raft, piles, walls), **Steel** (sections, bracing, connections, takeoff), **Marine & infrastructure** and **Arch to structure**.
- **Documents**: **Sheets & views**, **Tags & dimensions**, **Reinforcement** (3D rebar and 2D detail drawings) and **Title blocks & revisions**.
- **Check**: **Model check**, **Clashes**, **Drawing check**, **BOQ check**, **Embodied carbon** (assessment, compare options, carbon 3D view), **Value engineering** (opportunities, compare systems, VE 3D view, apply to a copy, VE register). **Issue**: **Export PDF**, **List of drawings**, **Quantities / BOQ**, **Export for Claude** (one small zip describing the project, to upload to a Claude chat for a report; see below).
- **My tools**: your saved recipes and learned tools, run right in Revit with no AI and no internet. It asks for any blanks ({level}...) and shows the result. Pin your most used tools, recipes and learned tools next to it as favourites (up to 12, in your order): click **Favourites**, or tick **Pin to My tools** in a tool's options window.
- **Sketch ProMax**: the running **Job** (click to stop it), **Update** (Up to date, Check, What's new), **Report bug**, **Bug folder**, **Export chats & bugs**, **Settings** and **Help** with the self-test and field test.

Every Model, Documents, Check and Issue button runs its tool without the chat: a small options window built from the tool's inputs (required ones marked, element ids can take the current selection) remembers what you typed last time (`%APPDATA%\SketchProMax\tool-defaults.json`), then the tool runs as a job with a progress window you can stop, and its result shows in a window and in the pane.

Several Revit windows, and different Revit versions, can be open at once. Name the model in chat ("in the Tower model, ...") and Claude switches to it.

If your company manages your Claude account, IT may need to allow the desktop app's local tools (MCP servers).

## Install

Requirements: Windows, Revit 2024 to 2027, and the .NET 8 and .NET 10 SDKs to build.

```powershell
.\build.ps1      # builds the add-in for 2024 to 2027 and the desktop-app connector into .\dist
.\install.ps1    # installs for your user, no admin needed
```

`install.ps1` copies the add-in into each installed Revit version, installs the connector to `%LOCALAPPDATA%\SketchProMax\mcp`, and registers it with the Claude desktop app, including the Microsoft Store version (your existing config is backed up to `claude_desktop_config.json.bak`). Revit repeats the registration at every start, so installing the Claude app later also works.

Then:
1. Start Revit, choose **Always Load**, and open a model. The **Sketch ProMax** tab appears on the ribbon.
2. Restart the Claude desktop app. The Sketch ProMax tools show under the tools (plug) menu. Chat as normal: "Tag all doors in the active view".

Revit must be running with a model open for the tools to work. With "Ask before changing the model" on (the default), Revit shows a Yes/No dialog before every change.

## Updates

Install once; after that Sketch ProMax updates itself, without restarting Revit. Each time Revit starts, and hourly while it is open, it checks this repository's latest GitHub Release. When there is a newer version a small window shows it downloading (percent and MB), verifying, unpacking and applying; you can hide it and keep working, or cancel the download. As soon as Revit is idle and no job is running, Sketch ProMax switches to the new version in place and says "Updated to X". The Claude app sees the new tool list straight away. Your settings are kept. Turn this off from **Settings**, or check right away with **Check now**.

How it works: Revit loads a small, never-changing loader (`SketchProMax.Loader.dll`) that owns the ribbon and loads the newest installed core (`SketchProMax.dll`) from `%LOCALAPPDATA%\SketchProMax\versions`. To switch, the loader stops the running core (its connection to Claude, its Revit event handlers and timers) and starts the new one, which takes over the same connection. The old DLLs stay loaded, unused, until Revit closes. Coming from 0.25.x, the first update still needs one Revit restart; from then on, none.

To publish an update: bump `<Version>` in both project files and push to `main`. The Release workflow builds on Windows and publishes `SketchProMax-<version>.zip` as a GitHub Release.

## What it can do

**Understand the model:** model summary, find elements by category/level/parameter, all parameters of an element, views and sheets, view templates, types, revisions, text notes, read any schedule, highlight elements.

**Documentation:**
- Tag any category in a view (doors, windows, rooms, equipment...)
- Dimension grids, walls, door/window openings and room clear widths; spot elevations placed automatically on every floor and roof in a view
- Create floor, ceiling and structural plans per level with templates and naming patterns
- Duplicate views
- Apply view templates
- Create full sheet sets in one go (including placeholder sheets) and place views on sheets
- Rename and renumber views, sheets, rooms, levels and types to firm standards
- Create schedules, then read them or export them to CSV/Excel
- Create revisions, add them to sheets, and cloud changed elements
- Translate drawings into any language (text notes, sheet, view, room and level names, schedule headings, type names, text parameters), or make them bilingual
- Sections (along a line or through elements), building elevations (N/S/E/W), interior elevations per room, callouts, 3D views with section boxes
- Visibility/graphics: hide, show or isolate categories and elements, color/halftone/transparency overrides, detail level, crop to elements, scope boxes
- Text notes, detail lines and filled regions in any view
- Align tags, turn leaders on/off, find overlapping tags and move them apart
- Format schedules: fields, headings, filters, sorting/grouping, itemize, totals
- Drawing list (sheet index) schedule, placed on a sheet
- Print to PDF; export DWG, IFC, Navisworks NWC and PNG/JPG images

**Modeling:**
- Levels (with plans) and grids, including rectangular grid layouts
- Walls, floors and ceilings (from room boundaries or outlines), and flat, hip or gable roofs
- Doors and windows inserted into walls
- Structural columns at grid intersections and beams along grids
- Any loaded family (furniture, equipment, fixtures) placed with rotation
- Move, copy and rotate elements
- Stairs (straight, L-shaped or U-shaped with a landing) with railings, railings along any path, room separation lines
- Materials: create, color and assign (by parameter or by painting faces)
- Ducts, pipes, cable trays and conduits along a path, with elbows
- Curtain wall grid layouts at set spacings

**View filters:** create rule-based filters, apply them to views or templates with colors, halftone, transparency or hide, and color-code a view by any parameter in one step (e.g. doors by fire rating) with a legend.

**Pictures, sketches and cut sheets:** attach photos or PDFs in the desktop app.
- Build a Revit family from a product photo or manufacturer cut sheet
- Redraw a hand sketch as clean linework in a drafting view
- Place pictures or PDF pages into views and sheets

**CAD to Revit:** link a DWG, read its layers, and convert layers into walls (from double lines, with matching wall types), grids (auto-named), columns, floors, room separation or model lines.

**Location and coordinates:** read and set latitude/longitude, shared coordinates and true north; acquire coordinates from a link; convert survey points to model points.

**Mass placement:** place a family, group or linked model hundreds or thousands of times from a CSV/Excel list of points, levels, rotations and parameter values (survey coordinates supported).

**Several models:** list every connected Revit window and its open models, and switch between them by name, across Revit 2024 to 2027.

**Linked models and families:** list links and query elements inside them; load families from a file or by searching Revit's content library.

**Recipes and activity log:** save a workflow that worked as a recipe and rerun it later without the AI (with blanks like {level} filled in each time), on the open model or on a whole folder of models. Every tool call is logged per day in `%APPDATA%\SketchProMax\logs`, so Claude can report what it changed.

**Export for Claude:** the Export for Claude button (Issue panel), or asking Claude to "export a project pack", writes one small zip to `Documents\SketchProMax\packs` named `<model> - Claude pack <date time>.zip` and opens the folder with it selected. Upload it to any Claude chat to prepare a report without Revit. It holds a README for Claude, project information, coordinates, levels and grids, element counts and quantities by category, type, material and level, Revit warnings, the sheet list with current revisions and issue status, views, revisions, up to 30 schedules (very large ones are skipped and listed), the newest saved check results (model check, clashes, drawing, rules, spec and BOQ checks, steel, rebar and precast schedules), change tracking, worksets, links, this model's in-Revit chat and any bug reports not sent yet. It is read-only, a few MB at most even on large models, has no images unless asked, and can be stopped from its progress window.

**Bug reports:** Report bug (Sketch ProMax panel or the pane), or telling Claude "that's a bug" in the Revit chat, saves one small zip to `Documents\Sketch ProMax\Bugs\Reports` with your one-line description, a picture of the Revit window, the versions, the model's name, the last 50 tool calls and the loaded-assemblies report. Nothing is sent. `Bug log.md` in that folder is this repository's [bug log](docs/bug-log.md), refreshed at every update check, with a "Not sent yet" list of your reports it doesn't have yet. Export chats & bugs puts the in-Revit chats of every model (last day, last week or everything), all bug reports, the tool-call log and the versions into one zip in `Documents\Sketch ProMax\Bugs\Exports`, to send to the developer.

**Notes Claude remembers:** say "remember that ..." in the Revit chat and Claude keeps it in `%APPDATA%\SketchProMax\memory.md`, for every model or for this model only; every chat starts with them. See, edit and delete them in the pane's Notes tab, or say "forget ...".

**Self-test:** the Self-test button on the Sketch ProMax tab (or asking Claude "test every tool on this model") runs each tool once on the open model, one at a time in the background, and reports pass, fail or skip. The quick check runs read-only tools only. The full check, meant for a copy of a model, also runs the editing tools and undoes each change straight after. Slow tools are skipped on large models, and the test stops if you switch to another model. The report is saved in `%APPDATA%\SketchProMax\self-test`.

**Learns new tools:** when you ask for something no tool covers, Claude writes the Revit API code, and once it works it saves it as a new named tool with its own inputs. Learned tools show up as ordinary tools in every later chat and every Revit window, run behind the same approval, and are kept in `%APPDATA%\SketchProMax\learned-tools` (copy the files to share them). Ask "what have you learned?" to list them, or ask Claude to change or delete one.


**Openings and slabs:** find where pipes, ducts, cable trays and conduits (in this model or linked MEP models) cross walls, floors and beams, and add openings with your own opening family or a plain cut; cut notches or recesses into slab edges; add slab edges; lay out hollow core planks over a slab, room or outline with plank marks.
**Title blocks:** list title block types and which sheets use them, find sheets with no title block or two, fill title block fields on many sheets at once (with {number} and {name} placeholders, and Project Information), swap the title block type, add missing ones and move them to the origin. Make a title block family from a DWG, copy title blocks, legends, drafting details or schedules from another Revit file, edit the fixed text inside a title block family, create and bind shared parameters (and add them to the title block family), place a legend on many sheets, and add or remove a revision on many sheets.

**Exports:** DWG, IFC and NWC in internal, project base point or shared coordinates, DWG in mm, m or ft; one file per sheet named from a pattern such as '{number} - {name}', with a check that every file has exactly that name; the list of drawings (LOD) to Excel, checked against the exported files; a bill of quantities to Excel; schedule checks (empty cells, repeated marks, elements left out).

**Detailing:** keynote tags, detail components (point and line-based, e.g. break lines, insulation, blocking), dimension strings through any grids, levels, reference planes, walls or family instances in plans and sections, and toposolids from survey points or a site outline.

**Steel:** load steel sections from the content library by size (UB, UC, W, HSS, SHS, IPE, HEA...), place bracing in bays (X, diagonal, V, chevron), find joints and add steel connections (beam to column, beam to beam, base plates to footings or slabs, beam to concrete wall, brace gussets, column splices), and a steel tonnage takeoff to Excel.

**Reinforcement:** rebar in concrete beams (bottom, top and side bars, stirrups), columns (perimeter bars, ties), isolated footings (mats both ways) and area reinforcement on slabs, rafts and walls, with cover set; rebar cross and long sections with tags and a bar note placed on sheets, rebar in plan views, 3D rebar views through see-through concrete, and a bar bending schedule with tonnage to Excel. For offices that detail reinforcement in 2D, the same drawings can be made with the office's own detail families instead of 3D rebar: additional-bar lines across slabs (in named groups), a mesh symbol on each slab, and column section details in schedule cells, with the family names remembered per model and every family parameter written and read back (rft_detail_families lists the families and their parameters).

**Precast and post-tensioning:** precast slab layouts (hollow core, solid planks, filigree planks, double tees) with joints, cut units and an optional structural topping, a precast unit schedule with lifting weights, post-tensioning tendons in slabs (along lines, uniform or banded on column lines) and beams with a parabolic drape over the supports, and a tendon schedule with strand quantities.

**From architecture to issued drawings:** turn the architectural model into a structural one (load-bearing walls, columns sized from the architect's, structural slabs), ramps as sloped slabs with landings and a gradient check, housekeeping pads under MEP equipment, a sleeve and opening schedule, general notes from a structural template, typical detail tables (laps, covers, hooks), and one-step issue: revision, checks, PDF and DWG per sheet with your naming, and a checked list of drawings.

**Model health and change tracking:** a scored model check (Revit warnings by type, duplicates, wall gaps, beams on nothing, columns off grid intersections, elements slightly off-axis or far from their level, unplaced or unenclosed rooms, empty required parameters), missing items (rooms with no door, columns with no footing, masonry openings with no lintel, untagged structure, empty sheets, architecture with no structure), model and link snapshots compared for added, deleted and modified elements with coloured views, and coordination progress over time (new, resolved and open clashes per test), all to CSV with optional BCF.

**Quantities, cost and documents:** check a hand-made BOQ (Excel, CSV, or rows read from a PDF) item by item against the model with differences flagged, find double-counted concrete where elements overlap without joining, show the basis of calculation for any item (elements, dimensions, formula, deductions, waste, NRM2/POMI/CESMM4 void rules) and select its elements, estimate cost from your rate table and compare with the last estimate, and check requirements from specifications, minutes, the basis of design or design reports against the model and the drawing notes.

**Embodied carbon:** assess the model's embodied carbon following EN 15978 and the RICS whole life carbon conventions: modules A1-A3 by default, with optional A4 transport (distance and mode) and A5 site waste. Quantities come from the model's material volumes: concrete by strength class (C32/40, 40MPa...) and cement replacement, reinforcement from modelled bars or an assumed kg/m³ per category that you can change, structural steel, timber, masonry, glass, aluminium and others, optionally in linked models. You get the total tCO2e and kgCO2e/m² (from a gross area plan, the slab areas, or the GIA you give), breakdowns by material class, level, category, type and material, the top contributors, the materials no factor covers (listed, never dropped), and an assumptions section, in Excel and CSV. The built-in factor table (`src/SketchProMax/Data/carbon-factors.csv`, versioned) holds generic placeholder values with their sources (ICE v3.0 as tabulated by IStructE, RICS defaults) that must be checked before reporting; give your own table (CSV or Excel: material pattern, class, unit, kgCO2e per unit, stage, source) and it is remembered for the model. Compare two options or snapshots (for example GGBS instead of CEM I, or C32/40 instead of C40/50) to show the saving, and colour the model by carbon intensity in a new 3D view.

**Value engineering (screening aids, not design; an engineer confirms every item):** ve_opportunities ranks where cost or material could be saved, with an estimated cost and carbon saving, a confidence and the element ids: slabs, beams and walls thicker than typical span/depth ratios, many slightly different sizes that could become one (suggested groups), the concrete grade mix and grades above typical, modelled reinforcement above typical kg/m³, steel kg/m², formwork complexity per level, and over-specification against a spec. The rules of thumb are listed in every report and can be replaced; costs come from cost_estimate's rate table (generic placeholder rates otherwise) and carbon from the embodied carbon factors. ve_compare_options sets RC flat slab, beam and slab, PT, precast hollow-core, steel composite, raft and piles side by side for a grid, span and load (quantities, cost, carbon, a rough programme index, risks), sized by documented rules of thumb or measured from the model or a design option. ve_view colours the ranked items in a new 3D view; ve_apply tries a change (switch types, rationalise sizes, change the concrete grade) on a saved copy (for a workshared model: run it in a detached copy, or use a design option) or in a design option, with before and after cost and carbon; ve_register keeps the VE register per model (ids, status proposed/accepted/rejected, owner) and exports it to Excel and CSV.

**Analysis:** check the analytical model (free ends, gaps, short members, missing analytical or physical elements) and export it to SAP2000 (.s2k) with sections, shells, materials and supports, plus CSV tables.

**One prompt to an issued package:** link the architectural model (link_model), build the structure from it with door and window openings, add a raft a set depth under the slab on grade (create_raft), make GA plans with sheets, tags and dimensions, reinforce rafts, slabs, beams and columns with typical values marked ASSUMED, and issue the set "Issued for Construction" (revision, checks, PDF/DWG, list of drawings). work_progress saves the plan and each step's result with the model, so a long job picks up where it stopped after "continue".

**Marine, infrastructure and robustness:** piles (round, square or tubular, vertical or raked, at points, on a grid or in rows along an alignment), revetments and rubble breakwaters with armour, underlayer and filter layers, crown walls, gravity block quay walls, cantilever retaining walls, masonry walls, gravity dams, open-piled jetties and quay decks, bridges (deck, piers, abutments), floating pontoons, manholes, planters and hoppers; bollards, fenders, ladders, cleats and crane rails along a quay edge; design vessels (tug to ULCV, hopper barge and dredger) at a berth; vessel impact screening (PIANC berthing energy, AASHTO collision force, exposed piles and piers); and progressive collapse screening (notional column removal, transfer columns, tie forces).

**Coordination:** write issues (elements, snapshot and camera) to a BCF file for ACC, Navisworks, Solibri or BIMcollab. Clash detection between trades and linked models (hard and clearance clashes, CSV and BCF), a drawing checker (grids and levels against links, overridden dimensions, duplicate marks, sections not on sheets, empty sheets, missing title block fields), code and standards rules checks from your own rule packs, construction sequence numbering with step-by-step 3D views, copy/monitor of levels, grids and columns from links, and loading plans (live and superimposed dead loads per room) from the architectural link.

**Anything else:** Claude can write and run short Revit API code (C#) for what no dedicated tool covers, such as stairs, railings, materials, curtain walls or MEP. Read-only code runs freely; code that changes the model is shown to you first and is one Undo step.

**Model data:** set parameters in bulk, place rooms automatically, duplicate types with new values, swap types, manage worksets, delete elements.

**QA:** deterministic model audit (unplaced/unbounded/unnumbered rooms, duplicate room numbers, untagged doors and windows, views not on sheets, empty sheets) and visual review of rendered views and sheets.

**Firm standards:** write your conventions in plain English in Settings (naming, numbering, tag styles). Claude reads them at the start of every conversation.

### Try

- "What's in this model?"
- "Which Revit models do you have open? Switch to the Podium model."
- "Add openings in the walls and slabs wherever the linked mechanical model's ducts and pipes pass, 50 mm clearance."
- "Lay hollow core planks over the Level 2 slab, 1200 wide, spanning north-south."
- "Put the A1 title block on every A-series sheet and fill Drawn By with AH and the date with today."
- "Dimension columns C1 to C6 along grid B, then keynote the walls in this section."
- "Color the Level 1 plan by room department and give me the legend."
- (attach a chair cut sheet) "Make a family from this and place 6 in the meeting room."
- (attach a sketch photo) "Turn this sketch into a 1:10 drafting view."
- "Set up 4 levels at 3.5 m, a 5 x 4 grid at 8 m spacing, columns at every grid intersection and beams along the grids on each level."
- "Draw a 20 x 12 m building on Level 1 with 300 mm walls, a floor, a 30-degree hip roof with 600 mm overhang, and 4 windows on the south wall."
- "Create floor plans for every level using the 'A - Floor Plan' template, named 'A - {level} - GA'."
- "Make a cartoon set: A-001 cover, A-101 to A-104 for each level plan, plus placeholder sheets S-101 and M-101."
- "Dimension the grids and the exterior walls on Level 1."
- "Run a QA check, fix what you safely can, and list the rest."
- "Create revision 'Bulletin 03', add it to A-101 and A-102 and cloud the doors whose fire rating changed."
- "Translate every text note on sheet A-101 to Arabic."
- "Look at sheet A-101 and tell me if any tags overlap."

## Project layout

```
build.ps1 / install.ps1      Build everything into .\dist / install for the current user
.github/workflows/release.yml  Publishes a GitHub Release for each new version on main
assets/logo/                 Product logo, icon and wordmark
src/SketchProMax.Loader/     What Revit loads: the ribbon, its buttons and icons, and loading/switching the newest core
src/SketchProMax/            The Revit add-in core (net48 for Revit 2024, net8.0-windows for 2025/2026, net10.0-windows for 2027)
  CoreEntry.cs               What the loader calls: start, stop, ribbon commands and ribbon status
  App.cs                     Starts/stops the dispatcher, bridge and update loop; boots through the loader for 0.25.x manifests
  Commands/                  What the ribbon buttons do, and running recipes / learned tools from the ribbon
  Agent/SystemPrompt         Instructions the desktop app receives from the connector
  Bridge/BridgeServer        Per-Revit-window named-pipe server the connector talks to
  Revit/                     Main-thread dispatcher (ExternalEvent), helpers, Revit 2024 dependency loader
  Tools/                     The 202 IRevitTool implementations and the registry (CodeTools compiles C# with Roslyn)
  UI/                        Settings, update progress, quick-tool input and result windows
  Settings/                  Per-user settings (approval switch, auto-update, firm standards)
  Update/Updater             Self-update from GitHub Releases, with progress, switched to without restarting Revit
src/SketchProMax.Mcp/        MCP server (stdio) the Claude desktop app launches; finds open Revit windows and forwards tool calls
```

## Adding a tool

1. Implement `IRevitTool` (name, description, JSON schema, `ModifiesModel`, `Execute`).
2. Wrap model changes in `RevitHelpers.StartTransaction(doc, "...")` and `Commit()`.
3. Return `ToolResult.Ok(new { ... })` with counts and element ids so Claude can verify its work.
4. Register it in `ToolRegistry.CreateDefault()`. It appears in the desktop app the next time Revit and the app start.

## Status

The add-in compiles for Revit 2024 to 2027, and the desktop-app connector has been tested end to end against a simulated Revit. It has not been run inside a real Revit session yet, so expect small fixes on first launch.
