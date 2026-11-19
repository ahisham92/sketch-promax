using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SketchProMax.UI;

namespace SketchProMax.Chat;

/// <summary>
/// The Questions tab (Chat.ChatQuestions; the owner, 19 Nov 2026): a pill under the message box, "Questions (3)", opens a
/// view in the conversation's place with nothing but the open questions, numbered. Each has its answers to tap and Other
/// to type; "4 yes" typed in the message box answers too. An answered question leaves the tab. The answers are gathered for
/// a moment and go to the coordinator as one message (with the next step while it works); one the user didn't understand
/// goes back to be written again and stays in the tab.
/// </summary>
internal sealed partial class ChatPane
{
    private readonly StackPanel _questionList = new() { Margin = new Thickness(ChatUi.S3, ChatUi.S2, ChatUi.S3, ChatUi.S4) };
    private readonly TextBlock _questionsLabel = new() { FontSize = ChatUi.SmallSize, VerticalAlignment = VerticalAlignment.Center };
    private ScrollViewer? _questionsScroll;
    private Button? _questionsPill;
    private bool _questionsShown;
    private DateTime _lastAnswer;
    private int _otherOpen;
    private string _questionsDrawn = "";
    private readonly Dictionary<int, string> _drafts = new();
    private DispatcherTimer? _questionsTimer;

    /// <summary>How long answers are gathered before they go to the coordinator together.</summary>
    private static readonly TimeSpan AnswerGather = TimeSpan.FromSeconds(4);

    private Button QuestionsPill()
    {
        var pill = new Button { Content = _questionsLabel, Padding = new Thickness(ChatUi.S1, 0, ChatUi.S1, 0), Margin = new Thickness(0, 0, ChatUi.S2, 0), VerticalAlignment = VerticalAlignment.Center };
        pill.SetResourceReference(StyleProperty, "SpmChatGhost");
        pill.Click += (_, _) =>
        {
            if (_questionsShown) HideQuestions(focus: true);
            else ShowQuestions();
        };
        _questionsPill = pill;
        _questionsTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _questionsTimer.Tick += (_, _) => DeliverAnswers(now: false);
        _questionsTimer.Start();
        ShowQuestionsPill();
        return pill;
    }

    /// <summary>The view in the conversation's place: the heading with Back, then the open questions.</summary>
    private ScrollViewer BuildQuestionsView()
    {
        _questionsScroll = new ScrollViewer
        {
            Content = _questionList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Visibility = Visibility.Collapsed, Focusable = false,
        };
        AutomationProperties.SetName(_questionsScroll, "Questions for you");
        return _questionsScroll;
    }

    private void ShowQuestionsPill()
    {
        if (_questionsPill is null) return;
        var open = _board?.Questions.OpenCount ?? 0;
        _questionsLabel.Text = _questionsShown ? "Back to the chat" : open > 0 ? $"Questions ({open})" : "Questions";
        Brand.Fg(_questionsLabel, open > 0 && !_questionsShown ? Brand.K.Link : Brand.K.Muted);
        _questionsLabel.FontWeight = open > 0 && !_questionsShown ? FontWeights.SemiBold : FontWeights.Normal;
        // Always there in a chat With coordinator, so the tab can be found before the first question.
        _questionsPill.Visibility = open > 0 || _questionsShown || IsCoordinator ? Visibility.Visible : Visibility.Collapsed;
        var tip = _questionsShown ? "Back to the conversation." : open > 0
            ? $"{open} question{(open == 1 ? "" : "s")} for you, numbered. Tap an answer, or type it in the box as \"4 yes\"."
            : "No questions are waiting. Questions from the coordinator and the jobs come here.";
        _questionsPill.ToolTip = tip;
        AutomationProperties.SetName(_questionsPill, _questionsLabel.Text);
        AutomationProperties.SetHelpText(_questionsPill, tip);
    }

    private void ShowQuestions()
    {
        if (_questionsScroll is null) return;
        if (_viewJob is not null) ShowCoordinator(focus: false);
        CloseDrawer();
        _questionsShown = true;
        _otherOpen = 0;
        _scroll.Visibility = Visibility.Collapsed;
        _jump.Visibility = Visibility.Collapsed;
        _questionsScroll.Visibility = Visibility.Visible;
        RenderQuestions();
        ShowQuestionsPill();
    }

    private void HideQuestions(bool focus)
    {
        if (!_questionsShown || _questionsScroll is null) return;
        _questionsShown = false;
        _questionsScroll.Visibility = Visibility.Collapsed;
        _scroll.Visibility = Visibility.Visible;
        UpdateJump();
        ShowQuestionsPill();
        DeliverAnswers(now: true); // leaving the tab sends what was answered
        if (focus) _input.Focus();
    }

    /// <summary>The questions changed (asked, rewritten, answered): the pill and the tab follow.</summary>
    private void QuestionsChanged()
    {
        ShowQuestionsPill();
        // Drawn again only when the questions changed: the jobs' progress mustn't take the box away while the user types.
        if (_questionsShown && Signature() != _questionsDrawn) RenderQuestions();
    }

    private string Signature() =>
        string.Join("\n", (_board?.Questions.Open() ?? []).Select(q => $"{q.Number}|{q.Text}|{string.Join("/", q.Options)}|{q.Recommended}|{q.Unclear}|{q.Answer}"));

    private void RenderQuestions()
    {
        _questionList.Children.Clear();
        _questionsDrawn = Signature();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, ChatUi.S3) };
        var back = ChatUi.Ghost(ChatUi.GlyphUp, "Back to the chat", "Back to the chat", "Back to the conversation (Esc).");
        back.Click += (_, _) => HideQuestions(focus: true);
        DockPanel.SetDock(back, Dock.Right);
        head.Children.Add(back);
        head.Children.Add(Brand.Text("Questions for you", Brand.K.Text, ChatUi.TitleSize, bold: true));
        _questionList.Children.Add(head);
        var open = _board?.Questions.Open() ?? [];
        if (open.Count == 0)
        {
            var none = Brand.Text("No questions are waiting. When the coordinator or a job needs you to decide something, it comes here, numbered.", Brand.K.Muted, ChatUi.BodySize);
            none.TextWrapping = TextWrapping.Wrap;
            _questionList.Children.Add(none);
            return;
        }
        var how = Brand.Text("Tap an answer, or Other to type one. You can also type \"4 yes\" in the box below. Answered questions leave this list.", Brand.K.Muted, ChatUi.SmallSize);
        how.TextWrapping = TextWrapping.Wrap;
        how.Margin = new Thickness(0, 0, 0, ChatUi.S3);
        _questionList.Children.Add(how);
        foreach (var q in open) _questionList.Children.Add(QuestionCard(q));
    }

    private UIElement QuestionCard(UserAsk q)
    {
        var panel = new StackPanel { Margin = new Thickness(ChatUi.S3) };
        var top = new WrapPanel();
        top.Children.Add(Brand.Text($"Q{q.Number}", Brand.K.Accent, ChatUi.BodySize, bold: true));
        var from = ChatUi.Small("  " + q.From + (q.Rewritten != default ? " · written again" : ""));
        top.Children.Add(from);
        panel.Children.Add(top);
        var text = Brand.Text(q.Text, Brand.K.Text, ChatUi.BodySize);
        text.TextWrapping = TextWrapping.Wrap;
        text.Margin = new Thickness(0, ChatUi.S1, 0, ChatUi.S2);
        panel.Children.Add(text);
        if (q.Unclear)
        {
            var wait = Brand.Text($"You said: \"{q.Answer}\". It is being written again; you can still answer it.", Brand.K.Muted, ChatUi.SmallSize);
            wait.TextWrapping = TextWrapping.Wrap;
            wait.Margin = new Thickness(0, 0, 0, ChatUi.S2);
            panel.Children.Add(wait);
        }
        var options = new WrapPanel();
        for (var k = 0; k < q.Options.Count; k++)
        {
            var answer = q.Options[k];
            var label = $"{(char)('A' + k)}. {answer}" + (k == q.Recommended ? " (suggested)" : "");
            var option = new Button { Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, Margin = new Thickness(0, 0, ChatUi.S1, ChatUi.S1) };
            option.SetResourceReference(StyleProperty, "SpmChatPill");
            if (k == q.Recommended) option.SetResourceReference(Control.BorderBrushProperty, Brand.K.Accent);
            AutomationProperties.SetName(option, $"Q{q.Number}: {answer}");
            option.ToolTip = "Answer with this.";
            option.Click += (_, _) => AnswerQuestion(q.Number, answer);
            options.Children.Add(option);
        }
        var other = new Button { Content = new TextBlock { Text = q.Options.Count > 0 ? "Other…" : "Answer…" }, Margin = new Thickness(0, 0, ChatUi.S1, ChatUi.S1) };
        other.SetResourceReference(StyleProperty, "SpmChatPill");
        AutomationProperties.SetName(other, $"Q{q.Number}: type an answer");
        other.ToolTip = "Type your own answer (or \"I don't understand\" to have it written again).";
        options.Children.Add(other);
        panel.Children.Add(options);
        var box = new TextBox
        {
            AcceptsReturn = false, TextWrapping = TextWrapping.Wrap, MinHeight = 56, MaxHeight = 160, FontSize = ChatUi.BodySize, Padding = new Thickness(ChatUi.S2),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, SpellCheck = { IsEnabled = true },
        };
        box.SetResourceReference(StyleProperty, "SpmChatField");
        if (_drafts.TryGetValue(q.Number, out var draft)) box.Text = draft;
        box.TextChanged += (_, _) => _drafts[q.Number] = box.Text;
        AutomationProperties.SetName(box, $"Your answer to Q{q.Number}");
        AutomationProperties.SetHelpText(box, "Enter sends it. Escape closes the box.");
        var send = new Button { Content = new TextBlock { Text = "Send answer" }, Margin = new Thickness(0, ChatUi.S1, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        send.SetResourceReference(StyleProperty, "SpmChatPrimary");
        var typing = new StackPanel { Visibility = _otherOpen == q.Number ? Visibility.Visible : Visibility.Collapsed };
        typing.Children.Add(box);
        typing.Children.Add(send);
        panel.Children.Add(typing);
        void SendTyped()
        {
            if (box.Text.Trim().Length == 0) { box.Focus(); return; }
            _otherOpen = 0;
            _drafts.Remove(q.Number);
            AnswerQuestion(q.Number, ChatQuestions.PickOption(q, box.Text.Trim()));
        }
        other.Click += (_, _) =>
        {
            _otherOpen = q.Number;
            typing.Visibility = Visibility.Visible;
            box.Focus();
        };
        send.Click += (_, _) => SendTyped();
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) { e.Handled = true; SendTyped(); }
            else if (e.Key == Key.Escape) { e.Handled = true; _otherOpen = 0; typing.Visibility = Visibility.Collapsed; other.Focus(); }
        };
        var card = ChatUi.Elevated(panel, out _);
        card.Margin = new Thickness(0, 0, 0, ChatUi.S2);
        return card;
    }

    /// <summary>The user's answer to question <paramref name="number"/>: it leaves the tab and goes to the coordinator shortly.</summary>
    private void AnswerQuestion(int number, string answer)
    {
        if (_board?.Questions.Answer(number, answer, DateTime.UtcNow) is not { } q) return;
        _lastAnswer = DateTime.UtcNow;
        Tools.ActivityLog.Note("question_answered", $"Q{q.Number}{(q.Unclear ? " (not understood)" : "")}: {JobBoard.OneLine(q.Answer, 200)}");
        Status(q.Unclear ? $"Q{q.Number} goes back to be written more plainly." : $"Answer to Q{q.Number} noted; it goes to the coordinator in a moment.", spin: false);
        Save();
        QuestionsChanged();
    }

    /// <summary>
    /// Answers typed in the message box by number ("4 yes", one per line): taken as answers, sent at once. False when the
    /// text isn't that (an ordinary message).
    /// </summary>
    private bool TypedAnswers(string typed)
    {
        if (_viewJob is not null || _board is null || _board.Questions.OpenCount == 0) return false;
        var answers = _board.Questions.Typed(typed);
        if (answers.Count == 0) return false;
        _input.Clear();
        var now = DateTime.UtcNow;
        foreach (var (ask, answer) in answers) _board.Questions.Answer(ask.Number, answer, now);
        Save();
        QuestionsChanged();
        DeliverAnswers(now: true);
        return true;
    }

    /// <summary>
    /// Sends the answers gathered to the coordinator as one message: its next turn when it is free, else with its next
    /// step. Kept for later while it plans or the chat isn't ready. <paramref name="now"/>: without waiting for more answers.
    /// </summary>
    private void DeliverAnswers(bool now)
    {
        if (_sample || _stopped || _board is not { } board || !board.Questions.AnswersWaiting) return;
        if (!now && DateTime.UtcNow - _lastAnswer < AnswerGather) return;
        if (_setup != Setup.Ready) return;
        if (_turn is not null && (_turn.Planning || _control is null)) return;
        var answers = board.Questions.TakeAnswers();
        if (answers.Count == 0) return;
        var shown = "Answered " + string.Join("; ", answers.Select(q => $"Q{q.Number}: {JobBoard.OneLine(q.Answer, 120)}"));
        var message = ChatQuestions.Message(answers);
        Tools.ActivityLog.Note("questions_sent", string.Join(", ", answers.Select(q => "Q" + q.Number)));
        if (_turn is not null)
        {
            ShowSentNote(Bridge.JobNotes.Add(message), shown, fromWindow: false);
            QuestionsChanged();
            return;
        }
        Add(new ChatItem { Kind = "user", Text = shown });
        Save();
        QuestionsChanged();
        _ = RunTurn(message, [], "");
    }

    /// <summary>
    /// A turn ended with questions written in its text instead of the tab (chat_questions): they go in the tab too, so
    /// every question can be answered there.
    /// </summary>
    private void CatchQuestions(Turn turn)
    {
        if (_board is not { } board || !IsCoordinator) return;
        var items = _conv.Items.Skip(Math.Max(0, Math.Min(turn.FirstItem, _conv.Items.Count))).ToList();
        if (items.Any(i => i.Kind == "tool" && i.Name.EndsWith(Tools.ChatQuestionsTool.ToolName, StringComparison.Ordinal))) return;
        if (items.LastOrDefault(i => i.Kind == "assistant" && i.Text.Trim().Length > 0) is not { } reply) return;
        var now = DateTime.UtcNow;
        var added = new List<int>();
        foreach (var (text, options) in ChatQuestions.FromReply(reply.Text))
        {
            var before = board.Questions.OpenCount;
            var q = board.Questions.Ask(0, "Coordinator", text, options, -1, now);
            if (board.Questions.OpenCount > before) added.Add(q.Number);
        }
        if (added.Count == 0) return;
        Tools.ActivityLog.Note("questions_caught", string.Join(", ", added.Select(n => "Q" + n)));
        Note(string.Join(", ", added.Select(n => "Q" + n)) + (added.Count == 1 ? " is" : " are") + " in Questions: answer there or type \"" + added[0] + " a\".");
        QuestionsChanged();
        SaveSoon();
    }
}
