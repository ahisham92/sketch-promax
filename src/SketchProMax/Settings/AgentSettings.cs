using System.IO;
using System.Text.Json;

namespace SketchProMax.Settings;

/// <summary>
/// Per-user settings stored in %APPDATA%\SketchProMax\settings.json, edited from the ribbon's Settings button.
/// The MCP server reads the same file, so firm standards reach Claude in the desktop app.
/// </summary>
public sealed class AgentSettings
{
    /// <summary>
    /// The old on/off switch, kept only so an older Sketch ProMax reading this file still behaves: written as true unless
    /// the level is "Ask only for risky changes". <see cref="ApprovalLevel"/> is what counts.
    /// </summary>
    public bool RequireApprovalForChanges { get; set; } = true;

    /// <summary>
    /// How often Revit asks before Claude changes something: risky_only (the default), once_per_task, every_change or
    /// always_allow.
    /// Empty in files written before the levels, which read as the default (Tools.ApprovalPolicy.Parse).
    /// </summary>
    public string? ApprovalLevel { get; set; }

    /// <summary>Tools the user said not to ask about again ("Don't ask again" in the dialog); cleared in Settings.</summary>
    public List<string>? NoAskTools { get; set; }

    /// <summary>The approval level as an enum.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Tools.ApprovalLevel Approvals
    {
        get => Tools.ApprovalPolicy.Parse(ApprovalLevel);
        set
        {
            ApprovalLevel = Tools.ApprovalPolicy.Store(value);
            RequireApprovalForChanges = value is not (Tools.ApprovalLevel.RiskyOnly or Tools.ApprovalLevel.AlwaysAllow);
        }
    }

    /// <summary>Firm standards in plain English (naming, sheet numbering, tag styles...), given to Claude.</summary>
    public string FirmStandards { get; set; } = "";

    /// <summary>Download new versions from GitHub when Revit starts; they take effect the next time Revit opens.</summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>
    /// Optional firm template or seed (.rvt/.rte) holding a legend. create_legend copies a legend from it when the model
    /// has none (Revit's API can't create a legend from nothing). Empty: not set.
    /// </summary>
    public string LegendSeedPath { get; set; } = "";

    /// <summary>Count working time (AI and by hand) and time saved, and keep them in each model (Revit/TimeLog).</summary>
    public bool TimeTracking { get; set; } = true;

    /// <summary>Minutes without activity after which working time stops counting.</summary>
    public double IdleMinutes { get; set; } = 5;

    /// <summary>By-hand rates the user set (minutes per run and per item); tools not listed use the built-in estimates.</summary>
    public List<Tools.TimeRate>? TimeRates { get; set; }

    /// <summary>
    /// Let an issue go ahead while the issue check still has open items (each issue that does says so, with the reason,
    /// in its issue record). Off: every item must be fixed or accepted first.
    /// </summary>
    public bool AllowIssueWithOpenItems { get; set; }

    /// <summary>How much the chat pane moves: full (the default), reduced or off (Chat.MotionLevels). Empty in older files: full.</summary>
    public string? ChatAnimations { get; set; }

    /// <summary>
    /// How many of a chat's parallel jobs run at once (each is a session on the user's plan): 3 by default, 1 to 6
    /// (Chat.JobBoard). More wait to start until one finishes.
    /// </summary>
    public int ChatJobLimit { get; set; } = 3;

    /// <summary>A job pauses itself after using this percent of the plan's 5-hour limit (Chat.JobBudget; 0: never).</summary>
    public int ChatJobBudget { get; set; } = 15;

    /// <summary>Which finished jobs a teammate checks before they are reported (Chat.JobReview: always, autopilot or off).</summary>
    public string ChatJobReview { get; set; } = "always";

    /// <summary>
    /// Auto sync (Revit.AutoSyncRule): when Claude's work has made <see cref="AutoSyncChanges"/> changes to a local copy of
    /// a central model since its last sync, every chat job pauses, the model is synchronized with central, and the work
    /// carries on where it stopped; a step bigger than that is split across syncs. On by default.
    /// </summary>
    public bool AutoSync { get; set; } = true;

    /// <summary>After how many changes auto sync synchronizes: 250 by default, 20 to 5000.</summary>
    public int AutoSyncChanges { get; set; } = 250;

    /// <summary>
    /// The names in the large chat's crew strip (Chat.CrewNames): you, the coordinator, the workers (comma separated, one
    /// per job at once) and the one who applies changes in Revit. Empty: the defaults (you: your Windows account's name).
    /// </summary>
    public string? ChatCrewYou { get; set; }
    public string? ChatCrewCoordinator { get; set; }
    public string? ChatCrewWorkers { get; set; }
    public string? ChatCrewApplier { get; set; }

    /// <summary>
    /// The type a new chat starts as (Chat.ChatTypes): "coordinator" (it hands work to parallel jobs, the default) or
    /// "simple" (one chat does everything itself). New chat's arrow picks the other type for one chat.
    /// </summary>
    public string ChatTypeDefault { get; set; } = "coordinator";

    /// <summary>
    /// The model and effort a new chat starts on (Chat.ChatAuto): "auto" (Automatic: picked per request and per job by
    /// the kind of work) unless set here; a choice in the chat's pickers sticks for that chat.
    /// </summary>
    public string ChatModelDefault { get; set; } = "auto";
    public string ChatEffortDefault { get; set; } = "auto";

    /// <summary>What Automatic gives each kind of work (Chat.ChatAuto): simple, everyday and hard; model aliases and effort levels.</summary>
    public string ChatModelSimple { get; set; } = "haiku";
    public string ChatModelEveryday { get; set; } = "sonnet";
    public string ChatModelHard { get; set; } = "opus";
    public string ChatEffortSimple { get; set; } = "low";
    public string ChatEffortEveryday { get; set; } = "medium";
    public string ChatEffortHard { get; set; } = "high";

    /// <summary>The coordinator's own turns in a chat With coordinator (ChatAuto.Coordinating): light, the jobs do the heavy work.</summary>
    public string ChatModelCoordinator { get; set; } = "sonnet";
    public string ChatEffortCoordinator { get; set; } = "low";

    /// <summary>An autopilot paused at a weekly limit carries on by itself when it resets (the 5-hour limit always does).</summary>
    public bool AutopilotResumeWeekly { get; set; } = true;

    /// <summary>Your name as it goes on a signed checklist (next to Chat).</summary>
    public string SignatureName { get; set; } = "";

    /// <summary>The sign-off role you sign as, e.g. "Trade BIM coordinator"; the checklist puts your signature there.</summary>
    public string SignatureRole { get; set; } = "";

    /// <summary>Your signature picture (PNG or JPEG), kept as a copy in %APPDATA%\SketchProMax. Empty: none.</summary>
    public string SignatureImage { get; set; } = "";

    /// <summary>The office's pre-submission checklist (.xlsx) the checklist window reads. Empty: asked the first time.</summary>
    public string ChecklistPath { get; set; } = "";

    /// <summary>
    /// Who helps with a checklist check the model test can't do (Ask the helper in the checklist's details): "promax"
    /// sends it to the Sketch ProMax chat, "sketchpro" copies the question to paste into SketchPro's chat, "none" hides
    /// the button.
    /// </summary>
    public string ChecklistHelper { get; set; } = "promax";

    /// <summary>
    /// Keep a change file for each model (Revit/ChangeFile): what changed since the last save, cleared at every save, to
    /// play onto the model again if it is lost or damaged (Recover > Replay a change file).
    /// </summary>
    public bool ChangeFile { get; set; } = true;

    /// <summary>
    /// The size a model should stay under, in MB (Storage & cleanup shows the model against it; a model can still pass it):
    /// 200 by default, 10 to 100000 (Tools.ModelStorageMath).
    /// </summary>
    public int ModelSizeLimitMb { get; set; } = 200;

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax", "settings.json");

    public static AgentSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AgentSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
