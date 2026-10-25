using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using SketchProMax.Tools;

namespace SketchProMax.UI;

/// <summary>
/// The hand-off window: a step only the user can do, with what to do, where in Revit and why, and Continue (re-check
/// and resume), Skip this item and Stop. Modeless and owned by Revit, so the user can work in Revit with it open; it
/// closes itself when the step is done, skipped or stopped. The chat shows the same request as a card instead
/// (Chat/HandoffCard) when the chat is running the task.
/// </summary>
internal sealed class HandoffWindow : Window
{
    private readonly HandoffRequest _request;
    private readonly HandoffView _view;
    private bool _closing;

    /// <summary>Opens a window for the request. UI thread.</summary>
    public static void Open(HandoffRequest request)
    {
        var window = new HandoffWindow(request);
        window.Show();
        window.Activate();
    }

    private HandoffWindow(HandoffRequest request)
    {
        _request = request;
        Title = "Sketch ProMax: over to you";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 720;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Brand.Prepare(this);

        _view = new HandoffView(request, compact: false);
        var root = new DockPanel();
        var header = Brand.Header("Over to you", request.Count > 1 ? $"Step {request.Index} of {request.Count}" : "One step only you can do in Revit");
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(new ScrollViewer
        {
            Content = _view, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });
        Content = root;

        // Bottom right of the screen, out of the way of the drawing area; the user moves it if needed.
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - 24;
            Top = Math.Max(area.Top + 24, area.Bottom - ActualHeight - 24);
            _view.Continue.Focus();
        };
        request.Changed += OnChanged;
        Closing += (_, e) =>
        {
            if (_closing || _request.State == HandoffState.Closed) return;
            // Closing would leave the task waiting: say how to end it instead.
            e.Cancel = true;
            _view.Say("Choose Continue when it is done, Skip this item, or Stop the task.");
        };
        Closed += (_, _) => request.Changed -= OnChanged;
    }

    private void OnChanged(HandoffRequest r)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (r.State != HandoffState.Closed) return;
            _closing = true;
            try { Close(); } catch (InvalidOperationException) { }
        }));
    }
}

/// <summary>
/// The content shared by the window and the chat card: the step (what, where, why), a line saying why it is asked
/// again, an optional guide link, and the three buttons. Follows the request; buttons answer it.
/// </summary>
internal sealed class HandoffView : StackPanel
{
    private readonly HandoffRequest _request;
    private readonly TextBlock _note;
    private readonly StackPanel _buttons;
    private readonly TextBlock _outcome;
    private readonly bool _compact;
    public Button Continue { get; }

    public HandoffView(HandoffRequest request, bool compact)
    {
        _request = request;
        _compact = compact;
        Margin = compact ? new Thickness(0) : new Thickness(18, 14, 18, 14);
        var step = request.Step;
        double size = compact ? 12.5 : 13;

        if (!string.IsNullOrWhiteSpace(step.Item)) Children.Add(Label("Item", step.Item!, Brand.K.Muted, size));
        Children.Add(Label("What to do", step.What, Brand.K.Text, size + 0.5, bold: true));
        Children.Add(Label("Where in Revit", step.Where, Brand.K.Text, size));
        Children.Add(Label("Why", step.Why, Brand.K.Muted, size));

        if (step.Guide is { } guide && HelpGuides.Read(guide) is { } markdown)
        {
            var link = new Hyperlink(new Run("Open the step-by-step guide")) { ToolTip = HelpGuides.Title(markdown) };
            link.SetResourceReference(TextElement.ForegroundProperty, Brand.K.Link);
            link.Click += (_, _) => new ResultWindow(HelpGuides.Title(markdown), true, HelpGuides.Title(markdown), markdown).Show();
            AutomationProperties.SetName(link, "Open the step-by-step guide: " + HelpGuides.Title(markdown));
            Children.Add(new TextBlock(link) { Margin = new Thickness(0, 2, 0, 6), FontSize = size, MinHeight = 24 });
        }

        // WarningText meets AA on the window's background but not on the chat card's accent tint: DangerText there.
        _note = Brand.Text("", compact ? Brand.K.DangerText : Brand.K.WarningText, size, bold: true);
        _note.Margin = new Thickness(0, 4, 0, 0);
        _note.Visibility = Visibility.Collapsed;
        AutomationProperties.SetLiveSetting(_note, AutomationLiveSetting.Assertive);
        Children.Add(_note);

        // One primary action (Continue, the default); Stop is red and never the default.
        Continue = Brand.Button("_Continue", Brand.Weight.Primary);
        Continue.IsDefault = !compact;
        Continue.ToolTip = "Done it: check again and carry on where the task stopped.";
        AutomationProperties.SetName(Continue, "Continue");
        AutomationProperties.SetHelpText(Continue, "Checks the step is done, then carries on where the task stopped.");
        Continue.Click += (_, _) => { if (!Brand.IsBusy(Continue)) _request.Answer(HandoffChoice.Continue); };
        var skip = Brand.Button("_Skip this item");
        skip.ToolTip = "Leave this item undone and carry on with the rest.";
        AutomationProperties.SetName(skip, "Skip this item");
        skip.Click += (_, _) => _request.Answer(HandoffChoice.Skip);
        var stop = Brand.Button("S_top", Brand.Weight.Danger);
        stop.ToolTip = "End the task here. What it did so far is kept.";
        AutomationProperties.SetName(stop, "Stop the task");
        stop.Click += (_, _) => _request.Answer(HandoffChoice.Stop);
        _buttons = Brand.ButtonRow(stop, skip, Continue);
        if (compact) { _buttons.HorizontalAlignment = HorizontalAlignment.Left; _buttons.Margin = new Thickness(-8, 10, 0, 0); }
        Children.Add(_buttons);

        _outcome = Brand.Text("", Brand.K.Muted, size);
        _outcome.Margin = new Thickness(0, 8, 0, 0);
        _outcome.Visibility = Visibility.Collapsed;
        AutomationProperties.SetLiveSetting(_outcome, AutomationLiveSetting.Polite);
        Children.Add(_outcome);

        request.Changed += OnChanged;
        Unloaded += (_, _) => request.Changed -= OnChanged;
        Loaded += (_, _) => { request.Changed -= OnChanged; request.Changed += OnChanged; Refresh(); };
        Refresh();
    }

    /// <summary>Shows a line in the note's place (why it is asked again, or how to end it).</summary>
    public void Say(string text)
    {
        _note.Text = text;
        _note.Visibility = Visibility.Visible;
    }

    private void OnChanged(HandoffRequest r) => Dispatcher.BeginInvoke(new Action(Refresh));

    private void Refresh()
    {
        if (_request.Note is { Length: > 0 } note) Say(note);
        var checking = _request.State == HandoffState.Checking;
        Brand.SetBusy(Continue, checking, "Checking…");
        if (_request.State == HandoffState.Closed)
        {
            _buttons.Visibility = Visibility.Collapsed;
            _note.Visibility = Visibility.Collapsed;
            _outcome.Text = _request.Outcome switch
            {
                "Done" => "✓  Done; the task carried on.",
                "Skipped" => "Skipped; the task carried on without it.",
                "Stopped" => "Stopped here; what was done so far is kept.",
                { } other => other,
                null => "",
            };
            _outcome.SetResourceReference(TextBlock.ForegroundProperty, _request.Outcome != "Done" ? Brand.K.Muted : _compact ? Brand.K.Text : Brand.K.SuccessText); // SuccessText is AA on Bg, not on the card's tint
            _outcome.Visibility = Visibility.Visible;
        }
    }

    private static StackPanel Label(string label, string text, string colour, double size, bool bold = false)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        panel.Children.Add(Brand.Text(label.ToUpperInvariant(), Brand.K.Muted, size - 2.5, bold: true));
        panel.Children.Add(Brand.Text(text, colour, size, bold));
        AutomationProperties.SetName(panel, label + ": " + text);
        return panel;
    }
}
