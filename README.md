# Sketch ProMax

An AI agent for Autodesk Revit, powered by Claude. Chat with your model in a docked pane; the agent queries, audits and edits the model through typed Revit tools, asking your approval before any change.

See [docs/PLAN.md](docs/PLAN.md) for the gap analysis against SketchPro, the roadmap and the architecture.

## Requirements

- Windows with Revit 2024, 2025 or 2026
- .NET 8 SDK (it also builds the .NET Framework 4.8 version for Revit 2024; Revit 2027 builds need the .NET 10 SDK)
- An Anthropic API key

## Build and install

```powershell
# All supported versions (2024, 2025, 2026)
.\build.ps1

# One version
dotnet build src\SketchProMax\SketchProMax.csproj -c Debug -p:RevitVersion=2024
```

| Revit | Runtime | Output folder |
|---|---|---|
| 2024 | .NET Framework 4.8 | `bin\<config>\Revit2024\` |
| 2025 | .NET 8 | `bin\<config>\Revit2025\` |
| 2026 | .NET 8 | `bin\<config>\Revit2026\` |

The same source and the same `.addin` manifest serve every version; each build goes into that version's add-ins folder.

A Debug build on Windows copies itself into `%APPDATA%\Autodesk\Revit\Addins\<version>\` (manifest plus a `SketchProMax\` folder). For Release, copy `bin\Release\Revit<version>\` there yourself: `SketchProMax.addin` at the top level, everything else into `SketchProMax\`.

Start Revit, choose **Always Load** when prompted, then open the **Sketch ProMax** ribbon tab.

## API key

Either set `ANTHROPIC_API_KEY` in your user environment, or paste the key in **Settings** in the pane. Keys saved in Settings are encrypted with Windows DPAPI for your user account (`%APPDATA%\SketchProMax\settings.json`).

## Try it

- "What's in this model?"
- "Tag every untagged door and window in this view."
- "Run a QA check and tell me what to fix first."
- "Make a door schedule with Mark, Type, Width, Height and Fire Rating."
- "Create sheet A-101 'Ground Floor Plan' and put the Level 1 plan on it."
- "Look at the active sheet and tell me if any tags overlap."

## Project layout

```
src/SketchProMax/
  App.cs                    IExternalApplication: ribbon, dockable pane, dispatcher
  SketchProMax.addin        Revit manifest
  Commands/ShowPaneCommand  Toggle the pane
  Agent/AgentSession        Claude tool-use loop (Anthropic C# SDK)
  Agent/SystemPrompt        Agent instructions + firm standards
  Revit/RevitDispatcher     Runs work on Revit's main thread via ExternalEvent
  Revit/DependencyResolver  Revit 2024 only: loads the SDK's System.* dependencies from the add-in folder
  Tools/                    IRevitTool implementations and the registry
  UI/                       Chat pane (WPF) and settings dialog
  Settings/                 Per-user settings, DPAPI-encrypted key
```

## Adding a tool

1. Implement `IRevitTool` (name, description, JSON schema properties, `ModifiesModel`, `Execute`).
2. Wrap model changes in `RevitHelpers.StartTransaction(doc, "...")` and `Commit()`.
3. Return `ToolResult.Ok(new { ... })` with counts and element ids so Claude can verify its work.
4. Register it in `ToolRegistry.CreateDefault()`.

## Status

Starter code. It compiles against the Revit 2024, 2025 and 2026 API reference packages, but has not yet been run inside Revit. Expect to fix small runtime issues on first launch.
