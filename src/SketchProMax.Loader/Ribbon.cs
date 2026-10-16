using System.Windows.Media;
using Autodesk.Revit.UI;

namespace SketchProMax.Loader;

/// <summary>
/// The Sketch ProMax tab. Built once at startup by the loader, so it survives switching to a new core; its live parts
/// (Claude connection, running job, update state, quick tools list) are refreshed from the running core every 2 s.
/// Every button is a large (32 px icon) button: Revit's stacked small buttons are about 22 px tall, under the 24 px
/// minimum click target. Destructive buttons (Cancel job) use the red danger tile.
/// </summary>
internal static class Ribbon
{
    public const string Tab = "Sketch ProMax";
    public const string HelpUrl = "https://github.com/ahisham92/sketch-promax#readme";
    public const int Slots = 20;

    private static PushButton? _status, _job, _cancel, _version, _pane;
    private static PulldownButton? _quick;
    private static readonly PushButton?[] SlotButtons = new PushButton?[Slots];
    private static readonly string?[] SlotKeys = new string?[Slots];
    private static readonly Dictionary<RibbonItem, string> Shown = new();
    private static int _ticks;

    private static string Assembly => typeof(Ribbon).Assembly.Location;

    private static PushButtonData Button<T>(string id, string text, string glyph, string tip, string? more = null, bool needsModel = false)
        where T : ForwardCommand => Button<T>(id, text, Icons.Get(glyph, 16), Icons.Get(glyph, 32), tip, more, needsModel);

    private static PushButtonData Button<T>(string id, string text, ImageSource small, ImageSource large, string tip, string? more, bool needsModel)
        where T : ForwardCommand
    {
        var data = new PushButtonData("SketchProMax." + id, text, Assembly, typeof(T).FullName)
        {
            Image = small,
            LargeImage = large,
            ToolTip = tip,
            AvailabilityClassName = needsModel ? typeof(ModelAvailable).FullName : typeof(AlwaysAvailable).FullName,
        };
        if (more is not null) data.LongDescription = more;
        data.SetContextualHelp(new ContextualHelp(ContextualHelpType.Url, HelpUrl));
        return data;
    }

    public static void Build(UIControlledApplication app)
    {
        try { app.CreateRibbonTab(Tab); }
        catch (Autodesk.Revit.Exceptions.ArgumentException) { /* already there */ }

        // Claude: is the desktop app connected to this Revit window, and how to start.
        var claude = app.CreateRibbonPanel(Tab, "Claude");
        _status = (PushButton)claude.AddItem(Button<StatusCommand>("Status", "Claude\nstarting", Icons.Logo(16, Icons.Gray), Icons.Logo(32, Icons.Gray),
            "Whether the Claude desktop app is connected to this Revit window.",
            "Green: Claude has talked to this Revit window in the last few minutes. Amber: ready, waiting for Claude. " +
            "Gray: the Claude desktop app is not running or can't reach Revit.\n\nClick for details, the models Claude can see, " +
            "and how to fix a missing connection.", false));
        claude.AddItem(Button<CopyPromptCommand>("CopyPrompt", "Copy\nprompt", "copy",
            "Copies a good first message for Claude to the clipboard. Paste it into a new chat in the Claude desktop app.",
            "The prompt names the open model and asks Claude to look at it and suggest what it can do for you."));
        claude.AddItem(Button<OpenClaudeCommand>("OpenClaude", "Open\nClaude", "open",
            "Starts the Claude desktop app, or brings it to the front.",
            "Works with the normal installer and the Microsoft Store version."));
        _pane = claude.AddItem(Button<PaneCommand>("Pane", "Show\npane", "pane",
            "Shows or hides the Sketch ProMax pane: connection, the running job with its steps and a Cancel button, " +
            "the last result with clickable element ids, and your quick tools.",
            "Click an element id in the pane to select it and zoom to it. The pane docks like the Properties palette; " +
            "drag its tab to move it.")) as PushButton;

        // Jobs: what Revit is doing for Claude right now.
        var jobs = app.CreateRibbonPanel(Tab, "Jobs");
        _job = (PushButton)jobs.AddItem(Button<JobCommand>("Job", "No job", "job",
            "The tool Claude is running in Revit, how long it has run and its progress.",
            "Revit runs one job at a time. While a long job runs, Claude waits for it (wait_for_job) and other requests are " +
            "told what is running. Click for details of the running or the last job.", false));
        _cancel = (PushButton)jobs.AddItem(Button<CancelJobCommand>("CancelJob", "Cancel\njob", "cancel",
            "Stops the running job (or a running self-test) at its next check, like cancel_job in the chat.",
            "Batch tools stop between items and keep what they finished as one Undo step. A single Revit operation that has " +
            "already started (a regeneration, a big join) always runs to its end."));
        _cancel.Enabled = false;

        // Quick tools: saved recipes and learned tools, run straight in Revit without AI or internet.
        var quick = app.CreateRibbonPanel(Tab, "Quick tools");
        var pulldown = new PulldownButtonData("SketchProMax.Quick", "Quick\ntools")
        {
            Image = Icons.Get("bolt", 16),
            LargeImage = Icons.Get("bolt", 32),
            ToolTip = "Your saved recipes and learned tools. They run right here in Revit: no AI, no internet, no chat needed.",
            LongDescription = "Ask Claude to \"save that as a recipe\" after a workflow you liked, or to \"save it as a tool\" when it " +
                              "wrote code that worked. They appear here. Blanks such as {level} are asked for when you run one.",
        };
        pulldown.SetContextualHelp(new ContextualHelp(ContextualHelpType.Url, HelpUrl));
        _quick = (PulldownButton)quick.AddItem(pulldown);
        var slotTypes = new[]
        {
            typeof(QuickSlot00), typeof(QuickSlot01), typeof(QuickSlot02), typeof(QuickSlot03), typeof(QuickSlot04),
            typeof(QuickSlot05), typeof(QuickSlot06), typeof(QuickSlot07), typeof(QuickSlot08), typeof(QuickSlot09),
            typeof(QuickSlot10), typeof(QuickSlot11), typeof(QuickSlot12), typeof(QuickSlot13), typeof(QuickSlot14),
            typeof(QuickSlot15), typeof(QuickSlot16), typeof(QuickSlot17), typeof(QuickSlot18), typeof(QuickSlot19),
        };
        for (var i = 0; i < Slots; i++)
        {
            var data = new PushButtonData($"SketchProMax.Quick{i:00}", i == 0 ? "No recipes or learned tools yet" : $"Slot {i + 1}", Assembly, slotTypes[i].FullName)
            {
                Image = Icons.Get("recipe", 16),
                LargeImage = Icons.Get("recipe", 32),
                ToolTip = "Ask Claude to save a workflow as a recipe, or code that worked as a tool, and it appears here.",
                AvailabilityClassName = typeof(ModelAvailable).FullName,
            };
            SlotButtons[i] = _quick.AddPushButton(data);
            if (i > 0) SlotButtons[i]!.Visible = false;
        }
        _quick.AddSeparator();
        _quick.AddPushButton(Button<QuickRefreshCommand>("QuickRefresh", "Refresh list", "refresh",
            "Reads the recipes and learned tools again (the list also refreshes by itself every half minute)."));
        _quick.AddPushButton(Button<QuickFolderCommand>("QuickFolder", "Open recipes folder", "folder",
            "Opens the folder with your recipes and learned tools. Copy files from a colleague into it to share them."));

        // Checks.
        var checks = app.CreateRibbonPanel(Tab, "Checks");
        checks.AddItem(Button<ModelCheckCommand>("ModelCheck", "Model\ncheck", "check",
            "Checks the open model and scores it: Revit warnings, duplicates, wall gaps, unsupported beams, columns off grid, " +
            "off-axis elements, wrong levels, rooms, empty required parameters.",
            "Read-only: nothing in the model changes. Runs here without the AI; the findings are listed with a CSV of every item.",
            needsModel: true));
        checks.AddItem(Button<SelfTestCommand>("SelfTest", "Self-test", "test",
            "Runs every Sketch ProMax tool once on the open model and reports what works.",
            "The quick check is read-only. The full check also runs the tools that change the model and undoes each change " +
            "straight after it; use it on a copy of a model. Click again while it runs to see progress or stop it."));
        checks.AddItem(Button<FieldTestCommand>("FieldTest", "Field\ntest", "field",
            "Builds a throwaway test project, runs every Sketch ProMax tool family on it one tool at a time and writes one report of what works and what to fix.",
            "Your open models are not changed: the test project opens in its own window and is closed without saving, and Revit " +
            "switches back to your model at the end. Save your models first. Takes about 5 to 15 minutes; Revit stays responsive " +
            "between tools. Click again while it runs to see progress or stop it after the current tool.",
            needsModel: true));

        // Updates.
        var updates = app.CreateRibbonPanel(Tab, "Updates");
        _version = (PushButton)updates.AddItem(Button<UpdateInfoCommand>("Version", "Version", "version",
            "The installed version and the state of updates.",
            "Updates download in the background and switch over while Revit stays open, as soon as no job is running. " +
            "Click to see the update window.", false));
        updates.AddItem(Button<UpdateCheckCommand>("UpdateCheck", "Check\nnow", "update",
            "Looks for a newer version right away and installs it without restarting Revit."));
        updates.AddItem(Button<WhatsNewCommand>("WhatsNew", "What's\nnew", "news",
            "Opens the release notes of the latest version."));

        // Settings and help.
        var more = app.CreateRibbonPanel(Tab, "Sketch ProMax");
        more.AddItem(Button<SettingsCommand>("Settings", "Settings", "settings",
            "Firm standards, whether Revit asks before Claude changes the model, and automatic updates."));
        more.AddItem(Button<HelpCommand>("Help", "Help", Icons.Logo(16), Icons.Logo(32),
            "How to use Sketch ProMax, what it can do, and what to try first.", null, false));
    }

    private static void Set(RibbonItem? item, string text, string? tip = null)
    {
        if (item is null) return;
        var key = text + "\u0001" + tip;
        if (Shown.TryGetValue(item, out var was) && was == key) return;
        Shown[item] = key;
        item.ItemText = text;
        if (tip is not null) item.ToolTip = tip;
    }

    private static readonly Dictionary<RibbonButton, string> Images = new();

    private static void Image(RibbonButton? button, string key, Func<int, ImageSource> make)
    {
        if (button is null) return;
        if (Images.TryGetValue(button, out var was) && was == key) return;
        Images[button] = key;
        button.Image = make(16);
        button.LargeImage = make(32);
    }

    /// <summary>Updates the live parts. Cheap: the core answers from memory.</summary>
    public static void Refresh()
    {
        if (++_ticks % 15 == 0) RefreshQuickTools();
        if (Host.Core is null)
        {
            var failed = Host.BootError is not null;
            Set(_status, failed ? "Claude\nnot started" : "Claude\nstarting", Host.BootError ?? "Sketch ProMax starts as soon as Revit is ready.");
            Image(_status, failed ? "red" : "gray", s => Icons.Logo(s, failed ? Icons.Red : Icons.Gray));
            return;
        }

        // "state \t text \t tooltip"
        var claude = Host.Query("claude").Split('\t');
        if (claude.Length >= 3)
        {
            Set(_status, "Claude\n" + claude[1], claude[2]);
            var color = claude[0] switch { "connected" => Icons.Green, "ready" => Icons.Amber, "error" => Icons.Red, _ => Icons.Gray };
            Image(_status, claude[0], s => Icons.Logo(s, color));
        }

        // "state \t text \t tooltip", state running / selftest / idle
        var job = Host.Query("job").Split('\t');
        if (job.Length >= 3)
        {
            var running = job[0] is "running" or "selftest";
            Set(_job, job[1], job[2]);
            Image(_job, running ? "run" : "idle", s => Icons.Get(running ? "job-running" : "job", s));
            if (_cancel is not null && _cancel.Enabled != running) _cancel.Enabled = running;
        }

        // "text \t tooltip \t glyph"
        var update = Host.Query("update").Split('\t');
        if (update.Length >= 3)
        {
            Set(_version, update[0], update[1]);
            Image(_version, update[2], s => Icons.Get(update[2], s));
        }
    }

    /// <summary>Fills the quick-tool slots from the running core's list of recipes and learned tools.</summary>
    public static void RefreshQuickTools(bool reload = false)
    {
        if (Host.Core is null || SlotButtons[0] is null) return;
        // One "kind \t name \t summary" line per item, recipes first.
        // An older core may not know "quick-reload"; it then answers empty and the cached list is asked for.
        var text = reload ? Host.Query("quick-reload") : "";
        if (text.Length == 0) text = Host.Query("quick");
        var items = text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('\t')).Where(p => p.Length >= 3).Take(Slots).ToList();
        for (var i = 0; i < Slots; i++)
        {
            var button = SlotButtons[i]!;
            if (i < items.Count)
            {
                var (kind, name, summary) = (items[i][0], items[i][1], items[i][2]);
                SlotKeys[i] = $"quick\t{kind}\t{name}";
                Set(button, Pretty(name), (kind == "recipe" ? "Recipe. " : "Learned tool. ") + summary);
                Image(button, kind, s => Icons.Get(kind == "recipe" ? "recipe" : "learned", s));
                if (!button.Visible) button.Visible = true;
                if (!button.Enabled) button.Enabled = true;
            }
            else
            {
                SlotKeys[i] = null;
                if (i == 0)
                {
                    Set(button, "No recipes or learned tools yet",
                        "Ask Claude to save a workflow as a recipe (\"save that as a recipe\"), or code that worked as a tool, and it appears here.");
                    Image(button, "recipe", s => Icons.Get("recipe", s));
                }
                else if (button.Visible) button.Visible = false;
            }
        }
    }

    /// <summary>Keeps the Pane button's text in step with the pane ("Show pane" / "Hide pane").</summary>
    public static void SetPaneShown(bool shown)
    {
        try { Set(_pane, shown ? "Hide\npane" : "Show\npane"); }
        catch (Exception) { /* cosmetic */ }
    }

    /// <summary>The core command for a slot, or null when the slot is empty.</summary>
    public static string? QuickKey(int slot) => slot >= 0 && slot < Slots ? SlotKeys[slot] : null;

    private static string Pretty(string name)
    {
        var text = name.Replace('_', ' ').Trim();
        return text.Length == 0 ? name : char.ToUpperInvariant(text[0]) + text.Substring(1);
    }
}
