# Sketch ProMax: gap analysis and product plan

_Draft 1, 14 Oct 2026_

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

**Honest read:** SketchPro is broad and already covers most "documentation copilot" basics. Sketch ProMax should not try to win by re-listing those features; it should win on the areas below, where SketchPro is either closed, silent, or documentation-only.

## 2. Gaps (what Sketch ProMax adds)

Items marked _(inferred)_ are not confirmed missing; public material simply doesn't show them. Verify in a SketchPro demo before treating them as differentiators.

| # | Gap in SketchPro | Sketch ProMax answer |
|---|---|---|
| G1 | **Closed platform.** No public API or SDK; firms can't add their own tools (DataDrivenAEC, Toolspedia). | Open tool registry: any C# class implementing `IRevitTool` becomes an agent capability. Phase 2: expose the same tools as an **MCP server** so Claude Desktop/Claude Code or other agents can drive Revit. |
| G2 | **Opaque AI provider and data path.** Site doesn't say which model runs or where data is processed; no SOC 2 confirmation found. | Bring-your-own Claude key, model and region. Supports Anthropic API, AWS Bedrock, Google Vertex and Microsoft Foundry so data stays in the firm's chosen cloud/region (important for GCC government clients). Every request/response logged locally. |
| G3 | **Pricing behind a demo.** | Internal tool; cost is token spend only. Show live token/cost per conversation in the pane. |
| G4 | **Documentation-centric.** Engineering disciplines barely appear. _(inferred)_ | Discipline packs: structural (grids, column/beam layouts, load-path sanity checks), MEP (system connectivity, unconnected connectors, duct/pipe sizing checks), clash detection between links with grouped results. |
| G5 | **No explicit building-code engine.** QA runs firm checklists, not codes. _(inferred)_ | Rule packs that compute, not guess: egress distance/occupant load (IBC/NFPA 101), accessibility clearances, plus regional packs (Saudi Building Code, Dubai/Abu Dhabi codes). Deterministic checks; Claude explains and proposes fixes. |
| G6 | **Visual QA of drawings.** Unclear whether it "looks" at sheets. _(inferred)_ | `capture_view_image` renders a view/sheet to PNG and gives it to Claude to spot overlapping tags, clutter, missing annotation. (In the starter code.) |
| G7 | **Non-reproducible AI runs.** Each run is a fresh LLM conversation. _(inferred)_ | "Recipes": save a successful conversation as a deterministic script (tool-call sequence with parameters) that reruns without the LLM, can be scheduled, and is code-reviewable. |
| G8 | **Single-model, interactive only.** _(inferred)_ | Batch mode across many models (open, run recipe, save, close) and cloud batch via Autodesk Platform Services Design Automation. |
| G9 | **Bilingual delivery.** Translation exists; RTL/Arabic sheet production not shown. _(inferred)_ | Arabic/English bilingual sheets: paired text notes, RTL-safe fonts, bilingual room/sheet names and schedules. |
| G10 | **Sustainability.** Not mentioned. | Embodied carbon from material quantities (EC3/ICE factors), plus quick daylight/area-efficiency metrics. |
| G11 | **Issue tracking.** Not mentioned. _(inferred)_ | Push QA findings to ACC Issues or BCF files with viewpoints. |

## 3. Feature roadmap

**Phase 0: foundation (this starter, done)**
Dockable chat pane, Claude tool-use loop, approval gate for model changes, each change a named Revit transaction (Ctrl+Z works), 13 tools: model context, element query, parameter dump, views/sheets, types, selection, QA audit, view image capture, set parameters, create walls, tag elements, create sheet, create schedule.

**Phase 1: documentation parity**
Dimensioning (grids, walls, openings), view creation with templates, sheet sets and renumbering, schedule formatting/export to Excel, rooms from boundaries, revisions/clouds, firm standards editor with examples, streaming replies, token/cost meter, conversation log export.

**Phase 2: platform**
MCP server for the tool registry; plugin folder for firm tools (C# DLLs, later Python); recipes (record, edit, replay); batch runner; Bedrock/Vertex/Foundry backends; centrally managed settings for the firm (model, standards, allowed tools).

**Phase 3: differentiators**
Code-compliance rule packs (G5), discipline packs (G4), visual drawing QA at sheet-set scale (G6), bilingual sheets (G9), embodied carbon (G10), ACC Issues/BCF (G11), families from images/PDF cut sheets.

## 4. Architecture

```
┌──────────────────── Revit process ────────────────────┐
│  Ribbon button ─► Dockable ChatPane (WPF)             │
│                        │ user message                 │
│                        ▼                              │
│                 AgentSession (background thread)      │
│      ┌─────────────────┼──────────────────┐           │
│      │ HTTPS           │ tool_use         │           │
│      ▼                 ▼                  │           │
│  Claude API      Approval gate (pane) ──► │           │
│  (Messages +           │                  │           │
│   tool use)            ▼                  │           │
│               RevitDispatcher ─ ExternalEvent ─► Revit main thread
│                        │                     IRevitTool.Execute(UIApplication)
│                        ▼                     (one Transaction per change)
│                   tool_result (JSON / PNG) ──► back to Claude
└────────────────────────────────────────────────────────┘
```

Key decisions:
- **Threading.** The Revit API is single-threaded. The agent loop awaits HTTP on a background thread; every tool runs on Revit's main thread via one `ExternalEvent` queue (`RevitDispatcher`). This keeps Revit responsive while Claude thinks.
- **Tools are small, typed and composable.** Each tool declares a JSON schema, whether it changes the model, and returns JSON (or an image). Counts and ids come back so Claude can verify its own work.
- **Human in the loop.** Model-changing tools pause for Approve/Reject in the pane (configurable). Each change is a named transaction, so Undo works per step.
- **Deterministic where it matters.** QA checks and future code checks are plain code. Claude interprets, explains and fixes; it doesn't do the counting.
- **Model.** Claude Opus 5.5 by default (`claude-opus-5-5`), effort `high`, prompt caching on tools + system prompt, server-side refusal fallback enabled. Model and effort are user settings.
- **Secrets.** API key from `ANTHROPIC_API_KEY` or stored encrypted with Windows DPAPI per user. Never in plain text.
- **Targets.** Revit 2024 (.NET Framework 4.8), 2025 and 2026 (.NET 8) build from the same source with `-p:RevitVersion=` (or `build.ps1` for all three). Modern C# on 4.8 comes from compiler polyfills (PolySharp). Revit 2027 (.NET 10) is wired in the project file but needs the .NET 10 SDK to build.

## 5. Risks and open questions

1. **Dependency conflicts.** The Anthropic SDK brings newer `System.Text.Json`; other add-ins or Revit may load a different version. On Revit 2024 (.NET Framework, no binding redirects for add-ins) an `AssemblyResolve` handler loads these from the add-in folder; if another add-in has already loaded an older version first, that one wins and may break the SDK. Test early in all three versions; on 2025+ fall back to an isolated `AssemblyLoadContext` if needed.
2. **Prompt-injected model content.** Text in a model (parameter values, linked files) is data, not instructions. The system prompt says so; the approval gate is the hard stop.
3. **Cost control.** Large models and image captures cost tokens. Add per-conversation budget and the cost meter in Phase 1.
4. **Professional responsibility.** The agent proposes; the architect approves. Keep the audit log exportable.
5. **Decisions needed from you:** target Revit versions, which disciplines first (G4/G5), cloud/region for Claude (direct API vs Bedrock/Vertex/Foundry), and whether this lives in a GitHub repo.
