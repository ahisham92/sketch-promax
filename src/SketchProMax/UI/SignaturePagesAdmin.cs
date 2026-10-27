using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SketchProMax.Tools;
using static SketchProMax.Tools.SignaturePages;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Clipboard = System.Windows.Clipboard;
using Image = System.Windows.Controls.Image;
using Panel = System.Windows.Controls.Panel;
using TextBox = System.Windows.Controls.TextBox;
using Visibility = System.Windows.Visibility;

namespace SketchProMax.UI;

/// <summary>
/// Signature pages' admin panel, behind the admin password (one admin per PC; the first person to open it sets the
/// password and gets a recovery code for when it is lost). The admin adds the people who may sign (initials and name
/// only: each person draws their own signature and chooses their own PIN, which no one else sees), resets a lost PIN
/// (the old signature goes; the person sets up again at their next Sign), and sets what the pages say about the project.
/// </summary>
internal sealed class SignaturePagesAdmin : Window
{
    private readonly string _project;
    private readonly Setup _setup;
    private readonly StackPanel _people = new();
    private readonly StackPanel _types = new();
    private readonly Image _logo = new() { Height = 30, MaxWidth = 120, Stretch = Stretch.Uniform, Margin = new Thickness(8, 0, 8, 0) };
    private readonly TextBlock _note = Brand.Text("", Brand.K.Muted, 12);

    /// <summary>Asks for the admin password (or sets it the first time), then shows the panel. False when it wasn't given.</summary>
    public static bool Open(Window owner, string project, Setup setup)
    {
        if (!Gate.Pass(owner)) return false;
        var w = new SignaturePagesAdmin(project, setup) { Owner = owner };
        w.ShowDialog();
        return true;
    }

    private static TextBox Box(double width)
    {
        var box = new TextBox { MinHeight = 28, VerticalContentAlignment = VerticalAlignment.Center };
        if (width > 0) box.Width = width;
        return box;
    }

    private static PasswordBox Secret(string name)
    {
        var box = new PasswordBox { Width = 200, MinHeight = 30, VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(box, name);
        return box;
    }

    private static TextBlock Heading(string text, double top = 16)
    {
        var t = Brand.Text(text, Brand.K.Text, 15, bold: true);
        t.Margin = new Thickness(0, top, 0, 4);
        return t;
    }

    private static TextBlock Small(string text, string key = Brand.K.Muted)
    {
        var t = Brand.Text(text, key, 12);
        t.TextWrapping = TextWrapping.Wrap;
        t.Margin = new Thickness(0, 2, 0, 4);
        return t;
    }

    private SignaturePagesAdmin(string project, Setup setup)
    {
        _project = project;
        _setup = setup;
        Title = "Sketch ProMax: signature pages admin";
        var area = SystemParameters.WorkArea;
        Width = Math.Min(760, Math.Floor(area.Width * 0.9));
        Height = Math.Min(820, Math.Floor(area.Height * 0.92));
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Brand.Prepare(this);
        RichUi.Apply(this, RichUi.MotionOf(Settings.AgentSettings.Load()));

        var stack = new StackPanel { Margin = new Thickness(18, 4, 18, 12) };

        // People: who may sign, and the state of their PIN.
        stack.Children.Add(Heading("People who sign"));
        stack.Children.Add(Small("Add a person here. At their first Sign they draw their signature and choose their own PIN. Lost PIN: Reset it, and they set up again."));
        stack.Children.Add(_people);
        var initials = Box(90);
        var name = Box(220);
        AutomationProperties.SetName(initials, "Initials of the new person");
        AutomationProperties.SetName(name, "Name of the new person");
        var add = RichUi.Button(RichUi.Icon.Add, "Add");
        add.Click += (_, _) => AddPerson(initials, name);
        var addRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        addRow.Children.Add(Label("Initials"));
        addRow.Children.Add(initials);
        addRow.Children.Add(Label("Name", 12));
        addRow.Children.Add(name);
        add.Margin = new Thickness(8, 0, 0, 0);
        addRow.Children.Add(add);
        stack.Children.Add(addRow);
        _note.TextWrapping = TextWrapping.Wrap;
        _note.Margin = new Thickness(0, 6, 0, 0);
        AutomationProperties.SetLiveSetting(_note, AutomationLiveSetting.Polite);
        stack.Children.Add(_note);

        // The pages: what they say about the project.
        stack.Children.Add(Heading($"Pages for {project}", 22));
        stack.Children.Add(Field("Project", _setup.Project, v => _setup.Project = v, wrap: true));
        stack.Children.Add(Field("Discipline", _setup.Discipline, v => _setup.Discipline = v));
        var logoRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        logoRow.Children.Add(Label("Logo"));
        logoRow.Children.Add(_logo);
        var pick = RichUi.Button(RichUi.Icon.Picture, "Pick");
        pick.Click += (_, _) => PickLogo();
        var drop = Brand.Button("Remove");
        drop.Click += (_, _) => { SetLogo(_project, ""); ShowLogo(); };
        logoRow.Children.Add(pick);
        logoRow.Children.Add(drop);
        stack.Children.Add(logoRow);
        var two = new CheckBox { Content = "REV 01 after the number (two digits)", IsChecked = _setup.TwoDigitRev, Margin = new Thickness(0, 10, 0, 0) };
        two.SetResourceReference(ForegroundProperty, Brand.K.Text);
        two.Click += (_, _) => _setup.TwoDigitRev = two.IsChecked == true;
        stack.Children.Add(two);
        var typesHead = Brand.Text("TYPES: code, heading at the top, form code at the foot, IDC table", Brand.K.Muted, 10.5, bold: true);
        typesHead.Margin = new Thickness(0, 14, 0, 4);
        stack.Children.Add(typesHead);
        stack.Children.Add(_types);
        foreach (var t in _setup.Types) AddType(t);
        var addType = Brand.Button("Add a type");
        addType.HorizontalAlignment = HorizontalAlignment.Left;
        addType.Margin = new Thickness(0, 4, 0, 0);
        addType.Click += (_, _) => { var t = new TypeDef(); _setup.Types.Add(t); AddType(t); };
        stack.Children.Add(addType);
        stack.Children.Add(Heading("IDC table (specifications)", 18));
        stack.Children.Add(Field("Departments across the foot (comma between)", string.Join(", ", _setup.Departments),
            v => _setup.Departments = v.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries).Select(d => d.Trim()).Where(d => d.Length > 0).ToList()));
        stack.Children.Add(Field("Form code at the foot", _setup.IdcForm, v => _setup.IdcForm = v));

        // The admin password.
        stack.Children.Add(Heading("Admin password", 22));
        var change = Brand.Button("Change the admin password");
        change.HorizontalAlignment = HorizontalAlignment.Left;
        change.Margin = new Thickness(0, 4, 0, 0);
        change.Click += (_, _) => Gate.NewPassword(this, "New admin password");
        stack.Children.Add(Small("Changing it gives a new recovery code; the old one stops working."));
        stack.Children.Add(change);

        var done = Brand.Button("Done", Brand.Weight.Primary);
        done.IsDefault = true;
        done.IsCancel = true;
        done.Click += (_, _) => Close();
        var foot = Brand.ButtonRow(done);
        foot.Margin = new Thickness(18, 8, 18, 12);
        DockPanel.SetDock(foot, Dock.Bottom);
        var header = Brand.Header("Admin", "People who sign, and what the pages say. Only the admin opens this.");
        DockPanel.SetDock(header, Dock.Top);
        var root = new DockPanel();
        root.Children.Add(header);
        root.Children.Add(foot);
        root.Children.Add(new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });
        root.SetResourceReference(Panel.BackgroundProperty, Brand.K.Bg);
        Content = root;
        ShowPeople();
        ShowLogo();
    }

    private static TextBlock Label(string text, double left = 0)
    {
        var t = Brand.Text(text, Brand.K.Muted, 12);
        t.Margin = new Thickness(left, 0, 8, 0);
        t.VerticalAlignment = VerticalAlignment.Center;
        return t;
    }

    private static FrameworkElement Field(string label, string value, Action<string> set, bool wrap = false)
    {
        var stack = new StackPanel();
        var t = Brand.Text(label, Brand.K.Muted, 12);
        t.Margin = new Thickness(0, 10, 0, 3);
        stack.Children.Add(t);
        var box = Box(0);
        box.Text = value;
        if (wrap) box.TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetName(box, label);
        box.TextChanged += (_, _) => set(box.Text.Trim());
        stack.Children.Add(box);
        return stack;
    }

    // ---- People -------------------------------------------------------------------------------------------------------

    private void Note(string text, string key = Brand.K.Muted)
    {
        _note.Text = text;
        _note.SetResourceReference(TextBlock.ForegroundProperty, key);
    }

    private bool Save(List<Signer> people)
    {
        try { SaveSigners(people); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Note("Couldn't save: " + ex.Message, Brand.K.DangerText); return false; }
    }

    private void AddPerson(TextBox initials, TextBox name)
    {
        var ini = initials.Text.Trim();
        if (ini.Length == 0) { Note("Type the initials as the table shows them (for example E.N.).", Brand.K.WarningText); initials.Focus(); return; }
        var people = LoadSigners();
        if (FindSigner(people, ini) is not null) { Note($"{ini} is already here.", Brand.K.WarningText); return; }
        people.Add(NewPerson(ini, name.Text));
        if (!Save(people)) return;
        initials.Clear();
        name.Clear();
        Note($"Added {ini}. They set their signature and PIN at their first Sign (or now: Set up).", Brand.K.SuccessText);
        ShowPeople();
    }

    private void ShowPeople()
    {
        _people.Children.Clear();
        var people = LoadSigners();
        if (people.Count == 0) _people.Children.Add(Small("No one yet."));
        foreach (var p in people)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            if (p.IsSetUp)
            {
                var changePin = Brand.Button("Change PIN");
                AutomationProperties.SetHelpText(changePin, "The person types their old PIN, then a new one.");
                changePin.Click += (_, _) => { if (ChangePinDialog.Run(this, p)) Note($"{p.Initials}'s PIN is changed.", Brand.K.SuccessText); };
                var reset = Brand.Button("Reset PIN");
                AutomationProperties.SetHelpText(reset, "For a lost PIN: their signature is taken off this PC; they draw it and choose a new PIN at their next Sign.");
                reset.ToolTip = AutomationProperties.GetHelpText(reset);
                reset.Click += (_, _) =>
                {
                    if (MessageBox.Show(this, $"Reset {p.Initials}'s PIN?\n\nTheir signature is taken off this PC. At their next Sign they draw it again and choose a new PIN.",
                            "Sketch ProMax", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
                    var all = LoadSigners();
                    if (FindSigner(all, p.Initials) is { } me) ResetPin(me);
                    if (Save(all)) Note($"{p.Initials}'s PIN is reset. They set up again at their next Sign.", Brand.K.SuccessText);
                    ShowPeople();
                };
                buttons.Children.Add(changePin);
                buttons.Children.Add(reset);
            }
            else
            {
                var setUp = Brand.Button("Set up now");
                AutomationProperties.SetHelpText(setUp, "Hand the PC to this person: they draw their signature and choose their PIN.");
                setUp.ToolTip = AutomationProperties.GetHelpText(setUp);
                setUp.Click += (_, _) => { if (SignaturePagesWindow.SetUpDialog.Run(this, p) is not null) Note($"{p.Initials} is set up.", Brand.K.SuccessText); ShowPeople(); };
                buttons.Children.Add(setUp);
            }
            var remove = Brand.Button("Remove", Brand.Weight.Danger);
            remove.Click += (_, _) =>
            {
                if (MessageBox.Show(this, $"Remove {p.Initials}? Their signature is deleted from this PC.", "Sketch ProMax",
                        MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
                var all = LoadSigners();
                all.RemoveAll(x => FindSigner([x], p.Initials) is not null);
                if (Save(all)) Note($"Removed {p.Initials}.");
                ShowPeople();
            };
            buttons.Children.Add(remove);
            DockPanel.SetDock(buttons, Dock.Right);
            row.Children.Add(buttons);
            var who = Brand.Text(string.IsNullOrWhiteSpace(p.Name) ? p.Initials : $"{p.Initials}   {p.Name}", Brand.K.Text, 13);
            who.VerticalAlignment = VerticalAlignment.Center;
            var state = Brand.Text(p.IsSetUp ? "Ready" : "Needs set-up", p.IsSetUp ? Brand.K.SuccessText : Brand.K.WarningText, 12);
            state.VerticalAlignment = VerticalAlignment.Center;
            state.Margin = new Thickness(12, 0, 0, 0);
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            left.Children.Add(who);
            left.Children.Add(state);
            row.Children.Add(left);
            _people.Children.Add(row);
        }
    }

    // ---- Pages --------------------------------------------------------------------------------------------------------

    private void AddType(TypeDef t)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        var code = Box(64);
        var heading = Box(230);
        var form = Box(170);
        code.Text = t.Code;
        heading.Text = t.Heading;
        form.Text = t.Form;
        var idc = new CheckBox { Content = "IDC", IsChecked = t.Idc, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        idc.SetResourceReference(ForegroundProperty, Brand.K.Text);
        AutomationProperties.SetName(code, "Type code");
        AutomationProperties.SetName(heading, "Heading at the top of the page");
        AutomationProperties.SetName(form, "Form code at the foot of the page");
        AutomationProperties.SetHelpText(idc, "This type also gets the IDC table page (specifications).");
        idc.ToolTip = AutomationProperties.GetHelpText(idc);
        code.TextChanged += (_, _) => t.Code = code.Text.Trim().ToUpperInvariant();
        heading.TextChanged += (_, _) => t.Heading = heading.Text.Trim();
        form.TextChanged += (_, _) => t.Form = form.Text.Trim();
        idc.Click += (_, _) => t.Idc = idc.IsChecked == true;
        foreach (var b in new[] { code, heading, form }) { b.Margin = new Thickness(0, 0, 6, 0); row.Children.Add(b); }
        row.Children.Add(idc);
        var remove = new Button { Content = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), Width = 28, Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        remove.SetResourceReference(ForegroundProperty, Brand.K.Muted);
        AutomationProperties.SetName(remove, "Remove this type");
        remove.Click += (_, _) => { _setup.Types.Remove(t); _types.Children.Remove(row); };
        row.Children.Add(remove);
        _types.Children.Add(row);
    }

    private void ShowLogo()
    {
        var logo = Logo(_project);
        _logo.Source = logo is null ? null : SignaturePagesWindow.ToBitmap(logo.Bytes);
        _logo.Visibility = logo is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void PickLogo()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "The logo for the foot of the pages", Filter = "Pictures (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg" };
        if (dialog.ShowDialog(this) != true) return;
        try { SetLogo(_project, dialog.FileName); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { Note(ex.Message, Brand.K.DangerText); return; }
        ShowLogo();
    }

    // ---- Dialogs --------------------------------------------------------------------------------------------------------

    /// <summary>The admin password: set it the first time, type it after, or set a new one with the recovery code.</summary>
    private sealed class Gate : Window
    {
        private bool _passed;

        public static bool Pass(Window owner)
        {
            if (!HasAdmin()) return NewPassword(owner, "Set the admin password");
            var w = new Gate { Owner = owner };
            w.ShowDialog();
            return w._passed;
        }

        /// <summary>Sets a new admin password (typed twice), then shows the new recovery code. False when cancelled.</summary>
        public static bool NewPassword(Window owner, string title)
        {
            var w = new Gate(title) { Owner = owner };
            w.ShowDialog();
            return w._passed;
        }

        private Gate() : this(null) { }

        private Gate(string? newTitle)
        {
            Title = "Sketch ProMax: admin";
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Brand.Prepare(this);
            var host = new ContentControl();
            var root = new StackPanel();
            root.Children.Add(Brand.Header("Admin", newTitle is null ? "Only the admin opens this." : "One admin password on this PC."));
            root.Children.Add(host);
            root.SetResourceReference(Panel.BackgroundProperty, Brand.K.Bg);
            Content = root;
            host.Content = newTitle is null ? Login(host) : Choose(host, newTitle, null);
        }

        private static TextBlock Error()
        {
            var e = Brand.Text("", Brand.K.DangerText, 12);
            e.TextWrapping = TextWrapping.Wrap;
            e.MaxWidth = 360;
            e.Margin = new Thickness(0, 6, 0, 0);
            AutomationProperties.SetLiveSetting(e, AutomationLiveSetting.Assertive);
            return e;
        }

        private FrameworkElement Login(ContentControl host)
        {
            var stack = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };
            var password = Secret("Admin password");
            var error = Error();
            var tries = 0;
            var forgot = new TextBlock { Margin = new Thickness(0, 8, 0, 0) };
            var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run("Forgot it? Use the recovery code"));
            link.Click += (_, _) => host.Content = Recover(host);
            forgot.Inlines.Add(link);
            forgot.SetResourceReference(TextBlock.ForegroundProperty, Brand.K.Link);
            var cancel = Brand.Button("Cancel");
            cancel.IsCancel = true;
            var open = Brand.Button("Open", Brand.Weight.Primary);
            open.IsDefault = true;
            open.Click += async (_, _) =>
            {
                open.IsEnabled = false;
                var typed = password.Password;
                if (await Task.Run(() => IsAdmin(typed))) { _passed = true; Close(); return; }
                tries++;
                error.Text = "That password isn't right.";
                password.Clear();
                if (tries >= 3) await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 5 * (tries - 2))));
                open.IsEnabled = true;
                password.Focus();
            };
            stack.Children.Add(Brand.Text("Admin password", Brand.K.Text, 14, bold: true));
            password.Margin = new Thickness(0, 8, 0, 0);
            stack.Children.Add(password);
            stack.Children.Add(error);
            stack.Children.Add(forgot);
            stack.Children.Add(Brand.ButtonRow(cancel, open));
            Loaded += (_, _) => password.Focus();
            return stack;
        }

        private FrameworkElement Recover(ContentControl host)
        {
            var stack = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };
            var code = Box(200);
            AutomationProperties.SetName(code, "Recovery code");
            var error = Error();
            var tries = 0;
            var cancel = Brand.Button("Cancel");
            cancel.IsCancel = true;
            var next = Brand.Button("Next", Brand.Weight.Primary);
            next.IsDefault = true;
            next.Click += async (_, _) =>
            {
                next.IsEnabled = false;
                var typed = code.Text;
                if (await Task.Run(() => IsRecoveryCode(typed))) { host.Content = Choose(host, "Set a new admin password", "The code is right."); return; }
                tries++;
                error.Text = "That code isn't right. It is the one shown when the password was set (XXXX-XXXX-XXXX).";
                if (tries >= 3) await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 5 * (tries - 2))));
                next.IsEnabled = true;
                code.Focus();
            };
            stack.Children.Add(Brand.Text("Recovery code", Brand.K.Text, 14, bold: true));
            stack.Children.Add(Small("The code you wrote down when the admin password was set."));
            stack.Children.Add(code);
            stack.Children.Add(error);
            stack.Children.Add(Brand.ButtonRow(cancel, next));
            Dispatcher.BeginInvoke(new Action(() => code.Focus()), System.Windows.Threading.DispatcherPriority.Input);
            return stack;
        }

        private FrameworkElement Choose(ContentControl host, string title, string? lead)
        {
            var stack = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };
            var password = Secret("New admin password");
            var again = Secret("New admin password again");
            var error = Error();
            var cancel = Brand.Button("Cancel");
            cancel.IsCancel = true;
            var save = Brand.Button("Save", Brand.Weight.Primary);
            save.IsDefault = true;
            save.Click += (_, _) =>
            {
                if (AdminPasswordProblem(password.Password) is { } problem) { error.Text = problem; password.Focus(); return; }
                if (password.Password != again.Password) { error.Text = "The two passwords are different."; again.Clear(); again.Focus(); return; }
                try { host.Content = Code(SetAdmin(password.Password)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error.Text = "Couldn't save: " + ex.Message; }
            };
            if (lead is not null) stack.Children.Add(Small(lead, Brand.K.SuccessText));
            stack.Children.Add(Brand.Text(title, Brand.K.Text, 14, bold: true));
            stack.Children.Add(Small("At least 6 characters, twice. Only the admin should know it."));
            var pair = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            pair.Children.Add(password);
            pair.Children.Add(new Border { Width = 10 });
            pair.Children.Add(again);
            stack.Children.Add(pair);
            stack.Children.Add(error);
            stack.Children.Add(Brand.ButtonRow(cancel, save));
            Dispatcher.BeginInvoke(new Action(() => password.Focus()), System.Windows.Threading.DispatcherPriority.Input);
            return stack;
        }

        /// <summary>The recovery code, shown once: Copy, and Continue only after "I've written it down".</summary>
        private FrameworkElement Code(string code)
        {
            _passed = true; // the password is set now, whether or not Continue is pressed
            var stack = new StackPanel { Margin = new Thickness(18, 14, 18, 14), MaxWidth = 420 };
            stack.Children.Add(Brand.Text("Your recovery code", Brand.K.Text, 14, bold: true));
            stack.Children.Add(Small("If the admin password is lost, this code sets a new one. It is shown only now: write it down and keep it away from this PC."));
            var shown = new TextBox { Text = code, IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 22, FontWeight = FontWeights.Bold, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetName(shown, "Recovery code");
            var copy = RichUi.Button(RichUi.Icon.Save, "Copy");
            copy.Margin = new Thickness(8, 6, 0, 0);
            copy.Click += (_, _) => { try { Clipboard.SetText(code); copy.Content = "Copied"; } catch (System.Runtime.InteropServices.ExternalException) { } };
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(shown);
            line.Children.Add(copy);
            stack.Children.Add(line);
            var kept = new CheckBox { Content = "I've written it down", Margin = new Thickness(0, 12, 0, 0) };
            kept.SetResourceReference(ForegroundProperty, Brand.K.Text);
            var go = Brand.Button("Continue", Brand.Weight.Primary);
            go.IsDefault = true;
            go.IsEnabled = false;
            kept.Click += (_, _) => go.IsEnabled = kept.IsChecked == true;
            go.Click += (_, _) => Close();
            stack.Children.Add(kept);
            stack.Children.Add(Brand.ButtonRow(go));
            return stack;
        }
    }

    /// <summary>The person changes their own PIN: the old one, then the new one twice.</summary>
    private sealed class ChangePinDialog : Window
    {
        private bool _changed;

        public static bool Run(Window owner, Signer person)
        {
            var w = new ChangePinDialog(person) { Owner = owner };
            w.ShowDialog();
            return w._changed;
        }

        private ChangePinDialog(Signer person)
        {
            Title = "Sketch ProMax: change PIN";
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Brand.Prepare(this);
            var old = Secret("Old PIN");
            var pin = Secret("New PIN");
            var again = Secret("New PIN again");
            var error = Brand.Text("", Brand.K.DangerText, 12);
            error.Margin = new Thickness(0, 6, 0, 0);
            AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Assertive);
            var tries = 0;
            var cancel = Brand.Button("Cancel");
            cancel.IsCancel = true;
            var save = Brand.Button("Save", Brand.Weight.Primary);
            save.IsDefault = true;
            save.Click += async (_, _) =>
            {
                if (PinProblem(pin.Password) is { } p) { error.Text = p; pin.Focus(); return; }
                if (pin.Password != again.Password) { error.Text = "The two new PINs are different."; again.Clear(); again.Focus(); return; }
                save.IsEnabled = false;
                string o = old.Password, n = pin.Password;
                try
                {
                    var people = LoadSigners();
                    var me = FindSigner(people, person.Initials);
                    if (me is not null && await Task.Run(() => ChangePin(me, o, n)))
                    {
                        SaveSigners(people);
                        _changed = true;
                        Close();
                        return;
                    }
                    tries++;
                    error.Text = "The old PIN isn't right. Lost it? Close this and use Reset PIN.";
                    old.Clear();
                    if (tries >= 3) await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 5 * (tries - 2))));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error.Text = "Couldn't save: " + ex.Message; }
                save.IsEnabled = true;
                old.Focus();
            };
            var who = string.IsNullOrWhiteSpace(person.Name) ? person.Initials : $"{person.Name} ({person.Initials})";
            var stack = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };
            stack.Children.Add(Brand.Text($"{who}: change your PIN", Brand.K.Text, 14, bold: true));
            foreach (var (label, box) in new[] { ("Old PIN", old), ("New PIN (at least 4 characters)", pin), ("New PIN again", again) })
            {
                var t = Brand.Text(label, Brand.K.Muted, 12);
                t.Margin = new Thickness(0, 8, 0, 3);
                stack.Children.Add(t);
                stack.Children.Add(box);
            }
            stack.Children.Add(error);
            stack.Children.Add(Brand.ButtonRow(cancel, save));
            var root = new StackPanel();
            root.Children.Add(stack);
            root.SetResourceReference(Panel.BackgroundProperty, Brand.K.Bg);
            Content = root;
            Loaded += (_, _) => old.Focus();
        }
    }
}
