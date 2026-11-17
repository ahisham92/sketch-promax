using System.IO;
using Autodesk.Revit.UI;
using SketchProMax.Commands;
using SketchProMax.Tools;
using SketchProMax.UI;
using SketchProMax.Update;

namespace SketchProMax.Sharing;

/// <summary>
/// The ribbon side of shared tools. In Sketch ProMax: the Share tools button (<see cref="Share"/>). On the TritoRevit
/// tab, in either add-in: a shared tool's button ("shared \t pack \t tool", <see cref="Run"/>) and the Shared tools
/// button (<see cref="Manage"/>). A shared tool runs exactly as the same button does on the Sketch ProMax tab: a
/// ribbon tool through its options window, a window as itself, a learned tool or recipe as on My tools.
/// </summary>
internal static class SharedTools
{
    /// <summary>What dialogs are titled: the add-in the user sees the button in.</summary>
    public static string Product => App.Shared ? "TritoRevit" : "Sketch ProMax";

    public static Result Run(UIApplication app, string packId, string toolId)
    {
        var pack = SharedPacks.Find(packId);
        var tool = pack?.Tools.FirstOrDefault(t => t.Id == toolId);
        if (pack is null || tool is null)
        {
            TaskDialog.Show(Product, "This shared tool is no longer installed. The TritoRevit tab is tidied within a few seconds.");
            return Result.Cancelled;
        }
        // A pack the installer just put here (or that updated a moment ago) has its learned tools and checklist put in place first.
        try { SharedPacks.Apply(pack.Id); } catch (Exception) { /* the tool may still run with what is there */ }
        if (Version.TryParse(pack.MinVersion, out var needs) && Updater.Parse(pack.MinVersion) > Updater.Current && tool.Kind == "command")
        {
            TaskDialog.Show(Product, $"{tool.Title} was shared from a newer version ({needs.ToString(3)}; this is {Updater.CurrentText}). " +
                                     (App.Shared ? "TritoRevit updates itself while Revit is open; try again in a few minutes." : "Update Sketch ProMax from its Update button, then try again."));
            if (App.Shared) _ = Task.Run(async () => { try { await TritoRuntime.CheckAsync(CancellationToken.None).ConfigureAwait(false); } catch (Exception) { } });
            return Result.Cancelled;
        }
        var command = SharedPackMath.CommandFor(tool);
        if (command is null || (tool.Kind == "command" && !SharedPackMath.Shareable(command)))
        {
            TaskDialog.Show(Product, $"{tool.Title} can't run here.");
            return Result.Cancelled;
        }
        return CoreEntry.Command(command, app) switch { 0 => Result.Succeeded, 1 => Result.Cancelled, _ => Result.Failed };
    }

    /// <summary>The Shared tools button: the installed packs, check for updates, add a pack from a zip, remove one.</summary>
    public static Result Manage(UIApplication app)
    {
        new SharedToolsWindow(app).ShowDialog();
        return Result.Succeeded;
    }

    /// <summary>
    /// The TritoRevit tab's Update button: checks right away, in the update window, and either says everything is up to
    /// date or updates (the add-in from its releases, the packs from their update folders), with no restart. With
    /// Sketch ProMax running the tab, its packs are checked and then Sketch ProMax's own Update runs.
    /// </summary>
    public static Result CheckForUpdates(UIApplication? app)
    {
        if (!App.Shared && app is not null)
        {
            _ = Task.Run(() => { try { SharedPacks.Sync(); } catch (Exception) { } });
            return CoreEntry.Command("update-button", app) switch { 0 => Result.Succeeded, 1 => Result.Cancelled, _ => Result.Failed };
        }
        if (UpdateProgress.Current is { } running && !running.Snapshot.IsFinal && running.Snapshot.Stage != UpdateStage.Idle)
        {
            UpdateWindow.ShowFor(running);
            return Result.Succeeded;
        }
        var progress = new UpdateProgress { Interactive = true };
        UpdateProgress.Current = progress;
        progress.Surfaced = true;
        progress.Report(UpdateStage.Checking, "Looking for updated shared tools…");
        UpdateWindow.ShowFor(progress);
        _ = Task.Run(() => CheckNowAsync(progress));
        return Result.Succeeded;
    }

    private static async Task CheckNowAsync(UpdateProgress progress)
    {
        try
        {
            var packs = SharedPacks.Sync();
            var version = await TritoRuntime.CheckAsync(progress.Cancellation.Token, progress).ConfigureAwait(false);
            var count = SharedPacks.List().Count;
            var packText = packs.Count > 0 ? $" Updated: {string.Join(", ", packs)}."
                : count == 0 ? "" : count == 1 ? " The shared pack is up to date too." : $" All {count} shared packs are up to date too.";
            if (version is not null)
                progress.Report(UpdateStage.Updated, null, version); // TritoRevit switches to it within seconds, with Revit open
            else if (packs.Count > 0)
                progress.Report(UpdateStage.UpToDate, $"TritoRevit {Updater.CurrentText} is up to date.{packText}");
            else
                progress.Report(UpdateStage.UpToDate, $"Everything is up to date: TritoRevit {Updater.CurrentText}.{packText}");
        }
        catch (OperationCanceledException) { progress.Report(UpdateStage.Cancelled); }
        catch (Exception ex) { progress.Report(UpdateStage.Failed, "Couldn't check: " + ex.Message); }
    }

    /// <summary>Sketch ProMax's Share tools button: pick tools, make the zip, publish it to the update folder.</summary>
    public static Result Share(UIApplication app)
    {
        if (App.Shared) return Result.Cancelled;
        if (!OwnerPc())
        {
            TaskDialog.Show(Product, "Only the owner of Sketch ProMax can share tools. Ask them for the tools you need.");
            return Result.Cancelled;
        }
        var window = new ShareWindow(Catalog());
        window.ShowDialog();
        return window.Made ? Result.Succeeded : Result.Cancelled;
    }

    /// <summary>One shareable item: its Favourites-style key, group, title and tooltip.</summary>
    internal sealed record Item(string Key, string Group, string Title, string Tip);

    /// <summary>
    /// Everything that can be shared, in groups: the windows (pre-submission checklist first), the ribbon tools this
    /// core has that run without the AI, then the learned tools and recipes on this PC.
    /// </summary>
    internal static List<Item> Catalog()
    {
        var items = new List<Item>();
        foreach (var (command, title, _, tip) in SharedPackMath.Commands)
            items.Add(new Item("command:" + command, "Windows", title, tip));
        var registry = App.Bridge?.Registry ?? ToolRegistry.CreateDefault();
        foreach (var (tool, text, _, tip) in CoreRibbonManifest.Tools())
        {
            if (!SharedPackMath.Shareable("tool:" + tool) || !registry.IsBuiltIn(tool) || registry.Find(tool) is null) continue;
            if (items.Any(i => i.Key == "tool:" + tool)) continue;
            items.Add(new Item("tool:" + tool, "Ribbon tools", text, tip));
        }
        foreach (var line in QuickTools.List().Split('\n'))
        {
            var f = line.TrimEnd('\r').Split('\t');
            if (f.Length < 3 || f[1].Length == 0) continue;
            if (f[0] == "learned") items.Add(new Item("learned:" + f[1], "Learned tools", SharedPackMath.Pretty(f[1]), f[2]));
            else if (f[0] == "recipe") items.Add(new Item("recipe:" + f[1], "Recipes", SharedPackMath.Pretty(f[1]), f[2]));
        }
        return items;
    }

    /// <summary>A ribbon tool's title, icon and tooltip, as its Sketch ProMax button shows them.</summary>
    internal static (string Title, string Glyph, string Tip)? RibbonTool(string name)
    {
        foreach (var (tool, text, glyph, tip) in CoreRibbonManifest.Tools())
            if (tool == name) return (text, glyph, tip);
        return null;
    }

    /// <summary>Publishes the packs this PC shared again when a tool in them changed (background, every few minutes).</summary>
    public static void AutoPublish()
    {
        if (App.Shared || !OwnerPc()) return;
        if (!Directory.Exists(SharedPacks.RecordsFolder)) return;
        var published = SharedPacks.AutoPublish(RibbonTool, Updater.CurrentText);
        foreach (var line in published) ActivityLogNote(line);
    }

    /// <summary>Sharing tools (Share tools, and publishing a pack's next revision) is for the owner's PC only (the owner's rule, 2026-10-25).</summary>
    private static bool OwnerPc()
    {
        try { return Licensing.LicenseState.IsOwnerPc; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
    }

    private static void ActivityLogNote(string line)
    {
        try { ActivityLog.Note("shared_tools", "Published " + line); } catch (Exception) { }
    }
}
