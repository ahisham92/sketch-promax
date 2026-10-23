using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SketchProMax.Tools;

/// <summary>
/// What the field test proves, in one table, so nothing is missed silently. Every registered tool is "tested" (a
/// self-test case the field test runs, or one of <see cref="FieldSteps"/>) or listed in <see cref="ManualTools"/>; every
/// non-tool feature is an entry in <see cref="Features"/>, either tested (by a probe in FieldTest.Features.cs, or by a
/// tool's case) or manual with the reason and the steps a person follows in the guided checklist; every ribbon and pane
/// command maps to a feature in <see cref="Ribbon"/>. The unit tests fail when a tool, a ribbon command or a feature has
/// no entry, and the report's coverage section is built from the registry against this table.
///
/// To extend: a new tool needs a self-test case (SelfTest.cs) or a <see cref="ManualTools"/> row; a new ribbon or pane
/// command needs a <see cref="Ribbon"/> row naming a feature; a new feature needs a <see cref="Features"/> row. No Revit here.
/// </summary>
internal static class FieldTestCoverage
{
    /// <summary>How a feature is covered: Probe (FieldTest.Features.cs checks it), Case (a tool's self-test case checks it), Manual (a person, in the checklist).</summary>
    internal enum How { Probe, Case, Manual }

    /// <param name="Key">Unique, e.g. "chat.history". A Probe's key appears as a string in FieldTest.Features.cs.</param>
    /// <param name="Area">The checklist and report group.</param>
    /// <param name="By">Case: the tool whose case checks it. Otherwise empty.</param>
    /// <param name="Reason">Manual: why it can't be automated. Probe/Case: what is checked.</param>
    /// <param name="Steps">Manual: what the person does, where in Revit. Empty otherwise.</param>
    internal sealed record Feature(string Key, string Area, string Title, How How, string By, string Reason, string Steps = "");

    /// <summary>Tools the field test runs itself, outside the self-test cases (they open documents or are the tests).</summary>
    public static readonly string[] FieldSteps = ["batch_run_recipe", "run_self_test", "self_test_status", "run_field_test"];

    /// <summary>Tools no test can run, with the reason. Each also becomes a checklist step. Empty: every tool runs.</summary>
    public static readonly Dictionary<string, (string Reason, string Steps)> ManualTools = new(StringComparer.Ordinal);

    /// <summary>
    /// Tools whose case needs something the API can't make and the test project may lack: when the case is skipped for
    /// that, the row is "manual (checklist)" under this feature (a Manual one), never "not run".
    /// </summary>
    public static readonly Dictionary<string, string> ManualWhenSkipped = new(StringComparer.Ordinal) { ["add_legend_components"] = "legend.components" };

    public const string Chat = "Chat", RibbonArea = "Ribbon and favourites", Carbon = "Embodied carbon", Ve = "Value engineering",
        Rebar2D = "2D rebar", Bugs = "Report bug and exports", Updates = "Updates and the Claude app", Model = "Real-model checks",
        Limits = "Revit limits", BugFixes = "Bug fixes", Tasks = "Hand-off and Cancel", Job = "Live job card", Time = "Time saved",
        Issue = "Issue check", Package = "Drawing package", Redlines = "Redlines", Costing = "Cost plan and what if",
        Cleanup = "Purge and cleanup", Central = "Shared models", BeforeSharing = "Test before sharing";

    public static readonly Feature[] Features =
    [
        // ---- Chat
        new("chat.cli", Chat, "Claude Code is found", How.Probe, "", "finds claude.exe where the chat looks for it and reads its version"),
        new("chat.signin", Chat, "Claude Code sign-in state", How.Probe, "", "runs `claude auth status` and reports signed in, signed out or unknown"),
        new("chat.roundtrip", Chat, "A short chat round-trip", How.Probe, "", "sends one short message through Claude Code when the run asks for it (chat_round_trip); it uses your plan, so it is off by default and the checklist covers it"),
        new("chat.models", Chat, "Model list and saved choice", How.Probe, "", "the model picker's list has the default, and a choice is saved and read back"),
        new("chat.history", Chat, "Chat history save and load", How.Probe, "", "a chat saved to a temporary history folder is listed, summarised and deleted"),
        new("chat.survives_update", Chat, "The open chat survives an update", How.Probe, "", "a chat with a Claude Code session and an unsent message is saved and handed over as a version switch does, then reopened as the next version's pane opens it: same chat, messages, session and draft (in a temporary history folder)"),
        new("chat.update_reopen", Chat, "The open chat after a real update", How.Manual, "", "a real in-session update needs a newer published version and Revit",
            "With a model open, open an older chat from the history menu and type a message without sending it. Install an update while Revit runs (Updates panel, or wait for the automatic one). When the update is done, the Chat tab shows that same chat scrolled to the end with your unsent message in the box. Send it: Sketch ProMax answers knowing the earlier messages. The history menu still lists every chat. " +
            "Also check bug #1: right after the update, without restarting Revit, the Chat button, the Chat tab and the tab's position are all there."),
        new("chat.http_mcp", Chat, "The chat reaches the Revit tools inside Revit", How.Probe, "", "a connection of its own on a free port of 127.0.0.1 answers initialize, lists the Revit tools with the connector's own (models, jobs, read_pdf_part) and runs list_revit_models over real HTTP with its token, turns away a request without the token, and the chat's and a job's config name the running connection (with the job); no helper program and no plan use"),
        new("chat.tools_ready", Chat, "A new chat waits for its Revit tools", How.Probe, "", "the chat's own connection remembers which process (the main chat or a job) was sent the tool list and when, so a new chat process sends its first message only once it has the Revit tools (bug #40: \"No such tool available\" after a brief); no plan use"),
        new("chat.connector_blocked", Chat, "The chat on a PC that blocks the helper program", How.Manual, "", "it needs a PC whose policy (AppLocker or App Control) blocks programs that aren't signed",
            "On a company PC that blocks unsigned programs (or after IT blocks %LOCALAPPDATA%\\SketchProMax\\mcp\\SketchProMax.Mcp.exe), open Chat and ask \"what is in this model?\": Sketch ProMax answers from the model, with no note that the Revit tools didn't connect. Ask it to make sheets for two levels with jobs: the job cards work too. The desktop app, which still uses the helper program, may fail to reach Revit there. If the chat ever says the Revit tools didn't connect there, the note reads \"Your company blocks Sketch ProMax's helper program (…). Ask IT to allow it, or …\" with the program's path, never \"run Install.cmd\"."),
        new("chat.shrink", Chat, "Shrink chat: the /compact message and the automatic shrink", How.Probe, "", "the Shrink message is Claude Code's /compact, the compaction and each request's size are read from the stream, and a chat shrinks by itself once it grew by the set size over an empty chat; no plan use"),
        new("chat.recall", Chat, "Chats remember each other", How.Probe, "", "in a temporary history folder: a new chat gets its model's earlier summaries only, a search finds another model's chat by its project's name, open gives its steps, and a chat is summarised again only with new messages"),
        new("chat.recall_run", Chat, "A chat's summary is written", How.Probe, "", "the real summary run on a short sample chat keeps what it decided; it uses your Claude plan, so it runs only with chat_round_trip and the checklist covers it otherwise"),
        new("chat.shrink_ui", Chat, "Shrink chat in the pane", How.Manual, "", "pressing Shrink in a real chat uses your Claude plan and Revit",
            "Open Chat and send three or four requests about the model. Press Shrink chat (the arrows icon next to New chat): the status says \"Shrinking this chat…\", then a note says the chat was shrunk; every message stays on screen. Ask \"what did we do so far?\": Sketch ProMax still knows. Keep a long chat going (many model reads): after a reply, a note says the chat got long and was shrunk by itself."),
        new("chat.recall_ui", Chat, "Chats remember each other in the pane", How.Manual, "", "real summaries use your Claude plan, and they cross chats and models in Revit",
            "In model A, tell Sketch ProMax a few things (for example \"columns are 600x600 C40\") and have it do one task. Press New chat: the status may say it is remembering your last chat. In the Notes tab, From earlier chats lists that chat's summary; edit a line and leave the box, then Delete it and undo by repeating the first step. In the new chat ask \"what size are our columns?\": Sketch ProMax answers 600x600 C40 without being told. Open model B and ask \"do the columns the same way as in A\": Sketch ProMax says which chat it followed and uses the same settings."),
        new("chat.pdf", Chat, "PDFs attached in the chat", How.Probe, "", "sample PDFs (plain, with compressed page objects, too long, password-protected) are sorted into sent whole or read page by page, one sent whole goes in the message as a document, a large one's text is written page by page, a large one is split into parts under the size with every page once, and made ready for the chat (parts, parts.json for read_pdf_part, its text); no plan use"),
        new("chat.office", Chat, "Word and PowerPoint files attached in the chat", How.Probe, "", "a sample .docx gives its headings, paragraphs, bullets and table rows without deleted text, and a sample .pptx its slides in order; no plan use"),
        new("chat.documents", Chat, "Excel, Word, PowerPoint and PDF files from the chat", How.Probe, "", "make_document writes, in a temporary folder, an Excel workbook (title, header, numbers, a formula and a bold total), a Word document (heading, list, table), a PowerPoint presentation (title slide, then the slides in order, a long table carried over to a second slide) and a PDF (its pages and text), each read back; a second file of the same name gets (2) instead of replacing the first, and the chat finds the file in the step's result for its card; no plan use"),
        new("chat.documents_ui", Chat, "Files made in the chat: the card, Open and Save a copy", How.Manual, "", "making real files uses your Claude plan, and opening them needs Office on this PC",
            "With a model open, ask in the chat: \"make an Excel bill of quantities of the concrete in this model, with rates I can fill in\". A step \"Make an Excel workbook: …\" runs and a card shows under it with the file's name, \"Excel workbook\", its size and Open, Show in folder and Save a copy. Press Open: Excel opens it, the header is bold and frozen, and changing a rate changes the amount and the total. Then ask for \"a short Word report of the model with a picture of level 1\", \"a 5-slide PowerPoint about this model for the client\" and \"the same report as a PDF\": each gets its card, and each opens in its app with the title, headings, tables and the picture. Press Show in folder: Explorer opens Documents\\Sketch ProMax\\Made in chat with the file selected. Press Save a copy and save to the Desktop: the card says where it was saved. Close and reopen the chat: the cards are still there."),
        new("chat.pdf_ui", Chat, "Reading attached PDFs, Word and PowerPoint files in the pane", How.Manual, "", "reading real files uses your Claude plan and Revit",
            "Open Chat, press + and attach a PDF of a few pages (a spec section or a drawing sheet) and ask \"what is this about?\": Sketch ProMax answers from its content, quoting text and describing drawings, and never says it can't read PDFs. In the same chat attach a second PDF of several MB and ask about both: it answers about both. Attach a PDF of more than 18 MB or 100 pages (a full drawing set or spec) and ask about one sheet or clause: \"Preparing the attachments…\" shows while it is split by itself, then step rows \"Read a part of the PDF\" name the parts it read, and the answer gives the page numbers; open a step's details to see that part's own answer. Ask a follow-up about the same thing: it is quicker. Ask for a summary of the whole file: every part is read. Attach a Word document and a PowerPoint file and ask for a summary of each: it quotes them, tables included."),
        new("chat.split_ui", Chat, "Split PDF", How.Manual, "", "the window, the file picker and the parts on disk are checked on screen",
            "Open the pane's Activity tab and press Split PDF: the window opens with 18 MB as the part size; Browse to a PDF of more than 18 MB. Press Split: the bar moves, Revit stays usable, and it ends with a list of parts, each 18 MB or less, named with their pages. Open folder shows them next to the PDF. Attach one part in the chat: it is read whole. Press Split again on a big file and then Stop: it stops and says where the parts written so far are."),
        new("chat.jobs", Chat, "Parallel jobs: routing, turns, limit, each job's own chat and saving", How.Probe, "", "in memory and a temporary folder: jobs are opened and a follow-up goes to the job it belongs to instead of a new one, no more than the set number start at once and the next starts when one ends, changes take turns in one queue first come first served (a read takes no turn), a job's own chat holds the coordinator's task and a message the user typed in it with its file (which the job gets next), Open job asks for the job's view, a job saved before job views opens with its lines, a finished job's report for the main chat, the jobs and their sessions (sessions.json) are saved with the chat and read back stopped, and a job's card shows its number, title, state, model and effort; no plan use"),
        new("chat.types", Chat, "Chat types: With coordinator or Simple chat", How.Probe, "", "a new chat takes the Settings' default type (or the one picked on New chat), a chat saved before types is a Simple chat unless it has jobs, the type is saved with the chat, switching it is refused while jobs are open, and a Simple chat's Claude Code is started without chat_jobs and without the coordinator's instructions while a chat With coordinator gets both; no plan use"),
        new("chat.auto", Chat, "Automatic model and effort", How.Probe, "", "requests are sorted into simple, everyday and hard work by their words; Automatic gives Haiku with low effort, Sonnet with medium and Opus with high by default, or what Settings > Chat says; the coordinator's kind for a job is used; a model and effort picked by hand stick; a model the plan refused is skipped; models are Claude Code's aliases; an older Claude Code's refusal of --effort is recognised; no plan use"),
        new("chat.jobs_ui", Chat, "Parallel jobs in the pane", How.Manual, "", "real jobs use your plan and change the model, and the cards are checked on screen",
            "Use a test model (the connector installed by the same Install.cmd as the add-in). Open Chat (a chat With coordinator, as shown under its name) and ask \"make sheets for levels 1 to 3, and check the walls for warnings\": Sketch ProMax says which jobs it started and a card shows per job (\"Job 1 · …\", Working) with its clock moving and the model and effort it runs on (for example \"Sonnet, medium effort · everyday work\"); Revit stays usable. While one job changes the model, another job's change shows \"Waiting its turn\" on its card until the first ends. Ask \"also add level 4\": it goes to the sheets job (its card works again), no new card. Press Stop on a working job: its card says Stopped. When a job ends, its card says Done with its report and the main chat sums it up in a line or two. In Settings, Chat tab, set Run up to 1 job at once and ask for two separate jobs: the second says Waiting to start until the first ends. Open another chat and come back from the history: the cards are still there. The cards say Sketch ProMax or Job, never Claude, and read well in the light and dark themes."),
        new("chat.jobs_view_ui", Chat, "A job's own chat, and back to the coordinator", How.Manual, "", "talking to a real job uses your plan, and the view switch is judged on screen",
            "In a chat With coordinator, start two jobs (as in the parallel jobs item). A row of pills shows over the conversation: Coordinator, then \"Job 1 · …\" and \"Job 2 · …\" with a dot for their state. Press Open job on job 1's card (or its pill): the whole pane shows job 1's chat: the task marked \"From the coordinator\", its answers and its steps (open one: its input and result show), and a bar \"Job 1 · …\" with its state, the model it runs on, Back to coordinator and Stop job. The message box says \"Message job 1…\". Attach a picture and type \"also add a north arrow\", Enter: your message shows with the picture and the status line says it was sent to job 1; the job carries on with it (when it finishes its current step if it was working). Press Job 2's pill: job 2's chat shows. Press Esc (or Back to coordinator): the main chat is back, with the message box for it, scrolled to the latest. With the keyboard only: Tab reaches the pills, Back to coordinator and Stop job, and Space opens them; a screen reader reads \"Job 1: …, Working, shown\". The view says Sketch ProMax, Coordinator or Job, never Claude, in the light and dark themes."),
        new("chat.types_ui", Chat, "Choosing a chat's type", How.Manual, "", "the menus and a real Simple chat are checked on screen and use your plan",
            "Under the chat's name it says \"With coordinator\" (for a chat made before this version: \"Simple chat\", unless it already had jobs). Press New chat (+, top right): \"New chat with coordinator\" and \"New simple chat\" show, your default first and marked \"(your default, Settings > Chat)\". Pick New simple chat: the new chat says Simple chat under its name and in the history drawer. Ask \"make sheets for levels 1 to 3, and check the walls\": Sketch ProMax does it in this chat, step by step, with no job cards. Press \"Simple chat\" under the name and pick With coordinator: a note says the chat now works with a coordinator; ask for two separate things: job cards appear. While a job runs, try to switch back: the status line says jobs are still open. In Settings, Chat tab, pick New chats start as Simple chat and Save: New chat now makes a Simple chat."),
        new("chat.auto_ui", Chat, "Automatic model and effort in the pane", How.Manual, "", "real requests use your plan, and the pickers are checked on screen",
            "Open a new chat: under its name it says \"Effort: Automatic\", and the model pill by the message box says \"Auto\". Ask \"rename the views on level 2 to L2 - …\": a note says \"Automatic: Haiku, low effort (simple work: 'rename')\", the pill says Auto · Haiku and the effort Automatic (low). Ask \"check the model against this BEP\" with a PDF attached: the note says Opus, high effort. In a chat With coordinator ask for two separate jobs: each card shows its model and effort. Press the model pill and pick Sonnet, and Effort and pick High: the next requests and new jobs use Sonnet, high effort, and no Automatic note shows; open another chat and come back: still Sonnet and High. Set both back to Automatic: the notes come back. In Settings, Chat tab, change Simple work's model to Sonnet and Save: a simple request in a new chat says Sonnet. With an old Claude Code (before effort was added), a request still runs and a note says it runs at its default effort."),
        new("chat.usage", Chat, "Plan usage: the 5-hour and weekly limits", How.Probe, "", "a rate_limit_event line as Claude Code writes it with each answer is read into the 5-hour and weekly limits, a later report naming a per-model weekly limit is merged in, the meter and reset words, a limit whose reset passed shows no figure, a report without a figure gets none, the 80 % warning comes once per limit and reset and again when reached, naming the running jobs, and the last report is kept on disk; no plan use"),
        new("chat.usage_ui", Chat, "Plan usage in the pane", How.Manual, "", "real figures come with real answers on your plan, and the meter is judged on screen against the Claude app",
            "Open Chat (signed in with your Claude plan). Before any answer the header says \"Usage: not reported yet\" (or the last figures from an earlier Revit session). Ask \"what is in this model?\": after the answer the header says \"Usage: 5h N% · Week N%\" with a small bar. Hover it: a tip lists the current session with its time left (\"2h 1m left (resets 17:30)\") and the weekly limit with its reset. Open the Claude app's usage page side by side: the session and weekly figures and reset times match (to the minute and the percent). Click the meter: a detail lists Current session (5-hour limit) and Weekly limit, all models, each with % used, a bar and \"Resets in … (HH:mm)\" or \"Resets Mon …\", and a line saying the figures are as reported at that time, never estimated. Esc or Close shuts it; Tab and Space reach it with the keyboard. Near a limit (80 % or more), a note in the chat says which limit, when it resets and which jobs are running; it comes once per limit. Check both themes."),
        new("chat.big_ui", Chat, "The chat large, and back to the side; a tidy header", How.Manual, "", "moving the pane between Revit's dock and its own window, and how crowded the header looks, are judged on screen",
            "Open Chat in its pane on the right. The header shows the menu, the chat's name, its type and model, \"Effort: …\" and \"Usage: 5h N% · Week N%\", and on the right only Shrink, + and the expand arrows (Check again shows only while the chat isn't set up). Drag the pane narrower than about 400 px: the words shorten (\"Effort: Auto\", \"5h N% · Wk N%\", the brief pill becomes its icon) and nothing overlaps. Press +: New chat with coordinator and New simple chat show, your default first. In a chat With coordinator start two jobs, then press the expand arrows: a maximised \"Sketch ProMax chat\" window opens with the same conversation, a running answer still streaming, and a Jobs column on the right listing Coordinator and each job with its state; the pane on the side says the chat is open in a large window. Click a job in the column: its chat opens; Esc goes back. Type and send a message there: the letters go into the box, not to Revit. Minimise the window: the chat is back in the pane on the right, as it was, and the window is gone. Open it large again and close it with X, then again with the collapse arrows: each time the chat returns to the side. Repeat in Revit's dark theme."),
        new("chat.install_blocked_ui", Chat, "Install Claude Code when the PC won't let Revit open PowerShell", How.Manual, "", "needs a PC whose policy blocks starting powershell.exe from another program",
            "On a PC without Claude Code where Revit may not start PowerShell, open Chat and press Install Claude Code. The card says the PC didn't let Sketch ProMax open PowerShell, shows the line irm https://claude.ai/install.ps1 | iex with Copy the line, and waits. Paste it into your own PowerShell window; when it finishes the pane goes on to Sign in by itself (or press Check again)."),
        new("chat.prompts", Chat, "Saved prompts", How.Probe, "", "a prompt is added, saved, read back and removed in a temporary file"),
        new("chat.notes", Chat, "Notes read and write", How.Probe, "", "notes are parsed, added, written and read back in memory; your notes file is only read"),
        new("brief.plan", Chat, "Prompt enhancer: a brief before anything runs", How.Probe, "", "an ambiguous request is planned (the real planning run) into a brief with assumptions and questions, and the model's element count is the same before and after; it uses your plan, so it runs only with chat_round_trip and the checklist covers it otherwise"),
        new("brief.choose", Chat, "Prompt enhancer: Run as I typed and a remembered edit", How.Probe, "", "on a brief card in the pane, Run as I typed sends exactly the typed text; an edited brief is remembered as a Global note, which is then removed; with chat_round_trip only"),
        new("brief.ui", Chat, "The brief card in the chat", How.Manual, "", "the card's look and its one-tap questions are judged on screen, and planning uses your plan",
            "Open Chat and turn briefs on in the model menu. Send \"make the walls thicker\": a brief card lists the scope, assumptions and one-tap questions, and nothing in the model changes before you choose. Tap an answer, then Run this. Send it again and press Run as I typed: your words are sent unchanged. Send it once more, press Edit, change a line and run it: \"Remembered for later briefs\" appears; delete that note in the Notes tab."),
        new("chat.dpi", Chat, "Chat screenshots at 100 to 175 % scale", How.Probe, "", "the pane with a table, code, steps, a brief and a finished job card is drawn at 100, 125, 150 and 175 % in both themes and saved as PNGs next to the report; a person compares them"),
        new("chat.frames", Chat, "Chat frame times", How.Probe, "", "in a hidden window, the history drawer opens and closes and a sample reply streams in; the average and worst frame times are reported (under 25 ms on average, no frame over 200 ms)"),
        new("chat.motion", Chat, "Chat animations: the setting and each animation settling", How.Probe, "", "the Full, Reduced and Off levels are stored and read back and switch the motion on and off; in a hidden window at Full, streamed text fades in and settles to the text colour, a finished step's check pops in, Send and Stop swap with a pop, the thinking mark breathes only while shown, and the finished glow comes and goes, with no transform or fade left behind; your setting is put back"),
        new("chat.animations", Chat, "Chat animations in the pane", How.Manual, "", "how the new animations look and feel, and the taskbar flash, are judged by a person in Revit",
            "Open Settings: under Chat animations, Full is picked. Open Chat and ask \"What is in this model?\". While it works, the Sketch ProMax mark next to the status line gently swells and its yellow spark twinkles. Send turns into the red Stop button with a small pop, and back when the answer ends. Each step row shows a spinning blue ring on a faint circle, then a green check (or red cross) pops in. New paragraphs of the answer fade in from light to full colour; the text stays sharp. Your own message rises up from the box. Then ask for something that takes over 20 seconds (for example \"tag all doors in every plan view\" on a _detached copy) and switch to another program while it runs: when it ends, Revit's taskbar button flashes, and back in Revit the answer has a blue glow that fades. In Settings pick Reduced and Save: the mark stands still, text appears at once, nothing pops or glows, but step details and the history drawer still slide. Pick Off: nothing slides, fades or pops (only the small progress spinners turn) and the taskbar doesn't flash. Put it back to Full."),
        new("chat.composer", Chat, "The message box", How.Probe, "", "in a hidden window, typed text starts top-left exactly where the hint was, four lines grow the box with the first line in place, the caret has a visible colour, and a click on the card's empty part or the box's padding puts the caret in the box"),
        new("chat.typing", Chat, "Typing in the message box", How.Manual, "", "the keyboard going to the box and not to Revit's shortcuts, and how the box looks at rest, on hover and focused, need a person in Revit",
            "Open Chat. The message box is a light field with a grey border and the hint \"Ask anything about your model, or tell me what to do…\". Hover it: the border darkens. Click anywhere on the card (not a button): a blue 2 px ring shows and the caret blinks top-left where the hint was. Type \"WA\" then \"MV\" and Delete with something selected in the view: the letters go into the box, Revit starts no Wall or Move command and nothing is deleted. Press Shift+Enter three times: the box grows upward and the first line stays at the top. Repeat in Revit's dark theme."),
        new("chat.sharp", Chat, "Chat is sharp at every scale", How.Manual, "", "sharpness is judged by eye, and moving between monitors needs two screens",
            "Open the chat-dpi folder next to the field test report and compare the screenshots: text, borders and icons are sharp at 100, 125, 150 and 175 %, in light and dark, with nothing cut off. Then move the Chat pane between monitors with different scaling (for example 100 % and 150 %) and check the text stays sharp on both, with no blur after the move."),
        new("chat.smooth", Chat, "Chat feels smooth", How.Manual, "", "how motion feels is judged by a person",
            "In the Chat pane: does it feel smooth? Hover and press buttons (a quick fade, a slight press), open and close the history drawer (it slides), open a step's details (they ease open), and watch a reply stream in (new messages ease in, the list glides down, scrolling up stops it and Jump to latest brings it back). With Windows' Show animations off (Settings > Accessibility > Visual effects), everything changes at once instead."),
        new("chat.ui", Chat, "Chat in the pane", How.Manual, "", "streaming, scrolling and the drawer are what you see on screen; a test can't judge them",
            "Click Chat on the Sketch ProMax tab. Send \"What is in this model?\" and watch the reply stream in: it leads with a short plain summary (levels, main elements with counts, views and sheets) in the model's units, with no tool names or ids. Open the history drawer, search, pin and rename a chat. Switch the model in the picker. Save a prompt from the home screen and use it. Open the Notes tab, add a note and check the chat uses it."),

        new("brand.pane", Chat, "Sketch ProMax by name, and plain step rows", How.Manual, "", "the pane's header, the sender line, the status line and the step rows are judged on screen",
            "Open the pane on a model. The header under Sketch ProMax reads \"Sketch ProMax: connected\" (or ready / not running), never Claude. Ask \"read the base and top of the selected columns\" with a few columns selected: each answer is headed \"Sketch ProMax · <time>\", the line under the box says \"Sketch ProMax is working…\", and each step row shows a check or spinner and the step's plain description (for example \"Read 4 selected columns: base and top\"), never a tool id or \"key=value\". While a change runs, the status reads \"Running: <what it does>…\" plus \"If Revit asks, answer it there.\" (nothing more when Ask before changes is Always allow). A job card lists steps such as \"Read model data\" and \"Change the model\"; the chevron on a row still opens its input and result. Open Settings, the history drawer, the Notes tab and the Activity tab, and hover the ribbon's first panel (titled Assistant): no Claude, except where you install or sign in to Claude Code or open the Claude desktop app."),
        new("brand.ribbon", RibbonArea, "The ribbon names the assistant Sketch ProMax", How.Probe, "", "the first panel is titled Assistant and no button text, tooltip or long description on the tab names the model except as a product to act on (Claude Code, the Claude desktop app)"),

        // ---- Ribbon and favourites
        new("ribbon.registered", RibbonArea, "Ribbon buttons registered and enabled", How.Probe, "", "every button, drop-down and entry the core declares is on the Sketch ProMax tab and enabled"),
        new("ribbon.dialogs", RibbonArea, "Tool option windows open with saved values", How.Probe, "", "every ribbon tool's options window builds, and a value typed last time is filled in again"),
        new("favourites", RibbonArea, "A favourite added and removed", How.Probe, "", "a ribbon tool is pinned to My tools, found there, and unpinned again"),
        new("ribbon.ui", RibbonArea, "Ribbon look and favourites in use", How.Manual, "", "button labels, icons and the My tools slots are judged by eye",
            "On the Sketch ProMax tab, check every panel shows its labels and icons (narrow the Revit window too). Open Favourites, pin two tools, reorder them and run one from My tools. Unpin them."),
        new("settings.tabs", RibbonArea, "Settings: every setting on its tab, and the window fits the screen", How.Probe, "", "the Settings window builds with its six tabs, every setting it saves sits on the tab the layout names (and no tab is empty), and its size stays within 85 % of this monitor's work area and of a 1080p screen at 100 % and 150 % scale; nothing is saved"),
        new("settings.ui", RibbonArea, "Settings: tabs, size and keys", How.Manual, "", "whether the window fits and reads well is judged on screen",
            "Click Settings. The window opens over Revit, fully on screen with Save and Cancel visible at the bottom, and the tabs on the left: Approvals, Chat, Firm standards, Time tracking, Issue & drawings, Updates. Make the window as small as it goes: each page scrolls on its own and Save and Cancel stay visible. On Issue & drawings the long check box label wraps instead of being cut off. Press Ctrl+Tab and Ctrl+Shift+Tab: the tabs change. On Firm standards, Enter in the box starts a new line; Browse picks a legend seed file. Open Time tracking, type 0 as the idle minutes and press Enter: Settings stays open on that tab with the message at the bottom; put 5 back. Press Esc: it closes without saving. Open it again: it opens on the tab you left. Switch Revit to the dark theme (and on a 150 % scale monitor if you have one) and check the tabs and text are readable."),
        new("license.ui", RibbonArea, "License: the lock on other PCs and the License manager", How.Manual, "", "needs the owner's PC and a second PC, and a key made for it",
            "On the owner's PC click License: the License manager opens (no lock). If it asks for the signing secret, paste it once and Save secret. On the second PC, start Revit: the License window opens by itself with its PC code; any Sketch ProMax button (and sending in the chat) opens it again and does nothing else, while Updates, Help and Settings still work. Copy the code. In the manager paste it, type a name, pick an end date and Make key: the key is copied and listed. On the second PC paste it and Activate: it says Licensed to that name, and the tools and chat work. Paste the same key on a third PC: it says the key belongs to another PC. In the manager pick the key, set a later end date and Change end date: a new key is copied, nothing to publish. Set an earlier date: the old key asks to be published; add a GitHub token (Add token, Make token on GitHub) and Publish now, then restart Revit on the second PC with the old key: it is locked once the date passes. Cancel key and publish: after a Revit restart on that PC it is locked. Export to Excel opens the list."),
        new("ribbon.status", RibbonArea, "Connection status, Copy prompt, Send view, pane", How.Manual, "", "these show dialogs, use the clipboard or depend on the Claude app running",
            "Click the connection status button (first panel) and read the dialog. Click Copy prompt and paste into Notepad. Open a plan, click Send view and check the picture is attached in the chat. Use More > Show or hide pane twice."),
        new("ribbon.job", RibbonArea, "Job and Update buttons", How.Manual, "", "they show live state that only exists while a job or update runs",
            "Start a long tool (e.g. Model check), watch the Job button show progress, click it and stop the job. Click Update: the update window opens on Checking, then says up to date (or installs the newer version in place); no browser or folder opens."),
        new("help", RibbonArea, "Help page", How.Manual, "", "opens a web page in your browser", "Help > Help and getting started: the guide opens in the browser."),
        new("diagnostics.journal", RibbonArea, "The field test's breadcrumbs when Revit closes", How.Probe, "", "in a temporary folder, a breadcrumb journal written as a run writes it, stopped as if Revit closed during a step: the next start finds it (its Revit is gone), its rows end with a crash row naming that step, Continue carries the steps that ended and doesn't run the crashed one (nor one remembered from an earlier crash), and a run that ends by itself leaves no journal"),
        new("diagnostics.running_call", RibbonArea, "Which call Revit closed during", How.Probe, "", "in a temporary folder, the running-call file of a Revit that is gone is read back at the next start as which call was running, with its arguments, and removed (once)"),
        new("diagnostics.crash_ui", RibbonArea, "After Revit closed during the field test", How.Manual, "", "it needs Revit to close during a run",
            "Start Help > Diagnostics > Field test (Run everything). When the job card says it is on the tool families, end Revit in Task Manager (End task on Revit). Start Revit again and open any model: within a minute or two (or at once when you click Field test) a window says \"Revit closed during the field test, while running <tool> (<family>). A report of what ran is in <folder>.\" Open the report folder: a report named \"field test (interrupted)\" lists the rows that ran and the tool that was running as \"crash\". Report bug's folder has a new bug report about it. Click Field test and pick Continue the field test after that step: the run builds the test project again, the steps from before say \"done before Revit closed\", the crashed tool says \"not run again\" and counts as a failure, and the rest runs. The window doesn't come back after that; Run everything runs every step again."),
        new("diagnostics", RibbonArea, "Self-test, field test and the test checklist", How.Probe, "", "the field test runs the self-test (safe mode) on its own project; this report is the field test"),

        new("checklist.build", RibbonArea, "The guided test checklist builds and saves", How.Probe, "", "the checklist lists every manual item plus the fixed bug rows, and its result file is saved (to a temporary folder)"),

        // ---- Hand-off and Cancel
        new("revit.blocked", Tasks, "A request Revit didn't start: pick-up and the busy card", How.Probe, "", "an empty request to an idle Revit is picked up within 8 s (asked again each second); the ribbon is read for an edit mode's Finish button and shows none; the \"Revit is busy\" card names the step in plain words with Esc, never the tool id"),
        new("revit.blocked_ui", Tasks, "The busy card in the chat", How.Manual, "", "it needs Revit held in a command or an edit mode while the chat asks something",
            "In Revit start the Wall command and leave it waiting for a click. In the chat ask \"What is in this model?\". If Revit doesn't take it, after about 36 s a card says \"Revit is busy\", \"Press Esc in Revit once or twice, then click Continue\", \"Revit didn't start reading your model yet. Nothing was changed.\" with no tool name. Press Esc twice in Revit and click Continue: the answer comes. Then edit a floor's boundary (sketch mode) and ask again: the card says \"Finish or cancel the edit in Revit\". Finish the sketch and click Continue. Also ask with the caret still in the chat box and Revit otherwise idle: the answer comes with no card."),
        new("handoff.continue", Tasks, "Hand-off: Continue re-checks and resumes", How.Probe, "", "copy_dimensions on a cut-edge dimension hands the step over; Continue before it is done is asked again, Continue after it is done passes the re-check, and the result's handoff section and the recent hand-offs say done"),
        new("handoff.skip_stop", Tasks, "Hand-off: Skip this item and Stop", How.Probe, "", "add_legend_components with no component to copy hands over and Skip is reported; on three steps, Skip, then Stop, and the third is not asked and counts as stopped"),
        new("handoff.static", Tasks, "copy_dimensions static stand-ins", How.Probe, "", "copy_dimensions with static_fallback draws a detail line and a \"(static)\" value note for a cut-edge dimension instead of handing it over"),
        new("handoff.ui", Tasks, "The hand-off window and chat card", How.Manual, "", "the window, the chat card and doing the step by hand are what a person sees and does",
            "On a _detached copy with no legend component in any legend (and no legend seed in Settings): ask in the chat to add legend components for two wall types. A card shows what to do, where in Revit and why, with Continue, Skip this item and Stop. Press Continue first: it says not done yet. Place one legend component by hand (Annotate > Component > Legend Component), press Continue: the task carries on. Run Add legend components from the ribbon too: the same step opens in a window. Press Stop there and check the result says you stopped."),
        new("cancel.fast", Tasks, "Cancel stops a heavy tool part-way", How.Probe, "", "place_many on 60 placements is asked to stop after the first chunk; it stops early, keeps what it did and says so in its result"),

        // ---- The live job card
        new("job.progress", Job, "Job card: progress reaches 100% and matches the items", How.Probe, "", "rename_elements on 24 drafting views made for the test, as a chat task: the job feed reports item n of 24 at every boundary, ends at 24 of 24 (100%), and all 24 are renamed"),
        new("job.pause_resume", Job, "Job card: Pause and Resume", How.Probe, "", "Pause pressed at item 8 of rename_elements on 24 views: the batch pauses at the next boundary with no transaction open, Resume carries on and all 24 are renamed"),
        new("job.stop", Job, "Job card: Stop rolls back the item in progress", How.Probe, "", "Stop pressed while item 9 of 24 runs: that item is rolled back (its name unchanged), the 8 before it are kept, the rest are not done, and the assistant is told the user stopped"),
        new("job.skip", Job, "Job card: Skip step", How.Probe, "", "Skip step at item 6 of 24: the step ends with 5 done (kept), its result says skipped by the user so the assistant moves on, and the next step runs normally"),
        new("job.message", Job, "Job card: a message sent mid-task reaches the assistant at the boundary", How.Probe, "", "a message queued while item 4 of 24 runs is added once to that step's result (what the assistant reads next) and reported as delivered; the batch is not interrupted. A test hook stands in for the chat, so no plan usage is spent"),
        new("job.thinking", Job, "Job card: the Thinking block", How.Probe, "", "a sample stream-json transcript with a streamed thinking block builds a card whose one-line \"Thinking…\" shows the latest thought and opens to the whole of it; a complete message without streaming shows it too"),
        new("approvals.flow", Job, "Approvals: when Revit asks before a change", How.Probe, "", "each approval level's decision for a new sheet on the test project, nothing asked at Always allow (chat code included) except the issue check's accept, Allow the rest of this task approving the chat task, chat code asked as risky even then, and the package plan not asked twice; a test hook answers, so no dialog shows and your settings are not changed"),
        new("approvals.ui", Job, "Approvals: the dialog, Settings and the chat menu", How.Manual, "", "the dialog, its buttons and the remembered list are what a person sees and clicks",
            "In Settings, under Ask before changes, pick Ask for every change and Save. In Chat, with Brief before running Off, ask \"make a sheet called Approval test\": Revit shows one short dialog (Allow \"Create sheets\"?, one line, Show details) with Allow, Allow the rest of this task, Allow and don't ask again for Create sheets, and Don't allow. Pick Don't ask again; ask for another sheet: no dialog. Open Settings: Create sheets is under Not asked again; press Ask again and Save. Now open the chat's model menu and pick Ask only for risky changes: ask for a third sheet, no dialog, and Ctrl+Z removes it. Ask the chat to delete a wall that has a door: Revit asks, saying the door goes too (Don't allow). Turn Brief before running on, ask for two sheets and press Run this: no approval dialog follows. Set Ask for every change again, ask for one more sheet, tick \"Always allow: don't ask me again\" under the dialog's buttons and press Allow: the sheet is made, and \"Always allow\" shows next to the Brief pill by the message box. Ask the chat to delete the wall with the door again: no dialog, it runs (Ctrl+Z restores it). In the model menu pick Ask only for risky changes: the Always allow label goes away. Delete the test sheets."),
        new("job.ui", Job, "The live job card in the chat", How.Manual, "", "the card's look, the job window over a busy Revit, Undo all and a real conversation need a person (and your plan)",
            "On a _detached copy with briefs on: ask the chat to \"renumber all sheets and fill their title blocks\" and press Run this. A card lists the planned steps; each updates in place with a spinner, then a check, with \"37% (148 of 400 items)\", the time and the time left, a thin bar and \"Now: …\". Open Thinking… to read the thought. While a long step runs, a small task window appears: press Pause (a Paused window appears between items), then Resume; type a message there and check the chat says \"Got it, will apply after this item\" and later \"Applied\". Press Skip step on the next step, then Stop on another run: the item in progress is rolled back and finished items stay. When a one-step task ends, the card collapses to \"Done in…, N elements changed\" with Undo all; change something in the model and check Undo all disappears. Close and reopen the chat: the card shows the same summary."),

        // ---- Time saved
        new("time.tracking", Time, "Working time, AI time and time saved are counted", How.Probe, "", "a scripted session (activity, idle beyond the threshold, AI minutes) counts what is expected, AI plus by hand equals working time with nothing counted twice, and it is in the Done in line, the four Project Information parameters, Extensible Storage and the PC log"),
        new("time.export", Time, "Time saved export to Excel", How.Probe, "", "the model's summary is written as the Excel file the Time saved window exports (to a temporary folder) and read back"),
        new("time.queue", Time, "Nothing is borrowed; parameters wait for ownership", How.Probe, "", "in the workshared test project a write borrows nothing (not Project Information, not a time record) and leaves the parameters alone while this user doesn't own Project Information; with everything owned by someone else (a test hook) the totals stay complete from this user's own record and the PC log; once this user owns Project Information, the next write brings the parameters to the totals"),
        new("time.ui", Time, "Time saved window and rates", How.Manual, "", "the window, the rates and a colleague's real ownership need a person (and a second user)",
            "Run a tool from the ribbon (e.g. Tag elements) and read the \"Done in…, By hand: about…, Saved:…\" line. Click Time saved: check the cards, the per tool, per day and per user tables, and Export to Excel. In Settings change one time rate, save and reopen it. In a workshared copy where you don't own Project Information, run a tool: nothing is borrowed (no new borrowed elements), and the Time saved window shows the full totals and says the Project Information parameters catch up once you own it."),

        // ---- Issue check (the zero-mistake gate)
        new("issue.gate", Issue, "Issue check blocks, then clears after fix or accept", How.Probe, "", "on the test project, a sheet seeded with known faults (no views, empty title block fields) and a section on no sheet: the gate lists them under the sheet and Model and blocks, issue_drawings refuses without changing the model, and export_pdf of the sheet called as from the chat refuses and writes no PDF, while a check print (check_print) is not stopped; filling the fields and placing a view clears the sheet on re-check, accepting the rest with a reason clears the gate, the acceptance is kept for the model, and a finding whose text changes comes back open"),
        new("checklist.word", Issue, "Pre-submission checklist: model hints and the signed Word file", How.Probe, "",
            "a checklist whose checks ask for grids, warnings and imported CAD gets a hint for each from the test project; the Word file is written with the signature picture when every check is Y or NA, and says NOT READY with no picture when one is N"),
        new("checklist.ui", Issue, "The pre-submission checklist window and the printed checklist", How.Manual, "", "the stages, the cards, the keys, the signature and how the Word file and PDF print are judged by a person",
            "On a _detached copy: Settings > Sign-off: type your name, pick your role, choose your signature picture and Save. Pre-submission checklist (large, next to Chat and Update): it opens on the built-in checklist (57 checks, 7 stages) with no Excel file. " +
            "Project details shows the file names and building from the model; Next goes stage by stage on the left, each with its count. On a card press Y: it turns green and the next check is focused; " +
            "press N: the comment box is focused and says why it is needed. Check the hints under the checks (grids, worksets, warnings) match the model. Close the window and open it again: your answers are still there. " +
            "Review and sign lists what is open; click one to go to it. Create the Word file while one check is No: it says NOT READY with no signatures. Answer everything Y or NA, create it again: it says READY with your signature " +
            "in your row and the other row left to sign by hand. Print it from the window. Click Create PDF: a PDF with the same pages and signatures opens with Open PDF. " +
            "Switch Revit to the dark theme: the window follows it."),
        new("berth.read", Issue, "Berth status: the model read in steps and the berth's Excel", How.Probe, "",
            "a whole-model berth on the test project is read step by step (each step a short Revit call) and gets categories, clashes, drawings, annotation and schedules results; a missing scope box says so instead of failing; the berth's Excel is written with its Status sheet"),
        new("costing.berth", Costing, "Berth costing: the model measured per berth and the three files", How.Probe, "",
            "a berth found by a parameter on the test project is measured step by step (each step a short Revit call), a missing scope box to leave out is said, the built-in BOQ (descriptions with the model's figures), " +
            "costing workbook and Word back-up are made with the berth in them, and the same three made from small project templates"),
        new("costing.ui", Costing, "The berth costing window", How.Manual, "", "the overview, live updating, the editing and the files in the built-in formats and the project's own templates are judged by a person",
            "On a _detached copy with the berth status set up: Berth status > Costing (no ribbon slot of its own). Each berth of the setup shows with a ring; with Follow the model ticked the open model's berths are measured by themselves. " +
            "Pick Berth 9 and type BERTH 9 - PART 1 in Leave out scope boxes and excl. RoRo in Which is. Press Measure the model: a strip shows progress, Revit stays usable, Stop ends it. The ring fills, the cost and US$ / lm show, " +
            "items say model, by hand or worked out, each with its BOQ description holding the berth's pile lengths, tip levels and reinforcement tonnage; Show selects an item's elements. Delete or copy a pile in Revit: " +
            "about 3 seconds later the cost and US$ / lm change by themselves (type in a box meanwhile: it keeps its focus). Type a quantity: the cost changes; empty it: back to the model's. Unit costs: change a rate: every berth follows. " +
            "Project details: type the header lines and a document number like PRJ1-B{nn}-BOQ. Untick an item. Tick Berth 9 and Berth 10, Make all three: a costing workbook (summary with US$/lm, a sheet per berth, " +
            "unit costs), a BOQ per berth in the bill layout (header lines, the berth in the footer, no unticked item) and one Word back-up with a table per berth. Templates > Export the BOQ format, change a word in Excel, " +
            "save, Templates > Use an edited BOQ: the next BOQ has the change; back to the built-in format undoes it. Berth status then shows Costing, Bills of quantities and Back-up calculations done."),
        new("berth.ui", Issue, "The berth status window", How.Manual, "", "the overview, the details, hand entries, the Excel and how Revit stays usable while it reads are judged by a person",
            "On a _detached copy: Submissions status (next to the pre-submission checklist; named Berth status before). With no setup it offers Make the setup Excel: it opens in Excel with Models, Berths, Submissions, Planned, Deliverables, Weights and Week sheets. " +
            "Put the copy's file name text on Models, one berth with Find elements by Whole model, a submission next week, save, and Setup > Import the setup Excel: the berth shows with No reading yet. " +
            "Press Update status: a strip shows the progress and you can pan the view in Revit meanwhile; Stop ends it. The berth gets a percentage, its areas fill in, and What to fix lists sheets and views (Show opens them). " +
            "The overview shows one ring per submission of the open model only (red when low, yellow at half, green at 90% and over) with Submitted / In progress / Not started / Overdue; clicking a package of several berths shows a ring per berth below it, and clicking a berth shows its details on the right with its areas as rings (what each measures on hover, click for what to fix). " +
            "Modelling lists only what is missing against the submitted berths (a submitted berth alone is the example, 100%; the BIM Model is the average of its berths); eLOD, iLOD and Assembly Codes are under Categories and data with how to fix each. Fill the Assembly Codes says what it did in a dialog and reads the berth again. " +
            "Press Update status twice with no change in between: the second says No changes and reads nothing. Move one element of one berth, Update status: only that berth is read (the strip names it) and the others keep their results; Setup > Read every berth again reads them all. " +
            "Word report opens one Word file (landscape): an overview table of the submissions, then each with its berths, areas, deliverables, what to fix and comment. " +
            "Coming submissions lists the due time, working time left and, with − / + engineers, the % each engineer needs a working day. " +
            "A berth's deliverables are typed on the berth (the package shows the berths' average); Modelling holds each berth against the submitted berths (what they have, about as many) and lists what is missing. " +
            "A package with two values (Value 5; 6) shows a card for each berth under it, each with its own percentage; the package's areas are their average. " +
            "With two models in the setup, the window shows only the open model's packages. In a piles plan with untagged piles, the Annotation area's Fix tags them; Categories and data's Fill the Assembly Codes fills them from the project checklist's UniFormat rules. " +
            "Type 80 in an area's Set % myself box: the berth's percentage changes. Set an expected finish by hand after the submission: the chip says At risk or Behind. Export this berth to Excel: it opens with Status, Submissions, Deliverables, the areas with findings, Comments and History. " +
            "Close and reopen: everything is as you left it, and nothing is read until you press Update status."),
        new("checklist.views", Issue, "Pre-submission checklist: Show in Revit's checking views are made and deleted", How.Probe, "",
            "a plan with the grids and schedules of sheets, views and title blocks are made in the test project, named Sketch ProMax check - …, found by name and all deleted (rolled back)"),
        new("checklist.show", Issue, "Pre-submission checklist: Show in Revit and the automatic model check", How.Manual, "",
            "opening views and Revit's dialogs from the window, and the two scores, are judged by a person",
            "On a _detached copy: Pre-submission checklist (next to Chat). The blue header shows two rings, Your answers and Automatic check, with their percentages; " +
            "answer a check and Your answers sweeps to the new value. Buttons have icons, lift on hover and the Yes / No / N/A button pops when chosen; pages slide in. " +
            "Set Settings > Chat > animations to Off and reopen: the same changes show at once. On the grids check (in the details panel) click Show the grids: a plan named Sketch ProMax check - Grids opens with the grids, beside the checklist. " +
            "Try Open the warnings (Revit's warnings list opens), Select the imported CAD and List the views (a schedule opens). Answer every check Yes: Review and sign shows " +
            "Your answers 100% and the automatic model check lower, and lists the checks you said Yes to that the model fails. Create the Word file: under the banner it says the " +
            "automatic model check result. Click Delete the checking views: they are gone from the Project Browser, except the one open in front, which it names."),
        new("checklist.fix", Issue, "Pre-submission checklist: the Fix buttons", How.Probe, "",
            "on the test project the Navisworks view is made, imported CAD and views on no sheet are deleted, groups used once are ungrouped and the landing view is set, then all rolled back (unloading links isn't probed: it can't be rolled back)"),
        new("checklist.fix_more", Issue, "Pre-submission checklist: the Fix buttons for title blocks, levels, worksets, backgrounds, view folders, model name, duplicates, submitted copy and NWC", How.Probe, "",
            "on the test project a spare level is deleted, a bare sheet gets the title block, the model name and a view folder value are written, a column copied onto itself is deleted again, " +
            "the worksets and backgrounds fixes run, then all rolled back; the submitted copy is named _S03_R00 then R01, and a package without .ifc exports the NWC only"),
        new("checklist.fix_standards", Issue, "Pre-submission checklist: the Fix buttons for units, UniFormat, URS names, view templates, type names and LOI data, and Select these in Revit", How.Probe, "",
            "on the test project the units are set to mm, every type in use gets an Assembly Code, a floor plan on a sheet gets the mapped template, a type is renamed from the suggestions, " +
            "the URS naming runs and a wall is selected, then all rolled back"),
        new("checklist.autotests", Issue, "Pre-submission checklist: type field, Revit version, free shapes, LOI matrix, sheets from model views, piles, shared coordinates, what must be there, joints", How.Probe, "",
            "each test reads the test project with its rule set and no error; a column copied onto itself is reported as a split pile, column piles report their cut-off and toe range from their levels, missing fenders are reported, the version matches the Revit open, " +
            "and with a package needing no IFC only the NWC name is asked for; rolled back"),
        new("checklist.details", Issue, "Pre-submission checklist: the details panel, beside Revit, and the helper", How.Manual, "",
            "the details panel, side-by-side mode, the Fix confirmations and the helper's answer in the chat are judged by a person",
            "On a _detached copy: Pre-submission checklist (next to Chat), stage A. Each card shows a short title; click one: the panel on the right shows what the model found " +
            "(with the full list, e.g. every level not named to the standard), How to fix, How to check and the checklist's wording, and Yes / No / N/A that follow the card. " +
            "Use the panel's arrows to go through every check. On the grids check click Show the grids: the window becomes a narrow panel at the right of the screen and the plan opens in Revit, " +
            "in view. Back to the full checklist puts the window where it was. On the unused views check click Fix it: it asks first, says what it will delete and that Ctrl+Z undoes it; " +
            "Yes deletes them, the check turns to passes and the automatic score goes up. Click the Automatic check ring: Review and sign opens at the list of checks the model test fails. " +
            "On check 15 click Ask Sketch ProMax to check: the Chat pane shows the question and the answer starts with PASS, FAIL or CAN'T TELL, and nothing in the model changes. " +
            "Settings > Sign-off > Helper = SketchPro: the button copies the question (paste it into Notepad to see it); None: the button is gone."),
        new("checklist.rules", Issue, "Pre-submission checklist: checking rules and the automatic tests they drive", How.Probe, "",
            "every model test of the built-in checklist, the slow ones included, with all checking rules on, reads the test project without an error (the window's rules dialog and the SketchPro paste are in checklist.sketchpro)"),
        new("checklist.files", Issue, "Pre-submission checklist: the checks read from files (MIDP, drawing list, clash report, exports, package)", How.Manual, "",
            "they read the office's own files and Navisworks output, which only a person has; the unit tests check the drawing list, clash report and date logic",
            "On a _detached copy, Checking rules…: set the MIDP file, the drawing list, the Navisworks clash report (XML or HTML), the NWC and IFC folder and the package folder. " +
            "Check 4 finds the model's number in the MIDP; 36 lists drawings in the list with no sheet; 44 counts your open clashes. On check 49 click Fix it: Export the NWC and IFC: " +
            "both files appear in the folder named like the model, and 49, 50 and 51 pass. Create the PDF on Review and sign: its name ends READY or NOT READY and 46 follows it. " +
            "54 lists sheets whose views show a linked model; 55 says N/A for Revit; 56 lists the package's file types; Run the slow checks: 53 and 57 open the packaged model from a clean folder."),
        new("checklist.projects", Issue, "Pre-submission checklist: the Default, a checklist per project, export / import, the Default's password", How.Manual, "",
            "the project checklist windows and the password prompt are judged by a person (ChecklistProjects itself is unit-tested)",
            "On a _detached copy: Pre-submission checklist > Settings (gear). Checklist for this project names the project code from the file name and says it uses the Default checklist. " +
            "Checking rules: change the MIDP file and Save, no password asked. Edit the checklist: the password is asked; 1234 is refused, 0000 opens the editor; Cancel. " +
            "New checklist (a copy of the Default): name it Ports: it has every check of the Default and the editor opens on it with no password; delete a stage and Save: only Ports changes " +
            "(Use for this project > The Default checklist shows all checks again). Export this checklist to Excel, then on a model of another project Import a checklist from Excel: it is named and used there. " +
            "Delete Ports removes it. Make this the Default for everyone: the password is asked, then Documents\\Sketch ProMax\\Default checklist for the next update.json is written. " +
            "Change the password to 4321: the next window asks for 4321, and 0000 is refused."),
        new("checklist.run", Issue, "Pre-submission checklist: nothing is tested until Run checks, with a progress bar", How.Manual, "",
            "how the run strip and its percentage look and read is judged by a person",
            "On a _detached copy: Pre-submission checklist (next to Chat). Under the header a yellow strip says The model hasn't been checked yet, and nothing is tested; " +
            "Final file size (MB) already shows the model's size, marked From the model. Press Run checks: the strip turns blue with a spinner, Reading the model… and a percentage that climbs " +
            "with a bar, and Revit stays usable. Press Run again and then Stop: it stops and the results stay as they were. Let it finish: the strip says Checked at the time, how many pass and need fixing. " +
            "Use a Fix button or save Checking rules: the strip says the results may be out of date and nothing runs until you press Run checks."),
        new("checklist.slow", Issue, "Pre-submission checklist: the slow automatic checks and the new model tests", How.Manual, "",
            "whether the new tests' findings are right on a real model (names, legends, clashes, the standard template) is judged by a person",
            "On a _detached copy: Pre-submission checklist (next to Chat). Checks 18, 19, 38, 43 and 45 say Automatic (slow check): not run yet. Project details > Run the slow checks: " +
            "the button says it is running, then those checks pass or list what they found with the time they ran (43 and 45 name the report file). Read the model again: they keep their results. " +
            "Checking rules…: set The trade's standard template file to your .rte and run the slow checks: 38 lists missing view templates or filters. " +
            "Check 15 lists type names not like *_*_*, 37 the types missing from the legends, 42 the schedules on no sheet, 2 and 5 look for the _S03_R copy, NWC and IFC next to the model."),
        new("checklist.sketchpro", Issue, "Pre-submission checklist: checking rules dialog, manual checks and pasting into SketchPro", How.Manual, "",
            "the rules dialog, the Automatic / You check this labels and pasting into another company's add-in are judged by a person",
            "On a _detached copy: Pre-submission checklist (next to Chat) > Project details: the How this checklist is checked card says how many checks are automatic. " +
            "Each card is marked Automatic or You check this; Review and sign lists the ones you check yourself. Checking rules…: set Approved worksets to your list and Save: " +
            "check 12 becomes automatic and the counts go up. Export the checklist to Excel: it has a Rules sheet; change a rule there and import it back. " +
            "Settings > Sign-off > Helper = SketchPro. On a manual check click Ask SketchPro: SketchPro's Copilot opens and the question is pasted in its message box, not sent. " +
            "Without SketchPro loaded it says so and leaves the question copied."),
        new("checklist.bep", Issue, "Assembly Codes, the BEP data Excel and keeping the model BIM-ready", How.Manual, "", "importing the codes, filling the Excel and the automatic filling are judged by a person on a copy",
            "On a _detached copy, Pre-submission checklist, the Classification code check: Import the Assembly Codes, pick the project's LOI matrix Excel: Manage > Additional Settings > Assembly Code lists its codes with their names (A1020.15 Bored Piles). " +
            "Export the BEP data Excel: it opens with one row per type in use, its code, the suggested code, the LOI matrix's parameters and Still missing. Type a code and a material on a row, save, Import the BEP data Excel: those values are on the type. " +
            "Checking rules: Keep the model BIM-ready while you model = yes. Duplicate a column type and place one: the new type gets its Assembly Code from the rules, and an element placed on Workset1 goes to its category's workset; Ctrl+Z undoes both with the placing. Leave Revit idle a few seconds: the new type's LOI parameters the model gives (material, sizes) are filled. " +
            "On a copy with slightly off axis warnings: Make BIM-ready (or the checklist's Clear the off-axis warnings) clears them and each element's start and end coordinates are unchanged (compare in the properties or with a dimension). " +
            "Draw a wall 0.1° off horizontal: no off-axis warning appears and the wall stays exactly where it was drawn (Manage > Warnings has none for it). " +
            "Submissions status, choose a berth, Make BIM-ready: the window lists what the button fixes, the warnings with what each needs, and the berth's structural clashes and services through the structure first; " +
            "press Make it BIM-ready, Yes: it fills each part with a progress bar (joins the clashes; nothing moves), then lists what is still left; Submissions status and the checklist's checks agree."),
        new("checklist.edit", Issue, "Editing the pre-submission checklist, in Revit and through Excel", How.Manual, "",
            "the editor's lists, moves and confirmations and the Excel round trip in real Excel are judged by a person (the unit tests check the numbering, the answers and the Excel file)",
            "On a _detached copy: answer the first three checks, then Project details > Edit the checklist…: delete check 1, add a check in stage A, move a check down with Ctrl+Down, " +
            "add a stage H with one check, rename it, and Save. The stages on the left show H; checks are numbered 1, 2, 3… again and checks 2 and 3 keep their answers. " +
            "Edit again > Export to Excel…: open it in Excel, change a check's wording, add a row with a number, save; Import from Excel… shows the change; Save. " +
            "Edit again > Use the built-in checklist: back to 57 checks. Try Save with an empty check or a stage with no checks: it says which and selects it."),
        new("checklist.pdf", Issue, "Pre-submission checklist: the PDF is made through Word", How.Probe, "",
            "a ready checklist's Word file is turned into a PDF by Microsoft Word on this PC and the result is a PDF; skipped when Word isn't installed"),
        new("share.pack", Issue, "Share tools: the TritoRevit zip, publishing and live updates", How.Probe, "",
            "a pack of two ribbon tools (and one that needs the AI, left out) is exported to a temporary folder: the zip holds the installer, the readme and the pack; " +
            "it is published to a temporary update folder, installed from the zip, shared again as revision 2 and the installed copy follows it by itself; nothing real on the PC is touched"),
        new("share.ui", Issue, "Share tools and TritoRevit on a PC without Sketch ProMax", How.Manual, "",
            "installing on another Windows user or PC, restarting Revit, the TritoRevit tab and an update arriving with Revit open are judged by a person",
            "On your PC: My tools > Share tools. Name the pack QA tools, keep Pre-submission checklist ticked, tick Model check and one learned tool, pick a folder everyone can reach as the update folder, and Make zip. " +
            "On a PC (or Windows user) without Sketch ProMax and with Revit closed: extract the zip, double-click Install TritoRevit.cmd; it downloads TritoRevit and lists the tools. Open Revit and a _detached copy: " +
            "the TritoRevit tab has a QA tools panel with the three buttons; each runs (the learned tool asks once to approve its code) and the checklist is the built-in one, not yours. " +
            "Share again with Include my checklist ticked: within a few minutes the other PC's checklist is yours; edit yours (add a check) and it follows there too. " +
            "On the other PC edit its checklist, then edit yours again: theirs is kept. " +
            "Leave Revit open. On your PC change the learned tool and wait a few minutes: Revit on the other PC runs the new version, with no restart, " +
            "and Shared tools on the TritoRevit tab shows revision 2. Click Update on the TritoRevit tab: a small window checks and says everything is up to date (or updates), and no Explorer window opens. Share again with Model check unticked: its button goes from the tab within a few minutes. " +
            "On your own PC (Sketch ProMax installed) install the zip too: the TritoRevit tab works through Sketch ProMax. Shared tools > Remove: the panel goes."),
        new("pretest.default", BeforeSharing, "The Default checklist that ships", How.Probe, "",
            "every check has wording, unique id and number, a known automatic test its wording still picks; every stage has checks; rules and fields known; signatories set; JSON and renumbering keep it the same; no e-mail addresses (it is public)"),
        new("pretest.in_use", BeforeSharing, "The checklist this PC uses now", How.Probe, "",
            "read only: whether this PC uses the Default (so updates reach it), an edited copy, a shared pack's or an Excel file; your own checking rules; every project checklist readable; the next Default saved for publishing; whether the password is still 0000"),
        new("pretest.password", BeforeSharing, "The Default's password", How.Probe, "",
            "in a test folder: 0000 works on a new PC, four wrong ones are refused, a new password replaces it and 0000 stops working, and it isn't saved as typed"),
        new("pretest.projects", BeforeSharing, "Checklists per project", How.Probe, "",
            "in a test folder: the project code from the file name; a new checklist is a full copy of the Default; PRJ1 uses it while PRJ2 keeps the Default; deleting a check, adding one, renaming a stage and changing a rule change it only; exported to Excel and imported for PRJ3 the same; deleting it puts PRJ1 back on the Default; names Windows refuses are cleaned"),
        new("pretest.own_rules", BeforeSharing, "Your own checking rules on the Default", How.Probe, "",
            "in a test folder: your rules are saved apart (the Default isn't copied), read back on it, a newer Default with an extra check arrives with your rules on it, and a rule set back is dropped"),
        new("pretest.excel", BeforeSharing, "Excel export and import of the whole Default", How.Probe, "",
            "every check's wording, short title, how to fix, priority, number and automatic test, the stages, fields, signatories and every rule survive Excel, and an edited copy comes back edited"),
        new("pretest.next_default", BeforeSharing, "Make this the Default for everyone", How.Probe, "",
            "in a test folder: the file saved for publishing is in Documents\\Sketch ProMax, reads back whole, carries nothing of this PC and only known tests"),
        new("pretest.answers", BeforeSharing, "Answers, the score and what is still open", How.Probe, "",
            "all Yes or N/A is 100% with nothing listed; a No and a gap are both listed and lower the score; a check answered N/A is removed (out of the score, the open list and the files, the checklist itself unchanged) and Put back brings it back; answers follow their checks when one is deleted and the rest renumbered"),
        new("pretest.word", BeforeSharing, "The whole Default as the Word file and PDF", How.Probe, "",
            "every check of the Default is in the Word file, READY with a signature per signatory when all answered; NOT READY and unsigned with one No; a check answered N/A left out with the rest READY; the PDF through Word when it is installed"),
        new("pretest.reading", BeforeSharing, "Run checks on a model", How.Probe, "",
            "on the throwaway project: the whole Default read in the window's short steps with no test failing, no step holding Revit over 2 s, the check names for the progress bar, Final file size filled, the slowest tests, then the slow checks"),
        new("pretest.windows", BeforeSharing, "The checklist's windows", How.Probe, "",
            "on the throwaway project, built without showing: the checklist (Default and own), its settings (Default and own), the editor (Default and own) and checking rules"),
        new("pretest.ports_rules", BeforeSharing, "Piles, detail lines and title blocks (the ports job's rules)", How.Probe, "",
            "on the throwaway project: columns named by the piles rule (or below the lowest level) aren't floating; detail lines test plans only by default and skip views by name; a sheet with no title block is listed and the landing page family isn't a title block"),
        new("pretest.share_rules", BeforeSharing, "When a shared pack replaces a colleague's checklist", How.Probe, "",
            "a colleague with none gets the pack's, the pack's own copy is updated, and one they edited, one from another pack or their own Excel file is kept"),
        new("pretest.share_export", BeforeSharing, "Share tools with everything on this PC", How.Probe, "",
            "in a test folder: every window, ribbon tool, learned tool and recipe on this PC goes in the pack (learned tools and recipes exactly as saved), button texts fit, the zip has the installer, readme, pack and buttons, and lists every tool"),
        new("pretest.share_install", BeforeSharing, "A colleague installs and follows the pack", How.Probe, "",
            "in a test folder standing for a colleague's PC: the zip installs, the pack's checklist arrives, the next revision follows from the update folder, a checklist they edited and their project checklists are kept, a problem report reaches the update folder, the Documents folder is named after the pack, Remove takes it away"),
        new("share.bugs", Issue, "TritoRevit problem reports on a colleague's PC", How.Manual, "",
            "the folder in Documents, the report window and the copy reaching the update folder are seen on another PC",
            "On the PC with TritoRevit (no Sketch ProMax), with the pack named Pre Submission Checklist: after Revit starts, Documents has a folder TritoRevit - Pre Submission Checklist " +
            "with Bugs inside and How to report a problem.txt. Open the checklist on a _detached copy, press Report a problem in its footer, type one line and Save report: a zip appears in Bugs " +
            "(screenshot, report.md naming TritoRevit and the pack revision) and Explorer shows it. On your PC, Bug reports in the pack's update folder has the same zip with the user's name in front. " +
            "Shared tools on the TritoRevit tab > Report a problem… does the same. Create the checklist PDF: it is saved in TritoRevit - Pre Submission Checklist, not in Sketch ProMax."),
        new("issue.report", Issue, "Transmittal with the issue record and pass/fail report", How.Case, "issue_drawings", "issue_drawings with open items let through writes the transmittal workbook (drawings, the issue record saying it was overridden, the pass/fail report)"),
        new("issue.chat_export", Issue, "Export PDF of sheets from the chat runs the issue check first", How.Manual, "",
            "from the chat the checks and the export run as a job over several Revit calls, which the field test (itself one job) can't start; its probe checks the same refusal in one call",
            "On a _detached copy with a sheet that has an open item (Issue > Issue check lists one): in the chat ask \"export sheet <number> to PDF\". The job card shows the issue check's four checks one by one and Revit stays usable between them; the chat says it was not exported and names the open item; no PDF is written. Accept the item in the chat with a reason (or fix it) and ask again: the PDF is written. Ask to export a floor plan view (not a sheet): it is exported at once, with no check. Ask for the sheet again and press Stop while it checks: nothing is exported."),
        new("issue.ui", Issue, "The issue check window", How.Manual, "", "the window, Fix taking you to the element and the ribbon flow are what a person sees and does",
            "On a _detached copy: Issue > Issue check. The list shows each sheet PASS or FAIL with its items, and Model. Press Fix on an item: the window closes and the element is selected and shown; fix it and click Issue check again: it is gone. Tick two items, type a reason, Accept: they show as accepted and Issue stays off while anything is open. Accept the rest: Issue turns on; press it and the Issue drawings options open. Run Issue drawings from the ribbon on a sheet with an open item: the window opens first. Run Export PDF on that sheet: the same. In Settings tick \"Allow issue with open items\": the window offers it in red, needs a reason, and the transmittal's Issue record says YES with the reason. Ask in the chat to check the issue: it lists pass/fail per sheet and accepts only with your reason."),

        // ---- Drawing package
        new("package.sentence", Package, "A drawing package from one sentence", How.Probe, "", "on the test project, \"GA plans, sections and column schedule for levels 1-2 on A1 with our title block\" is planned (counts) and made: every planned view, each stage's check passes (views exist, every view placed, nothing overlaps, no orphan tags), the register sheet and its Excel copy, the time-saved line, and every view and sheet that was there before is unchanged; a step only the user can do is handed over (answered Skip); rolled back after"),
        new("package.skip", Package, "Drawing package: Skip step skips one stage", How.Probe, "", "Skip pressed at the second view ends the Views stage with one view kept and says so; the skip is used up there, so the Sheets stage still runs and the step ends done"),
        new("package.real", Package, "Drawing package on a real model", How.Manual, "", "how the drawings look, the office title block and tag families need a person on a real project",
            "On a _detached copy: Documents > Sheets & views > Drawing package, type \"GA plans, sections and column schedule for levels 1-3 on A1 with our title block\" and run it. Check the counts in the dialog, then the sheets: views inside the frame, tags and grid dimensions readable, the register sheet lists the drawings and the Excel file opens. Press Undo once: the whole package goes and your own views and sheets are still there. In the chat, ask for the same sentence and press Skip step during the Tags stage: the sheets are still made."),

        // ---- Carbon, VE, 2D rebar
        new("carbon.mix", Carbon, "Carbon with and without the mix in the material name", How.Case, "embodied_carbon", "a slab whose material names the mix and one whose doesn't (assumed CEM I) are both assessed"),
        new("carbon.real", Carbon, "Carbon on a real model", How.Manual, "", "the factors and mapping need an engineer's eye on a real project",
            "On a _detached copy: Check > Embodied carbon > Embodied carbon. Open the Excel file; check the unmapped list and the top contributors make sense. Run Compare options with 50% GGBS and Carbon 3D view."),
        new("ve.apply_copy", Ve, "VE apply on a copy", How.Probe, "", "ve_apply target copy runs in a non-workshared copy the field test opened, and the saved copy is checked"),
        new("ve.workshared", Ve, "VE apply refuses to copy a workshared model", How.Case, "ve_apply", "on the workshared test project, target copy is refused with a clear message"),
        new("ve.design_option", Ve, "VE apply on a design option", How.Manual, "", "the Revit API can't create design options, so the test project has none",
            "On a _detached copy: Manage > Design Options, make an option set with an option and copy a slab into it. Check > Value engineering > Apply to a copy with target design_option and the option's name. Check only the option's slab changed."),
        new("ve.real", Ve, "VE on a real model", How.Manual, "", "savings need an engineer's judgement",
            "On a _detached copy: Check > Value engineering > VE opportunities, then Compare systems and VE register. Read the ranked list and the Excel register."),
        new("rebar2d", Rebar2D, "2D rebar with detail families", How.Case, "place_additional_reinforcement", "additional bars, mesh symbols and column section details are placed with families the field test makes"),
        new("rebar2d.real", Rebar2D, "2D rebar with your office families", How.Manual, "", "needs the office's own detail families from your template",
            "On a _detached copy with your template's detail families: Reinforcement > 2D rebar families, then Additional bars (2D) on a slab plan, Mesh symbols (2D) and Column sections (2D). Check the drawings look right."),

        // ---- Cost plan and what if
        new("cost.template", Costing, "Rate table template", How.Probe, "", "cost_estimate template true writes the documented template (Rates first, Read me); it is read back and its rows chosen by region"),
        new("cost.plan", Costing, "Cost plan from a rate-table xlsx with add-ons", How.Probe, "", "a small rate table (two regions, a row in EUR) made as .xlsx by the field test costs the test project with region, currency, exchange rate, prelims, contingency, escalation by date and VAT; the total matches the add-on arithmetic, the rates per m² GFA and per m³ are there, the Excel cost plan links each line to its Basis rows, and the summary card is short"),
        new("cost.remembered", Costing, "The rate table is remembered per model", How.Probe, "", "a second run with no rates_file uses the table, region, currency and add-ons remembered for the test project"),
        new("cost.options", Costing, "Options side by side on cost, carbon and programme", How.Probe, "", "the model, a flat slab and a steel frame swapped in for its floors are compared on total cost with the add-ons, carbon and frame weeks, against the first"),
        new("whatif.preview", Costing, "What if: preview first, nothing changes", How.Probe, "", "what_if without confirm on the test project lists the plan, the current figures and the drawings affected; the model's element count and the slab's type are the same after"),
        new("whatif.workshared", Costing, "What if refuses a workshared model", How.Case, "what_if", "on the workshared test project, the run with confirm is refused with a clear message (detach a copy, or use a design option)"),
        new("whatif.copy", Costing, "What if on a copy, side by side", How.Probe, "", "in a plain copy of the test project: a slab made 100 mm thicker on a saved copy; the side-by-side shows more concrete and cost, the Excel file has the drawings affected, and the open model's slab keeps its type"),
        new("whatif.link", Costing, "What if in the cost plan's options", How.Probe, "", "cost_estimate options [model, what_if] in the same copy reads the saved what-if and sets it against the model"),
        new("cost.real", Costing, "Cost plan with the firm's rate table on a real model", How.Manual, "", "the rates, the region and the quantities need a cost consultant's eye on a real project",
            "On a _detached copy: Check > Value engineering > Cost plan with the firm's rate table, its region and currency, and the usual prelims, contingency, escalation dates and VAT. Open the Excel cost plan: click a line's BOQ basis link and check the Basis rows; check the rates per m² GFA (and its source), per level and zone, per m of walls, beams, piles and railings, per m³ and per tonne. For a marine model, draw model lines in a line style named Quay and check the per m of quay rate. Run it again without the table: it is remembered."),
        new("whatif.design_option", Costing, "What if in a design option", How.Manual, "", "the Revit API can't create design options, so the test project has none",
            "On a _detached copy: Manage > Design Options, make an option set with an option and copy a slab into it. Ask the chat for a what-if on that slab's thickness with target design_option: a preview comes first; agree, and check only the option's slab changed and the side-by-side and drawings list make sense."),
        new("whatif.ask", Costing, "What if asks before it runs", How.Manual, "", "the approval dialog and the chat's question are what a person sees",
            "In the chat, ask \"what if the slabs were 250 mm?\" on a _detached copy: the chat shows the preview and asks; say yes: Revit asks to try it on a copy (at every level but Always allow, even with change approvals off in Settings). Say no once and check nothing was copied."),

        // ---- Report bug and exports
        new("bugs.report", Bugs, "Report bug zip size and contents", How.Case, "report_bug", "the zip is written to a temporary Bugs folder, is small and holds the description, activity and versions"),
        new("bugs.export", Bugs, "Export chats & bugs zip", How.Case, "export_chats_and_bugs", "the zip holds the (test) chats, reports and versions and is small"),
        new("pack.export", Bugs, "Export project pack zip", How.Case, "export_project_pack", "the project pack zip is written, is small and holds the project data"),
        new("bugs.sync", Bugs, "Bug log sync and \"Not sent yet\"", How.Probe, "", "the published bug log downloads, and a report it doesn't list shows under \"Not sent yet\""),
        new("bugs.ui", Bugs, "Report bug, Bug folder and Export chats from the ribbon", How.Manual, "", "the buttons ask questions and open Explorer",
            "Click Report bug, type a line and send it; the Bugs folder opens with the zip. Click Bug folder. Click Export chats, pick Today, and check the zip in Exports. Click Export project pack and check the zip opens."),
        new("exports.ui", Bugs, "Export PDF, list of drawings and quantities", How.Manual, "", "the files are opened and read by a person",
            "On a _detached copy: Issue > Export PDF for two sheets, List of drawings and Quantities / BOQ. Open each file."),

        // ---- Updates and the Claude app
        new("update.endpoint", Updates, "Update check endpoint", How.Probe, "", "the releases page answers and names a version"),
        new("update.version", Updates, "Version compare", How.Probe, "", "this version and the latest release compare the way the Update button shows them"),
        new("update.loader", Updates, "Loader path and version", How.Probe, "", "the loader that started this core is found, with its file version and contract"),
        new("claude.msix", Updates, "Microsoft Store Claude app detection", How.Probe, "", "looks for the Store (MSIX) Claude app's package and config folder, and the normal install"),
        new("claude.open", Updates, "Open Claude app launches the app", How.Manual, "", "starting the desktop app is visible only to you",
            "Close the Claude desktop app, then More > Open Claude app. The app starts (also for the Microsoft Store version)."),

        // ---- Redlines in, revised sheets out
        new("redlines.pdf", Redlines, "A marked-up PDF page read as an image", How.Probe, "", "a test sheet exported to PDF is rasterised by Revit (no PDF library) and redline_read returns the page as an image, ready to be read"),
        new("redlines.pipeline", Redlines, "Redlines applied, clouded, revised and exported", How.Probe, "",
            "a test sheet's image gets five markups drawn on it (new text, a parameter note with an arrow, a strike-through, a new note, a cloud with no instruction); " +
            "redline_apply, with the known markup JSON standing in for the assistant's reading, matches each to the right viewport and element, asks to approve each change, " +
            "applies them, clouds them under a new revision, exports the revised sheet to PDF, hands the cloud over, and the Excel and chat report say 4 done, 1 not done"),
        new("redlines.vision", Redlines, "The drawn markups are read", How.Probe, "", "with chat_round_trip only (it uses your plan): Claude Code reads the marked-up test page and finds the markups, the strike-through among them"),
        new("redlines.real", Redlines, "Redlines from a real PDF or photo", How.Manual, "", "handwriting, real drawings and phone photos vary more than a drawn test page, and the approval dialogs are judged on screen",
            "On a _detached copy: print a sheet, mark it up by hand (change a note, cross something out, write a new dimension, cloud a question), photograph it with a phone. " +
            "In Chat, attach the photo and ask \"apply these redlines\": Sketch ProMax reads it (redline_read), lists the planned changes (redline_plan) and, when you agree, Revit asks " +
            "about each change one by one (Skip is the default; try Skip and Stop asking). Check the clouds, the new revision on the sheet, the exported PDF, the manual steps " +
            "for what it couldn't read, and the done / not done table in the chat and in Excel. Then Issue > Redlines to sheets on the ribbon with a marked-up PDF of two pages: a message says Claude Code is reading them; while it reads, Revit stays usable (pan a view) and the Job button shows the page it is on. Then Revit asks about the changes and the same results follow. Run it again and click the Job button during the reading: it stops, nothing in the model changes and the result says so."),

        // ---- Real-model checks
        new("model.joined_faces", Model, "Hosting on faces of joined elements", How.Case, "place_void_cuts", "a void is hosted on the top face of a slab joined to a beam"),
        new("model.void_rebuild", Model, "Clash check void rebuild", How.Case, "find_clashes", "a cutting void family is rebuilt as a solid in a clash run"),
        new("model.real_hosting", Model, "Hosting and voids on your model", How.Manual, "", "joined geometry on real projects varies more than a test project can show",
            "On a _detached copy: place a void cut on a slab joined to beams (Clashes > Void cuts) and run a clash check with voids on. Check the void cuts and is reported."),

        // ---- Purge and cleanup
        new("cleanup.passes", Cleanup, "Purge finds what only unused things used, previews, then deletes", How.Probe, "", "in the test project, a new material used only by a new unused wall type: purge_unused previews the type without deleting anything (no transaction: a preview that deleted families closed Revit 2024) and says applying finds more; with apply both are deleted, the material in a later pass; rolled back after"),
        new("cleanup.workshared", Cleanup, "Purge leaves what someone else owns", How.Probe, "", "in the workshared test project, with an unused type made to look owned by another user (a test hook), purge_unused lists it under left alone, owned by someone else, and does not delete it"),
        new("cleanup.ui", Cleanup, "Purge & cleanup from the ribbon on a real model", How.Manual, "", "what is safe to delete on a real project, the file size after saving and the Revit dialogs need a person",
            "On a _detached copy: Check > Purge & cleanup, pick Purge unused, leave the options and Run. Read the preview (counts by kind, what is left alone and why, the file size): nothing has changed yet. Press Close without deleting and check nothing went. Run it again and press Delete them: the counts drop; press Ctrl+Z once and everything is back. Run Delete unused views with keep set to your working views' prefix, Clean up imports and Model health clean the same way. Save the copy with Compact ticked and compare the file size. In the chat, ask \"purge this model\": the chat shows the preview first and asks before deleting."),

        // ---- Shared models: the central model
        new("central.once", Central, "One request to the central model, then a pause when it doesn't answer", How.Probe, "",
            "in the test project, with four new types made to look in central and free (a test hook) and the request made to fail as if the central model were busy and then unreachable: a batch asks once and once more, every item says why it wasn't done, a second batch asks nothing during the pause, and a batch stops after an item fails while \"Unable to Access the Model\" shows, keeping the item done before it"),
        new("central.real", Central, "Fewer \"Unable to Access the Model\" dialogs on a shared model", How.Manual, "", "needs a real central model and a colleague (or a second Revit) synchronizing at the same moment",
            "On a workshared test model (not the live project): open a local copy in two Revit sessions. In the first, start Synchronize with Central; while it runs, ask the chat in the second to rename a few doors (or run any batch edit on elements nobody owns). Revit shows \"Unable to Access the Model\" at most once: press Cancel. The result says the central model didn't answer and when Sketch ProMax will ask again, and no more dialogs follow. Run the same edit again before that time: it stops at once with the same message and no dialog. After that time it runs normally."),

        // ---- Bug fixes checked by a person (the rest are field-test cases named "tool bug#N", see BugChecks)
        new("bug.titleblock_params", BugFixes, "Bug #7: a copied title block fills its fields", How.Manual, "", "needs the office's own title block family, whose labels use shared parameters",
            "On a _detached copy of a model without your title block: ask in the chat to copy the title block from your template (copy_from_file). Put it on a new sheet and fill the sheet's drawn by, checked by and other fields: the title block shows them (the shared parameters were added to Sheets)."),
        new("bug.newer_link", BugFixes, "Bug #13: linking a model from a newer Revit", How.Manual, "", "needs a model saved in a newer Revit than this one",
            "In Revit 2024 or 2025, ask in the chat to link a model saved in a newer Revit (link_model): the answer says the model is from a newer Revit and must be linked in that version (or saved back), not an internal error."),
        new("bug.always_load", BugFixes, "Bug #17: Always Load is remembered across updates", How.Manual, "", "Revit's security dialog only shows at Revit start",
            "Start Revit; if it asks about the Sketch ProMax add-in, pick Always Load. Install an update (Updates panel), close Revit and start it again: it does not ask again."),
        new("update.loader_fixed", Updates, "The loader stays at one place (bug #17)", How.Probe, "", "the loader that started this core is the fixed one in the add-in folder (not inside a version folder), and the .addin file points at it"),
        new("startup.timings", Updates, "Startup stays out of the way (bug #18)", How.Probe, "", "the time Sketch ProMax took at Revit start, and what it added to opening models, are under the limits (3 s, 1 s)"),
        new("update.fallback", Updates, "The update check falls back to github.com (bug #23)", How.Probe, "", "with GitHub's API made to fail, the check still finds the latest version from the github.com releases page"),
        new("update.resume", Updates, "An interrupted update continues where it stopped", How.Probe, "", "on this PC's disk, an unpack stopped part way is continued (the files already written are kept) and the finished version is marked so Try again goes straight to installing"),

        // ---- Revit limits
        new("legend.components", Limits, "Legend components copied from one placed by hand", How.Manual, "", "the Revit API can't place a legend component from scratch, and the test project's template has none to copy (the field test looks in the Revit templates first)",
            "On a _detached copy, open a legend and place one legend component by hand (Annotate > Component > Legend Component). Then run Add legend components for two wall types: both appear in the legend, each with its type name as a label."),
        new("matchlines.trim", Limits, "Trimming a real matchline", How.Manual, "", "the Revit API can't create a matchline, so the field test only checks the tool on a dependent-view pair with none",
            "On a _detached copy with dependent views and a matchline: ask the chat to trim the matchlines in a plan (trim_matchlines). The redrawn lines stop at the crop."),
    ];

    /// <summary>
    /// Every ribbon and pane command (and pane tab, as "tab:name") the core declares, with the feature it belongs to.
    /// Commands "tool:&lt;name&gt;" are covered by the tool itself and by ribbon.dialogs; "fav:N" slots by favourites.
    /// </summary>
    public static readonly Dictionary<string, string> Ribbon = new(StringComparer.Ordinal)
    {
        ["chat"] = "chat.ui", ["status"] = "ribbon.status", ["send-view"] = "ribbon.status", ["copy-prompt"] = "ribbon.status",
        ["open-claude"] = "claude.open", ["pane"] = "ribbon.status", ["export-pack"] = "pack.export", ["job-button"] = "ribbon.job",
        ["update-button"] = "ribbon.job", ["report-bug"] = "bugs.ui", ["split-pdf"] = "chat.split_ui", ["bug-folder"] = "bugs.ui", ["export-chats-bugs"] = "bugs.export", ["license"] = "license.ui",
        ["settings"] = "settings.ui", ["help"] = "help", ["self-test"] = "diagnostics", ["field-test"] = "diagnostics",
        ["test-checklist"] = "diagnostics", ["time-saved"] = "time.ui", ["issue-check"] = "issue.ui", ["pre-submission"] = "checklist.ui", ["berth-status"] = "berth.ui", ["cleanup"] = "cleanup.ui", ["share-tools"] = "share.ui", ["favourites"] = "favourites", ["fav:*"] = "favourites", ["tab:notes"] = "chat.notes",
    };

    /// <summary>The coverage key of a manifest line, or null when it runs nothing (drop-downs, hidden items, tool entries).</summary>
    internal static string? RibbonKey(string line)
    {
        var f = line.Split('\t');
        if (f.Length < 3) return null;
        if (f[0] == "tab") return "tab:" + f[2];
        if (f[0] is not ("button" or "item" or "pane") || f.Length < 5 || f[4].Length == 0) return null;
        var command = f[4];
        if (command.StartsWith("tool:", StringComparison.Ordinal)) return null;
        return Regex.IsMatch(command, @"^fav:\d+$") ? "fav:*" : command;
    }

    /// <summary>Where a tool stands: "tested", "manual (checklist)" or null when the table has nothing for it.</summary>
    internal static string? ToolStatus(string tool, ICollection<string> caseTools) =>
        caseTools.Contains(tool) || FieldSteps.Contains(tool) ? "tested" : ManualTools.ContainsKey(tool) ? "manual (checklist)" : null;

    /// <summary>What the unit test checks: every gap, one line each (empty when coverage is complete).</summary>
    internal static List<string> Gaps(IEnumerable<string> tools, ICollection<string> caseTools, IEnumerable<string> manifestLines, string probeSource)
    {
        var gaps = new List<string>();
        foreach (var t in tools)
            if (ToolStatus(t, caseTools) is null) gaps.Add($"tool {t}: no field-test case and no ManualTools row");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in Features)
        {
            if (!keys.Add(f.Key)) gaps.Add($"feature {f.Key}: listed twice");
            if (f.How == How.Probe && probeSource.IndexOf("\"" + f.Key + "\"", StringComparison.Ordinal) < 0) gaps.Add($"feature {f.Key}: no probe in FieldTest.Features.cs");
            if (f.How == How.Case && !caseTools.Contains(f.By)) gaps.Add($"feature {f.Key}: {f.By} has no self-test case");
            if (f.How == How.Manual && (f.Reason.Length == 0 || f.Steps.Length == 0)) gaps.Add($"feature {f.Key}: manual without a reason and steps");
        }
        foreach (var pair in ManualTools)
            if (pair.Value.Reason.Length == 0 || pair.Value.Steps.Length == 0) gaps.Add($"tool {pair.Key}: manual without a reason and steps");
        foreach (var line in manifestLines)
            if (RibbonKey(line) is { } key && !(Ribbon.TryGetValue(key, out var feature) && keys.Contains(feature)))
                gaps.Add($"ribbon {key}: no Ribbon row naming a feature");
        return gaps;
    }

    // ---- The guided checklist --------------------------------------------------------------------------------------------

    /// <param name="Unchanged">Set when the item's code is unchanged since the last full run's version: it is not asked again
    /// (the window lists it apart, collapsed), with this note. Empty: an item to check.</param>
    internal sealed record CheckItem(string Id, string Area, string Title, string Steps, string Why, string Unchanged = "");

    internal sealed record BugRow(int Number, string What, string Status);

    private static readonly Regex Row = new(@"^\|\s*(\d+)\s*\|[^|]*\|([^|]*)\|[^|]*\|([^|]*)\|", RegexOptions.CultureInvariant);

    /// <summary>The rows of a bug log (docs/bug-log.md format) whose status says "Fixed in", in number order.</summary>
    internal static List<BugRow> FixedBugs(string? markdown)
    {
        var rows = new Dictionary<int, BugRow>();
        foreach (var line in (markdown ?? "").Replace("\r", "").Split('\n'))
        {
            var m = Row.Match(line.Trim());
            if (!m.Success || !int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) continue;
            var status = m.Groups[3].Value.Trim();
            if (status.IndexOf("Fixed in", StringComparison.OrdinalIgnoreCase) < 0) continue;
            rows[n] = new BugRow(n, m.Groups[2].Value.Trim(), status);
        }
        return rows.Values.OrderBy(r => r.Number).ToList();
    }

    /// <summary>
    /// The checklist: every manual feature and tool, then one step per fixed bug row, in area order. Features in
    /// <paramref name="unchanged"/> (code unchanged since the last full run, FieldTestProven) carry <paramref name="note"/>
    /// and come last, so the list to check is the new and changed items.
    /// </summary>
    internal static List<CheckItem> Checklist(IEnumerable<BugRow> fixedBugs, IReadOnlyCollection<string>? unchanged = null, string note = "")
    {
        var items = Features.Where(f => f.How == How.Manual)
            .Select(f => new CheckItem(f.Key, f.Area, f.Title, f.Steps, f.Reason, unchanged?.Contains(f.Key) == true ? note : "")).ToList();
        items.AddRange(ManualTools.OrderBy(t => t.Key).Select(t => new CheckItem("tool:" + t.Key, Limits, t.Key, t.Value.Steps, t.Value.Reason)));
        string[] order = [Chat, RibbonArea, Tasks, Job, Time, Issue, Package, Cleanup, Central, Carbon, Ve, Costing, Redlines, Rebar2D, Bugs, Updates, Model, Limits, BugFixes];
        items = items.OrderBy(i => Array.IndexOf(order, i.Area) is var x && x < 0 ? order.Length : x).ToList();
        items.AddRange(fixedBugs.Select(b => new CheckItem("bug:" + b.Number, BugFixes, $"#{b.Number}: {b.What}",
            "Do what the row describes on a _detached copy and check it no longer happens." +
            (BugChecks.FirstOrDefault(c => c.Row == b.Number) is { } check ? $" The field test checks it too ({check.What}): see its Bug log section." : ""), b.Status)));
        // Stable: the items to check keep their order, the unchanged ones follow in theirs.
        return items.OrderBy(i => i.Unchanged.Length > 0 ? 1 : 0).ToList();
    }

    // ---- The bug log rows, each with what proves its fix still holds ---------------------------------------------------------

    /// <summary>The field-test row name of a self-test case that guards bug log row <paramref name="row"/>: "tool bug#N".</summary>
    internal static string BugTag(int row) => "bug#" + row.ToString(CultureInfo.InvariantCulture);

    /// <param name="Row">The bug log row (docs/bug-log.md).</param>
    /// <param name="Refs">What checks it: "bug#N" (a self-test case with Bug = N), "tool:name" (that tool's cases) or a <see cref="Features"/> key.</param>
    /// <param name="What">In a few words, what the check does.</param>
    internal sealed record BugCheck(int Row, string[] Refs, string What);

    /// <summary>
    /// Every bug log row and what fails if it comes back. A new row in docs/bug-log.md with "Fixed in" needs a row here
    /// (the unit test fails otherwise); the field test report's Bug log section is built from it.
    /// </summary>
    public static readonly BugCheck[] BugChecks =
    [
        new(1, ["ribbon.registered", "chat.update_reopen"], "every ribbon entry is there; after a real update, the checklist"),
        new(2, ["bug#2"], "arch_to_structure on a link with cladding columns and finish floors"),
        new(3, ["bug#3"], "create_raft: hosted on the nearest level, named by type_name"),
        new(4, ["bug#4"], "place_views_on_sheet finds the title block's drawing area"),
        new(5, ["bug#5"], "create_plan_views structural: engineering plans looking down"),
        new(6, ["bug#6"], "tag_elements_in_view: one tag per column stack, outside the column"),
        new(7, ["bug.titleblock_params"], "checklist: the office title block's shared parameters"),
        new(8, ["bug#8"], "loading_plan: the legend is a table"),
        new(9, ["bug#9", "tool:wait_for_job"], "export_pdf to a locked file names the reason; wait_for_job runs"),
        new(10, ["bug#10"], "run_revit_query_code makes a level and it is rolled back"),
        new(11, ["revit.blocked", "revit.blocked_ui"], "a request Revit didn't start is picked up or explained"),
        new(12, ["bug#12"], "edit_family_text adds text and reloads without an invalid object"),
        new(13, ["bug#13", "bug.newer_link"], "link_model refuses a file it can't read with the reason"),
        new(14, ["bug#14"], "capture_view_image: a sheet region at full resolution"),
        new(15, ["bug#15"], "check_model: tolerance and exceptions for off-grid columns"),
        new(16, ["bug#16", "rebar2d"], "2D rebar with detail families"),
        new(17, ["update.loader_fixed", "bug.always_load"], "the loader stays in one place"),
        new(18, ["startup.timings"], "startup time and model opening"),
        new(19, ["chat.frames", "chat.dpi", "chat.smooth"], "chat frame times and screenshots"),
        new(20, ["bug#20"], "arch_to_structure says why nothing was made"),
        new(21, ["chat.history", "chat.models", "chat.prompts"], "chat history, model picker and saved prompts"),
        new(22, ["tool:ve_opportunities", "tool:ve_compare_options", "tool:ve_apply", "tool:ve_register"], "the value engineering tools"),
        new(23, ["update.fallback", "update.endpoint"], "the update check with GitHub's API failing"),
        new(24, ["bug#24", "tool:load_steel_sections"], "load_family refuses a bad type catalog with the reason"),
        new(25, ["chat.frames", "chat.dpi"], "the chat draws replies with the reply-header mark"),
        new(26, ["bug#26"], "title_block_from_cad on the field test's DWG"),
        new(27, ["bug#27"], "find_clashes with a void-only cutting family"),
        new(28, ["issue.gate"], "the issue check on seeded faults"),
        new(29, ["redlines.pipeline"], "redlines matched to the right viewport and element"),
        new(30, ["chat.composer", "chat.typing"], "the message box: text starts where the hint was"),
        new(31, ["revit.blocked", "revit.blocked_ui"], "the busy card names no edit mode when there is none"),
        new(32, ["approvals.flow"], "approvals at each level"),
        new(33, ["chat.survives_update", "chat.update_reopen"], "the open chat survives an update"),
        new(34, ["central.once", "central.real"], "one borrow request per batch, then a pause, on a shared model"),
        new(35, ["chat.shrink"], "after a shrink the chat relies on the summary and never rereads the old transcript"),
        new(36, ["cleanup.passes", "diagnostics.journal", "diagnostics.crash_ui"], "the purge preview deletes nothing; a run Revit closes during is reported and continued after"),
        new(37, ["diagnostics.running_call"], "the call Revit closed during is read back at the next start with its arguments"),
        new(38, ["issue.gate"], "export_pdf with check_print isn't stopped by the issue check"),
        new(39, ["bug#39"], "export_pdf looks a saved View/Sheet Set up by name"),
        new(40, ["chat.tools_ready"], "a new chat process has the Revit tools before its first message"),
        new(41, ["tool:query_elements"], "a family asked in the wrong category names the categories where it is"),
        new(42, ["tool:list_types"], "Detail Groups and Slab Edges are known categories"),
        new(43, ["tool:capture_view_image"], "zoom to elements in a section or detail view"),
        new(44, ["tool:duplicate_view_to_location"], "a copied dimension that loses its references is left out, not the whole copy"),
        new(45, ["tool:arrange_tags"], "a view with no tags says what it has instead"),
        new(46, ["berth.ui"], "after Setup > Import the setup Excel the window shows the berths, not a blank page"),
        new(47, ["berth.read"], "a pair Revit cannot intersect is skipped, the clash area still gets its result"),
        new(48, ["berth.ui"], "opened on one model, only that model's submissions show (not every model of the setup)"),
    ];

    /// <summary>
    /// What the unit test checks for the bug log: every fixed row has a <see cref="BugChecks"/> row, and every reference
    /// resolves (a self-test case tagged with that bug, a tool with a case, a feature).
    /// </summary>
    internal static List<string> BugGaps(IEnumerable<BugRow> fixedRows, ICollection<int> caseBugs, ICollection<string> caseTools)
    {
        var gaps = new List<string>();
        var keys = new HashSet<string>(Features.Select(f => f.Key), StringComparer.Ordinal);
        foreach (var row in fixedRows)
            if (!BugChecks.Any(c => c.Row == row.Number)) gaps.Add($"bug #{row.Number}: no BugChecks row");
        foreach (var c in BugChecks)
            foreach (var r in c.Refs)
            {
                if (r.StartsWith("bug#", StringComparison.Ordinal))
                {
                    if (!int.TryParse(r.Substring(4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n != c.Row) gaps.Add($"bug #{c.Row}: {r} is not its own tag");
                    else if (!caseBugs.Contains(n)) gaps.Add($"bug #{c.Row}: no self-test case has Bug = {n}");
                }
                else if (r.StartsWith("tool:", StringComparison.Ordinal))
                {
                    if (!caseTools.Contains(r.Substring(5))) gaps.Add($"bug #{c.Row}: {r} has no self-test case");
                }
                else if (!keys.Contains(r)) gaps.Add($"bug #{c.Row}: {r} is not a feature");
            }
        return gaps;
    }

    /// <summary>
    /// What one reference proved in a run: "pass", "fail", "manual" (a checklist step) or "not run". Rows are (name, status)
    /// of the report: "tool bug#N" for a tagged case, "tool" or "tool #2" for a tool's cases, the feature key for a probe.
    /// </summary>
    internal static string RefStatus(string reference, IReadOnlyCollection<(string Name, string Status)> rows)
    {
        IEnumerable<(string Name, string Status)> mine;
        if (reference.StartsWith("bug#", StringComparison.Ordinal))
            mine = rows.Where(r => r.Name.EndsWith(" " + reference, StringComparison.Ordinal));
        else if (reference.StartsWith("tool:", StringComparison.Ordinal))
        {
            var tool = reference.Substring(5);
            mine = rows.Where(r => r.Name == tool || r.Name.StartsWith(tool + " ", StringComparison.Ordinal));
        }
        else if (Features.FirstOrDefault(f => f.Key == reference) is { } feature)
        {
            if (feature.How == How.Manual) return "manual";
            mine = feature.How == How.Case ? rows.Where(r => r.Name == feature.By || r.Name.StartsWith(feature.By + " ", StringComparison.Ordinal)) : rows.Where(r => r.Name == reference);
        }
        else return "not run";
        var list = mine.ToList();
        if (list.Any(r => r.Status == "fail")) return "fail";
        // Proven: passed in the last full run, with code unchanged since (FieldTestProven).
        if (list.Any(r => r.Status is "pass" or "proven")) return "pass";
        if (list.Any(r => r.Status == "manual")) return "manual";
        return "not run";
    }

    /// <summary>
    /// The report's Bug log section: each row of the bug log, what checks it, how that went, and what to do with the row
    /// (mark it Confirmed, keep it open, or finish the checklist step).
    /// </summary>
    internal static string BugLogMarkdown(IEnumerable<BugRow> fixedRows, IReadOnlyCollection<(string Name, string Status)> rows)
    {
        var sb = new StringBuilder("## Bug log\n\n");
        sb.Append("Each fixed row of the bug log with the check that fails if the bug comes back. Passed: the row can be marked Confirmed. " +
                  "A checklist step is in Sketch ProMax tab > Help > Test checklist.\n\n");
        sb.Append("| # | What happened | Checked by | Result | The row |\n|---|---|---|---|---|\n");
        foreach (var bug in fixedRows)
        {
            var check = BugChecks.FirstOrDefault(c => c.Row == bug.Number);
            if (check is null) { sb.Append($"| {bug.Number} | {Cell(bug.What)} | nothing yet | **not checked** | {Cell(bug.Status)} |\n"); continue; }
            var results = check.Refs.Select(r => (Ref: r, Status: RefStatus(r, rows))).ToList();
            var auto = results.Where(r => r.Status != "manual").ToList();
            string result, verdict;
            if (results.Any(r => r.Status == "fail")) { result = "**fail**"; verdict = "keep open: the fix has regressed"; }
            else if (auto.Count > 0 && auto.All(r => r.Status == "pass"))
            {
                result = "pass";
                verdict = results.Any(r => r.Status == "manual") ? "mark Confirmed once the checklist step passes too" : "mark Confirmed";
            }
            else if (auto.Any(r => r.Status == "pass")) { result = "partly run"; verdict = "not all checks ran: run the rest, then mark Confirmed"; }
            else if (results.Any(r => r.Status == "manual")) { result = "checklist"; verdict = "mark Confirmed when the checklist step passes"; }
            else { result = "not run"; verdict = "not checked in this run"; }
            var by = string.Join(", ", results.Select(r => $"{r.Ref} ({r.Status})"));
            sb.Append($"| {bug.Number} | {Cell(bug.What)} | {Cell(check.What)}: {Cell(by)} | {result} | {verdict} |\n");
        }
        return sb.ToString();
    }

    internal sealed record Mark(string Id, string Result, string Note);

    /// <summary>The result file: a short Markdown table, small enough to paste or attach.</summary>
    internal static string ResultMarkdown(IReadOnlyList<CheckItem> items, IReadOnlyDictionary<string, Mark> marks, string version, string revit, string model, DateTime when)
    {
        int Count(string r) => items.Count(i => marks.TryGetValue(i.Id, out var m) && m.Result == r);
        var unchanged = items.Count(i => i.Unchanged.Length > 0 && !(marks.TryGetValue(i.Id, out var m) && m.Result.Length > 0));
        var sb = new StringBuilder();
        sb.AppendLine("# Sketch ProMax test checklist").AppendLine();
        sb.AppendLine($"- Date: {when.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- Sketch ProMax {version}, Revit {revit}");
        if (model.Length > 0) sb.AppendLine($"- Model: {model}");
        sb.AppendLine($"- Result: {Count("pass")} pass, {Count("fail")} fail, {items.Count - Count("pass") - Count("fail") - unchanged} not checked" +
                      (unchanged > 0 ? $", {unchanged} unchanged (not asked again)" : "")).AppendLine();
        sb.AppendLine("| Item | Area | Result | Note |").AppendLine("|---|---|---|---|");
        foreach (var i in items)
        {
            marks.TryGetValue(i.Id, out var m);
            var result = m is null || m.Result.Length == 0 ? (i.Unchanged.Length > 0 ? "unchanged" : "not checked") : m.Result == "fail" ? "**fail**" : m.Result;
            sb.AppendLine($"| {Cell(i.Title)} | {i.Area} | {result} | {Cell(m?.Note is { Length: > 0 } n ? n : m is null || m.Result.Length == 0 ? i.Unchanged : "")} |");
        }
        return sb.ToString();
    }

    private static string Cell(string s) => s.Replace("|", "/").Replace("\r", "").Replace("\n", " ");
}
