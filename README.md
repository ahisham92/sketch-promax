# Sketch ProMax

An AI agent for Autodesk Revit 2024, 2025 and 2026, powered by Claude. Ask in plain language; it reads, documents, audits and edits the open model through 53 tools. It asks for your approval before every change, and every change can be undone with Ctrl+Z.

See [docs/PLAN.md](docs/PLAN.md) for the comparison with SketchPro, the roadmap and the architecture.

## How you use it

You chat in the **Claude desktop app**, next to Revit, on your normal Claude plan: no API key and no extra payment. The work happens in Revit. There is no chat inside Revit, because that would need a paid API key; your Claude plan can only be used through Anthropic's own apps.

On the Revit ribbon, the **AI Agent** panel has two buttons:
- **Status** shows whether the desktop app can reach this Revit window.
- **Settings** holds your firm standards and the "ask me before changing the model" switch.

Several Revit windows, and different Revit versions, can be open at once. Name the model in chat ("in the Tower model, ...") and Claude switches to it.

If your company manages your Claude account, IT may need to allow the desktop app's local tools (MCP servers).

## Install

Requirements: Windows, Revit 2024/2025/2026, and the .NET 8 SDK to build.

```powershell
.\build.ps1      # builds the add-in for 2024, 2025, 2026 and the desktop-app connector into .\dist
.\install.ps1    # installs for your user, no admin needed
```

`install.ps1` copies the add-in into each installed Revit version, installs the connector to `%LOCALAPPDATA%\SketchProMax\mcp`, and registers it with the Claude desktop app if it's installed (your existing config is backed up to `claude_desktop_config.json.bak`).

Then:
1. Start Revit, choose **Always Load**, and open a model. The **Sketch ProMax** tab appears on the ribbon.
2. Restart the Claude desktop app. The Sketch ProMax tools show under the tools (plug) menu. Chat as normal: "Tag all doors in the active view".

Revit must be running with a model open for the tools to work. With "Ask before changing the model" on (the default), Revit shows a Yes/No dialog before every change.

## Updates

Install once; after that Sketch ProMax updates itself. Each time Revit starts it checks this repository's latest GitHub Release. When there is a newer version it downloads it in the background and shows a notice; the new version runs the next time you open Revit (and restart the Claude desktop app). Your settings are kept. Turn this off, or check right away, from the ribbon's **Settings** and **Status** buttons.

To publish an update: bump `<Version>` in both project files and push to `main`. The Release workflow builds on Windows and publishes `SketchProMax-<version>.zip` as a GitHub Release.

## What it can do

**Understand the model:** model summary, find elements by category/level/parameter, all parameters of an element, views and sheets, view templates, types, revisions, text notes, read any schedule, highlight elements.

**Documentation:**
- Tag any category in a view (doors, windows, rooms, equipment...)
- Dimension grids and walls
- Create floor, ceiling and structural plans per level with templates and naming patterns
- Duplicate views
- Apply view templates
- Create full sheet sets in one go (including placeholder sheets) and place views on sheets
- Rename and renumber views, sheets, rooms, levels and types to firm standards
- Create schedules, then read them or export them to CSV/Excel
- Create revisions, add them to sheets, and cloud changed elements
- Edit and translate text notes (e.g. English to Arabic)
- Print to PDF

**Modeling:**
- Levels (with plans) and grids, including rectangular grid layouts
- Walls, floors and ceilings (from room boundaries or outlines), and flat, hip or gable roofs
- Doors and windows inserted into walls
- Structural columns at grid intersections and beams along grids
- Any loaded family (furniture, equipment, fixtures) placed with rotation
- Move, copy and rotate elements

**View filters:** create rule-based filters, apply them to views or templates with colors, halftone, transparency or hide, and color-code a view by any parameter in one step (e.g. doors by fire rating) with a legend.

**Pictures, sketches and cut sheets:** attach photos or PDFs in the desktop app.
- Build a Revit family from a product photo or manufacturer cut sheet
- Redraw a hand sketch as clean linework in a drafting view
- Place pictures or PDF pages into views and sheets

**Several models:** list every connected Revit window and its open models, and switch between them by name, across Revit 2024, 2025 and 2026.

**Model data:** set parameters in bulk, place rooms automatically, duplicate types with new values, swap types, manage worksets, delete elements.

**QA:** deterministic model audit (unplaced/unbounded/unnumbered rooms, duplicate room numbers, untagged doors and windows, views not on sheets, empty sheets) and visual review of rendered views and sheets.

**Firm standards:** write your conventions in plain English in Settings (naming, numbering, tag styles). Claude reads them at the start of every conversation.

### Try

- "What's in this model?"
- "Which Revit models do you have open? Switch to the Podium model."
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
src/SketchProMax/            The Revit add-in (net48 for Revit 2024, net8.0-windows for 2025/2026)
  App.cs                     Ribbon (Status, Settings), dispatcher, desktop-app bridge
  Agent/SystemPrompt         Instructions the desktop app receives from the connector
  Bridge/BridgeServer        Per-Revit-window named-pipe server the connector talks to
  Revit/                     Main-thread dispatcher (ExternalEvent), helpers, Revit 2024 dependency loader
  Tools/                     The 51 IRevitTool implementations and the registry
  UI/                        Settings dialog
  Settings/                  Per-user settings (approval switch, auto-update, firm standards)
  Update/Updater             Self-update from GitHub Releases
src/SketchProMax.Mcp/        MCP server (stdio) the Claude desktop app launches; finds open Revit windows and forwards tool calls
```

## Adding a tool

1. Implement `IRevitTool` (name, description, JSON schema, `ModifiesModel`, `Execute`).
2. Wrap model changes in `RevitHelpers.StartTransaction(doc, "...")` and `Commit()`.
3. Return `ToolResult.Ok(new { ... })` with counts and element ids so Claude can verify its work.
4. Register it in `ToolRegistry.CreateDefault()`. It appears in the desktop app the next time Revit and the app start.

## Status

The add-in compiles for Revit 2024, 2025 and 2026, and the desktop-app connector has been tested end to end against a simulated Revit. It has not been run inside a real Revit session yet, so expect small fixes on first launch.
