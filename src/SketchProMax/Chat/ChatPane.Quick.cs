using System.Windows.Threading;

namespace SketchProMax.Chat;

/// <summary>
/// Questions answered at once while the chat is busy (19 Nov 2026: "while the guys are working I might need to ask the
/// coordinator for something ... while I don't get an answer"). A plain question (ChatFocus.IsQuestion) typed while the
/// main chat is answering, writing a brief or hearing a job's report doesn't wait for it: a quick read-only run answers it
/// from the chat's summary, its last messages and the jobs' reports. The main chat hears the question and its answer with
/// the user's next message (ChatFocus.Answered), so it doesn't answer again.
/// </summary>
internal sealed partial class ChatPane
{
    /// <summary>The longest a quick answer may take before the question goes to the main chat instead.</summary>
    private const int QuickTimeoutMs = 120_000;

    /// <summary>Per chat: questions answered while the main chat was busy (Answer "" when none came), told to it with the next message.</summary>
    private readonly Dictionary<string, List<(string Question, string Answer)>> _asides = new();

    private List<(string Question, string Answer)> Asides(Conversation conv)
    {
        var key = conv.Id.Length > 0 ? conv.Id : conv.Key;
        if (!_asides.TryGetValue(key, out var list)) _asides[key] = list = [];
        return list;
    }

    /// <summary>True when the typed message was a question taken by the quick run (the box is cleared, the answer comes).</summary>
    private bool QuickAnswer(string typed)
    {
        if (_sample || _install is not { } install || _turn is null || _pending.Count > 0 || Run is { Active: true }) return false;
        var question = typed.Trim();
        if (!ChatFocus.IsQuestion(question)) return false;
        _input.Clear();
        Add(new ChatItem { Kind = "user", Text = question });
        Note("Answering your question now; the work carries on.");
        Save();
        var recent = _conv.Items.Where(i => i.Kind is "user" or "assistant" && i.Text.Trim().Length > 0)
            .Reverse().Skip(1).Take(10).Reverse().Select(i => (i.Kind, i.Text)).ToList();
        var message = ChatFocus.QuickMessage(question, _conv.Recall, recent, _board?.Snapshot() ?? [])
                      + Revit.ChatModelWatch.Note(_conv.Key);
        var (session, conv, model) = (_s, _conv, "sonnet");
        _ = Task.Run(() => BriefRunner.Run(install, ChatFocus.QuickInstructions, message, readTools: true, model, QuickTimeoutMs, CancellationToken.None))
            .ContinueWith(t => Dispatcher.BeginInvoke(new Action(() => In(session, () =>
            {
                if (_stopped) return;
                var (text, error) = t.Status == TaskStatus.RanToCompletion ? t.Result : ("", "failed");
                if (error.Length == 0 && text.Trim().Length > 0)
                {
                    Add(new ChatItem { Kind = "assistant", Text = text.Trim() });
                    Asides(conv).Add((question, text.Trim()));
                    Save();
                    return;
                }
                // No quick answer: the question goes to the main chat, with its next step or as its next message.
                if (_conv != conv) Asides(conv).Add((question, ""));
                else if (_turn is null) _ = RunTurn(question, [], "", hint: "\n\n[Sketch ProMax, not typed by the user: " + ChatFocus.QuestionRule + "]" + TakeAsides());
                else if (!_turn.Planning && _control is not null) Bridge.JobNotes.Add(question);
                else Asides(conv).Add((question, ""));
                Save();
            }))), TaskScheduler.Default);
        return true;
    }

    /// <summary>The questions answered while the main chat was busy, for its next message (and forgotten once said).</summary>
    private string TakeAsides()
    {
        var list = Asides(_conv);
        var said = ChatFocus.Answered(list);
        list.Clear();
        return said;
    }
}
