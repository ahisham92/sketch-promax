using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Autodesk.Revit.UI.Events;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;
using TaskDialogCommonButtons = Autodesk.Revit.UI.TaskDialogCommonButtons;
using TaskDialogResult = Autodesk.Revit.UI.TaskDialogResult;
using UIApplication = Autodesk.Revit.UI.UIApplication;
using SketchProMax.Bridge;
using SketchProMax.Tools;
using SketchProMax.UI;

namespace SketchProMax.Chat;

/// <summary>
/// The Chat tab of the Sketch ProMax pane: a conversation with Claude through Claude Code, signed in with the user's
/// Claude account (so it runs on their plan). The loader owns the dockable pane and asks the running core for this
/// element through CoreEntry.PaneContent("chat"); after a version switch the new core's element takes its place and
/// resumes the same conversation.
///
/// Claude reaches Revit only through the sketch-promax MCP server, i.e. the same bridge, job queue and approval dialog
/// as the Claude desktop app. Everything slow (finding Claude Code, starting it, reading its output, encoding pictures)
/// runs off Revit's UI thread.
/// </summary>
internal sealed partial class ChatPane : DockPanel, IHistoryHost
{
    // ---- Lifecycle (called by App and CoreEntry on Revit's UI thread) ---------------------------------------------

    private static ChatPane? _instance, _retired;
    private static string _modelKey = "", _modelTitle = "";
    private static EventHandler<ViewActivatedEventArgs>? _viewActivated;

    /// <summary>True while a turn runs: switching to a new core version waits for it.</summary>
    public static bool IsBusy => _instance?._turn is not null;

    /// <summary>The pane's chat element (created on first use).</summary>
    public static FrameworkElement Get() => _instance ??= new ChatPane();

    public static void Start(UIApplication app)
    {
        try
        {
            if (app.ActiveUIDocument?.Document is { } doc) SetModel(doc);
            _viewActivated = (_, e) =>
            {
                try { if (e.Document is { } d) SetModel(d); }
                catch (Exception) { /* a document being closed: keep the current one */ }
            };
            app.ViewActivated += _viewActivated;
        }
        catch (Exception) { /* the chat still works, with one conversation */ }
        // Started again after a version switch that failed: the pane still shows this core's element, so revive it.
        if (_instance is null && _retired is { } pane)
        {
            _instance = pane;
            _retired = null;
            pane._stopped = false;
            UI.RestartNote.Changed -= pane.ShowRestartNote;
            UI.RestartNote.Changed += pane.ShowRestartNote;
            pane._idle.Start();
            Brand.UseTheme(pane);
            pane.ShowStatus();
            pane.ModelChanged();
        }
    }

    /// <summary>The core is stopping (switching versions): end the engine and save; the new core resumes the session.</summary>
    public static void Stop(UIApplication app)
    {
        try { if (_viewActivated is not null) app.ViewActivated -= _viewActivated; }
        catch (Exception) { }
        _viewActivated = null;
        _instance?.Retire();
    }

    /// <summary>Revit is closing.</summary>
    public static void Shutdown()
    {
        if (_instance is not { } pane) return;
        pane.Save();
        pane._engine?.KillNow();
        pane._engine = null;
    }

    /// <summary>The key a model's conversation is saved under (ChatStore): its path, or its title when never saved.</summary>
    internal static string ModelKey(Autodesk.Revit.DB.Document doc) => string.IsNullOrEmpty(doc.PathName) ? doc.Title : doc.PathName;

    /// <summary>The open model's key (see <see cref="ModelKey"/>), "" before one is open: memory notes are kept under it.</summary>
    internal static string CurrentModelKey => _modelKey;

    internal static string CurrentModelTitle => _modelTitle;

    private static void SetModel(Autodesk.Revit.DB.Document doc)
    {
        var key = ModelKey(doc);
        if (string.Equals(key, _modelKey, StringComparison.OrdinalIgnoreCase)) return;
        _modelKey = key;
        _modelTitle = doc.Title;
        // Switching conversations reads and lays out the saved one: at the next idle moment, not inside Revit's model
        // open or view activation (bug #18). It reads the current key then, so several switches collapse into one.
        if (_instance is not null) SketchProMax.App.Dispatcher.Later(() => _instance?.ModelChanged());
    }

    // ---- State ----------------------------------------------------------------------------------------------------

    private enum Setup { Checking, Missing, Installing, SignedOut, SigningIn, Ready, Failed }

    private sealed class Turn
    {
        public string Line = "";
        public bool Stopping, Retried, SawText, SawTool, ModelRetried;
        public DispatcherTimer? KillTimer;
        /// <summary>True while the prompt enhancer reads the model to make a brief (nothing is sent to the chat yet).</summary>
        public bool Planning;
        public CancellationTokenSource? Cancel;
        /// <summary>How the turn ended for the job card: done, or failed when Claude Code reported an error.</summary>
        public string Outcome = "done";
        /// <summary>The brief this task runs ("" when it runs as typed or had none): see ChatPane.ActiveBrief.</summary>
        public string Brief = "";
    }

    private sealed class Attachment
    {
        public string Path = "", Name = "";
        public bool IsImage => ImageType(Path) is not null;
    }

    private sealed class ToolRow
    {
        public ChatItem Item = new();
        public ContentControl Mark = new();
        public ToggleButton Header = new();
        public TextBlock Line = new(), Chevron = new();
        public TextBlock Detail = new();
        public StackPanel Details = new();
    }

    private const int MaxImages = 8, MaxResult = 3000, MaxIdLinks = 20;
    private static readonly FontFamily Icons = ChatUi.Icons;

    private Conversation _conv;
    private ChatEngine? _engine;
    private ClaudeCli.Install? _install;
    private Setup _setup = Setup.Checking;
    private string _setupDetail = "";
    private Turn? _turn;
    private bool _stopped, _checking, _scrollQueued;
    private int _firstShown; // the first conversation item drawn (see History)
    private readonly ScrollFollow _follow = new();
    private readonly Throttle _throttle = new(TimeSpan.FromMilliseconds(40));
    private (string Key, string Title)? _pendingModel;
    private DateTime _lastActivity = DateTime.UtcNow, _waitingSince;
    private readonly List<Attachment> _pending = [];
    private readonly Dictionary<string, (ChatItem Item, MarkdownView View)> _texts = new();
    private readonly Dictionary<string, ToolRow> _tools = new();
    private readonly Dictionary<ChatItem, MarkdownView> _views = new();
    private readonly Dictionary<ChatItem, ToolRow> _rows = new();
    private readonly HashSet<string> _dirty = [];

    // ---- Controls -------------------------------------------------------------------------------------------------

    private readonly TextBlock _modelLine, _chatName;
    private readonly Button _newChat, _refresh, _send, _stop, _attach, _view, _menu, _prompts, _modelPill, _briefPill;
    private readonly TextBlock _briefLabel = new() { FontSize = ChatUi.SmallSize, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _promptPanel = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, ChatUi.S2) };
    private List<SavedPrompt>? _saved;
    private HistoryDrawer? _drawer;
    private readonly Border _scrim = new() { Visibility = Visibility.Collapsed };
    private readonly Grid _center = new();
    private readonly Popup _modelMenu = new() { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Custom };
    private readonly StackPanel _modelList = new() { Margin = new Thickness(ChatUi.S1) };
    private readonly TextBlock _modelLabel = new() { FontSize = ChatUi.SmallSize, VerticalAlignment = VerticalAlignment.Center };
    private bool _refreshAsked;
    private DateTime _modelMenuClosed;
    private string _model = ChatModels.Load(ClaudeCli.Folder), _lastWorkingModel = "";
    private readonly Border _setupCard = new() { Visibility = Visibility.Collapsed };
    private readonly StackPanel _messages = new();
    private readonly ScrollViewer _scroll;
    private readonly UIElement _empty;
    private readonly Button _earlier, _jump;
    private readonly TextBlock _earlierText = new() { FontSize = ChatUi.SmallSize, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _jumpText = new() { FontSize = ChatUi.SmallSize, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _working = new() { Visibility = Visibility.Collapsed, Padding = new Thickness(0, ChatUi.S4, 0, ChatUi.S1) };
    private readonly FrameworkElement _shimmer = ChatUi.Shimmer();
    private bool _gliding, _workingShown, _shimmerShown, _drawerOpen;
    private readonly bool _sample;
    private readonly TextBlock _workingText = Brand.Text("", Brand.K.Muted, ChatUi.SmallSize);
    private readonly TextBox _input;
    private readonly WrapPanel _chips = new();
    private readonly DockPanel _statusRow = new() { Margin = new Thickness(0, 0, 0, 4) };
    private readonly ContentControl _statusSpin = new() { Focusable = false, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status;
    private readonly DispatcherTimer _render = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly DispatcherTimer _idle = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly Border _restartNote = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _restartNoteText = Brand.Text("", Brand.K.Text, 12);

    private void ShowRestartNote()
    {
        var note = UI.RestartNote.Text;
        _restartNoteText.Text = note ?? "";
        _restartNote.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(_restartNote, note ?? "");
    }

    private ChatPane() : this(null) { }

    /// <summary>The pane; with <paramref name="sample"/> a stand-alone copy showing that conversation (the DPI snapshot), which looks for nothing and saves nothing.</summary>
    private ChatPane(Conversation? sample)
    {
        Brand.UseTheme(this);
        Resources.MergedDictionaries.Add(ChatUi.Styles());
        _sample = sample is not null;
        if (sample is null) Tools.Handoff.ChatPresenter = r => _instance is { } pane && pane.ShowHandoff(r);
        SetResourceReference(BackgroundProperty, Brand.K.Bg);
        SetResourceReference(TextElement.ForegroundProperty, Brand.K.Text);
        TextElement.SetFontSize(this, ChatUi.BodySize);
        TextElement.SetFontFamily(this, ChatUi.Text);
        // Sharp lines and text: whole-pixel layout, and text laid out for the screen (Display) with ClearType where the
        // pane allows it. The greeting, being large, uses Ideal (HomeView).
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
        ChatUi.Opaque(this); // an opaque background: ClearType holds even where a child fades or moves
        // Tab order follows the reading order: header, notices, conversation, then the message box.
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Local);
        LastChildFill = true;
        _conv = sample ?? ChatStore.Load(ConversationKey, ConversationTitle);

        // Top: the history menu, this chat's name with the Revit model as a muted caption, then New chat and Refresh.
        var top = new DockPanel { Margin = new Thickness(ChatUi.S2, ChatUi.S2, ChatUi.S2, ChatUi.S2) };
        _menu = ChatUi.IconButton(ChatUi.Paths.Menu, "Chat history", "Your chats: search, open, rename, pin, export or delete them.");
        _menu.Margin = new Thickness(0, 0, ChatUi.S1, 0);
        _menu.Click += (_, _) => ToggleDrawer();
        DockPanel.SetDock(_menu, Dock.Left);
        top.Children.Add(_menu);
        _refresh = ChatUi.IconButton(ChatUi.Paths.Refresh, "Check Claude Code again", "Checks again that Claude Code is installed and signed in.");
        _refresh.Click += (_, _) =>
        {
            _refreshAsked = true;
            Status("Checking Claude Code…", spin: true);
            Check(quiet: false);
        };
        DockPanel.SetDock(_refresh, Dock.Right);
        top.Children.Add(_refresh);
        _newChat = ChatUi.IconButton(ChatUi.Paths.Plus, "New chat", "New chat. This one stays in the history (the menu at the top left).");
        _newChat.Margin = new Thickness(ChatUi.S1, 0, 0, 0);
        _newChat.Click += (_, _) => NewChat();
        DockPanel.SetDock(_newChat, Dock.Right);
        top.Children.Add(_newChat);
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(ChatUi.S1, 0, 0, 0) };
        _chatName = ChatUi.Small("", Brand.K.Text, bold: true);
        _chatName.FontSize = ChatUi.BodySize;
        names.Children.Add(_chatName);
        _modelLine = ChatUi.Small("");
        _modelLine.FontSize = ChatUi.SmallSize;
        names.Children.Add(_modelLine);
        top.Children.Add(names);
        SetDock(top, Dock.Top);
        KeyboardNavigation.SetTabIndex(top, 0);
        Children.Add(top);

        SetDock(_setupCard, Dock.Top);
        KeyboardNavigation.SetTabIndex(_setupCard, 1);
        _setupCard.Margin = new Thickness(ChatUi.S3, ChatUi.S1, ChatUi.S3, ChatUi.S1);
        Children.Add(_setupCard);

        // "Restart Revit to see the new buttons and pane" after an in-session update that couldn't show everything.
        SetDock(_restartNote, Dock.Top);
        KeyboardNavigation.SetTabIndex(_restartNote, 2);
        _restartNote.Margin = new Thickness(ChatUi.S3, 0, ChatUi.S3, ChatUi.S1);
        _restartNote.Padding = new Thickness(ChatUi.S3, ChatUi.S2, ChatUi.S3, ChatUi.S2);
        _restartNote.BorderThickness = new Thickness(1);
        _restartNote.CornerRadius = new CornerRadius(ChatUi.RadiusSmall);
        _restartNote.SetResourceReference(Border.BackgroundProperty, Brand.K.NoticeBg);
        _restartNote.SetResourceReference(Border.BorderBrushProperty, Brand.K.NoticeBorder);
        _restartNote.Child = _restartNoteText;
        AutomationProperties.SetLiveSetting(_restartNote, AutomationLiveSetting.Polite);
        Children.Add(_restartNote);
        ShowRestartNote();
        if (sample is null) UI.RestartNote.Changed += ShowRestartNote;

        // "Sketch ProMax 0.32.0 is available" / "Updated to ...": the same message as the update toast (UI/UpdateNotice).
        var updateBand = UI.UpdateNotice.Banner();
        SetDock(updateBand, Dock.Top);
        KeyboardNavigation.SetTabIndex(updateBand, 2);
        Children.Add(updateBand);

        // Bottom: the saved prompts (when asked for), then one rounded card holding the attachments, the message box and
        // a row of icon buttons with the model and Send; the status line under it.
        var composer = new StackPanel { Margin = new Thickness(ChatUi.S3, ChatUi.S2, ChatUi.S3, ChatUi.S3) };
        KeyboardNavigation.SetTabIndex(composer, 4);
        KeyboardNavigation.SetTabNavigation(composer, KeyboardNavigationMode.Local);
        _status = Brand.Text("", Brand.K.Muted, ChatUi.SmallSize);
        _status.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        DockPanel.SetDock(_statusSpin, Dock.Left);
        _statusRow.Children.Add(_statusSpin);
        _statusRow.Children.Add(_status);
        _statusRow.Margin = new Thickness(ChatUi.S2, ChatUi.S2, ChatUi.S2, 0);
        composer.Children.Add(_promptPanel);
        _input = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 44, MaxHeight = 160, Padding = new Thickness(ChatUi.S3, ChatUi.S3, ChatUi.S3, ChatUi.S1),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            SpellCheck = { IsEnabled = true }, FontSize = ChatUi.BodySize,
        };
        _input.SetResourceReference(StyleProperty, "SpmChatBare");
        AutomationProperties.SetName(_input, "Message to Claude");
        AutomationProperties.SetHelpText(_input, "Enter sends, Shift+Enter starts a new line, Escape stops Claude. Paste a picture or a file to attach it.");
        _input.ToolTip = "Enter sends. Shift+Enter: new line. Paste or drop pictures, PDFs and drawings to attach them.";
        _input.PreviewKeyDown += OnInputKey;
        CommandManager.AddPreviewExecutedHandler(_input, OnPreviewPaste);
        CommandManager.AddPreviewCanExecuteHandler(_input, OnPreviewCanPaste);
        // A hint inside the empty box (the box's name and help text say the same for screen readers).
        var hint = Brand.Text("Ask Sketch ProMax…", Brand.K.Muted, ChatUi.BodySize);
        hint.IsHitTestVisible = false;
        hint.Margin = new Thickness(ChatUi.S3 + 2, ChatUi.S3, ChatUi.S3, 0);
        _input.TextChanged += (_, _) => hint.Visibility = _input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _input.TextChanged += (_, _) => { if (_turn is not null) ShowSteer(); };
        var field = new Grid();
        field.Children.Add(_input);
        field.Children.Add(hint);

        var buttons = new DockPanel { Margin = new Thickness(ChatUi.S1, 0, ChatUi.S2, ChatUi.S2) };
        _attach = ChatUi.IconButton(ChatUi.Paths.Plus, "Attach files", "Attach pictures, PDFs or drawings (DWG, DXF). You can also paste or drop them here.");
        _attach.Click += (_, _) => PickFiles();
        _prompts = ChatUi.IconButton(ChatUi.Paths.Bookmark, "Saved prompts", "Your saved prompts: pick one to put it in the message box. Save one from any message with ⋯ › Save prompt.");
        _prompts.Click += (_, _) => TogglePrompts();
        AutomationProperties.SetItemStatus(_prompts, "Collapsed");
        _view = ChatUi.IconButton(ChatUi.Paths.Camera, "Attach the current view", "Attaches a picture of the active Revit view, as Revit shows it now.");
        _view.Click += (_, _) => AttachView();
        // Send is a round accent arrow; while Claude works a round red Stop square takes its place (never the default).
        _send = new Button { Content = ChatUi.Icon(ChatUi.Paths.Send, stroke: 2), ToolTip = "Send (Enter)" };
        _send.SetResourceReference(StyleProperty, "SpmChatSend");
        AutomationProperties.SetName(_send, "Send message");
        AutomationProperties.SetHelpText(_send, "Sends your message to Claude (Enter).");
        _send.Click += (_, _) => Send();
        _stop = new Button { Content = ChatUi.Icon(ChatUi.Paths.Square, fill: true), Visibility = Visibility.Collapsed };
        _stop.SetResourceReference(StyleProperty, "SpmChatStop");
        _stop.ToolTip = "Stops Claude's answer (Escape). A Revit change already made stays; undo it with Ctrl+Z.";
        AutomationProperties.SetName(_stop, "Stop Claude's answer");
        AutomationProperties.SetHelpText(_stop, _stop.ToolTip as string ?? "");
        _stop.Click += (_, _) => StopTurn();
        DockPanel.SetDock(_send, Dock.Right);
        DockPanel.SetDock(_stop, Dock.Right);
        // The model for the next message: a pill left of Send that opens a small menu.
        _lastWorkingModel = _model;
        var pill = new StackPanel { Orientation = Orientation.Horizontal };
        pill.Children.Add(_modelLabel);
        var chevron = ChatUi.Icon(ChatUi.Paths.ChevronUp);
        chevron.Margin = new Thickness(2, 0, -4, 0);
        pill.Children.Add(chevron);
        _modelPill = new Button { Content = pill, Margin = new Thickness(0, 0, ChatUi.S2, 0), VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(ChatUi.S3, ChatUi.S1, ChatUi.S3, ChatUi.S1) };
        _modelPill.SetResourceReference(StyleProperty, "SpmChatPill");
        _modelPill.SetResourceReference(BackgroundProperty, Brand.K.Raised);
        _modelPill.Click += (_, _) => ToggleModelMenu();
        AutomationProperties.SetItemStatus(_modelPill, "Collapsed");
        DockPanel.SetDock(_modelPill, Dock.Right);
        BuildModelMenu();
        ShowModelChoice();
        // "Brief before running" next to the model, so the setting is in sight where the message is sent: a quiet pill
        // that opens the same menu at its brief section.
        var briefIcon = ChatUi.Icon(ChatUi.Paths.Sparkle, 14);
        briefIcon.Margin = new Thickness(0, 0, ChatUi.S1, 0);
        var briefRow = new StackPanel { Orientation = Orientation.Horizontal };
        briefRow.Children.Add(briefIcon);
        briefRow.Children.Add(_briefLabel);
        _briefPill = new Button { Content = briefRow, Margin = new Thickness(0, 0, ChatUi.S1, 0), VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(ChatUi.S2, ChatUi.S1, ChatUi.S2, ChatUi.S1) };
        _briefPill.SetResourceReference(StyleProperty, "SpmChatGhost");
        _briefPill.Click += (_, _) => ToggleModelMenu(brief: true);
        AutomationProperties.SetItemStatus(_briefPill, "Collapsed");
        DockPanel.SetDock(_briefPill, Dock.Right);
        ShowBriefChoice();
        _attach.Margin = _prompts.Margin = _view.Margin = new Thickness(0, 0, 2, 0);
        buttons.Children.Add(_send);
        buttons.Children.Add(_stop);
        buttons.Children.Add(_modelPill);
        buttons.Children.Add(_briefPill);
        buttons.Children.Add(_attach);
        buttons.Children.Add(_prompts);
        buttons.Children.Add(_view);
        // Keyboard order follows the reading order (left to right), not the docking order.
        KeyboardNavigation.SetTabNavigation(buttons, KeyboardNavigationMode.Local);
        KeyboardNavigation.SetTabIndex(_attach, 1);
        KeyboardNavigation.SetTabIndex(_prompts, 2);
        KeyboardNavigation.SetTabIndex(_view, 3);
        KeyboardNavigation.SetTabIndex(_briefPill, 4);
        KeyboardNavigation.SetTabIndex(_modelPill, 5);
        KeyboardNavigation.SetTabIndex(_stop, 6);
        KeyboardNavigation.SetTabIndex(_send, 7);
        buttons.Children.Add(new Border()); // fills the middle

        var inside = new StackPanel();
        _chips.Margin = new Thickness(ChatUi.S3, ChatUi.S3, ChatUi.S3, 0);
        inside.Children.Add(_chips);
        inside.Children.Add(field);
        inside.Children.Add(buttons);
        var card = ChatUi.Elevated(inside, out var frame);
        frame.Padding = new Thickness(1);
        // The card shows the box's focus: a 2 px ring in the focus colour (the hairline alone is too faint to find).
        _input.IsKeyboardFocusedChanged += (_, _) =>
        {
            var focused = _input.IsKeyboardFocused;
            frame.BorderThickness = new Thickness(focused ? 2 : 1);
            frame.Padding = new Thickness(focused ? 0 : 1);
            frame.SetResourceReference(Border.BorderBrushProperty, focused ? Brand.K.Focus : Brand.K.BorderSubtle);
        };
        // A click on the card's empty parts goes to the box, as in a text field.
        frame.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource == frame || e.OriginalSource == inside || e.OriginalSource is Border { Child: null }) _input.Focus();
        };
        composer.Children.Add(card);
        composer.Children.Add(_statusRow);
        composer.Children.Add(_modelMenu);
        SetDock(composer, Dock.Bottom);
        Children.Add(composer);

        // The conversation: "Show earlier", the messages, the typing indicator; "Jump to latest" floats over its foot.
        _earlier = ChatUi.Ghost(ChatUi.GlyphUp, null, "Show earlier messages", "Shows the earlier messages of this conversation.");
        _earlier.Content = Row(ChatUi.Glyph(ChatUi.GlyphUp, Brand.K.Muted, 10), _earlierText);
        _earlier.HorizontalAlignment = HorizontalAlignment.Center;
        _earlier.Margin = new Thickness(0, ChatUi.S1, 0, 0);
        _earlier.Click += (_, _) => ShowEarlier();
        var home = new HomeView(UsePrompt, () => Saved);
        PromptsChanged += home.SavedChanged;
        _empty = home;
        // What Claude is doing: the typing dots and a line, and until the first words a shimmering placeholder.
        var dots = ChatUi.Dots();
        var working = new DockPanel();
        DockPanel.SetDock(dots, Dock.Left);
        working.Children.Add(dots);
        working.Children.Add(_workingText);
        var workingStack = new StackPanel();
        workingStack.Children.Add(working);
        _shimmer.Visibility = Visibility.Collapsed;
        workingStack.Children.Add(_shimmer);
        _working.Child = workingStack;
        AutomationProperties.SetLiveSetting(_working, AutomationLiveSetting.Polite);
        var conversation = new StackPanel { Margin = new Thickness(ChatUi.S4, 0, ChatUi.S4, ChatUi.S8) };
        conversation.Children.Add(_earlier);
        conversation.Children.Add(_empty);
        conversation.Children.Add(_messages);
        conversation.Children.Add(_working);
        _scroll = new ScrollViewer
        {
            Content = conversation, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = true, IsTabStop = true,
        };
        _scroll.SetResourceReference(FocusVisualStyleProperty, "SpmChatFocus");
        AutomationProperties.SetName(_scroll, "Conversation");
        AutomationProperties.SetHelpText(_scroll, "Arrow keys and Page Up/Down scroll; End goes to the latest message.");
        _scroll.ScrollChanged += (_, e) =>
        {
            // The list's own glide to the bottom isn't the reader scrolling (the wheel, keys and a click stop it).
            if (!_gliding || e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0)
                _follow.Scrolled(_scroll.VerticalOffset, _scroll.ScrollableHeight, e.ExtentHeightChange, e.ViewportHeightChange);
            if (_follow.Following && (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0)) ScrollToEnd();
            else UpdateJump();
        };
        // Reading back while Claude writes: stop following at once, before the next streamed text pulls the list down.
        _scroll.PreviewMouseWheel += (_, e) =>
        {
            if (e.Delta > 0 && _scroll.VerticalOffset > 0) _follow.ReaderScrolledUp();
        };
        // A click in the list (the scroll bar, say) takes over from the glide.
        _scroll.PreviewMouseDown += (_, _) => StopGlide();
        _scroll.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.PageUp or Key.Up or Key.Home && _scroll.VerticalOffset > 0 && e.OriginalSource == _scroll) _follow.ReaderScrolledUp();
        };
        var jumpIcon = ChatUi.Icon(ChatUi.Paths.ArrowDown);
        _jump = new Button { Content = Row(jumpIcon, _jumpText), Visibility = Visibility.Collapsed };
        _jump.SetResourceReference(StyleProperty, "SpmChatFloat");
        _jump.HorizontalAlignment = HorizontalAlignment.Center;
        _jump.VerticalAlignment = VerticalAlignment.Bottom;
        _jump.Margin = new Thickness(0, 0, 0, ChatUi.S3);
        _jump.ToolTip = "Scrolls to the newest message and follows Claude's answer again (End).";
        _jump.Click += (_, _) => ScrollToEnd(force: true);
        _jumpText.Text = "Jump to latest";
        AutomationProperties.SetName(_jump, "Jump to latest");
        _center.Children.Add(_scroll);
        _center.Children.Add(_jump);
        // The history drawer slides over the conversation; the dimmed rest closes it when clicked.
        _scrim.Background = Brand.Brush(Color.FromArgb(0x55, 0, 0, 0));
        _scrim.MouseDown += (_, _) => CloseDrawer();
        _center.Children.Add(_scrim);
        KeyboardNavigation.SetTabIndex(_center, 3);
        Children.Add(_center);

        // Escape stops Claude from anywhere in the chat (the message box handles it itself first).
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || e.Handled || _turn is null) return;
            e.Handled = true;
            StopTurn();
        };

        // Files dropped anywhere on the chat are attached.
        AllowDrop = true;
        PreviewDragOver += (_, e) =>
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        };
        PreviewDrop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
            e.Handled = true;
            AddFiles(files);
        };

        _render.Tick += (_, _) => RenderDirty();
        _poll.Tick += (_, _) => Check(quiet: true);
        _idle.Tick += (_, _) => IdleCheck();
        if (sample is not null) _setup = Setup.Ready;
        else _idle.Start();

        ShowConversation();
        ShowSetup();
        ShowStatus();
        if (sample is not null) return;
        Check(quiet: false);
        Task.Run(CleanAttachments);
    }

    /// <summary>Pasted pictures and captured views are kept two weeks (a resumed conversation may refer to them).</summary>
    private static void CleanAttachments()
    {
        try
        {
            if (!Directory.Exists(ClaudeCli.AttachmentsFolder)) return;
            foreach (var file in Directory.GetFiles(ClaudeCli.AttachmentsFolder))
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-14)) File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string ConversationKey => _modelKey.Length > 0 ? _modelKey : "(no model)";

    private static string ConversationTitle => _modelTitle.Length > 0 ? _modelTitle : "no model open";

    // ---- The model pill and its menu -----------------------------------------------------------------------------

    /// <summary>The menu over the model pill: one row per model with its hint, a check on the chosen one.</summary>
    private void BuildModelMenu()
    {
        var card = ChatUi.Elevated(_modelList, out _, ChatUi.Radius);
        card.Margin = new Thickness(ChatUi.S3); // room for the shadow inside the transparent popup
        card.MinWidth = 220;
        card.MaxWidth = 300;
        _modelMenu.Child = card;
        _modelMenu.PlacementTarget = _modelPill;
        // Above the pill, right edges aligned (below it when there is no room above).
        _modelMenu.CustomPopupPlacementCallback = (popup, target, _) =>
        [
            new CustomPopupPlacement(new Point(target.Width - popup.Width + ChatUi.S3, -popup.Height + ChatUi.S3 - 2), PopupPrimaryAxis.Horizontal),
            new CustomPopupPlacement(new Point(target.Width - popup.Width + ChatUi.S3, target.Height - ChatUi.S3 + 2), PopupPrimaryAxis.Horizontal),
        ];
        KeyboardNavigation.SetTabNavigation(_modelList, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetDirectionalNavigation(_modelList, KeyboardNavigationMode.Cycle);
        AutomationProperties.SetName(_modelList, "Claude model");
        _modelList.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            CloseModelMenu(focusPill: true);
        };
        _modelMenu.Closed += (_, _) =>
        {
            _modelMenuClosed = DateTime.UtcNow;
            AutomationProperties.SetItemStatus(_modelPill, "Collapsed");
            AutomationProperties.SetItemStatus(_briefPill, "Collapsed");
            ShowBriefChoice();
        };
    }

    private void ToggleModelMenu(bool brief = false)
    {
        if (_modelMenu.IsOpen)
        {
            CloseModelMenu(focusPill: true);
            return;
        }
        // A click on the pill while the menu is open first closes it (it closes on any click outside): don't reopen it.
        if (DateTime.UtcNow - _modelMenuClosed < TimeSpan.FromMilliseconds(250)) return;
        _modelList.Children.Clear();
        var heading = Brand.Text("Model for your next message", Brand.K.Muted, ChatUi.SmallSize, bold: true);
        heading.Margin = new Thickness(10, 6, 10, 4);
        _modelList.Children.Add(heading);
        Button? chosen = null;
        foreach (var choice in ChatModels.All)
        {
            var selected = choice.Id == ChatModels.Find(_model).Id;
            var text = new StackPanel();
            text.Children.Add(Brand.Text(choice.Label, Brand.K.Text, ChatUi.BodySize, bold: selected));
            var line = Brand.Text(choice.Hint, Brand.K.Muted, ChatUi.SmallSize);
            line.Margin = new Thickness(0, 1, 0, 0);
            text.Children.Add(line);
            var row = new DockPanel();
            var check = ChatUi.Glyph(ChatUi.GlyphCheck, Brand.K.Accent, 12);
            check.Margin = new Thickness(ChatUi.S2, 0, 0, 0);
            check.Visibility = selected ? Visibility.Visible : Visibility.Hidden;
            DockPanel.SetDock(check, Dock.Right);
            row.Children.Add(check);
            row.Children.Add(text);
            var item = new Button { Content = row, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            item.SetResourceReference(StyleProperty, "SpmChatMenuItem");
            AutomationProperties.SetName(item, choice.Label + (selected ? ", chosen" : ""));
            AutomationProperties.SetHelpText(item, choice.Hint);
            var id = choice.Id;
            item.Click += (_, _) =>
            {
                CloseModelMenu(focusPill: true);
                PickModel(id);
            };
            _modelList.Children.Add(item);
            if (selected) chosen = item;
        }
        AddBriefMenu();
        _modelMenu.IsOpen = true;
        AutomationProperties.SetItemStatus(_modelPill, "Expanded");
        AutomationProperties.SetItemStatus(_briefPill, brief ? "Expanded" : "Collapsed");
        // From the brief pill, the keyboard starts on the chosen brief setting.
        var briefChosen = brief ? _modelList.Children.OfType<Button>().FirstOrDefault(b =>
            AutomationProperties.GetName(b) is { } n && n.StartsWith("Brief before running:", StringComparison.Ordinal) && n.EndsWith(", chosen", StringComparison.Ordinal)) : null;
        var first = briefChosen ?? chosen ?? _modelList.Children.OfType<Button>().FirstOrDefault();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => first?.Focus()));
    }

    private void CloseModelMenu(bool focusPill)
    {
        _modelMenu.IsOpen = false;
        if (focusPill) _modelPill.Focus();
    }

    private void PickModel(string id)
    {
        if (id == _model) return;
        _model = id;
        ChatModels.Save(ClaudeCli.Folder, id);
        ShowModelChoice(); // the pill says where the next message goes
    }

    /// <summary>Shows the chosen model on the pill, with its hint as the tooltip.</summary>
    private void ShowModelChoice()
    {
        var choice = ChatModels.Find(_model);
        _modelLabel.Text = choice.Label;
        _modelPill.ToolTip = $"Claude model: {choice.Label}. {choice.Hint}. Used from the next message on; click to change.";
        AutomationProperties.SetName(_modelPill, "Claude model: " + choice.Label);
        AutomationProperties.SetHelpText(_modelPill, _modelPill.ToolTip as string ?? "");
    }

    /// <summary>Shows the brief setting on its pill: Brief: Always, Changes or Off.</summary>
    private void ShowBriefChoice()
    {
        var label = _briefMode switch { BriefMode.Always => "Always", BriefMode.Off => "Off", _ => "Changes" };
        _briefLabel.Text = "Brief: " + label;
        _briefPill.ToolTip = "Brief before running: " + PromptBriefs.Label(_briefMode) + ". " + PromptBriefs.Hint(_briefMode) + ". Click to change.";
        AutomationProperties.SetName(_briefPill, "Brief before running: " + PromptBriefs.Label(_briefMode));
        AutomationProperties.SetHelpText(_briefPill, _briefPill.ToolTip as string ?? "");
    }

    private static StackPanel Row(params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var child in children)
        {
            if (row.Children.Count > 0 && child is FrameworkElement f) f.Margin = new Thickness(6, 0, 0, 0);
            row.Children.Add(child);
        }
        return row;
    }

    // ---- Setup: find Claude Code and its sign-in -------------------------------------------------------------------

    private void Check(bool quiet)
    {
        if (_checking || _stopped) return;
        _checking = true;
        if (!quiet && _setup != Setup.Ready)
        {
            _setup = Setup.Checking;
            ShowSetup();
        }
        Task.Run(() =>
        {
            var install = ClaudeCli.Find();
            var signIn = install is null ? (ClaudeCli.SignIn.Unknown, "") : ClaudeCli.CheckSignIn(install);
            return (install, signIn.Item1);
        }).ContinueWith(t => Dispatcher.BeginInvoke(new Action(() => Checked(t))), TaskScheduler.Default);
    }

    private void Checked(Task<(ClaudeCli.Install? install, ClaudeCli.SignIn signIn)> t)
    {
        {
            _checking = false;
            if (t.IsFaulted)
            {
                _setup = Setup.Failed;
                _setupDetail = t.Exception?.GetBaseException().Message ?? "";
            }
            else
            {
                var (install, signIn) = t.Result;
                _install = install;
                var waiting = _setup is Setup.Installing or Setup.SigningIn;
                if (install is null) _setup = _setup == Setup.Installing ? Setup.Installing : Setup.Missing;
                else if (signIn == ClaudeCli.SignIn.SignedOut) _setup = _setup == Setup.SigningIn ? Setup.SigningIn : Setup.SignedOut;
                else
                {
                    _setup = Setup.Ready;
                    if (waiting) Note($"Claude Code {install.Version} is ready. Ask your first question below.");
                }
                // Give up waiting after a quarter of an hour; the card then offers to try again.
                if (_setup is Setup.Installing or Setup.SigningIn && DateTime.UtcNow - _waitingSince > TimeSpan.FromMinutes(15))
                    _setup = _setup == Setup.Installing ? Setup.Missing : Setup.SignedOut;
            }
            if (_setup is Setup.Installing or Setup.SigningIn) _poll.Start();
            else _poll.Stop();
            ShowSetup();
            ShowStatus();
            if (_refreshAsked)
            {
                _refreshAsked = false;
                if (_setup == Setup.Ready && _turn is null && !_stopped) Status($"Claude Code {_install?.Version} is ready and signed in.", spin: false);
            }
        }
    }

    private void ShowSetup()
    {
        if (_setup == Setup.Ready)
        {
            _setupCard.Visibility = Visibility.Collapsed;
            _setupCard.Child = null;
            return;
        }
        var panel = new StackPanel();
        void Title(string text) => panel.Children.Add(Brand.Text(text, Brand.K.Text, ChatUi.TitleSize, bold: true));
        void Para(string text, string key = Brand.K.Text)
        {
            var t = Brand.Text(text, key);
            t.Margin = new Thickness(0, 4, 0, 0);
            panel.Children.Add(t);
        }
        var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        Button Add(string text, Brand.Weight weight, Action click)
        {
            var b = new Button { Content = text };
            b.SetResourceReference(StyleProperty, weight == Brand.Weight.Primary ? "SpmChatPrimary" : "SpmChatPill");
            b.Margin = new Thickness(0, 0, ChatUi.S2, ChatUi.S1);
            b.Click += (_, _) => click();
            row.Children.Add(b);
            return b;
        }
        void Waiting(string text)
        {
            var line = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            var spin = Brand.Spinner();
            spin.SetResourceReference(TextElement.ForegroundProperty, Brand.K.Text);
            DockPanel.SetDock(spin, Dock.Left);
            line.Children.Add(spin);
            line.Children.Add(Brand.Text(text));
            panel.Children.Add(line);
        }

        switch (_setup)
        {
            case Setup.Checking:
                Waiting("Looking for Claude Code on this PC…");
                break;
            case Setup.Missing:
                Title("Install Claude Code to chat here");
                Para("The chat runs Claude Code, Anthropic's app, signed in with your Claude account, so it uses your Claude plan: " +
                     "no API key and no extra cost. It isn't installed on this PC yet.");
                Para("Install opens a PowerShell window that runs Anthropic's official installer:");
                var cmd = Brand.Text(ClaudeCli.InstallCommand, Brand.K.Text, ChatUi.SmallSize);
                cmd.FontFamily = new FontFamily("Consolas, Courier New");
                cmd.Margin = new Thickness(0, 2, 0, 0);
                panel.Children.Add(cmd);
                Para("It takes a minute or two and doesn't need admin rights. This pane notices by itself when it's done.", Brand.K.Muted);
                Add("Install Claude Code", Brand.Weight.Primary, () => Launch(Setup.Installing));
                Add("Check again", Brand.Weight.Secondary, () => Check(quiet: false));
                break;
            case Setup.Installing:
                Title("Installing Claude Code");
                Waiting("Waiting for the installer in the PowerShell window to finish…");
                Para("If the window shows an error, press Install again, or ask IT to install Claude Code for you.", Brand.K.Muted);
                Add("Check again", Brand.Weight.Secondary, () => Check(quiet: false));
                Add("Install again", Brand.Weight.Secondary, () => Launch(Setup.Installing));
                break;
            case Setup.SignedOut:
                Title("Sign in to Claude");
                Para($"Claude Code {_install?.Version} is installed but not signed in. Sign in opens a window and your browser: " +
                     "sign in with the Claude account that has your plan, then come back here.");
                Add("Sign in", Brand.Weight.Primary, () => Launch(Setup.SigningIn));
                Add("Check again", Brand.Weight.Secondary, () => Check(quiet: false));
                break;
            case Setup.SigningIn:
                Title("Signing in");
                Waiting("Waiting for you to finish signing in in the browser…");
                Para("When the window says you're signed in, close it. This pane continues by itself.", Brand.K.Muted);
                Add("Check again", Brand.Weight.Secondary, () => Check(quiet: false));
                Add("Open sign-in again", Brand.Weight.Secondary, () => Launch(Setup.SigningIn));
                break;
            case Setup.Failed:
                Title("Claude Code couldn't be checked");
                Para(_setupDetail, Brand.K.DangerText);
                Para("Press Check again. If it keeps failing, restart Revit.", Brand.K.Muted);
                Add("Check again", Brand.Weight.Secondary, () => Check(quiet: false));
                break;
        }
        panel.Children.Add(row);
        _setupCard.Child = panel;
        _setupCard.Padding = new Thickness(ChatUi.S4, ChatUi.S3, ChatUi.S4, ChatUi.S2);
        _setupCard.BorderThickness = new Thickness(1);
        _setupCard.CornerRadius = new CornerRadius(ChatUi.Radius);
        _setupCard.SetResourceReference(Border.BackgroundProperty, Brand.K.NoticeBg);
        _setupCard.SetResourceReference(Border.BorderBrushProperty, Brand.K.NoticeBorder);
        AutomationProperties.SetLiveSetting(_setupCard, AutomationLiveSetting.Polite);
        _setupCard.Visibility = Visibility.Visible;
    }

    private void Launch(Setup next)
    {
        try
        {
            if (next == Setup.Installing) ClaudeCli.StartInstall();
            else if (_install is { } install) ClaudeCli.StartSignIn(install);
            else { Check(quiet: false); return; }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _setup = Setup.Failed;
            _setupDetail = (next == Setup.Installing ? "PowerShell could not be opened: " : "The sign-in window could not be opened: ") + ex.Message;
            ShowSetup();
            return;
        }
        _setup = next;
        _waitingSince = DateTime.UtcNow;
        _poll.Start();
        ShowSetup();
        ShowStatus();
    }

    // ---- Conversation view ----------------------------------------------------------------------------------------

    private void ShowTitle()
    {
        _chatName.Text = _conv.Name.Length > 0 ? _conv.Name : _conv.Items.Count == 0 ? "New chat" : ChatHistory.AutoTitle(_conv.Items.FirstOrDefault(i => i.Kind == "user")?.Text);
        _chatName.ToolTip = _chatName.Text;
        _modelLine.Text = (_conv.Pinned ? "\u2605 Pinned · " : "") + _conv.Title;
        _modelLine.ToolTip = "Each Revit model keeps its own chats, also after Revit restarts; open the others from the menu at the top left. " + _conv.Key;
        AutomationProperties.SetName(_chatName, "Chat: " + _chatName.Text);
        AutomationProperties.SetName(_modelLine, "Revit model: " + _conv.Title);
    }

    private void ShowConversation()
    {
        ShowTitle();
        // Another chat: the saved prompts and the model menu open only on request, so they start closed.
        if (_promptPanel.Visibility == Visibility.Visible) TogglePrompts();
        _modelMenu.IsOpen = false;
        _messages.Children.Clear();
        _texts.Clear();
        _tools.Clear();
        _views.Clear();
        _rows.Clear();
        _dirty.Clear();
        _briefCards.Clear();
        _acks.Clear();
        // Long conversations draw their latest page; "Show earlier" adds a page at a time.
        _firstShown = History.FirstShown(_conv.Items.Count);
        for (var k = _firstShown; k < _conv.Items.Count; k++) _messages.Children.Add(Build(k));
        ShowEarlierButton();
        _empty.Visibility = _conv.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ScrollToEnd(force: true, instant: true);
    }

    private void Add(ChatItem item)
    {
        if (item.Time == default) item.Time = DateTime.UtcNow;
        _empty.Visibility = Visibility.Collapsed;
        _conv.Items.Add(item);
        var element = Build(_conv.Items.Count - 1);
        _messages.Children.Add(element);
        ChatUi.Arrive(element); // eases in; settles at full opacity with no transform
        if (_shimmerShown && item.Kind is "assistant" or "tool") ChatUi.Reveal(_shimmer, _shimmerShown = false);
        if (item.Kind == "user") _follow.Jump(); // sending shows the answer as it comes
        else _follow.Added();
        UpdateJump();
        ScrollToEnd();
    }

    /// <summary>The element for one conversation item; Claude's name and time head each run of Claude's messages and steps.</summary>
    private UIElement Build(int index)
    {
        var item = _conv.Items[index];
        var first = index == 0 || _conv.Items[index - 1].Kind is not ("assistant" or "tool");
        return item.Kind switch
        {
            "user" => UserMessage(item),
            "assistant" => AssistantMessage(item, first),
            "tool" => ToolCard(item, first),
            "error" => ErrorBox(item),
            "handoff" => HandoffCard.Build(item, Tools.Handoff.Find(item.Name)),
            "brief" => BriefCard(item),
            "job" => JobCard(item),
            "note" when item.Name.StartsWith("ack:", StringComparison.Ordinal) => AckLine(item),
            _ => NoteLine(item),
        };
    }

    /// <summary>A step only the user can do, as a card, while a turn runs and the pane is on screen; false: the window shows it.</summary>
    private bool ShowHandoff(Tools.HandoffRequest request)
    {
        if (_turn is null || !IsVisible) return false;
        Add(HandoffCard.Item(request));
        ShowStatus("Over to you: do the step in Revit, then press Continue on the card.");
        return true;
    }

    private static string When(ChatItem item)
    {
        var stamp = StreamRender.Stamp(item.Time == default ? default : item.Time.ToLocalTime(), DateTime.Now);
        return stamp.Length > 0 ? " · " + stamp : "";
    }

    private static string WhenLong(ChatItem item) =>
        item.Time == default ? "" : item.Time.ToLocalTime().ToString("dddd d MMMM yyyy, HH:mm", CultureInfo.InvariantCulture);

    private static UIElement ClaudeHeader(ChatItem item)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, ChatUi.S2) };
        var mark = ChatUi.Mark(20);
        mark.Margin = new Thickness(0, 0, ChatUi.S2, 0);
        mark.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(mark, "");
        row.Children.Add(mark);
        row.Children.Add(ChatUi.Small("Claude", Brand.K.Text, bold: true));
        var time = ChatUi.Small(When(item));
        time.ToolTip = WhenLong(item);
        row.Children.Add(time);
        return row;
    }

    private UIElement UserMessage(ChatItem item)
    {
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(ChatUi.S8 + ChatUi.S2, ChatUi.S6, 0, ChatUi.S1) };
        var head = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, ChatUi.S1) };
        var more = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Visibility = Visibility.Collapsed };
        if (item.Text.Length > 0)
        {
            var copy = ChatUi.CopyButton(() => item.Text, "your message", null);
            head.Children.Add(copy);
            head.Children.Add(MoreButton(item, more));
        }
        var who = ChatUi.Small("You" + When(item));
        who.ToolTip = WhenLong(item);
        who.Margin = new Thickness(ChatUi.S1, 0, 0, 0);
        head.Children.Add(who);
        panel.Children.Add(head);
        panel.Children.Add(more);

        var body = new StackPanel();
        if (item.Text.Length > 0) body.Children.Add(ChatUi.Selectable(item.Text, "Your message"));
        if (item.Files.Count > 0)
        {
            var files = new WrapPanel { Margin = new Thickness(0, item.Text.Length > 0 ? ChatUi.S2 : 0, 0, 0) };
            foreach (var path in item.Files)
            {
                FrameworkElement shown;
                if (ImageType(path) is not null) shown = ChatUi.Thumbnail(path, 72);
                else
                {
                    var chip = new StackPanel { Orientation = Orientation.Horizontal };
                    chip.Children.Add(ChatUi.Glyph(ChatUi.GlyphFile, Brand.K.Muted));
                    chip.Children.Add(ChatUi.Small(" " + System.IO.Path.GetFileName(path), Brand.K.Text));
                    var open = new Button { Content = chip, ToolTip = path };
                    open.SetResourceReference(StyleProperty, "SpmChatGhost");
                    AutomationProperties.SetName(open, "Attached file " + System.IO.Path.GetFileName(path) + ", open it");
                    var target = path;
                    open.Click += (_, _) => ChatUi.Open(target);
                    shown = open;
                }
                shown.Margin = new Thickness(0, 0, ChatUi.S1, ChatUi.S1);
                files.Children.Add(shown);
            }
            body.Children.Add(files);
        }
        else if (item.Name.Length > 0)
        {
            var files = Brand.Text("Attached: " + item.Name, Brand.K.Muted, ChatUi.SmallSize);
            files.Margin = new Thickness(0, 2, 0, 0);
            body.Children.Add(files);
        }
        // Your messages: a rounded bubble in the accent tint, tail corner bottom right; Claude's answers sit on the page
        // under Claude's mark and name.
        var bubble = ChatUi.Opaque(new Border
        {
            Child = body, Padding = new Thickness(ChatUi.S4, ChatUi.S2 + 2, ChatUi.S4, ChatUi.S2 + 2), CornerRadius = new CornerRadius(ChatUi.Radius, ChatUi.Radius, ChatUi.S1, ChatUi.Radius),
            HorizontalAlignment = HorizontalAlignment.Right, SnapsToDevicePixels = true,
        });
        bubble.SetResourceReference(Border.BackgroundProperty, Brand.K.AccentSoft);
        panel.Children.Add(bubble);
        AutomationProperties.SetName(panel, "You" + When(item) + ": " + item.Text + (item.Name.Length > 0 ? ". Attached: " + item.Name : ""));
        return panel;
    }

    private UIElement AssistantMessage(ChatItem item, bool header)
    {
        var panel = new StackPanel { Margin = new Thickness(0, header ? ChatUi.S6 : ChatUi.S2, 0, 0) };
        if (header) panel.Children.Add(ClaudeHeader(item));
        var view = new MarkdownView();
        view.SetMarkdown(item.Text);
        _views[item] = view;
        // Copy sits over the top right corner, shown on hover or when it has keyboard focus, so it takes no room.
        var grid = new Grid();
        grid.Children.Add(view);
        var copy = ChatUi.CopyButton(() => item.Text, "Claude's answer", null);
        copy.HorizontalAlignment = HorizontalAlignment.Right;
        copy.VerticalAlignment = VerticalAlignment.Top;
        copy.Margin = new Thickness(0, -ChatUi.S1, 0, 0);
        copy.SetResourceReference(BackgroundProperty, Brand.K.Bg);
        var more = new WrapPanel { Visibility = Visibility.Collapsed };
        var moreButton = MoreButton(item, more);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = copy.Margin };
        copy.Margin = new Thickness(0);
        tools.SetResourceReference(BackgroundProperty, Brand.K.Bg);
        tools.Children.Add(copy);
        tools.Children.Add(moreButton);
        tools.Opacity = 0;
        void ToolsShown() => tools.Opacity = grid.IsMouseOver || tools.IsKeyboardFocusWithin || more.Visibility == Visibility.Visible ? 1 : 0;
        grid.MouseEnter += (_, _) => ToolsShown();
        grid.MouseLeave += (_, _) => ToolsShown();
        tools.IsKeyboardFocusWithinChanged += (_, _) => ToolsShown();
        grid.Children.Add(tools);
        panel.Children.Add(grid);
        panel.Children.Add(more);
        return panel;
    }

    /// <summary>A message's ⋯: shows (or hides) its extra actions under it: Save prompt.</summary>
    private Button MoreButton(ChatItem item, WrapPanel more)
    {
        var button = ChatUi.Ghost(ChatUi.GlyphMore, null, "More actions for this message", "More: save it as a prompt");
        AutomationProperties.SetItemStatus(button, "Collapsed");
        var save = ChatUi.Ghost(ChatUi.GlyphPrompts, "Save prompt", "Save this message as a prompt",
            "Keeps this text in your saved prompts, to use again from the bookmark button by the message box or the home screen.");
        save.Click += (_, _) =>
        {
            SavePrompt(item.Text);
            more.Visibility = Visibility.Collapsed;
            AutomationProperties.SetItemStatus(button, "Collapsed");
            button.Focus();
        };
        more.Children.Add(save);
        button.Click += (_, _) =>
        {
            var open = more.Visibility != Visibility.Visible;
            more.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetItemStatus(button, open ? "Expanded" : "Collapsed");
            if (open) save.Focus();
        };
        return button;
    }

    // ---- Saved prompts --------------------------------------------------------------------------------------------

    private List<SavedPrompt> Saved => _saved ??= SavedPrompts.Load(SavedPrompts.DefaultFile);

    private void SavePrompt(string text)
    {
        if (text.Trim().Length == 0) return;
        try
        {
            var (list, saved) = SavedPrompts.Update(SavedPrompts.DefaultFile, l => SavedPrompts.Add(l, text, DateTime.UtcNow));
            _saved = list;
            Status(saved
                ? "Saved to your prompts. Use it again from the bookmark button below."
                : "The prompt couldn't be saved (the prompts file is in use or read-only).", spin: false);
            if (_promptPanel.Visibility == Visibility.Visible) ShowPrompts();
            PromptsChanged?.Invoke();
        }
        catch (Exception ex) { Status($"The prompt couldn't be saved ({ex.Message}).", spin: false); } // never into Revit's dispatcher
    }

    /// <summary>Raised when the saved prompts change (the home screen lists them).</summary>
    private event Action? PromptsChanged;

    private void TogglePrompts()
    {
        if (_promptPanel.Visibility == Visibility.Visible)
        {
            _promptPanel.Visibility = Visibility.Collapsed;
            AutomationProperties.SetItemStatus(_prompts, "Collapsed");
            return;
        }
        ShowPrompts();
        _promptPanel.Visibility = Visibility.Visible;
        AutomationProperties.SetItemStatus(_prompts, "Expanded");
        _promptPanel.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    /// <summary>The saved prompts above the message box: pick one to put it in the box, or delete it.</summary>
    private void ShowPrompts()
    {
        var panel = new DockPanel();
        var head = new DockPanel { Margin = new Thickness(ChatUi.S2, ChatUi.S1, ChatUi.S1, ChatUi.S1) };
        var close = ChatUi.Ghost(ChatUi.GlyphCross, null, "Close saved prompts", "Close");
        close.Click += (_, _) => TogglePrompts();
        DockPanel.SetDock(close, Dock.Right);
        head.Children.Add(close);
        head.Children.Add(ChatUi.Small("Saved prompts", Brand.K.Text, bold: true));
        DockPanel.SetDock(head, Dock.Top);
        panel.Children.Add(head);
        var list = new StackPanel { Margin = new Thickness(ChatUi.S1, 0, ChatUi.S1, ChatUi.S1) };
        if (Saved.Count == 0)
        {
            var none = Brand.Text("No saved prompts yet. Save one from any message: ⋯ › Save prompt.", Brand.K.Muted, ChatUi.SmallSize);
            none.Margin = new Thickness(ChatUi.S1, 0, 0, ChatUi.S1);
            list.Children.Add(none);
        }
        foreach (var prompt in Saved)
        {
            var row = new DockPanel();
            var delete = ChatUi.Ghost(ChatUi.GlyphDelete, null, "Delete saved prompt " + prompt.Title, "Delete this saved prompt");
            delete.Click += (_, _) =>
            {
                try
                {
                    var (list, saved) = SavedPrompts.Update(SavedPrompts.DefaultFile, l => SavedPrompts.Remove(l, prompt.Text));
                    _saved = list;
                    if (!saved) Status("The prompt couldn't be deleted (the prompts file is in use or read-only).", spin: false);
                    ShowPrompts();
                    PromptsChanged?.Invoke();
                    _prompts.Focus();
                }
                catch (Exception ex) { Status($"The prompt couldn't be deleted ({ex.Message}).", spin: false); }
            };
            DockPanel.SetDock(delete, Dock.Right);
            row.Children.Add(delete);
            var use = new Button { Content = ChatUi.Small(prompt.Title, Brand.K.Text), HorizontalContentAlignment = HorizontalAlignment.Left, ToolTip = prompt.Text };
            use.SetResourceReference(StyleProperty, "SpmChatGhost");
            AutomationProperties.SetName(use, "Use prompt: " + prompt.Title);
            AutomationProperties.SetHelpText(use, "Puts it in the message box to edit or send.");
            use.Click += (_, _) =>
            {
                try
                {
                    UsePrompt(prompt.Text);
                    TogglePrompts();
                }
                catch (Exception ex) { Status($"The prompt couldn't be used ({ex.Message}).", spin: false); }
            };
            row.Children.Add(use);
            list.Children.Add(row);
        }
        var scroll = new ScrollViewer { Content = list, MaxHeight = 200, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        panel.Children.Add(scroll);
        _promptPanel.Child = panel;
        _promptPanel.BorderThickness = new Thickness(1);
        _promptPanel.CornerRadius = new CornerRadius(ChatUi.Radius);
        _promptPanel.Padding = new Thickness(ChatUi.S1);
        _promptPanel.SetResourceReference(Border.BackgroundProperty, Brand.K.Raised);
        _promptPanel.SetResourceReference(Border.BorderBrushProperty, Brand.K.BorderSubtle);
        AutomationProperties.SetName(_promptPanel, "Saved prompts");
        _promptPanel.PreviewKeyDown -= OnPromptPanelKey;
        _promptPanel.PreviewKeyDown += OnPromptPanelKey;
    }

    private void OnPromptPanelKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        TogglePrompts();
        _prompts.Focus();
    }

    /// <summary>Puts a prompt in the message box, to edit before sending.</summary>
    private void UsePrompt(string text)
    {
        _input.Text = text;
        _input.CaretIndex = text.Length;
        _input.Focus();
    }

    private UIElement ErrorBox(ChatItem item)
    {
        var row = new DockPanel();
        var icon = ChatUi.Glyph(ChatUi.GlyphError, Brand.K.DangerText, 14);
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 1, ChatUi.S2, 0);
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(Brand.Text(item.Text, Brand.K.Text));
        var box = new Border
        {
            Child = row, Padding = new Thickness(ChatUi.S4, ChatUi.S3, ChatUi.S4, ChatUi.S3), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(ChatUi.Radius), Margin = new Thickness(0, ChatUi.S4, 0, ChatUi.S1),
        };
        box.SetResourceReference(Border.BackgroundProperty, Brand.K.NoticeBg);
        box.SetResourceReference(Border.BorderBrushProperty, Brand.K.NoticeBorder);
        AutomationProperties.SetName(box, "Problem: " + item.Text);
        AutomationProperties.SetLiveSetting(box, AutomationLiveSetting.Assertive);
        return box;
    }

    private static UIElement NoteLine(ChatItem item)
    {
        var row = new DockPanel { Margin = new Thickness(0, ChatUi.S4, 0, ChatUi.S1) };
        var icon = ChatUi.Glyph(ChatUi.GlyphInfo, Brand.K.Muted, 12);
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 1, ChatUi.S2, 0);
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        var text = Brand.Text(item.Text, Brand.K.Muted, ChatUi.SmallSize);
        text.FontStyle = FontStyles.Italic;
        row.Children.Add(text);
        AutomationProperties.SetName(row, item.Text);
        return row;
    }

    // ---- Tool steps -----------------------------------------------------------------------------------------------

    /// <summary>
    /// A tool step: a one-line header (status, tool, short input) that opens to the input, the result and the element
    /// ids found in it, which select and zoom to the elements in Revit.
    /// </summary>
    private UIElement ToolCard(ChatItem item, bool header)
    {
        var row = new ToolRow { Item = item };
        _rows[item] = row;
        var outer = new StackPanel { Margin = new Thickness(0, header ? ChatUi.S6 : ChatUi.S1, 0, 0) };
        if (header) outer.Children.Add(ClaudeHeader(item));

        var line = new DockPanel();
        // The status mark keeps its box when the spinner turns into a check, so the line doesn't shift.
        row.Mark.Width = 18;
        row.Mark.Height = 18;
        row.Mark.Focusable = false;
        row.Mark.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(row.Mark, Dock.Left);
        line.Children.Add(row.Mark);
        row.Chevron = ChatUi.Glyph(ChatUi.GlyphRight, Brand.K.Muted, 9);
        row.Chevron.Margin = new Thickness(ChatUi.S2, 0, 0, 0);
        DockPanel.SetDock(row.Chevron, Dock.Right);
        line.Children.Add(row.Chevron);
        row.Line = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = ChatUi.SmallSize, VerticalAlignment = VerticalAlignment.Center };
        Brand.Fg(row.Line, Brand.K.Muted);
        line.Children.Add(row.Line);
        row.Header = new ToggleButton { Content = line };
        row.Header.SetResourceReference(StyleProperty, "SpmChatDisclosure");
        row.Header.Checked += (_, _) => Expand(row, true);
        row.Header.Unchecked += (_, _) => Expand(row, false);

        row.Detail = Brand.Text("", Brand.K.DangerText, ChatUi.SmallSize);
        row.Detail.Margin = new Thickness(26, 0, ChatUi.S2, ChatUi.S2);
        row.Detail.Visibility = Visibility.Collapsed;
        row.Details = new StackPanel { Margin = new Thickness(26, 0, ChatUi.S2, ChatUi.S2), Visibility = Visibility.Collapsed };

        var stack = new StackPanel();
        stack.Children.Add(row.Header);
        stack.Children.Add(row.Detail);
        stack.Children.Add(row.Details);
        var card = ChatUi.Opaque(new Border { Child = stack, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(ChatUi.Radius), Padding = new Thickness(ChatUi.S1 / 2), SnapsToDevicePixels = true });
        card.SetResourceReference(Border.BorderBrushProperty, Brand.K.BorderSubtle);
        card.SetResourceReference(Border.BackgroundProperty, Brand.K.Raised);
        outer.Children.Add(card);
        SetTool(row, item.State, item.Text, "");
        return outer;
    }

    /// <summary>Opens or closes a step's details: they slide open (height eased, clipped) and closed again.</summary>
    private void Expand(ToolRow row, bool open)
    {
        row.Chevron.Text = open ? ChatUi.GlyphDown : ChatUi.GlyphRight;
        AutomationProperties.SetItemStatus(row.Header, open ? "Expanded" : "Collapsed");
        if (open) FillDetails(row);
        ChatUi.Reveal(row.Details, open);
    }

    private void SetTool(ToolRow row, string state, string summary, string result)
    {
        row.Item.State = state;
        row.Item.Text = summary;
        if (result.Length > 0) row.Item.Result = result.Length > MaxResult ? result.Substring(0, MaxResult) + "…" : result;
        row.Line.Inlines.Clear();
        row.Line.Inlines.Add(new Run(row.Item.Name) { FontWeight = FontWeights.SemiBold });
        Brand.Fg(row.Line, Brand.K.Muted);
        if (summary.Length > 0) row.Line.Inlines.Add(new Run("  " + summary));
        FrameworkElement mark;
        string said;
        switch (state)
        {
            case "running":
                mark = Brand.Spinner();
                mark.SetResourceReference(TextElement.ForegroundProperty, Brand.K.Accent);
                said = "running";
                break;
            case "done":
                mark = ChatUi.Glyph(ChatUi.GlyphCheck, Brand.K.SuccessText, 12);
                said = "succeeded";
                break;
            case "stopped":
                mark = ChatUi.Glyph(ChatUi.GlyphStop, Brand.K.Muted, 11);
                said = "stopped";
                break;
            default:
                mark = ChatUi.Glyph(ChatUi.GlyphCross, Brand.K.DangerText, 11);
                said = "failed";
                break;
        }
        row.Mark.Content = mark;
        row.Header.ToolTip = row.Item.Name + " (" + said + ")" + (summary.Length > 0 ? "\n" + summary : "") + "\nClick for the input, the result and the elements.";
        AutomationProperties.SetName(row.Header, $"Step {row.Item.Name}, {said}. {summary}");
        AutomationProperties.SetHelpText(row.Header, "Press Space to show or hide the step's input, result and elements.");
        if (state == "failed" && row.Item.Result.Length > 0)
        {
            var first = row.Item.Result.Replace("\r", "").Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? "";
            row.Detail.Text = first.Length > 240 ? first.Substring(0, 240) + "…" : first;
            row.Detail.Visibility = Visibility.Visible;
        }
        if (row.Header.IsChecked == true) FillDetails(row);
    }

    private void FillDetails(ToolRow row)
    {
        var item = row.Item;
        row.Details.Children.Clear();
        void Label(string text)
        {
            var t = ChatUi.Small(text, Brand.K.Muted, bold: true);
            t.Margin = new Thickness(0, ChatUi.S2, 0, 2);
            row.Details.Children.Add(t);
        }
        void Mono(string text, string name)
        {
            var box = ChatUi.Selectable(text, name);
            box.FontFamily = ChatUi.Mono;
            box.FontSize = ChatUi.SmallSize;
            box.Padding = new Thickness(ChatUi.S2, ChatUi.S1, ChatUi.S2, ChatUi.S1);
            box.SetResourceReference(BackgroundProperty, Brand.K.Surface);
            row.Details.Children.Add(box);
        }
        if (item.Input.Length > 0)
        {
            Label("Input");
            Mono(item.Input, item.Name + " input");
        }
        if (item.Result.Length > 0)
        {
            Label(item.State == "failed" ? "Error" : "Result");
            Mono(item.Result, item.Name + " result");
        }
        var ids = new List<long>();
        if (item.Result.Length > 0) UI.Pane.Collect(item.Result, ids);
        if (ids.Count > 0)
        {
            Label("Elements (click to select and zoom in Revit)");
            var links = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = ChatUi.SmallSize };
            foreach (var id in ids.Take(MaxIdLinks))
            {
                if (links.Inlines.Count > 0) links.Inlines.Add(new Run("  "));
                links.Inlines.Add(IdLink(id.ToString(CultureInfo.InvariantCulture), new[] { id }, $"Select element {id} in Revit"));
            }
            if (ids.Count > 1)
            {
                links.Inlines.Add(new Run("   "));
                links.Inlines.Add(IdLink($"Select all {ids.Count}", ids, $"Select all {ids.Count} elements in Revit"));
            }
            row.Details.Children.Add(links);
        }
        if (row.Details.Children.Count == 0)
        {
            var none = ChatUi.Small(item.State == "running" ? "Running; the result shows here when it's done." : "No details were kept for this step.");
            none.Margin = new Thickness(0, ChatUi.S1, 0, 0);
            row.Details.Children.Add(none);
        }
        if (WhenLong(item) is { Length: > 0 } when)
        {
            var t = ChatUi.Small(when);
            t.Margin = new Thickness(0, ChatUi.S2, 0, 0);
            row.Details.Children.Add(t);
        }
    }

    private Hyperlink IdLink(string text, IReadOnlyList<long> ids, string name)
    {
        var link = new Hyperlink(new Run(text)) { ToolTip = name };
        link.SetResourceReference(TextElement.ForegroundProperty, Brand.K.Link);
        AutomationProperties.SetName(link, name);
        link.Click += (_, _) => SelectInRevit(ids);
        return link;
    }

    /// <summary>Selects the elements in the active model and zooms to them (one view id opens that view), as the job pane does.</summary>
    private async void SelectInRevit(IReadOnlyList<long> ids)
    {
        var list = string.Join(",", ids.Select(i => i.ToString(CultureInfo.InvariantCulture)));
        try { await App.Dispatcher.InvokeAsync(app => UI.Pane.Select(app, list)); }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or Autodesk.Revit.Exceptions.ApplicationException)
        {
            Status("Revit couldn't select the elements now: " + ex.Message, spin: false);
        }
    }

    private void Note(string text)
    {
        if (text.Length == 0) return;
        Add(new ChatItem { Kind = "note", Text = text });
    }

    private void Error(string text) => Add(new ChatItem { Kind = "error", Text = text });

    // ---- Scrolling and history ------------------------------------------------------------------------------------

    /// <summary>
    /// Scrolls to the newest message once layout is done, when the list follows (or <paramref name="force"/>): a short
    /// glide (see <see cref="SmoothScroll"/>), or a jump with <paramref name="instant"/> (another chat opened) or when
    /// Windows' animations are off.
    /// </summary>
    private void ScrollToEnd(bool force = false, bool instant = false)
    {
        if (force) _follow.Jump();
        UpdateJump();
        if (!_follow.Following || _scrollQueued) return;
        _scrollQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _scrollQueued = false;
            if (_follow.Following)
            {
                if (instant || !ChatUi.Animate)
                {
                    StopGlide();
                    _scroll.ScrollToEnd();
                }
                else StartGlide();
            }
            UpdateJump();
        }));
    }

    private void StartGlide()
    {
        if (_gliding) return;
        _gliding = true;
        CompositionTarget.Rendering += Glide;
    }

    private void StopGlide()
    {
        if (!_gliding) return;
        _gliding = false;
        CompositionTarget.Rendering -= Glide;
    }

    /// <summary>One frame of the glide to the bottom, on whole device pixels; it stops at the bottom or when the reader takes over.</summary>
    private void Glide(object? sender, EventArgs e)
    {
        var target = _scroll.ScrollableHeight;
        if (!_follow.Following || !IsVisible && PresentationSource.FromVisual(this) is null)
        {
            StopGlide();
            return;
        }
        var next = SmoothScroll.Next(_scroll.VerticalOffset, target, _scroll.ViewportHeight, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (next != _scroll.VerticalOffset) _scroll.ScrollToVerticalOffset(next);
        if (next >= target) StopGlide();
    }

    private void UpdateJump()
    {
        var hasMessages = _conv.Items.Count > 0 && _empty.Visibility != Visibility.Visible;
        var show = ScrollFollow.JumpVisible(hasMessages, _follow.Following, _scroll.ScrollableHeight, _scroll.VerticalOffset);
        _jump.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        var text = _follow.Unseen > 0 ? $"Jump to latest ({_follow.Unseen} new)" : "Jump to latest";
        if (_jumpText.Text != text)
        {
            _jumpText.Text = text;
            AutomationProperties.SetName(_jump, text);
        }
    }

    private void ShowEarlierButton()
    {
        _earlier.Visibility = _firstShown > 0 ? Visibility.Visible : Visibility.Collapsed;
        var text = _firstShown == 1 ? "Show 1 earlier message" : $"Show earlier messages ({_firstShown} more)";
        _earlierText.Text = text;
        AutomationProperties.SetName(_earlier, text);
    }

    /// <summary>Draws the page before the first message drawn, keeping what the reader sees in place.</summary>
    private void ShowEarlier()
    {
        if (_firstShown == 0) return;
        var from = History.Earlier(_firstShown);
        var before = _scroll.ExtentHeight;
        var offset = _scroll.VerticalOffset;
        for (var k = _firstShown - 1; k >= from; k--) _messages.Children.Insert(0, Build(k));
        _firstShown = from;
        ShowEarlierButton();
        _scroll.UpdateLayout();
        _scroll.ScrollToVerticalOffset(offset + _scroll.ExtentHeight - before);
        if (_firstShown == 0) _scroll.Focus(); // the button is gone: keep the keyboard in the list
    }

    /// <summary>After a turn, while the reader is at the bottom, the oldest drawn messages beyond two pages are let go.</summary>
    private void TrimDrawn()
    {
        var drop = History.Trim(_messages.Children.Count, _follow.Following);
        if (drop <= 0) return;
        Forget(_conv.Items.Skip(_firstShown).Take(drop));
        _messages.Children.RemoveRange(0, drop);
        _firstShown += drop;
        ShowEarlierButton();
    }

    /// <summary>Lets go of everything kept for items no longer drawn (their views, tool rows and streaming keys).</summary>
    private void Forget(IEnumerable<ChatItem> items)
    {
        var gone = new HashSet<ChatItem>(items);
        if (gone.Count == 0) return;
        foreach (var item in gone)
        {
            _views.Remove(item);
            _rows.Remove(item);
        }
        foreach (var key in _texts.Where(kv => gone.Contains(kv.Value.Item)).Select(kv => kv.Key).ToList()) { _texts.Remove(key); _dirty.Remove(key); }
        foreach (var id in _tools.Where(kv => gone.Contains(kv.Value.Item)).Select(kv => kv.Key).ToList()) _tools.Remove(id);
    }

    private void RenderDirty()
    {
        _render.Stop();
        _throttle.Drawn(DateTime.UtcNow);
        foreach (var key in _dirty)
            if (_texts.TryGetValue(key, out var t)) t.View.SetMarkdown(t.Item.Text);
        _dirty.Clear();
        ScrollToEnd();
    }

    /// <summary>Streamed text changed: draw it soon, at most every 40 ms, together with whatever else arrives meanwhile.</summary>
    private void RequestRender()
    {
        if (_throttle.Request(DateTime.UtcNow) is not { } wait) return;
        _render.Interval = wait > TimeSpan.FromMilliseconds(1) ? wait : TimeSpan.FromMilliseconds(1);
        _render.Start();
    }

    // ---- Status and buttons ---------------------------------------------------------------------------------------

    private void ShowStatus(string? text = null)
    {
        var busy = _turn is not null;
        // While Claude works, Stop takes Send's place (Send isn't greyed out: the pane is working, not disabled).
        var sendHadFocus = _send.IsKeyboardFocused;
        var stopHadFocus = _stop.IsKeyboardFocused;
        _stop.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _send.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        ShowSteer();
        if (!busy) Brand.SetBusy(_stop, false);
        if (busy && sendHadFocus) _stop.Focus();
        if (!busy && stopHadFocus) _input.Focus();
        _newChat.IsEnabled = !_stopped;
        _send.IsEnabled = _attach.IsEnabled = _view.IsEnabled = _prompts.IsEnabled = !_stopped;
        if (_stopped)
        {
            Status("Sketch ProMax is switching to its new version. The chat continues in a moment.", spin: true);
            return;
        }
        if (busy)
        {
            Status(text ?? (_turn!.Stopping ? "Stopping…" : "Claude is thinking…"), spin: true);
            return;
        }
        Status(_setup switch
        {
            Setup.Ready => "", // the box's hint and tooltip say how to send; the model pill says where it goes
            Setup.Checking => "Looking for Claude Code…",
            _ => "Finish the step above to start chatting.",
        }, spin: false);
    }

    /// <summary>
    /// Shows a status line. While a turn runs, what Claude is doing shows at the end of the conversation with the typing
    /// dots, and the line under the message box says how to stop it.
    /// </summary>
    private void Status(string text, bool spin)
    {
        var working = _turn is not null && !_stopped;
        var wasWorking = _workingShown;
        // The typing line eases open and closed, so the list doesn't jump when a turn starts or ends; the shimmer
        // stands in for the answer until its first words or step.
        var waiting = working && _turn is { SawText: false, SawTool: false };
        if (waiting != _shimmerShown)
        {
            _shimmerShown = waiting;
            // While the line stays, only the shimmer eases; when the whole block opens it is part of it, and when the
            // block closes the shimmer goes once it has.
            if (working && _workingShown) ChatUi.Reveal(_shimmer, waiting);
            else if (working) _shimmer.Visibility = waiting ? Visibility.Visible : Visibility.Collapsed;
        }
        if (working != _workingShown)
            ChatUi.Reveal(_working, _workingShown = working, () => { if (!_shimmerShown) _shimmer.Visibility = Visibility.Collapsed; });
        if (working && !wasWorking) ScrollToEnd();
        if (working && spin)
        {
            _workingText.Text = text;
            AutomationProperties.SetName(_working, text);
            _status.Text = _turn!.Stopping ? "Stopping Claude…" : _control is not null
                ? "Claude is working. Send a message to steer it: it is applied after the current item. Esc or Stop ends it."
                : "Claude is working. Esc or Stop ends it; you can type your next message meanwhile.";
            _statusRow.Visibility = Visibility.Visible;
            _statusSpin.Content = null;
            return;
        }
        _status.Text = text;
        _statusRow.Visibility = text.Length > 0 || spin ? Visibility.Visible : Visibility.Collapsed;
        if (spin && _statusSpin.Content is null)
        {
            var s = Brand.Spinner();
            s.SetResourceReference(TextElement.ForegroundProperty, Brand.K.Muted);
            _statusSpin.Content = s;
        }
        else if (!spin) _statusSpin.Content = null;
    }

    private void OnInputKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0 && (Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            e.Handled = true;
            Send();
        }
        else if (e.Key == Key.Escape && _turn is not null)
        {
            e.Handled = true;
            StopTurn();
        }
    }

    // ---- Sending --------------------------------------------------------------------------------------------------

    private async void Send()
    {
        if (_stopped) return;
        if (_turn is not null)
        {
            // While a task runs the box stays open: the message reaches Claude at the next step boundary.
            var note = _input.Text.Trim();
            if (_turn.Planning || _control is null || note.Length == 0)
            {
                Status(note.Length == 0 && !_turn.Planning ? "Type a message first; Claude gets it after the current item." : "Claude is still answering. Wait for it, or press Stop.", spin: true);
                return;
            }
            _input.Clear();
            QueueNote(note);
            if (_pending.Count > 0) Status("Got it. Attachments go with your next message, after this task.", spin: true);
            return;
        }
        if (_setup != Setup.Ready)
        {
            ShowSetup();
            Status(_setup == Setup.Checking ? "Still looking for Claude Code; try again in a moment." : "Finish the step above first, then send.", spin: false);
            if (_setup is Setup.Missing or Setup.SignedOut or Setup.Failed) Check(quiet: true);
            return;
        }
        var text = _input.Text.Trim();
        var attachments = _pending.ToList();
        if (text.Length == 0 && attachments.Count == 0)
        {
            Status("Type a message first.", spin: false);
            _input.Focus();
            return;
        }
        _input.Clear();
        _pending.Clear();
        ShowChips();
        Add(new ChatItem { Kind = "user", Text = text, Name = string.Join(", ", attachments.Select(a => a.Name)), Files = attachments.Select(a => a.Path).ToList() });
        if (_conv.Name.Length == 0) _conv.Name = ChatHistory.AutoTitle(text.Length > 0 ? text : string.Join(", ", attachments.Select(a => a.Name)));
        ShowTitle();
        SupersedeBriefs();
        if (StartBrief(text, attachments)) return; // a brief card first; the choice on it runs the turn
        await RunTurn(text, attachments, "");
    }

    /// <summary>Sends a message to Claude Code as a new turn (from Send, or from a choice on a brief card).</summary>
    private async Task RunTurn(string text, List<Attachment> attachments, string brief, PromptBrief? plan = null)
    {
        if (DeliveryCapture is { } capture)
        {
            capture(text); // the field test's hook: what would be sent, never sent
            return;
        }
        var turn = _turn = new Turn { Brief = brief };
        Revit.TimeLog.BeginChat(brief);
        BeginJob(plan);
        _lastActivity = DateTime.UtcNow;
        ShowStatus(attachments.Count > 0 ? "Preparing the attachments…" : null);
        try
        {
            turn.Line = await Task.Run(() => BuildMessage(text, attachments));
        }
        catch (Exception ex)
        {
            // Whatever went wrong (an unreadable file, a picture WPF can't decode), the pane must not stay busy.
            if (_turn == turn) EndTurn();
            Error($"An attachment couldn't be read: {ex.Message} Remove it or attach it again, then send.");
            return;
        }
        if (_turn != turn) return; // stopped meanwhile
        try
        {
            await Deliver(turn);
        }
        catch (Exception ex)
        {
            if (_turn == turn) EndTurn();
            Error($"The message could not be sent to Claude Code ({ex.Message}). Send it again.");
        }
    }

    private async Task Deliver(Turn turn)
    {
        try
        {
            if (_engine is { IsAlive: true } engine && engine.Model == _model)
            {
                ShowStatus();
                await engine.SendAsync(turn.Line);
                return;
            }
            _engine?.Dispose();
            var resume = _conv.SessionId.Length > 0 && !turn.Retried ? _conv.SessionId : null;
            // A new model needs a new Claude Code process; it resumes the same conversation.
            var fresh = new ChatEngine(Dispatcher, _install!, resume, _model);
            fresh.Event += e => { if (_engine == fresh) OnEvent(e); };
            fresh.Exited += tail => { if (_engine == fresh) OnExited(fresh, tail); };
            _engine = fresh;
            ShowStatus(resume is null ? "Starting Claude Code…" : "Reopening this conversation…");
            await fresh.StartAsync(turn.Line);
        }
        catch (Exception ex)
        {
            if (_turn != turn) return;
            // Writing failed because Claude Code already ended (a bad option, a session it couldn't open): its Exited
            // event follows and says why, or starts fresh. The pipe can break a moment before the process is seen to end,
            // so it gets half a second to end before it is killed.
            if (ex is IOException && _engine is { } ended && await Task.Run(() => ended.WaitForExit(500))) return;
            if (_turn != turn) return; // stopped meanwhile
            _engine?.Kill();
            _engine = null;
            EndTurn();
            Error(ex is System.ComponentModel.Win32Exception or FileNotFoundException
                ? $"Claude Code could not be started ({ex.Message}). Press Check again above, or reinstall Claude Code."
                : $"The message could not be sent to Claude Code ({ex.Message}). Send it again.");
            if (ex is System.ComponentModel.Win32Exception or FileNotFoundException) Check(quiet: false);
        }
    }

    /// <summary>The stream-json line for a turn: text with notes on attached files, and pictures as image blocks.</summary>
    private static string BuildMessage(string text, List<Attachment> attachments)
    {
        var images = new List<(string, string)>();
        var notes = new StringBuilder();
        foreach (var a in attachments)
        {
            if (a.IsImage && images.Count < MaxImages)
            {
                images.Add(EncodeImage(a.Path));
                continue;
            }
            var ext = System.IO.Path.GetExtension(a.Path).ToLowerInvariant();
            var kind = ext switch
            {
                ".pdf" => "PDF; read it with the Read tool",
                ".dwg" or ".dxf" => "CAD drawing; use this path with the Revit tools, e.g. import_cad. It can't be read as text",
                ".rvt" or ".rfa" => "Revit file; use this path with the Revit tools (e.g. link_model or copy_from_file)",
                ".xlsx" or ".xls" or ".csv" => "spreadsheet; the Revit tools that take a file (e.g. place_many, boq_check) accept this path",
                _ when ImageType(a.Path) is not null => "image (more than " + MaxImages + " images: read it with the Read tool)",
                _ => "file; read it with the Read tool if it is text",
            };
            notes.Append("- ").Append(a.Path).Append(" (").Append(kind).Append(")\n");
        }
        var body = text.Length > 0 ? text : attachments.Count == 1 ? "Please look at the attached file." : "Please look at the attached files.";
        if (notes.Length > 0) body += "\n\nAttached files (full paths on this PC):\n" + notes.ToString().TrimEnd();
        if (images.Count > 0)
            body += "\n\n(Attached picture" + (images.Count > 1 ? "s" : "") + ": " +
                    string.Join(", ", attachments.Where(a => a.IsImage).Take(MaxImages).Select(a => a.Name)) + ")";
        return StreamInput.UserMessage(body, images);
    }

    private static string? ImageType(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" or ".tif" or ".tiff" => "image/png", // converted
        _ => null,
    };

    /// <summary>
    /// A picture as an image block: passed as is when it is a small PNG/JPEG/GIF/WebP, otherwise scaled to at most
    /// 1568 px on the long side (what Claude looks at anyway) and re-encoded, keeping it under the API's size limit.
    /// </summary>
    private static (string MediaType, string Base64) EncodeImage(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var type = ImageType(path)!;
        var native = System.IO.Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp";
        if (native && bytes.Length <= 3_500_000 && type == "image/webp") return (type, Convert.ToBase64String(bytes)); // WPF may not decode it

        // The size from the header only, so a huge photo is never decoded at full resolution.
        int width = 0, height = 0;
        try
        {
            var probe = BitmapDecoder.Create(new MemoryStream(bytes), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            (width, height) = (probe.PixelWidth, probe.PixelHeight);
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException) { }
        if (native && bytes.Length <= 3_500_000 && width > 0 && Math.Max(width, height) <= 2000) return (type, Convert.ToBase64String(bytes));

        // Decoded straight to at most 1568 px on the long side (setting one side keeps the aspect ratio).
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(bytes);
        if (width <= 0 || Math.Max(width, height) > 1568)
        {
            if (width >= height) image.DecodePixelWidth = 1568;
            else image.DecodePixelHeight = 1568;
        }
        image.EndInit();
        image.Freeze();
        BitmapSource source = image;
        var png = Encode(new PngBitmapEncoder(), source);
        if (png.Length <= 3_500_000) return ("image/png", Convert.ToBase64String(png));
        var jpeg = Encode(new JpegBitmapEncoder { QualityLevel = 85 }, new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0));
        return ("image/jpeg", Convert.ToBase64String(jpeg));
    }

    private static byte[] Encode(BitmapEncoder encoder, BitmapSource source)
    {
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    // ---- Events from Claude Code ----------------------------------------------------------------------------------

    private void OnEvent(ChatEvent e)
    {
        _lastActivity = DateTime.UtcNow;
        var turn = _turn;
        switch (e)
        {
            case InitEvent init:
                if (init.SessionId.Length > 0) _conv.SessionId = init.SessionId;
                var server = init.Servers.FirstOrDefault(s => s.Name == ClaudeCli.ServerName);
                if (server.Name is null)
                    Note("The Revit tools didn't load in this chat. Run Install.cmd from the Sketch ProMax download again, then press New chat.");
                else if (server.Status is not ("connected" or "pending" or ""))
                    Note($"The Revit tools didn't connect ({server.Status}). Check that Sketch ProMax is installed (Install.cmd), then press New chat.");
                if (turn is not null && !turn.Stopping) ShowStatus();
                break;
            case TextStartEvent start:
            {
                JobWriting();
                var item = new ChatItem { Kind = "assistant" };
                Add(item);
                _texts[start.Key] = (item, _views[item]);
                if (turn is not null) turn.SawText = true;
                if (turn is { Stopping: false }) ShowStatus("Claude is writing…");
                break;
            }
            case TextDeltaEvent delta:
                if (!_texts.TryGetValue(delta.Key, out var target)) break;
                target.Item.Text += delta.Text;
                _dirty.Add(delta.Key);
                RequestRender();
                break;
            case AssistantTextEvent text:
                if (text.Text.Trim().Length == 0) break;
                JobWriting();
                if (turn is not null) turn.SawText = true;
                Add(new ChatItem { Kind = "assistant", Text = text.Text });
                break;
            case ThinkingEvent:
                JobThinking("", newBlock: true);
                if (turn is { Stopping: false }) ShowStatus("Claude is thinking…");
                break;
            case ThinkingDeltaEvent thought:
                JobThinking(thought.Text, newBlock: false);
                break;
            case ThinkingTextEvent whole:
                JobThinking(whole.Text, newBlock: true);
                break;
            case ToolUseEvent tool:
            {
                var summary = Summary(tool.InputJson);
                if (!_tools.TryGetValue(tool.Id, out var row))
                {
                    var item = new ChatItem { Kind = "tool", Name = StreamParser.ShortName(tool.Name), State = "running", Text = summary };
                    Add(item);
                    row = _rows[item];
                    _tools[tool.Id] = row;
                    JobToolStarted(tool.Id, item.Name, summary);
                }
                if (turn is not null) turn.SawTool = true;
                if (tool.InputJson.Length > 0)
                {
                    row.Item.Input = Pretty(tool.InputJson);
                    SetTool(row, row.Item.State, summary, "");
                    if (summary.Length > 0) _jobInputs[tool.Id] = summary;
                }
                if (turn is { Stopping: false })
                    ShowStatus($"Running {row.Item.Name}… If Revit asks to approve a change, answer it in Revit.");
                break;
            }
            case ToolResultEvent result:
                if (_tools.TryGetValue(result.ToolUseId, out var done))
                    SetTool(done, result.IsError ? "failed" : "done", done.Item.Text, result.Text);
                JobToolEnded(result.ToolUseId, result.Text, result.IsError);
                if (turn is { Stopping: false }) ShowStatus("Claude is thinking…");
                break;
            case RetryEvent retry:
                if (turn is { Stopping: false })
                    ShowStatus(retry.Error is "rate_limit" or "overloaded"
                        ? $"Claude is busy right now; trying again (attempt {retry.Attempt} of {retry.MaxRetries})…"
                        : $"Couldn't reach Claude ({retry.Error}); trying again (attempt {retry.Attempt} of {retry.MaxRetries})…");
                break;
            case NoticeEvent notice:
                Note(notice.Text);
                break;
            case ResultEvent end:
                OnResult(end);
                break;
        }
    }

    private void OnResult(ResultEvent end)
    {
        var turn = _turn;
        if (end.SessionId.Length > 0) _conv.SessionId = end.SessionId;
        RenderDirty();
        if (turn is null) return;
        var message = end.Text.Trim().Length > 0 ? end.Text.Trim() : string.Join(" ", end.Errors);
        if (end.IsError && !turn.Stopping && !turn.Retried && _engine?.ResumedFrom is not null &&
            message.IndexOf("No conversation found", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            // The saved session is gone (deleted, or made on another PC): start fresh with the same message.
            turn.Retried = true;
            _conv.SessionId = "";
            _engine?.Kill();
            _engine = null;
            Note("The earlier conversation couldn't be reopened, so Claude starts fresh (it doesn't remember the messages above).");
            _ = Deliver(turn);
            return;
        }
        if (end.IsError && !turn.Stopping && ChatModels.IsRejection(message) && _engine is { } refused && FallBack(turn, refused.Model, message)) return;
        if (!end.IsError && _engine is { } worked) _lastWorkingModel = worked.Model;
        if (end.IsError) turn.Outcome = "failed";
        if (turn.Stopping)
            Note("Stopped. Ask again, or tell Claude what to do instead.");
        else if (end.IsError)
            Failure(message, end.Subtype);
        else if (!turn.SawText && message.Length > 0)
            Add(new ChatItem { Kind = "assistant", Text = message });
        EndTurn();
    }

    private void Failure(string message, string subtype)
    {
        if (LooksSignedOut(message))
        {
            _setup = Setup.SignedOut;
            ShowSetup();
            Error("Claude Code isn't signed in. Press Sign in above, then send your message again.");
            return;
        }
        if (subtype == "error_max_turns")
        {
            Error("Claude stopped after many steps. Say \"continue\" to let it carry on.");
            return;
        }
        if (Regex.IsMatch(message, @"usage limit|rate limit|limit reached|out of (extra )?usage", RegexOptions.IgnoreCase))
        {
            Error($"Your Claude plan's usage limit is reached: {message}. The conversation is kept; send again when the limit resets.");
            return;
        }
        Error((message.Length > 0 ? message : "Claude Code reported an error.") + " Send your message again; if it keeps happening, press New chat.");
    }

    /// <summary>
    /// The plan can't use the chosen model: say so with Claude Code's own words, go back to the model that last worked
    /// (or the default) and send the same message again, once. False when there is nothing to go back to.
    /// </summary>
    private bool FallBack(Turn turn, string refused, string said)
    {
        // Only a turn that did nothing yet is sent again: never after text was streamed or a tool ran (it could run twice).
        if (turn.SawText || turn.SawTool || turn.ModelRetried || ChatModels.Fallback(refused, _lastWorkingModel) is not { } back) return false;
        turn.ModelRetried = true;
        _engine?.Kill();
        _engine = null;
        _model = back;
        ChatModels.Save(ClaudeCli.Folder, back);
        ShowModelChoice();
        Error($"{ChatModels.Find(refused).Label} isn't available with your Claude plan, so the chat went back to {ChatModels.Find(back).Label} " +
              $"and sent your message again. Claude Code said: {said.Trim()}");
        _ = Deliver(turn);
        return true;
    }

    private static bool LooksSignedOut(string text) =>
        Regex.IsMatch(text, @"not logged in|/login\b|\blog ?in again|invalid api key|authentication_failed|authentication failed|oauth token (has )?expired|please run .*login|\bsign in again\b", RegexOptions.IgnoreCase);

    private void OnExited(ChatEngine engine, string tail)
    {
        _engine = null;
        engine.Dispose();
        var turn = _turn;
        if (turn is null) return; // an idle engine ended: the next message starts another
        RenderDirty();
        if (turn.Stopping)
        {
            Note("Stopped. Ask again, or tell Claude what to do instead.");
            EndTurn();
            return;
        }
        if (!engine.Initialized && engine.ResumedFrom is not null && !turn.Retried)
        {
            // The saved session couldn't be reopened (deleted, or from another PC): start fresh with the same message.
            turn.Retried = true;
            _conv.SessionId = "";
            Note("The earlier conversation couldn't be reopened, so Claude starts fresh (it doesn't remember the messages above).");
            _ = Deliver(turn);
            return;
        }
        if (ChatModels.IsRejection(tail) && FallBack(turn, engine.Model, Last(tail, 300))) return;
        MarkRunningTools("stopped");
        EndTurn();
        var detail = Last(tail, 500);
        if (LooksSignedOut(tail))
        {
            _setup = Setup.SignedOut;
            ShowSetup();
            Error("Claude Code isn't signed in. Press Sign in above, then send your message again.");
        }
        else if (Regex.IsMatch(tail, @"unknown option|unrecognized|invalid value", RegexOptions.IgnoreCase))
            Error("This Claude Code is too old for the Revit chat. Update it (open a Command Prompt and run: claude update), then send again." +
                  (detail.Length > 0 ? "\n\n" + detail : ""));
        else
            Error("Claude Code stopped unexpectedly. Send your message again; the conversation is kept." + (detail.Length > 0 ? "\n\n" + detail : ""));
    }

    private static string Last(string text, int max)
    {
        text = text.Trim();
        return text.Length <= max ? text : "…" + text.Substring(text.Length - max);
    }

    private void MarkRunningTools(string state)
    {
        foreach (var row in _tools.Values.Where(r => r.Item.State == "running").ToList()) SetTool(row, state, row.Item.Text, "");
    }

    private void EndTurn()
    {
        if (_turn?.KillTimer is { } timer) timer.Stop();
        // "Done in X. By hand: about Y. Saved: Z." when the task's tools saved any time: on the job card when it has one.
        string? time = null;
        try { if (_turn is not null) time = Revit.TimeLog.EndChat(); } catch (Exception) { }
        var stopped = _turn?.Stopping == true || _control?.State == JobState.Stopping;
        var left = new List<(string Id, string Text)>();
        if (_control is not null) left = EndJob(stopped ? "stopped" : _turn?.Outcome ?? "done", time);
        else if (time is not null) Note(time);
        MarkRunningTools("stopped");
        _turn = null;
        // Idle now: the streaming keys and tool ids of the turn are no longer needed (the drawn items stay in _views/_rows).
        if (_dirty.Count > 0) RenderDirty();
        _texts.Clear();
        _tools.Clear();
        _lastActivity = DateTime.UtcNow;
        Save();
        ShowStatus();
        TrimDrawn();
        // Messages sent during the task that no step picked up: sent now (or back in the box after a stop or a switch).
        SendLeftover(left, stopped || _pendingModel is not null);
        if (_pendingModel is { } next)
        {
            _pendingModel = null;
            SwitchTo(next.Key, next.Title);
        }
    }

    private void StopTurn()
    {
        var turn = _turn;
        if (turn is null || turn.Stopping) return;
        if (turn.Planning)
        {
            StopBrief(turn);
            return;
        }
        turn.Stopping = true;
        // The running Revit tool stops at its next item too: the item in progress is rolled back, finished ones kept.
        _control?.Apply(JobAction.Stop);
        Brand.SetBusy(_stop, true, ""); // a spinner in the round button; the status line says "Stopping…"
        if (_stop.Content is StackPanel { Children.Count: > 0 } busy && busy.Children[0] is FrameworkElement spinner) spinner.Margin = new Thickness(0);
        ShowStatus("Stopping…");
        var engine = _engine;
        if (engine is { IsAlive: true }) _ = engine.InterruptAsync().ContinueWith(_ => { }, TaskScheduler.Default);
        // If Claude Code doesn't end the turn soon (a tool call waiting on Revit, say), end the process; the
        // conversation is resumed with the next message.
        turn.KillTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(engine is { IsAlive: true } ? 6 : 0.1) };
        turn.KillTimer.Tick += (_, _) =>
        {
            turn.KillTimer.Stop();
            if (_turn != turn) return;
            _engine?.Kill();
            _engine = null;
            RenderDirty();
            Note("Stopped. Ask again, or tell Claude what to do instead. (A Revit tool that was already running finishes on its own.)");
            EndTurn();
        };
        turn.KillTimer.Start();
    }

    private void NewChat()
    {
        if (_stopped) return;
        // The chat shown stays in the history; a running answer is stopped (the chat resumes when reopened).
        if (_turn is not null) MarkRunningTools("stopped");
        _turn?.KillTimer?.Stop();
        // A brief being planned: end its Claude Code run now, not when it finishes on its own.
        _turn?.Cancel?.Cancel();
        // The task's time ends here, or its open span would stop by-hand time being counted.
        try { if (_turn is not null) Revit.TimeLog.EndChat(); } catch (Exception) { }
        _control?.Apply(JobAction.Stop);
        var left = _control is not null ? EndJob("stopped", null) : new List<(string Id, string Text)>();
        _turn = null;
        _engine?.Kill();
        _engine = null;
        Save();
        if (left.Count > 0 && _input.Text.Trim().Length == 0) _input.Text = string.Join("\n\n", left.Select(n => n.Text));
        _conv = ChatStore.New(ConversationKey, ConversationTitle);
        ShowConversation();
        ShowStatus();
        CloseDrawer();
        _input.Focus();
    }

    // ---- History drawer (IHistoryHost) ----------------------------------------------------------------------------

    private void ToggleDrawer()
    {
        if (_drawerOpen) CloseDrawer();
        else OpenDrawer();
    }

    private void OpenDrawer()
    {
        if (_stopped) return;
        if (_drawer is null)
        {
            _drawer = new HistoryDrawer(this) { HorizontalAlignment = HorizontalAlignment.Left };
            _center.Children.Add(_drawer);
            _center.SizeChanged += (_, _) => SizeDrawer();
        }
        SizeDrawer();
        _drawerOpen = true;
        _scrim.Visibility = Visibility.Visible;
        _drawer.Visibility = Visibility.Visible;
        // The drawer slides in from the left over a scrim that fades in; neither keeps a transform once there.
        ChatUi.Slide(_drawer, -_drawer.Width, open: true);
        ChatUi.Fade(_scrim, show: true);
        AutomationProperties.SetItemStatus(_menu, "Expanded");
        _drawer.Refresh();
        _drawer.FocusSearch();
    }

    private void SizeDrawer()
    {
        if (_drawer is not null) _drawer.Width = Math.Max(200, Math.Min(340, _center.ActualWidth - 40));
    }

    public void CloseDrawer()
    {
        if (_drawer is not { } drawer || !_drawerOpen) return;
        _drawerOpen = false;
        ChatUi.Slide(drawer, -drawer.Width, open: false, () => { if (!_drawerOpen) drawer.Visibility = Visibility.Collapsed; });
        ChatUi.Fade(_scrim, show: false, () => { if (!_drawerOpen) _scrim.Visibility = Visibility.Collapsed; });
        AutomationProperties.SetItemStatus(_menu, "Collapsed");
        _menu.Focus();
    }

    string IHistoryHost.ModelKey => ConversationKey;

    string IHistoryHost.CurrentFile => _conv.File;

    void IHistoryHost.NewChat() => NewChat();

    void IHistoryHost.OpenChat(ChatSummary chat)
    {
        if (string.Equals(chat.File, _conv.File, StringComparison.OrdinalIgnoreCase))
        {
            CloseDrawer();
            return;
        }
        if (_turn is not null)
        {
            Status("Claude is still answering. Wait for it, or press Stop, then open the other chat.", spin: false);
            CloseDrawer();
            return;
        }
        if (ChatStore.Read(chat.File) is not { } conv)
        {
            Status("That chat couldn't be read. Open the history again and press Refresh.", spin: false);
            return;
        }
        Save();
        _engine?.Dispose();
        _engine = null;
        _conv = conv;
        ShowConversation();
        ShowStatus();
        CloseDrawer();
        _input.Focus();
    }

    void IHistoryHost.ChangeChat(string file, Action<Conversation> change)
    {
        if (string.Equals(file, _conv.File, StringComparison.OrdinalIgnoreCase))
        {
            change(_conv);
            ChatStore.Save(_conv, touch: false);
            ShowTitle();
            return;
        }
        if (ChatStore.Read(file) is not { } conv) return;
        change(conv);
        ChatStore.Save(conv, touch: false);
    }

    bool IHistoryHost.DeleteChat(string file)
    {
        var current = string.Equals(file, _conv.File, StringComparison.OrdinalIgnoreCase);
        if (current && _turn is not null) return false;
        if (!ChatStore.Delete(file)) return false;
        if (current)
        {
            _engine?.Dispose();
            _engine = null;
            _conv = ChatStore.New(ConversationKey, ConversationTitle);
            ShowConversation();
            ShowStatus();
        }
        return true;
    }

    private void ModelChanged()
    {
        if (_stopped) return;
        if (_turn is not null) _pendingModel = (ConversationKey, ConversationTitle);
        else SwitchTo(ConversationKey, ConversationTitle);
    }

    private void SwitchTo(string key, string title)
    {
        if (string.Equals(key, _conv.Key, StringComparison.OrdinalIgnoreCase)) return;
        Save();
        _engine?.Dispose();
        _engine = null;
        _conv = ChatStore.Load(key, title);
        ShowConversation();
        ShowStatus();
    }

    private void IdleCheck()
    {
        // An idle Claude Code (and its MCP server) is ended after a while; the next message resumes the session.
        if (_turn is null && _engine is not null && DateTime.UtcNow - _lastActivity > TimeSpan.FromMinutes(20))
        {
            _engine.Dispose();
            _engine = null;
        }
    }

    private void Save()
    {
        if (_sample) return; // the DPI snapshot's and frame test's stand-alone pane keeps nothing
        var old = _conv.Items.ToList();
        ChatStore.Save(_conv);
        // The store keeps the latest items only: let go of the drawn ones it dropped too.
        var removed = old.Count - _conv.Items.Count;
        if (removed <= 0) return;
        Forget(old.Take(removed));
        var drawnGone = Math.Min(Math.Max(0, removed - _firstShown), _messages.Children.Count);
        if (drawnGone > 0) _messages.Children.RemoveRange(0, drawnGone);
        _firstShown = Math.Max(0, _firstShown - removed);
        ShowEarlierButton();
    }

    /// <summary>This core is stopping: end the engine and keep the conversation for the next core.</summary>
    private void Retire()
    {
        _stopped = true;
        _poll.Stop();
        // Nothing may keep a retired pane alive after a hot swap: timers (rooted by the dispatcher while running) and static events.
        _idle.Stop();
        _render.Stop();
        UI.RestartNote.Changed -= ShowRestartNote;
        _turn?.KillTimer?.Stop();
        _turn?.Cancel?.Cancel();
        if (_turn is not null) MarkRunningTools("stopped");
        try { if (_turn is not null) Revit.TimeLog.EndChat(); } catch (Exception) { }
        if (_control is not null)
        {
            _control.Apply(JobAction.Stop);
            EndJob("stopped", null);
        }
        _undoTimer?.Stop();
        _turn = null;
        Save();
        _engine?.Kill();
        _engine = null;
        ShowStatus();
        Brand.ReleaseTheme(this);
        _instance = null;
        _retired = this;
    }

    // ---- Attachments ----------------------------------------------------------------------------------------------

    private void PickFiles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Attach files for Claude",
            Multiselect = true,
            Filter = "Pictures, PDFs and drawings|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp;*.tif;*.tiff;*.pdf;*.dwg;*.dxf|All files|*.*",
        };
        var owner = Window.GetWindow(this);
        if ((owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner)) == true) AddFiles(dialog.FileNames);
        _input.Focus();
    }

    private void AddFiles(IEnumerable<string> files)
    {
        foreach (var file in files)
        {
            if (Directory.Exists(file))
            {
                Status($"{System.IO.Path.GetFileName(file)} is a folder; attach the files in it instead.", spin: false);
                continue;
            }
            if (!File.Exists(file) || _pending.Any(p => string.Equals(p.Path, file, StringComparison.OrdinalIgnoreCase))) continue;
            _pending.Add(new Attachment { Path = file, Name = System.IO.Path.GetFileName(file) });
        }
        ShowChips();
    }

    private void ShowChips()
    {
        _chips.Children.Clear();
        _chips.Margin = new Thickness(ChatUi.S2, _pending.Count > 0 ? ChatUi.S2 : 0, ChatUi.S2, 0);
        foreach (var a in _pending)
        {
            var row = new DockPanel();
            var remove = ChatUi.Ghost(ChatUi.GlyphCross, null, "Remove attachment " + a.Name, "Remove " + a.Name);
            remove.Padding = new Thickness(4, 0, 4, 0);
            remove.Margin = new Thickness(4, 0, 0, 0);
            var attachment = a;
            remove.Click += (_, _) =>
            {
                _pending.Remove(attachment);
                ShowChips();
                _input.Focus();
            };
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            if (a.IsImage)
            {
                var thumb = ChatUi.Thumbnail(a.Path, 28);
                thumb.Margin = new Thickness(0, 0, ChatUi.S1, 0);
                DockPanel.SetDock(thumb, Dock.Left);
                row.Children.Add(thumb);
            }
            else
            {
                var icon = ChatUi.Glyph(ChatUi.GlyphFile, Brand.K.Muted);
                icon.Margin = new Thickness(2, 0, ChatUi.S1 + 2, 0);
                DockPanel.SetDock(icon, Dock.Left);
                row.Children.Add(icon);
            }
            row.Children.Add(new TextBlock
            {
                Text = a.Name, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 200,
                VerticalAlignment = VerticalAlignment.Center, ToolTip = a.Path, FontSize = ChatUi.SmallSize,
            });
            var chip = new Border { Child = row, Padding = new Thickness(4, 2, 2, 2), Margin = new Thickness(0, 0, ChatUi.S1, ChatUi.S1), CornerRadius = new CornerRadius(ChatUi.RadiusSmall), BorderThickness = new Thickness(1) };
            AutomationProperties.SetName(chip, (a.IsImage ? "Picture " : "File ") + a.Name);
            chip.SetResourceReference(Border.BackgroundProperty, Brand.K.Surface);
            chip.SetResourceReference(Border.BorderBrushProperty, Brand.K.BorderSubtle);
            _chips.Children.Add(chip);
        }
    }

    private void OnPreviewCanPaste(object sender, CanExecuteRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Paste) return;
        try
        {
            if (Clipboard.ContainsImage() || Clipboard.ContainsFileDropList())
            {
                e.CanExecute = true;
                e.Handled = true;
            }
        }
        catch (System.Runtime.InteropServices.COMException) { }
    }

    private void OnPreviewPaste(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Paste) return;
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                e.Handled = true;
                AddFiles(Clipboard.GetFileDropList().Cast<string>());
                return;
            }
            if (!Clipboard.ContainsImage()) return; // ordinary text paste
            e.Handled = true;
            byte[] png;
            if (Clipboard.GetData("PNG") is MemoryStream stream) png = stream.ToArray();
            else if (Clipboard.GetImage() is { } image)
                // Screenshots often come as 32-bit bitmaps with an empty alpha channel: drop the alpha.
                png = Encode(new PngBitmapEncoder(), new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0));
            else return;
            AddFiles(new[] { SaveAttachment(png, "Pasted picture") });
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            Status("The clipboard couldn't be read: " + ex.Message, spin: false);
        }
    }

    private static string SaveAttachment(byte[] png, string name)
    {
        Directory.CreateDirectory(ClaudeCli.AttachmentsFolder);
        var stamp = DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss", CultureInfo.InvariantCulture);
        var safe = string.Concat(name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var path = System.IO.Path.Combine(ClaudeCli.AttachmentsFolder, $"{safe} {stamp}.png");
        File.WriteAllBytes(path, png);
        return path;
    }

    /// <summary>The ribbon's Send view button (the loader shows the chat first). False when the chat isn't there.</summary>
    internal static bool AttachCurrentView()
    {
        if (_instance is null) return false;
        _instance.AttachView();
        return true;
    }

    /// <summary>"Send current view": the same capture as capture_view_image (screen when possible), run on Revit's thread.</summary>
    private async void AttachView()
    {
        if (Brand.IsBusy(_view) || _stopped) return;
        Brand.SetBusy(_view, true, "");
        Status("Capturing the active view…", spin: true);
        try
        {
            var result = await App.Dispatcher.InvokeAsync(app =>
            {
                if (app.ActiveUIDocument?.Document is null) return ToolResult.Error("Open a model and a view first, then press the camera button again.");
                try
                {
                    var args = new Dictionary<string, JsonElement> { ["pixel_width"] = JsonDocument.Parse("1600").RootElement.Clone() };
                    return new CaptureViewImageTool().Execute(app, new ToolArgs(args));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return ToolResult.Error("The view couldn't be captured: " + ex.Message);
                }
            });
            if (result.IsError || result.PngImage is not { Length: > 0 } png)
            {
                Status(result.IsError ? result.Text : "The view couldn't be captured. Try again, or attach a screenshot.", spin: false);
                return;
            }
            var name = Regex.Match(result.Text, @"view '([^']+)'") is { Success: true } m ? "View " + m.Groups[1].Value : "Current view";
            AddFiles(new[] { SaveAttachment(png, name) });
            ShowStatus();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
        {
            Status("The view couldn't be captured: " + ex.Message, spin: false);
        }
        finally
        {
            Brand.SetBusy(_view, false);
        }
    }

    // ---- Helpers --------------------------------------------------------------------------------------------------

    /// <summary>A tool call's input in one short line: key=value, key=value…</summary>
    internal static string Summary(string json)
    {
        if (json.Length == 0) return "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return "";
            var parts = new List<string>();
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                var v = p.Value.ValueKind switch
                {
                    JsonValueKind.String => p.Value.GetString() ?? "",
                    JsonValueKind.Array => $"[{p.Value.GetArrayLength()}]",
                    JsonValueKind.Object => "{…}",
                    _ => p.Value.GetRawText(),
                };
                v = v.Replace('\n', ' ');
                if (v.Length > 32) v = v.Substring(0, 31) + "…";
                parts.Add($"{p.Name}={v}");
            }
            var line = string.Join(", ", parts);
            return line.Length > 120 ? line.Substring(0, 119) + "…" : line;
        }
        catch (JsonException) { return ""; }
    }

    private static string Pretty(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var text = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
            return text.Length > 1200 ? text.Substring(0, 1200) + "…" : text;
        }
        catch (JsonException) { return json; }
    }
}
