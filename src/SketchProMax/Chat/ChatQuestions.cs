using System.Text.RegularExpressions;

namespace SketchProMax.Chat;

/// <summary>A question to the user in the chat's Questions tab (chat_questions). Saved with the chat.</summary>
internal sealed class UserAsk
{
    /// <summary>Its number in this chat: 1, 2, 3 … never used again, so "4 yes" always means the same question.</summary>
    public int Number { get; set; }
    /// <summary>The job that asks (0: the coordinator itself).</summary>
    public int Job { get; set; }
    /// <summary>Who asks, as the user sees it: "Job 51: 1800 mm beam", or "Coordinator".</summary>
    public string From { get; set; } = "";
    public string Text { get; set; } = "";
    /// <summary>The answers to tap (none: the user types one).</summary>
    public List<string> Options { get; set; } = [];
    /// <summary>The option suggested, -1 none.</summary>
    public int Recommended { get; set; } = -1;
    public DateTime Asked { get; set; }
    /// <summary>When it was written again (the user didn't understand it); default never.</summary>
    public DateTime Rewritten { get; set; }
    /// <summary>The user's answer, "" none yet.</summary>
    public string Answer { get; set; } = "";
    public DateTime AnsweredAt { get; set; }
    /// <summary>The answer reached the coordinator (or the asking job).</summary>
    public bool Sent { get; set; }
    /// <summary>The user said they don't understand it: it stays in the tab until it is written again.</summary>
    public bool Unclear { get; set; }
    /// <summary>Withdrawn by the coordinator (no longer needed).</summary>
    public bool Closed { get; set; }

    /// <summary>Shown in the Questions tab: not answered (or not understood) and not withdrawn. An answered one leaves the tab.</summary>
    public bool Open => !Closed && (Answer.Length == 0 || Unclear);
}

/// <summary>
/// The Questions tab (the owner, 19 Nov 2026: "the questions popping out during work need a specific tab for the questions,
/// numbered, where you don't see anything except these questions, which are sent to the coordinator ... if I don't understand
/// the 4th question it updates it; you answer by the number and answer, or by choosing MCQs, or Other to type"; "once a question
/// is answered it is removed from its tab"). The coordinator and the jobs ask with chat_questions; the user answers on the tab
/// or types "4 yes" in the message box. Answers go to the coordinator together. No WPF or Revit (the tests compile it).
/// </summary>
internal sealed class ChatQuestions
{
    private readonly List<UserAsk> _list;
    private readonly object _gate = new();

    public ChatQuestions(List<UserAsk>? list) => _list = list ?? [];

    /// <summary>The longest a question or option may be.</summary>
    public const int MaxText = 600, MaxOption = 120, MaxOptions = 6;

    /// <summary>The questions shown in the tab, by number.</summary>
    public List<UserAsk> Open()
    {
        lock (_gate) return _list.Where(q => q.Open).OrderBy(q => q.Number).ToList();
    }

    public int OpenCount
    {
        get { lock (_gate) return _list.Count(q => q.Open); }
    }

    public UserAsk? Find(int number)
    {
        lock (_gate) return _list.FirstOrDefault(q => q.Number == number);
    }

    /// <summary>
    /// A new question (the same open question from the same asker again: that one, so asking twice never shows it twice).
    /// </summary>
    public UserAsk Ask(int job, string from, string text, IEnumerable<string>? options, int recommended, DateTime now)
    {
        var clean = Clip(text, MaxText);
        var opts = Options(options);
        lock (_gate)
        {
            if (_list.FirstOrDefault(q => q.Open && q.Job == job && Same(q.Text, clean)) is { } again) return again;
            var q = new UserAsk
            {
                Number = _list.Count == 0 ? 1 : _list.Max(x => x.Number) + 1, Job = job, From = from.Trim(), Text = clean, Options = opts,
                Recommended = recommended >= 0 && recommended < opts.Count ? recommended : -1, Asked = now,
            };
            _list.Add(q);
            return q;
        }
    }

    /// <summary>The question written again (clearer, or with other options): it shows as new, waiting for an answer.</summary>
    public UserAsk? Rewrite(int number, string text, IEnumerable<string>? options, int recommended, DateTime now)
    {
        lock (_gate)
        {
            if (_list.FirstOrDefault(q => q.Number == number && !q.Closed) is not { } q) return null;
            if (text.Trim().Length > 0) q.Text = Clip(text, MaxText);
            if (options is not null)
            {
                q.Options = Options(options);
                q.Recommended = recommended >= 0 && recommended < q.Options.Count ? recommended : -1;
            }
            else if (recommended >= 0 && recommended < q.Options.Count) q.Recommended = recommended;
            q.Rewritten = now;
            q.Answer = "";
            q.Unclear = false;
            q.Sent = false;
            return q;
        }
    }

    /// <summary>Withdrawn: it leaves the tab.</summary>
    public bool Close(int number)
    {
        lock (_gate)
        {
            if (_list.FirstOrDefault(q => q.Number == number && q.Open) is not { } q) return false;
            q.Closed = true;
            return true;
        }
    }

    /// <summary>The user's answer to question <paramref name="number"/> (null when there is no such open question).</summary>
    public UserAsk? Answer(int number, string answer, DateTime now)
    {
        var text = answer.Trim();
        if (text.Length == 0) return null;
        lock (_gate)
        {
            if (_list.FirstOrDefault(q => q.Number == number && q.Open) is not { } q) return null;
            q.Answer = Clip(text, 2000);
            q.AnsweredAt = now;
            q.Unclear = SaysUnclear(text);
            q.Sent = false;
            return q;
        }
    }

    /// <summary>The answers not passed on yet, marked as passed on.</summary>
    public List<UserAsk> TakeAnswers()
    {
        lock (_gate)
        {
            var due = _list.Where(q => q.Answer.Length > 0 && !q.Sent && !q.Closed).OrderBy(q => q.Number).ToList();
            foreach (var q in due) q.Sent = true;
            return due;
        }
    }

    public bool AnswersWaiting
    {
        get { lock (_gate) return _list.Any(q => q.Answer.Length > 0 && !q.Sent && !q.Closed); }
    }

    /// <summary>For chat_questions action=list: the open questions, one line each.</summary>
    public string Describe()
    {
        var open = Open();
        if (open.Count == 0) return "No questions are open in the Questions tab.";
        return string.Join("\n", open.Select(q => $"Q{q.Number} ({q.From}): {q.Text}" + (q.Options.Count > 0 ? " Options: " + string.Join(" / ", q.Options) : "")
            + (q.Unclear ? $" - the user didn't understand it (\"{q.Answer}\"): write it again with action=update" : "")));
    }

    // ---- The message box: "4 yes", "Q4: the second", "4) B" ---------------------------------------------------------

    private static readonly Regex Numbered = new(@"^\s*(?<q>q(?:uestion)?\s*|#)?(?<n>\d{1,3})\s*(?:(?<sep>[.:)\-=,])\s*|\s+)(?<a>\S.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Short = new(@"^(?:yes|no|yep|nope|ok|okay|correct|right|wrong|agreed|both|neither|none|all|skip|keep|leave it|same|y|n)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Answers typed in the message box by number, one per line ("4 yes", "Q5: 1.30"): each line's open question and
    /// answer. Empty when any line isn't one (it is an ordinary message then). A lone option number or letter picks that
    /// option ("4 2", "4 b").
    /// </summary>
    public List<(UserAsk Ask, string Answer)> Typed(string typed)
    {
        var lines = typed.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var found = new List<(UserAsk, string)>();
        if (lines.Count == 0) return found;
        foreach (var line in lines)
        {
            var m = Numbered.Match(line);
            if (!m.Success || !int.TryParse(m.Groups["n"].Value, out var n)) return [];
            var q = Find(n);
            if (q is null || !q.Open) return [];
            var said = m.Groups["a"].Value.Trim();
            var picked = PickOption(q, said);
            // "2 piles are missing" is a message, not an answer to Q2: a bare number counts only before an option, a yes or no,
            // or "I don't understand"; "Q2 ...", "2: ...", "2) ..." always count.
            var marked = m.Groups["q"].Success || m.Groups["sep"].Success;
            if (!marked && picked == said && !Short.IsMatch(said) && !SaysUnclear(said)
                && !q.Options.Any(o => string.Equals(o.Trim(), said.TrimEnd('.'), StringComparison.OrdinalIgnoreCase))) return [];
            found.Add((q, picked));
        }
        return found;
    }

    /// <summary>"2" or "b" (or "option 2") picks that option of the question; anything else is the answer as typed.</summary>
    public static string PickOption(UserAsk q, string answer)
    {
        if (q.Options.Count == 0) return answer;
        var a = Regex.Replace(answer.Trim(), @"^(?:option|choice)\s*", "", RegexOptions.IgnoreCase).Trim().TrimEnd('.', ')');
        if (int.TryParse(a, out var k) && k >= 1 && k <= q.Options.Count) return q.Options[k - 1];
        if (a.Length == 1 && char.IsLetter(a[0]))
        {
            var i = char.ToLowerInvariant(a[0]) - 'a';
            if (i >= 0 && i < q.Options.Count) return q.Options[i];
        }
        return answer;
    }

    private static readonly Regex UnclearWords = new(
        @"\b(?:i\s+)?(?:don'?t|do\s+not|didn'?t|did\s+not)\s+(?:understand|get\s+(?:it|this|the\s+question)|follow)\b|\bnot\s+clear\b|\bunclear\b|\bwhat\s+do\s+you\s+mean\b|\bexplain\b|\bwhich\s+one\s+do\s+you\s+mean\b|^\s*\?+\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The answer says the question wasn't understood: it is written again instead of acted on.</summary>
    public static bool SaysUnclear(string answer) => UnclearWords.IsMatch(answer);

    /// <summary>What the coordinator is told about the answers: each with its question and who asked.</summary>
    public static string Message(IReadOnlyList<UserAsk> answers)
    {
        var lines = answers.Select(q => q.Unclear
            ? $"- Q{q.Number} ({q.From}: \"{q.Text}\"): the user doesn't understand it (\"{q.Answer}\"). Write it again, short and plain, with " +
              $"chat_questions action=update number={q.Number} (the tab shows it again); don't ask it in your answer."
            : $"- Q{q.Number} ({q.From}: \"{q.Text}\"): {q.Answer}" + (q.Job > 0 ? $" -> pass it to job {q.Job} now (chat_jobs action=message)." : ""));
        return "[The user answered in the Questions tab, not typed as a message]\n" + string.Join("\n", lines) +
               "\nAct on these now. Reply in one or two lines; don't repeat the questions.";
    }

    // ---- Questions left in a reply's text --------------------------------------------------------------------------

    private static readonly Regex Marker = new(@"(?:^|[\s:;,])\(?([a-e])\)\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ChoiceWord = new(@"\b(?:choose|chose|pick|decide|decision|prefer|which|either|or)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex YesNoStart = new(@"^(?:do|does|did|should|shall|want|would|will|can|could|may|is|are|am|was|were|have|has)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Tabbed = new(@"\bQ\d+\b.{0,40}\bQuestions\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The questions a reply asked in its text instead of the tab (19 Nov 2026: the coordinator wrote "until you choose:
    /// (a) lift all 15 whole, or (b) keep the bottoms" and no tab showed): a paragraph with lettered choices, or a sentence
    /// ending in "?". Each with its choices; a yes-or-no question gets Yes and No. At most four; none when the reply
    /// already points at the tab.
    /// </summary>
    public static List<(string Text, List<string> Options)> FromReply(string reply)
    {
        var found = new List<(string, List<string>)>();
        if (string.IsNullOrWhiteSpace(reply) || Tabbed.IsMatch(reply)) return found;
        var plain = Regex.Replace(reply, @"```.*?```", " ", RegexOptions.Singleline);
        plain = Regex.Replace(plain, @"`[^`]*`", " ");
        plain = plain.Replace("**", "").Replace("__", "");
        foreach (var raw in Regex.Split(plain, @"\n\s*\n|\n(?=\s*(?:[-*•]|\d+\.)\s)"))
        {
            var para = Regex.Replace(raw, @"^\s*(?:[-*•]|\d+\.)\s+", "").Replace("\n", " ").Trim();
            if (para.Length < 8) continue;
            var marks = Marker.Matches(para).Cast<Match>().ToList();
            var letters = marks.Select(m => char.ToLowerInvariant(m.Groups[1].Value[0])).ToList();
            if (marks.Count >= 2 && letters[0] == 'a' && letters[1] == 'b' && ChoiceWord.IsMatch(para))
            {
                var options = new List<string>();
                for (var k = 0; k < marks.Count; k++)
                {
                    var from = marks[k].Index + marks[k].Length;
                    var to = k + 1 < marks.Count ? marks[k + 1].Index : para.Length;
                    var option = para.Substring(from, Math.Max(0, to - from)).Trim();
                    option = Regex.Replace(option, @"[\s,;]*(?:,\s*)?\bor\s*$", "", RegexOptions.IgnoreCase).Trim().TrimEnd(',', ';', '.', '?', ' ');
                    if (option.Length > 0) options.Add(option);
                }
                var ask = para.Substring(0, marks[0].Index).Trim().TrimEnd(':', ',', ';', ' ');
                if (ask.Length == 0) ask = "Which one?";
                if (!ask.EndsWith("?", StringComparison.Ordinal)) ask += ":";
                if (options.Count >= 2) found.Add((ask, options));
            }
            else
                foreach (Match q in Regex.Matches(para, @"[^.!?]*\?"))
                {
                    var text = q.Value.Trim();
                    if (text.Length < 8 || text.IndexOf("http", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    found.Add((text, YesNoStart.IsMatch(text) && !ChoiceWord.IsMatch(text) ? ["Yes", "No"] : []));
                }
            if (found.Count >= 4) break;
        }
        return found.Take(4).ToList();
    }

    private static List<string> Options(IEnumerable<string>? options) =>
        (options ?? []).Select(o => Clip(o, MaxOption)).Where(o => o.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxOptions).ToList();

    private static bool Same(string a, string b) => string.Equals(Squash(a), Squash(b), StringComparison.OrdinalIgnoreCase);

    private static string Squash(string s) => Regex.Replace(s, @"\s+", " ").Trim().TrimEnd('?', '.');

    private static string Clip(string s, int max)
    {
        var t = Regex.Replace(s ?? "", @"[ \t]+", " ").Trim();
        return t.Length > max ? t.Substring(0, max).TrimEnd() + "…" : t;
    }
}
