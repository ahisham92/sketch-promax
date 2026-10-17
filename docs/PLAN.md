# Sketch ProMax: gap analysis and product plan

_Draft 2, 14 Oct 2026_

## 1. What Sketch Pro is today

SketchPro (sketchpro.ai) is an AI copilot that runs as a docked panel inside Revit. Users type (or speak) instructions and it acts on the live model. From the vendor's site and academy demos, it covers:

| Area | What SketchPro shows |
|---|---|
| Documentation | Auto-dimension plans, tag doors/rooms/equipment, create and organize views and sheets ("cartoon sets"), placeholder sheets, rename/renumber sheets to firm standards |
| Schedules & data | Create/format/export schedules, material cost estimate from a cost spreadsheet, validate area targets against a brief, query linked models |
| Modeling from references | Families from reference images or PDF cut sheets, 2D drafting view from a hand sketch |
| Family editing | Bulk-rename parameters and types, duplicate types and swap instances |
| QA/QC | Documentation review, "model health check" against an uploaded checklist, reusable "custom agents" built from checklists |
| Construction admin | Revisions, clouds, bulletin audits, print sets of affected sheets |
| Other | Firm "AI Knowledge Base" of standards, mentor mode for juniors, translations, voice input, web read-only viewer, AI rendering (separate product) |
| Controls | Changes previewed and approved, full undo/history, audit logs, SSO and RBAC on enterprise |

Sources: [sketchpro.ai](https://sketchpro.ai/), [SketchPro Academy](https://sketchpro.ai/academy), [AEC+Tech listing](https://www.aecplustech.com/tools/sketchpro-ai), [DataDrivenAEC review](https://datadrivenaec.com/tools/sketchpro-ai), [Toolspedia](https://toolspedia.io/ai-tool/sketchpro-2/).

**Honest read:** SketchPro is broad. Sketch ProMax matches its documentation core (section 3) and wins on cost, openness and visual QA.

## 2. Scope decided (14 Oct 2026)

- **Standalone:** Sketch ProMax must cover SketchPro's documentation work on its own, not just add to it.
- **Revit 2024, 2025, 2026 and 2027.**
- **No extra cost:** chat from the Claude desktop app on the existing Claude plan. There is no in-Revit chat, since that would need a paid API key.
- **Out of scope for now:** embodied carbon and building-code rule packs.

## 3. Feature comparison

| SketchPro capability | Sketch ProMax today |
|---|---|
| Dimension plans | `dimension_grids`, `dimension_walls` (wall faces). Opening dimensions are next. |
| Tag doors/rooms/equipment | `tag_elements_in_view`, any category, skips already-tagged elements |
| Views and sheets, cartoon sets | `create_plan_views`, `duplicate_views`, `apply_view_template`, `create_sheets` (batch, title blocks, view placement), `place_views_on_sheet` |
| Placeholder sheets | `create_sheets` with `placeholder: true` |
| Rename/renumber to standards | `rename_elements` (views, sheets, rooms, levels, types; safe swaps of numbers) |
| Schedules | `create_schedule`, `read_schedule`, `export_schedule` (CSV/Excel) |
| Worksets | `manage_worksets` (create, move elements) |
| Family/type editing | `duplicate_type` (with values, swap instances), `change_type`, `set_parameters` |
| Revisions and bulletins | `create_revision` (adds to sheets), `cloud_elements`, `list_revisions` |
| Print sets | `export_pdf` (combined or per sheet) |
| Translations | `list_text_notes` + `update_text_notes` (Claude translates) |
| QA/QC and model health | `run_model_audit` (deterministic) + `capture_view_image` (visual review) |
| Firm standards knowledge base | Firm standards in Settings, applied in every conversation |
| Quick select | `query_elements` + `select_elements` |
| Mentor mode | Ask "explain as you go"; Claude narrates each step |
| Modeling (SketchPro: families from references only) | **Beyond SketchPro:** `create_levels`, `create_grids`, `create_walls`, `create_floors`, `create_ceilings`, `create_roof`, `place_doors_windows`, `place_family_instances`, `place_columns`, `place_beams`, `move_copy_rotate` |
| Families from images/PDFs | `create_family_from_shapes` (Claude reads the photo or cut sheet, builds solids, loads the family) |
| 2D drafting from a sketch | `create_drafting_view` (lines, polylines, circles, arcs, text, line styles) |
| (not in SketchPro) View filters | `create_view_filter`, `apply_view_filter`, `color_by_parameter`, `list_view_filters` |
| Images in Revit | `import_image` (pictures and PDF pages into views and sheets); 📎 attachments in the Revit chat |
| Rendering, web viewer, voice | Not yet |

**Where Sketch ProMax is already better:**
1. **No extra cost.** It runs on your Claude plan through the desktop app. SketchPro is sold per seat behind a demo.
2. **Open.** Any `IRevitTool` you write appears in both chat surfaces. SketchPro has no public API.
3. **Models, not just documents.** Levels, grids, walls, floors, roofs, openings, structure and furniture from a description.
4. **Visual QA.** Claude looks at rendered sheets, not just data.
5. **Revit 2024 to 2027 from one codebase,** and the model and data path are yours to choose.
6. **Transparent.** Every change is a named transaction with a confirm step, and tool calls are visible in the chat.

## 4. Roadmap

**Done:** 78 Revit tools (documentation, modeling, view filters, images, views/graphics/annotation, schedules, exports, links, families, stairs/railings/materials/MEP, BCF, recipes and activity log, and a run-Revit-code catch-all), the desktop-app connector (MCP) with several Revit windows and versions at once, ribbon Status and Settings, self-update from GitHub Releases, and builds for Revit 2024 to 2027.

**Next (documentation depth):** dimension openings and room widths, elevation and section creation, interior elevations per room, legends and keynotes, schedule formatting (filters, grouping, totals), sheet-index and drawing-list automation, align/tidy tags (using visual QA), linked-model queries, a conversation log export.

**Then (beyond SketchPro):** "recipes" (save a successful run as a repeatable script, run without the LLM), batch runs over many models, families from photos or PDF cut sheets, bilingual Arabic/English sheet production, ACC Issues/BCF export, MEP/structural checks.

**Later (parked by request):** building-code rule packs, embodied carbon.

## 5. Architecture

```
Claude desktop app ──stdio──► SketchProMax.Mcp.exe ──┬─ named pipe ─► Revit 2024 window (BridgeServer)
  (your Claude plan)          finds Revit windows    ├─ named pipe ─► Revit 2025 window
                              from %APPDATA%\        └─ named pipe ─► Revit 2026 window
                              SketchProMax\sessions

┌──────────────────── each Revit process ───────────────┐
│  Loader: ribbon, loads newest core, live switch-over  │
│  BridgeServer (pipe SketchProMax.Revit.<user>.<pid>)  │
│     │  ping / activate_document / call_tool           │
│     ▼                                                 │
│  Approval (per level)    ──► RevitDispatcher          │
│        ─ ExternalEvent ─► Revit main thread           │
│        IRevitTool.Execute(UIApplication)              │
│        (one Transaction per change)                   │
│     ▲                                                 │
│     └── result (JSON / PNG) back to Claude            │
└────────────────────────────────────────────────────────┘
```

Key decisions:
- **Threading.** The Revit API is single-threaded. The pipe server waits on a background thread; every tool runs on Revit's main thread via one `ExternalEvent` queue (`RevitDispatcher`). Revit stays responsive while Claude thinks.
- **Tools are small, typed and composable.** Each tool declares a JSON schema, whether it changes the model, and returns JSON (or an image). Counts and ids come back so Claude can verify its own work.
- **Human in the loop.** Risky calls (code, new recipes and learned tools, deletes that take other elements, overwriting files, what-ifs, the issue gate) always ask in Revit; other model changes ask per the approval level in Settings (only risky, once per task, or every change). At Always allow nothing asks but the issue gate. Each change is a named transaction, so Undo works per step.
- **Deterministic where it matters.** QA checks and future code checks are plain code. Claude interprets, explains and fixes; it doesn't do the counting.
- **One front end: the Claude desktop app.** Each Revit window announces itself with a file in `%APPDATA%\SketchProMax\sessions` and listens on its own pipe. The connector pings them, drops stale ones, and routes calls to the window and model the user named (`list_revit_models`, `use_revit_model`), or to the only one open.
- **No secrets.** No API key is stored or needed.
- **Targets.** Revit 2024 (.NET Framework 4.8), 2025 and 2026 (.NET 8) and 2027 (.NET 10) build from the same source with `-p:RevitVersion=` (or `build.ps1` for all four). Modern C# on 4.8 comes from compiler polyfills (PolySharp).

## 6. Risks and open questions

1. **Dependency conflicts.** On Revit 2024 the add-in ships `System.Text.Json` 8; an `AssemblyResolve` handler loads it from the add-in folder. If another add-in has already loaded an older version first, that one wins. Test early in all three versions.
2. **Prompt-injected model content.** Text in a model (parameter values, linked files) is data, not instructions. The system prompt says so; the approval gate is the hard stop.
3. **Plan limits.** Large models and image reviews use more of the Claude plan's usage limits.
4. **Professional responsibility.** The agent proposes; the architect approves. Keep the audit log exportable.
5. **Company-managed Claude accounts** may restrict local tools in the desktop app; IT may need to allow them.
