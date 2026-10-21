using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using SketchProMax.Settings;
using SketchProMax.UI;

namespace SketchProMax.Chat;

/// <summary>
/// Each chat's model and effort (ChatAuto), shown under its name as two pickers that start on Automatic (the Settings
/// default): Automatic picks per request (and per job) by the kind of work and says in the chat what it picked when that
/// changes; a model or effort picked by hand sticks for the chat and its new jobs until it is set back to Automatic.
/// And each chat's kind (ChatTypes): With coordinator (it hands work to parallel jobs) or Simple chat (it does everything
/// itself). The kind shows under the chat's name as a small pill that switches it (while none of its jobs is open), New
/// chat starts one of the Settings' default kind, and the arrow beside New chat picks the other kind for one chat.
/// </summary>
internal sealed partial class ChatPane
{
    private readonly Popup _choiceMenu = new() { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom };
    private readonly StackPanel _choiceList = new() { Margin = new Thickness(ChatUi.S1) };
    private readonly TextBlock _typeLabel = new() { FontSize = ChatUi.SmallSize, VerticalAlignment = VerticalAlignment.Center };
    private Button _typePill = null!, _newKind = null!;
    private DateTime _choiceClosed;
    private readonly TextBlock _modelPickLabel = new() { FontSize = ChatUi.SmallSize, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _effortPickLabel = new() { FontSize = ChatUi.SmallSize, VerticalAlignment = VerticalAlignment.Center };
    private Button _modelPick = null!, _effortPick = null!;
    /// <summary>What Automatic picked for the main chat's latest request (null before one, or after a chat switch).</summary>
    private AutoPick? _lastPick;
    /// <summary>Models the plan refused in this Revit session: Automatic doesn't pick them again.</summary>
    private readonly HashSet<string> _refusedModels = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The default kind, model and effort of a new chat (Settings, Chat tab), kept where ChatStore.New reads them.</summary>
    private static string DefaultType()
    {
        var settings = AgentSettings.Load();
        var type = ChatTypes.Parse(settings.ChatTypeDefault);
        ChatStore.NewType = type;
        ChatStore.NewModel = ChatAuto.ParseModel(settings.ChatModelDefault);
        ChatStore.NewEffort = ChatAuto.ParseEffort(settings.ChatEffortDefault);
        return type;
    }

    /// <summary>The model choices: Automatic first, then the models.</summary>
    private static IEnumerable<(string Id, string Label, string Hint)> ModelChoices() =>
        new[] { (ChatAuto.Automatic, "Automatic", "Haiku, Sonnet or Opus per request and per job, by the kind of work (Settings > Chat)") }
            .Concat(ChatModels.All.Select(c => (c.Id, c.Label, c.Hint)));

    private static IEnumerable<(string Id, string Label, string Hint)> EffortChoices() =>
        new[] { ChatAuto.Automatic }.Concat(ChatAuto.Efforts).Select(e => (e, ChatAuto.EffortLabel(e), ChatAuto.EffortHint(e)));

    /// <summary>
    /// Picks the main chat's model and effort for a request (the chat's own, or Automatic's by its kind of work) and,
    /// when Automatic picked something other than last time, says so in the chat.
    /// </summary>
    private void PickFor(string text, bool documents)
    {
        var pick = ChatJobs.Pick(text, _conv.Model, _conv.Effort, null, documents, _refusedModels);
        _model = pick.Model;
        _effort = pick.Effort;
        var changed = _lastPick is null || _lastPick.Model != pick.Model || _lastPick.Effort != pick.Effort;
        _lastPick = pick;
        if (changed && (pick.ModelAuto || pick.EffortAuto) && !_sample) Note(ChatAuto.Said(pick));
        ShowModelChoice();
    }

    /// <summary>The kind pill (under the chat's name) and the arrow beside New chat, with the menu they share.</summary>
    private void BuildTypeControls()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_typeLabel);
        var chevron = ChatUi.Icon(ChatUi.Paths.ChevronDown, 12);
        chevron.Margin = new Thickness(2, 0, 0, 0);
        row.Children.Add(chevron);
        _typePill = new Button { Content = row, Padding = new Thickness(ChatUi.S1, 0, ChatUi.S1, 0), Margin = new Thickness(-ChatUi.S1, 0, ChatUi.S1, 0), VerticalAlignment = VerticalAlignment.Center };
        _typePill.SetResourceReference(StyleProperty, "SpmChatGhost");
        _typePill.Click += (_, _) => ToggleChoice(_typePill, TypeChoices());
        AutomationProperties.SetItemStatus(_typePill, "Collapsed");

        // The model and effort pickers.
        _modelPick = PickerPill(_modelPickLabel, () => ToggleChoice(_modelPick, new Choice("Model for this chat",
            ModelChoices().Select(c => (c.Label, c.Hint, c.Id == ChatAuto.ParseModel(_conv.Model), (Action)(() => SetChatModel(c.Id)))).ToList())));
        _effortPick = PickerPill(_effortPickLabel, () => ToggleChoice(_effortPick, new Choice("Effort for this chat",
            EffortChoices().Select(c => (c.Label, c.Hint, c.Id == ChatAuto.ParseEffort(_conv.Effort), (Action)(() => SetChatEffort(c.Id)))).ToList())));

        _newKind = ChatUi.IconButton(ChatUi.Paths.ChevronDown, "New chat options", "Start a new chat With coordinator or a Simple chat.");
        _newKind.Width = 20;
        _newKind.Click += (_, _) => ToggleChoice(_newKind, NewChoices());
        AutomationProperties.SetItemStatus(_newKind, "Collapsed");

        var card = ChatUi.Elevated(_choiceList, out _, ChatUi.Radius);
        card.Margin = new Thickness(ChatUi.S3);
        card.MinWidth = 240;
        card.MaxWidth = 320;
        _choiceMenu.Child = card;
        _choiceMenu.Closed += (_, _) =>
        {
            _choiceClosed = DateTime.UtcNow;
            AutomationProperties.SetItemStatus(_typePill, "Collapsed");
            AutomationProperties.SetItemStatus(_newKind, "Collapsed");
        };
    }

    private Button PickerPill(TextBlock label, Action click)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(label);
        var chevron = ChatUi.Icon(ChatUi.Paths.ChevronDown, 12);
        chevron.Margin = new Thickness(2, 0, 0, 0);
        row.Children.Add(chevron);
        var pill = new Button { Content = row, Padding = new Thickness(ChatUi.S1, 0, ChatUi.S1, 0), Margin = new Thickness(-ChatUi.S1, 0, ChatUi.S2, 0), VerticalAlignment = VerticalAlignment.Center };
        pill.SetResourceReference(StyleProperty, "SpmChatGhost");
        pill.Click += (_, _) => click();
        AutomationProperties.SetItemStatus(pill, "Collapsed");
        return pill;
    }

    /// <summary>The model and effort pickers say the chat's choice; on Automatic, what it picked last.</summary>
    private void ShowPickers()
    {
        if (_modelPick is null) return;
        var model = ChatAuto.ParseModel(_conv.Model);
        var effort = ChatAuto.ParseEffort(_conv.Effort);
        var modelAuto = ChatAuto.IsAuto(model);
        var effortAuto = ChatAuto.IsAuto(effort);
        _modelPickLabel.Text = "Model: " + (modelAuto ? "Automatic" + (_lastPick is { } p ? " (" + ChatModels.Find(p.Model).Label + ")" : "") : ChatModels.Find(model).Label);
        _effortPickLabel.Text = "Effort: " + (effortAuto ? "Automatic" + (_lastPick is { } q ? " (" + ChatAuto.EffortLabel(q.Effort).ToLowerInvariant() + ")" : "") : ChatAuto.EffortLabel(effort));
        Brand.Fg(_modelPickLabel, Brand.K.Muted);
        Brand.Fg(_effortPickLabel, Brand.K.Muted);
        _modelPick.ToolTip = modelAuto
            ? "Automatic: each request and each job gets Haiku, Sonnet or Opus by the kind of work (Settings > Chat). Click to pick one for this chat."
            : $"{ChatModels.Find(model).Label} for this chat and its new jobs, until you set it back to Automatic. Click to change.";
        _effortPick.ToolTip = effortAuto
            ? "Automatic: low effort for simple work, higher for hard reasoning (Settings > Chat). Click to pick one for this chat."
            : $"{ChatAuto.EffortLabel(effort)} effort for this chat and its new jobs, until you set it back to Automatic. Click to change.";
        AutomationProperties.SetName(_modelPick, "Model for this chat: " + (modelAuto ? "Automatic" : ChatModels.Find(model).Label));
        AutomationProperties.SetHelpText(_modelPick, (string)_modelPick.ToolTip);
        AutomationProperties.SetName(_effortPick, "Effort for this chat: " + (effortAuto ? "Automatic" : ChatAuto.EffortLabel(effort)));
        AutomationProperties.SetHelpText(_effortPick, (string)_effortPick.ToolTip);
        ChatJobs.ChatModel = model;
        ChatJobs.ChatEffort = effort;
    }

    /// <summary>The chat's model: a model sticks for this chat and its new jobs; Automatic picks per request again.</summary>
    private void SetChatModel(string id)
    {
        id = ChatAuto.ParseModel(id);
        if (id == ChatAuto.ParseModel(_conv.Model)) return;
        _conv.Model = id;
        if (!ChatAuto.IsAuto(id)) _model = id;
        _lastPick = null;
        SaveSoon();
        ShowModelChoice();
        Status(ChatAuto.IsAuto(id) ? "Model: Automatic. Each request and new job gets one by the kind of work."
            : $"Model: {ChatModels.Find(id).Label} for this chat and its new jobs, until you set it back to Automatic.", spin: false);
    }

    /// <summary>The chat's effort: a level sticks for this chat and its new jobs; Automatic picks per request again.</summary>
    private void SetChatEffort(string level)
    {
        level = ChatAuto.ParseEffort(level);
        if (level == ChatAuto.ParseEffort(_conv.Effort)) return;
        _conv.Effort = level;
        if (!ChatAuto.IsAuto(level)) _effort = level;
        _lastPick = null;
        SaveSoon();
        ShowModelChoice();
        Status((ChatAuto.IsAuto(level) ? "Effort: Automatic. Low for simple work, higher for hard reasoning."
            : $"Effort: {ChatAuto.EffortLabel(level)} for this chat and its new jobs, until you set it back to Automatic.")
            + (ClaudeCli.EffortRefused ? " This Claude Code doesn't take an effort setting; update it (claude update) to use it." : ""), spin: false);
    }

    /// <summary>The kind pill says the chat's kind; its tooltip says what that means and how to switch.</summary>
    private void ShowType()
    {
        var type = ChatTypes.Of(_conv);
        _typeLabel.Text = ChatTypes.Label(type);
        Brand.Fg(_typeLabel, Brand.K.Muted);
        _typePill.ToolTip = $"This chat: {ChatTypes.Label(type)}. {ChatTypes.Hint(type)}. Click to switch (while no job is open).";
        AutomationProperties.SetName(_typePill, "Chat type: " + ChatTypes.Label(type));
        AutomationProperties.SetHelpText(_typePill, (string)_typePill.ToolTip);
    }

    private sealed record Choice(string Heading, List<(string Label, string Hint, bool Chosen, Action Pick)> Rows);

    private Choice TypeChoices()
    {
        var current = ChatTypes.Of(_conv);
        return new Choice("This chat's type", ChatTypes.All.Select(t => (ChatTypes.Label(t), ChatTypes.Hint(t), t == current, (Action)(() => SetChatType(t)))).ToList());
    }

    private Choice NewChoices()
    {
        var standard = DefaultType();
        return new Choice("Start a new chat", ChatTypes.All.Select(t => (
            t == ChatTypes.Simple ? "New simple chat" : "New chat with coordinator",
            ChatTypes.Hint(t) + (t == standard ? " (your default, Settings > Chat)" : ""),
            false, (Action)(() => NewChat(t)))).ToList());
    }

    /// <summary>Opens (or closes) the small menu under <paramref name="anchor"/> with <paramref name="choice"/>'s rows.</summary>
    private void ToggleChoice(Button anchor, Choice choice)
    {
        if (_choiceMenu.IsOpen)
        {
            _choiceMenu.IsOpen = false;
            anchor.Focus();
            return;
        }
        // A click on the button while the menu is open first closes it (it closes on any click outside): don't reopen it.
        if (DateTime.UtcNow - _choiceClosed < TimeSpan.FromMilliseconds(250)) return;
        _choiceList.Children.Clear();
        var heading = Brand.Text(choice.Heading, Brand.K.Muted, ChatUi.SmallSize, bold: true);
        heading.Margin = new Thickness(10, 6, 10, 4);
        _choiceList.Children.Add(heading);
        Button? first = null;
        foreach (var (label, hint, chosen, pick) in choice.Rows)
        {
            var text = new StackPanel();
            text.Children.Add(Brand.Text(label, Brand.K.Text, ChatUi.BodySize, bold: chosen));
            var line = Brand.Text(hint, Brand.K.Muted, ChatUi.SmallSize);
            line.Margin = new Thickness(0, 1, 0, 0);
            text.Children.Add(line);
            var row = new DockPanel();
            var check = ChatUi.Glyph(ChatUi.GlyphCheck, Brand.K.Accent, 12);
            check.Margin = new Thickness(ChatUi.S2, 0, 0, 0);
            check.Visibility = chosen ? Visibility.Visible : Visibility.Hidden;
            DockPanel.SetDock(check, Dock.Right);
            row.Children.Add(check);
            row.Children.Add(text);
            var item = new Button { Content = row, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            item.SetResourceReference(StyleProperty, "SpmChatMenuItem");
            AutomationProperties.SetName(item, label + (chosen ? ", chosen" : ""));
            AutomationProperties.SetHelpText(item, hint);
            item.Click += (_, _) =>
            {
                _choiceMenu.IsOpen = false;
                anchor.Focus();
                pick();
            };
            _choiceList.Children.Add(item);
            if (chosen || first is null) first = chosen ? item : first ?? item;
        }
        _choiceMenu.PlacementTarget = anchor;
        _choiceMenu.IsOpen = true;
        AutomationProperties.SetItemStatus(anchor, "Expanded");
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => first?.Focus()));
    }

    /// <summary>
    /// Switches the shown chat's kind, unless one of its jobs is open or an answer is running (the status line says
    /// why). The next message starts the chat's Claude Code again with the new kind's tools and instructions; the
    /// conversation itself carries on.
    /// </summary>
    private void SetChatType(string type)
    {
        type = ChatTypes.Parse(type);
        if (type == ChatTypes.Of(_conv) || _stopped) return;
        if (ChatTypes.WhyNotSwitch(JobsOpen, _turn is not null) is { } why)
        {
            Status(why, spin: false);
            return;
        }
        _conv.Type = type;
        ChatJobs.Coordinator = type == ChatTypes.Coordinator;
        // The process was started for the other kind: the next message starts one for this kind (the same session).
        if (_engine is { } engine && engine.Coordinator != (type == ChatTypes.Coordinator))
        {
            engine.Dispose();
            _engine = null;
        }
        Note(type == ChatTypes.Simple
            ? "This chat is now a simple chat: Sketch ProMax does all the work here itself, without jobs."
            : "This chat now works with a coordinator: it can hand separate parts of a request to jobs that work side by side.");
        Save();
        ShowTitle();
        ShowSwitcher();
        if (_viewJob is { } shown) OpenJobView(shown);
        else Status($"Chat type: {ChatTypes.Label(type)}.", spin: false);
    }
}
