using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using SketchProMax.Settings;
using SketchProMax.Tools;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using Grid = System.Windows.Controls.Grid;
using ListBox = System.Windows.Controls.ListBox;
using TextBox = System.Windows.Controls.TextBox;
using Visibility = System.Windows.Visibility;

namespace SketchProMax.UI;

/// <summary>
/// Edits the pre-submission checklist (from the checklist window's Project details): stages on the left, the stage's
/// checks in the middle, the chosen check on the right. Add, change, move and delete checks and stages; change the
/// title and who signs. Export to Excel / Import from Excel edit it outside Revit in the office's layout
/// (ChecklistXlsx / ChecklistMath.ReadXlsx). Save renumbers the checks 1, 2, 3… and keeps the checklist on this PC
/// (ChecklistStore); answers already given stay with their checks by wording. Modal; no Revit API.
/// Keys: Ctrl+S saves, Esc cancels, Ctrl+Up / Ctrl+Down move the chosen check.
/// </summary>
internal sealed class ChecklistEditor : Window
{
    /// <summary>A line in either list; the label follows the text as it is typed.</summary>
    private sealed class Row : INotifyPropertyChanged
    {
        private string _label = "";
        public string Label { get => _label; set { _label = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed class Stage
    {
        public string Title = "";
        public List<Check> Checks = [];
    }

    private sealed class Check
    {
        public string Text = "", Short = "", Fix = "", Example = "", From = "", FoundOn = "", Priority = "", Waiting = "";
    }

    private readonly AgentSettings _settings;
    private ChecklistMath.Definition _base;
    private readonly List<Stage> _model = [];
    private readonly ObservableCollection<Row> _stageRows = [];
    private readonly ObservableCollection<Row> _checkRows = [];
    private readonly ListBox _stageList = new();
    private readonly ListBox _checkList = new();
    private readonly TextBox _stageName = Box(), _title = Box(), _roles = Box();
    private readonly TextBox _text = Box(multi: true), _short = Box(), _fix = Box(multi: true), _example = Box(multi: true), _from = Box(), _foundOn = Box(), _waiting = Box();
    private readonly ComboBox _priority = new() { IsEditable = true, MinHeight = 28, Margin = new Thickness(0, 4, 0, 0) };
    private readonly ComboBox _moveTo = new() { MinHeight = 28, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _error = Brand.Text("", Brand.K.DangerText, 12);
    private readonly StackPanel _details = new();
    private readonly TextBlock _noCheck = Brand.Text("This stage has no checks yet. Click Add check.", Brand.K.Muted, 12);
    private bool _loading, _changed;
    private string? _imported;

    /// <summary>The saved checklist after Save or Go back to the built-in one; null when nothing changed.</summary>
    public ChecklistMath.Definition? Saved { get; private set; }

    /// <summary>Opens the editor over <paramref name="owner"/> and returns the new checklist, or null when cancelled.</summary>
    public static ChecklistMath.Definition? Edit(Window owner, ChecklistMath.Definition current, AgentSettings settings)
    {
        var editor = new ChecklistEditor(current, settings) { Owner = owner };
        editor.ShowDialog();
        return editor.Saved;
    }

    private ChecklistEditor(ChecklistMath.Definition current, AgentSettings settings)
    {
        _settings = settings;
        _base = current;
        Title = "Sketch ProMax: edit the checklist";
        var area = SystemParameters.WorkArea;
        Width = Math.Min(1180, Math.Floor(area.Width * 0.9));
        Height = Math.Min(760, Math.Floor(area.Height * 0.9));
        MinWidth = Math.Min(820, Width);
        MinHeight = Math.Min(520, Height);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Brand.Prepare(this);
        Styles();
        RichUi.Apply(this, RichUi.MotionOf(settings));
        Content = Layout();
        Load(current);
        PreviewKeyDown += OnKey;
        Closing += OnClosing;
        Loaded += (_, _) => _checkList.Focus();
    }

    private static TextBox Box(bool multi = false) => new()
    {
        MinHeight = multi ? 64 : 28, TextWrapping = multi ? TextWrapping.Wrap : TextWrapping.NoWrap, AcceptsReturn = multi,
        VerticalScrollBarVisibility = multi ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden, MaxHeight = multi ? 140 : double.PositiveInfinity,
        VerticalContentAlignment = multi ? VerticalAlignment.Top : VerticalAlignment.Center, Margin = new Thickness(0, 4, 0, 0), MaxLength = 2000,
    };

    // ---- Layout ---------------------------------------------------------------------------------------------------

    private FrameworkElement Layout()
    {
        var header = Brand.Header("Edit the checklist", "Add, change, move or delete checks and stages. Answers already given stay with their checks.");
        DockPanel.SetDock(header, Dock.Top);

        // Stages
        _stageList.ItemsSource = _stageRows;
        _stageList.DisplayMemberPath = nameof(Row.Label);
        AutomationProperties.SetName(_stageList, "Stages");
        _stageList.SelectionChanged += (_, _) => { if (!_loading) ShowStage(); };
        AutomationProperties.SetName(_stageName, "Stage name");
        _stageName.TextChanged += (_, _) =>
        {
            if (_loading || CurrentStage is not { } st) return;
            st.Title = _stageName.Text;
            _stageRows[_stageList.SelectedIndex].Label = StageLabel(_stageList.SelectedIndex);
            RefreshMoveTo();
            Changed();
        };
        var addStage = Small(RichUi.Icon.Add, "Add stage");
        addStage.Click += (_, _) => AddStage();
        var stageUp = Small(RichUi.Icon.Up, "", "Move the stage up");
        stageUp.Click += (_, _) => MoveStage(-1);
        var stageDown = Small(RichUi.Icon.Down, "", "Move the stage down");
        stageDown.Click += (_, _) => MoveStage(1);
        var deleteStage = Small(RichUi.Icon.Delete, "Delete stage", danger: true);
        deleteStage.Click += (_, _) => DeleteStage();
        var stages = Column("Stages", _stageList, Wrap(addStage, stageUp, stageDown, deleteStage), Label("Stage name"), _stageName);

        // Checks
        _checkList.ItemsSource = _checkRows;
        _checkList.DisplayMemberPath = nameof(Row.Label);
        AutomationProperties.SetName(_checkList, "Checks in this stage");
        _checkList.SelectionChanged += (_, _) => { if (!_loading) ShowCheck(); };
        var addCheck = Small(RichUi.Icon.Add, "Add check");
        addCheck.Click += (_, _) => AddCheck();
        var checkUp = Small(RichUi.Icon.Up, "", "Move the check up (Ctrl+Up)");
        checkUp.Click += (_, _) => MoveCheck(-1);
        var checkDown = Small(RichUi.Icon.Down, "", "Move the check down (Ctrl+Down)");
        checkDown.Click += (_, _) => MoveCheck(1);
        var deleteCheck = Small(RichUi.Icon.Delete, "Delete check", danger: true);
        deleteCheck.Click += (_, _) => DeleteCheck();
        var checks = Column("Checks", _checkList, Wrap(addCheck, checkUp, checkDown, deleteCheck));

        // The chosen check
        void Bind(TextBox box, string name, Action<Check, string> set)
        {
            AutomationProperties.SetName(box, name);
            box.TextChanged += (_, _) =>
            {
                if (_loading || CurrentCheck is not { } c) return;
                set(c, box.Text);
                if (box == _text || box == _short) _checkRows[_checkList.SelectedIndex].Label = CheckLabel(_stageList.SelectedIndex, _checkList.SelectedIndex);
                Changed();
            };
        }
        Bind(_text, "Check (what to do)", (c, v) => c.Text = v);
        Bind(_short, "Short title", (c, v) => c.Short = v);
        Bind(_fix, "How to fix", (c, v) => c.Fix = v);
        Bind(_example, "Example or how to check", (c, v) => c.Example = v);
        Bind(_from, "Where it came from", (c, v) => c.From = v);
        Bind(_foundOn, "Found it on", (c, v) => c.FoundOn = v);
        Bind(_waiting, "Waiting on", (c, v) => c.Waiting = v);
        _priority.Items.Add("");
        _priority.Items.Add("High");
        AutomationProperties.SetName(_priority, "Priority");
        _priority.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) =>
        {
            if (_loading || CurrentCheck is not { } c) return;
            c.Priority = _priority.Text.Trim();
            Changed();
        }));
        AutomationProperties.SetName(_moveTo, "Stage this check is in");
        _moveTo.SelectionChanged += (_, _) => { if (!_loading) MoveToStage(_moveTo.SelectedIndex); };
        var two = new Grid();
        two.ColumnDefinitions.Add(new ColumnDefinition());
        two.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        two.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new StackPanel { Children = { Label("Found it on"), _foundOn } };
        var right = new StackPanel { Children = { Label("Priority"), _priority } };
        two.Children.Add(left);
        Grid.SetColumn(right, 2);
        two.Children.Add(right);
        _details.Children.Add(Label("Check (what to do)"));
        _details.Children.Add(_text);
        _details.Children.Add(Label("Short title (a few plain words for the list; empty uses the first sentence)"));
        _details.Children.Add(_short);
        _details.Children.Add(Label("How to fix"));
        _details.Children.Add(_fix);
        _details.Children.Add(Label("Example / how to check"));
        _details.Children.Add(_example);
        _details.Children.Add(Label("Where it came from"));
        _details.Children.Add(_from);
        _details.Children.Add(two);
        _details.Children.Add(Label("Waiting on (an open point; empty when none)"));
        _details.Children.Add(_waiting);
        _details.Children.Add(Label("Stage"));
        _details.Children.Add(_moveTo);
        var detailScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new StackPanel { Children = { _noCheck, _details } } };
        var detail = Column("The check", detailScroll, null);

        var body = new Grid { Margin = new Thickness(18, 14, 18, 0) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });
        body.Children.Add(stages);
        Grid.SetColumn(checks, 2);
        body.Children.Add(checks);
        Grid.SetColumn(detail, 4);
        body.Children.Add(detail);

        // Title and sign-off, under the lists.
        AutomationProperties.SetName(_title, "Checklist title");
        AutomationProperties.SetName(_roles, "Who signs, separated by commas");
        _title.TextChanged += (_, _) => { if (!_loading) Changed(); };
        _roles.TextChanged += (_, _) => { if (!_loading) Changed(); };
        var meta = new Grid { Margin = new Thickness(18, 10, 18, 0) };
        meta.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        meta.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        meta.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var titleBox = new StackPanel { Children = { Label("Checklist title (printed on the Word file and PDF)"), _title } };
        var rolesBox = new StackPanel { Children = { Label("Who signs (separated by commas)"), _roles } };
        meta.Children.Add(titleBox);
        Grid.SetColumn(rolesBox, 2);
        meta.Children.Add(rolesBox);

        // Footer: Excel and the built-in checklist on the left, Cancel / Save on the right.
        _error.TextWrapping = TextWrapping.Wrap;
        _error.Visibility = Visibility.Collapsed;
        _error.Margin = new Thickness(18, 8, 18, 0);
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Assertive);
        var export = RichUi.Button(RichUi.Icon.Export, "Export to Excel…");
        export.Margin = new Thickness(0);
        AutomationProperties.SetHelpText(export, "Saves the checklist as it is here as an Excel file in the office's layout, to edit in Excel and import back.");
        export.Click += (_, _) => Export();
        var import = RichUi.Button(RichUi.Icon.Import, "Import from Excel…");
        AutomationProperties.SetHelpText(import, "Replaces the checks here with an Excel file in the office's layout. Nothing is kept until you Save.");
        import.Click += (_, _) => Import();
        var reset = RichUi.Button(RichUi.Icon.Reset, "Use the built-in checklist", Brand.Weight.Danger);
        AutomationProperties.SetHelpText(reset, "Drops your edits and imports and goes back to the checklist that comes with Sketch ProMax.");
        reset.Click += (_, _) => ResetToBuiltIn();
        var cancel = RichUi.Button(RichUi.Icon.Close, "Cancel");
        cancel.IsCancel = true;
        var save = RichUi.Button(RichUi.Icon.Save, "Save", Brand.Weight.Primary);
        save.Click += (_, _) => Save();
        var leftButtons = new WrapPanel { Children = { export, import, reset }, VerticalAlignment = VerticalAlignment.Center };
        var rightButtons = Brand.ButtonRow(cancel, save);
        rightButtons.Margin = new Thickness(0);
        var footGrid = new Grid();
        footGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footGrid.Children.Add(leftButtons);
        Grid.SetColumn(rightButtons, 1);
        footGrid.Children.Add(rightButtons);
        var foot = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(18, 10, 18, 12), Margin = new Thickness(0, 12, 0, 0), Child = footGrid };
        foot.SetResourceReference(Border.BorderBrushProperty, Brand.K.Border);

        var bottom = new StackPanel { Children = { meta, _error, foot } };
        DockPanel.SetDock(bottom, Dock.Bottom);
        var root = new DockPanel();
        root.Children.Add(header);
        root.Children.Add(bottom);
        root.Children.Add(body);
        root.SetResourceReference(Panel.BackgroundProperty, Brand.K.Bg);
        return root;
    }

    private static FrameworkElement Column(string heading, FrameworkElement main, FrameworkElement? under, params FrameworkElement[] after)
    {
        var dock = new DockPanel();
        var h = Brand.Text(heading, Brand.K.Text, 13, bold: true);
        h.Margin = new Thickness(0, 0, 0, 6);
        DockPanel.SetDock(h, Dock.Top);
        dock.Children.Add(h);
        var bottom = new StackPanel();
        if (under is not null) bottom.Children.Add(under);
        foreach (var a in after) bottom.Children.Add(a);
        DockPanel.SetDock(bottom, Dock.Bottom);
        dock.Children.Add(bottom);
        dock.Children.Add(main);
        return dock;
    }

    private static WrapPanel Wrap(params Button[] buttons)
    {
        var wrap = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var b in buttons) wrap.Children.Add(b);
        return wrap;
    }

    private static Button Small(string glyph, string text, string? name = null, bool danger = false)
    {
        var b = RichUi.Button(glyph, text, danger ? Brand.Weight.Danger : Brand.Weight.Secondary);
        b.MinWidth = 32;
        b.Margin = new Thickness(0, 0, 6, 6);
        if (name is not null) AutomationProperties.SetName(b, name);
        return b;
    }

    private static TextBlock Label(string text)
    {
        var t = Brand.Text(text, Brand.K.Muted, 12);
        t.Margin = new Thickness(0, 10, 0, 0);
        return t;
    }

    /// <summary>List styles that follow Revit's theme (the selected line in the accent colour).</summary>
    private void Styles()
    {
        const string xaml = """
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Style TargetType="ListBox">
                <Setter Property="Background" Value="{DynamicResource SpmField}"/>
                <Setter Property="Foreground" Value="{DynamicResource SpmText}"/>
                <Setter Property="BorderBrush" Value="{DynamicResource SpmInputBorder}"/>
                <Setter Property="BorderThickness" Value="1"/>
                <Setter Property="ScrollViewer.HorizontalScrollBarVisibility" Value="Disabled"/>
              </Style>
              <Style TargetType="ListBoxItem">
                <Setter Property="Foreground" Value="{DynamicResource SpmText}"/>
                <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
                <Setter Property="Template"><Setter.Value>
                  <ControlTemplate TargetType="ListBoxItem">
                    <Border x:Name="b" Background="Transparent" BorderBrush="Transparent" BorderThickness="1" Padding="8,5,8,5">
                      <ContentPresenter>
                        <ContentPresenter.Resources>
                          <Style TargetType="TextBlock"><Setter Property="TextWrapping" Value="Wrap"/></Style>
                        </ContentPresenter.Resources>
                      </ContentPresenter>
                    </Border>
                    <ControlTemplate.Triggers>
                      <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="b" Property="Background" Value="{DynamicResource SpmSurfaceHover}"/></Trigger>
                      <Trigger Property="IsSelected" Value="True">
                        <Setter TargetName="b" Property="Background" Value="{DynamicResource SpmAccent}"/>
                        <Setter Property="Foreground" Value="{DynamicResource SpmOnAccent}"/>
                      </Trigger>
                      <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="b" Property="BorderBrush" Value="{DynamicResource SpmFocus}"/></Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate>
                </Setter.Value></Setter>
              </Style>
            </ResourceDictionary>
            """;
        try { Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(xaml)); }
        catch (XamlParseException) { /* plain lists still work */ }
    }

    // ---- The model ------------------------------------------------------------------------------------------------

    private Stage? CurrentStage => _stageList.SelectedIndex >= 0 && _stageList.SelectedIndex < _model.Count ? _model[_stageList.SelectedIndex] : null;

    private Check? CurrentCheck => CurrentStage is { } st && _checkList.SelectedIndex >= 0 && _checkList.SelectedIndex < st.Checks.Count ? st.Checks[_checkList.SelectedIndex] : null;

    private void Load(ChecklistMath.Definition def)
    {
        _loading = true;
        _model.Clear();
        foreach (var s in def.Sections)
            _model.Add(new Stage
            {
                Title = s.Title,
                Checks = s.Items.Select(i => new Check { Text = i.Check, Short = i.Short, Fix = i.Fix, Example = i.Example, From = i.From, FoundOn = i.FoundOn, Priority = i.Priority, Waiting = i.Waiting }).ToList(),
            });
        _title.Text = def.Title;
        _roles.Text = string.Join(", ", def.Signatories);
        _loading = false;
        RefreshStages(_model.Count > 0 ? 0 : -1);
    }

    /// <summary>The checks' numbers as Save will make them: 1, 2, 3… down the stages.</summary>
    private int NumberOf(int stage, int check) => _model.Take(stage).Sum(s => s.Checks.Count) + check + 1;

    private string StageLabel(int i) => $"{i + 1}. {(_model[i].Title.Trim().Length > 0 ? _model[i].Title.Trim() : "(no name)")}  ·  {_model[i].Checks.Count}";

    private string CheckLabel(int stage, int i)
    {
        var check = _model[stage].Checks[i];
        var text = Regex.Replace(check.Short.Trim().Length > 0 ? check.Short : check.Text, @"\s+", " ").Trim();
        if (text.Length == 0) text = "(empty: type the check)";
        else if (text.Length > 120) text = text.Substring(0, 117).TrimEnd() + "…";
        return $"{NumberOf(stage, i)}. {text}";
    }

    private void RefreshStages(int select)
    {
        _loading = true;
        _stageRows.Clear();
        for (var i = 0; i < _model.Count; i++) _stageRows.Add(new Row { Label = StageLabel(i) });
        _stageList.SelectedIndex = Math.Min(select, _model.Count - 1);
        RefreshMoveTo();
        _loading = false;
        ShowStage();
    }

    private void RefreshMoveTo()
    {
        var was = _loading;
        _loading = true;
        var keep = _moveTo.SelectedIndex;
        _moveTo.Items.Clear();
        for (var i = 0; i < _model.Count; i++) _moveTo.Items.Add($"{i + 1}. {_model[i].Title.Trim()}");
        _moveTo.SelectedIndex = _stageList.SelectedIndex >= 0 ? _stageList.SelectedIndex : Math.Min(keep, _model.Count - 1);
        _loading = was;
    }

    private void ShowStage(int selectCheck = 0)
    {
        _loading = true;
        _stageName.Text = CurrentStage?.Title ?? "";
        _stageName.IsEnabled = CurrentStage is not null;
        _checkRows.Clear();
        if (CurrentStage is { } st)
            for (var i = 0; i < st.Checks.Count; i++) _checkRows.Add(new Row { Label = CheckLabel(_stageList.SelectedIndex, i) });
        _checkList.SelectedIndex = _checkRows.Count == 0 ? -1 : Math.Max(0, Math.Min(selectCheck, _checkRows.Count - 1));
        _moveTo.SelectedIndex = _stageList.SelectedIndex;
        _loading = false;
        ShowCheck();
    }

    private void ShowCheck()
    {
        _loading = true;
        var c = CurrentCheck;
        _details.Visibility = c is null ? Visibility.Collapsed : Visibility.Visible;
        _noCheck.Visibility = c is null && CurrentStage is not null ? Visibility.Visible : Visibility.Collapsed;
        _text.Text = c?.Text ?? "";
        _short.Text = c?.Short ?? "";
        _fix.Text = c?.Fix ?? "";
        _example.Text = c?.Example ?? "";
        _from.Text = c?.From ?? "";
        _foundOn.Text = c?.FoundOn ?? "";
        _priority.Text = c?.Priority ?? "";
        _waiting.Text = c?.Waiting ?? "";
        _moveTo.SelectedIndex = _stageList.SelectedIndex;
        _loading = false;
    }

    /// <summary>Every check label after the numbers moved (an add, a delete or a move above them).</summary>
    private void Relabel()
    {
        for (var i = 0; i < _stageRows.Count && i < _model.Count; i++) _stageRows[i].Label = StageLabel(i);
        var s = _stageList.SelectedIndex;
        for (var i = 0; i < _checkRows.Count && s >= 0 && i < _model[s].Checks.Count; i++) _checkRows[i].Label = CheckLabel(s, i);
    }

    private void Changed()
    {
        _changed = true;
        _importUntouched = false; // an import stays an import (following its Excel file) only until something is changed here
        _error.Visibility = Visibility.Collapsed;
    }

    private bool _importUntouched;

    // ---- Stages and checks ----------------------------------------------------------------------------------------

    private void AddStage()
    {
        var at = Math.Max(0, _stageList.SelectedIndex + 1);
        var letter = (char)('A' + Math.Min(25, _model.Count));
        _model.Insert(at, new Stage { Title = $"{letter}. New stage", Checks = [new Check()] });
        Changed();
        RefreshStages(at);
        _stageName.Focus();
        _stageName.SelectAll();
    }

    private void MoveStage(int by)
    {
        var i = _stageList.SelectedIndex;
        var to = i + by;
        if (i < 0 || to < 0 || to >= _model.Count) return;
        (_model[i], _model[to]) = (_model[to], _model[i]);
        Changed();
        RefreshStages(to);
        _stageList.Focus();
    }

    private void DeleteStage()
    {
        if (CurrentStage is not { } st) return;
        if (_model.Count == 1) { ShowError("A checklist needs at least one stage. Add another stage first, then delete this one."); return; }
        if (st.Checks.Count > 0 && MessageBox.Show(this, $"Delete the stage \"{st.Title.Trim()}\" and its {st.Checks.Count} checks?", "Sketch ProMax",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var i = _stageList.SelectedIndex;
        _model.RemoveAt(i);
        Changed();
        RefreshStages(Math.Max(0, i - 1));
        _stageList.Focus();
    }

    private void AddCheck()
    {
        if (CurrentStage is not { } st) { ShowError("Add a stage first."); return; }
        var at = _checkList.SelectedIndex + 1;
        st.Checks.Insert(at, new Check());
        Changed();
        ShowStage(at);
        Relabel();
        _text.Focus();
    }

    private void MoveCheck(int by)
    {
        if (CurrentStage is not { } st || _checkList.SelectedIndex < 0) return;
        var i = _checkList.SelectedIndex;
        var to = i + by;
        if (to < 0 || to >= st.Checks.Count) return;
        (st.Checks[i], st.Checks[to]) = (st.Checks[to], st.Checks[i]);
        Changed();
        ShowStage(to);
        _checkList.Focus();
    }

    private void MoveToStage(int target)
    {
        if (CurrentStage is not { } st || CurrentCheck is not { } c || target < 0 || target >= _model.Count || target == _stageList.SelectedIndex) return;
        st.Checks.Remove(c);
        _model[target].Checks.Add(c);
        Changed();
        RefreshStages(target);
        ShowStage(_model[target].Checks.Count - 1);
        Relabel();
    }

    private void DeleteCheck()
    {
        if (CurrentStage is not { } st || CurrentCheck is not { } c) return;
        var preview = Regex.Replace(c.Text, @"\s+", " ").Trim();
        if (preview.Length > 0 && MessageBox.Show(this, $"Delete this check?\n\n{(preview.Length > 200 ? preview.Substring(0, 197) + "…" : preview)}\n\nAnswers already given to it are cleared.",
                "Sketch ProMax", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var i = _checkList.SelectedIndex;
        st.Checks.RemoveAt(i);
        Changed();
        ShowStage(Math.Max(0, i - 1));
        Relabel();
        _checkList.Focus();
    }

    // ---- Checking and saving --------------------------------------------------------------------------------------

    private static readonly Regex Role = new(@"^[A-Za-z][A-Za-z /&\-]*$");

    /// <summary>The checklist as edited, numbered, or null with the reason shown (and the problem selected).</summary>
    private ChecklistMath.Definition? Build()
    {
        if (_model.Count == 0) { ShowError("Add at least one stage with a check."); return null; }
        for (var s = 0; s < _model.Count; s++)
        {
            var title = _model[s].Title.Trim();
            string? why = title.Length == 0 ? "has no name"
                : title.Length > 80 ? "has a name longer than 80 characters"
                : Regex.IsMatch(title, @"^\d") ? "starts with a number (a number in column A means a check in Excel); start it with a letter, e.g. \"H. Handover\""
                : Regex.IsMatch(title, @"^sign\W*off", RegexOptions.IgnoreCase) ? "is named Sign-off, which is kept for the signatures"
                : _model[s].Checks.Count == 0 ? "has no checks: add one or delete the stage"
                : null;
            if (why is not null) { Select(s, -1); ShowError($"Stage {s + 1} {why}."); _stageName.Focus(); return null; }
            for (var c = 0; c < _model[s].Checks.Count; c++)
                if (_model[s].Checks[c].Text.Trim().Length == 0)
                {
                    Select(s, c);
                    ShowError($"Check {NumberOf(s, c)} is empty: type what to check, or delete it.");
                    _text.Focus();
                    return null;
                }
        }
        var roles = _roles.Text.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(r => Regex.Replace(r, @"\s+", " ").Trim()).Where(r => r.Length > 0).Distinct().ToList();
        if (roles.Count == 0) { ShowError("Name at least one person who signs, e.g. Trade BIM coordinator."); _roles.Focus(); return null; }
        if (roles.FirstOrDefault(r => !Role.IsMatch(r) || r.Equals("Date", StringComparison.OrdinalIgnoreCase)) is { } bad)
        {
            ShowError($"\"{bad}\" can't be used as a sign-off role: use letters, spaces, / & and - only (no numbers or brackets), and not \"Date\".");
            _roles.Focus();
            return null;
        }
        var def = new ChecklistMath.Definition
        {
            Title = _title.Text.Trim().Length > 0 ? Regex.Replace(_title.Text, @"\s+", " ").Trim() : "Pre-submission checklist",
            Fields = _base.Fields.ToList(),
            Signatories = roles,
            Sections = _model.Select(s => new ChecklistMath.Section(s.Title, s.Checks.Select(c => new ChecklistMath.Item("", "", c.Text.Trim(), c.Example.Trim(),
                c.From.Trim(), c.FoundOn.Trim(), c.Priority.Trim(), Regex.Replace(c.Waiting.Trim(), @"^Waiting\s*:\s*", "", RegexOptions.IgnoreCase), "")
                { Short = Regex.Replace(c.Short, @"\s+", " ").Trim(), Fix = c.Fix.Trim() }).ToList())).ToList(),
            SourcePath = ChecklistMath.EditedSource,
            SourceStamp = DateTime.Now.ToString("dd-MMM-yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture),
        };
        return ChecklistMath.Renumber(def);
    }

    private void Select(int stage, int check)
    {
        RefreshStages(stage);
        if (check >= 0) ShowStage(check);
    }

    private void ShowError(string text)
    {
        _error.Text = text;
        _error.Visibility = Visibility.Visible;
    }

    private void Save()
    {
        ChecklistMath.Definition? def;
        if (_imported is not null && _importUntouched) def = _base;
        else if ((def = Build()) is null) return;
        try
        {
            ChecklistStore.Save(def);
            // An import follows its Excel file again (a newer revision saved over it is picked up); edits made here stop that.
            // Fresh settings, so a change made in Settings since the checklist window opened isn't overwritten.
            var settings = AgentSettings.Load();
            settings.ChecklistPath = _imported is not null && _importUntouched ? _imported : "";
            settings.Save();
            _settings.ChecklistPath = settings.ChecklistPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            ShowError($"Couldn't save the checklist ({ex.Message}). Check that %APPDATA%\\SketchProMax can be written to, then try again.");
            return;
        }
        Saved = def;
        _changed = false;
        ActivityLog.Note("checklist_edit", $"{def.Items.Count()} checks in {def.Sections.Count} stages, {(_importUntouched ? "imported" : "edited")}");
        DialogResult = true;
    }

    private void Export()
    {
        var def = Build();
        if (def is null) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export the checklist to Excel",
            Filter = "Excel workbook|*.xlsx",
            FileName = ChecklistMath.SafeName(def.Title) + ".xlsx",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            ChecklistXlsx.Write(dialog.FileName, def);
            _error.Visibility = Visibility.Collapsed;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{dialog.FileName}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ShowError($"Couldn't write the Excel file ({ex.Message}). If it is open in Excel, close it and try again.");
        }
    }

    private void Import()
    {
        if (_changed && MessageBox.Show(this, "Replace the checks here with the Excel file? Your changes here that aren't saved are lost.", "Sketch ProMax",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Import the checklist from Excel", Filter = "Excel workbooks|*.xlsx|All files|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var def = ChecklistMath.ReadXlsx(dialog.FileName);
            _base = def;
            Load(def);
            _imported = dialog.FileName;
            _changed = true;
            _importUntouched = true;
            _error.Visibility = Visibility.Collapsed;
            _checkList.Focus();
        }
        catch (InvalidDataException ex) { ShowError($"Couldn't read the checklist: {ex.Message}"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError($"Couldn't open the file ({ex.Message}). If Excel has it locked, save and close it, then try again.");
        }
    }

    private void ResetToBuiltIn()
    {
        if (MessageBox.Show(this, "Go back to the checklist that comes with Sketch ProMax? Your edits and imported checklist are removed from this PC " +
                                  "(Excel files you exported are kept).", "Sketch ProMax", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var builtIn = ChecklistMath.BuiltIn();
        if (builtIn is null) { ShowError("The built-in checklist couldn't be read. Your checklist was left as it is."); return; }
        try
        {
            ChecklistStore.Reset();
            var settings = AgentSettings.Load();
            settings.ChecklistPath = "";
            settings.Save();
            _settings.ChecklistPath = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            ShowError($"Couldn't remove the saved checklist ({ex.Message}). Try again.");
            return;
        }
        Saved = builtIn;
        _changed = false;
        DialogResult = true;
    }

    // ---- Keys and closing -----------------------------------------------------------------------------------------

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S) { Save(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.Up or Key.Down && Keyboard.FocusedElement is not TextBox)
        {
            MoveCheck(e.Key == Key.Up ? -1 : 1);
            e.Handled = true;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (Saved is not null || !_changed) return;
        if (MessageBox.Show(this, "Close without saving your changes to the checklist?", "Sketch ProMax", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
            != MessageBoxResult.Yes) e.Cancel = true;
    }
}
