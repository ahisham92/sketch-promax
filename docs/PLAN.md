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
- **Revit 2024, 2025 and 2026.**
- **No extra cost:** chat from the Claude desktop app on the existing Claude plan. The in-Revit pane with an API key stays as an optional pay-per-use mode.
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
| Families from images/PDFs, rendering, web viewer, voice | Not yet |

**Where Sketch ProMax is already better:**
1. **No extra cost.** It runs on your Claude plan through the desktop app. SketchPro is sold per seat behind a demo.
2. **Open.** Any `IRevitTool` you write appears in both chat surfaces. SketchPro has no public API.
3. **Visual QA.** Claude looks at rendered sheets, not just data.
4. **Revit 2024 to 2026 from one codebase,** and the model and data path are yours to choose.
5. **Transparent.** Every change is a named transaction with a confirm step, and tool calls are visible in the chat.

## 4. Roadmap

**Done:** the 34 tools above, the in-Revit pane, the desktop-app connector (MCP), install scripts, and builds for Revit 2024/2025/2026.

**Next (documentation depth):** dimension openings and room widths, elevation and section creation, interior elevations per room, legends and keynotes, schedule formatting (filters, grouping, totals), sheet-index and drawing-list automation, align/tidy tags (using visual QA), linked-model queries, a conversation log export.

**Then (beyond SketchPro):** "recipes" (save a successful run as a repeatable script, run without the LLM), batch runs over many models, families from photos or PDF cut sheets, bilingual Arabic/English sheet production, ACC Issues/BCF export, MEP/structural checks.

**Later (parked by request):** building-code rule packs, embodied carbon.

## 5. Architecture

```
Claude desktop app ──stdio──► SketchProMax.Mcp.exe ──named pipe──► BridgeServer ─┐   (no-cost mode)
                                                                                 │
┌──────────────────── Revit process ────────────────────┐                        │
│  BridgeServer ◄─────────────────────────────────────────────────────────────────┘
│     └─► same ToolRegistry + RevitDispatcher as below  │
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
- **Two front ends, one tool set.** The desktop app reaches Revit through a small MCP server (stdio) that forwards calls over a per-user named pipe to the add-in. The pane calls the Claude API directly. Both run the same `IRevitTool` registry on Revit's main thread, and both confirm changes before running them.
- **Model (pane mode).** Claude Opus 5.5 by default (`claude-opus-5-5`), effort `high`, prompt caching on tools + system prompt, server-side refusal fallback enabled. Model and effort are user settings.
- **Secrets.** API key from `ANTHROPIC_API_KEY` or stored encrypted with Windows DPAPI per user. Never in plain text.
- **Targets.** Revit 2024 (.NET Framework 4.8), 2025 and 2026 (.NET 8) build from the same source with `-p:RevitVersion=` (or `build.ps1` for all three). Modern C# on 4.8 comes from compiler polyfills (PolySharp). Revit 2027 (.NET 10) is wired in the project file but needs the .NET 10 SDK to build.

## 6. Risks and open questions

1. **Dependency conflicts.** The Anthropic SDK brings newer `System.Text.Json`; other add-ins or Revit may load a different version. On Revit 2024 (.NET Framework, no binding redirects for add-ins) an `AssemblyResolve` handler loads these from the add-in folder; if another add-in has already loaded an older version first, that one wins and may break the SDK. Test early in all three versions; on 2025+ fall back to an isolated `AssemblyLoadContext` if needed.
2. **Prompt-injected model content.** Text in a model (parameter values, linked files) is data, not instructions. The system prompt says so; the approval gate is the hard stop.
3. **Cost control.** Large models and image captures cost tokens. Add per-conversation budget and the cost meter in Phase 1.
4. **Professional responsibility.** The agent proposes; the architect approves. Keep the audit log exportable.
5. **Company-managed Claude accounts** may restrict local tools in the desktop app; IT may need to allow them.
