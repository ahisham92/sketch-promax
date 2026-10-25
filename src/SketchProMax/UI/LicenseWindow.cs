using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SketchProMax.Licensing;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

namespace SketchProMax.UI;

/// <summary>
/// This PC's license (Help > License): the PC code to send to the owner, a box for the key they send back, and what the
/// key says now. Opened by itself at start, and by any button, while this PC has no valid key. On the owner's PC the
/// License button opens the License manager instead (UI/LicenseManagerWindow).
/// </summary>
internal sealed class LicenseWindow : Window
{
    private readonly TextBox _key = new() { MinHeight = 60, TextWrapping = TextWrapping.Wrap, AcceptsReturn = false, VerticalContentAlignment = VerticalAlignment.Top };
    private readonly TextBlock _state = Brand.Text("", Brand.K.Text, 13);
    private readonly TextBlock _result = Brand.Text("", Brand.K.Muted, 12);
    private static LicenseWindow? _open;

    /// <summary>The License button: the manager on the owner's PC, this window elsewhere.</summary>
    public static Autodesk.Revit.UI.Result Command()
    {
        if (LicenseState.IsOwnerPc) return LicenseManagerWindow.Open();
        return Open();
    }

    public static Autodesk.Revit.UI.Result Open()
    {
        if (_open is { IsLoaded: true })
        {
            _open.Activate();
            return Autodesk.Revit.UI.Result.Succeeded;
        }
        _open = new LicenseWindow();
        _open.Closed += (_, _) => _open = null;
        _open.Show();
        return Autodesk.Revit.UI.Result.Succeeded;
    }

    /// <summary>
    /// A button pressed on a PC without a valid key: the window opens and the button does nothing else. Returns true when
    /// the PC is locked (the caller stops).
    /// </summary>
    public static bool BlockIfLocked()
    {
        if (LicenseState.Allowed) return false;
        Open();
        return true;
    }

    private LicenseWindow()
    {
        Title = "Sketch ProMax: License";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Brand.Prepare(this);
        RichUi.Apply(this, RichUi.MotionOf(Settings.AgentSettings.Load()));

        var status = LicenseState.Current;
        var page = new StackPanel { Margin = new Thickness(18, 12, 18, 14) };
        _state.TextWrapping = TextWrapping.Wrap;
        page.Children.Add(_state);

        page.Children.Add(Label("Your PC code"));
        var code = new TextBox
        {
            Text = status.PcCode.Length == 0 ? "Can't be read on this PC" : status.PcCode, IsReadOnly = true, MinHeight = 30,
            FontSize = 16, FontFamily = new System.Windows.Media.FontFamily("Consolas"), VerticalContentAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(code, "Your PC code");
        var copy = RichUi.Button(RichUi.Icon.Export, "Copy");
        copy.IsEnabled = status.PcCode.Length > 0;
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(status.PcCode); Info("PC code copied. Send it to whoever gave you Sketch ProMax."); }
            catch (System.Runtime.InteropServices.ExternalException) { Error("The clipboard is busy. Try Copy again."); }
        };
        var codeRow = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        DockPanel.SetDock(copy, Dock.Right);
        codeRow.Children.Add(copy);
        codeRow.Children.Add(code);
        page.Children.Add(codeRow);
        page.Children.Add(Para("Send this code to whoever gave you Sketch ProMax. They send back a key made for this PC only.", Brand.K.Muted));

        page.Children.Add(Label("License key"));
        AutomationProperties.SetName(_key, "License key");
        AutomationProperties.SetHelpText(_key, "Paste the key you were sent. It starts with SPM1.");
        _key.Margin = new Thickness(0, 4, 0, 0);
        page.Children.Add(_key);

        _result.TextWrapping = TextWrapping.Wrap;
        _result.Margin = new Thickness(0, 10, 0, 0);
        AutomationProperties.SetLiveSetting(_result, AutomationLiveSetting.Polite);
        page.Children.Add(_result);

        var close = RichUi.Button(RichUi.Icon.Close, "Close");
        close.IsCancel = true;
        close.Click += (_, _) => Close();
        var activate = RichUi.Button(RichUi.Icon.Next, "Activate", Brand.Weight.Primary);
        activate.IsDefault = true;
        activate.Click += (_, _) => Activate(activate);
        page.Children.Add(Brand.ButtonRow(close, activate));

        var root = new DockPanel();
        var header = Brand.Header("License", "Sketch ProMax works on PCs with a key made for them");
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(page);
        Content = root;
        ShowState();
        Loaded += (_, _) => _key.Focus();
    }

    private void ShowState()
    {
        var s = LicenseState.Current;
        switch (s.Kind)
        {
            case LicenseState.Kind.Owner:
                _state.Text = "This is the owner's PC: no key is needed.";
                Brand.Fg(_state, Brand.K.SuccessText);
                break;
            case LicenseState.Kind.Licensed:
                _state.Text = $"Licensed to {s.License!.Name}" + (s.Ends is { } e ? $" until {e:d MMM yyyy}." : ", with no end date.");
                Brand.Fg(_state, Brand.K.SuccessText);
                break;
            default:
                _state.Text = s.License is null
                    ? "Sketch ProMax needs a license for this PC. Until then its tools and chat are off."
                    : LicenseState.Explain(s.Check, s.Ends) + " Until then its tools and chat are off.";
                Brand.Fg(_state, Brand.K.WarningText);
                break;
        }
    }

    private void Activate(Button button)
    {
        var key = _key.Text.Trim();
        if (key.Length == 0) { Error("Paste the key you were sent first."); _key.Focus(); return; }
        var check = LicenseState.Activate(key);
        if (check != LicenseKey.Check.Valid)
        {
            Error(LicenseState.Explain(check, LicenseKey.Read(key)?.Ends));
            _key.Focus();
            return;
        }
        ShowState();
        _key.Clear();
        Info("Activated. Sketch ProMax is ready to use on this PC.");
        button.IsEnabled = false;
    }

    private void Info(string text)
    {
        _result.Text = text;
        Brand.Fg(_result, Brand.K.SuccessText);
    }

    private void Error(string text)
    {
        _result.Text = text;
        Brand.Fg(_result, Brand.K.DangerText);
    }

    internal static TextBlock Label(string text)
    {
        var t = Brand.Text(text, Brand.K.Text, 13, bold: true);
        t.Margin = new Thickness(0, 14, 0, 0);
        return t;
    }

    internal static TextBlock Para(string text, string key)
    {
        var t = Brand.Text(text, key, 12);
        t.TextWrapping = TextWrapping.Wrap;
        t.Margin = new Thickness(0, 4, 0, 0);
        return t;
    }
}
