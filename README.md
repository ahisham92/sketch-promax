# Sketch ProMax

An AI agent for Autodesk Revit 2024, 2025 and 2026, powered by Claude. Ask in plain language; it reads, documents, audits and edits the open model through 51 Revit tools. It asks for your approval before every change, and every change can be undone with Ctrl+Z.

See [docs/PLAN.md](docs/PLAN.md) for the comparison with SketchPro, the roadmap and the architecture.

## Two ways to use it

| | Claude desktop app (recommended) | Chat pane inside Revit |
|---|---|---|
| Cost | Included in your Claude plan, no extra payment | Pay per use with an Anthropic API key |
| Where you chat | The Claude desktop app, next to Revit | A docked pane in Revit |
| Setup | Install the desktop app, run `install.ps1` | Paste an API key in the pane's Settings |

Both use the same tools on the same open model. Your Claude plan can only be used through Anthropic's own apps, which is why the no-cost route goes through the desktop app. If your company manages your Claude account, IT may need to allow the desktop app's local tools (MCP servers).

## Install

Requirements: Windows, Revit 2024/2025/2026, and the .NET 8 SDK to build.

```powershell
.\build.ps1      # builds the add-in for 2024, 2025, 2026 and the desktop-app connector into .\dist
.\install.ps1    # installs for your user, no admin needed
```

`install.ps1` copies the add-in into each installed Revit version, installs the connector to `%LOCALAPPDATA%\SketchProMax\mcp`, and registers it with the Claude desktop app if it's installed (your existing config is backed up to `claude_desktop_config.json.bak`).

Then:
1. Start Revit, choose **Always Load**, and open a model. The **Sketch ProMax** tab appears on the ribbon.
2. **Desktop app:** restart the Claude desktop app. The Sketch ProMax tools show under the tools (plug) menu. Chat as normal: "Tag all doors in the active view".
3. **Revit pane:** click **Sketch ProMax** on the ribbon, then **Settings**, and paste an API key (or set `ANTHROPIC_API_KEY`). Keys are encrypted with Windows DPAPI.

Revit must be running with a model open for the tools to work. With "Ask before changing the model" on (the default), Revit shows a Yes/No dialog before every change made from the desktop app.

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

**Pictures, sketches and cut sheets:** attach photos or PDFs in the Revit chat (📎) or the desktop app.
- Build a Revit family from a product photo or manufacturer cut sheet
- Redraw a hand sketch as clean linework in a drafting view
- Place pictures or PDF pages into views and sheets

**Model data:** set parameters in bulk, place rooms automatically, duplicate types with new values, swap types, manage worksets, delete elements.

**QA:** deterministic model audit (unplaced/unbounded/unnumbered rooms, duplicate room numbers, untagged doors and windows, views not on sheets, empty sheets) and visual review of rendered views and sheets.

**Firm standards:** write your conventions in plain English in Settings (naming, numbering, tag styles). They're applied in every conversation, in both the desktop app and the pane.

### Try

- "What's in this model?"
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
src/SketchProMax/            The Revit add-in (net48 for Revit 2024, net8.0-windows for 2025/2026)
  App.cs                     Ribbon, dockable pane, dispatcher, desktop-app bridge
  Agent/                     Claude tool-use loop for the in-Revit pane (Anthropic C# SDK) and agent instructions
  Bridge/BridgeServer        Named-pipe server the desktop-app connector talks to
  Revit/                     Main-thread dispatcher (ExternalEvent), helpers, Revit 2024 dependency loader
  Tools/                     The 51 IRevitTool implementations and the registry
  UI/                        Chat pane and settings dialog
  Settings/                  Per-user settings, DPAPI-encrypted API key
src/SketchProMax.Mcp/        MCP server (stdio) the Claude desktop app launches; forwards tool calls to Revit
```

## Adding a tool

1. Implement `IRevitTool` (name, description, JSON schema, `ModifiesModel`, `Execute`).
2. Wrap model changes in `RevitHelpers.StartTransaction(doc, "...")` and `Commit()`.
3. Return `ToolResult.Ok(new { ... })` with counts and element ids so Claude can verify its work.
4. Register it in `ToolRegistry.CreateDefault()`. It appears in both the pane and the desktop app automatically.

## Status

The add-in compiles for Revit 2024, 2025 and 2026, and the desktop-app connector has been tested end to end against a simulated Revit. It has not been run inside a real Revit session yet, so expect small fixes on first launch.
