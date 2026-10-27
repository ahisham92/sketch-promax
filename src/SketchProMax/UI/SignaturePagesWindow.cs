using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SketchProMax.Tools;
using static SketchProMax.Tools.SignaturePages;
using Binding = System.Windows.Data.Binding;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using Grid = System.Windows.Controls.Grid;
using Image = System.Windows.Controls.Image;
using Line = System.Windows.Shapes.Line;
using Panel = System.Windows.Controls.Panel;
using TextBox = System.Windows.Controls.TextBox;
using Visibility = System.Windows.Visibility;

namespace SketchProMax.UI;

/// <summary>
/// Signature pages (Submit panel): the quality cover page of each document of a submission, one Word file and one PDF
/// each, in the office's layout (Tools/SignaturePages). Three steps, drawn as a strip at the top: 1 Documents (sheets
/// picked in Revit, an Excel list, or rows typed in), 2 Sign (each person puts on their own signature with their PIN;
/// a role left unsigned stays blank to sign by hand), 3 Make. A preview shows the page of the row in focus (and a
/// specification's IDC table). What a page says about the project (name, discipline, form codes, logo) and who may sign
/// are set by the admin only (SignaturePagesAdmin), on this PC.
/// </summary>
internal sealed class SignaturePagesWindow : Window
{
    private static SignaturePagesWindow? _open;

    private sealed class Row
    {
        public Doc Doc = new("", "", "0", "");
        public readonly CheckBox Use = new() { IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
        public readonly ComboBox Type = new() { IsEditable = true, Width = 74, MinHeight = 28, VerticalContentAlignment = VerticalAlignment.Center };
        public readonly TextBox Number = Box(250);
        public readonly TextBox Rev = Box(44);
        public readonly TextBox Title = Box(0);
        public Grid? Line;
    }

    private sealed class Role(string label)
    {
        public readonly string Label = label;
        public readonly ComboBox Who = new() { IsEditable = true, MinHeight = 28, VerticalContentAlignment = VerticalAlignment.Center };
        public readonly Button Sign = RichUi.Button(RichUi.Icon.Edit, "Sign");
        public readonly Image Mark = new() { Height = 34, Margin = new Thickness(0, 6, 0, 0), Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
        public readonly TextBlock State = Brand.Text("", Brand.K.Muted, 11);
        public OfficeDocs.Picture? Signature;
        public Signer? SignedAs;
    }

    private string _project;
    private Setup _setup;
    private List<Signer> _people = LoadSigners();
    private readonly List<(string Number, string Name, string Rev)> _sheets;
    private readonly string _modelProjectName;
    private readonly List<Row> _rows = [];
    private readonly StackPanel _list = new();
    private readonly TextBlock _empty = Brand.Text("Pick sheets in Revit or import your Excel list.", Brand.K.Muted, 13);
    private readonly Role _by = new("By"), _chk = new("CHK"), _app = new("Approved");
    private readonly DatePicker _date = new() { SelectedDate = DateTime.Today, Width = 130 };
    private readonly TextBox _description = Box(180);
    private readonly ComboBox _projectBox = new() { IsEditable = true, Width = 170, MinHeight = 28, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBox _folder = Box(0);
    private readonly Border _sectionsLine = new() { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _sectionsText = Brand.Text("", Brand.K.Text, 12.5);
    private readonly RadioButton _showPage = new() { Content = "Signature page", IsChecked = true, Margin = new Thickness(0, 0, 12, 0) };
    private readonly RadioButton _showIdc = new() { Content = "IDC table" };
    private readonly StackPanel _previewPick = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
    private readonly Canvas _preview = new() { Width = PageW * Scale, Height = PageH * Scale, ClipToBounds = true };
    private readonly TextBlock _status = Brand.Text("", Brand.K.Muted, 12);
    private readonly Button _make = RichUi.Button(RichUi.Icon.Save, "Make", Brand.Weight.Primary);
    private readonly Button _openFolder = RichUi.Button(RichUi.Icon.Folder, "Open folder");
    private readonly (Border Circle, TextBlock Glyph)[] _steps = new (Border, TextBlock)[3];
    private Row? _focus;
    private string _lastFolder = "";
    private bool _loading;

    private const double Scale = 0.5;

    private static TextBox Box(double width)
    {
        var box = new TextBox { MinHeight = 28, VerticalContentAlignment = VerticalAlignment.Center };
        if (width > 0) box.Width = width;
        return box;
    }

    // ---- Opening --------------------------------------------------------------------------------------------------

    public static Result Command(UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document;
        if (_open is { IsLoaded: true }) { _open.Activate(); return Result.Succeeded; }
        var (project, name, sheets) = doc is null || doc.IsFamilyDocument ? ("", "", new List<(string, string, string)>()) : Read(doc);
        var window = new SignaturePagesWindow(project, name, sheets);
        _open = window;
        window.Closed += (_, _) => { if (_open == window) _open = null; };
        window.Show();
        return Result.Succeeded;
    }

    /// <summary>The model's project key, its Project Information name and its sheets (number, name, current revision).</summary>
    private static (string Project, string Name, List<(string, string, string)> Sheets) Read(Document doc)
    {
        string path;
        try { path = string.IsNullOrEmpty(doc.PathName) ? doc.Title : doc.PathName; }
        catch (Exception) { path = doc.Title; }
        var sheets = new List<(string, string, string)>();
        try
        {
            foreach (var s in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>())
            {
                if (s.IsPlaceholder) continue;
                var rev = s.get_Parameter(BuiltInParameter.SHEET_CURRENT_REVISION)?.AsString() ?? "";
                sheets.Add((s.SheetNumber, s.Name, rev));
            }
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException ex) { Debug.WriteLine(ex); }
        string name;
        try { name = doc.ProjectInformation?.Name ?? ""; }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { name = ""; }
        return (ChecklistProjects.ProjectKey(path), name, sheets.OrderBy(s => s.Item1, StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>The window, not shown (the field test builds it).</summary>
    internal static Window ForTest() => new SignaturePagesWindow("", "", []);

    private SignaturePagesWindow(string project, string modelProjectName, List<(string, string, string)> sheets)
    {
        _sheets = sheets;
        _modelProjectName = modelProjectName;
        _project = project.Length > 0 ? project : Projects().FirstOrDefault() ?? "Default";
        _setup = LoadSetup(_project);
        Title = "Sketch ProMax: signature pages";
        var area = SystemParameters.WorkArea;
        Width = Math.Min(1260, Math.Floor(area.Width * 0.92));
        Height = Math.Min(860, Math.Floor(area.Height * 0.92));
        MinWidth = Math.Min(960, Width);
        MinHeight = Math.Min(560, Height);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Brand.Prepare(this);
        RichUi.Apply(this, RichUi.MotionOf(Settings.AgentSettings.Load()));
        Content = Build();
        Fill();
        _date.SelectedDateChanged += (_, _) => Preview();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { Make(); e.Handled = true; }
            else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        };
        Closing += (_, _) => Keep();
    }

    // ---- Layout -----------------------------------------------------------------------------------------------------

    private FrameworkElement Build()
    {
        var header = Brand.Header("Signature pages", "One Word file and PDF per document, signed and ready to upload.");
        DockPanel.SetDock(header, Dock.Top);
        var strip = Steps();
        DockPanel.SetDock(strip, Dock.Top);

        // Left: documents, then signing, then the page details (folded).
        var left = new StackPanel { Margin = new Thickness(18, 4, 12, 12) };
        left.Children.Add(Section("1", "Documents"));
        var fromRevit = RichUi.Button(RichUi.Icon.Import, "Sheets from Revit");
        fromRevit.IsEnabled = _sheets.Count > 0;
        AutomationProperties.SetHelpText(fromRevit, _sheets.Count > 0 ? "Pick sheets of the open model: one page per sheet, with its current revision." : "Open the model in Revit first.");
        fromRevit.ToolTip = AutomationProperties.GetHelpText(fromRevit);
        fromRevit.Click += (_, _) => PickSheets();
        var excel = RichUi.Button(RichUi.Icon.Import, "Import Excel");
        AutomationProperties.SetHelpText(excel, "An Excel list (.xlsx or .csv) with Number, Rev, Title and Type columns.");
        excel.ToolTip = AutomationProperties.GetHelpText(excel);
        excel.Click += (_, _) => ImportExcel();
        var add = RichUi.Button(RichUi.Icon.Add, "Add");
        AutomationProperties.SetHelpText(add, "Adds an empty row to type in.");
        add.Click += (_, _) => AddRow(new Doc("", "", "0", ""), focus: true);
        var template = new Button { Content = "Excel template", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(8, 2, 8, 2) };
        template.SetResourceReference(StyleProperty, "SpmSecondaryButton");
        AutomationProperties.SetHelpText(template, "Saves an empty Excel list with the right columns to fill.");
        template.ToolTip = AutomationProperties.GetHelpText(template);
        template.Click += (_, _) => SaveTemplate();
        var clear = new Button { Content = "Clear", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(8, 2, 8, 2) };
        clear.SetResourceReference(StyleProperty, "SpmSecondaryButton");
        clear.Click += (_, _) => { foreach (var r in _rows.ToList()) Remove(r); };
        var tools = new WrapPanel { Margin = new Thickness(0, 6, 0, 8) };
        foreach (var b in new[] { fromRevit, excel, add, template, clear }) { b.Margin = new Thickness(0, 0, 8, 4); tools.Children.Add(b); }
        left.Children.Add(tools);
        left.Children.Add(Heads());
        _empty.Margin = new Thickness(4, 10, 0, 10);
        left.Children.Add(_empty);
        left.Children.Add(_list);
        left.Children.Add(SectionsLine());

        left.Children.Add(Section("2", "Sign"));
        var roles = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        for (var i = 0; i < 3; i++) roles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var r0 = 0;
        foreach (var role in new[] { _by, _chk, _app })
        {
            var tile = RoleTile(role);
            Grid.SetColumn(tile, r0++);
            roles.Children.Add(tile);
        }
        left.Children.Add(roles);
        var when = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        when.Children.Add(Label("Date"));
        when.Children.Add(_date);
        when.Children.Add(Label("Description", 16));
        when.Children.Add(_description);
        AutomationProperties.SetName(_date, "Date on the pages");
        AutomationProperties.SetName(_description, "Description in the revision table");
        _description.TextChanged += (_, _) => { if (!_loading) { Gather(); Preview(); } };
        left.Children.Add(when);


        var leftScroll = new ScrollViewer { Content = left, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };

        // Right: the preview of the page in focus, on white paper in either theme.
        var paper = new Border { Background = Brushes.White, BorderThickness = new Thickness(1), Child = _preview, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
        paper.SetResourceReference(Border.BorderBrushProperty, Brand.K.Border);
        paper.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Opacity = 0.25 };
        AutomationProperties.SetName(paper, "Preview of the page");
        var right = new StackPanel { Margin = new Thickness(6, 10, 18, 12) };
        right.Children.Add(Brand.Text("PREVIEW", Brand.K.Muted, 10.5, bold: true));
        right.Children.Add(new Border { Height = 8 });
        foreach (var r in new[] { _showPage, _showIdc })
        {
            r.SetResourceReference(ForegroundProperty, Brand.K.Text);
            r.Checked += (_, _) => Preview();
            _previewPick.Children.Add(r);
        }
        right.Children.Add(_previewPick);
        right.Children.Add(paper);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.Children.Add(leftScroll);
        Grid.SetColumn(right, 1);
        body.Children.Add(right);

        // Foot: project, folder, Make.
        var browse = new Button { Content = "…", Width = 32, Margin = new Thickness(4, 0, 0, 0) };
        browse.SetResourceReference(StyleProperty, "SpmSecondaryButton");
        AutomationProperties.SetName(browse, "Choose the folder");
        browse.Click += (_, _) => PickFolder();
        _folder.Width = 300;
        AutomationProperties.SetName(_folder, "Folder the pages are saved in");
        AutomationProperties.SetName(_projectBox, "Project");
        _projectBox.ToolTip = "Each project keeps its own page details and list on this PC.";
        _projectBox.LostFocus += (_, _) => SwitchProject(_projectBox.Text);
        _projectBox.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(new Action(() => SwitchProject(_projectBox.Text)));
        _make.IsDefault = true;
        _make.Click += (_, _) => Make();
        AutomationProperties.SetHelpText(_make, "Writes each document's Word file and PDF (Ctrl+Enter).");
        _openFolder.Visibility = Visibility.Collapsed;
        _openFolder.Click += (_, _) => { if (Directory.Exists(_lastFolder)) Process.Start(new ProcessStartInfo(_lastFolder) { UseShellExecute = true }); };
        _status.TextWrapping = TextWrapping.Wrap;
        _status.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        var admin = RichUi.Button(RichUi.Icon.Settings, "Admin");
        AutomationProperties.SetHelpText(admin, "Page details and the people who sign. Needs the admin password.");
        admin.ToolTip = AutomationProperties.GetHelpText(admin);
        admin.Margin = new Thickness(0, 0, 14, 0);
        admin.Click += (_, _) => OpenAdmin();
        var where = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        where.Children.Add(admin);
        where.Children.Add(Label("Project"));
        where.Children.Add(_projectBox);
        where.Children.Add(Label("Save in", 14));
        where.Children.Add(_folder);
        where.Children.Add(browse);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(_openFolder);
        buttons.Children.Add(_make);
        var foot = new Grid();
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        foot.Children.Add(where);
        _status.Margin = new Thickness(16, 0, 12, 0);
        Grid.SetColumn(_status, 1);
        foot.Children.Add(_status);
        Grid.SetColumn(buttons, 2);
        foot.Children.Add(buttons);
        var footer = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(18, 10, 18, 12), Child = foot };
        footer.SetResourceReference(Border.BorderBrushProperty, Brand.K.Border);
        DockPanel.SetDock(footer, Dock.Bottom);

        var root = new DockPanel();
        root.Children.Add(header);
        root.Children.Add(strip);
        root.Children.Add(footer);
        root.Children.Add(body);
        root.SetResourceReference(Panel.BackgroundProperty, Brand.K.Bg);
        return root;
    }

    /// <summary>The three steps as numbered circles joined by a line; a circle turns into a tick when its step is done.</summary>
    private FrameworkElement Steps()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 12, 18, 4) };
        string[] names = ["Documents", "Sign", "Make"];
        for (var i = 0; i < 3; i++)
        {
            if (i > 0)
            {
                var line = new Border { Width = 46, Height = 2, Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
                line.SetResourceReference(Border.BackgroundProperty, Brand.K.Border);
                row.Children.Add(line);
            }
            var glyph = new TextBlock { Text = (i + 1).ToString(), FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var circle = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), BorderThickness = new Thickness(2), Child = glyph };
            _steps[i] = (circle, glyph);
            row.Children.Add(circle);
            var label = Brand.Text(names[i], Brand.K.Text, 13, bold: true);
            label.Margin = new Thickness(8, 0, 0, 0);
            label.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(label);
        }
        AutomationProperties.SetName(row, "Steps: documents, sign, make");
        return row;
    }

    private void ShowSteps()
    {
        var done = new[] { Included().Count > 0, new[] { _by, _chk, _app }.Any(r => r.Signature is not null), _lastFolder.Length > 0 };
        for (var i = 0; i < 3; i++)
        {
            var (circle, glyph) = _steps[i];
            glyph.Text = done[i] ? "" : (i + 1).ToString();
            glyph.FontFamily = done[i] ? new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets") : new FontFamily("Segoe UI");
            circle.SetResourceReference(Border.BorderBrushProperty, done[i] ? Brand.K.Accent : Brand.K.InputBorder);
            circle.SetResourceReference(Border.BackgroundProperty, done[i] ? Brand.K.Accent : Brand.K.Bg);
            glyph.SetResourceReference(TextBlock.ForegroundProperty, done[i] ? Brand.K.OnAccent : Brand.K.Text);
        }
        var n = Included().Count;
        _make.Content = n == 1 ? "Make 1 page" : $"Make {n} pages";
        _make.IsEnabled = n > 0;
        _empty.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowSections();
    }

    private static FrameworkElement Section(string number, string title)
    {
        var t = Brand.Text($"{number}  {title}", Brand.K.Text, 15, bold: true);
        t.Margin = new Thickness(0, 14, 0, 0);
        return t;
    }

    private static TextBlock Label(string text, double left = 0)
    {
        var t = Brand.Text(text, Brand.K.Muted, 12);
        t.Margin = new Thickness(left, 0, 8, 0);
        t.VerticalAlignment = VerticalAlignment.Center;
        return t;
    }

    private static readonly double[] Widths = [28, 82, 258, 52, 0, 32];

    private static Grid Columns()
    {
        var g = new Grid();
        foreach (var w in Widths) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w > 0 ? new GridLength(w) : new GridLength(1, GridUnitType.Star), MinWidth = w > 0 ? 0 : 160 });
        return g;
    }

    private static FrameworkElement Heads()
    {
        var g = Columns();
        string[] names = ["", "TYPE", "NUMBER", "REV", "TITLE", ""];
        for (var i = 0; i < names.Length; i++)
        {
            var t = Brand.Text(names[i], Brand.K.Muted, 10.5, bold: true);
            Grid.SetColumn(t, i);
            g.Children.Add(t);
        }
        return g;
    }

    private FrameworkElement RoleTile(Role role)
    {
        var tile = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10), Margin = new Thickness(0, 0, 10, 0) };
        tile.SetResourceReference(Border.BorderBrushProperty, Brand.K.Border);
        tile.SetResourceReference(Border.BackgroundProperty, Brand.K.Surface);
        var stack = new StackPanel();
        stack.Children.Add(Brand.Text(role.Label, Brand.K.Text, 13, bold: true));
        var line = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        role.Sign.Margin = new Thickness(6, 0, 0, 0);
        DockPanel.SetDock(role.Sign, Dock.Right);
        line.Children.Add(role.Sign);
        line.Children.Add(role.Who);
        stack.Children.Add(line);
        stack.Children.Add(role.Mark);
        stack.Children.Add(role.State);
        tile.Child = stack;
        AutomationProperties.SetName(role.Who, $"{role.Label}: initials");
        AutomationProperties.SetHelpText(role.Sign, $"The {role.Label} person types their PIN to put their signature on the pages.");
        role.Who.ToolTip = "Initials as the table shows them";
        void WhoChanged()
        {
            // A signature belongs to its person: other initials take it off.
            if (role.SignedAs is { } was && FindSigner(_people, role.Who.Text)?.Salt != was.Salt) Unsign(role);
            ShowRole(role);
            Preview();
        }
        role.Who.LostFocus += (_, _) => WhoChanged();
        role.Who.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(new Action(WhoChanged));
        role.Sign.Click += (_, _) => { if (role.Signature is null) SignAs(role); else { Unsign(role); ShowRole(role); Preview(); } };
        return tile;
    }

    private void ShowRole(Role role)
    {
        if (role.Signature is { } p)
        {
            role.Mark.Source = ToBitmap(p.Bytes);
            role.Mark.Visibility = Visibility.Visible;
            role.State.Text = "Signed";
            role.State.SetResourceReference(TextBlock.ForegroundProperty, Brand.K.SuccessText);
            RichUi.Set(role.Sign, RichUi.Icon.Undo, "Take off");
        }
        else
        {
            role.Mark.Source = null;
            role.Mark.Visibility = Visibility.Collapsed;
            role.State.Text = role.Who.Text.Trim().Length == 0 ? "No one" : "Blank: signed by hand";
            role.State.SetResourceReference(TextBlock.ForegroundProperty, Brand.K.Muted);
            RichUi.Set(role.Sign, RichUi.Icon.Edit, "Sign");
        }
        ShowSteps();
    }

    private void Say(string text, string key = Brand.K.Muted)
    {
        _status.Text = text;
        _status.SetResourceReference(TextBlock.ForegroundProperty, key);
    }

    private static void Unsign(Role role) { role.Signature = null; role.SignedAs = null; }

    /// <summary>Shown when a listed document has the IDC table (specifications): how many sections it lists, and Edit.</summary>
    private FrameworkElement SectionsLine()
    {
        _sectionsLine.SetResourceReference(Border.BorderBrushProperty, Brand.K.Border);
        _sectionsLine.SetResourceReference(Border.BackgroundProperty, Brand.K.Surface);
        var edit = RichUi.Button(RichUi.Icon.Edit, "Spec sections");
        AutomationProperties.SetHelpText(edit, "The sections listed in the specification's IDC table: type them or import them from Excel.");
        edit.ToolTip = AutomationProperties.GetHelpText(edit);
        edit.Click += (_, _) =>
        {
            if (!SectionsDialog.Edit(this, _setup)) return;
            Keep();
            ShowSections();
            Preview();
        };
        DockPanel.SetDock(edit, Dock.Right);
        _sectionsText.VerticalAlignment = VerticalAlignment.Center;
        _sectionsText.TextWrapping = TextWrapping.Wrap;
        var line = new DockPanel();
        line.Children.Add(edit);
        line.Children.Add(_sectionsText);
        _sectionsLine.Child = line;
        _sectionsLine.Visibility = Visibility.Collapsed;
        return _sectionsLine;
    }

    private bool HasIdc(Doc d) => TypeOf(_setup, d.Type).Idc;

    private void ShowSections()
    {
        var idc = Included().Where(HasIdc).ToList();
        _sectionsLine.Visibility = idc.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var n = _setup.Sections.Count;
        _sectionsText.Text = (idc.Count == 1 ? "1 specification gets an IDC table" : $"{idc.Count} specifications get an IDC table") +
                             (n == 0 ? ": no sections listed yet." : $" listing {n} section{(n == 1 ? "" : "s")}.");
        _sectionsText.SetResourceReference(TextBlock.ForegroundProperty, n == 0 ? Brand.K.WarningText : Brand.K.Text);
    }

    private void OpenAdmin()
    {
        Gather();
        if (!SignaturePagesAdmin.Open(this, _project, _setup)) return;
        Keep();
        _people = LoadSigners();
        foreach (var r in new[] { _by, _chk, _app })
        {
            var keep = r.Who.Text;
            r.Who.Items.Clear();
            foreach (var p in _people) r.Who.Items.Add(p.Initials);
            r.Who.Text = keep;
            if (r.SignedAs is { } was && FindSigner(_people, keep)?.Salt != was.Salt) Unsign(r);
            ShowRole(r);
        }
        RefreshTypeLists();
        ShowSections();
        Preview();
    }

    // ---- Filling and keeping ------------------------------------------------------------------------------------------

    private void Fill()
    {
        _loading = true;
        try
        {
            _projectBox.Items.Clear();
            foreach (var p in Projects().Append(_project).Distinct(StringComparer.OrdinalIgnoreCase)) _projectBox.Items.Add(p);
            _projectBox.Text = _project;
            if (_setup.Project.Length == 0) _setup.Project = _modelProjectName;
            _description.Text = _setup.Description;
            _folder.Text = _setup.Folder.Length > 0 ? _setup.Folder : DefaultFolder();
            foreach (var r in _rows.ToList()) Remove(r);
            foreach (var d in _setup.Docs) AddRow(d);
            foreach (var (role, initials) in new[] { (_by, _setup.By), (_chk, _setup.Checked), (_app, _setup.Approved) })
            {
                Unsign(role);
                role.Who.Items.Clear();
                foreach (var p in _people) role.Who.Items.Add(p.Initials);
                role.Who.Text = initials;
                ShowRole(role);
            }
        }
        finally { _loading = false; }
        ShowSections();
        Gather();
        Preview();
    }

    private static string DefaultFolder() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Sketch ProMax", "Signature pages");

    /// <summary>Reads the window into the setup.</summary>
    private void Gather()
    {
        _setup.Description = _description.Text.Trim();
        _setup.By = _by.Who.Text.Trim();
        _setup.Checked = _chk.Who.Text.Trim();
        _setup.Approved = _app.Who.Text.Trim();
        _setup.Folder = _folder.Text.Trim() == DefaultFolder() ? "" : _folder.Text.Trim();
        _setup.Docs = _rows.Select(Read).Where(d => d.Number.Length > 0 || d.Title.Length > 0).ToList();
    }

    private void Keep()
    {
        try { Gather(); SaveSetup(_project, _setup); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Say("Couldn't keep the list on this PC: " + ex.Message); }
    }

    private void SwitchProject(string name)
    {
        name = (name ?? "").Trim();
        if (_loading || name.Length == 0 || string.Equals(name, _project, StringComparison.OrdinalIgnoreCase)) return;
        Keep();
        _project = name;
        _setup = LoadSetup(name);
        Fill();
    }

    // ---- The list -----------------------------------------------------------------------------------------------------

    private Doc Read(Row r) => new(r.Type.Text.Trim().ToUpperInvariant(), r.Number.Text.Trim(), CleanRev(r.Rev.Text), r.Title.Text.Trim());

    private List<Doc> Included() => _rows.Where(r => r.Use.IsChecked == true).Select(Read).Where(d => d.Number.Length > 0).ToList();

    private void RefreshTypeLists()
    {
        foreach (var r in _rows)
        {
            var keep = r.Type.Text;
            r.Type.Items.Clear();
            foreach (var t in _setup.Types.Where(t => t.Code.Length > 0)) r.Type.Items.Add(t.Code);
            r.Type.Text = keep;
        }
    }

    private void AddRow(Doc d, bool focus = false)
    {
        var row = new Row { Doc = d };
        foreach (var t in _setup.Types.Where(t => t.Code.Length > 0)) row.Type.Items.Add(t.Code);
        row.Type.Text = d.Type;
        row.Number.Text = d.Number;
        row.Rev.Text = d.Rev;
        row.Title.Text = d.Title;
        AutomationProperties.SetName(row.Use, "Make this page");
        AutomationProperties.SetName(row.Type, "Type");
        AutomationProperties.SetName(row.Number, "Document number");
        AutomationProperties.SetName(row.Rev, "Revision");
        AutomationProperties.SetName(row.Title, "Title");
        var remove = new Button { Content = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), Width = 28, Height = 28, Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        remove.SetResourceReference(ForegroundProperty, Brand.K.Muted);
        AutomationProperties.SetName(remove, "Remove this row");
        remove.Click += (_, _) => Remove(row);
        var g = Columns();
        g.Margin = new Thickness(0, 2, 0, 2);
        UIElement[] cells = [row.Use, row.Type, row.Number, row.Rev, row.Title, remove];
        for (var i = 0; i < cells.Length; i++)
        {
            Grid.SetColumn(cells[i], i);
            if (cells[i] is FrameworkElement f && i is > 0 and < 5) { f.Margin = new Thickness(0, 0, 6, 0); if (f is TextBox tb) tb.Width = double.NaN; }
            g.Children.Add(cells[i]);
        }
        row.Line = g;
        void Changed() { if (!_loading) { ShowSteps(); if (_focus == row) Preview(); } }
        row.Use.Click += (_, _) => Changed();
        row.Number.TextChanged += (_, _) => Changed();
        row.Rev.TextChanged += (_, _) => Changed();
        row.Title.TextChanged += (_, _) => Changed();
        row.Type.LostFocus += (_, _) => Changed();
        row.Type.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(new Action(Changed));
        g.IsKeyboardFocusWithinChanged += (_, e) => { if ((bool)e.NewValue) { _focus = row; Preview(); } };
        _rows.Add(row);
        _list.Children.Add(g);
        _focus ??= row;
        ShowSteps();
        if (focus) Dispatcher.BeginInvoke(new Action(() => row.Number.Focus()), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void Remove(Row row)
    {
        _rows.Remove(row);
        if (row.Line is not null) _list.Children.Remove(row.Line);
        if (_focus == row) _focus = _rows.FirstOrDefault();
        ShowSteps();
        Preview();
    }

    /// <summary>Adds documents to the list; a number already listed with the same revision is skipped.</summary>
    private int AddAll(IEnumerable<Doc> docs)
    {
        var have = new HashSet<string>(_rows.Select(r => Read(r)).Select(d => d.Number + "|" + d.Rev), StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var d in docs)
        {
            if (!have.Add(d.Number + "|" + d.Rev)) continue;
            AddRow(d);
            added++;
        }
        Preview();
        return added;
    }

    private void PickSheets()
    {
        var picked = SheetPicker.Pick(this, _sheets);
        if (picked.Count == 0) return;
        var code = _setup.Types.Any(t => t.Code == "DWG") ? "DWG" : "DOC";
        var added = AddAll(picked.Select(s => new Doc(code, s.Number, CleanRev(s.Rev) is { Length: > 0 } r ? r : "0", s.Name)));
        Say($"Added {added} sheet{(added == 1 ? "" : "s")}.");
    }

    private void ImportExcel()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Excel list of the documents", Filter = "Excel or CSV (*.xlsx;*.csv;*.xls)|*.xlsx;*.csv;*.xls|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var docs = ReadList(_setup, dialog.FileName);
            if (docs.Count == 0) { Say("No document numbers found. Use a Number column (Excel template shows the columns)."); return; }
            var added = AddAll(docs);
            Say($"Added {added} of {docs.Count} from {Path.GetFileName(dialog.FileName)}" + (added < docs.Count ? " (the rest were listed already)." : "."));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            Say(ex is IOException and not FileNotFoundException ? $"{Path.GetFileName(dialog.FileName)} is open in Excel or can't be read: close it and try again." : ex.Message);
        }
    }

    private void SaveTemplate()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "Save the Excel list template", FileName = "Signature pages list.xlsx", Filter = "Excel (*.xlsx)|*.xlsx" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            WriteTemplate(dialog.FileName);
            Process.Start(new ProcessStartInfo(dialog.FileName) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { Say("Couldn't save the template: " + ex.Message); }
    }

    // ---- Signing ------------------------------------------------------------------------------------------------------

    private void SignAs(Role role)
    {
        var initials = role.Who.Text.Trim();
        if (initials.Length == 0) { Say($"Type the {role.Label} initials first."); role.Who.Focus(); return; }
        _people = LoadSigners();
        var person = FindSigner(_people, initials);
        if (person is null) { Say($"{initials} isn't on this PC. Ask the admin to add them, or leave {role.Label} to sign by hand.", Brand.K.WarningText); return; }
        OfficeDocs.Picture? signature;
        if (!person.IsSetUp)
        {
            // Added by the admin (or their PIN was reset): they draw their signature and choose their PIN now.
            if (SetUpDialog.Run(this, person) is not { } picture) return;
            signature = picture;
        }
        else if (PinDialog.Ask(this, person) is not { } typed) return;
        else signature = typed;
        role.Signature = signature;
        role.SignedAs = FindSigner(LoadSigners(), initials) ?? person;
        ShowRole(role);
        Preview();
    }

    // ---- Folder --------------------------------------------------------------------------------------------

    private void PickFolder()
    {
        var start = Directory.Exists(_folder.Text) ? _folder.Text : DefaultFolder();
#if NET8_0_OR_GREATER
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Folder for the signature pages", InitialDirectory = Directory.Exists(start) ? start : "" };
        if (dialog.ShowDialog(this) != true) return;
        _folder.Text = dialog.FolderName;
#else
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "Folder for the signature pages (open it, then press Save)", FileName = "Pages go here", InitialDirectory = Directory.Exists(start) ? start : "", Filter = "Folder|*.folder", OverwritePrompt = false };
        if (dialog.ShowDialog(this) != true) return;
        _folder.Text = Path.GetDirectoryName(dialog.FileName) ?? _folder.Text;
#endif
    }

    // ---- Make -------------------------------------------------------------------------------------------------------

    private Common CommonNow() => new(_setup, _date.SelectedDate ?? DateTime.Today, Logo(_project), _by.Signature, _chk.Signature, _app.Signature);

    private async void Make()
    {
        if (Brand.IsBusy(_make)) return;
        Gather();
        var docs = Included();
        if (docs.Count == 0) { Say("Add documents with a number first."); return; }
        var date = _date.SelectedDate ?? DateTime.Today;
        var folder = Path.Combine(_folder.Text.Trim().Length > 0 ? _folder.Text.Trim() : DefaultFolder(), date.ToString("dd-MM-yyyy", System.Globalization.CultureInfo.InvariantCulture));
        var common = CommonNow();
        Brand.SetBusy(_make, true, "Making…");
        var clock = Stopwatch.StartNew();
        try
        {
            await Task.Run(() => SignaturePages.Make(docs, folder, common));
            _lastFolder = folder;
            _openFolder.Visibility = Visibility.Visible;
            var blank = new[] { _by, _chk, _app }.Where(r => r.Signature is null && r.Who.Text.Trim().Length > 0).Select(r => r.Label).ToList();
            Say($"Made {docs.Count} page{(docs.Count == 1 ? "" : "s")} (Word and PDF) in {clock.Elapsed.TotalSeconds:0.0} s." +
                           (blank.Count > 0 ? $" {string.Join(" and ", blank)} left to sign by hand." : ""), Brand.K.SuccessText);
            Keep();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Say("Couldn't write the files: " + (ex is IOException and not DirectoryNotFoundException ? "a page is open in Word or a PDF reader; close it and press Make again." : ex.Message), Brand.K.DangerText);
        }
        finally { Brand.SetBusy(_make, false); ShowSteps(); }
    }

    // ---- Preview ------------------------------------------------------------------------------------------------------

    private void Preview()
    {
        _preview.Children.Clear();
        var doc = _focus is { } f ? Read(f) : new Doc("BOQ", "PRJ1-FD-SITE-01-BOQ-ST-01", "0", "Document title");
        var idc = HasIdc(doc);
        _previewPick.Visibility = idc ? Visibility.Visible : Visibility.Collapsed;
        var ink = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11));
        foreach (var m in idc && _showIdc.IsChecked == true ? IdcLayout(doc, CommonNow()) : Layout(doc, CommonNow()))
        {
            switch (m.Kind)
            {
                case Kind.Line:
                    _preview.Children.Add(new Line { X1 = m.X * Scale, Y1 = m.Y * Scale, X2 = (m.X + m.W) * Scale, Y2 = (m.Y + m.H) * Scale, Stroke = ink, StrokeThickness = 0.6 });
                    break;
                case Kind.Text:
                {
                    var t = new TextBlock { Text = m.Text, FontFamily = new FontFamily("Arial"), FontSize = m.Size * Scale * 96 / 72, Foreground = ink, FontWeight = m.Bold ? FontWeights.Bold : FontWeights.Normal };
                    t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    var w = t.DesiredSize.Width;
                    var x = m.Align switch { Align.Center => m.X * Scale + (m.W * Scale - w) / 2, Align.Right => (m.X + m.W) * Scale - w, _ => m.X * Scale };
                    Canvas.SetLeft(t, x);
                    Canvas.SetTop(t, m.Y * Scale - t.DesiredSize.Height * 0.8);
                    _preview.Children.Add(t);
                    break;
                }
                case Kind.Picture when ToBitmap(m.Picture!.Bytes) is { } bitmap:
                {
                    var (x, y, w, h) = Fit(m);
                    var image = new Image { Source = bitmap, Width = w * Scale, Height = h * Scale, Stretch = Stretch.Fill };
                    Canvas.SetLeft(image, x * Scale);
                    Canvas.SetTop(image, y * Scale);
                    _preview.Children.Add(image);
                    break;
                }
            }
        }
    }

    internal static BitmapSource? ToBitmap(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or InvalidOperationException or FileFormatException) { return null; }
    }

    // ---- Dialogs ------------------------------------------------------------------------------------------------------

    /// <summary>Sheets of the open model to tick, with a filter; returns the ticked ones.</summary>
    private sealed class SheetPicker : Window
    {
        private readonly List<(CheckBox Box, (string Number, string Name, string Rev) Sheet)> _items = [];
        private readonly List<(string, string, string)> _picked = [];

        public static List<(string Number, string Name, string Rev)> Pick(Window owner, List<(string, string, string)> sheets)
        {
            var w = new SheetPicker(sheets) { Owner = owner };
            w.ShowDialog();
            return w._picked;
        }

        private SheetPicker(List<(string Number, string Name, string Rev)> sheets)
        {
            Title = "Sketch ProMax: sheets";
            Width = 640;
            Height = 620;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Brand.Prepare(this);
            var filter = Box(0);
            AutomationProperties.SetName(filter, "Filter sheets by number or name");
            var list = new StackPanel();
            foreach (var s in sheets)
            {
                var box = new CheckBox { Content = $"{s.Number}   {s.Name}" + (s.Rev.Length > 0 ? $"   (Rev {s.Rev})" : ""), Margin = new Thickness(0, 3, 0, 3) };
                box.SetResourceReference(ForegroundProperty, Brand.K.Text);
                _items.Add((box, s));
                list.Children.Add(box);
            }
            filter.TextChanged += (_, _) =>
            {
                var f = filter.Text.Trim();
                foreach (var (box, s) in _items)
                    box.Visibility = f.Length == 0 || (s.Number + " " + s.Name).IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0 ? Visibility.Visible : Visibility.Collapsed;
            };
            var all = Brand.Button("Tick shown");
            all.Click += (_, _) => { foreach (var (box, _) in _items) if (box.Visibility == Visibility.Visible) box.IsChecked = true; };
            var none = Brand.Button("Untick all");
            none.Click += (_, _) => { foreach (var (box, _) in _items) box.IsChecked = false; };
            var cancel = Brand.Button("Cancel");
            cancel.IsCancel = true;
            var add = Brand.Button("Add", Brand.Weight.Primary);
            add.IsDefault = true;
            add.Click += (_, _) => { _picked.AddRange(_items.Where(i => i.Box.IsChecked == true).Select(i => i.Sheet)); Close(); };
            var top = new DockPanel { Margin = new Thickness(16, 12, 16, 8) };
            var label = Label("Filter");
            DockPanel.SetDock(label, Dock.Left);
            top.Children.Add(label);
            top.Children.Add(filter);
            DockPanel.SetDock(top, Dock.Top);
            var buttons = Brand.ButtonRow(all, none, cancel, add);
            buttons.Margin = new Thickness(16, 8, 16, 12);
            DockPanel.SetDock(buttons, Dock.Bottom);
            var root = new DockPanel();
            root.Children.Add(Brand.Header("Sheets", "Tick the sheets that need a page."));
            DockPanel.SetDock(root.Children[0], Dock.Top);
            root.Children.Add(top);
            root.Children.Add(buttons);
            root.Children.Add(new ScrollViewer { Content = list, Padding = new Thickness(16, 0, 16, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            root.SetResourceReference(Panel.BackgroundProperty, Brand.K.Bg);
            Content = root;
            Loaded += (_, _) => filter.Focus();
        }
    }

    /// <summary>Asks the person for their PIN; returns their signature when it is theirs.</summary>
    internal sealed class PinDialog : Window
    {
        private OfficeDocs.Picture? _signature;

        public static OfficeDocs.Picture? Ask(Window owner, Signer person)
        {
            var w = new PinDialog(person) { Owner = owner };
            w.ShowDialog();
            return w._signature;
        }

        private PinDialog(Signer person)
        {
            Title = "Sketch ProMax: sign";
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Brand.Prepare(this);
            var pin = new PasswordBox { Width = 200, MinHeight = 30, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
            AutomationProperties.SetName(pin, $"PIN of {person.Initials}");
            var error = Brand.Text("", Brand.K.DangerText, 12);
            error.Margin = new Thickness(0, 6, 0, 0);
            AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Assertive);
            var tries = 0;
            var cancel = Brand.Button("Cancel");
            cancel.IsCancel = true;
            var sign = Brand.Button("Sign", Brand.Weight.Primary);
            sign.IsDefault = true;
            sign.Click += async (_, _) =>
            {
                sign.IsEnabled = false;
                var typed = pin.Password;
                var got = await Task.Run(() => Unlock(person, typed));
                if (got is not null) { _signature = got; Close(); return; }
                tries++;
                error.Text = tries >= 3 ? "That PIN isn't right. Wait a moment and try again." : "That PIN isn't right.";
                pin.Clear();
                if (tries >= 3) await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 5 * (tries - 2))));
                sign.IsEnabled = true;
                pin.Focus();
            };
            var who = string.IsNullOrWhiteSpace(person.Name) ? person.Initials : $"{person.Name} ({person.Initials})";
            var stack = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };
            stack.Children.Add(Brand.Text($"{who}, type your PIN", Brand.K.Text, 14, bold: true));
            stack.Children.Add(pin);
            stack.Children.Add(error);
            stack.Children.Add(Brand.ButtonRow(cancel, sign));
            var root = new StackPanel();
            root.Children.Add(stack);
            root.SetResourceReference(Panel.BackgroundProperty, Brand.K.Bg);
            Content = root;
            Loaded += (_, _) => pin.Focus();
        }
    }

    /// <summary>
    /// A person the admin added (or whose PIN was reset) sets up: draws their signature (or uses a picture) and chooses
    /// their PIN, typed twice. Saved locked with that PIN; returns the signature.
    /// </summary>
    internal sealed class SetUpDialog : Window
    {
        private OfficeDocs.Picture? _done;
        private byte[]? _picture;

        public static OfficeDocs.Picture? Run(Window owner, Signer person)
        {
            var w = new SetUpDialog(person) { Owner = owner };
            w.ShowDialog();
            return w._done;
        }

        private SetUpDialog(Signer person)
        {
            Title = "Sketch ProMax: set up a signature";
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Brand.Prepare(this);
            var pin = new PasswordBox { Width = 160, MinHeight = 30, VerticalContentAlignment = VerticalAlignment.Center };
            var again = new PasswordBox { Width = 160, MinHeight = 30, VerticalContentAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(pin, "PIN");
            AutomationProperties.SetName(again, "PIN again");
            var pad = new InkCanvas { Width = 380, Height = 120, Background = Brushes.White };
            pad.DefaultDrawingAttributes = new DrawingAttributes { Color = Color.FromRgb(0x1A, 0x2A, 0x6C), Width = 2.6, Height = 2.6, FitToCurve = true };
            AutomationProperties.SetName(pad, "Draw the signature here");
            var picked = new Image { Height = 60, MaxWidth = 300, Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left };
            var fromFile = RichUi.Button(RichUi.Icon.Picture, "Use a picture");
            var clear = Brand.Button("Clear");
            clear.Margin = new Thickness(8, 0, 0, 0);
            clear.Click += (_, _) => { pad.Strokes.Clear(); _picture = null; picked.Visibility = Visibility.Collapsed; pad.Visibility = Visibility.Visible; };
            var error = Brand.Text("", Brand.K.DangerText, 12);
            error.TextWrapping = TextWrapping.Wrap;
            error.MaxWidth = 380;
            AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Assertive);
            fromFile.Click += (_, _) =>
            {
                var dialog = new Microsoft.Win32.OpenFileDialog { Title = "A picture of the signature", Filter = "Pictures (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg" };
                if (dialog.ShowDialog(this) != true) return;
                try
                {
                    var bytes = File.ReadAllBytes(dialog.FileName);
                    if (OfficeDocs.ReadPicture(bytes) is null) { error.Text = "Use a PNG or JPG picture."; return; }
                    _picture = bytes;
                    picked.Source = ToBitmap(bytes);
                    picked.Visibility = Visibility.Visible;
                    pad.Visibility = Visibility.Collapsed;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error.Text = ex.Message; }
            };
            var cancel = Brand.Button("Cancel");
            cancel.IsCancel = true;
            var save = Brand.Button("Save", Brand.Weight.Primary);
            save.IsDefault = true;
            save.Click += async (_, _) =>
            {
                var bytes = _picture ?? Drawn(pad);
                if (bytes is null) { error.Text = "Draw the signature or use a picture."; return; }
                if (PinProblem(pin.Password) is { } p) { error.Text = p; pin.Focus(); return; }
                if (pin.Password != again.Password) { error.Text = "The two PINs are different."; again.Clear(); again.Focus(); return; }
                save.IsEnabled = false;
                var code = pin.Password;
                try
                {
                    var people = LoadSigners();
                    var me = FindSigner(people, person.Initials);
                    if (me is null) { error.Text = $"{person.Initials} was removed by the admin."; save.IsEnabled = true; return; }
                    await Task.Run(() => SignaturePages.SetUp(me, bytes, code));
                    SaveSigners(people);
                    _done = OfficeDocs.ReadPicture(bytes);
                    Close();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) { error.Text = ex.Message; save.IsEnabled = true; }
            };
            var stack = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };
            void Row(string label, FrameworkElement box)
            {
                var t = Brand.Text(label, Brand.K.Muted, 12);
                t.Margin = new Thickness(0, 8, 0, 3);
                stack.Children.Add(t);
                stack.Children.Add(box);
            }
            Row("Signature: draw it with the mouse or pen, or use a picture", new Border { BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray, Child = new Grid { Children = { pad, picked } }, HorizontalAlignment = HorizontalAlignment.Left });
            var under = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            under.Children.Add(fromFile);
            under.Children.Add(clear);
            stack.Children.Add(under);
            var pins = new StackPanel { Orientation = Orientation.Horizontal };
            pins.Children.Add(pin);
            pins.Children.Add(new Border { Width = 10 });
            pins.Children.Add(again);
            Row("Your PIN (at least 4 characters, twice). Only you should know it.", pins);
            stack.Children.Add(error);
            stack.Children.Add(Brand.ButtonRow(cancel, save));
            var who = string.IsNullOrWhiteSpace(person.Name) ? person.Initials : $"{person.Name} ({person.Initials})";
            var root = new StackPanel();
            root.Children.Add(Brand.Header($"{who}: your signature", "Locked with your PIN on this PC. No one else can put it on a page."));
            root.Children.Add(stack);
            root.SetResourceReference(Panel.BackgroundProperty, Brand.K.Bg);
            Content = root;
            Loaded += (_, _) => pad.Focus();
        }

        /// <summary>The drawn strokes as a transparent PNG cropped to them; null when nothing is drawn.</summary>
        private static byte[]? Drawn(InkCanvas pad)
        {
            if (pad.Strokes.Count == 0) return null;
            var bounds = pad.Strokes.GetBounds();
            if (bounds.IsEmpty || bounds.Width < 2 || bounds.Height < 2) return null;
            const double Up = 3; // drawn larger than the screen, so it prints sharp
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(Up, Up));
                dc.PushTransform(new TranslateTransform(-bounds.X + 2, -bounds.Y + 2));
                pad.Strokes.Draw(dc);
            }
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling((bounds.Width + 4) * Up), (int)Math.Ceiling((bounds.Height + 4) * Up), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
    }

    /// <summary>The specification sections in the IDC table: Ref, title, lead and IDC discipline; typed or from Excel.</summary>
    private sealed class SectionsDialog : Window
    {
        private readonly List<(TextBox Ref, TextBox Title, TextBox Lead, TextBox Idc, Grid Line)> _lines = [];
        private readonly StackPanel _list = new();
        private bool _saved;

        public static bool Edit(Window owner, Setup setup)
        {
            var w = new SectionsDialog(setup) { Owner = owner };
            w.ShowDialog();
            return w._saved;
        }

        private static readonly double[] Cols = [90, 0, 60, 60, 32];

        private static Grid Cells()
        {
            var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            foreach (var w in Cols) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w > 0 ? new GridLength(w) : new GridLength(1, GridUnitType.Star) });
            return g;
        }

        private SectionsDialog(Setup setup)
        {
            Title = "Sketch ProMax: spec sections";
            Width = 760;
            Height = 620;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Brand.Prepare(this);
            RichUi.Apply(this, RichUi.MotionOf(Settings.AgentSettings.Load()));
            var status = Brand.Text("", Brand.K.Muted, 12);
            status.TextWrapping = TextWrapping.Wrap;
            var import = RichUi.Button(RichUi.Icon.Import, "Import Excel");
            AutomationProperties.SetHelpText(import, "An Excel list with Ref and Title columns (Lead and IDC too). Replaces the list below.");
            import.ToolTip = AutomationProperties.GetHelpText(import);
            import.Click += (_, _) =>
            {
                var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Excel list of the spec sections", Filter = "Excel or CSV (*.xlsx;*.csv;*.xls)|*.xlsx;*.csv;*.xls|All files (*.*)|*.*" };
                if (dialog.ShowDialog(this) != true) return;
                try
                {
                    var found = ReadSections(dialog.FileName);
                    if (found.Count == 0) { status.Text = "No sections found: the sheet needs Ref and Title columns (the Excel template has them on its second sheet)."; return; }
                    foreach (var l in _lines.ToList()) DropLine(l.Line);
                    foreach (var sec in found) Add(sec);
                    status.Text = $"{found.Count} sections from {Path.GetFileName(dialog.FileName)}.";
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Xml.XmlException)
                {
                    status.Text = ex is IOException and not FileNotFoundException ? $"{Path.GetFileName(dialog.FileName)} is open in Excel or can't be read: close it and try again." : ex.Message;
                }
            };
            var add = RichUi.Button(RichUi.Icon.Add, "Add");
            add.Click += (_, _) => { Add(new Section("", "", "", "")); _lines[^1].Ref.Focus(); };
            var clear = Brand.Button("Clear");
            clear.Click += (_, _) => { foreach (var l in _lines.ToList()) DropLine(l.Line); };
            var tools = new WrapPanel { Margin = new Thickness(16, 10, 16, 6) };
            foreach (var b in new[] { import, add, clear }) { b.Margin = new Thickness(0, 0, 8, 4); tools.Children.Add(b); }
            var heads = Cells();
            heads.Margin = new Thickness(16, 4, 16, 2);
            string[] names = ["REF", "DIVISION \u2013 SECTION TITLE", "LEAD", "IDC", ""];
            for (var i = 0; i < names.Length; i++) { var t = Brand.Text(names[i], Brand.K.Muted, 10.5, bold: true); Grid.SetColumn(t, i); heads.Children.Add(t); }
            foreach (var sec in setup.Sections) Add(sec);
            if (setup.Sections.Count == 0) Add(new Section("", "", "", ""));
            var cancel = Brand.Button("Cancel");
            cancel.IsCancel = true;
            var save = Brand.Button("Save", Brand.Weight.Primary);
            save.IsDefault = true;
            save.Click += (_, _) =>
            {
                var list = _lines.Select(l => new Section(l.Ref.Text.Trim(), l.Title.Text.Trim(), l.Lead.Text.Trim().ToUpperInvariant(), l.Idc.Text.Trim().ToUpperInvariant()))
                                 .Where(x => x.Ref.Length > 0 || x.Title.Length > 0).ToList();
                if (list.Count > TocMaxRows) { status.Text = $"One page holds {TocMaxRows} sections; this list has {list.Count}."; status.SetResourceReference(TextBlock.ForegroundProperty, Brand.K.DangerText); return; }
                setup.Sections = list;
                _saved = true;
                Close();
            };
            var foot = new DockPanel { Margin = new Thickness(16, 8, 16, 12) };
            var buttons = Brand.ButtonRow(cancel, save);
            buttons.Margin = new Thickness(0);
            DockPanel.SetDock(buttons, Dock.Right);
            foot.Children.Add(buttons);
            status.VerticalAlignment = VerticalAlignment.Center;
            foot.Children.Add(status);
            var root = new DockPanel();
            var header = Brand.Header("Spec sections", "The table of contents on the specification's IDC table page.");
            foreach (var e in new FrameworkElement[] { header, tools, heads }) { DockPanel.SetDock(e, Dock.Top); root.Children.Add(e); }
            DockPanel.SetDock(foot, Dock.Bottom);
            root.Children.Add(foot);
            root.Children.Add(new ScrollViewer { Content = _list, Padding = new Thickness(16, 0, 16, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });
            root.SetResourceReference(Panel.BackgroundProperty, Brand.K.Bg);
            Content = root;
        }

        private void Add(Section sec)
        {
            var g = Cells();
            var r = Box(0); var t = Box(0); var l = Box(0); var i = Box(0);
            (r.Text, t.Text, l.Text, i.Text) = (sec.Ref, sec.Title, sec.Lead, sec.Idc);
            AutomationProperties.SetName(r, "Ref");
            AutomationProperties.SetName(t, "Division - section title");
            AutomationProperties.SetName(l, "Lead discipline");
            AutomationProperties.SetName(i, "IDC discipline");
            var remove = new Button { Content = "\uE711", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), Width = 28, Height = 28, Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
            remove.SetResourceReference(ForegroundProperty, Brand.K.Muted);
            AutomationProperties.SetName(remove, "Remove this section");
            remove.Click += (_, _) => DropLine(g);
            UIElement[] cells = [r, t, l, i, remove];
            for (var k = 0; k < cells.Length; k++)
            {
                Grid.SetColumn(cells[k], k);
                if (cells[k] is TextBox tb) { tb.Width = double.NaN; tb.Margin = new Thickness(0, 0, 6, 0); }
                g.Children.Add(cells[k]);
            }
            _lines.Add((r, t, l, i, g));
            _list.Children.Add(g);
        }

        private void DropLine(Grid line)
        {
            _lines.RemoveAll(l => l.Line == line);
            _list.Children.Remove(line);
        }
    }
}
