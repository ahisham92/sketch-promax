using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using SketchProMax.UI;

namespace SketchProMax.Chat;

/// <summary>
/// The prompt enhancer: before a task that changes the model (or every message, per the setting), a planning run of
/// Claude Code reads the model with read-only tools and turns the message into a precise brief, shown as a card with
/// Run this, Edit and Run as I typed. Nothing reaches the chat (and so the model) until one is picked. An edited brief
/// is distilled into a "Brief preference" memory note, so later briefs need fewer edits.
/// </summary>
internal sealed partial class ChatPane
{
    private BriefMode _briefMode = PromptBriefs.LoadMode(ClaudeCli.Folder);
    private readonly Dictionary<ChatItem, Action> _briefCards = new();
    private static Task<string>? _lastRemember;

    /// <summary>Test hook (BriefTest): when set, a turn's text goes here instead of to Claude Code.</summary>
    private static Action<string>? DeliveryCapture;

    /// <summary>
    /// The brief the running task was started from ("" when it runs as typed, had no brief, or nothing runs).
    /// </summary>
    internal static string ActiveBrief => _instance?._turn?.Brief ?? "";

    // ---- Planning -------------------------------------------------------------------------------------------------

    /// <summary>Starts the planning run when the setting says so; false when the message should run as typed.</summary>
    private bool StartBrief(string text, List<Attachment> attachments)
    {
        if (_install is null || !PromptBriefs.ShouldPlan(_briefMode, text)) return false;
        var turn = _turn = new Turn { Planning = true, Cancel = new CancellationTokenSource() };
        _lastActivity = DateTime.UtcNow;
        ShowStatus("Reading the model to write a brief. Nothing changes yet…");
        // The end of the conversation (before this message), so a follow-up is briefed in its context.
        var recent = _conv.Items.Take(_conv.Items.Count - 1).Where(i => i.Kind is "user" or "assistant" && i.Text.Length > 0)
            .Reverse().Take(6).Reverse().Select(i => (i.Kind, i.Text)).ToList();
        var message = PromptBriefs.PlanMessage(text, attachments.Select(a => a.Name), recent);
        _ = PlanAndShow(turn, _install, text, message, _model, _briefMode, attachments);
        return true;
    }

    private async Task PlanAndShow(Turn turn, ClaudeCli.Install install, string text, string message, string model, BriefMode mode, List<Attachment> attachments)
    {
        var conv = _conv;
        (PromptBrief? Brief, string Error) result;
        try { result = await Task.Run(() => BriefRunner.Plan(install, text, message, model, turn.Cancel!.Token)); }
        catch (Exception ex) { result = (null, ex.Message); }
        if (_turn != turn) return; // stopped meanwhile
        turn.Cancel?.Dispose();
        EndTurn();
        if (result.Brief is { } plain && !PromptBriefs.ShowCard(mode, plain))
        {
            if (_conv == conv) await RunTurn(text, attachments, ""); // a question: it runs as typed
            return;
        }
        var item = new ChatItem
        {
            Kind = "brief", Files = attachments.Select(a => a.Path).ToList(),
            State = result.Brief is null ? "failed" : "pending",
        };
        var brief = result.Brief ?? new PromptBrief { Request = text, Prompt = text };
        item.Brief = PromptBriefs.ToJson(brief);
        item.Text = result.Brief is null ? result.Error : PromptBriefs.Compose(brief);
        if (_conv != conv)
        {
            // The chat was switched meanwhile (another model opened): keep the card in its own chat.
            item.Time = DateTime.UtcNow;
            conv.Items.Add(item);
            ChatStore.Save(conv);
            return;
        }
        Add(item);
        Save();
        if (_briefCards.ContainsKey(item)) Status(item.State == "failed" ? "No brief this time; choose below." : "Check the brief, then choose how to run it.", spin: false);
    }

    private void StopBrief(Turn turn)
    {
        turn.Cancel?.Cancel();
        EndTurn();
        Note("Stopped writing the brief. Nothing in the model changed. Send again, or turn briefs off in the model menu.");
    }

    /// <summary>A newer message replaces any brief still waiting for a choice.</summary>
    private void SupersedeBriefs()
    {
        foreach (var item in _conv.Items.Where(i => i.Kind == "brief" && i.State is "pending" or "failed"))
        {
            item.State = "stale";
            if (_briefCards.TryGetValue(item, out var render)) render();
        }
    }

    // ---- The card -------------------------------------------------------------------------------------------------

    /// <summary>The brief card: what was understood, one-tap questions, then Run this (primary), Edit and Run as I typed.</summary>
    private UIElement BriefCard(ChatItem item)
    {
        var holder = new ContentControl { Focusable = false, Margin = new Thickness(0, ChatUi.S3, 0, ChatUi.S1) };
        var brief = PromptBriefs.FromStored(item.Brief) ?? new PromptBrief { Request = item.Text, Prompt = item.Text };
        var editing = false;
        TextBox? editor = null;

        Button Pill(string text, bool primary, string help, Action click)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, ChatUi.S2, ChatUi.S1), ToolTip = help };
            b.SetResourceReference(StyleProperty, primary ? "SpmChatPrimary" : "SpmChatPill");
            AutomationProperties.SetName(b, text);
            AutomationProperties.SetHelpText(b, help);
            b.Click += (_, _) =>
            {
                try { click(); }
                catch (Exception ex) { Status($"That didn't work ({ex.Message}).", spin: false); } // never into Revit's dispatcher
            };
            return b;
        }

        void Render()
        {
            var panel = new StackPanel();
            var pending = item.State == "pending";
            var failed = item.State == "failed";

            var head = new DockPanel { Margin = new Thickness(0, 0, 0, ChatUi.S1) };
            var icon = ChatUi.Icon(ChatUi.Paths.Sparkle, 14);
            icon.Margin = new Thickness(0, 0, ChatUi.S2, 0);
            DockPanel.SetDock(icon, Dock.Left);
            head.Children.Add(icon);
            var kind = brief.Kind switch { "change" => "Changes the model", "multi_step" => "Several steps", "read_only" => "Reads only", _ => "May change the model" };
            var title = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            title.Inlines.Add(new Run("Brief") { FontWeight = FontWeights.SemiBold });
            if (!failed) title.Inlines.Add(new Run(" · " + kind) { FontSize = ChatUi.SmallSize });
            head.Children.Add(title);
            panel.Children.Add(head);

            var line = item.State switch
            {
                "pending" => "Nothing changes in the model until you choose.",
                "run" => "You ran this brief.",
                "edited" => "You ran your edited brief.",
                "typed" => "You ran your message as typed; this brief wasn't used.",
                "failed" => "No brief this time: " + item.Text.TrimEnd('.') + ". Nothing in the model changed.",
                _ => "Not run: a newer message replaced it.",
            };
            var sub = Brand.Text(line, Brand.K.Muted, ChatUi.SmallSize + 0.5);
            sub.Margin = new Thickness(0, 0, 0, ChatUi.S2);
            panel.Children.Add(sub);

            if (!failed && !editing)
            {
                var view = new MarkdownView();
                view.SetMarkdown(item.Text);
                view.Opacity = item.State is "typed" or "stale" ? 0.7 : 1;
                panel.Children.Add(view);
            }

            // One-tap questions: the suggested option is picked until another is tapped; the brief above updates.
            if (pending && !editing)
            {
                foreach (var q in brief.Questions)
                {
                    var ask = Brand.Text(q.Text, Brand.K.Text, ChatUi.SmallSize + 0.5, bold: true);
                    ask.Margin = new Thickness(0, ChatUi.S3, 0, ChatUi.S1);
                    panel.Children.Add(ask);
                    var options = new WrapPanel();
                    for (var k = 0; k < q.Options.Count; k++)
                    {
                        var picked = k == q.Effective;
                        var at = k;
                        var label = q.Options[k] + (k == q.Recommended && q.Chosen < 0 ? " (suggested)" : "");
                        var option = new Button { Content = Row(ChatUi.Glyph(picked ? ChatUi.GlyphCheck : "", Brand.K.Accent, 11), new TextBlock { Text = label }), Margin = new Thickness(0, 0, ChatUi.S1, ChatUi.S1) };
                        option.SetResourceReference(StyleProperty, "SpmChatPill");
                        if (picked)
                        {
                            option.SetResourceReference(Control.BackgroundProperty, Brand.K.AccentSoft);
                            option.SetResourceReference(Control.BorderBrushProperty, Brand.K.Accent);
                        }
                        var name = q.Text + ": " + q.Options[k];
                        AutomationProperties.SetName(option, name);
                        AutomationProperties.SetItemStatus(option, picked ? "Selected" : "");
                        option.ToolTip = picked ? "Picked. The brief above uses it." : "Use this answer in the brief.";
                        option.Click += (_, _) =>
                        {
                            if (item.State != "pending" || !PromptBriefs.Answer(brief, q.Id, at)) return;
                            item.Brief = PromptBriefs.ToJson(brief);
                            item.Text = PromptBriefs.Compose(brief);
                            Save();
                            Render();
                            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                                FindButton(holder, name)?.Focus()));
                        };
                        options.Children.Add(option);
                    }
                    panel.Children.Add(options);
                }
            }

            var row = new WrapPanel { Margin = new Thickness(0, ChatUi.S3, 0, 0) };
            if (editing)
            {
                editor = new TextBox
                {
                    Text = failed ? brief.Request : item.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 96, MaxHeight = 280,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    SpellCheck = { IsEnabled = true }, FontSize = ChatUi.BodySize, Padding = new Thickness(ChatUi.S2),
                };
                editor.SetResourceReference(StyleProperty, "SpmChatField");
                AutomationProperties.SetName(editor, "Edit the brief");
                AutomationProperties.SetHelpText(editor, "Change the brief, then Run edited brief (Ctrl+Enter). Escape cancels.");
                editor.PreviewKeyDown += (_, e) =>
                {
                    if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { e.Handled = true; RunEdited(); }
                    else if (e.Key == Key.Escape) { e.Handled = true; editing = false; Render(); }
                };
                panel.Children.Add(editor);
                row.Children.Add(Pill("Run edited brief", true, "Runs the brief as you edited it (Ctrl+Enter). Your change is remembered for later briefs.", RunEdited));
                row.Children.Add(Pill("Cancel", false, "Back to the brief without running anything.", () => { editing = false; Render(); }));
                Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                {
                    editor?.Focus();
                    if (editor is not null) editor.CaretIndex = editor.Text.Length;
                }));
            }
            else if (pending)
            {
                row.Children.Add(Pill("Run this", true, "Runs the brief above, with your answers.", () => Choose(item, brief, BriefChoice.RunBrief, null, Render)));
                row.Children.Add(Pill("Edit", false, "Opens the brief as text to change before running it.", () => { editing = true; Render(); }));
                row.Children.Add(Pill("Run as I typed", false, "Runs your message exactly as you typed it, without the brief.", () => Choose(item, brief, BriefChoice.RunAsTyped, null, Render)));
            }
            else if (failed)
            {
                row.Children.Add(Pill("Run as I typed", true, "Runs your message exactly as you typed it.", () => Choose(item, brief, BriefChoice.RunAsTyped, null, Render)));
                row.Children.Add(Pill("Edit", false, "Opens your message as text to change before running it.", () => { editing = true; Render(); }));
            }
            else if (item.State is "run" or "edited")
            {
                var save = ChatUi.Ghost(ChatUi.GlyphPrompts, "Save to my prompts", "Save this brief to my prompts",
                    "Keeps this brief in your saved prompts, to use again from the bookmark button by the message box or the home screen.");
                save.Click += (_, _) => SavePrompt(item.Text);
                row.Children.Add(save);
                row.Children.Add(ChatUi.CopyButton(() => item.Text, "the brief"));
            }
            if (row.Children.Count > 0) panel.Children.Add(row);

            var card = ChatUi.Elevated(panel, out var frame, ChatUi.Radius);
            frame.Padding = new Thickness(ChatUi.S3 + 2, ChatUi.S3, ChatUi.S3 + 2, ChatUi.S2);
            AutomationProperties.SetName(card, "Brief: " + line);
            if (pending || failed) AutomationProperties.SetLiveSetting(card, AutomationLiveSetting.Polite);
            holder.Content = card;
        }

        void RunEdited()
        {
            var text = editor?.Text ?? "";
            if (text.Trim().Length == 0)
            {
                Status("The brief is empty. Write what to do, or Cancel.", spin: false);
                editor?.Focus();
                return;
            }
            ChooseEdited(item, brief, item.State == "failed", text, () => { editing = false; Render(); });
        }

        _briefCards[item] = Render;
        Render();
        return holder;
    }

    private void ChooseEdited(ChatItem item, PromptBrief brief, bool failed, string edited, Action render)
    {
        // Edit on a card without a brief: the user's own message, changed. Nothing to learn from.
        if (failed) Choose(item, brief, BriefChoice.RunAsTyped, edited, render, learn: false);
        else Choose(item, brief, BriefChoice.RunEdited, edited, render);
    }

    /// <summary>Runs what the user picked on a brief card. Only now does anything reach Claude Code (and the model).</summary>
    private void Choose(ChatItem item, PromptBrief brief, BriefChoice choice, string? edited, Action render, bool learn = true)
    {
        if (_stopped || item.State is not ("pending" or "failed")) return;
        if (_turn is not null)
        {
            Status("Claude is still answering. Wait for it, or press Stop.", spin: true);
            return;
        }
        if (_setup != Setup.Ready && DeliveryCapture is null)
        {
            ShowSetup();
            Status("Finish the step above first, then choose again.", spin: false);
            return;
        }
        var shown = item.Text;
        var text = edited is not null && choice == BriefChoice.RunAsTyped ? edited.Trim() : PromptBriefs.TextFor(choice, brief, edited);
        item.State = choice switch { BriefChoice.RunBrief => "run", BriefChoice.RunEdited => "edited", _ => "typed" };
        if (choice == BriefChoice.RunEdited) item.Text = text;
        item.Brief = PromptBriefs.ToJson(brief);
        render();
        Save();
        if (choice == BriefChoice.RunEdited && learn && PromptBriefs.IsEdit(shown, text)) Remember(brief.Request, shown, text);
        var attachments = item.Files.Where(System.IO.File.Exists).Select(p => new Attachment { Path = p, Name = System.IO.Path.GetFileName(p) }).ToList();
        _ = RunTurn(text, attachments, choice == BriefChoice.RunAsTyped ? "" : text);
    }

    /// <summary>Distils the edit into a Global memory note in the background, and says what was kept.</summary>
    private void Remember(string request, string brief, string edited)
    {
        var install = _install;
        var model = _model;
        var task = _lastRemember = Task.Run(() => BriefRunner.RememberEdit(install, request, brief, edited, model));
        // Back on the pane's dispatcher: a click may come with no synchronization context (the field test presses the
        // button from Revit's API context), where TaskScheduler.FromCurrentSynchronizationContext throws.
        task.ContinueWith(t =>
        {
            if (t.Status != TaskStatus.RanToCompletion || t.Result.Length == 0) return;
            var note = t.Result.StartsWith(PromptBriefs.NotePrefix, StringComparison.Ordinal) ? t.Result.Substring(PromptBriefs.NotePrefix.Length) : t.Result;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_stopped) Note("Remembered for later briefs: " + note + " (edit or delete it in the Notes tab).");
            }));
        }, TaskScheduler.Default);
    }

    private static Button? FindButton(DependencyObject root, string name)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is Button b && AutomationProperties.GetName(b) == name) return b;
            if (FindButton(child, name) is { } found) return found;
        }
        return null;
    }

    // ---- The setting (in the model menu) --------------------------------------------------------------------------

    /// <summary>Adds "Brief before running: Always / Model changes only / Off" under the models in the model menu.</summary>
    private void AddBriefMenu()
    {
        var heading = Brand.Text("Brief before running", Brand.K.Muted, 11, bold: true);
        heading.Margin = new Thickness(10, ChatUi.S3, 10, 4);
        _modelList.Children.Add(heading);
        foreach (var mode in new[] { BriefMode.Always, BriefMode.ModelChanges, BriefMode.Off })
        {
            var selected = mode == _briefMode;
            var text = new StackPanel();
            text.Children.Add(Brand.Text(PromptBriefs.Label(mode), Brand.K.Text, ChatUi.BodySize, bold: selected));
            var hint = Brand.Text(PromptBriefs.Hint(mode), Brand.K.Muted, ChatUi.SmallSize);
            hint.Margin = new Thickness(0, 1, 0, 0);
            text.Children.Add(hint);
            var row = new DockPanel();
            var check = ChatUi.Glyph(ChatUi.GlyphCheck, Brand.K.Accent, 12);
            check.Margin = new Thickness(ChatUi.S2, 0, 0, 0);
            check.Visibility = selected ? Visibility.Visible : Visibility.Hidden;
            DockPanel.SetDock(check, Dock.Right);
            row.Children.Add(check);
            row.Children.Add(text);
            var item = new Button { Content = row, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            item.SetResourceReference(StyleProperty, "SpmChatMenuItem");
            AutomationProperties.SetName(item, "Brief before running: " + PromptBriefs.Label(mode) + (selected ? ", chosen" : ""));
            AutomationProperties.SetHelpText(item, PromptBriefs.Hint(mode));
            var chosen = mode;
            item.Click += (_, _) =>
            {
                CloseModelMenu(focusPill: true);
                _briefMode = chosen;
                PromptBriefs.SaveMode(ClaudeCli.Folder, chosen);
                Status("Brief before running: " + PromptBriefs.Label(chosen) + ". " + PromptBriefs.Hint(chosen) + ".", spin: false);
            };
            _modelList.Children.Add(item);
        }
    }

    // ---- Field-test hooks -----------------------------------------------------------------------------------------

    /// <summary>
    /// Hooks for the field test (Tools/FieldTest*.cs), on Revit's UI thread. Await the tasks without blocking that
    /// thread: the planning run's read-only tool calls are answered on it.
    /// </summary>
    internal static class BriefTest
    {
        /// <summary>
        /// Plans <paramref name="request"/> with the real planning run (read-only tools; nothing is sent to the chat).
        /// Check that the model's element counts are the same afterwards, and that an ambiguous request gives
        /// assumptions and questions.
        /// </summary>
        public static async Task<(PromptBrief? Brief, string Error)> Plan(string request)
        {
            var install = await Task.Run(ClaudeCli.Find);
            if (install is null) return (null, "Claude Code isn't installed.");
            var message = PromptBriefs.PlanMessage(request, [], []);
            return await Task.Run(() => BriefRunner.Plan(install, request, message, null, CancellationToken.None));
        }

        /// <summary>
        /// Builds a brief card in the open chat pane (not added to the conversation) and presses <paramref name="choice"/>
        /// on it as the user would; for RunEdited it presses Edit, types <paramref name="edited"/> and runs it. Returns
        /// the text the pane would have sent (captured, never sent; null when nothing would be) and, for an edit, the
        /// task saving the distilled note to memory (its result is the note; remove it afterwards with Forget).
        /// </summary>
        public static (string? Sent, Task<string>? Remembered, string Problem) Choose(PromptBrief brief, BriefChoice choice, string? edited = null)
        {
            if (_instance is not { } pane) return (null, null, "The chat pane isn't open.");
            if (pane._turn is not null) return (null, null, "The chat is busy.");
            string? sent = null;
            var before = _lastRemember;
            DeliveryCapture = t => sent = t;
            try
            {
                var item = new ChatItem { Kind = "brief", State = "pending", Brief = PromptBriefs.ToJson(brief), Text = PromptBriefs.Compose(brief) };
                var card = pane.BriefCard(item);
                pane._briefCards.Remove(item);
                void Press(string name)
                {
                    var button = FindButton(card, name) ?? throw new InvalidOperationException($"The card has no {name} button.");
                    button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                }
                switch (choice)
                {
                    case BriefChoice.RunBrief: Press("Run this"); break;
                    case BriefChoice.RunAsTyped: Press("Run as I typed"); break;
                    default:
                        Press("Edit");
                        var box = Find<TextBox>(card) ?? throw new InvalidOperationException("Edit didn't open the brief as text.");
                        box.Text = edited ?? "";
                        Press("Run edited brief");
                        break;
                }
                return (sent, _lastRemember != before ? _lastRemember : null, "");
            }
            catch (InvalidOperationException ex) { return (sent, null, ex.Message); }
            finally { DeliveryCapture = null; }
        }

        /// <summary>Removes a note the field test made from the Global memory notes.</summary>
        public static bool Forget(string note) => note.Length > 0 && MemoryNotes.Change(s => MemoryNotes.Remove(s, null, note));

        private static T? Find<T>(DependencyObject root) where T : DependencyObject
        {
            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            {
                if (child is T match) return match;
                if (Find<T>(child) is { } found) return found;
            }
            return null;
        }
    }
}
