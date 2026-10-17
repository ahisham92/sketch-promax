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
